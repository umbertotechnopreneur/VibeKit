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
            self.settings_error = settings.get("recoveryMessage")
            self.settings_ready = True
            self.sync_pin()
            self.apply_size()
        return False

    def queue_settings_save(self, error_label=None):
        if self.settings_save_source:
            GLib.source_remove(self.settings_save_source)
        def save_latest():
            self.settings_save_source = 0
            self.save_async(error_label)
            return False
        self.settings_save_source = GLib.timeout_add(300, save_latest)

    def save_async(self, error_label=None):
        if self.settings_save_source:
            GLib.source_remove(self.settings_save_source)
            self.settings_save_source = 0
        if self.saving:
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
                    self.settings_error = error
                    if error_label is not None:
                        error_label.set_text(error)
                    self.problem.set_visible(True)
                    self.problem.set_tooltip_text(error)
                    log_event("settings.failed", error)
                    if revision == self.settings_revision and self.exit_pending:
                        self.exit_pending = False
                        self.allow_close = False
                        self.present()
                elif revision == self.settings_revision:
                    self.settings_error = None
                    self.saved_settings_revision = revision
                    if error_label is not None:
                        error_label.set_text("")
                    if self.exit_pending:
                        self.close()
                    else:
                        self.refresh()
                if revision != self.settings_revision:
                    self.save_async(error_label)
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
        dialog = Gtk.Window(title="Settings", transient_for=self, modal=True, default_width=460, icon_name="biz.umbertogiacobbi.OpenAIUsage")
        self.settings_dialog = dialog
        def closed(*_):
            self.settings_dialog = None
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
        alerts = {}
        for threshold in (50, 20, 5):
            check = Gtk.CheckButton(label=f"Notify at {threshold}% remaining", active=self.settings.get(f"alert{threshold}", False))
            alerts[threshold] = check
            content.append(check)
        error = text(self.settings_error or "", "warning")
        content.append(error)
        def apply(*_):
            path = executable.get_text().strip()
            if not path:
                error.set_text("Choose a Codex executable.")
                path = self.settings.get("codexPath", "codex")
            selected = timezone.get_selected()
            if selected >= len(zones):
                return
            updated = dict(self.settings, codexPath=path, timeZoneId=zones[selected], bigSize=big_size.get_active(),
                           **{f"alert{threshold}": check.get_active() for threshold, check in alerts.items()})
            if updated != self.settings:
                self.settings = updated
                self.settings_revision += 1
                self.apply_size()
                self.check_alerts()
                self.queue_settings_save(error)
        executable.connect("changed", apply)
        timezone.connect("notify::selected", apply)
        big_size.connect("notify::active", apply)
        for check in alerts.values():
            check.connect("toggled", apply)
        save = Gtk.Button(label="Save settings")
        def save_explicitly(*_):
            self.settings_revision += 1
            self.save_async(error)
        save.connect("clicked", save_explicitly)
        content.append(save)
        close = Gtk.Button(label="Close")
        close.connect("clicked", lambda *_: dialog.close())
        content.append(close)
        dialog.set_child(content)
        dialog.present()
