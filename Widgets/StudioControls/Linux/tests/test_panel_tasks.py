"""Exercise actual controller methods without GTK, cameras or live OBS.

Only the class declaration is loaded from the UI module. Display construction
and imports are deliberately excluded; small widgets/executors model the event
loop boundary while the production controller methods run unchanged.
"""

import ast
from concurrent.futures import CancelledError, Future
import json
from pathlib import Path
import tempfile
from threading import Event
from types import SimpleNamespace
import unittest
from unittest.mock import Mock

from obs_remote import OBSConnectionError
from panel_config import DEFAULT_SCREEN_PRESETS, load_screen_presets


class Widget:
    def __init__(self, text="", value=0, sensitive=True):
        self.text = text
        self.value = value
        self.sensitive = sensitive
        self.classes = set()

    def set_sensitive(self, enabled):
        self.sensitive = enabled

    def set_label(self, text):
        self.text = text

    def get_text(self):
        return self.text

    def set_text(self, text):
        self.text = text

    def get_value(self):
        return self.value

    def set_value(self, value):
        self.value = value

    def get_active(self):
        return bool(self.value)

    def set_active(self, value):
        self.value = value

    def add_css_class(self, name):
        self.classes.add(name)

    def remove_css_class(self, name):
        self.classes.discard(name)


class Loop:
    SOURCE_REMOVE = False
    SOURCE_CONTINUE = True

    def __init__(self):
        self.timers = {}
        self.idles = []
        self.next_id = 1
        self.MainContext = SimpleNamespace(default=lambda: self)

    def timeout_add(self, delay, function):
        identifier = self.next_id
        self.next_id += 1
        self.timers[identifier] = (delay, function)
        return identifier

    def source_remove(self, identifier):
        self.timers.pop(identifier, None)

    def find_source_by_id(self, identifier):
        return self.timers.get(identifier)

    def idle_add(self, function, *args):
        self.idles.append((function, args))


class Executor:
    def __init__(self):
        self.tasks = []
        self.closed = False

    def submit(self, function, *args):
        if self.closed:
            raise RuntimeError("Executor already closed")
        future = Future()
        self.tasks.append((future, function, args))
        return future

    def shutdown(self, wait=False, cancel_futures=False):
        self.closed = True
        if cancel_futures:
            for future, _, _ in self.tasks:
                future.cancel()

    def run_pending(self):
        tasks, self.tasks = self.tasks, []
        for future, function, args in tasks:
            if future.set_running_or_notify_cancel():
                try:
                    future.set_result(function(*args))
                except Exception as error:
                    future.set_exception(error)


def load_controller(loop):
    source = Path(__file__).resolve().parents[1] / "controlli-brio.py"
    tree = ast.parse(source.read_text())
    controller = next(
        node for node in tree.body if isinstance(node, ast.ClassDef) and node.name == "BrioPanel"
    )
    controller.bases = []
    module = ast.fix_missing_locations(ast.Module(body=[controller], type_ignores=[]))
    namespace = {
        "GLib": loop,
        "Gtk": SimpleNamespace(Application=SimpleNamespace(do_shutdown=lambda self: None)),
        "OBSConnectionError": OBSConnectionError,
        "CancelledError": CancelledError,
        "logger": Mock(),
        "load_config": lambda path: {"logo": ""},
        "prepare_cards": Mock(return_value={"name": "Example"}),
    }
    exec(compile(module, str(source), "exec"), namespace)
    return namespace["BrioPanel"], namespace


