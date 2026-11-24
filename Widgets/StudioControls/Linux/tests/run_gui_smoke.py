"""Run a GTK smoke test in a private X display/session with fake devices."""

from __future__ import annotations

import importlib.util
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import traceback


ROOT = Path(__file__).resolve().parents[1]


def run_isolated() -> int:
    for command in ("xvfb-run", "dbus-run-session"):
        if not shutil.which(command):
            print(f"GUI test requires {command}; no app or hardware was opened.", file=sys.stderr)
            return 2
    with tempfile.TemporaryDirectory(prefix="umberto-lab-gui-") as temporary:
        runtime_directory = Path(temporary) / "runtime"
        runtime_directory.mkdir(mode=0o700)
        bus_configuration = Path(temporary) / "session.conf"
        # Omit service directories: this fixture must not activate desktop
        # portals, a keyring, PipeWire or any service from the owner's session.
        bus_configuration.write_text(
            "<busconfig><type>session</type><auth>EXTERNAL</auth>"
            f"<listen>unix:tmpdir={runtime_directory}</listen>"
            '<policy context="default"><allow send_destination="*" eavesdrop="true"/>'
            '<allow eavesdrop="true"/><allow own="*"/></policy></busconfig>'
        )
        environment = dict(os.environ)
        environment.pop("DBUS_SESSION_BUS_ADDRESS", None)
        environment.pop("DISPLAY", None)
        environment.update(
            HOME=temporary,
            XDG_CONFIG_HOME=str(Path(temporary) / "config"),
            XDG_DATA_HOME=str(Path(temporary) / "data"),
            XDG_STATE_HOME=str(Path(temporary) / "state"),
            XDG_CACHE_HOME=str(Path(temporary) / "cache"),
            XDG_RUNTIME_DIR=str(runtime_directory),
            PYTHONPYCACHEPREFIX=str(Path(temporary) / "pycache"),
            GDK_BACKEND="x11",
            GSK_RENDERER="cairo",
            GSETTINGS_BACKEND="memory",
            GIO_USE_VFS="local",
            GTK_USE_PORTAL="0",
            GTK_A11Y="none",
            UMBERTO_LAB_GUI_TEST="1",
        )
        command = [
            "xvfb-run",
            "-a",
            "-s",
            "-screen 0 1280x1080x24",
            "dbus-run-session",
            f"--config-file={bus_configuration}",
            "--",
            sys.executable,
            str(Path(__file__).resolve()),
            "--child",
        ]
        for option in ("--exercise-failure", "--screenshots", "--offline"):
            if option in sys.argv:
                command.append(option)
        try:
            return subprocess.run(
                command, env=environment, cwd=ROOT, timeout=25, check=False
            ).returncode
        except subprocess.TimeoutExpired:
            print("FAIL: isolated GUI did not exit within 25 seconds", file=sys.stderr)
            return 1


