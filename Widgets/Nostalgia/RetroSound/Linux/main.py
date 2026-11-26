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
from gi.repository import AyatanaAppIndicator3 as Indicator, GdkPixbuf, Gio, GLib, Gst, Gtk

from disk_activity import DiskActivity

APP_ID = "local.vibeware.HardDriveSound"
ROOT = Path(__file__).resolve().parent
PRODUCT = json.loads((ROOT / "metadata.json").read_text(encoding="utf-8"))
DEFAULT = {"enabled": True, "idle": False, "volume": 55, "quiet": 8,
           "density": 100, "retry": 0, "profile": "", "startup": False}
RANGES = {"volume": (0, 100), "quiet": (0, 120), "density": (0, 150), "retry": (0, 600)}


class Audio:
    def __init__(self, on_error):
        self.error = on_error
        self.lib = ffi.CDLL(str(ROOT / "libhdd-mixer.so"))
        for name, restype, args in (
            ("create", ffi.c_void_p, []), ("destroy", None, [ffi.c_void_p]),
            ("load", ffi.c_int, [ffi.c_void_p, ffi.c_char_p, ffi.c_int]),
            ("configure", None, [ffi.c_void_p, ffi.c_float, ffi.c_int, ffi.c_int]),
            ("frequency", ffi.c_int, [ffi.c_void_p, ffi.c_int, ffi.c_int, ffi.c_int]),
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
        self.profile_loaded = False
        self.pipeline = None
        self.timer = 0
        self.quiet_frames = 0

    def configure(self, settings, enabled):
        self.lib.hdd_configure(self.mixer, settings["volume"] / 100, settings["idle"], enabled)
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
        GLib.set_application_name("Retro Sound")
        super().__init__(application_id=APP_ID, flags=Gio.ApplicationFlags.HANDLES_COMMAND_LINE)
        config = Path(GLib.get_user_config_dir())
        self.config = config / "vibeware" / "hard-drive-sound.json"
        self.autostart = config / "autostart" / (APP_ID + ".desktop")
        self.settings = None
        self.startup_error = None
        self.startup_dialog = None
        try:
            self.settings = self.load()
        except (OSError, ValueError) as error:
            self.startup_error = f"{self.config}\n{error}"
        self.engine = None
        self.window = None
        self.session = None
        self.ending = False
        self.error = ""
        self.close_dialog = None
        self.about_dialog = None
        self.syncing_controls = False
        self.tray_available = False
        self.hidden_to_tray = False

    def load(self):
        """Use defaults only on first launch. Validate the complete file before starting audio."""
        try:
            text = self.config.read_text(encoding="utf-8")
        except FileNotFoundError:
            if self.config.is_symlink():
                raise
            return DEFAULT.copy()
        def unique_fields(pairs):
            fields = dict(pairs)
            if len(fields) != len(pairs):
                raise ValueError("A preference is defined more than once.")
            return fields

        stored = json.loads(text, object_pairs_hook=unique_fields)
        if not isinstance(stored, dict):
            raise ValueError("Expected a JSON object containing the preferences.")
        for key, default in DEFAULT.items():
            if key not in stored:
                raise ValueError(f"Missing preference '{key}'.")
            value = stored[key]
            if type(value) is not type(default):
                raise ValueError(f"Preference '{key}' has the wrong type.")
            if key in RANGES:
                low, high = RANGES[key]
                if not low <= value <= high:
                    raise ValueError(f"Preference '{key}' must be between {low} and {high}.")
        profile = stored["profile"]
        if profile not in ("", "@warm") and ("\0" in profile or not Path(profile).is_absolute()):
            raise ValueError("The sound profile must name a built-in profile or an absolute folder path.")
        unknown = stored.keys() - DEFAULT.keys()
        if unknown:
            raise ValueError("Unknown preferences: " + ", ".join(sorted(unknown)))
        return stored

    def show_startup_error(self):
        """Keep the offending file intact and make the path visible on desktop and autostart launches."""
        if self.startup_dialog is None:
            print(f"Retro Sound could not load its preferences: {self.startup_error}", file=sys.stderr)
            dialog = Gtk.MessageDialog(application=self, message_type=Gtk.MessageType.ERROR,
                                       buttons=Gtk.ButtonsType.CLOSE, text="Retro Sound could not start")
            dialog.format_secondary_text(f"Correct the preferences file and open Retro Sound again.\n\n{self.startup_error}")
            dialog.connect("response", lambda *_: dialog.destroy())
            dialog.connect("destroy", lambda *_: setattr(self, "startup_dialog", None))
            self.startup_dialog = dialog
        self.startup_dialog.present()

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
                self.write(self.autostart, "[Desktop Entry]\nType=Application\nName=Retro Sound\n"
                           "Exec=/usr/bin/vibeware-retro-sound --autostart\n")
            else:
                self.autostart.unlink(missing_ok=True)
        except OSError as error:
            self.show_status(f"Could not save preferences: {error}")

    def do_command_line(self, command):
        if self.startup_error:
            self.show_startup_error()
            return 1
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
        self.indicator = Indicator.Indicator.new(APP_ID, PRODUCT["icon"], Indicator.IndicatorCategory.APPLICATION_STATUS)
        menu = Gtk.Menu()
        for label, action in (("Preferences", self.activate), ("Open", self.activate),
                              (None, None), ("Effect", lambda: self.set("enabled", self.effect_menu.get_active())),
                              ("Play spin-up", lambda: self.play(0)), ("Test seeks", lambda: self.play(2)),
                              ("Play spin-down", lambda: self.play(1)), ("Stop", lambda: self.set("enabled", False)),
                              ("status", None), (None, None), ("Quit", self.quit), (None, None),
                              ("Mini Utilities", self.open_site), ("About", self.about)):
            if label is None:
                menu.append(Gtk.SeparatorMenuItem())
                continue
            if label == "status":
                self.status_menu = Gtk.MenuItem(label="Stopped", sensitive=False)
                menu.append(self.status_menu)
                continue
            item = Gtk.CheckMenuItem(label=label) if label == "Effect" else Gtk.MenuItem(label=label)
            if label == "Effect":
                self.effect_menu = item
                item.set_active(self.settings["enabled"])
            item.connect("toggled" if label == "Effect" else "activate", lambda _item, callback=action: callback())
            menu.append(item)
        menu.show_all()
        self.indicator.set_menu(menu)
        self.indicator.connect("connection-changed", self.tray_connection_changed)
        self.indicator.set_status(Indicator.IndicatorStatus.ACTIVE)
        self.tray_available = self.indicator.get_property("connected")
        self.load_profile(self.settings["profile"])
        self.apply()
        self.update_id = GLib.timeout_add(250, self.update_audio)
        self.register_session()

    def load_profile(self, folder):
        """Keep an already loaded bank playing if the user selects an invalid one."""
        path = ROOT / "warm" if folder == "@warm" else Path(folder) if folder else ROOT / "default"
        result = self.engine.lib.hdd_load(self.engine.mixer, os.fsencode(path), self.settings["retry"])
        if result != 1:
            problem = ("Head retry requires error.wav. Set Head retry to 0 or choose another profile."
                       if result == -1 else
                       "The profile needs valid startup.wav, idle.wav, shutdown.wav and seek1.wav through "
                       "seek4.wav recordings. If present, error.wav must also be valid.")
            if self.engine.profile_loaded:
                self.show_status(problem)
            else:
                self.audio_failed(problem)
            return False
        self.engine.stop()
        self.engine.profile_loaded = True
        self.error = ""
        self.settings["profile"] = str(folder)
        if self.window:
            self.profile.set_text("Built-in warm drive" if folder == "@warm" else Path(folder).name if folder else "Built-in classic drive")
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
        if hasattr(self, "status_menu"):
            self.status_menu.set_label(message)
        if self.window:
            self.status.set_text(message)

    def apply(self, reset=False):
        if reset:
            self.engine.configure(self.settings, False)
        if self.engine.profile_loaded and not self.engine.lib.hdd_frequency(
                self.engine.mixer, self.settings["quiet"], self.settings["density"], self.settings["retry"]):
            self.audio_failed("Head retry requires error.wav in the current sound profile.")
        playing = self.settings["enabled"] and self.engine.profile_loaded and not self.error
        self.engine.configure(self.settings, playing)
        self.syncing_controls = True
        try:
            self.effect_menu.set_active(bool(playing))
            if self.window:
                self.toggle.set_active(bool(playing))
        finally:
            self.syncing_controls = False
        self.engine.ensure()
        self.show_status(self.error or ("Following disk activity" if playing else "Stopped"))

    def set(self, key, value):
        if self.syncing_controls:
            return
        if key == "retry" and self.engine.profile_loaded and not self.engine.lib.hdd_frequency(
                self.engine.mixer, self.settings["quiet"], self.settings["density"], value):
            self.syncing_controls = True
            try:
                self.retry_field.set_value(self.settings[key])
            finally:
                self.syncing_controls = False
            self.show_status("Head retry requires error.wav in the current sound profile.")
            return
        self.settings[key] = value
        # A corrected profile or retry interval can repair a failed startup without restarting the app.
        if not self.engine.profile_loaded and (key == "retry" or (key == "enabled" and value)):
            self.load_profile(self.settings["profile"])
        elif key == "enabled" and value:
            self.error = ""
        self.apply()
        self.save()

    def update_audio(self):
        if not self.settings["enabled"] or self.error:
            return True
        self.engine.lib.hdd_activity(self.engine.mixer, self.activity.sample())
        self.engine.ensure()
        return True

    def play(self, kind):
        if not self.settings["enabled"] or self.error:
            self.set("enabled", True)
        if self.error or not self.engine.profile_loaded:
            return
        if kind == 1:
            self.apply(reset=True)
        self.engine.lib.hdd_trigger(self.engine.mixer, 3 if kind == 1 else kind)
        self.engine.ensure()

    def open_site(self):
        Gio.AppInfo.launch_default_for_uri('https://umbertogiacobbi.biz/mini-utilities/', None)

    def tray_connection_changed(self, _indicator, connected):
        self.tray_available = connected
        if self.close_dialog:
            self.update_close_choices()
        if not connected and self.hidden_to_tray:
            self.activate()

    def update_close_choices(self):
        self.close_dialog.set_response_sensitive(Gtk.ResponseType.APPLY, self.tray_available)
        self.close_dialog.set_default_response(Gtk.ResponseType.APPLY if self.tray_available else Gtk.ResponseType.CANCEL)
        self.close_dialog.format_secondary_text(
            ("Hide the panel to keep the effect running in the top bar." if self.tray_available
             else "The top bar icon is unavailable.") + " Quitting stops the effect.")

    def request_close(self, window, _event):
        """Hide is safe only while the indicator has a connected host."""
        if self.close_dialog:
            self.close_dialog.present()
            return True
        dialog = Gtk.MessageDialog(transient_for=window, modal=True, destroy_with_parent=True,
                                   text="Hide the panel or quit Retro Sound?")
        self.close_dialog = dialog
        dialog.add_buttons("Cancel", Gtk.ResponseType.CANCEL, "Hide to tray", Gtk.ResponseType.APPLY,
                           "Quit", Gtk.ResponseType.CLOSE)
        dialog.get_widget_for_response(Gtk.ResponseType.CLOSE).get_style_context().add_class("destructive-action")
        self.update_close_choices()

        def respond(_dialog, response):
            self.close_dialog = None
            dialog.destroy()
            if response == Gtk.ResponseType.APPLY and self.tray_available:
                self.hidden_to_tray = True
                window.hide()
            elif response == Gtk.ResponseType.CLOSE:
                self.quit()

        dialog.connect("response", respond)
        dialog.show_all()
        return True

    def about(self):
        """Present the same identity, license and links as the extension panels using GTK3."""
        if self.about_dialog:
            self.about_dialog.present()
            return
        content = json.loads((ROOT / "about-content.json").read_text(encoding="utf-8"))
        self.activate()
        dialog = Gtk.Dialog(title="About " + PRODUCT["name"], transient_for=self.window, modal=True,
                            destroy_with_parent=True)
        dialog.set_icon_name(PRODUCT["icon"])
        dialog.set_default_size(640, 650)
        dialog.add_button("Close", Gtk.ResponseType.CLOSE)
        scroll = Gtk.ScrolledWindow(hscrollbar_policy=Gtk.PolicyType.NEVER,
                                    vscrollbar_policy=Gtk.PolicyType.AUTOMATIC)
        dialog.get_content_area().pack_start(scroll, True, True, 0)
        body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=20, margin=24)
        scroll.add(body)
        icon = Gtk.Image.new_from_icon_name(PRODUCT["icon"], Gtk.IconSize.DIALOG)
        icon.set_pixel_size(100)
        body.pack_start(icon, False, False, 0)
        for text in (PRODUCT["name"], "Version " + PRODUCT["version"], PRODUCT["description"],
                     content["warranty"], content["attribution"]):
            label = Gtk.Label(label=text, wrap=True, justify=Gtk.Justification.CENTER, max_width_chars=58)
            label.get_style_context().add_class("monospace")
            if text == PRODUCT["name"]:
                label.get_style_context().add_class("title")
            body.pack_start(label, False, False, 0)
        badge = Gtk.LinkButton(uri=content["manifesto"], halign=Gtk.Align.CENTER, tooltip_text="VibeWare manifesto")
        badge.get_accessible().set_name("VibeWare manifesto")
        if badge.get_child():
            badge.remove(badge.get_child())
        badge.add(Gtk.Image.new_from_pixbuf(GdkPixbuf.Pixbuf.new_from_file_at_scale(
            str(ROOT / "VibeWare.png"), 80, 80, True)))
        body.pack_start(badge, False, False, 0)
        links = Gtk.FlowBox(selection_mode=Gtk.SelectionMode.NONE, max_children_per_line=3, halign=Gtk.Align.CENTER)
        license_name = PRODUCT["license"]
        for name, uri in [(license_name, content["licenses"][license_name]), *content["links"].items()]:
            links.add(Gtk.LinkButton.new_with_label(uri, name))
        body.pack_start(links, False, False, 0)
        self.about_dialog = dialog
        dialog.connect("destroy", lambda *_: setattr(self, "about_dialog", None))
        dialog.connect("response", lambda *_: dialog.destroy())
        dialog.show_all()
        dialog.present()

    def do_activate(self):
        if self.startup_error:
            self.show_startup_error()
            return
        if self.engine is None:
            self.start()
        self.hidden_to_tray = False
        if self.window:
            self.window.present()
            return
        self.window = Gtk.ApplicationWindow(application=self, title="Retro Sound")
        self.window.set_icon_name(PRODUCT["icon"])
        self.window.set_default_size(520, 560)
        self.window.connect("delete-event", self.request_close)
        scroll = Gtk.ScrolledWindow(hscrollbar_policy=Gtk.PolicyType.NEVER,
                                    vscrollbar_policy=Gtk.PolicyType.AUTOMATIC)
        self.window.add(scroll)
        grid = Gtk.Grid(column_spacing=14, row_spacing=10, margin=20)
        scroll.add(grid)
        def heading(title, row):
            label = Gtk.Label(label=title, xalign=0)
            label.get_style_context().add_class('heading')
            grid.attach(label, 0, row, 2, 1)
        heading('Playback', 0)
        self.toggle = Gtk.CheckButton(label="Effect")
        grid.attach(self.toggle, 0, 1, 2, 1)
        self.toggle.set_active(self.settings["enabled"])
        self.toggle.connect("toggled", lambda check: self.set("enabled", check.get_active()))
        for row, key, title in ((2, "startup", "Play spin-up at sign-in and spin-down at sign-out"),
                                (3, "idle", "Quiet spindle sound")):
            check = Gtk.CheckButton(label=title)
            grid.attach(check, 0, row, 2, 1)
            check.set_active(self.settings[key])
            check.connect("toggled", lambda control, name=key: self.set(name, control.get_active()))
        heading('Sound', 4)
        for row, key, label in ((5, "volume", "Volume (%)"), (6, "quiet", "Idle clicks (seconds, 0 off)"),
                                (7, "density", "Seek density (%)"), (8, "retry", "Head retry (seconds, 0 off)")):
            text = Gtk.Label(label=label, xalign=0)
            field = Gtk.SpinButton.new_with_range(*RANGES[key], 1)
            if key == "retry":
                self.retry_field = field
            field.set_value(self.settings[key])
            field.connect("value-changed", lambda control, name=key: self.set(name, control.get_value_as_int()))
            text.set_mnemonic_widget(field)
            grid.attach(text, 0, row, 1, 1)
            grid.attach(field, 1, row, 1, 1)
        heading('Sound profile', 9)
        self.profile = Gtk.Label(xalign=0, label="Built-in warm drive" if self.settings["profile"] == "@warm" else Path(self.settings["profile"]).name or "Built-in classic drive")
        profile_buttons = Gtk.Box(spacing=6)
        for title, callback in (("Choose folder…", self.choose), ("Classic", lambda: self.select("")),
                                ("Warm", lambda: self.select("@warm"))):
            button = Gtk.Button(label=title)
            button.connect("clicked", lambda _button, run=callback: run())
            profile_buttons.add(button)
        grid.attach(self.profile, 0, 10, 1, 1)
        grid.attach(profile_buttons, 1, 10, 1, 1)
        heading('Preview', 11)
        previews = Gtk.Box(spacing=6)
        for title, kind in (("Spin-up", 0), ("Test seeks", 2), ("Spin-down", 1)):
            button = Gtk.Button(label=title)
            button.connect("clicked", lambda _button, sound=kind: self.play(sound))
            previews.add(button)
        grid.attach(previews, 0, 12, 2, 1)
        self.status = Gtk.Label(xalign=0, wrap=True, max_width_chars=55)
        grid.attach(self.status, 0, 13, 2, 1)
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
