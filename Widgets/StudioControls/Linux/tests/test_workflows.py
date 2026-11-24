"""Portable rundown persistence and deterministic playback, without a display or OBS."""

import fcntl
import json
import tempfile
import unittest
from pathlib import Path
from threading import Event
from unittest.mock import patch

from obs_remote import OBS, OBSConnectionError, SCREEN_SOURCE
from workflows import LEGACY_PROJECT_ID, MAX_FILE_BYTES, Playback, WorkflowLibrary, new_step


class LibraryTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.path = Path(directory.name) / "workflows.json"
        self.library = WorkflowLibrary(self.path, legacy_name="Existing project")

    def test_roundtrip_isolated_projects_and_independent_workflow_copies(self):
        legacy = self.library.project
        self.assertEqual(legacy["id"], LEGACY_PROJECT_ID)
        first = legacy["workflows"][0]
        first["targetSeconds"] = 180
        first["steps"] = [new_step("Detail", 20, "Show the knob"), new_step("Detail", 60)]
        duplicate = self.library.create_workflow("Alternate edit", source=first)
        self.assertEqual(duplicate["targetSeconds"], 180)
        self.assertNotEqual(first["steps"][0]["id"], duplicate["steps"][0]["id"])
        duplicate["steps"][0]["seconds"] = 7
        project = self.library.create_project("Second project")
        self.assertEqual(len(project["workflows"]), 1)
        self.assertEqual(project["workflows"][0]["steps"], [])
        reopened = WorkflowLibrary(self.path)
        self.assertEqual(reopened.project["id"], project["id"])
        reopened.select_project(LEGACY_PROJECT_ID)
        self.assertEqual(reopened.project, legacy)
        self.assertEqual(reopened.project["workflows"][0]["steps"][0]["seconds"], 20)

    def test_failed_write_and_invalid_edits_preserve_last_saved_state(self):
        self.library.save()
        saved = self.path.read_bytes()
        self.library.project["workflows"][0]["steps"] = [new_step("Intro")]
        with patch("workflows.os.replace", side_effect=OSError), self.assertRaises(OSError):
            self.library.save()
        self.assertEqual(self.path.read_bytes(), saved)
        self.assertEqual(self.library.project["workflows"][0]["steps"], [])
        with self.assertRaises(ValueError):
            self.library.select_project("not a project")
        self.assertEqual(self.library.data, json.loads(saved))

    def test_stale_instance_cannot_overwrite_newer_edits(self):
        self.library.save()
        stale = WorkflowLibrary(self.path)
        self.library.create_workflow("From first instance")
        with self.assertRaisesRegex(ValueError, "changed on disk"):
            stale.create_workflow("From second instance")
        self.assertEqual(WorkflowLibrary(self.path).data, self.library.data)

    def test_busy_writer_does_not_block_the_user_interface(self):
        self.library.save()
        with self.path.with_suffix(".json.lock").open("r+b") as writer:
            fcntl.flock(writer, fcntl.LOCK_EX | fcntl.LOCK_NB)
            with self.assertRaisesRegex(ValueError, "Another instance"):
                self.library.create_project("Draft")
        self.assertEqual(len(self.library.data["projects"]), 1)
        self.assertEqual(WorkflowLibrary(self.path).data, self.library.data)

    def test_unreadable_or_unsupported_library_preserved(self):
        for payload in (b"bad-json", b"{}", b"x" * (MAX_FILE_BYTES + 1)):
            with self.subTest(size=len(payload)):
                self.path.write_bytes(payload)
                with self.assertRaises(ValueError):
                    WorkflowLibrary(self.path)
                self.assertEqual(self.path.read_bytes(), payload)

    def test_ids_are_safe_path_components_and_durations_are_bounded_integers(self):
        self.library.save()
        data = json.loads(self.path.read_bytes())
        for key in ("../../escape", None, 12, LEGACY_PROJECT_ID.replace("-", "")):
            with self.subTest(key=key):
                data["projects"][0]["id"] = data["activeProjectId"] = key
                self.path.write_text(json.dumps(data))
                with self.assertRaises(ValueError):
                    WorkflowLibrary(self.path)
        for duration in (-1, 0, True, 2.5, 86401):
            with self.assertRaises(ValueError):
                new_step("Intro", duration)

    def test_duplicate_step_ids_rejected_and_rolled_back(self):
        flow = self.library.project["workflows"][0]
        step = new_step("Intro")
        flow["steps"] = [step, dict(step)]
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            self.library.save()
        self.assertEqual(self.library.project["workflows"][0]["steps"], [])


