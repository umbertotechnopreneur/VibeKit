"""Native settings, refresh coordination, and serialized-save lifecycle for the widget."""
import threading
from zoneinfo import available_timezones
from gi.repository import GLib, Gtk
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
        self.brand.set_tooltip_text("Refreshing Codex account…")
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
        dialog = Gtk.Window(title="Settings", transient_for=self, modal=True, default_width=460, default_height=640,
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
        executable = Gtk.Entry(text=self.settings.get("codexPath", "codex"))
        zones = sorted(available_timezones())
        current = self.settings.get("timeZoneId", "UTC")
        if current not in zones:
            zones.insert(0, current)
        timezone = Gtk.DropDown.new_from_strings(zones)
        timezone.set_selected(zones.index(current))
        timezone.set_enable_search(True)
        content.append(text("Codex executable", "dim"))
        content.append(executable)
        content.append(text("Time zone", "dim"))
        content.append(timezone)
        content.append(text("Uses your existing Codex ChatGPT sign-in.", "dim"))
        big_size = Gtk.Switch(active=self.settings.get("bigSize", False), halign=Gtk.Align.END)
        big_row = box(12, True)
        big_label = text("Big size (+25%)")
        big_label.set_hexpand(True)
        big_row.append(big_label)
        big_row.append(big_size)
        content.append(big_row)
        label_positions = ("above", "below", "hidden")
        position = self.settings.get("labelPosition", "above")
        positions = Gtk.DropDown.new_from_strings(["Above the tiles", "Below the tiles", "Hidden"])
        positions.set_selected(label_positions.index(position) if position in label_positions else 0)
        position_row = box(12, True)
        position_label = text("Field labels")
        position_label.set_hexpand(True)
        position_row.append(position_label)
        position_row.append(positions)
        content.append(position_row)
        fields = {}
        for key, label in (("showAvailableResets", "Available resets"), ("showCredits", "Credits left"),
                           ("showEstimatedTime", "Est. time Left")):
            check = Gtk.CheckButton(label=f"Show {label}", active=self.settings.get(key, True))
            fields[key] = check
            content.append(check)
        details = Gtk.CheckButton(label="Enable usage details bar", active=self.settings.get("enableDetailsBar", False))
        content.append(details)
        fields["enableDetailsBar"] = details
        content.append(Gtk.Separator())
        alerts = {}
        for threshold in (50, 20, 5):
            check = Gtk.CheckButton(label=f"Notify at {threshold}% remaining", active=self.settings.get(f"alert{threshold}", False))
            alerts[threshold] = check
            content.append(check)
        alert_sound = Gtk.CheckButton(label="Play an alert ding", active=self.settings.get("alertSound", True))
        content.append(alert_sound)
        configured_limit = self.settings.get("creditsAlertThreshold")
        credit_alert = Gtk.CheckButton(label="Notify at or below this credit balance", active=configured_limit is not None)
        credit_limit = Gtk.SpinButton.new_with_range(0, 2_147_483_647, 1)
        credit_limit.set_numeric(True)
        credit_limit.set_value(configured_limit if configured_limit is not None else 0)
        credit_limit.set_sensitive(credit_alert.get_active())
        credits_row = box(12, True)
        credits_row.append(credit_alert)
        credits_row.append(credit_limit)
        content.append(credits_row)
        path_error = text("Choose a Codex executable.", "warning")
        path_error.set_visible(False)
        content.insert_child_after(path_error, executable)
        threshold_error = text("Enter a whole number from 0 to 2147483647.", "warning")
        threshold_error.set_visible(False)
        content.append(threshold_error)

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
            enabled = credit_alert.get_active()
            credit_limit.set_sensitive(enabled)
            value = credit_limit.get_text().strip()
            valid = value.isdecimal() and len(value) <= 10 and int(value) <= 2_147_483_647
            threshold_error.set_visible(enabled and not valid)
            if not enabled or valid:
                self.update_settings(creditsAlertThreshold=int(value) if enabled else None)

        executable.connect("changed", executable_changed)
        timezone.connect("notify::selected", timezone_changed)
        big_size.connect("notify::active", lambda control, _: self.update_settings(bigSize=control.get_active()))
        positions.connect("notify::selected", lambda control, _: self.update_settings(labelPosition=label_positions[control.get_selected()]))
        credit_limit.connect("changed", credit_changed)
        credit_alert.connect("toggled", credit_changed)
        for key, check in {**fields, **{f"alert{threshold}": check for threshold, check in alerts.items()}, "alertSound": alert_sound}.items():
            check.connect("toggled", lambda control, field=key: self.update_settings(**{field: control.get_active()}))

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
