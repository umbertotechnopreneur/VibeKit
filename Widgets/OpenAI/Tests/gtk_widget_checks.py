"""Check live GTK display and preferences against a synthetic quota snapshot."""
from pathlib import Path
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Linux"))
import openai_usage as widget


expiry = int(time.time()) + 86_400
snapshot = {
    "connected": True, "remaining": 60, "selectedWindow": "codex:weekly", "creditsBalance": 170,
    "creditsLabel": "170", "resetVoucherCount": 3, "forecastAvailable": True, "forecastCompact": "3d 21h",
    "resetVoucherExpirations": [{"detail": "October 1 · 10:00 UTC"}, {"detail": "October 2 · 10:00 UTC"},
                                {"detail": "October 3 · 10:00 UTC"}],
    "resetsAt": expiry, "resetCalendarDate": "WEDNESDAY 30 SEPTEMBER 2026", "selectedResetCompact": "30/09 10:44",
    "resetDisplays": [{"resetsAt": expiry - 3600, "heading": "First reset", "value": "30/09 09:44", "detail": "Earlier reset"},
                      {"resetsAt": expiry, "heading": "Second reset", "value": "30/09 10:44", "detail": "Selected quota reset"}],
}
snapshot.update(
    fiveHourAllowance={"remaining": 72, "resetsAt": int(time.time()) + 6120, "resetCompact": "30/09 12:26"},
    weeklyAllowance={"remaining": 20, "resetsAt": expiry, "resetCompact": "30/09 10:44"},
    resetForecast={"message": "RUNS OUT 6H BEFORE RESET", "warning": True},
)
stored = {"timeZoneId": "UTC", "alert20": True, "alert5": True, "creditsAlertThreshold": 150}


def invoke(command="snapshot", value=None):
    if command == "settings-status":
        return {"settings": dict(stored)}
    if command == "save-settings":
        stored.update(value)
        return {"ok": True}
    return dict(snapshot)


def descendants(control):
    yield control
    child = control.get_first_child()
    while child:
        yield from descendants(child)
        child = child.get_next_sibling()


widget.invoke = invoke
app = widget.UsageApp()
app.set_flags(widget.Gio.ApplicationFlags.NON_UNIQUE)
notifications = []
dings = []
app.send_notification = lambda name, _: notifications.append(name)
stage = 0
size = None
started = time.monotonic()
failure = None


