#!/usr/bin/python3
# SPDX-License-Identifier: MIT
import ctypes as ffi
import json
import os
from pathlib import Path
import sys

import gi
gi.require_version("Gtk", "3.0")
gi.require_version("AyatanaAppIndicator3", "0.1")
gi.require_version("Gst", "1.0")
from gi.repository import AyatanaAppIndicator3 as Indicator, Gio, GLib, Gst, Gtk

from disk_activity import DiskActivity

APP_ID = "local.vibeware.HardDriveSound"
ROOT = Path(__file__).resolve().parent
DEFAULT = {"enabled": True, "idle": False, "volume": 55, "quiet": 8,
           "density": 100, "retry": 0, "profile": "", "startup": False}
RANGES = {"volume": (0, 100), "quiet": (0, 120), "density": (0, 150), "retry": (0, 600)}


class Audio:
    def __init__(self, on_error):
        self.error = on_error
        self.lib = ffi.CDLL(str(ROOT / "libhdd-mixer.so"))
        for name, restype, args in (
            ("create", ffi.c_void_p, []), ("destroy", None, [ffi.c_void_p]),
            ("load", ffi.c_int, [ffi.c_void_p, ffi.c_char_p]),
            ("configure", None, [ffi.c_void_p, ffi.c_float, ffi.c_int, ffi.c_int]),
            ("frequency", None, [ffi.c_void_p, ffi.c_int, ffi.c_int, ffi.c_int]),
            ("activity", None, [ffi.c_void_p, ffi.c_float]),
            ("trigger", None, [ffi.c_void_p, ffi.c_int]),
            ("pending", ffi.c_int, [ffi.c_void_p]),
            ("render", None, [ffi.c_void_p, ffi.POINTER(ffi.c_int16), ffi.c_size_t]),
        ):
            call = getattr(self.lib, "hdd_" + name)
            call.restype, call.argtypes = restype, args
        self.mixer = self.lib.hdd_create()
        if not self.mixer:
            raise MemoryError("Could not create the audio mixer")
        self.pcm = (ffi.c_int16 * 480)()
        self.pipeline = None
        self.timer = 0
        self.quiet_frames = 0

    def configure(self, settings, enabled):
        self.lib.hdd_configure(self.mixer, settings["volume"] / 100, settings["idle"], enabled)
        self.lib.hdd_frequency(self.mixer, settings["quiet"], settings["density"], settings["retry"])
        if not enabled:
            self.stop()

    def tick(self):
        if not self.pipeline:
            return False
        try:
            active = bool(self.lib.hdd_pending(self.mixer))
            self.quiet_frames = 0 if active else self.quiet_frames + 1
            if self.quiet_frames > 18:
                self.timer = 0
                self.stop()
                return False
            while self.source.get_property("current-level-bytes") < 3 * ffi.sizeof(self.pcm):
                self.lib.hdd_render(self.mixer, self.pcm, len(self.pcm))
                data = Gst.Buffer.new_wrapped(bytes(self.pcm))
                data.pts = self.frames * Gst.SECOND // 24000
                self.frames += len(self.pcm)
                data.duration = 20 * Gst.MSECOND
                result = self.source.emit("push-buffer", data)
                if result != Gst.FlowReturn.OK:
                    raise RuntimeError(f"Audio stream returned {result.value_nick}")
        except (GLib.Error, RuntimeError) as error:
            self.stop()
            self.error(str(error))
            return False
        return True

    def ensure(self):
        if self.pipeline or not self.lib.hdd_pending(self.mixer):
            return
        try:
            self.pipeline = Gst.parse_launch(
                "appsrc name=pcm is-live=true format=time max-bytes=2880 "
                "caps=audio/x-raw,format=S16LE,layout=interleaved,rate=24000,channels=1 "
                "! audioconvert ! audioresample ! pulsesink buffer-time=80000 latency-time=20000")
            self.source = self.pipeline.get_by_name("pcm")
            self.bus = self.pipeline.get_bus()
            self.bus.add_signal_watch()
            self.message = self.bus.connect("message::error", self.failed)
            self.frames = self.quiet_frames = 0
            if self.pipeline.set_state(Gst.State.PLAYING) == Gst.StateChangeReturn.FAILURE:
                raise RuntimeError("Could not open the audio device")
            if self.tick():
                self.timer = GLib.timeout_add(20, self.tick)
        except (GLib.Error, RuntimeError) as error:
            self.stop()
            self.error(str(error))

    def failed(self, _bus, message):
        error, _ = message.parse_error()
        self.stop()
        self.error(error.message)

    def stop(self):
        if self.timer:
            GLib.source_remove(self.timer)
            self.timer = 0
        if self.pipeline:
            self.bus.disconnect(self.message)
            self.bus.remove_signal_watch()
            self.pipeline.set_state(Gst.State.NULL)
            self.pipeline = None


