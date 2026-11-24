"""Native presenter display and modal preferences for the GTK workflow timer."""

import gi

from studio_paths import bundled_assets
from timer_options import TimerOptions
from workflow_widgets import add, box, button, icon, icon_button, margins, text, timecode

gi.require_version("Gtk", "4.0")
gi.require_version("GdkPixbuf", "2.0")
from gi.repository import Gdk, GdkPixbuf, GObject, Gtk, Pango  # noqa: E402


def clock_text(seconds):
    value = timecode(seconds)
    sign = "+" if value.startswith("+") else ""
    minutes, seconds = value.lstrip("+").split(":")
    return f"{sign}{minutes.zfill(2)}:{seconds}"


class SignalLight(Gtk.Stack):
    """GTK crossfades the original signal artwork only when a cue changes."""

    def __init__(self):
        super().__init__(halign=Gtk.Align.CENTER, valign=Gtk.Align.CENTER)
        self.set_transition_type(Gtk.StackTransitionType.CROSSFADE)
        self.set_transition_duration(180)
        folder = bundled_assets() / "TimerSignals"
        for color in ("off", "green", "amber", "red"):
            path = folder / f"{color}.png"
            picture = Gtk.Picture(can_shrink=True, can_target=False)
            if path.is_file():
                pixbuf = GdkPixbuf.Pixbuf.new_from_file_at_scale(str(path), -1, 240, True)
                picture.set_paintable(Gdk.Texture.new_for_pixbuf(pixbuf))
            # Overlay children do not impose the natural size of the source texture.
            layer = Gtk.Overlay(child=Gtk.Box())
            layer.add_overlay(picture)
            if color != "off":
                glow = Gtk.Box(can_target=False)
                glow.add_css_class("presenter-glow-" + color)
                layer.add_overlay(glow)
            self.add_named(layer, color)
        self.set_visible_child_name("off")

    def update(self, color, description):
        if self.get_visible_child_name() != color:
            self.set_visible_child_name(color)
        if self.get_tooltip_text() != description:
            self.set_tooltip_text(description)
            self.update_property([Gtk.AccessibleProperty.LABEL], [description])


def text_field(style):
    label = text(style=style, truncate=True)
    label.set_max_width_chars(1)
    label.set_hexpand(True)
    return label


def caption_label():
    label = text(style="presenter-caption")
    label.set_ellipsize(Pango.EllipsizeMode.END)
    label.set_max_width_chars(20)
    return label


