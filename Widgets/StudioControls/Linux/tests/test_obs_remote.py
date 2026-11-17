"""Offline failure-injection tests; never connect to OBS or capture devices."""

import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

from obs_remote import OBS, OBSConnectionError, OBSRequestError, ProjectApplyError


class FakeSocket:
    def __init__(self, replies):
        self.replies = iter(replies)
        self.sent = []
        self.timeouts = []
        self.closed = False

    def settimeout(self, timeout):
        self.timeouts.append(timeout)

    def send(self, message):
        self.sent.append(json.loads(message))

    def recv(self):
        reply = next(self.replies)
        if isinstance(reply, Exception):
            raise reply
        if callable(reply):
            reply = reply(self.sent[-1])
        return json.dumps(reply)

    def close(self, timeout=0):
        self.closed = True


class PrivateErrorTests(unittest.TestCase):
    def test_arbitrary_server_comment_is_not_copied_into_errors(self):
        def reject(request):
            result = response(False)(request)
            result["d"]["requestStatus"]["comment"] = "synthetic private project text"
            return result

        obs = OBS()
        obs.ws = FakeSocket([reject])
        with self.assertRaises(OBSRequestError) as caught:
            obs.call("StartRecord")
        self.assertEqual(caught.exception.code, 600)
        self.assertIn("600", str(caught.exception))
        self.assertNotIn("synthetic private", str(caught.exception))
        self.assertFalse(obs.ws.closed)

    def test_malformed_confirmation_is_uncertain_and_never_exposes_raw_status(self):
        for field in ("result", "code"):

            def malformed(request, field=field):
                result = response()(request)
                result["d"]["requestStatus"][field] = "synthetic private detail"
                return result

            obs = OBS()
            connection = FakeSocket([malformed])
            obs.ws = connection
            with self.assertRaises(OBSConnectionError) as caught:
                obs.call("StartRecord")
            self.assertTrue(caught.exception.request_sent)
            self.assertTrue(connection.closed)
            self.assertNotIn("synthetic private detail", str(caught.exception))
            self.assertEqual(len(connection.sent), 1)

    def test_invalid_port_never_creates_a_socket(self):
        for port in [0, 65536, True, "4455@synthetic.invalid", 1.5]:
            obs = OBS()
            obs.config_path = Mock()
            obs.config_path.read_text.return_value = json.dumps({"server_port": port})
            with patch("obs_remote.websocket.create_connection") as create:
                with self.assertRaises(OBSConnectionError):
                    obs.connect()
                create.assert_not_called()


def response(ok=True, data=None):
    return lambda request: {
        "op": 7,
        "d": {
            "requestId": request["d"]["requestId"],
            "requestStatus": {"result": ok, "code": 100 if ok else 600, "comment": "Missing input"},
            "responseData": data or {},
        },
    }


class StubOBS(OBS):
    def __init__(self):
        super().__init__(assets_root=Path(tempfile.gettempdir()) / "studio-synthetic-assets")
        self.requests = []
        self.missing = set()
        self.active = True
        self.paused = False
        self.scene_name = "Intro"
        self.fail_status = False
        self.fail_optional_transport = False

    def call(self, kind, data=None):
        self.requests.append((kind, data))
        source = (data or {}).get("sourceName", (data or {}).get("inputName"))
        if source in self.missing:
            raise OBSRequestError(kind, 600, "Missing input")
        if kind == "GetRecordStatus":
            if self.fail_status:
                raise OBSConnectionError("Disconnected")
            return {"outputActive": self.active, "outputPaused": self.paused}
        if kind == "GetCurrentProgramScene":
            return {"currentProgramSceneName": self.scene_name}
        if kind == "GetSourceFilterList":
            name = "Background blur" if source == "Logitech Brio" else "Vignette intensity"
            return {"filters": [{"filterName": name, "filterEnabled": True, "filterSettings": {}}]}
        if kind == "GetInputMute":
            if self.fail_optional_transport:
                raise OBSConnectionError("Disconnected")
            return {"inputMuted": False}
        if kind == "GetSceneItemList":
            return {"sceneItems": []}
        if kind == "GetSceneList":
            return {"scenes": [{"sceneName": self.scene_name}]}
        if kind == "StartRecord":
            self.active = True
            return {}
        if kind == "StopRecord":
            self.active = False
            return {"outputPath": "/tmp/offline-test-recording.mkv"}
        if kind == "PauseRecord":
            self.paused = True
            return {}
        if kind == "ResumeRecord":
            self.paused = False
            return {}
        if kind == "SetCurrentProgramScene":
            self.scene_name = data["sceneName"]
            return {}
        if kind in (
            "TriggerMediaInputAction",
            "SetStudioModeEnabled",
            "SetInputSettings",
            "SetInputMute",
            "SetInputVolume",
        ):
            return {}
        raise AssertionError(kind)