def poll():
    global stage, size, failure
    try:
        if time.monotonic() - started > 7:
            raise AssertionError("Native widget check timed out")
        view = app.window
        if stage == 0 and view and view.settings_ready and not view.busy and view.get_mapped():
            if not view.reset_clock.face.get_width():
                return True
            project = view.brands.get_first_child()
            assert isinstance(project, widget.Gtk.LinkButton)
            assert project.get_uri() == widget.PROJECT_URL and project.get_child() == view.brand
            manifesto = project.get_next_sibling()
            assert isinstance(manifesto, widget.Gtk.LinkButton)
            assert manifesto.get_uri() == widget.MANIFESTO_URL
            assert view.brand.get_width() >= view.APP_ICON_SIZE
            assert manifesto.get_child() == view.manifesto_mark
            assert view.manifesto_mark.get_width() >= view.MANIFESTO_ICON_SIZE
            assert view.manifesto_mark.get_height() >= view.MANIFESTO_ICON_SIZE
            assert not view.manifesto_mark.get_can_target()
            assert not view.details_button.get_visible()
            assert view.signal.last_remaining == snapshot["remaining"]
            assert 80 <= view.signal.get_height() <= 128
            assert view.signal.get_width() >= 30
            view.signal.next_flicker = time.monotonic() - 1
            view.signal.maybe_flicker(True)
            assert view.signal.next_flicker - time.monotonic() >= 299
            assert view.estimate_tile.caption.get_text() == "Estimated time remaining"
            assert view.voucher_tile.value == "3"
            assert view.reset_tile.value == "30/09 09:44"
            assert view.reset_clock.caption.get_text() == "Next reset date"
            assert view.reset_clock.caption.get_visible()
            assert view.reset_clock.get_first_child() == view.reset_clock.caption
            size = view.get_width()
            assert not view.voucher_tile.count_button.get_popover().get_visible()
            assert view.voucher_tile.count_button.activate()
            view.clock_cycle_start -= 10.1
            view.update_clock(86350)
            assert view.reset_clock.value == "WEDNESDAY 30 SEPTEMBER 26"
            stage = 1
        elif stage == 1 and view.voucher_tile.count_button.get_popover().get_visible():
            assert view.get_width() == size
            assert "Reset 3: October 3 · 10:00 UTC" in view.voucher_tile.expirations.get_text()
            view.voucher_tile.count_button.popdown()
            view.clock_cycle_start -= 10
            view.update_clock(86350)
            assert view.reset_clock.value == "00d 23h 59m 10s"
            view.play_alert_sound = lambda: dings.append("ding")
            view.state.update(remaining=96, creditsBalance=150)
            view.check_alerts()
            view.check_alerts()
            assert notifications == ["credits-alert"] and dings == ["ding"]
            assert view.signal.last_remaining == 96 and "Low credits" in view.signal.get_tooltip_text()
            view.state.update(remaining=None)
            view.check_alerts()
            assert view.signal.last_remaining is None and dings == ["ding"]
            view.state.update(remaining=19)
            view.check_alerts()
            assert view.signal.last_remaining == 19
            assert notifications == ["credits-alert", "allowance-alert"] and len(dings) == 2
            view.state.update(creditsBalance=160)
            view.check_alerts()
            assert view.signal.last_remaining == 19
            view.state.update(remaining=40)
            view.check_alerts()
            view.state.update(remaining=19)
            view.check_alerts()
            assert len(dings) == 3
            view.show_settings()
            controls = tuple(descendants(view.settings_dialog))
            positions = [control for control in controls if isinstance(control, widget.Gtk.DropDown)][1]
            positions.set_selected(1)
            assert all(tile.get_first_child() == tile.display for tile in view.metrics)
            positions.set_selected(2)
            assert not any(tile.caption.get_visible() for tile in view.metrics)
            assert not view.reset_clock.caption.get_visible()
            toggles = {}
            for control in controls:
                if isinstance(control, widget.Gtk.Switch) and (label_group := control.get_prev_sibling()) is not None:
                    title = next((node for node in descendants(label_group) if isinstance(node, widget.Gtk.Label)), None)
                    if title is not None:
                        toggles[title.get_text()] = control
            for name, tile in (("Resets available", view.voucher_tile), ("Remaining Credits", view.credits_tile),
                               ("Estimated time remaining", view.estimate_tile)):
                toggles[name].set_active(False)
                assert tile.get_parent() is None
            assert view.reset_tile.get_parent() is view.strip
            toggles["Play a sound for alerts"].set_active(False)
            view.state.update(remaining=4)
            view.check_alerts()
            assert view.signal.last_remaining == 4 and len(dings) == 3
            limit = next(control for control in controls if isinstance(control, widget.Gtk.Entry)
                         and control.get_placeholder_text() == "Example: 1000")
            limit.set_text("125")
            assert view.settings["creditsAlertThreshold"] == 125
            toggles["Enable usage details"].set_active(True)
            view.update_settings(fieldOrder=["forecast", "allowance"])
            view.settings_dialog.close()
            stage = 2
        elif stage == 2 and view.saved_settings_revision == view.settings_revision:
            assert stored["labelPosition"] == "hidden"
            assert stored["alertSound"] is False and stored["creditsAlertThreshold"] == 125
            assert not any(stored[key] for key in ("showAvailableResets", "showCredits", "showEstimatedTime"))
            assert view.get_width() == size
            assert stored["enableDetailsBar"] and view.details_button.get_visible() and view.details_button.get_active()
            assert view.strip.get_first_child() == view.detail_tiles[-1]
            assert stored["fieldOrder"] == ["forecast", "allowance"]
            assert view.detail_tiles[0].value == "72%" and view.detail_tiles[1].value == "01:42"
            assert view.detail_tiles[2].value == "20%" and view.detail_tiles[3].value == "30/09 10:44"
            assert view.detail_tiles[4].value == "3d 21h" and view.detail_tiles[5].value == "01d 00h"
            assert view.detail_tiles[-1].value == "RUNS OUT 6h BEFORE RESET" and view.detail_tiles[-1].ink != (1, 1, 1)
            view.state["fiveHourAllowance"]["remaining"] = 73
            view.state["forecastCompact"] = "4d"
            view.update_details(int(time.time()))
            assert view.detail_tiles[0].value == "73%" and view.detail_tiles[4].value == "4d"
            assert view.details_key_pressed(None, widget.Gdk.KEY_Escape) and not view.details_button.get_active()
            stage = 3
            view.exit_application()
            return False
        return True
    except Exception as error:
        failure = error
        app.quit()
        return False


widget.GLib.timeout_add(50, poll)
result = app.run(["openai-usage-test-widget"])
if failure is not None:
    raise failure
assert result == 0 and stage == 3
print("PASS native GTK tile alignment, date transition, light and ding thresholds, and independent saved controls")