class PresenterTimer(Gtk.Window):
    def __init__(self, flow):
        super().__init__(
            title=flow.tr("Timer"),
            transient_for=flow.panel.window,
            destroy_with_parent=True,
            hide_on_close=True,
        )
        self.flow = flow
        self.settings_window = None
        self.add_css_class("umberto-lab")
        self.add_css_class("presenter")
        self.header = Gtk.HeaderBar()
        self.set_titlebar(self.header)
        self.heading = text(style="heading", truncate=True)
        self.header.set_title_widget(self.heading)
        self.config_button = icon_button(
            "emblem-system-symbolic", flow.tr("Timer settings"), self.open_settings
        )
        self.header.pack_end(self.config_button)

        self.body = box(True, 0)
        self.body.set_valign(Gtk.Align.FILL)
        self.set_child(self.body)
        self.stage = box(True, 0)
        self.stage.add_css_class("presenter-stage")
        self.stage.set_vexpand(True)
        self.body.append(self.stage)

        self.clock_row = box(spacing=18)
        self.clock_row.add_css_class("presenter-clock-row")
        self.clock_row.set_vexpand(True)
        self.stage.append(self.clock_row)
        self.signal = SignalLight()
        self.clock_caption = caption_label()
        self.clock = text("00:00", "presenter-clock")
        self.clock_block = add(box(True, 0), self.clock_caption, self.clock)
        self.clock_block.set_hexpand(True)
        self.clock_block.set_valign(Gtk.Align.CENTER)
        self.total_caption = caption_label()
        self.total = text("00:00", "presenter-total")
        self.total_block = add(box(True, 2), self.total_caption, self.total)
        self.total_block.set_valign(Gtk.Align.CENTER)
        add(self.clock_row, self.signal, self.clock_block, self.total_block)

        self.route = Gtk.Grid(column_spacing=12)
        self.route.add_css_class("presenter-route")
        self.stage.append(self.route)
        self.current_caption = caption_label()
        self.current_name = text_field("presenter-current")
        self.next_caption = caption_label()
        self.next_name = text_field("presenter-next")
        self.current_block = add(box(True, 2), self.current_caption, self.current_name)
        self.next_block = add(box(True, 2), self.next_caption, self.next_name)
        self.arrow = icon("go-next-symbolic", 16)
        self.arrow.add_css_class("dim-label")
        self.route.attach(self.current_block, 0, 0, 1, 1)
        self.route.attach(self.arrow, 1, 0, 1, 1)
        self.route.attach(self.next_block, 2, 0, 1, 1)
        self.note = Gtk.Label(xalign=0, wrap=True, lines=2, ellipsize=Pango.EllipsizeMode.END)
        self.note.add_css_class("presenter-note")
        self.stage.append(self.note)

        footer = box(spacing=10)
        footer.set_margin_top(9)
        self.body.append(footer)
        controls = box(spacing=4)
        self.pause = icon_button(
            "media-playback-pause-symbolic", flow.tr("Pause"), flow.toggle_pause
        )
        self.advance = icon_button("media-skip-forward-symbolic", flow.tr("Next"), flow.next_step)
        self.stop_button = icon_button(
            "media-playback-stop-symbolic", flow.tr("Stop"), self.stop_pressed
        )
        for control in (self.pause, self.advance, self.stop_button):
            control.add_css_class("presenter-command")
            controls.append(control)
        self.obs = Gtk.Label(xalign=1, hexpand=True, wrap=True, wrap_mode=Pango.WrapMode.WORD_CHAR)
        self.obs.set_lines(2)
        self.obs.set_ellipsize(Pango.EllipsizeMode.END)
        self.obs.set_max_width_chars(32)
        self.obs.add_css_class("presenter-obs")
        add(footer, controls, self.obs)
        self.connect("hide", self.on_hide)
        self.layout_size = None
        self.configure()

    def configure(self):
        opts = self.flow.timer_options
        if self.layout_size == opts.size:
            return
        if self.layout_size is not None:
            self.remove_css_class("presenter-" + self.layout_size)
        self.layout_size = opts.size
        self.add_css_class("presenter-" + opts.size)
        large = opts.size == "large"
        self.set_default_size(710 if large else 458, 442 if large else 296)
        self.signal.set_size_request(74 if large else 42, 146 if large else 82)
        margins(self.body, 18 if large else 12)

    def on_hide(self, *_):
        self.flow.panel.cancel_record_cue()

    def stop_pressed(self):
        if self.flow.active:
            self.flow.stop()
        else:
            self.flow.panel.cancel_record_cue()

    def open_settings(self):
        if self.settings_window is None:
            self.settings_window = TimerSettings(self)
        self.settings_window.present()
        return self.settings_window

    def update_display(self, state):
        flow = self.flow
        player = flow.player
        tr = flow.tr
        opts = flow.timer_options
        cue = flow.record_cue
        active = flow.active
        free = player.mode == "timer" and active
        clock_active = active and not flow.checking and player.state != "switching"
        if active and not flow.checking:
            steps = player.steps
        elif player.state in ("finished", "stopped"):
            steps = []
        else:
            steps = flow.flow["steps"]
        index = player.index if active and not flow.checking else 0
        current = steps[index] if not free and 0 <= index < len(steps) else None
        upcoming = steps[index + 1] if current and index + 1 < len(steps) else None

        if cue:
            heading = tr("Timer")
        elif active and not free and not flow.checking:
            mode = "OBS" if player.mode == "live" else tr("Rehearsal")
            heading = mode + " · " + tr(state)
        else:
            heading = tr("Free timer") if free and player.state == "running" else tr(state)
        self.heading.set_label(heading)
        self.set_title(tr("Timer"))
        self.clock_caption.set_label(
            tr("Recording in" if cue else "Free timer" if free else "Scene remaining")
        )
        remaining = player.remaining if clock_active else 0
        if not active and current:
            remaining = current["seconds"]
        digits = clock_text(cue[1] if cue else remaining)
        self.clock.set_label(digits)
        if len(digits) > 6:
            self.clock.add_css_class("long-time")
        else:
            self.clock.remove_css_class("long-time")
        self.total_caption.set_label(tr("Workflow remaining"))
        total = player.total_remaining if clock_active else sum(s["seconds"] for s in steps[index:])
        self.total.set_label(clock_text(total))
        self.total_block.set_visible(opts.show_total and not free and cue is None and bool(steps))
        self.route.set_visible(not free and bool(current) and cue is None)
        self.current_caption.set_label(tr("Current scene"))
        self.current_name.set_label(flow.scene_title(current["scene"]) if current else "")
        self.next_caption.set_label(tr("Next scene"))
        self.next_name.set_label(flow.scene_title(upcoming["scene"]) if upcoming else "—")
        self.next_block.set_visible(opts.show_next)
        self.arrow.set_visible(opts.show_next)
        note = current["note"] if current else ""
        self.note.set_label(note)
        self.note.set_tooltip_text(note or None)
        self.note.set_visible(opts.show_note and bool(note) and cue is None)
        warning = (
            opts.end_warning
            and clock_active
            and player.state == "running"
            and 0 < remaining <= opts.warning_seconds
        )
        signal = cue[0] if cue else "green" if warning else "off"
        self.signal.set_visible(opts.lead_in_enabled or opts.end_warning)
        self.signal.update(
            signal, tr("Recording countdown" if cue else "End warning" if warning else "Signals")
        )
        status = flow.panel.obs_status.get_label()
        if player.reason:
            status = tr(player.reason) + (" · " + status if opts.show_obs else "")
        self.obs.set_label(status)
        self.obs.set_visible((opts.show_obs and bool(status)) or bool(player.reason))
        self.obs.set_tooltip_text(status)
        paused = player.state == "paused"
        pause_icon = "media-playback-start-symbolic" if paused else "media-playback-pause-symbolic"
        if self.pause.get_icon_name() != pause_icon:
            self.pause.set_icon_name(pause_icon)
        self.config_button.set_sensitive(cue is None)
        for control, label in (
            (self.pause, "Resume" if paused else "Pause"),
            (self.advance, "Next"),
            (self.stop_button, "Stop"),
            (self.config_button, "Timer settings"),
        ):
            description = tr(label)
            if control.get_tooltip_text() != description:
                control.set_tooltip_text(description)
                control.update_property([Gtk.AccessibleProperty.LABEL], [description])
        self.pause.set_sensitive(
            not flow.checking and player.state in ("paused", "running") and cue is None
        )
        self.advance.set_sensitive(
            not flow.checking and not free and player.state == "running" and cue is None
        )
        self.stop_button.set_sensitive(active or cue is not None)
        if self.settings_window:
            self.settings_window.start.set_sensitive(not active and cue is None)


