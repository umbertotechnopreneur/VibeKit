"""Native settings, refresh coordination, and serialized-save lifecycle for the widget."""
import os
import shutil
import threading
from pathlib import Path
from zoneinfo import available_timezones
from gi.repository import Gdk, GLib, Gtk
from ui_helpers import box, text
from watchdog import record as log_event


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
            first_load = not self.settings_ready
            self.settings = settings["settings"]
            self.set_settings_error(settings.get("recoveryMessage"))
            self.settings_ready = True
            self.sync_pin()
            self.sync_taskbar()
            if first_load and self.settings.get("enableDetailsBar", False):
                self.details_button.set_active(True)
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
        row = box(8, True)
        label = text("Start on sign-in")
        label.set_hexpand(True)
        toggle = Gtk.Switch(valign=Gtk.Align.CENTER)
        error = text("", "warning")
        error.set_visible(False)
        row.append(label)
        row.append(toggle)
        parent.append(row)
        parent.append(error)
        toggle.update_property([Gtk.AccessibleProperty.LABEL], ["Start on sign-in"])
        try:
            existing = path.read_text(encoding="utf-8") if path.exists() else None
        except OSError:
            existing = None
            toggle.set_sensitive(False)
        toggle.set_active(existing is not None)
        if not shutil.which("openai-usage") or (existing is not None and existing != entry):
            toggle.set_sensitive(False)
            toggle.set_tooltip_text("Available for the installed widget with a managed startup entry.")

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
                error.set_visible(False)
            except OSError as exc:
                changing = True
                control.set_active(existing is not None)
                changing = False
                error.set_text(str(exc))
                error.set_visible(True)

        toggle.connect("notify::active", change)

    def build_module_preferences(self):
        listing = Gtk.ListBox(selection_mode=Gtk.SelectionMode.NONE)
        rows = {}
        labels = {"clock": "Next reset countdown and date", "detail-estimate": "Detailed time estimate"}
        for key, widget, purpose in self.ordered_modules():
            label = labels[key] if key in labels else widget.name
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

    def show_settings(self, *_):
        self.present()
        if self.settings_dialog is not None:
            self.settings_dialog.present()
            return
        if not self.settings_ready:
            self.problem.set_visible(True)
            self.problem.set_tooltip_text("Settings are not available yet; retry after the account reader starts.")
            self.refresh()
            return
        dialog = Gtk.Window(title="Settings", transient_for=self, modal=True, default_width=620, default_height=640,
                            icon_name="biz.umbertogiacobbi.OpenAIUsage")
        self.settings_dialog = dialog
        def closed(*_):
            self.settings_status_label = None
            self.settings_dialog = None
            self.save_async()
            return False
        dialog.connect("close-request", closed)
        content = box(12)
        for setter in (content.set_margin_start, content.set_margin_end, content.set_margin_top, content.set_margin_bottom):
            setter(24)
        executable = Gtk.Entry(text=self.settings.get("codexPath", "codex"), hexpand=True)
        zones = sorted(available_timezones())
        current = self.settings.get("timeZoneId", "UTC")
        if current not in zones:
            zones.insert(0, current)
        timezone = Gtk.DropDown.new_from_strings(zones)
        timezone.set_selected(zones.index(current))
        timezone.set_enable_search(True)
        timezone.set_hexpand(True)
        path_error = text("Choose a Codex executable.", "warning")
        path_error.set_visible(False)
        connection = Gtk.Grid(column_homogeneous=True, column_spacing=18)
        for index, (label, control) in enumerate((("Codex executable", executable), ("Time zone", timezone))):
            field = box(4)
            field.append(text(label, "dim"))
            field.append(control)
            if index == 0:
                field.append(path_error)
            connection.attach(field, index, 0, 1, 1)
        content.append(connection)
        content.append(text("Uses your existing Codex ChatGPT sign-in.", "dim"))

        columns = Gtk.Grid(column_homogeneous=True, column_spacing=18)
        widget, alerts = box(8), box(8)
        columns.attach(widget, 0, 0, 1, 1)
        columns.attach(alerts, 1, 0, 1, 1)
        content.append(columns)
        widget.append(text("Widget", "heading"))
        alerts.append(text("Alerts", "heading"))

        def switch(parent, label, key, default=False):
            control = Gtk.Switch(active=self.settings.get(key, default), valign=Gtk.Align.CENTER)
            control.update_property([Gtk.AccessibleProperty.LABEL], [label])
            row = box(8, True)
            title = text(label)
            title.set_hexpand(True)
            row.append(title)
            row.append(control)
            parent.append(row)
            control.connect("notify::active", lambda item, _: self.update_settings(**{key: item.get_active()}))
            return control

        def choice(parent, label, labels, values, key, default):
            parent.append(text(label))
            control = Gtk.DropDown.new_from_strings(labels)
            current = self.settings.get(key, default)
            control.set_selected(values.index(current) if current in values else values.index(default))
            control.connect("notify::selected", lambda item, _: self.update_settings(**{key: values[item.get_selected()]}))
            parent.append(control)

        choice(widget, "Field labels", ["Above values", "Below values", "Hidden"],
               ("above", "below", "hidden"), "labelPosition", "above")
        details = switch(widget, "Enable usage details", "enableDetailsBar")
        details.connect("notify::active", lambda item, _: self.details_button.set_active(item.get_active()))
        switch(widget, "Larger widget (+25%)", "bigSize")
        choice(widget, "Window opacity", ["25%", "50%", "75%", "100%"],
               (25, 50, 75, 100), "windowOpacityPercent", 100)
        widget.append(text("Desktop", "heading"))
        taskbar = switch(widget, "Show in taskbar", "showInTaskbar", True)
        if self.x11_surface() is None:
            taskbar.set_sensitive(False)
            taskbar.set_tooltip_text("The Wayland compositor controls taskbar visibility.")
        self.add_startup_preference(widget)
        for threshold in (50, 20, 5):
            switch(alerts, f"{threshold}% remaining", f"alert{threshold}")
        switch(alerts, "Play a sound for alerts", "alertSound", True)

        alerts.append(text("Credit alert threshold"))
        existing = self.settings.get("creditsAlertThreshold")
        credits = Gtk.Entry(text=str(existing) if existing is not None else "", placeholder_text="Example: 1000")
        alerts.append(credits)
        alerts.append(text("Send an alert at or below this number of remaining credits. Enter whole credits, such as 1000. An empty field disables credit alerts.", "dim"))
        threshold_error = text("Enter a whole number from 0 to 2147483647.", "warning")
        threshold_error.set_visible(False)
        alerts.append(threshold_error)

        def executable_changed(*_):
            path = executable.get_text().strip()
            path_error.set_visible(not path)
            if path:
                self.update_settings(codexPath=path)

        def timezone_changed(*_):
            selected = timezone.get_selected()
            if selected < len(zones):
                self.update_settings(timeZoneId=zones[selected])

        def credit_changed(*_):
            value = credits.get_text().strip()
            valid = not value or (value.isascii() and value.isdecimal() and len(value) <= 10 and int(value) <= 2_147_483_647)
            threshold_error.set_visible(not valid)
            if valid:
                self.update_settings(creditsAlertThreshold=int(value) if value else None)

        executable.connect("changed", executable_changed)
        timezone.connect("notify::selected", timezone_changed)
        credits.connect("changed", credit_changed)
        content.append(text("Widget modules", "heading"))
        content.append(text("Drag the grip to change the order. Use the switch to show or hide a module.", "dim"))
        content.append(self.build_module_preferences())

        scroll = Gtk.ScrolledWindow(child=content, hscrollbar_policy=Gtk.PolicyType.NEVER, vexpand=True)
        surface = box(0)
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
        dialog.set_child(surface)
        dialog.present()
