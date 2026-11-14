"""Xe fdinfo process accounting only; synthetic fixtures exercise GPU-tick deltas and sharing gates."""
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from gputaskman import drm
from test_drm import Clock, GiB, KiB, MiB, XE_FDINFO, _write_fd


def fdinfo(busy=0, total=1000, *, client=3, shared=False):
    text = "\n".join(line for line in XE_FDINFO.splitlines()
                     if not line.startswith(("drm-cycles-", "drm-total-cycles-"))) + "\n"
    text = text.replace("drm-client-id:\t3", f"drm-client-id:\t{client}")
    if not shared:
        text = text.replace("drm-shared-vram0:\t16 MiB", "drm-shared-vram0:\t0")
    return text + f"drm-cycles-ccs:\t{busy}\ndrm-total-cycles-ccs:\t{total}\n"


class XeParserTests(unittest.TestCase):
    def test_multiple_local_regions_and_cpu_memory_are_kept_apart(self):
        text = fdinfo() + "drm-resident-vram1:\t4 MiB\ndrm-shared-vram1:\t0\n"
        text = text.replace("drm-resident-system:\t0", "drm-resident-system:\t2 MiB")
        info = drm.parse_fdinfo(text)
        self.assertEqual(info["vram"], 23992 * KiB + 4 * MiB)
        self.assertEqual(info["gtt"], 192 * KiB)
        self.assertEqual(info["cpu"], 2 * MiB)
        self.assertEqual(info["shared"], 0)

    def test_missing_region_or_sharing_value_never_becomes_zero(self):
        for line in ("drm-shared-system:\t0\n", "drm-shared-gtt:\t0\n",
                     "drm-shared-vram0:\t0\n", "drm-shared-stolen:\t0\n"):
            with self.subTest(line=line):
                self.assertIsNone(drm.parse_fdinfo(fdinfo().replace(line, ""))["shared"])
        text = fdinfo() + "drm-total-vram1:\t4 MiB\ndrm-shared-vram1:\t0\n"
        self.assertIsNone(drm.parse_fdinfo(text)["vram"])
        text = fdinfo().replace("drm-resident-vram0:\t23992 KiB", "drm-resident-vram0:\tbad")
        self.assertIsNone(drm.parse_fdinfo(text)["vram"])

    def test_incomplete_malformed_and_negative_cycles_are_unknown(self):
        for old, new in (("drm-total-cycles-ccs:\t1000\n", ""),
                         ("drm-cycles-ccs:\t0\n", ""),
                         ("drm-cycles-ccs:\t0", "drm-cycles-ccs:\tbad"),
                         ("drm-cycles-ccs:\t0", "drm-cycles-ccs:\t-1")):
            with self.subTest(new=new):
                self.assertIsNone(drm.parse_fdinfo(fdinfo().replace(old, new))["cycles"])

    def test_integrated_xe_has_no_local_region(self):
        text = "\n".join(line for line in fdinfo().splitlines() if "vram0" not in line)
        info = drm.parse_fdinfo(text)
        self.assertIsNone(info["vram"])
        self.assertEqual((info["gtt"], info["shared"]), (192 * KiB, 0))


class XeScanTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        root = Path(self.tmp.name)
        self.proc = root / "proc"
        self.proc.mkdir()
        self.clock = Clock()
        self.card = drm.DrmCard(key=12288, pdev="0000:03:00.0", sysfs=str(root / "device"),
                                driver="xe", vendor_id=0x8086, device_id=0xe222,
                                name="Intel GPU", vram_total=32 * GiB, integrated=False, intel=True)
        self.counters = drm.DrmCounters([self.card], proc=str(self.proc), clock=self.clock)
        self.addCleanup(self.counters.close)

    def sample(self, busy=0, total=1000, *, text=None):
        _write_fd(self.proc, 100, 5, fdinfo(busy, total) if text is None else text)
        self.clock.tick()
        return self.counters.sample()[0][self.card.key][100]

    def test_gpu_ticks_drive_compute_and_private_memory_is_charged(self):
        first = self.sample(100, 1000)
        self.assertNotIn("engine", first.available)
        self.clock.tick(50)  # CPU wall time must have no effect on the GPU ratio
        row = self.sample(850, 2000)
        self.assertEqual(row.engines["compute"], 75.0)
        self.assertEqual(row.gpu_pct, 75.0)
        self.assertEqual((row.dedicated, row.shared), (23992 * KiB, 192 * KiB))
        self.assertEqual(row.drm_reason, "")

    def test_idle_is_measured_zero_but_stalled_gpu_clock_is_unknown(self):
        self.sample()
        idle = self.sample(0, 2000)
        self.assertIn("engine", idle.available)
        self.assertEqual(idle.gpu_pct, 0)
        self.assertNotIn("engine", self.sample(0, 2000).available)

    def test_stalled_reset_or_new_engine_does_not_hide_other_advancing_engines(self):
        def engines(busy, total, copy_busy, copy_total):
            return fdinfo(busy, total) + (f"drm-cycles-bcs:\t{copy_busy}\n"
                                         f"drm-total-cycles-bcs:\t{copy_total}\n")
        self.sample(text=engines(100, 1000, 0, 1000))
        row = self.sample(text=engines(600, 2000, 0, 1000))
        self.assertIn("engine", row.available)
        self.assertEqual(row.engines, {"compute": 50})
        row = self.sample(text=engines(600, 2000, 500, 2000))
        self.assertEqual(row.engines, {"copy": 50})
        row = self.sample(text=engines(0, 100, 1000, 3000))  # compute total reset
        self.assertEqual(row.engines, {"copy": 50})
        row = self.sample(text=engines(0, 100, 1500, 4000) +
                          "drm-cycles-vcs:\t0\ndrm-total-cycles-vcs:\t100\n")
        self.assertEqual(row.engines, {"copy": 50})  # new engine needs its own baseline
        self.assertNotIn("engine", self.sample(text=engines(0, 100, 1500, 4000)).available)

    def test_busy_counter_high_water_and_total_counter_reset(self):
        self.sample(100, 1000)
        self.assertEqual(self.sample(90, 2000).gpu_pct, 0)
        self.assertEqual(self.sample(600, 3000).gpu_pct, 50)
        self.assertNotIn("engine", self.sample(0, 10).available)
        self.assertEqual(self.sample(25, 110).gpu_pct, 25)

    def test_class_capacity_preserves_one_busy_engine_semantics_and_clamps(self):
        self.sample(0, 1000)
        self.assertEqual(self.sample(1000, 2000).gpu_pct, 100)
        self.assertEqual(self.sample(3000, 3000).gpu_pct, 100)

    def test_all_clients_must_have_a_measured_interval(self):
        self.sample()
        _write_fd(self.proc, 100, 6, fdinfo(client=4))
        self.assertNotIn("engine", self.sample(500, 2000).available)
        _write_fd(self.proc, 100, 6, fdinfo(100, 2000, client=4))
        self.assertEqual(self.sample(1000, 3000).gpu_pct, 60)

    def test_duplicate_fds_are_counted_once_and_shared_clients_are_unknown(self):
        self.sample()
        _write_fd(self.proc, 100, 6, fdinfo(500, 2000))
        row = self.sample(500, 2000)
        self.assertEqual((row.gpu_pct, row.dedicated), (50, 23992 * KiB))
        _write_fd(self.proc, 200, 7, fdinfo(1000, 3000))
        row = self.sample(1000, 3000)
        self.assertNotIn("engine", row.available)
        self.assertEqual(row.drm_reason, drm.REASON_SHARED_CLIENT)

    def test_missing_pairs_and_partial_scans_do_not_look_idle(self):
        self.sample()
        broken = fdinfo(500, 2000).replace("drm-total-cycles-ccs:\t2000\n", "")
        self.assertNotIn("engine", self.sample(text=broken).available)
        row = self.sample(1000, 3000)
        self.assertEqual(row.gpu_pct, 50)
        original = self.counters._clients

        def partial():
            result = original()
            self.counters.partial = True
            return result

        with patch.object(self.counters, "_clients", side_effect=partial):
            row = self.sample(1500, 4000)
        self.assertNotIn("engine", row.available)
        self.assertEqual(row.drm_reason, drm.REASON_PARTIAL_SCAN)


    def test_integrated_xe_charges_gtt_not_cpu_only_memory(self):
        self.card.vram_total = 0
        self.card.integrated = True
        text = "\n".join(line for line in fdinfo().splitlines() if "vram0" not in line)
        row = self.sample(text=text)
        self.assertTrue(self.card.integrated)
        self.assertEqual(row.dedicated, 192 * KiB)
        self.assertNotIn("shared", row.available)
