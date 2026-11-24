import json
import tempfile
import unittest
from dataclasses import replace
from pathlib import Path

from timer_options import TimerOptions


class TimerOptionsTests(unittest.TestCase):
    def test_round_trip_and_missing_defaults(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "preferences.json"
            opts = TimerOptions.load(path)
            self.assertEqual(opts, TimerOptions())
            opts = replace(
                opts,
                size="large",
                show_note=True,
                lead_in_enabled=False,
                end_warning=False,
                warning_seconds=16,
                free_seconds=240,
            )
            opts.save(path)
            self.assertEqual(TimerOptions.load(path), opts)
            path.write_text(json.dumps({"size": "large"}))
            self.assertEqual(TimerOptions.load(path), replace(TimerOptions(), size="large"))

    def test_invalid_values_do_not_activate_recording_preferences(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "preferences.json"
            for bad in (
                {"size": "unbounded"},
                {"lead_in_enabled": 0},
                {"free_seconds": True},
                {"warning_seconds": 61},
                {"free_seconds": -1},
                [],
            ):
                with self.subTest(bad=bad):
                    path.write_text(json.dumps(bad))
                    self.assertEqual(TimerOptions.load(path), TimerOptions())
            path.write_bytes(b" " * 4097)
            self.assertEqual(TimerOptions.load(path), TimerOptions())