class App(Gtk.Application):
    def __init__(self):
        super().__init__(application_id=APP_ID, flags=Gio.ApplicationFlags.HANDLES_COMMAND_LINE)
        config = Path(GLib.get_user_config_dir())
        self.config = config / "vibeware" / "hard-drive-sound.json"
        self.autostart = config / "autostart" / (APP_ID + ".desktop")
        self.settings = DEFAULT.copy()
        self.load()
        self.engine = None
        self.window = None
        self.session = None
        self.ending = False
        self.error = ""

    def load(self):
        try:
            stored = json.loads(self.config.read_text())
            if not isinstance(stored, dict):
                return
            for key, default in DEFAULT.items():
                value = stored.get(key)
                if type(value) is type(default):
                    self.settings[key] = min(RANGES[key][1], max(RANGES[key][0], value)) if key in RANGES else value
        except (ValueError, OSError):
            pass

    @staticmethod
    def write(path, data):
        path.parent.mkdir(parents=True, exist_ok=True)
        backup = path.with_suffix(".new")
        backup.write_text(data, encoding="utf-8")
        backup.replace(path)

    def save(self):
        try:
            self.write(self.config, json.dumps(self.settings, indent=2))
            if self.settings["startup"]:
                self.write(self.autostart, "[Desktop Entry]\nType=Application\nName=Hard Drive Sound\n"
                           "Exec=/usr/bin/vibeware-hard-drive-sound --autostart\n")
            else:
                self.autostart.unlink(missing_ok=True)
        except OSError as error:
            self.show_status(f"Could not save preferences: {error}")

    def do_command_line(self, command):
        args = set(command.get_arguments()[1:])
        if self.engine is None:
            self.start()
            if "--autostart" in args and self.settings["startup"] and self.settings["enabled"]:
                self.play(0)
        if "--autostart" not in args:
            self.activate()
        return 0

    def start(self):
        self.hold()
        Gst.init(None)
        self.activity = DiskActivity()
        self.engine = Audio(self.audio_failed)
        self.indicator = Indicator.Indicator.new(APP_ID, "drive-harddisk-symbolic", Indicator.IndicatorCategory.APPLICATION_STATUS)
        menu = Gtk.Menu()
        for label, action in (("Preferences", self.activate), ("Play spin-up", lambda: self.play(0)),
                              ("Play spin-down", lambda: self.play(1)), ("Stop", lambda: self.set("enabled", False)),
                              ("Quit", self.quit)):
            item = Gtk.MenuItem(label=label)
            item.connect("activate", lambda _item, callback=action: callback())
            menu.append(item)
        menu.show_all()
        self.indicator.set_menu(menu)
        self.indicator.set_status(Indicator.IndicatorStatus.ACTIVE)
        self.load_profile(self.settings["profile"], fallback=True)
        self.apply()
        self.update_id = GLib.timeout_add(250, self.update_audio)
        self.register_session()

    def load_profile(self, folder, fallback=False):
        path = Path(folder) if folder else ROOT / "default"
        if not self.engine.lib.hdd_load(self.engine.mixer, os.fsencode(path)):
            if not fallback:
                self.show_status("This folder must contain the seven WAV files from a sample profile.")
                return False
            if not self.engine.lib.hdd_load(self.engine.mixer, os.fsencode(ROOT / "default")):
                self.audio_failed("The built-in audio files are unavailable.")
                return False
            folder = ""
        self.engine.stop()
        self.settings["profile"] = str(folder)
        if self.window:
            self.profile.set_text(Path(folder).name if folder else "Built-in vintage drive")
        return True

    def register_session(self):
        try:
            bus = Gio.bus_get_sync(Gio.BusType.SESSION)
            service = "org.gnome.SessionManager"
            reply = bus.call_sync(service, "/org/gnome/SessionManager", service, "RegisterClient",
                                  GLib.Variant("(ss)", (APP_ID, os.environ.get("DESKTOP_AUTOSTART_ID", ""))),
                                  GLib.VariantType.new("(o)"), Gio.DBusCallFlags.NONE, 1200)
            path = reply.unpack()[0]
            self.session = Gio.DBusProxy.new_sync(bus, Gio.DBusProxyFlags.DO_NOT_LOAD_PROPERTIES, None,
                                                 service, path, service + ".ClientPrivate")
            self.session.connect("g-signal", self.on_session)
        except GLib.Error:
            if self.settings["startup"]:
                self.show_status("The desktop does not expose a sign-out audio event.")

    def on_session(self, proxy, _sender, signal, _parameters):
        if signal in ("QueryEndSession", "EndSession"):
            # Acknowledge promptly so audio never holds up sign-out.
            proxy.call("EndSessionResponse", GLib.Variant("(bs)", (True, "")),
                       Gio.DBusCallFlags.NONE, 1000, None, None)
            if signal == "EndSession" and not self.ending and self.settings["startup"] and self.settings["enabled"] and not self.error:
                self.ending = True
                self.engine.lib.hdd_trigger(self.engine.mixer, 1)
                self.engine.ensure()
        elif signal == "CancelEndSession" and self.ending:
            self.ending = False
            self.apply(reset=True)
        elif signal == "Stop":
            self.quit()

    def audio_failed(self, message):
        self.error = message
        self.engine.lib.hdd_configure(self.engine.mixer, 0, 0, False)
        self.show_status(message)

    def show_status(self, message):
        if self.window:
            self.status.set_text(message)

    def apply(self, reset=False):
        if reset:
            self.engine.configure(self.settings, False)
        self.engine.configure(self.settings, self.settings["enabled"] and not self.error)
        self.engine.ensure()
        self.show_status(self.error or ("Following disk activity" if self.settings["enabled"] else "Stopped"))

    def set(self, key, value):
        self.settings[key] = value
        if key == "enabled":
            self.error = ""
        self.apply()
        self.save()
        if self.window and key == "enabled" and self.toggle.get_active() != value:
            self.toggle.set_active(value)

    def update_audio(self):
        if not self.settings["enabled"] or self.error:
            return True
        self.engine.lib.hdd_activity(self.engine.mixer, self.activity.sample())
        self.engine.ensure()
        return True

    def play(self, kind):
        if not self.settings["enabled"] or self.error:
            self.set("enabled", True)
        if kind == 1:
            self.apply(reset=True)
        self.engine.lib.hdd_trigger(self.engine.mixer, 3 if kind == 1 else kind)
        self.engine.ensure()

    def do_activate(self):
        if self.window:
            self.window.present()
            return
        self.window = Gtk.ApplicationWindow(application=self, title="Hard Drive Sound")
        self.window.set_default_size(460, 355)
        self.window.connect("delete-event", lambda window, _event: (window.hide(), True)[1])
        grid = Gtk.Grid(column_spacing=14, row_spacing=12, margin=20)
        self.window.add(grid)
        self.toggle = Gtk.CheckButton(label="Effect")
        grid.attach(self.toggle, 0, 0, 2, 1)
        self.toggle.set_active(self.settings["enabled"])
        self.toggle.connect("toggled", lambda check: self.set("enabled", check.get_active()))
        for row, key, title in ((1, "startup", "Play spin-up at sign-in and spin-down at sign-out"),
                                (2, "idle", "Quiet spindle sound")):
            check = Gtk.CheckButton(label=title)
            grid.attach(check, 0, row, 2, 1)
            check.set_active(self.settings[key])
            check.connect("toggled", lambda control, name=key: self.set(name, control.get_active()))
        for row, key, label in ((3, "volume", "Volume (%)"), (4, "quiet", "Idle clicks (seconds, 0 off)"),
                                (5, "density", "Seek density (%)"), (6, "retry", "Head retry (seconds, 0 off)")):
            text = Gtk.Label(label=label, xalign=0)
            field = Gtk.SpinButton.new_with_range(*RANGES[key], 1)
            field.set_value(self.settings[key])
            field.connect("value-changed", lambda control, name=key: self.set(name, control.get_value_as_int()))
            text.set_mnemonic_widget(field)
            grid.attach(text, 0, row, 1, 1)
            grid.attach(field, 1, row, 1, 1)
        self.profile = Gtk.Label(xalign=0, label=Path(self.settings["profile"]).name or "Built-in vintage drive")
        profile_buttons = Gtk.Box(spacing=6)
        for title, callback in (("Choose folder…", self.choose), ("Built-in", lambda: self.select(""))):
            button = Gtk.Button(label=title)
            button.connect("clicked", lambda _button, run=callback: run())
            profile_buttons.add(button)
        grid.attach(self.profile, 0, 7, 1, 1)
        grid.attach(profile_buttons, 1, 7, 1, 1)
        previews = Gtk.Box(spacing=6)
        for title, kind in (("Spin-up", 0), ("Test seeks", 2), ("Spin-down", 1)):
            button = Gtk.Button(label=title)
            button.connect("clicked", lambda _button, sound=kind: self.play(sound))
            previews.add(button)
        grid.attach(previews, 0, 8, 2, 1)
        self.status = Gtk.Label(xalign=0, wrap=True, max_width_chars=55)
        grid.attach(self.status, 0, 9, 2, 1)
        self.apply()
        self.window.show_all()
        self.window.present()

    def select(self, folder):
        if self.load_profile(folder):
            self.apply()
            self.save()

    def choose(self):
        dialog = Gtk.FileChooserDialog(title="Choose a sound profile folder", parent=self.window,
                                       action=Gtk.FileChooserAction.SELECT_FOLDER)
        dialog.add_buttons("Cancel", Gtk.ResponseType.CANCEL, "Open", Gtk.ResponseType.OK)
        if dialog.run() == Gtk.ResponseType.OK:
            self.select(dialog.get_filename())
        dialog.destroy()

    def do_shutdown(self):
        if self.engine:
            GLib.source_remove(self.update_id)
            self.engine.stop()
            self.engine.lib.hdd_destroy(self.engine.mixer)
        Gtk.Application.do_shutdown(self)


if __name__ == "__main__":
    sys.exit(App().run(sys.argv))
