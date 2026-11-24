#!/usr/bin/python3
"""Native control desk for OBS scenes, recording, camera framing and project cards."""

import json
import subprocess
from concurrent.futures import CancelledError, ThreadPoolExecutor
from threading import Event

from obs_remote import OBS, OBSConnectionError, RecordCountdown
from screen_framing import VIGNETTES
from lab_titles import load_config, atomic_bytes, prepare_cards, CONFIG, FIELD_LIMITS
from camera_controls import (
    CONTROLS,
    load_presets,
    save_presets,
    set_values,
    PRESETS,
    snapshot,
    set_one,
)
from panel_config import load_screen_presets
from localization import LANGUAGES, load_language, save_language
from studio_paths import user_data_home, working_assets
from workflows import WorkflowLibrary, LEGACY_PROJECT_ID
from panel_theme import SCENE_COLORS, THEMES, load_theme, save_theme, resolve_theme, theme_css
from diagnostics import configure_diagnostics, export_diagnostics, get_logger
from memory_watchdog import start_watchdog

import gi

gi.require_version("Gtk", "4.0")
gi.require_version("GdkX11", "4.0")
from gi.repository import Gio, GLib, Gtk, Gdk, Pango  # noqa: E402 — select GI versions before importing
from window_placement import move_to_side_monitor  # noqa: E402 — imports selected GI types

from workflow_panel import WorkflowPanel  # noqa: E402
from gtk_localization import PanelLocalization  # noqa: E402 — GTK versions selected above

logger = get_logger(__name__)

SCENES = (
    (
        "Test card",
        "video-display-symbolic",
        "Test card · CRT",
        "Retro standby with the video title",
    ),
    ("Intro", "media-playback-start-symbolic", "Intro", "Project title, description and links"),
    ("That's me", "avatar-default-symbolic", "That's me", "You, full screen"),
    ("Software Demo", "computer-symbolic", "Software Demo", "Your screen with you in the corner"),
    ("Screen Only", "video-display-symbolic", "Screen Only", "Full screen, no camera"),
    ("Detail", "zoom-in-symbolic", "Detail", "Screen details and saved framing"),
    (
        "Comparison A-B",
        "view-dual-symbolic",
        "Comparison A/B",
        "Left and right halves of your display",
    ),
    ("Chapter", "view-list-symbolic", "Chapter", "A title card for the next section"),
    (
        "Hardware Demo",
        "applications-engineering-symbolic",
        "Hardware Demo",
        "Your screen, you and the table",
    ),
    ("On the Table", "camera-video-symbolic", "On the Table", "The table with you in the corner"),
    ("Thank you", "starred-symbolic", "Thank you", "You, project links and credits"),
    (
        "Privacy",
        "changes-prevent-symbolic",
        "Pause / privacy",
        "Screen hidden · audio stays as set",
    ),
)
SCENE_SUBTITLES = {
    "Test card": "Retro standby",
    "Intro": "Project introduction",
    "That's me": "Full-screen webcam",
    "Software Demo": "Screen + corner webcam",
    "Screen Only": "Screen, no webcam",
    "Detail": "Screen zoom & presets",
    "Comparison A-B": "Two apps side by side",
    "Chapter": "Next section",
    "Hardware Demo": "Screen, webcam + table",
    "On the Table": "Table + corner webcam",
    "Thank you": "You, links + credits",
    "Privacy": "Video hidden · audio as set",
}


