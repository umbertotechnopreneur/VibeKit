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
        self.project_link.set_tooltip_text("OpenAI Usage project page\nRefreshing Codex account…")
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
                    self.problem.set_visible(True)
                    self.problem.set_tooltip_text(error)
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

    def toggle_settings(self):
        if self.settings_panel is None:
            self.show_settings()
        else:
            self.hide_settings()

    def hide_settings(self, *_):
        if self.settings_panel is None:
            return
        panel, restore = self.settings_panel, self.settings_window_size
        self.settings_panel = self.settings_window_size = self.settings_status_label = None
        self.window_content.remove(panel)
        self.remove_css_class("settings-open")
        self.widget_preview.set_vexpand(True)
        self.fit_layout(self.layout_width)
        self.queue_fit_height(restore)
        self.save_async()

    def show_settings(self, *_):
        self.present()
        if self.settings_panel is not None:
            return
        if not self.settings_ready:
            self.problem.set_visible(True)
            self.problem.set_tooltip_text("Settings are not available yet; retry after the account reader starts.")
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

        content = Adw.Clamp(child=group, maximum_size=640, tightening_threshold=480)
        for setter in (content.set_margin_start, content.set_margin_end, content.set_margin_top, content.set_margin_bottom):
            setter(16)
        scroll = Gtk.ScrolledWindow(child=content, hscrollbar_policy=Gtk.PolicyType.NEVER, vexpand=True)
        surface = self.settings_panel = box(0)
        surface.add_css_class("settings-panel")
        surface.set_vexpand(True)
        heading = box(8, True)
        heading.set_margin_start(24)
        heading.set_margin_end(24)
        heading.set_margin_top(12)
        title = text("Settings", "heading")
        title.set_hexpand(True)
        done = Gtk.Button(label="Done")
        done.connect("clicked", self.hide_settings)
        heading.append(title)
        heading.append(done)
        surface.append(heading)
        surface.append(scroll)
        footer = box(4)
        for setter in (footer.set_margin_start, footer.set_margin_end):
            setter(24)
        footer.set_margin_bottom(16)
        self.settings_status_label = text("", "warning")
        self.set_settings_error(self.settings_error)
        footer.append(self.settings_status_label)
        footer.append(text("Changes are saved automatically.", "dim"))
        surface.append(footer)
        window_surface = self.get_surface()
        self.initial_fit_done = True
        self.settings_window_size = (window_surface.get_width(), window_surface.get_height())
        monitor = self.get_display().get_monitor_at_surface(window_surface)
        width = min(640, monitor.get_geometry().width - 32) if monitor else 640
        self.add_css_class("settings-open")
        self.widget_preview.set_vexpand(False)
        self.window_content.append(surface)
        self.queue_fit_height(minimum_width=width)