class TransportTests(unittest.TestCase):
    def test_rejected_request_keeps_connection_for_next_request(self):
        obs = OBS()
        connection = FakeSocket([response(False), response(data={"outputActive": True})])
        obs.ws = connection
        with self.assertRaises(OBSRequestError) as caught:
            obs.call("GetInputMute", {"inputName": "Removed input"})
        self.assertEqual(caught.exception.code, 600)
        self.assertIs(obs.ws, connection)
        self.assertFalse(connection.closed)
        self.assertTrue(obs.call("GetRecordStatus")["outputActive"])

    def test_unrelated_frames_cannot_extend_request_deadline(self):
        obs = OBS()
        connection = FakeSocket([{"op": 5}, {"op": 5}])
        obs.ws = connection
        with patch("obs_remote.time.monotonic", side_effect=[0, 0, 1, 2, 4]):
            with self.assertRaises(OBSConnectionError) as caught:
                obs.call("GetRecordStatus")
        self.assertIn("deadline", str(caught.exception))
        self.assertTrue(connection.closed)
        self.assertIsNone(obs.ws)

    def test_failed_mutation_is_never_replayed_on_reconnect(self):
        for action, request in (
            (lambda obs: obs.record(True), "StartRecord"),
            (lambda obs: obs.pause(False), "ResumeRecord"),
        ):
            with self.subTest(request=request):
                obs = OBS()
                failed = FakeSocket([OSError("Connection lost after send")])
                obs.ws = failed
                with self.assertRaises(OBSConnectionError) as caught:
                    action(obs)
                self.assertTrue(caught.exception.request_sent)
                self.assertEqual(
                    [message["d"]["requestType"] for message in failed.sent], [request]
                )
                recovered = FakeSocket([response(data={"outputActive": True})])
                with patch.object(
                    obs,
                    "connect",
                    side_effect=lambda deadline, obs=obs, recovered=recovered: setattr(
                        obs, "ws", recovered
                    ),
                ):
                    self.assertTrue(obs.call("GetRecordStatus")["outputActive"])
                self.assertEqual(
                    [message["d"]["requestType"] for message in recovered.sent], ["GetRecordStatus"]
                )

    def test_bad_handshake_closes_socket(self):
        obs = OBS()
        obs.config_path = Mock()
        obs.config_path.read_text.return_value = json.dumps({"server_port": 4455})
        connection = FakeSocket([{"op": 5}])
        with patch("obs_remote.websocket.create_connection", return_value=connection):
            with self.assertRaises(OBSConnectionError):
                obs.connect()
        self.assertTrue(connection.closed)
        self.assertIsNone(obs.ws)

    def test_close_clears_reference_even_if_socket_close_fails(self):
        obs = OBS()
        obs.ws = Mock()
        obs.ws.close.side_effect = OSError("Already closed")
        obs.close()
        self.assertIsNone(obs.ws)
        obs.close()


