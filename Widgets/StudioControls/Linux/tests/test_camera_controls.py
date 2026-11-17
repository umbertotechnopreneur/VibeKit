"""Camera failure paths use mocks, never a V4L2 device."""

import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import camera_controls as camera


class CameraTests(unittest.TestCase):
    def test_failed_snapshot_closes_device(self):
        with (
            patch.object(camera, "camera_fd", return_value=41),
            patch.object(camera, "query", side_effect=OSError("unplugged")),
            patch.object(camera.os, "close") as close,
        ):
            with self.assertRaises(OSError):
                camera.snapshot()
            close.assert_called_once_with(41)

    def test_single_adjustment_clamps_and_steps(self):
        with (
            patch.object(camera, "camera_fd", return_value=41),
            patch.object(camera, "query", return_value=(100, 500, 5, 100)),
            patch.object(camera, "control") as control,
            patch.object(camera.os, "close") as close,
        ):
            camera.set_one(9, 900)
            control.assert_called_once_with(41, camera.SET_CONTROL, 9, 500)
            close.assert_called_once_with(41)

    def test_preset_failure_restores_previous_values_and_closes(self):
        previous = {
            str(item[1]): 100 if index == 0 else 0 for index, item in enumerate(camera.CONTROLS)
        }
        writes = []

        def control(_fd, command, identifier, value=0):
            if command == camera.GET_CONTROL:
                return previous[str(identifier)]
            writes.append((identifier, value))
            if len(writes) == 2:
                raise OSError("disconnected during preset")

        with (
            patch.object(camera, "camera_fd", return_value=41),
            patch.object(camera, "query", return_value=(0, 1000, 1, 0)),
            patch.object(camera, "control", side_effect=control),
            patch.object(camera.os, "close") as close,
        ):
            with self.assertRaises(OSError):
                camera.set_values({key: 200 for key in previous})
            self.assertEqual(writes[-3:], [(int(key), value) for key, value in previous.items()])
            close.assert_called_once_with(41)

    def test_corrupt_preset_is_preserved_and_defaults_remain(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "presets.json"
            path.write_text("[]")
            with self.assertLogs("umberto_lab.camera_controls", level="WARNING"):
                presets = camera.load_presets(path)
            self.assertEqual(json.loads(path.read_text()), [])
            self.assertEqual(presets["Wide"][str(camera.CONTROLS[0][1])], 100)