class PlaybackTests(unittest.TestCase):
    def setUp(self):
        self.now = 1000
        self.p = Playback(clock=lambda: self.now)
        self.steps = [
            new_step("Intro", 15),
            new_step("That's me", 20),
            new_step("Detail", 60),
            new_step("On the Table", 75),
            new_step("Thank you", 10),
        ]

    def test_rehearsal_boundaries_and_stall(self):
        self.p.start(self.steps)
        self.steps[0]["seconds"] = 1
        for elapsed, index, left in ((0, 0, 15), (15, 1, 20), (35, 2, 60), (170, 4, 10)):
            with self.subTest(elapsed=elapsed):
                self.now = 1000 + elapsed
                self.p.tick()
                self.assertEqual(self.p.index, index)
                self.assertEqual(self.p.remaining, left)
                self.assertEqual(self.p.total_remaining, 180 - elapsed)
                self.assertIsNone(self.p.pending)
        self.now += 10
        self.p.tick()
        self.assertEqual(self.p.state, "finished")
        self.assertEqual(self.p.total_remaining, 0)

    def test_live_dwell_begins_after_confirmation_and_no_catchup(self):
        self.p.start(self.steps, live=True)
        token, scene = self.p.pending
        self.now += 300
        self.p.tick()
        self.assertEqual(self.p.pending, (token, scene))
        self.p.confirm(token, scene)
        self.assertEqual(self.p.remaining, 15)
        self.now += 300
        self.p.tick()
        self.assertEqual(self.p.index, 1)
        self.assertEqual(self.p.pending[1], "That's me")
        self.p.confirm(*self.p.pending)
        self.assertEqual(self.p.remaining, 20)

    def test_stale_reply_after_stop_or_restart_cannot_rearm(self):
        self.p.start(self.steps, live=True)
        first = self.p.pending
        self.p.stop("Manual stop")
        self.p.confirm(*first)
        self.assertEqual(self.p.state, "stopped")
        self.p.start(self.steps, live=True)
        second = self.p.pending
        self.p.fail(first[0], "Stale timeout")
        self.p.confirm(*first)
        self.assertEqual(self.p.pending, second)
        self.p.fail(second[0], "No reply")
        self.now += 180
        self.p.tick()
        self.assertIsNone(self.p.pending)
        self.assertEqual(self.p.state, "stopped")

    def test_mismatched_confirmation_cannot_advance(self):
        self.p.start(self.steps, live=True)
        self.p.confirm(self.p.pending[0], "Privacy")
        self.assertEqual(self.p.state, "stopped")
        self.assertIsNone(self.p.pending)

    def test_external_scene_override_during_pause_cancels_run(self):
        self.p.start(self.steps, live=True)
        self.p.confirm(*self.p.pending)
        self.now += 5
        self.p.pause()
        self.now += 100
        self.p.observe("Intro")
        self.assertEqual(self.p.remaining, 10)
        self.p.observe("Privacy")
        self.p.resume()
        self.p.next()
        self.p.tick()
        self.assertIsNone(self.p.pending)
        self.assertEqual(self.p.state, "stopped")

    def test_next_then_pause_resume(self):
        self.p.start(self.steps)
        self.p.next()
        self.now += 3
        self.p.pause()
        self.now += 50
        self.p.tick()
        self.assertEqual(self.p.remaining, 17)
        self.p.resume()
        self.now += 17
        self.p.tick()
        self.assertEqual(self.p.index, 2)

    def test_free_timer_overrun_survives_pause_without_scene_requests(self):
        self.p.start(self.steps, live=True)
        old = self.p.pending
        self.p.start_timer(5)
        self.p.confirm(*old)
        self.now += 7
        self.p.tick()
        self.assertEqual(self.p.total_remaining, -2)
        self.p.pause()
        self.now += 60
        self.p.resume()
        self.now += 2
        self.p.next()
        self.assertEqual(self.p.remaining, -4)
        self.assertIsNone(self.p.pending)

    def test_invalid_start_does_not_interrupt_a_running_timer(self):
        self.p.start_timer(3)
        with self.assertRaises(ValueError):
            self.p.start([])
        self.assertEqual(self.p.state, "running")
        self.assertEqual(self.p.mode, "timer")