class StatusTests(unittest.TestCase):
    def test_missing_optional_sources_preserve_recording_and_scene_state(self):
        obs = StubOBS()
        obs.missing = {"Logitech Brio", "Desktop Audio", "Screen vignette"}
        result = obs.status()
        self.assertTrue(result["outputActive"])
        self.assertEqual(result["currentProgramSceneName"], "Intro")
        self.assertTrue(result["connectionAvailable"])
        self.assertTrue(result["statusFresh"])
        self.assertFalse(result["blurAvailable"])
        self.assertFalse(result["audioAvailable"]["Desktop Audio"])
        self.assertTrue(result["audioAvailable"]["Mic/Aux"])
        self.assertIs(result["audioMutes"]["Mic/Aux"], False)
        self.assertNotIn("screen", result["vignettes"])
        self.assertIn("camera", result["vignettes"])
        self.assertEqual(len(result["warnings"]), 3)
        stopped = obs.record(False)
        self.assertFalse(stopped["outputActive"])
        self.assertTrue(stopped["actionSucceeded"])
        self.assertEqual(stopped["outputPath"], "/tmp/offline-test-recording.mkv")

    def test_optional_transport_loss_keeps_core_state_and_stops_extra_queries(self):
        obs = StubOBS()
        obs.fail_optional_transport = True
        result = obs.status()
        self.assertTrue(result["outputActive"])
        self.assertFalse(result["connectionAvailable"])
        self.assertEqual(len(result["warnings"]), 1)
        self.assertEqual(obs.requests[-1], ("GetInputMute", {"inputName": "Mic/Aux"}))

    def test_missing_intro_sound_is_a_warning_after_confirmed_recording_start(self):
        obs = StubOBS()
        obs.active = False
        obs.missing = {"Lab intro sound"}
        with self.assertLogs("umberto_lab.obs_remote", level="WARNING"):
            result = obs.record(True)
        self.assertTrue(result["outputActive"])
        self.assertTrue(result["actionSucceeded"])
        self.assertTrue(any("Intro sound" in warning for warning in result["warnings"]))
        self.assertEqual(sum(kind == "StartRecord" for kind, _ in obs.requests), 1)

    def test_confirmed_recording_survives_refresh_failure_and_later_scene_action(self):
        obs = StubOBS()
        obs.active = False
        obs.status()
        obs.fail_status = True
        with self.assertLogs("umberto_lab.obs_remote", level="WARNING"):
            started = obs.record(True)
            changed = obs.scene("Software Demo")
        for result in (started, changed):
            self.assertTrue(result["outputActive"])
            self.assertTrue(result["actionSucceeded"])
            self.assertFalse(result["statusFresh"])
            self.assertFalse(result["connectionAvailable"])
        self.assertEqual(changed["currentProgramSceneName"], "Software Demo")
        self.assertEqual(len(changed["warnings"]), 1)

    def test_primary_recording_rejection_is_not_reported_as_success(self):
        obs = OBS()
        obs.ws = FakeSocket([response(False)])
        with self.assertRaises(OBSRequestError):
            obs.record(True)
        self.assertEqual(len(obs.ws.sent), 1)

    def test_stale_scene_snapshot_cannot_expose_previous_screen_identity(self):
        obs = StubOBS()
        obs.status()
        obs._last_status["screen"] = {"itemId": 7, "zoom": 2, "x": 0, "y": 0}
        obs.fail_status = True
        with self.assertLogs("umberto_lab.obs_remote", level="WARNING"):
            result = obs.scene("Detail")
        self.assertEqual(result["currentProgramSceneName"], "Detail")
        self.assertIsNone(result["screen"])

    def test_later_refresh_failure_cannot_report_an_old_saved_filename(self):
        obs = StubOBS()
        stopped = obs.record(False)
        self.assertIn("outputPath", stopped)
        obs.fail_status = True
        with self.assertLogs("umberto_lab.obs_remote", level="WARNING"):
            started = obs.record(True)
        self.assertTrue(started["outputActive"])
        self.assertNotIn("outputPath", started)

    def test_title_update_failure_reports_saved_local_state(self):
        obs = StubOBS()
        obs.missing = {"Intro artwork"}
        values = {"vibeware": True, "sound_enabled": False, "sound_volume": 18}
        with self.assertRaises(ProjectApplyError) as caught:
            obs.apply_titles(values, prepared=True)
        self.assertTrue(caught.exception.saved_locally)
        self.assertIn("saved locally", str(caught.exception))
        self.assertIn("incomplete", str(caught.exception))


if __name__ == "__main__":
    unittest.main()
