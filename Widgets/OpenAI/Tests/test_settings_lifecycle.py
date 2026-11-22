"""Exercise the UI methods with a controlled backend and event queue, without GTK."""
import ast
from pathlib import Path
from types import MethodType, SimpleNamespace
import unittest


class SettingsLifecycleChecks(unittest.TestCase):
    def setUp(self):
        self.workers, self.callbacks, self.saved = [], [], []
        self.fail = False
        fixture = self

        class Worker:
            def __init__(self, target, daemon):
                self.target = target

            def start(self):
                fixture.workers.append(self.target)

        def invoke(command, value):
            if fixture.fail:
                raise OSError("Disk is not writable")
            fixture.saved.append(dict(value))

        source = ast.parse((Path(__file__).resolve().parents[1] / "Linux/openai_usage.py").read_text())
        coordinator = ast.parse((Path(__file__).resolve().parents[1] / "Linux/settings_ui.py").read_text())
        window = next(node for node in source.body if isinstance(node, ast.ClassDef) and node.name == "UsageWindow")
        helper = next(node for node in coordinator.body if isinstance(node, ast.ClassDef) and node.name == "SettingsUiMixin")
        window.body += helper.body
        methods = [node for node in window.body if isinstance(node, ast.FunctionDef) and node.name in
                   ("save_async", "on_close", "exit_application", "settings_loaded", "set_settings_error")]
        environment = {"threading": SimpleNamespace(Thread=Worker), "invoke": invoke,
                       "GLib": SimpleNamespace(idle_add=self.callbacks.append, source_remove=lambda _: None),
                       "log_event": lambda *_: None}
        exec(compile(ast.Module(body=methods, type_ignores=[]), str(__file__), "exec"), environment)
        self.window = SimpleNamespace(saving=False, backend=invoke, settings_save_source=0, settings={"bigSize": True}, settings_revision=1,
                                      saved_settings_revision=0, closed=False, exit_pending=False,
                                      settings_ready=False, settings_error=None, settings_status_label=None,
                                      allow_close=False, tray=None, sound_process=None, clock_id=1, refresh_id=2,
                                      problem=SimpleNamespace(set_visible=lambda _: None, set_tooltip_text=lambda _: None),
                                      present=lambda: None, refresh=lambda: None)
        for method in methods:
            setattr(self.window, method.name, MethodType(environment[method.name], self.window))
        self.window.close = lambda: self.window.on_close()

    def complete_write(self):
        self.workers.pop(0)()
        self.callbacks.pop(0)()

    def test_exit_drains_latest_revision_before_destroying_window(self):
        window = self.window
        window.save_async()
        window.settings = {"bigSize": False}
        window.settings_revision = 2
        window.save_async()
        window.exit_application()
        self.assertFalse(window.closed)
        self.complete_write()
        self.assertFalse(window.closed)
        self.complete_write()
        self.assertTrue(window.closed)
        self.assertEqual(self.saved[-1], {"bigSize": False})
        self.assertEqual(window.saved_settings_revision, 2)

    def test_dirty_exit_starts_a_save_and_waits(self):
        self.window.exit_application()
        self.assertFalse(self.window.closed)
        self.complete_write()
        self.assertTrue(self.window.closed)
        self.assertEqual(self.saved, [{"bigSize": True}])

    def test_save_failure_cancels_exit_until_explicit_retry(self):
        self.fail = True
        self.window.exit_application()
        self.complete_write()
        self.assertFalse(self.window.closed)
        self.assertFalse(self.window.exit_pending)
        self.assertFalse(self.window.allow_close)
        self.assertEqual(self.window.settings_error, "Disk is not writable")
        self.assertEqual(self.workers, [])
        self.fail = False
        self.window.exit_application()
        self.complete_write()
        self.assertTrue(self.window.closed)
        self.assertIsNone(self.window.settings_error)

    def test_repeated_exit_and_further_edits_are_serialized(self):
        self.window.exit_application()
        self.window.exit_application()
        self.assertEqual(len(self.workers), 1)
        self.window.settings = {"bigSize": False, "alert20": True}
        self.window.settings_revision = 3
        self.complete_write()
        self.complete_write()
        self.assertTrue(self.window.closed)
        self.assertEqual(self.saved[-1], self.window.settings)

    def test_refresh_does_not_replace_unsaved_settings(self):
        self.window.settings_loaded({"settings": {"bigSize": False}, "recoveryMessage": None}, 1)
        self.assertEqual(self.window.settings, {"bigSize": True})

    def test_invalid_settings_still_enable_editing(self):
        self.window.saved_settings_revision = self.window.settings_revision
        self.window.sync_pin = lambda: None
        self.window.sync_taskbar = lambda: None
        self.window.apply_size = lambda: None
        self.window.settings_loaded({"settings": {"bigSize": False}, "recoveryMessage": "Original preserved on save"}, 1)
        self.assertTrue(self.window.settings_ready)
        self.assertEqual(self.window.settings_error, "Original preserved on save")


if __name__ == "__main__":
    unittest.main()