class LiveAdapterTests(unittest.TestCase):
    def setUp(self):
        self.obs = OBS()
        self.obs.ws = object()
        self.scene = "Intro"
        self.collection = "Synthetic scenes"
        self.recording = True
        self.paused = False
        self.cancel = Event()
        self.requests = []
        # OBS is free to return scene items in stack order rather than screen order.
        self.items = [
            {
                "sourceName": SCREEN_SOURCE,
                "sceneItemId": 1,
                "sceneItemTransform": {
                    "sourceWidth": 1921,
                    "positionX": 1300,
                    "cropLeft": 960,
                    "cropRight": 0,
                    "cropTop": 0,
                    "cropBottom": 0,
                },
            },
            {
                "sourceName": SCREEN_SOURCE,
                "sceneItemId": 2,
                "sceneItemTransform": {
                    "sourceWidth": 1921,
                    "positionX": 80,
                    "cropLeft": 0,
                    "cropRight": 961,
                    "cropTop": 0,
                    "cropBottom": 0,
                },
            },
        ]
        self.obs.call = self.request

    def request(self, kind, data=None):
        self.requests.append(kind)
        if kind == "GetSceneList":
            return {
                "currentProgramSceneName": self.scene,
                "scenes": [
                    {"sceneName": s} for s in ("Intro", "Detail", "Privacy", "Comparison A-B")
                ],
            }
        if kind == "GetSceneCollectionList":
            return {"currentSceneCollectionName": self.collection}
        if kind == "GetSceneItemList":
            self.assertEqual(data, {"sceneName": "Comparison A-B"})
            return {"sceneItems": self.items}
        if kind == "GetCurrentProgramScene":
            return {"currentProgramSceneName": self.scene}
        if kind == "GetRecordStatus":
            return {"outputActive": self.recording, "outputPaused": self.paused}
        if kind == "SetCurrentProgramScene":
            self.scene = data["sceneName"]
            return {}
        self.fail(f"Unexpected OBS request {kind}")

    def prepare(self):
        socket, collection, expected, active = self.obs.workflow_preflight(["Intro", "Detail"])
        self.assertTrue(active)
        self.assertEqual(expected, "Intro")
        return (socket, collection)

    def test_switch_once_and_verify_through_existing_socket(self):
        session = self.prepare()
        confirmed, recording = self.obs.workflow_switch(
            session, "Intro", "Detail", self.cancel, True
        )
        self.assertEqual(confirmed, "Detail")
        self.assertTrue(recording)
        self.assertEqual(self.requests[-2:], ["SetCurrentProgramScene", "GetCurrentProgramScene"])
        self.obs.workflow_switch(session, "Detail", "Detail", self.cancel, True)
        self.assertEqual(self.requests.count("SetCurrentProgramScene"), 1)

    def test_preflight_validates_comparison_split_only_when_used(self):
        self.prepare()
        self.assertNotIn("GetSceneItemList", self.requests)
        self.obs.workflow_preflight(["Intro", "Comparison A-B", "Comparison A-B"])
        self.assertEqual(self.requests.count("GetSceneItemList"), 1)
        self.items[1]["sceneItemTransform"]["cropRight"] -= 1
        with self.assertRaisesRegex(ValueError, "calibrate"):
            self.obs.workflow_preflight(["Comparison A-B"])
        self.assertFalse(any(kind.startswith("Set") for kind in self.requests))

    def test_display_change_after_preflight_blocks_comparison_without_recropping(self):
        socket, collection, expected, active = self.obs.workflow_preflight(["Comparison A-B"])
        self.items[1]["sceneItemTransform"]["sourceWidth"] = 3840
        with self.assertRaisesRegex(ValueError, "calibrate"):
            self.obs.workflow_switch(
                (socket, collection), expected, "Comparison A-B", self.cancel, active
            )
        self.assertFalse(any(kind.startswith("Set") for kind in self.requests))

    def test_unknown_scene_preflight_read_only(self):
        with self.assertRaises(ValueError):
            self.obs.workflow_preflight(["Chapter"])
        self.assertNotIn("SetCurrentProgramScene", self.requests)

    def test_takeover_disconnect_collection_and_recording_changes_block_mutations(self):
        for change in ("manual", "collection", "disconnected", "stopped", "paused"):
            with self.subTest(change=change):
                self.setUp()
                session = self.prepare()
                if change == "manual":
                    self.scene = "Privacy"
                elif change == "collection":
                    self.collection = "Other collection"
                elif change == "disconnected":
                    self.obs.ws = object()
                elif change == "stopped":
                    self.recording = False
                else:
                    self.paused = True
                with self.assertRaises(ValueError):
                    self.obs.workflow_switch(session, "Intro", "Detail", self.cancel, True)
                self.assertNotIn("SetCurrentProgramScene", self.requests)

    def test_unconfirmed_recording_and_pause_fields_prevent_first_mutation(self):
        for status in ({}, {"outputActive": False}, {"outputActive": 0, "outputPaused": False}):
            with self.subTest(status=status):
                self.setUp()
                session = self.prepare()

                def invalid_status(kind, data=None, status=status):
                    return status if kind == "GetRecordStatus" else self.request(kind, data)

                self.obs.call = invalid_status
                with self.assertRaisesRegex(ValueError, "unconfirmed"):
                    self.obs.workflow_switch(session, "Intro", "Detail", self.cancel, False)
                self.assertNotIn("SetCurrentProgramScene", self.requests)

    def test_stop_while_checking_program_scene_cancels_the_pending_send(self):
        session = self.prepare()

        def stop_on_last_read(kind, data=None):
            if kind == "GetCurrentProgramScene":
                self.cancel.set()
            return self.request(kind, data)

        self.obs.call = stop_on_last_read
        self.assertIsNone(self.obs.workflow_switch(session, "Intro", "Detail", self.cancel, True))
        self.assertNotIn("SetCurrentProgramScene", self.requests)

    def test_lost_reply_never_replays_a_scene_change(self):
        session = self.prepare()

        def lose_reply(kind, data=None):
            reply = self.request(kind, data)
            if kind == "SetCurrentProgramScene":
                raise OBSConnectionError("Synthetic uncertain response", request_sent=True)
            return reply

        self.obs.call = lose_reply
        with self.assertRaises(OBSConnectionError):
            self.obs.workflow_switch(session, "Intro", "Detail", self.cancel, True)
        self.assertEqual(self.scene, "Detail")
        self.assertEqual(self.requests.count("SetCurrentProgramScene"), 1)


if __name__ == "__main__":
    unittest.main()