def run_child() -> int:
    if os.environ.get("UMBERTO_LAB_GUI_TEST") != "1":
        print(
            "Use the test launcher without --child to create an isolated session.", file=sys.stderr
        )
        return 2
    sys.path.insert(0, str(ROOT))
    from gi.repository import GLib

    spec = importlib.util.spec_from_file_location("panel", ROOT / "controlli-brio.py")
    panel = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = panel
    spec.loader.exec_module(panel)

    def disconnected():
        raise FileNotFoundError("Test camera: intentionally unavailable")

    class FakeOBS:
        def __init__(self, **_options):
            self.calls = []
            self.scene_name = "Software Demo"

        def status(self):
            if "--offline" in sys.argv:
                raise panel.OBSConnectionError("Test OBS: intentionally unavailable")
            return {
                "currentProgramSceneName": self.scene_name,
                "outputActive": False,
                "outputPaused": False,
                "blurEnabled": True,
                "blurAvailable": True,
                "blurIntensity": 4,
                "screen": {"itemId": 1, "zoom": 1, "x": 0, "y": 0},
                "vignettes": {
                    key: {"enabled": True, "strength": 25}
                    for key in ("screen", "camera", "hardware")
                },
                "audioMutes": {"Mic/Aux": True, "Desktop Audio": True},
            }

        def workflow_preflight(self, required, transitions=()):
            assert list(transitions) == ["fade", "default"]
            assert set(required) == {"Intro", "Detail"}
            return self, "Synthetic collection", self.scene_name, False

        def workflow_switch(self, session, expected, scene, cancel, recording, transition="default"):
            assert transition == ("fade" if scene == "Intro" else "default")
            assert session[0] is self and expected == self.scene_name
            if cancel.is_set():
                return None
            self.scene_name = scene
            self.calls.append(("workflow", scene))
            return scene, False

        def scene(self, scene):
            self.scene_name = scene
            return self.status()

        def select_card_project(self, root, settings):
            self.assets_root = root
            self.title_config = settings

        def frame_screen(self, *arguments):
            self.calls.append(("screen", arguments))

        def vignette(self, *arguments):
            self.calls.append(("vignette", arguments))

        def close(self):
            pass

    panel.snapshot = disconnected
    panel.move_to_side_monitor = lambda _: False
    panel.OBS = FakeOBS
    app = panel.BrioPanel()
    result = {"completed": False, "failed": False}

    def fail(kind, error, tb):
        result["failed"] = True
        traceback.print_exception(kind, error, tb)
        app.quit()

    previous_hook = sys.excepthook
    sys.excepthook = fail

    def guarded(callback):
        def invoke():
            try:
                return callback()
            except BaseException:
                fail(*sys.exc_info())
                return GLib.SOURCE_REMOVE

        return invoke

    def finish():
        if "--exercise-failure" in sys.argv:
            raise AssertionError("Intentional callback failure for test-runner verification")
        if "--offline" in sys.argv:
            assert not app.obs.calls
            assert not app.record_button.get_sensitive()
            assert app.obs_status.get_label().startswith("Waiting for OBS")
            assert app.blur_button.get_label().endswith(" · offline")
            assert all(
                button.get_label().endswith(" · offline") for button in app.audio_buttons.values()
            )
        else:
            assert any(
                kind == "screen" and args[-3:] == (2, 75, -50) for kind, args in app.obs.calls
            ), app.obs.calls
            assert any(
                kind == "vignette" and args == ("screen", False, 43) for kind, args in app.obs.calls
            ), app.obs.calls
        assert app.tabs.get_n_pages() == 4
        app.theme_selector.set_selected(0)
        assert app.effective_theme == "light"
        assert panel.load_theme() == "light"
        from localization import CODES, load_language

        original_record_label = app.record_button.get_label()
        original_scene = app.current_scene
        original_project = dict(app.title_values)
        for index, code in enumerate(CODES):
            app.language_selector.set_selected(index)
            assert app.localizer.language == code
            assert load_language() == code
            assert app.record_button.get_label() == app.localizer.text(original_record_label)
            assert app.current_scene == original_scene
            assert app.title_values == original_project
            assert app.theme_preference == "light"
        app.language_selector.set_selected(CODES.index("en"))
        flow = app.workflow

        def drop(widget, payload, x):
            target = next(
                c for c in widget.observe_controllers() if isinstance(c, panel.Gtk.DropTarget)
            )
            return target.emit("drop", payload, x, 0)

        assert drop(flow.timeline, "scene:Detail", 0)
        assert drop(flow.timeline, "scene:Intro", 0)
        detail, intro = flow.flow["steps"]
        assert drop(flow.step_widgets[detail["id"]], "step:" + intro["id"], -1)
        assert [s["scene"] for s in flow.flow["steps"]] == ["Intro", "Detail"]
        from workflow_dialogs import workflow_details
        from workflow_widgets import TRANSITIONS

        editor = flow.edit_step(intro["id"])
        assert editor.get_modal()
        editor.note.set_text("Synthetic cue")
        editor.seconds.set_value(1)
        editor.transition.set_selected(list(TRANSITIONS).index("fade"))
        editor.response(panel.Gtk.ResponseType.OK)
        assert flow.flow["steps"][0]["seconds"] == 1
        assert flow.flow["steps"][0]["note"] == "Synthetic cue"
        assert flow.flow["steps"][0]["transition"] == "fade"
        editor = flow.edit_step(intro["id"])
        editor.note.set_text("Discard me")
        editor.response(panel.Gtk.ResponseType.CANCEL)
        assert flow.flow["steps"][0]["note"] == "Synthetic cue"

        def name_entry(widget):
            if isinstance(widget, panel.Gtk.Entry):
                return widget
            child = widget.get_first_child()
            while child is not None:
                found = name_entry(child)
                if found is not None:
                    return found
                child = child.get_next_sibling()
            return None

        library = flow.open_library()
        assert library.get_modal()
        old_id = app.library.project["id"]
        app.title_entries["subtitle"].set_text("Unsaved synthetic draft")
        editor = library.project_details(True)
        name_entry(editor.get_content_area()).set_text("Another project")
        editor.response(panel.Gtk.ResponseType.OK)
        assert app.title_entries["name"].get_text() == "Another project"
        assert not flow.flow["steps"]
        editor = library.project_details(False)
        name_entry(editor.get_content_area()).set_text("Renamed project")
        editor.response(panel.Gtk.ResponseType.OK)
        assert app.library.project["name"] == "Renamed project"
        library.projects.select_row(library.projects.get_row_at_index(0))
        assert app.library.project["id"] == old_id
        assert app.title_entries["subtitle"].get_text() == "Unsaved synthetic draft"
        assert len(flow.flow["steps"]) == 2
        editor = workflow_details(flow, library, flow.flow["id"], duplicate=True)
        name_entry(editor.get_content_area()).set_text("Copy")
        editor.response(panel.Gtk.ResponseType.OK)
        assert flow.flow["name"] == "Copy"
        assert flow.flow["steps"][0]["transition"] == "fade"
        library.confirm_remove(False).response(panel.Gtk.ResponseType.CANCEL)
        assert len(app.library.project["workflows"]) == 2
        library.confirm_remove(False).response(panel.Gtk.ResponseType.OK)
        assert len(app.library.project["workflows"]) == 1
        library.projects.select_row(library.projects.get_row_at_index(1))
        library.confirm_remove(True).response(panel.Gtk.ResponseType.OK)
        assert app.library.project["id"] == old_id
        assert not library.project_actions[-1].get_sensitive()
        assert len(flow.flow["steps"]) == 2
        library.close()
        flow.start(False)
        assert flow.player.state == "running" and flow.timer_window.get_visible()
        timer = flow.timer_window
        assert timer.clock.get_label() == "00:01"
        assert timer.current_name.get_label() == "Intro"
        assert timer.next_name.get_label() == flow.scene_title("Detail")
        assert timer.signal.get_visible_child_name() == "green"
        assert not timer.note.get_visible()
        settings = timer.open_settings()
        assert settings.get_modal()
        settings.values["show_note"].set_active(True)
        settings.cancel.emit("clicked")
        assert not flow.timer_options.show_note
        settings = timer.open_settings()
        settings.size.set_selected(1)
        settings.values["show_note"].set_active(True)
        settings.apply.emit("clicked")
        assert timer.has_css_class("presenter-large")
        assert timer.note.get_visible() and timer.note.get_label() == "Synthetic cue"
        assert type(flow.timer_options).load() == flow.timer_options
        for color, seconds in (("green", 3), ("amber", 2), ("red", 1)):
            flow.show_record_cue(color, seconds)
            assert timer.signal.get_visible_child_name() == color
            assert timer.clock.get_label() == f"00:0{seconds}"
        flow.clear_record_cue()
        flow.toggle_pause()
        assert flow.player.state == "paused"
        flow.toggle_pause()
        flow.next_step()
        assert flow.player.index == 1
        flow.stop()
        flow.start_free()
        assert flow.player.mode == "timer"
        flow.stop()
        if "--offline" not in sys.argv:
            flow.start(True)
            GLib.timeout_add(50, guarded(check_live))
            return GLib.SOURCE_REMOVE
        return success()

    def check_live():
        flow = app.workflow
        if flow.player.state != "running":
            return GLib.SOURCE_CONTINUE
        if flow.player.index == 0:
            # Advance on the confirmed scene deadline through the real GTK timer.
            return GLib.SOURCE_CONTINUE
        assert [scene for kind, scene in app.obs.calls if kind == "workflow"] == ["Intro", "Detail"]
        app.manual_scene("Privacy")
        assert flow.player.state == "stopped"
        return success()

    def success():
        result["completed"] = True
        app.quit()
        return GLib.SOURCE_REMOVE

    def capture(theme):
        if "--screenshots" not in sys.argv:
            return
        if not shutil.which("ffmpeg"):
            print("Screenshot skipped: ffmpeg is not installed")
            return
        if "--offline" in sys.argv:
            theme = "offline-" + theme
        # DISPLAY belongs to this private Xvfb fixture, never the owner's desktop.
        subprocess.run(
            [
                "ffmpeg",
                "-loglevel",
                "error",
                "-y",
                "-f",
                "x11grab",
                "-draw_mouse",
                "0",
                "-video_size",
                f"{app.window.get_surface().get_width()}x{app.window.get_surface().get_height()}",
                "-i",
                os.environ["DISPLAY"],
                "-frames:v",
                "1",
                f"/tmp/umberto-lab-{theme}.png",
            ],
            check=True,
            timeout=5,
        )

    def check_system_dark():
        assert app.effective_theme == "dark"
        return finish()

    def check_system_light():
        assert app.effective_theme == "light"
        assert panel.load_theme() == "system"
        app.system_settings.set_string("color-scheme", "prefer-dark")
        GLib.timeout_add(150, guarded(check_system_dark))
        return GLib.SOURCE_REMOVE

    def check_dark():
        assert app.effective_theme == "dark"
        assert panel.load_theme() == "dark"
        assert panel.Gtk.Settings.get_default().get_property("gtk-application-prefer-dark-theme")
        assert app.record_button.get_sensitive() == ("--offline" not in sys.argv)
        capture("dark")
        if app.system_settings:
            app.theme_selector.set_selected(2)
            app.system_settings.set_string("color-scheme", "prefer-light")
            GLib.timeout_add(150, guarded(check_system_light))
        else:
            finish()
        return GLib.SOURCE_REMOVE

    def check_light():
        assert app.effective_theme == "light"
        assert not panel.Gtk.Settings.get_default().get_property(
            "gtk-application-prefer-dark-theme"
        )
        assert isinstance(app.window.get_titlebar(), panel.Gtk.HeaderBar)
        capture("light")
        app.theme_selector.set_selected(1)
        if app.system_settings:
            # A manual preference must survive a change of the desktop setting.
            app.system_settings.set_string("color-scheme", "prefer-light")
        GLib.timeout_add(350, guarded(check_dark))
        return GLib.SOURCE_REMOVE

    def check():
        if "--offline" in sys.argv:
            if not app.obs_status.get_label().startswith("Waiting for OBS"):
                return GLib.SOURCE_CONTINUE
        elif app.screen_item is None:
            return GLib.SOURCE_CONTINUE
        if "--screenshots" in sys.argv:
            app.window.set_default_size(1040, 1040)
        icon_theme = panel.Gtk.IconTheme.get_for_display(panel.Gdk.Display.get_default())
        for _, icon, _, _ in panel.SCENES:
            assert icon_theme.has_icon(icon), icon
        assert set(app.scene_buttons) == set(panel.SCENE_COLORS)
        if "--offline" not in sys.argv:
            assert app.screen_box.get_sensitive()
            assert not app.screen_controls[1].get_sensitive()
            for slider, value in zip(app.screen_controls, (2, 75, -50), strict=False):
                slider.set_value(value)
            toggle, slider = app.vignette_controls["screen"]
            slider.set_value(43)
            toggle.set_active(False)
        GLib.timeout_add(700, guarded(check_light))
        return GLib.SOURCE_REMOVE

    def timed_out():
        raise TimeoutError("GUI smoke test did not complete within 8 seconds")

    GLib.timeout_add(100, guarded(check))
    GLib.timeout_add_seconds(8, guarded(timed_out))
    try:
        app.run([])
    finally:
        sys.excepthook = previous_hook
        app.worker.shutdown(wait=True, cancel_futures=True)
    if result["failed"] or not result["completed"]:
        print("FAIL: isolated GTK smoke test", file=sys.stderr)
        return 1
    print(
        "PASS: isolated GTK controls, appearance, workflows, project selection and detached timer"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(run_child() if "--child" in sys.argv else run_isolated())
