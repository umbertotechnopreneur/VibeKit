"""Verify native settings remain editable after a corrupt configuration, with a synthetic backend."""
from pathlib import Path
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Linux"))
import openai_usage as widget

saved = []
message = "Saved settings are invalid; the original file will be preserved on save."


def invoke(command="snapshot", value=None):
    if command == "settings-status":
        return {"settings": {"codexPath": "codex", "timeZoneId": "UTC"},
                "recoveryMessage": None if saved else message}
    if command == "save-settings":
        saved.append(dict(value))
        return {"ok": True}
    return {"connected": False, "status": "Synthetic configuration recovery"}


def descendants(widget):
    yield widget
    child = widget.get_first_child()
    while child is not None:
        yield from descendants(child)
        child = child.get_next_sibling()


widget.invoke = invoke
app = widget.UsageApp()
app.set_flags(widget.Gio.ApplicationFlags.NON_UNIQUE)
started = time.monotonic()
phase = 0
failure = None


def poll():
    global phase, failure
    try:
        if time.monotonic() - started > 10:
            raise AssertionError("Recovery panel check timed out")
        window = app.window
        if phase == 0 and window is not None and window.settings_ready:
            assert window.settings_error == message
            window.show_settings()
            dialog = window.settings_panel
            assert dialog is not None and dialog.get_visible()
            window.show_settings()
            assert window.settings_panel is dialog
            controls = list(descendants(dialog))
            assert any(isinstance(control, widget.Gtk.Label) and control.get_text() == message for control in controls)
            assert any(isinstance(control, widget.Gtk.Label) and control.get_text() == "Changes are saved automatically."
                       for control in controls)
            assert not any(isinstance(control, widget.Gtk.Button) and control.get_label() in ("Save settings", "Close")
                           for control in controls)
            fields = next(control for control in controls if isinstance(control, widget.Adw.ExpanderRow)
                          and control.get_title() == "Widget fields")
            fields.set_expanded(True)
            label = next(control for control in descendants(fields)
                         if isinstance(control, widget.Gtk.Label) and control.get_text() == "Remaining Credits")
            toggle = label.get_parent().get_next_sibling()
            assert isinstance(toggle, widget.Gtk.Switch)
            toggle.set_active(False)
            phase = 1
        elif phase == 1 and saved and not window.saving:
            assert window.settings_error is None
            assert window.settings_panel.get_visible()
            window.hide_settings()
            window.exit_application()
            phase = 2
        return True
    except Exception as error:
        failure = error
        app.quit()
        return False


widget.GLib.timeout_add(50, poll)
result = app.run(["openai-usage-settings-recovery-check"])
assert failure is None, str(failure)
assert result == 0 and phase == 2 and saved[-1]["timeZoneId"] == "UTC" and saved[-1]["showCredits"] is False
print("PASS native GTK recovery message, reusable settings panel and autosave while preferences remain open")