def make_panel(loop):
    controller, namespace = load_controller(loop)
    panel = object.__new__(controller)
    panel.__dict__.update(
        closing=False,
        system_settings=None,
        system_theme_handler=None,
        applying_titles=False,
        action_busy=False,
        active_operation=None,
        polling=False,
        visual_writing=False,
        visual_pending={},
        visual_timer=None,
        visual_syncing=False,
        blur_pending=None,
        blur_writing=False,
        blur_syncing=False,
        blur_available=False,
        blur_enabled=False,
        prepared_titles=None,
        prepared_timer=None,
        obs_connected=True,
        current_scene="Software Demo",
        screen_item=7,
        recording=True,
        paused=False,
        audio_mutes={"Mic/Aux": False},
        vignette_values={},
        vignette_controls={},
        scene_buttons={"Software Demo": Widget(), "Detail": Widget()},
        last_warnings=(),
        cancel_render=Event(),
        timers=[],
        futures=set(),
        pending={},
        worker=Executor(),
        renderer=Executor(),
        camera_worker=Executor(),
        task_errors={},
        title_entries={"name": Widget("Example")},
        link_entries=[Widget(), Widget(), Widget()],
        sound_check=Widget(value=False),
        vibe_check=Widget(value=False),
        sound_slider=Widget(value=18),
        logo_entry=Widget(),
        screen_controls=[Widget(value=1), Widget(), Widget()],
        audio_buttons={"Mic/Aux": Widget(), "Desktop Audio": Widget()},
    )
    for name in (
        "title_apply_button",
        "title_note",
        "obs_status",
        "record_button",
        "pause_button",
        "blur_button",
        "blur_slider",
        "screen_box",
        "result_note",
        "obs_warning",
        "screen_note",
    ):
        setattr(panel, name, Widget())
    buffer = SimpleNamespace(
        get_start_iter=lambda: 0, get_end_iter=lambda: 0, get_text=lambda *args: "Description"
    )
    panel.description = SimpleNamespace(get_buffer=lambda: buffer)
    panel.obs = SimpleNamespace(
        assets_root=Path("/tmp"),
        title_config=Path("/tmp/offline-panel-titles.json"),
        status=Mock(),
        close=Mock(),
        record=Mock(),
        apply_titles=Mock(),
        frame_screen=Mock(),
    )
    return panel, namespace


def status(scene="Software Demo", screen=None):
    return {
        "currentProgramSceneName": scene,
        "outputActive": True,
        "outputPaused": False,
        "blurAvailable": False,
        "blurEnabled": False,
        "audioMutes": {"Mic/Aux": False},
        "screen": screen,
        "vignettes": {},
        "warnings": [],
        "connectionAvailable": True,
        "statusFresh": True,
    }


