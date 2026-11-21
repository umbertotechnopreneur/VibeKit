import importlib.util
import json
from pathlib import Path
from tempfile import TemporaryDirectory
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("widget_guard", Path(__file__).resolve().parents[1]/"Linux/watchdog.py")
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


class TreeOwnershipChecks(unittest.TestCase):
    def test_only_widget_descendants_are_included(self):
        snapshot = {1: {"parent": 0}, 2: {"parent": 1}, 3: {"parent": 2},
                    4: {"parent": 1}, 5: {"parent": 4}, 99: {"parent": 0}}
        self.assertEqual(set(guard.descendants(1, snapshot, excluded=[4])), {1, 2, 3})

    def test_tracked_orphan_remains_owned(self):
        snapshot = {1: {"parent": 0}, 2: {"parent": 999}}
        self.assertEqual(set(guard.descendants(1, snapshot, retained=[2])), {1, 2})

    def test_reused_pid_is_never_signalled(self):
        with patch.object(guard, "process_info", return_value={"started": 202}), patch.object(guard.os, "kill") as kill:
            guard.stop_owned({123: {"started": 101}})
            kill.assert_not_called()

    def test_owned_identity_is_stopped_then_killed(self):
        with patch.object(guard, "process_info", return_value={"started": 101}), patch.object(guard.os, "kill") as kill:
            guard.stop_owned({123: {"started": 101}})
            self.assertEqual([call.args for call in kill.call_args_list], [(123, guard.STOP_SIGNAL), (123, guard.KILL_SIGNAL)])

    def test_limit_logs_the_same_owned_snapshot_used_to_stop_processes(self):
        def sample(pid, parent, name, memory):
            return {"pid": pid, "parent": parent, "name": name, "bytes": memory, "started": pid, "state": "S"}

        snapshot = {
            10: sample(10, 1, "python3", 300_000_000),
            11: sample(11, 10, "backend", 500_000_000),
            12: sample(12, 11, "codex", 250_000_000),
            13: sample(13, 10, "watchdog", 20_000_000),
            14: sample(14, 1, "other-codex", 900_000_000),
        }
        entries = [Path("/proc") / str(pid) for pid in snapshot]
        with (
            TemporaryDirectory() as state,
            patch.object(guard, "state_dir", return_value=Path(state)),
            patch.object(Path, "iterdir", return_value=iter(entries)),
            patch.object(guard, "process_info", side_effect=snapshot.get),
            patch.object(guard.os, "getpid", return_value=13),
            patch.object(guard.os, "getppid", return_value=10),
            patch.object(guard.os, "write"),
            patch.object(guard.os, "close"),
            patch.object(guard, "stop_owned") as stop,
            patch.object(guard, "notice"),
        ):
            self.assertEqual(guard.run(10, 10, 123), 137)
            event = json.loads((Path(state) / "logs" / "linux.jsonl").read_text())

        stop.assert_called_once_with({pid: snapshot[pid] for pid in (10, 11, 12)})
        self.assertEqual(event["kind"], "guard.memory-limit")
        self.assertEqual(event["limitBytes"], guard.LIMIT_BYTES)
        self.assertEqual(event["bytes"], 1_050_000_000)
        self.assertEqual(event["processes"], [
            {"pid": 11, "parentPid": 10, "name": "backend", "isRoot": False, "rssBytes": 500_000_000},
            {"pid": 10, "parentPid": 1, "name": "python3", "isRoot": True, "rssBytes": 300_000_000},
            {"pid": 12, "parentPid": 11, "name": "codex", "isRoot": False, "rssBytes": 250_000_000},
        ])


if __name__ == "__main__":
    unittest.main()