class TimerSettings(Gtk.Window):
    def __init__(self, timer):
        self.timer = timer
        self.flow = timer.flow
        self.tr = self.flow.tr
        super().__init__(
            title=self.tr("Timer settings"),
            transient_for=timer,
            modal=True,
            destroy_with_parent=True,
        )
        self.add_css_class("umberto-lab")
        self.add_css_class("timer-settings")
        self.set_titlebar(Gtk.HeaderBar())
        self.set_default_size(474, 512)
        self.connect("close-request", self.on_close)
        key = Gtk.EventControllerKey()
        key.connect("key-pressed", self.key_pressed)
        self.add_controller(key)
        body = box(True, 10)
        margins(body, 14)
        self.set_child(body)
        scroll = Gtk.ScrolledWindow(vexpand=True)
        scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        self.sections = box(True, 9)
        scroll.set_child(self.sections)
        body.append(scroll)
        self.values = {}
        opts = self.flow.timer_options
        display = self.section("Display", True)
        self.size = Gtk.DropDown.new_from_strings([self.tr("Compact"), self.tr("Large")])
        self.size.set_selected(("compact", "large").index(opts.size))
        self.row(display, "Timer size", self.size)
        for key, caption in (
            ("show_next", "Show next scene"),
            ("show_total", "Show total remaining"),
            ("show_note", "Show scene note"),
            ("show_obs", "Show OBS status"),
        ):
            self.switch(display, key, caption)
        signals = self.section("Signals", False)
        self.switch(signals, "lead_in_enabled", "Recording countdown")
        self.switch(signals, "end_warning", "End warning")
        self.warning = self.spin(signals, "Warning (seconds)", 60, opts.warning_seconds)
        self.values["end_warning"].bind_property(
            "active", self.warning, "sensitive", GObject.BindingFlags.SYNC_CREATE
        )
        free = self.section("Free timer", False)
        self.free = self.spin(free, "Free timer (seconds)", 86400, opts.free_seconds)
        self.start = button(self.tr("Start timer"), lambda: self.save(True))
        self.start.set_halign(Gtk.Align.END)
        self.start.set_sensitive(not self.flow.active and self.flow.record_cue is None)
        free.append(self.start)
        self.error = Gtk.Label(wrap=True, xalign=0, visible=False)
        body.append(self.error)
        actions = box(spacing=8)
        actions.set_halign(Gtk.Align.END)
        self.cancel = button(self.tr("Cancel"), self.close)
        self.apply = button(self.tr("Save"), lambda: self.save(False))
        self.apply.add_css_class("suggested-action")
        add(actions, self.cancel, self.apply)
        body.append(actions)
        self.set_default_widget(self.apply)

    def section(self, name, opened):
        section = Gtk.Expander(label=self.tr(name), expanded=opened)
        section.add_css_class("workflow-library-section")
        body = box(True, 0)
        body.set_margin_top(9)
        section.set_child(body)
        self.sections.append(section)
        return body

    def row(self, body, name, control):
        if body.get_first_child():
            body.append(Gtk.Separator())
        row = box(spacing=12)
        row.add_css_class("timer-settings-row")
        label = text(self.tr(name))
        label.set_hexpand(True)
        label.set_wrap(True)
        label.set_mnemonic_widget(control)
        control.set_valign(Gtk.Align.CENTER)
        control.update_property([Gtk.AccessibleProperty.LABEL], [self.tr(name)])
        add(row, label, control)
        body.append(row)

    def switch(self, section, key, name):
        control = Gtk.Switch(active=getattr(self.flow.timer_options, key))
        self.values[key] = control
        self.row(section, name, control)

    def spin(self, section, name, maximum, value):
        control = Gtk.SpinButton.new_with_range(1, maximum, 1)
        control.set_numeric(True)
        control.set_value(value)
        self.row(section, name, control)
        return control

    def save(self, start):
        if start and (self.flow.active or self.flow.record_cue):
            return
        self.warning.update()
        self.free.update()
        try:
            opts = TimerOptions(
                size=("compact", "large")[self.size.get_selected()],
                warning_seconds=self.warning.get_value_as_int(),
                free_seconds=self.free.get_value_as_int(),
                **{key: ctrl.get_active() for key, ctrl in self.values.items()},
            )
            self.flow.apply_timer_options(opts)
        except (ValueError, OSError):
            self.error.set_label(self.tr("Could not save timer settings."))
            self.error.set_visible(True)
            return
        self.close()
        if start:
            self.flow.start_free()

    def on_close(self, _window):
        self.timer.settings_window = None
        return False

    def key_pressed(self, _ctrl, keyval, _code, _state):
        if keyval == Gdk.KEY_Escape:
            self.close()
            return True
        return False