class PanelTaskTests(unittest.TestCase):
    def setUp(self):
        self.loop = Loop()
        self.panel, self.namespace = make_panel(self.loop)

    def test_card_rendering_keeps_stop_available_on_obs_worker(self):
        panel = self.panel
        panel.apply_titles(None)
        self.assertTrue(panel.applying_titles)
        self.assertFalse(panel.action_busy)
        self.assertTrue(panel.record_button.sensitive)
        self.assertEqual(len(panel.renderer.tasks), 1)
        self.assertEqual(len(panel.worker.tasks), 0)
        self.assertTrue(panel.perform(panel.obs.record, False, operation="record"))
        self.assertEqual(len(panel.worker.tasks), 1)

    def test_poll_failure_does_not_finish_or_fail_unrelated_title_render(self):
        panel = self.panel
        panel.applying_titles = True
        panel.polling = True
        panel.title_note.text = "Preparing cards"
        panel.obs_result(None, OBSConnectionError("Offline"), "poll")
        self.assertTrue(panel.applying_titles)
        self.assertEqual(panel.title_note.text, "Preparing cards")
        self.assertFalse(panel.title_apply_button.sensitive)
        self.assertFalse(panel.polling)

    def test_initial_offline_state_invites_opening_obs_without_claiming_recording_loss(self):
        panel = self.panel
        panel.current_scene = None
        panel.obs_connected = False
        panel.recording = False
        panel.obs_result(None, OBSConnectionError("Not running"), "poll")
        self.assertEqual(
            panel.obs_status.text, "Waiting for OBS · open OBS with WebSocket enabled."
        )
        self.assertEqual(panel.blur_button.text, "Background blur · offline")
        self.assertFalse(panel.record_button.sensitive)
        for button in panel.audio_buttons.values():
            self.assertTrue(button.text.endswith(" · offline"))
            self.assertFalse(button.sensitive)

    def test_connection_loss_keeps_recording_warning_until_obs_state_is_restored(self):
        panel = self.panel
        panel.obs_result(None, OBSConnectionError("Connection lost"), "poll")
        self.assertIn("recording state unconfirmed", panel.obs_status.text)
        self.assertFalse(panel.record_button.sensitive)
        panel.obs_result(status(), None, "poll")
        self.assertTrue(panel.record_button.sensitive)
        self.assertEqual(panel.audio_buttons["Mic/Aux"].text, "Microphone · ON")
        self.assertNotIn("offline", panel.blur_button.text)

    def test_scene_change_drops_old_screen_identity_while_other_effect_is_pending(self):
        panel = self.panel
        old_screen = (panel.obs.frame_screen, ("Software Demo", 7, 2, 0, 0))
        panel.visual_pending = {"screen": old_screen, "vignette-camera": (Mock(), ())}
        panel.obs_result(status("Detail", {"itemId": 7, "zoom": 1, "x": 0, "y": 0}), None, "scene")
        self.assertNotIn("screen", panel.visual_pending)
        self.assertIsNone(panel.screen_item)
        self.assertFalse(panel.screen_box.sensitive)
        panel.queue_screen(None)
        self.assertNotIn("screen", panel.visual_pending)

    def test_scene_change_can_sync_fresh_identity_when_no_other_effect_is_pending(self):
        panel = self.panel
        panel.visual_pending = {"screen": (panel.obs.frame_screen, ("Software Demo", 7, 2, 0, 0))}
        panel.obs_result(
            status("Detail", {"itemId": 9, "zoom": 2, "x": 20, "y": 30}), None, "scene"
        )
        panel.queue_screen(None)
        _, arguments = panel.visual_pending["screen"]
        self.assertEqual(arguments, ("Detail", 9, 2, 20, 30))

    def test_prepared_titles_wait_with_one_timer_and_apply_once(self):
        panel = self.panel
        panel.polling = True
        panel.prepared_titles = {"name": "Example"}
        for _ in range(20):
            panel.apply_prepared_titles()
        self.assertEqual(len(self.loop.timers), 1)
        self.assertEqual(panel.timers, [])
        panel.polling = False
        self.assertFalse(panel.apply_prepared_titles())
        self.assertEqual(len(panel.worker.tasks), 1)
        self.assertIsNone(panel.prepared_timer)
        self.assertIsNone(panel.prepared_titles)
        panel.action_busy = False
        panel.apply_prepared_titles()
        self.assertEqual(len(panel.worker.tasks), 1)

    def test_shutdown_cancels_queued_mutations_and_only_closes_obs(self):
        panel = self.panel
        panel.perform(panel.obs.record, True, operation="record")
        panel.visual_pending = {"screen": (panel.obs.frame_screen, ("Software Demo", 7, 2, 0, 0))}
        panel.prepared_titles = {"name": "Example"}
        panel.prepared_timer = self.loop.timeout_add(60, panel.apply_prepared_titles)
        panel.do_shutdown()
        panel.worker.run_pending()
        panel.obs.record.assert_not_called()
        panel.obs.frame_screen.assert_not_called()
        panel.obs.close.assert_called_once()
        self.assertTrue(panel.cancel_render.is_set())
        self.assertFalse(panel.visual_pending)
        self.assertIsNone(panel.prepared_titles)
        self.assertFalse(self.loop.timers)
        self.assertFalse(self.loop.idles)
        self.assertFalse(panel.perform(panel.obs.record, True, operation="record"))
        self.assertFalse(panel.poll_obs())

    def test_completed_background_task_does_not_update_ui_after_shutdown(self):
        panel = self.panel
        callback = Mock()
        panel.submit_task(lambda: {}, (), callback, operation="poll")
        future, _, _ = panel.worker.tasks[0]
        future.set_running_or_notify_cancel()
        panel.closing = True
        future.set_result({})
        self.assertFalse(self.loop.idles)
        callback.assert_not_called()


class PresetValidationTests(unittest.TestCase):
    def test_wrong_root_or_nonfinite_values_fall_back_without_crashing(self):
        bad_values = [
            [],
            {"Overview": [float("nan"), 0, 0]},
            {"Overview": [1, float("inf"), 0]},
            {"Overview": [True, 0, 0]},
        ]
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "screen-presets.json"
            for values in bad_values:
                with self.subTest(values=values):
                    path.write_text(json.dumps(values))
                    with self.assertLogs("umberto_lab.panel_config", level="WARNING"):
                        self.assertEqual(load_screen_presets(path), DEFAULT_SCREEN_PRESETS)

    def test_out_of_range_finite_values_are_bounded(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "screen-presets.json"
            path.write_text(json.dumps({"Overview": [99, -1000, 500]}))
            self.assertEqual(load_screen_presets(path)["Overview"], [4, -100, 100])


if __name__ == "__main__":
    unittest.main()
