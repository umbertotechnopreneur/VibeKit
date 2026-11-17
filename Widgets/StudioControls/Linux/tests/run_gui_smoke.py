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

        def status(self):
            if "--offline" in sys.argv:
                raise panel.OBSConnectionError("Test OBS: intentionally unavailable")
            return {
                "currentProgramSceneName": "Software Demo",
                "outputActive": False,
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
        assert app.tabs.get_n_pages() == 3
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
        "PASS: isolated GTK controls, Light/Dark/System appearance, preference persistence and native header"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(run_child() if "--child" in sys.argv else run_isolated())
