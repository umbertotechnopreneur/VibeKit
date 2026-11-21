"""Check live GTK display and preferences against a synthetic quota snapshot."""
from pathlib import Path
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Linux"))
import openai_usage as widget


expiry = int(time.time()) + 86_400
snapshot = {
    "connected": True, "remaining": 60, "selectedWindow": "codex:weekly", "creditsBalance": 170,
    "creditsLabel": "170", "resetVoucherCount": 2, "forecastAvailable": True, "forecastCompact": "3d 21h",
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
            if not view.tiles[0].face.get_width():
                return True
            assert view.brand.get_next_sibling() == view.signal
            manifesto = next(control for control in descendants(view.strip) if isinstance(control, widget.Gtk.LinkButton))
            assert manifesto.get_uri() == widget.MANIFESTO_URL
            assert manifesto.get_prev_sibling().get_tooltip_text() == "About OpenAI Usage"
            assert manifesto.get_next_sibling().get_tooltip_text() == "Close"
            assert not view.details_button.get_visible() and not view.details_band.get_visible()
            assert view.signal.active == 2
            assert view.signal.face.get_height() > view.allowance_tile.face.get_height()
            assert view.signal.face.get_width() >= 30
            view.signal.next_flicker = time.monotonic() - 1
            view.signal.maybe_flicker(True)
            assert view.signal.flicker_started is not None and view.signal.active == 2
            view.signal.maybe_flicker(False)
            assert view.signal.flicker_started is None and view.signal.active == 2
            assert view.signal.next_flicker - time.monotonic() >= 479
            assert view.voucher_tile.value == "02"
            assert view.reset_tile.value == "30/09 09:44"
            assert view.estimate_tile.caption.get_text() == "Est. time Left"
            tiles = (*view.metrics, *view.tiles)
            hinges = [tile.face.compute_bounds(view.strip)[1].get_y() + tile.face.get_height() / 2 for tile in tiles]
            assert max(hinges) - min(hinges) <= 1, hinges
            assert all(tile.get_first_child() == tile.caption for tile in tiles)
            size = view.get_width()
            view.clock_cycle_start -= 10.1
            view.update_clock(86350)
            assert view.clock_views.get_visible_child() == view.calendar_row
            assert [tile.value for tile in view.calendar_tiles] == ["WEDNESDAY", "30", "SEPTEMBER", "2026"]
            stage = 1
        elif stage == 1 and not view.clock_views.get_transition_running():
            assert view.get_width() == size
            view.clock_cycle_start -= 10
            view.update_clock(86350)
            assert view.clock_views.get_visible_child() == view.tile_row
            view.play_alert_sound = lambda: dings.append("ding")
            view.state.update(remaining=96, creditsBalance=150)
            view.check_alerts()
            view.check_alerts()
            assert notifications == ["credits-alert"] and dings == ["ding"]
            assert view.signal.active == 2 and "Low credits" in view.signal.face.get_tooltip_text()
            view.state.update(remaining=None)
            view.check_alerts()
            assert view.signal.active is None and dings == ["ding"]
            view.state.update(remaining=19)
            view.check_alerts()
            assert view.signal.active == 1
            assert notifications == ["credits-alert", "allowance-alert"] and len(dings) == 2
            view.state.update(creditsBalance=160)
            view.check_alerts()
            assert view.signal.active == 1
            view.state.update(remaining=40)
            view.check_alerts()
            view.state.update(remaining=19)
            view.check_alerts()
            assert len(dings) == 3
            view.show_settings()
            controls = tuple(descendants(view.settings_dialog))
            positions = [control for control in controls if isinstance(control, widget.Gtk.DropDown)][-1]
            positions.set_selected(1)
            assert all(tile.get_first_child() == tile.display for tile in (view.signal, *view.metrics, *view.tiles))
            positions.set_selected(2)
            assert not any(tile.caption.get_visible() for tile in (view.signal, *view.metrics, *view.tiles))
            toggles = {control.get_label(): control for control in controls if isinstance(control, widget.Gtk.CheckButton)}
            for label, tile in (("Show Available resets", view.voucher_tile), ("Show Credits left", view.credits_tile),
                                ("Show Est. time Left", view.estimate_tile)):
                toggles[label].set_active(False)
                assert not tile.get_visible()
            assert view.reset_tile.get_visible() and view.allowance_tile.get_visible()
            toggles["Play an alert ding"].set_active(False)
            view.state.update(remaining=4)
            view.check_alerts()
            assert view.signal.active == 0 and len(dings) == 3
            limit = next(control for control in controls if isinstance(control, widget.Gtk.SpinButton))
            limit.set_text("125")
            assert view.settings["creditsAlertThreshold"] == 125
            toggles["Enable usage details bar"].set_active(True)
            view.settings_dialog.close()
            stage = 2
        elif stage == 2 and view.saved_settings_revision == view.settings_revision:
            assert stored["labelPosition"] == "hidden"
            assert stored["alertSound"] is False and stored["creditsAlertThreshold"] == 125
            assert not any(stored[key] for key in ("showAvailableResets", "showCredits", "showEstimatedTime"))
            assert view.get_width() < size, (view.get_width(), size)
            assert stored["enableDetailsBar"] and view.details_button.get_visible() and not view.details_band.get_visible()
            view.details_button.set_active(True)
            assert view.details_band.get_visible()
            assert view.detail_tiles[0].value == "72%" and view.detail_tiles[1].value == "01:42"
            assert view.detail_tiles[2].value == "20%" and view.detail_tiles[3].value == "30/09 10:44"
            selector = next(item for item in descendants(view.details_band) if isinstance(item, widget.Gtk.DropDown))
            selector.set_selected(1)
            assert view.detail_tiles[-1].value == "RUNS OUT 6H BEFORE RESET" and view.detail_tiles[-1].ink != (1, 1, 1)
            assert view.details_key_pressed(None, widget.Gdk.KEY_Escape) and not view.details_band.get_visible()
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
