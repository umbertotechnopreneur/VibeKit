"""Native settings, refresh coordination, and serialized-save lifecycle for the widget."""
import os
import shutil
import threading
from pathlib import Path
from zoneinfo import available_timezones
from gi.repository import Adw, Gdk, GLib, Gtk
from ui_helpers import box, text
from watchdog import record as log_event

LARGER_WIDGET_PERCENT = 15


class SettingsUiMixin:
    def refresh(self):
        if self.closed:
            return False
        if self.exit_pending:
            return True
        if self.busy:
            self.refresh_pending = True
            return True
        self.busy = True
        revision = self.settings_revision
        if self.settings_error is None:
            self.update_menu_status("Refreshing Codex account…", warning=not self.state.get("connected", False))
        def worker():
            try:
                settings = self.backend("settings-status")
                GLib.idle_add(self.settings_loaded, settings, revision)
                state = self.backend()
            except (OSError, RuntimeError) as exc:
                settings, state = {}, {"connected": False, "status": str(exc)}
            GLib.idle_add(self.received, settings, state, revision)
        threading.Thread(target=worker, daemon=True).start()
        return True

    def settings_loaded(self, settings, revision):
        if (not self.closed and revision == self.settings_revision and not self.saving
                and self.saved_settings_revision == self.settings_revision):
            self.settings = settings["settings"]
            self.set_settings_error(settings.get("recoveryMessage"))
            self.settings_ready = True
            self.sync_pin()
            self.sync_taskbar()
            self.apply_size()
        return False

    def set_settings_error(self, error):
        self.settings_error = error
        if self.settings_status_label is not None:
            self.settings_status_label.set_text(error or "")
            self.settings_status_label.set_visible(bool(error))

    def update_settings(self, **changes):
        updated = self.settings | changes
        if updated == self.settings:
            return
        self.settings = updated
        self.settings_revision += 1
        if "showInTaskbar" in changes:
            self.sync_taskbar()
        self.apply_size()
        self.check_alerts()
        self.queue_settings_save()

    def queue_settings_save(self):
        if self.settings_save_source:
            GLib.source_remove(self.settings_save_source)
        def save_latest():
            self.settings_save_source = 0
            self.save_async()
            return False
        self.settings_save_source = GLib.timeout_add(300, save_latest)

    def save_async(self):
        if self.settings_save_source:
            GLib.source_remove(self.settings_save_source)
            self.settings_save_source = 0
        if self.saving or self.saved_settings_revision == self.settings_revision:
            return
        self.saving = True
        settings = dict(self.settings)
        revision = self.settings_revision
        def worker():
            try:
                self.backend("save-settings", settings)
                error = None
            except (OSError, RuntimeError) as exc:
                error = str(exc)
            def done():
                self.saving = False
                if self.closed:
                    return False
                if error:
                    self.set_settings_error(error)
                    self.update_menu_status(error, warning=True)
                    log_event("settings.failed", error)
                    if revision == self.settings_revision and self.exit_pending:
                        self.exit_pending = False
                        self.allow_close = False
                        self.present()
                elif revision == self.settings_revision:
                    self.set_settings_error(None)
                    self.saved_settings_revision = revision
                    if self.exit_pending:
                        self.close()
                    else:
                        self.refresh()
                if revision != self.settings_revision:
                    self.save_async()
                return False
            GLib.idle_add(done)
        threading.Thread(target=worker, daemon=True).start()

    @staticmethod
    def add_startup_preference(parent):
        path = Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config") / "autostart" / "biz.umbertogiacobbi.OpenAIUsage.desktop"
        entry = "[Desktop Entry]\nType=Application\nName=OpenAI Usage\nExec=openai-usage\nIcon=biz.umbertogiacobbi.OpenAIUsage\n"
        toggle = Adw.SwitchRow(title="Start on sign-in", use_markup=False)
        parent.add_row(toggle)
        default_subtitle = ""
        try:
            existing = path.read_text(encoding="utf-8") if path.exists() else None
        except OSError:
            existing = None
            toggle.set_sensitive(False)
        toggle.set_active(existing is not None)
        if not shutil.which("openai-usage") or (existing is not None and existing != entry):
            toggle.set_sensitive(False)
            default_subtitle = "Available for the installed widget with a managed startup entry."

        toggle.set_subtitle(default_subtitle)
        changing = False

        def change(control, _):
            nonlocal changing, existing
            if changing:
                return
            try:
                if control.get_active():
                    path.parent.mkdir(parents=True, exist_ok=True)
                    with path.open("x", encoding="utf-8") as output:
                        output.write(entry)
                    existing = entry
                elif path.read_text(encoding="utf-8") == entry:
                    path.unlink()
                    existing = None
                else:
                    raise OSError("Startup entry was modified outside the widget.")
                control.set_subtitle(default_subtitle)
            except OSError as exc:
                changing = True
                control.set_active(existing is not None)
                changing = False
                control.set_subtitle(str(exc))

        toggle.connect("notify::active", change)

    def build_module_preferences(self):
        listing = Gtk.ListBox(selection_mode=Gtk.SelectionMode.NONE)
        rows = {}
        for key, widget, purpose in self.ordered_modules():
            label = widget.name
            row = Gtk.ListBoxRow(activatable=False, selectable=False)
            rows[key] = row
            line = box(10, True)
            line.set_margin_top(8)
            line.set_margin_bottom(8)
            grip = Gtk.Label(label="⠿", valign=Gtk.Align.CENTER)
            grip.add_css_class("heading")
            grip.set_tooltip_text("Drag to move " + label)
            grip.set_cursor_from_name("grab")
            source = Gtk.DragSource(actions=Gdk.DragAction.MOVE)
            source.connect("prepare", lambda _, x, y, field=key: Gdk.ContentProvider.new_for_value(field))
            grip.add_controller(source)
            line.append(grip)
            fields = box(3)
            fields.set_hexpand(True)
            toggle = Gtk.Switch(active=self.module_enabled(key), valign=Gtk.Align.CENTER)
            toggle.update_property([Gtk.AccessibleProperty.LABEL], [label])
            toggle.connect("notify::active", lambda item, _, field=key: self.set_module_enabled(field, item.get_active()))
            fields.append(text(label))
            fields.append(text(purpose, "dim"))
            line.append(fields)
            line.append(toggle)
            row.set_child(line)
            drop = Gtk.DropTarget.new(str, Gdk.DragAction.MOVE)
            drop.connect("drop", lambda _, source_key, x, y, target=key: move(source_key, target, y))
            row.add_controller(drop)
            listing.append(row)

        def move(source, target, y):
            if source == target or source not in rows:
                return False
            row, destination = rows[source], rows[target]
            index = destination.get_index() + (y >= destination.get_height() / 2)
            if row.get_index() < index:
                index -= 1
            listing.remove(row)
            listing.insert(row, index)
            order = sorted(rows, key=lambda key: rows[key].get_index())
            self.update_settings(fieldOrder=order)
            return True

        return listing

    def hide_settings(self, *_):
        if self.settings_window is not None:
            self.settings_window.close()

    def show_settings(self, *_):
        if self.closed or self.exit_pending:
            return
        if self.settings_window is not None:
            self.settings_window.present()
            return
        if not self.get_visible():
            self.present()
        if not self.settings_ready:
            self.update_menu_status("Settings are not available yet; retry after the account reader starts.", warning=True)
            self.refresh()
            return
        group = Adw.PreferencesGroup()
        sections = []

        def section(title, subtitle, icon):
            expander = Adw.ExpanderRow(title=title, subtitle=subtitle, use_markup=False)
            expander.add_prefix(Gtk.Image(icon_name=icon))
            group.add(expander)
            sections.append(expander)

            def on_expanded(row, _):
                if row.get_expanded():
                    for other in sections:
                        if other is not row:
                            other.set_expanded(False)

            expander.connect("notify::expanded", on_expanded)
            return expander

        appearance = section("Appearance", "Labels, usage details, size and opacity", "preferences-desktop-appearance-symbolic")
        fields = section("Widget fields", "Choose what the widget shows and in which order", "view-list-symbolic")
        alerts = section("Alerts", "Allowance and credit thresholds", "preferences-system-notifications-symbolic")
        connection = section("Codex connection", "Executable and time zone", "network-server-symbolic")
        desktop = section("Desktop", "Taskbar visibility and startup", "preferences-system-symbolic")

        def switch(parent, title, key, default=False):
            row = Adw.SwitchRow(title=title, active=self.settings.get(key, default))
            row.connect("notify::active", lambda item, _: self.update_settings(**{key: item.get_active()}))
            parent.add_row(row)
            return row

        def choice(parent, title, labels, values, key, default):
            row = Adw.ComboRow(title=title, model=Gtk.StringList.new(labels))
            current = self.settings.get(key, default)
            row.set_selected(values.index(current) if current in values else values.index(default))
            row.connect("notify::selected", lambda item, _: self.update_settings(**{key: values[item.get_selected()]}))
            parent.add_row(row)

        def entry_row(parent, title, value, subtitle="", placeholder=None):
            row = Adw.ActionRow(title=title, subtitle=subtitle, use_markup=False)
            entry = Gtk.Entry(text=value, placeholder_text=placeholder, valign=Gtk.Align.CENTER,
                              width_chars=14)
            entry.update_property([Gtk.AccessibleProperty.LABEL], [title])
            row.add_suffix(entry)
            row.set_activatable_widget(entry)
            parent.add_row(row)
            return row, entry

        choice(appearance, "Field labels", ["Above values", "Below values", "Hidden"],
               ("above", "below", "hidden"), "labelPosition", "above")
        switch(appearance, "Enable usage details", "enableDetailsBar")
        switch(appearance, f"Larger widget (+{LARGER_WIDGET_PERCENT}%)", "bigSize")
        choice(appearance, "Window opacity", ["25%", "50%", "75%", "100%"],
               (25, 50, 75, 100), "windowOpacityPercent", 100)

        field_list = box(8)
        for set_margin in (field_list.set_margin_start, field_list.set_margin_end,
                           field_list.set_margin_top, field_list.set_margin_bottom):
            set_margin(12)
        field_list.append(text("Drag to reorder. Use the switches to show or hide fields. Details appear in normal mode when usage details are enabled.", "dim"))
        field_list.append(self.build_module_preferences())
        fields.add_row(Gtk.ListBoxRow(child=field_list, activatable=False, selectable=False))

        for threshold in (50, 20, 5):
            switch(alerts, f"{threshold}% remaining", f"alert{threshold}")
        switch(alerts, "Play a sound for alerts", "alertSound", True)
        existing = self.settings.get("creditsAlertThreshold")
        credit_hint = "Send an alert at or below this balance. Leave blank to disable."

        credit_row, credits = entry_row(alerts, "Credit alert threshold", "" if existing is None else str(existing),
                                       credit_hint, "Example: 1000")

        def credit_changed(*_):
            value = credits.get_text().strip()
            valid = not value or (value.isascii() and value.isdecimal() and len(value) <= 10 and int(value) <= 2_147_483_647)
            credit_row.set_subtitle(credit_hint if valid else "Enter a whole number from 0 to 2147483647.")
            if valid:
                self.update_settings(creditsAlertThreshold=int(value) if value else None)

        credits.connect("changed", credit_changed)
        path_hint = "Uses your existing Codex ChatGPT sign-in."
        executable_row, executable = entry_row(connection, "Codex executable", self.settings.get("codexPath", "codex"), path_hint)
        zones = sorted(available_timezones())
        current = self.settings.get("timeZoneId", "UTC")
        if current not in zones:
            zones.insert(0, current)
        timezone = Adw.ComboRow(title="Time zone", model=Gtk.StringList.new(zones),
                                selected=zones.index(current), enable_search=True)
        connection.add_row(timezone)

        def executable_changed(*_):
            path = executable.get_text().strip()
            executable_row.set_subtitle(path_hint if path else "Choose a Codex executable.")
            if path:
                self.update_settings(codexPath=path)

        def timezone_changed(*_):
            selected = timezone.get_selected()
            if selected < len(zones):
                self.update_settings(timeZoneId=zones[selected])

        executable.connect("changed", executable_changed)
        timezone.connect("notify::selected", timezone_changed)
        taskbar = switch(desktop, "Show in taskbar", "showInTaskbar", True)
        if self.x11_surface() is None:
            taskbar.set_sensitive(False)
            taskbar.set_subtitle("The Wayland compositor controls taskbar visibility.")
        self.add_startup_preference(desktop)

        for setter in (group.set_margin_start, group.set_margin_end, group.set_margin_top, group.set_margin_bottom):
            setter(16)
        scroll = Gtk.ScrolledWindow(child=group, hscrollbar_policy=Gtk.PolicyType.NEVER, vexpand=True)
        monitor = self.get_display().get_monitor_at_surface(self.get_surface()) if self.get_surface() else None
        height = min(720, monitor.get_geometry().height - 48) if monitor else 720
        window = Gtk.Window(application=self.get_application(), transient_for=self, destroy_with_parent=True,
                            modal=False, resizable=False, title="OpenAI Usage · Settings",
                            default_width=540, default_height=max(1, height))
        header = Gtk.HeaderBar(show_title_buttons=False)
        done = Gtk.Button(label="Done")
        done.connect("clicked", self.hide_settings)
        header.pack_end(done)
        window.set_titlebar(header)
        surface = box(0)
        surface.append(scroll)
        self.settings_status_label = text("", "warning")
        self.settings_status_label.set_margin_start(16)
        self.settings_status_label.set_margin_end(16)
        self.settings_status_label.set_margin_bottom(16)
        self.set_settings_error(self.settings_error)
        surface.append(self.settings_status_label)
        window.set_child(surface)

        def on_close(dialog):
            if self.settings_window is dialog:
                self.settings_window = self.settings_status_label = None
                self.save_async()
            return False

        def key_pressed(_, key, *_args):
            if key != Gdk.KEY_Escape:
                return False
            self.hide_settings()
            return True

        keys = Gtk.EventControllerKey()
        keys.connect("key-pressed", key_pressed)
        window.add_controller(keys)
        window.connect("close-request", on_close)
        self.settings_window = window
        window.present()