class BrioPanel(Gtk.Application):
    workflow = None
    record_cue = None
    record_cue_timer = None
    library = None

    def __init__(self):
        super().__init__(application_id="biz.jacobi.BrioControls")
        self.localizer = PanelLocalization(load_language())
        self.window = None
        self.controls = []
        self.pending = {}
        self.syncing = False
        self.presets = load_presets()
        self.legacy_assets = working_assets()
        self.obs = OBS(assets_root=self.legacy_assets)
        self.worker = ThreadPoolExecutor(max_workers=1, thread_name_prefix="obs")
        self.polling = False
        self.action_busy = False
        self.recording = False
        self.blur_enabled = False
        self.blur_pending = None
        self.blur_requested = 3
        self.blur_writing = False
        self.blur_syncing = False
        self.scene_buttons = {}
        self.current_scene = None
        self.screen_item = None
        self.visual_syncing = False
        self.visual_pending = {}
        self.visual_timer = None
        self.visual_writing = False
        self.vignette_values = {}
        self.vignette_controls = {}
        self.title_values = load_config()
        self.workflow_directory = user_data_home() / "VibeWare/StudioControls"
        self.library_error = None
        self.card_project_id = None
        self.card_drafts = {}
        try:
            self.library = WorkflowLibrary(
                self.workflow_directory / "workflows.json", self.title_values["name"]
            )
            self.switch_card_project()
        except (ValueError, OSError) as error:
            self.library_error = str(error)
        self.applying_titles = False
        self.paused = False
        self.audio_mutes = {}
        self.audio_buttons = {}
        self.screen_presets = load_screen_presets()
        self.camera_worker = ThreadPoolExecutor(max_workers=1, thread_name_prefix="camera")
        self.camera_polling = False
        self.camera_writing = False
        self.task_errors = {}
        self.closing = False
        self.cancel_render = Event()
        self.renderer = ThreadPoolExecutor(max_workers=1, thread_name_prefix="cards")
        self.futures = set()
        self.timers = []
        self.active_operation = None
        self.prepared_titles = None
        self.prepared_timer = None
        self.blur_available = False
        self.obs_connected = False
        self.last_warnings = ()
        self.theme_preference = load_theme()
        self.system_settings = None
        self.system_theme_handler = None
        self.system_dark_fallback = False

    def do_activate(self):
        if self.window is not None:
            self.window.present()
            return
        self.window = Gtk.ApplicationWindow(application=self, title="Umberto’s Lab · Controls")
        self.window.set_default_size(1040, 820)
        self.setup_theme()
        body = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        for edge in ("top", "bottom", "start", "end"):
            getattr(body, "set_margin_" + edge)(18)
        self.window.set_child(body)
        heading = Gtk.Label(label="Umberto’s Lab", xalign=0)
        heading.add_css_class("title-1")
        body.append(heading)
        body.append(Gtk.Label(label="umbertogiacobbi.biz", xalign=0))
        columns = Gtk.Grid(column_spacing=24)
        tabs = Gtk.Notebook()
        tabs.set_hexpand(True)
        tabs.set_vexpand(True)
        self.tabs = tabs
        body.append(tabs)
        diagnostic_row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=10)
        export = Gtk.Button(label="Export diagnostic logs…")
        export.connect("clicked", self.export_logs)
        diagnostic_row.append(export)
        self.diagnostic_note = Gtk.Label(xalign=0, wrap=True)
        diagnostic_row.append(self.diagnostic_note)
        body.append(diagnostic_row)
        self.add_tab(tabs, columns, "Camera & recording")
        self.build_title_editor(tabs)
        effects_columns = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=24)
        screen_column = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        effect_column = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        screen_column.set_hexpand(True)
        screen_column.set_size_request(350, -1)
        effect_column.set_hexpand(True)
        effect_column.set_size_request(350, -1)
        effects_columns.append(screen_column)
        effects_columns.append(effect_column)
        self.add_tab(tabs, effects_columns, "Screen & vignettes", position=1)
        camera_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        camera_box.set_hexpand(True)
        camera_box.set_size_request(310, -1)
        scenes_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        scenes_box.set_hexpand(True)
        scenes_box.set_size_request(480, -1)
        columns.attach(camera_box, 0, 0, 1, 1)
        columns.attach(scenes_box, 1, 0, 1, 1)
        self.build_camera_controls(camera_box)
        self.build_vignettes(effect_column)
        self.build_recording_controls(scenes_box)
        self.build_screen_controls(screen_column)
        if self.library is None:
            self.add_tab(tabs, Gtk.Label(label=self.library_error, wrap=True), "Workflow")
        else:
            self.workflow = WorkflowPanel(self, self.library, SCENES)
        self.localizer.attach(self.window)
        self.refresh()
        self.poll_obs()
        self.timers.append(GLib.timeout_add_seconds(3, self.refresh))
        self.timers.append(GLib.timeout_add_seconds(2, self.poll_obs))
        self.window.present()
        self.timers.append(GLib.timeout_add(750, self.move))

    def language_changed(self, dropdown, _):
        if self.closing or dropdown.get_selected() >= len(LANGUAGES):
            return
        code = LANGUAGES[dropdown.get_selected()][0]
        self.localizer.select(code)
        if self.workflow:
            self.workflow.refresh()
        try:
            save_language(code)
        except OSError:
            logger.warning("Could not save panel language")
            self.diagnostic_note.set_label(
                "Language changed for this window; the preference could not be saved."
            )

    def setup_theme(self):
        self.window.add_css_class("umberto-lab")
        settings = Gtk.Settings.get_default()
        original_name = settings.get_property("gtk-theme-name") or ""
        self.system_dark_fallback = bool(
            settings.get_property("gtk-application-prefer-dark-theme")
            or original_name.casefold().endswith("-dark")
        )
        schema_source = Gio.SettingsSchemaSource.get_default()
        schema = (
            schema_source.lookup("org.gnome.desktop.interface", True) if schema_source else None
        )
        if schema and schema.has_key("color-scheme"):
            self.system_settings = Gio.Settings.new_full(schema, None, None)
            self.system_theme_handler = self.system_settings.connect(
                "changed::color-scheme", self.system_theme_changed
            )
        self.theme_provider = Gtk.CssProvider()
        Gtk.StyleContext.add_provider_for_display(
            Gdk.Display.get_default(), self.theme_provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION
        )
        # A native client-side header follows the same palette as the controls,
        # including on X11 desktops whose window-manager titlebars use another theme.
        header = Gtk.HeaderBar()
        self.theme_selector = self.localizer.dropdown(["Light", "Dark", "System"])
        self.theme_selector.set_selected(THEMES.index(self.theme_preference))
        self.localizer.accessible(self.theme_selector, "Panel appearance")
        self.theme_selector.set_tooltip_text(
            "Light, Dark or System. Appearance only changes the control panel, not the video."
        )
        self.theme_selector.connect("notify::selected", self.theme_changed)
        self.language_selector = Gtk.DropDown.new_from_strings([label for _, label in LANGUAGES])
        self.language_selector.set_selected(
            [code for code, _ in LANGUAGES].index(self.localizer.language)
        )
        self.language_selector.set_tooltip_text("Language")
        self.localizer.accessible(self.language_selector, "Language")
        self.language_selector.connect("notify::selected", self.language_changed)
        header.pack_end(self.language_selector)
        header.pack_end(self.theme_selector)
        self.window.set_titlebar(header)
        self.apply_theme()

    def apply_theme(self):
        scheme = (
            self.system_settings.get_string("color-scheme") if self.system_settings else "default"
        )
        self.effective_theme = resolve_theme(
            self.theme_preference, scheme, self.system_dark_fallback
        )
        Gtk.Settings.get_default().set_property(
            "gtk-application-prefer-dark-theme", self.effective_theme == "dark"
        )
        self.theme_provider.load_from_data(theme_css(self.effective_theme).encode())
        # Refresh child snapshots too: X11/Cairo can retain individual labels
        # when the native theme and our palette change together.
        widgets = [self.window]
        while widgets:
            widget = widgets.pop()
            widget.queue_draw()
            child = widget.get_first_child()
            while child is not None:
                widgets.append(child)
                child = child.get_next_sibling()

    def theme_changed(self, selector, _property):
        self.theme_preference = THEMES[selector.get_selected()]
        self.apply_theme()
        try:
            save_theme(self.theme_preference)
        except OSError as error:
            logger.exception("Could not save panel appearance")
            self.diagnostic_note.set_label(
                "Appearance changed for this window, but could not save: " + str(error)
            )

    def system_theme_changed(self, _settings, _key):
        if self.theme_preference == "system" and not self.closing:
            self.apply_theme()

    @staticmethod
    def add_tab(tabs, content, label, position=None):
        for edge in ("top", "bottom", "start", "end"):
            getattr(content, "set_margin_" + edge)(18)
        page = Gtk.ScrolledWindow()
        page.set_policy(Gtk.PolicyType.AUTOMATIC, Gtk.PolicyType.AUTOMATIC)
        page.set_child(content)
        if position is None:
            tabs.append_page(page, Gtk.Label(label=label))
        else:
            tabs.insert_page(page, Gtk.Label(label=label), position)

    def build_camera_controls(self, camera_box):
        title = Gtk.Label(label="FRAMING · LOGITECH BRIO", xalign=0)
        title.add_css_class("heading")
        camera_box.append(title)
        for label, cid, divisor, unit, digits in CONTROLS:
            row = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=0)
            row.append(Gtk.Label(label=label, xalign=0))
            slider = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, 0, 1, 1)
            slider.set_digits(digits)
            slider.set_draw_value(True)
            slider.set_value_pos(Gtk.PositionType.RIGHT)
            slider.set_format_value_func(lambda _, v, u=unit, d=digits: f"{v:.{d}f}{u}")
            slider.connect("value-changed", self.queue_change, cid, divisor)
            self.localizer.accessible(slider, label)
            row.append(slider)
            camera_box.append(row)
            self.controls.append((cid, divisor, slider))
        self.reset_button = Gtk.Button(label="↺  Reset framing")
        self.reset_button.set_tooltip_text(
            "Return to 1× and center the camera. Your saved presets are kept."
        )
        self.reset_button.connect("clicked", self.reset)
        camera_box.append(self.reset_button)
        self.preset_buttons = []
        grid = Gtk.Grid(column_spacing=6, row_spacing=4, column_homogeneous=True)
        for col, name in enumerate(PRESETS):
            recall = Gtk.Button(label=name)
            recall.connect("clicked", self.recall_preset, name)
            save = Gtk.Button(label="Save preset")
            save.set_tooltip_text("Save preset · " + name)
            save.connect("clicked", self.save_preset, name)
            grid.attach(recall, col, 0, 1, 1)
            grid.attach(save, col, 1, 1, 1)
            self.preset_buttons.extend([recall, save])
        camera_box.append(grid)
        self.preset_note = Gtk.Label(
            label="Adjust the camera, then click “Save preset”.", xalign=0, wrap=True
        )
        self.preset_note.add_css_class("dim-label")
        camera_box.append(self.preset_note)
        self.status = Gtk.Label(xalign=0, wrap=True)
        self.status.add_css_class("dim-label")
        camera_box.append(self.status)
        self.blur_button = Gtk.Button(label="Background blur · connecting…")
        self.blur_button.set_sensitive(False)
        self.blur_button.connect(
            "clicked", lambda _: self.perform(self.obs.blur, not self.blur_enabled)
        )
        camera_box.append(self.blur_button)
        camera_box.append(Gtk.Label(label="Blur intensity", xalign=0))
        self.blur_slider = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, 1, 20, 1)
        self.blur_slider.set_digits(0)
        self.blur_slider.set_draw_value(True)
        self.blur_slider.set_value_pos(Gtk.PositionType.RIGHT)
        self.blur_slider.set_format_value_func(lambda _, v: f"{v:.0f} / 20")
        self.blur_slider.set_value(3)
        self.blur_slider.set_sensitive(False)
        self.blur_slider.set_tooltip_text(
            "1 = light, 20 = strong. Use Background blur ON/OFF to enable or disable the effect."
        )
        self.localizer.accessible(self.blur_slider, "Blur intensity")
        self.blur_slider.connect("value-changed", self.queue_blur_intensity)
        camera_box.append(self.blur_slider)

    def build_vignettes(self, effect_column):
        vignette_title = Gtk.Label(label="VIGNETTES · independent controls", xalign=0)
        vignette_title.add_css_class("heading")
        effect_column.append(vignette_title)
        for key, (_, _, label) in VIGNETTES.items():
            row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
            toggle = Gtk.ToggleButton(label=label + " · OFF")
            toggle.set_size_request(140, -1)
            toggle.set_tooltip_text(
                "Each vignette stays inside its own video box. OFF keeps the selected intensity."
            )
            toggle.connect("toggled", self.queue_vignette, key)
            intensity = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, 0, 100, 1)
            intensity.set_hexpand(True)
            intensity.set_digits(0)
            intensity.set_draw_value(True)
            intensity.set_value_pos(Gtk.PositionType.RIGHT)
            intensity.set_format_value_func(lambda _, v: f"{v:.0f}%")
            self.localizer.accessible(intensity, label + " vignette intensity")
            intensity.set_tooltip_text(
                "Vignette darkens only the edges. 0% = clear, 100% = strongest."
            )
            intensity.connect("value-changed", self.queue_vignette, key)
            row.append(toggle)
            row.append(intensity)
            effect_column.append(row)
            self.vignette_controls[key] = (toggle, intensity)
            toggle.set_sensitive(False)
            intensity.set_sensitive(False)
        note = Gtk.Label(
            label="Each vignette stays inside its own video box. OFF keeps the selected intensity.",
            xalign=0,
            wrap=True,
        )
        note.add_css_class("dim-label")
        effect_column.append(note)

    def build_recording_controls(self, scenes_box):
        title = Gtk.Label(label="SCENES · choose what to record", xalign=0)
        title.add_css_class("heading")
        scenes_box.append(title)
        scene_grid = Gtk.Grid(column_spacing=10, row_spacing=10, column_homogeneous=True)
        scenes_box.append(scene_grid)
        for index, (name, icon, label, description) in enumerate(SCENES):
            button = Gtk.Button()
            button.add_css_class("scene-button")
            row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=12)
            scene_icon = Gtk.Image.new_from_icon_name(icon)
            scene_icon.set_pixel_size(30)
            scene_icon.add_css_class("scene-" + SCENE_COLORS[name][0])
            row.append(scene_icon)
            labels = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
            labels.set_hexpand(True)
            scene_title = Gtk.Label(label=label, xalign=0)
            scene_title.set_ellipsize(Pango.EllipsizeMode.END)
            scene_title.add_css_class("scene-title")
            labels.append(scene_title)
            detail = Gtk.Label(label=SCENE_SUBTITLES[name], xalign=0)
            detail.set_ellipsize(Pango.EllipsizeMode.END)
            detail.add_css_class("dim-label")
            labels.append(detail)
            row.append(labels)
            button.set_child(row)
            button.set_tooltip_text(label + "\n" + description)
            button.connect("clicked", lambda _, n=name: self.manual_scene(n))
            button.set_sensitive(False)
            scene_grid.attach(button, index % 2, index // 2, 1, 1)
            self.scene_buttons[name] = button
        self.obs_status = Gtk.Label(label="Connecting to OBS…", xalign=0, wrap=True)
        scenes_box.append(self.obs_status)
        self.obs_warning = Gtk.Label(xalign=0, wrap=True)
        self.obs_warning.add_css_class("dim-label")
        scenes_box.append(self.obs_warning)
        self.record_button = Gtk.Button(label="●  Start recording")
        self.record_button.set_sensitive(False)
        self.record_button.connect(
            "clicked", lambda _: self.manual_record()
        )
        scenes_box.append(self.record_button)
        self.pause_button = Gtk.Button(label="Pause recording")
        self.pause_button.set_sensitive(False)
        self.pause_button.connect(
            "clicked", lambda _: self.manual_pause()
        )
        scenes_box.append(self.pause_button)
        audio_row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=6)
        for name, label in [("Mic/Aux", "Microphone"), ("Desktop Audio", "Desktop audio")]:
            button = Gtk.Button(label=label + " · connecting…")
            button.set_hexpand(True)
            button.set_sensitive(False)
            button.connect(
                "clicked",
                lambda _, n=name: self.perform(
                    self.obs.mute_audio, n, not self.audio_mutes.get(n, True)
                ),
            )
            audio_row.append(button)
            self.audio_buttons[name] = button
        scenes_box.append(audio_row)
        self.result_note = Gtk.Label(
            label="Click a scene while recording. The crossfade is automatic.", xalign=0, wrap=True
        )
        self.result_note.add_css_class("dim-label")
        scenes_box.append(self.result_note)

    def build_screen_controls(self, screen_column):
        self.screen_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        self.screen_box.set_sensitive(False)
        screen_column.append(self.screen_box)
        title = Gtk.Label(label="SCREEN · zoom & position", xalign=0)
        title.add_css_class("heading")
        self.screen_box.append(title)
        self.screen_controls = []
        for label, lower, upper, step, initial in (
            ("Screen zoom", 1, 4, 0.01, 1),
            ("Horizontal", -100, 100, 1, 0),
            ("Vertical", -100, 100, 1, 0),
        ):
            self.screen_box.append(Gtk.Label(label=label, xalign=0))
            slider = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, lower, upper, step)
            slider.set_draw_value(True)
            slider.set_value(initial)
            slider.set_digits(2 if label == "Screen zoom" else 0)
            slider.set_value_pos(Gtk.PositionType.RIGHT)
            slider.set_format_value_func(
                (lambda _, v: f"{v:.2f}×")
                if label == "Screen zoom"
                else (lambda _, v: f"{v:+.0f}%")
            )
            self.localizer.accessible(slider, label)
            slider.connect("value-changed", self.queue_screen)
            self.screen_box.append(slider)
            self.screen_controls.append(slider)
        reset = Gtk.Button(label="↺  Reset screen · 1×, centered")
        reset.connect("clicked", self.reset_screen)
        self.screen_box.append(reset)
        presets = Gtk.Grid(column_spacing=6, row_spacing=4, column_homogeneous=True)
        for col, name in enumerate(self.screen_presets):
            recall = Gtk.Button(label=name)
            recall.connect("clicked", self.recall_screen_preset, name)
            save = Gtk.Button(label="Save position")
            save.connect("clicked", self.save_screen_preset, name)
            presets.attach(recall, col, 0, 1, 1)
            presets.attach(save, col, 1, 1, 1)
        self.screen_box.append(presets)
        self.screen_note = Gtk.Label(
            label="Choose a scene with a captured screen.", xalign=0, wrap=True
        )
        self.screen_note.add_css_class("dim-label")
        screen_column.append(self.screen_note)
        self.visual_note = Gtk.Label(xalign=0, wrap=True)
        self.visual_note.add_css_class("dim-label")
        screen_column.append(self.visual_note)

    def queue_blur_intensity(self, slider):
        if self.blur_syncing:
            return
        self.blur_requested = round(slider.get_value())
        if self.blur_pending is not None:
            GLib.source_remove(self.blur_pending)
        self.blur_pending = GLib.timeout_add(250, self.apply_blur_intensity)

    def apply_blur_intensity(self):
        # Coalesce drag events, and wait for other OBS actions instead of dropping the final value.
        if self.action_busy or self.polling or self.visual_writing:
            return GLib.SOURCE_CONTINUE
        self.blur_pending = None
        self.blur_writing = True
        self.perform(self.obs.blur_intensity, self.blur_requested)
        return GLib.SOURCE_REMOVE

    def poll_obs(self):
        if self.closing:
            return GLib.SOURCE_REMOVE
        can_poll = not any(
            (
                self.polling,
                self.action_busy,
                self.blur_pending is not None,
                self.visual_pending,
                self.visual_writing,
                self.prepared_titles,
            )
        )
        if can_poll:
            self.polling = True
            self.submit_task(self.obs.status, (), self.obs_result, operation="poll")
        return GLib.SOURCE_CONTINUE

    def manual_scene(self, name):
        self.cancel_record_cue()
        if self.workflow:
            self.workflow.manual_override()
        self.perform(self.obs.scene, name)

    def manual_record(self):
        if self.record_cue is not None:
            self.cancel_record_cue()
            return
        if self.closing or self.action_busy or not self.obs_connected:
            return
        if self.recording:
            if self.workflow:
                self.workflow.manual_override()
            self.perform(self.obs.record, False)
            return
        if self.workflow is None or not self.workflow.timer_options.lead_in_enabled:
            self.perform(self.obs.record, True)
            return
        cue = RecordCountdown(self.obs.ws, self.current_scene)
        self.record_cue = cue
        self.record_button.set_label("Cancel recording countdown")

        def prepared(target, error, _operation):
            if self.record_cue is not cue:
                return
            if error:
                self.cancel_record_cue()
                self.result_note.set_label(self.localizer.text(str(error)))
                return
            if not cue.begin():
                self.cancel_record_cue()
                return
            self.workflow.show_record_cue("green", 3)
            stages = iter((("amber", 2), ("red", 1)))

            def advance():
                if self.record_cue is not cue:
                    return GLib.SOURCE_REMOVE
                stage = next(stages, None)
                if stage is not None:
                    self.workflow.show_record_cue(*stage)
                    return GLib.SOURCE_CONTINUE
                self.record_cue_timer = None
                if self.closing or self.action_busy or not self.obs_connected:
                    self.cancel_record_cue()
                else:
                    self.action_busy = True
                    self.active_operation = "record"
                    self.set_obs_sensitive(False)
                    self.submit_task(
                        self.obs.record_after_countdown, (cue, target),
                        lambda value, exc, op: self.finish_record_cue(cue, value, exc, op),
                        operation="record",
                    )
                return GLib.SOURCE_REMOVE

            self.record_cue_timer = GLib.timeout_add(1000, advance)

        self.submit_task(self.obs.recording_target, (cue,), prepared, operation="record_preflight")

    def finish_record_cue(self, cue, result, error, operation):
        if self.record_cue is cue:
            self.record_cue = None
            self.workflow.clear_record_cue()
            self.record_button.set_label("●  Start recording")
        self.obs_result(result, error, operation)

    def cancel_record_cue(self):
        cue = self.record_cue
        if cue is None or not cue.cancel():
            return False
        self.record_cue = None
        if self.record_cue_timer is not None:
            GLib.source_remove(self.record_cue_timer)
            self.record_cue_timer = None
        if not self.closing:
            self.workflow.clear_record_cue()
            self.record_button.set_label("●  Start recording")
            self.result_note.set_label("Recording countdown cancelled.")
        return True

    def manual_pause(self):
        self.cancel_record_cue()
        if self.workflow:
            self.workflow.manual_override()
        self.perform(self.obs.pause, not self.paused)

    def perform(self, function, *args, operation=None):
        if self.action_busy or self.closing:
            return False
        self.action_busy = True
        self.active_operation = operation or function.__name__
        self.set_obs_sensitive(False)
        self.submit_task(function, args, self.obs_result, operation=self.active_operation)
        return True

    def submit_task(self, function, args, callback, *, operation, executor=None):
        """Return every task to the GTK loop with its own operation identity."""
        if self.closing:
            return

        def execute():
            if self.closing:
                raise CancelledError()
            return function(*args)

        future = (executor or self.worker).submit(execute)
        self.futures.add(future)

        def finished(task):
            try:
                result, error = task.result(), None
            except CancelledError:
                return
            except Exception as exc:
                signature = (type(exc).__name__, str(exc))
                if (
                    operation not in ("poll", "camera_poll")
                    or self.task_errors.get(operation) != signature
                ):
                    logger.exception("Operation failed: %s", operation)
                self.task_errors[operation] = signature
                result, error = None, exc
            if not self.closing:
                GLib.idle_add(deliver, task, result, error)

        def deliver(task, result, error):
            self.futures.discard(task)
            if not self.closing:
                if error is None:
                    self.task_errors.pop(operation, None)
                callback(result, error, operation)
            return GLib.SOURCE_REMOVE

        future.add_done_callback(finished)

    def set_obs_sensitive(self, enabled):
        for button in self.scene_buttons.values():
            button.set_sensitive(enabled)
        self.record_button.set_sensitive(enabled or self.record_cue is not None)
        self.pause_button.set_sensitive(enabled and self.recording)
        for name, button in self.audio_buttons.items():
            button.set_sensitive(enabled and name in self.audio_mutes)
        self.blur_button.set_sensitive(enabled and self.blur_available)
        self.blur_slider.set_sensitive(enabled and self.blur_available)
        self.screen_box.set_sensitive(enabled and self.screen_item is not None)
        for key, (toggle, slider) in self.vignette_controls.items():
            available = enabled and key in self.vignette_values
            toggle.set_sensitive(available)
            slider.set_sensitive(available)
        self.title_apply_button.set_sensitive(not self.applying_titles and not self.action_busy)

    def obs_result(self, result, error, operation):
        is_action = operation != "poll"
        if is_action:
            self.action_busy = False
            self.active_operation = None
            self.blur_writing = False
        else:
            self.polling = False
        if error:
            self.cancel_record_cue()
            self.visual_pending.clear()
            self.cancel_blur()
            if isinstance(error, OBSConnectionError) or not self.obs_connected:
                self.obs_connected = False
                self.obs_status.set_label(
                    "OBS connection lost · recording state unconfirmed. Check OBS."
                    if self.current_scene is not None
                    else "Waiting for OBS · open OBS with WebSocket enabled."
                )
                self.blur_button.set_label("Background blur · offline")
                self.update_audio_status()
            self.set_obs_sensitive(self.obs_connected and not self.action_busy)
            if is_action:
                self.result_note.set_label("Action could not be confirmed: " + str(error))
            if operation == "apply_titles":
                self.applying_titles = False
                self.title_note.set_label(str(error))
                self.title_apply_button.set_sensitive(not self.action_busy)
            if self.workflow:
                self.workflow.observe({}, error)
            return GLib.SOURCE_REMOVE

        self.obs_connected = result.get("connectionAvailable", True)
        current_scene = result.get("currentProgramSceneName", self.current_scene)
        if self.record_cue is not None and (
            current_scene != self.record_cue.scene or not self.obs_connected
            or not result.get("statusFresh", True) or result.get("outputActive") is not False
            or result.get("outputPaused") is not False
        ):
            self.cancel_record_cue()
        if current_scene != self.current_scene:
            # Item IDs belong to a scene. Never combine a new scene name with
            # the previous scene's item ID while other effects are pending.
            self.visual_pending.pop("screen", None)
            self.screen_item = None
        self.current_scene = current_scene
        self.recording = result["outputActive"]
        self.paused = result.get("outputPaused", False)
        self.audio_mutes = result.get("audioMutes", {})
        self.blur_available = result.get("blurAvailable", False)
        self.blur_enabled = result.get("blurEnabled", False)
        self.update_recording_status(result)
        if self.workflow:
            self.workflow.observe(result)
        self.update_audio_status()
        for name, button in self.scene_buttons.items():
            if name == self.current_scene:
                button.add_css_class("suggested-action")
            else:
                button.remove_css_class("suggested-action")
        if self.blur_pending is None and not self.blur_writing:
            self.blur_syncing = True
            self.blur_slider.set_value(result.get("blurIntensity", 3))
            self.blur_syncing = False
        self.blur_button.set_label(
            "Background blur · " + ("ON" if self.blur_enabled else "OFF")
            if self.blur_available
            else "Background blur · filter unavailable"
        )
        if not self.visual_pending and not self.visual_writing:
            self.sync_visuals(result)
        warnings = tuple(result.get("warnings", ()))
        if warnings != self.last_warnings:
            if warnings:
                logger.warning("OBS capability/status warnings: %s", "; ".join(warnings))
            elif self.last_warnings:
                logger.info("OBS capabilities restored")
            self.last_warnings = warnings
        self.obs_warning.set_label(" · ".join(warnings))
        if result.get("outputPath"):
            self.result_note.set_label("Video saved: " + result["outputPath"])
        elif is_action:
            self.result_note.set_label("Click a scene while recording. The crossfade is automatic.")
        if operation == "apply_titles":
            self.applying_titles = False
            self.title_values = load_config(self.obs.title_config)
            self.logo_entry.set_text(self.title_values["logo"])
            self.title_note.set_label(
                "Project saved and applied to OBS."
                if not warnings
                else "Project applied. Check the OBS warnings below the scene controls."
            )
        self.set_obs_sensitive(self.obs_connected and not self.action_busy)
        return GLib.SOURCE_REMOVE

    def update_recording_status(self, result):
        if self.recording:
            state = ("Ⅱ PAUSED " if self.paused else "● REC ") + result.get("outputTimecode", "")
        else:
            state = "Ready · not recording"
        if not result.get("statusFresh", True):
            state += " · status refresh pending"
        self.obs_status.set_label(state + " · " + (self.current_scene or "Unknown scene"))
        record_label = "■  Stop and save recording" if self.recording else "●  Start recording"
        self.record_button.set_label("Cancel recording countdown" if self.record_cue else record_label)
        self.pause_button.set_label("Resume recording" if self.paused else "Pause recording")

    def update_audio_status(self):
        for name, button in self.audio_buttons.items():
            label = "Microphone" if name == "Mic/Aux" else "Desktop audio"
            muted = self.audio_mutes.get(name)
            state = "unavailable" if muted is None else ("MUTED" if muted else "ON")
            if not self.obs_connected:
                state = "offline"
            button.set_label(label + " · " + state)
            if muted and self.obs_connected:
                button.add_css_class("suggested-action")
            else:
                button.remove_css_class("suggested-action")

    def cancel_blur(self):
        if self.blur_pending is not None:
            GLib.source_remove(self.blur_pending)
            self.blur_pending = None

    def build_title_editor(self, tabs):
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        self.add_tab(tabs, box, "Project & cards")
        heading = Gtk.Label(label="PROJECT · intro & thank you", xalign=0)
        heading.add_css_class("heading")
        box.append(heading)
        self.title_entries = {}
        form = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=24)
        left = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        right = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        for column in (left, right):
            column.set_hexpand(True)
            column.set_size_request(350, -1)
            form.append(column)
        box.append(form)
        grid = Gtk.Grid(column_spacing=14, row_spacing=9)
        section_grid = Gtk.Grid(column_spacing=14, row_spacing=9)
        left.append(grid)
        right.append(section_grid)
        fields = (
            ("name", "Project name", FIELD_LIMITS["name"]),
            ("eyebrow", "Eyebrow", 70),
            ("subtitle", "Subtitle", 180),
            ("chapter", "Chapter title", 100),
            ("chapter_description", "Chapter subtitle", 180),
            ("compare_a", "Comparison · left", 60),
            ("compare_b", "Comparison · right", 60),
        )
        for index, (key, label, limit) in enumerate(fields):
            target = grid if index < 3 else section_grid
            row = index if index < 3 else index - 3
            target.attach(Gtk.Label(label=label, xalign=0), 0, row, 1, 1)
            entry = Gtk.Entry()
            entry.set_hexpand(True)
            entry.set_max_length(limit)
            entry.set_text(self.title_values[key])
            target.attach(entry, 1, row, 1, 1)
            self.title_entries[key] = entry
        left.append(Gtk.Label(label="Description · up to 600 characters", xalign=0))
        self.description = Gtk.TextView()
        self.description.set_wrap_mode(Gtk.WrapMode.WORD_CHAR)
        self.description.get_buffer().set_text(self.title_values["description"])
        scroll = Gtk.ScrolledWindow()
        scroll.set_min_content_height(90)
        scroll.set_child(self.description)
        scroll.set_has_frame(True)
        left.append(scroll)
        self.link_entries = []
        for index, value in enumerate(self.title_values["links"]):
            entry = Gtk.Entry()
            entry.set_max_length(150)
            entry.set_text(value)
            entry.set_placeholder_text(
                "Link " + str(index + 1) + " · project website, repository, documentation…"
            )
            self.localizer.accessible(entry, "Project link " + str(index + 1))
            right.append(entry)
            self.link_entries.append(entry)
        row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        self.logo_entry = Gtk.Entry()
        self.logo_entry.set_hexpand(True)
        self.logo_entry.set_placeholder_text("Optional project logo · PNG, JPEG or WebP")
        self.logo_entry.set_text(self.title_values["logo"])
        row.append(self.logo_entry)
        browse = Gtk.Button(label="Choose logo…")
        browse.connect("clicked", self.choose_logo)
        row.append(browse)
        clear = Gtk.Button(label="Clear")
        clear.connect("clicked", lambda _: self.logo_entry.set_text(""))
        row.append(clear)
        left.append(row)
        self.vibe_check = Gtk.CheckButton(label="VibeWare project")
        self.vibe_check.set_tooltip_text(
            "Show VibeWare branding on the intro, a logo and link on Thank you, and a small logo in the other scenes."
        )
        self.vibe_check.set_active(self.title_values["vibeware"])
        left.append(self.vibe_check)
        row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=12)
        self.sound_check = Gtk.CheckButton(label="Intro sound")
        self.sound_check.set_active(self.title_values["sound_enabled"])
        row.append(self.sound_check)
        self.sound_slider = Gtk.Scale.new_with_range(Gtk.Orientation.HORIZONTAL, 0, 100, 1)
        self.sound_slider.set_hexpand(True)
        self.sound_slider.set_digits(0)
        self.sound_slider.set_draw_value(True)
        self.sound_slider.set_value_pos(Gtk.PositionType.RIGHT)
        self.sound_slider.set_format_value_func(lambda _, v: f"{v:.0f}%")
        self.sound_slider.set_value(self.title_values["sound_volume"])
        self.localizer.accessible(self.sound_slider, "Intro sound volume")
        row.append(self.sound_slider)
        right.append(row)
        self.title_apply_button = Gtk.Button(label="Apply project to scenes")
        self.title_apply_button.add_css_class("suggested-action")
        self.title_apply_button.connect("clicked", self.apply_titles)
        box.append(self.title_apply_button)
        self.title_note = Gtk.Label(
            label="Open OBS, then Apply. The local logo is copied beside the panel for portability. Scene changes remain manual.",
            xalign=0,
            wrap=True,
        )
        self.title_note.add_css_class("dim-label")
        box.append(self.title_note)

    def choose_logo(self, _):
        dialog = Gtk.FileChooserNative.new(
            self.localizer.text("Choose project logo"),
            self.window,
            Gtk.FileChooserAction.OPEN,
            self.localizer.text("Choose"),
            self.localizer.text("Cancel"),
        )
        images = Gtk.FileFilter()
        images.set_name("Project logos · PNG, JPEG, WebP")
        for mime in ["image/png", "image/jpeg", "image/webp"]:
            images.add_mime_type(mime)
        dialog.add_filter(images)

        def selected(chooser, response):
            if response == Gtk.ResponseType.ACCEPT:
                selected = chooser.get_file()
                if selected and selected.get_path():
                    self.logo_entry.set_text(selected.get_path())
            chooser.destroy()

        dialog.connect("response", selected)
        dialog.show()

    def switch_card_project(self):
        if self.card_project_id and hasattr(self, "title_entries"):
            self.card_drafts[self.card_project_id] = self.read_card_editor()
        project = self.library.project
        self.card_project_id = project["id"]
        if self.card_project_id == LEGACY_PROJECT_ID:
            root, config = self.legacy_assets, CONFIG
        else:
            root = self.workflow_directory / "projects" / self.card_project_id
            config = root / "cards.json"
        self.obs.select_card_project(root, config)
        values = self.card_drafts.get(self.card_project_id)
        if values is None:
            values = load_config(config)
            if not config.exists():
                values["name"] = project["name"]
        self.title_values = values
        if hasattr(self, "title_entries"):
            for key, entry in self.title_entries.items():
                entry.set_text(values[key])
            self.description.get_buffer().set_text(values["description"])
            for entry, link in zip(self.link_entries, values["links"], strict=True):
                entry.set_text(link)
            self.logo_entry.set_text(values["logo"])
            self.vibe_check.set_active(values["vibeware"])
            self.sound_check.set_active(values["sound_enabled"])
            self.sound_slider.set_value(values["sound_volume"])
            self.title_note.set_label("Apply the project to update the cards in OBS.")

    def read_card_editor(self):
        values = {key: entry.get_text() for key, entry in self.title_entries.items()}
        buffer = self.description.get_buffer()
        values["description"] = buffer.get_text(
            buffer.get_start_iter(), buffer.get_end_iter(), False
        )
        values.update(
            links=[entry.get_text() for entry in self.link_entries],
            logo=self.logo_entry.get_text(),
            vibeware=self.vibe_check.get_active(),
            sound_enabled=self.sound_check.get_active(),
            sound_volume=self.sound_slider.get_value(),
        )
        return values

    def apply_titles(self, _):
        if self.applying_titles or self.closing:
            return
        values = self.read_card_editor()
        if self.workflow:
            self.workflow.manual_override()
        self.applying_titles = True
        self.title_apply_button.set_sensitive(False)
        self.title_note.set_label("Preparing project cards… Recording controls remain available.")

        def render():
            return prepare_cards(
                values,
                root=self.obs.assets_root,
                config_path=self.obs.title_config,
                cancel_event=self.cancel_render,
            )

        self.submit_task(
            render, (), self.titles_rendered, operation="render_titles", executor=self.renderer
        )

    def titles_rendered(self, result, error, _operation):
        if error:
            self.applying_titles = False
            self.title_note.set_label("Project was not saved: " + str(error))
            self.title_apply_button.set_sensitive(not self.action_busy)
            return
        self.prepared_titles = result
        self.title_note.set_label("Project saved locally. Applying to OBS…")
        self.apply_prepared_titles()

    def apply_prepared_titles(self):
        if self.closing:
            self.prepared_timer = None
            return GLib.SOURCE_REMOVE
        if self.action_busy or self.polling or self.visual_writing:
            if self.prepared_timer is None:
                self.prepared_timer = GLib.timeout_add(60, self.apply_prepared_titles)
            return GLib.SOURCE_CONTINUE
        self.prepared_timer = None
        values, self.prepared_titles = self.prepared_titles, None
        if values is None:
            return GLib.SOURCE_REMOVE
        self.perform(lambda: self.obs.apply_titles(values, prepared=True), operation="apply_titles")
        return GLib.SOURCE_REMOVE

    def sync_visuals(self, result):
        self.visual_syncing = True
        try:
            screen = result.get("screen")
            self.screen_item = screen["itemId"] if screen else None
            self.screen_box.set_sensitive(screen is not None and not self.action_busy)
            if screen:
                for slider, value in zip(
                    self.screen_controls, (screen["zoom"], screen["x"], screen["y"]), strict=False
                ):
                    slider.set_value(value)
                self.update_pan_sensitive()
                self.screen_note.set_label(
                    "Zoom in to move horizontally or vertically. · " + self.current_scene
                )
            else:
                self.screen_note.set_label(
                    "Choose Software Demo, Screen Only, Detail or Hardware Demo. Comparison uses the two display halves."
                )
            self.vignette_values = result.get("vignettes", {})
            for key, (toggle, slider) in self.vignette_controls.items():
                state = self.vignette_values.get(key)
                toggle.set_sensitive(state is not None and not self.action_busy)
                slider.set_sensitive(state is not None and not self.action_busy)
                if state:
                    toggle.set_active(state["enabled"])
                    slider.set_value(state["strength"])
                    toggle.set_label(
                        VIGNETTES[key][2] + " · " + ("ON" if state["enabled"] else "OFF")
                    )
        finally:
            self.visual_syncing = False

    def update_pan_sensitive(self):
        zoomed = self.screen_controls[0].get_value() > 1.001
        for slider in self.screen_controls[1:]:
            slider.set_sensitive(zoomed)

    def queue_screen(self, _):
        if self.visual_syncing or self.screen_item is None:
            return
        self.update_pan_sensitive()
        values = tuple(slider.get_value() for slider in self.screen_controls)
        self.queue_visual(
            "screen", self.obs.frame_screen, self.current_scene, self.screen_item, *values
        )

    def reset_screen(self, _):
        self.visual_syncing = True
        for slider, value in zip(self.screen_controls, (1, 0, 0), strict=False):
            slider.set_value(value)
        self.visual_syncing = False
        self.queue_screen(None)

    def recall_screen_preset(self, _, name):
        self.visual_syncing = True
        for slider, value in zip(self.screen_controls, self.screen_presets[name], strict=False):
            slider.set_value(value)
        self.visual_syncing = False
        self.queue_screen(None)

    def save_screen_preset(self, _, name):
        updated = dict(self.screen_presets)
        updated[name] = [slider.get_value() for slider in self.screen_controls]
        try:
            atomic_bytes(
                CONFIG.parent / "screen-presets.json",
                (json.dumps(updated, indent=2) + "\n").encode(),
            )
            self.screen_presets = updated
            self.visual_note.set_label("Screen position saved: " + name)
        except OSError as exc:
            logger.exception("Screen preset save failed")
            self.visual_note.set_label("Could not save screen position: " + str(exc))

    def queue_vignette(self, _, key):
        if self.visual_syncing:
            return
        toggle, slider = self.vignette_controls[key]
        enabled, strength = toggle.get_active(), round(slider.get_value())
        toggle.set_label(VIGNETTES[key][2] + " · " + ("ON" if enabled else "OFF"))
        self.queue_visual("vignette-" + key, self.obs.vignette, key, enabled, strength)

    def queue_visual(self, key, fn, *args):
        # One latest value per control group: dragging cannot grow the worker queue.
        self.visual_pending[key] = (fn, args)
        if self.visual_timer is None:
            self.visual_timer = GLib.timeout_add(60, self.apply_visual)

    def apply_visual(self):
        if self.closing:
            return GLib.SOURCE_REMOVE
        if self.action_busy or self.polling or self.visual_writing:
            return GLib.SOURCE_CONTINUE
        if not self.visual_pending:
            self.visual_timer = None
            return GLib.SOURCE_REMOVE
        key = next(iter(self.visual_pending))
        function, args = self.visual_pending.pop(key)
        self.visual_writing = True
        self.submit_task(function, args, self.visual_result, operation=key)
        return GLib.SOURCE_CONTINUE

    def visual_result(self, _result, error, _operation):
        self.visual_writing = False
        if error:
            if self.workflow:
                self.workflow.observe({}, error)
            self.visual_pending.clear()
            if isinstance(error, OBSConnectionError):
                self.obs_connected = False
            self.set_obs_sensitive(self.obs_connected and not self.action_busy)
            self.visual_note.set_label("Adjustment failed: " + str(error))
        else:
            self.visual_note.set_label(
                "Adjustments apply to the recorded video. Reset screen returns to the full view."
            )

    def refresh(self):
        if self.closing:
            return GLib.SOURCE_REMOVE
        if self.pending or self.camera_polling or self.camera_writing:
            return GLib.SOURCE_CONTINUE
        self.camera_polling = True
        self.submit_task(
            snapshot, (), self.camera_result, operation="camera_poll", executor=self.camera_worker
        )
        return GLib.SOURCE_CONTINUE

    def camera_result(self, result, error, operation):
        if operation == "camera_poll":
            self.camera_polling = False
        else:
            self.camera_writing = False
            if not error:
                readings, saved_presets, message = result
                if saved_presets is not None:
                    self.presets = saved_presets
                self.preset_note.set_label(message)
                result = readings
            else:
                self.preset_note.set_label("Camera operation failed: " + str(error))
        self.syncing = True
        try:
            if error:
                self.status.set_label(str(error))
            else:
                for identifier, divisor, slider in self.controls:
                    lower, upper, step, value = result[identifier]
                    slider.set_range(lower / divisor, upper / divisor)
                    slider.set_increments(step / divisor, 10 * step / divisor)
                    slider.set_value(value / divisor)
                self.status.set_label("1× = full framing. Zoom in to use pan and tilt.")
            enabled = error is None and not self.camera_writing
            for _, _, slider in self.controls:
                slider.set_sensitive(enabled)
            for button in self.preset_buttons + [self.reset_button]:
                button.set_sensitive(enabled)
        finally:
            self.syncing = False

    def perform_camera(self, function, args, message, *, save_name=None):
        if self.camera_writing or self.closing:
            return False
        self.camera_writing = True
        for _, _, slider in self.controls:
            slider.set_sensitive(False)
        for button in self.preset_buttons + [self.reset_button]:
            button.set_sensitive(False)

        def execute():
            values = function(*args)
            updated = None
            if save_name is not None:
                updated = dict(self.presets)
                updated[save_name] = values
                save_presets(updated)
            return snapshot(), updated, message

        self.submit_task(
            execute, (), self.camera_result, operation="camera_action", executor=self.camera_worker
        )
        return True

    def cancel_pending(self):
        for source in self.pending.values():
            GLib.source_remove(source)
        self.pending.clear()

    def queue_change(self, slider, cid, divisor):
        if self.syncing:
            return
        if cid in self.pending:
            GLib.source_remove(self.pending[cid])
        self.pending[cid] = GLib.timeout_add(
            100, self.apply_value, cid, round(slider.get_value() * divisor)
        )

    def apply_value(self, cid, value):
        if self.camera_writing or self.camera_polling:
            return GLib.SOURCE_CONTINUE
        self.pending.pop(cid, None)
        self.perform_camera(set_one, (cid, value), "Camera framing adjusted.")
        return GLib.SOURCE_REMOVE

    def save_preset(self, _, name):
        self.cancel_pending()
        values = {
            str(cid): round(slider.get_value() * divisor) for cid, divisor, slider in self.controls
        }
        self.perform_camera(set_values, (values,), "Preset saved: " + name, save_name=name)

    def recall_preset(self, _, name):
        self.cancel_pending()
        self.perform_camera(set_values, (self.presets[name],), "Preset recalled: " + name)

    def reset(self, _):
        self.cancel_pending()
        values = {
            str(control[1]): 100 if index == 0 else 0 for index, control in enumerate(CONTROLS)
        }
        self.perform_camera(set_values, (values,), "Framing reset: 1×, centered.")

    def move(self):
        try:
            move_to_side_monitor(self.window)
        except (OSError, subprocess.SubprocessError):
            logger.debug("Optional monitor placement unavailable", exc_info=True)
        return GLib.SOURCE_REMOVE

    def export_logs(self, _):
        chooser = Gtk.FileChooserNative.new(
            self.localizer.text("Export diagnostic logs"),
            self.window,
            Gtk.FileChooserAction.SAVE,
            self.localizer.text("Export"),
            self.localizer.text("Cancel"),
        )
        chooser.set_current_name("umberto-lab-diagnostics.zip")

        def selected(dialog, response):
            if response == Gtk.ResponseType.ACCEPT:
                destination = dialog.get_file()
                if destination and destination.get_path():
                    self.submit_task(
                        export_diagnostics,
                        (destination.get_path(),),
                        self.logs_exported,
                        operation="export_diagnostics",
                        executor=self.renderer,
                    )
            dialog.destroy()

        chooser.connect("response", selected)
        chooser.show()

    def logs_exported(self, _result, error, _operation):
        self.diagnostic_note.set_label(
            "Could not export logs: " + str(error) if error else "Diagnostic logs exported."
        )

    def do_shutdown(self):
        # Closing the panel never changes recording or resumes any device/action.
        self.closing = True
        self.cancel_record_cue()
        if self.workflow:
            self.workflow.close()
        if self.system_settings and self.system_theme_handler is not None:
            self.system_settings.disconnect(self.system_theme_handler)
            self.system_theme_handler = None
        self.cancel_render.set()
        self.cancel_pending()
        self.cancel_blur()
        self.visual_pending.clear()
        self.prepared_titles = None
        for timer in self.timers + [self.visual_timer, self.prepared_timer]:
            if timer and GLib.MainContext.default().find_source_by_id(timer):
                GLib.source_remove(timer)
        for future in self.futures:
            future.cancel()
        self.worker.submit(self.obs.close)
        self.worker.shutdown(wait=False)
        self.renderer.shutdown(wait=False, cancel_futures=True)
        self.camera_worker.shutdown(wait=False, cancel_futures=True)
        if hasattr(self, "localizer"):
            self.localizer.close()
        Gtk.Application.do_shutdown(self)


if __name__ == "__main__":
    configure_diagnostics()
    panel = BrioPanel()

    def close_for_memory():
        try:
            if panel.get_is_registered():
                notice = Gio.Notification.new("Studio Controls closed")
                notice.set_body("The panel exceeded its memory limit. See the local diagnostic log.")
                panel.send_notification("memory-limit", notice)
        finally:
            panel.quit()
        return GLib.SOURCE_REMOVE

    watchdog_stop = start_watchdog(get_logger("memory"), lambda: GLib.idle_add(close_for_memory))
    try:
        panel.run()
    finally:
        watchdog_stop.set()
