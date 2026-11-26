"""Check live GTK display and preferences against a synthetic quota snapshot."""
from pathlib import Path
import locale
import sys
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Linux"))
import openai_usage as widget


expiry = int(time.time()) + 86_400
snapshot = {
    "connected": True, "remaining": 60, "selectedWindow": "codex:weekly", "creditsBalance": 170,
    "creditsLabel": "170", "resetVoucherCount": 3,
    "forecastAvailable": True, "forecastCompact": "3d 21h",
    "windows": [{"key": "codex:weekly", "minutes": 10080}],
    "resetVoucherExpirations": [
        {"expiresAt": expiry + index * 86400, "value": f"0{index}/10 10:00", "detail": f"October {index} · 10:00 UTC"}
        for index in range(1, 4)
    ],
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


def check_credit_meter():
    # Sample the rendered glass itself so reversing the drawing order is caught.
    tile = widget.CreditsPercentageTile()
    for width in (tile.MINIMUM_WIDTH, tile.base_width):
        tile.resize(1, width)
        meter_width = tile.meter.get_content_width()
        height = tile.meter.get_content_height()
        centers = [round(4 + (i + .5) * (meter_width - 8) / 16) for i in range(16)]

        def lens_pixels(percent):
            tile.set_percentage(percent, False)
            surface = widget.cairo.ImageSurface(widget.cairo.FORMAT_ARGB32, meter_width, height)
            tile.draw_meter(tile.meter, widget.cairo.Context(surface), meter_width, height)
            surface.flush()
            row = surface.get_data()[height // 2 * surface.get_stride():]
            return [bytes(row[4*x:4*x+4]) for x in centers]

        off, lit = lens_pixels(None), lens_pixels(100)
        assert all(dark != light for dark, light in zip(off, lit))
        assert lens_pixels(42) == off[:9] + lit[9:]
        assert tile.value == "42%"
        assert lens_pixels(6.25) == off[:15] + lit[15:]
        assert tile.value == locale.format_string("%g", 6.3) + "%"


def assert_resize_edges(view):
    """Check hit-testing and that every edge is passed to the native resize API."""
    class Area:
        def get_width(self): return 200
        def get_height(self): return 100
        def translate_coordinates(self, _view, x, y): return (x, y)

    class Surface:
        def __init__(self): self.calls = []
        def begin_resize(self, *args): self.calls.append(args)

    class Gesture:
        def __init__(self, area): self.area, self.state = area, None
        def get_widget(self): return self.area
        def get_current_event_device(self): return device
        def get_current_event_time(self): return 123
        def set_state(self, state): self.state = state

    class Window:
        closed = False
        resize_edge = staticmethod(widget.UsageWindow.resize_edge)
        def get_surface(self): return surface
        def cancel_fit_height(self): pass
        def get_surface_transform(self): return (0, 0)

    area, surface, device, window = Area(), Surface(), object(), Window()
    points = ((1, 1, widget.Gdk.SurfaceEdge.NORTH_WEST), (100, 1, widget.Gdk.SurfaceEdge.NORTH),
              (199, 1, widget.Gdk.SurfaceEdge.NORTH_EAST), (1, 50, widget.Gdk.SurfaceEdge.WEST),
              (199, 50, widget.Gdk.SurfaceEdge.EAST), (1, 99, widget.Gdk.SurfaceEdge.SOUTH_WEST),
              (100, 99, widget.Gdk.SurfaceEdge.SOUTH), (199, 99, widget.Gdk.SurfaceEdge.SOUTH_EAST))
    for x, y, edge in points:
        assert view.resize_edge(area, x, y) == edge
        gesture = Gesture(area)
        widget.UsageWindow.begin_move(window, gesture, 1, x, y)
        assert gesture.state == widget.Gtk.EventSequenceState.CLAIMED
        assert surface.calls[-1] == (edge, device, widget.Gdk.BUTTON_PRIMARY, x, y, 123)
    assert view.resize_edge(area, 100, 50) is None


widget.invoke = invoke
app = widget.UsageApp()
app.set_flags(widget.Gio.ApplicationFlags.NON_UNIQUE)
notifications = []
dings = []
app.send_notification = lambda name, _: notifications.append(name)
stage = 0
size = None
stable_after = 0
widths_to_check = [450, 650, 850, 1000, 1200, 1600, 650]
row_counts = []
width_retry_at = None
single_row_width = None
started = time.monotonic()
failure = None


def request_test_width(view, width):
    global stable_after, width_retry_at
    view.set_default_size(width, view.get_surface().get_height())
    stable_after = time.monotonic() + 0.15
    width_retry_at = time.monotonic() + 0.5


def poll():
    global stage, size, stable_after, width_retry_at, single_row_width, failure
    try:
        if time.monotonic() - started > 12:
            raise AssertionError(f"Native widget check timed out at stage {stage}, surface {(app.window.get_surface().get_width(), app.window.get_surface().get_height())}, rows {row_counts}")
        view = app.window
        if stage == 0 and view and view.settings_ready and not view.busy and view.get_mapped():
            tiles = list(widget.WrapLayout.children(view.strip))
            if not view.reset_clock.face.get_width():
                return True
            assert not view.compact_switch.get_active(), "Widget must initially show the expanded view"
            assert tiles == [*view.metrics, view.reset_clock]
            assert view.menu_button.get_mapped() and view.menu_button.get_icon_name() == "view-more-symbolic"
            assert list(widget.WrapLayout.children(view.body))[-1] is view.menu_button
            items = list(widget.WrapLayout.children(view.widget_menu.get_child()))
            rows = [item.get_child() if isinstance(item, widget.Gtk.Button) else item for item in items]
            assert [next(child.get_text() for child in widget.WrapLayout.children(row)
                         if isinstance(child, widget.Gtk.Label)) for row in rows] == [
                "Pin", "Compact mode", "Settings", "About", "VibeWare", "Exit"]
            assert isinstance(rows[1].get_last_child(), widget.Gtk.Switch)
            assert len({row.get_first_child().get_icon_name() for row in rows}) == len(rows)
            assert not any(isinstance(control, widget.Gtk.LinkButton) for control in descendants(view.body))
            assert len({tile.layout.font_size for tile in tiles}) == 1
            assert all(tile.layout.row_height == 30 for tile in tiles)
            percentage = view.credits_percentage_tile
            assert tiles.index(percentage) == tiles.index(view.credits_tile) + 1
            assert percentage.display.get_first_child() is percentage.face
            assert percentage.display.get_last_child() is percentage.meter
            assert percentage.meter.get_content_height() == percentage.layout.row_height
            assert percentage.value == "100%" and percentage.active_lenses == 16
            assert percentage.LENS_COLORS.count(percentage.LENS_COLORS[-1]) == 2
            assert percentage.LENS_COLORS.count(percentage.LENS_COLORS[-3]) == 5
            view.update_metrics(dict(snapshot, creditsBalance=93.75))
            assert percentage.value == locale.format_string("%g", 62.5) + "%"
            assert percentage.active_lenses == 10 and view.credits_tile.progress is None
            view.update_metrics(dict(snapshot, creditsBalance=1.275, creditsBalanceExact="1.275"))
            assert percentage.value == locale.format_string("%g", .9) + "%" and percentage.active_lenses == 1
            check_credit_meter()
            view.update_metrics(dict(snapshot, creditsBalance=.15))
            assert percentage.value == locale.format_string("%g", .1) + "%" and percentage.active_lenses == 1
            view.update_metrics(dict(snapshot, creditsBalance=None))
            assert percentage.value == "—" and percentage.active_lenses == 0
            view.update_metrics(dict(snapshot, creditsBalance=0))
            assert percentage.value == "0%" and percentage.active_lenses == 0
            view.update_metrics(dict(snapshot, creditsBalance=15))
            assert percentage.value == "10%" and percentage.active_lenses == 2
            assert "Credit alert threshold" in percentage.meter.get_tooltip_text()
            view.update_metrics(snapshot)
            assert_resize_edges(view)
            assert view.signal.last_remaining == snapshot["remaining"]
            view.signal.next_flicker = time.monotonic() - 1
            view.signal.maybe_flicker(True)
            assert view.signal.next_flicker - time.monotonic() >= 299
            assert view.estimate_tile.caption.get_text() == "Time to run out"
            assert view.voucher_tile.value == "3" and view.voucher_tile.caption.get_text() == "Vouchers"
            assert view.voucher_expiry_tile.value == "01/10 10:00"
            assert view.voucher_expiry_tile.caption.get_text() == "Next voucher expiry"
            assert "October 1" in view.voucher_expiry_tile.face.get_tooltip_text()
            view.state["resetVoucherExpirations"] = [{"expiresAt": None, "value": "—", "detail": "Date unavailable"}]
            view.update_voucher_expiry(int(time.time()))
            assert view.voucher_expiry_tile.value == "—"
            view.state["resetVoucherExpirations"] = snapshot["resetVoucherExpirations"]
            view.update_voucher_expiry(expiry + 86400)
            assert view.voucher_expiry_tile.value == "02/10 10:00"
            assert view.reset_clock.caption.get_text() == "Auto reset clock"
            assert "weekly quota" in view.reset_clock.face.get_tooltip_text()
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
            assert "Voucher 3: October 3 · 10:00 UTC" in view.voucher_tile.expirations.get_text()
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
            view.settings_item.emit("clicked")
            stage = 1.5
        elif (stage == 1.5 and not view.fit_source and view.settings_window.get_height() > 200
              and view.widget_preview.get_mapped()):
            dialog = view.settings_window
            assert dialog.get_transient_for() is view
            assert not dialog.get_modal() and not dialog.get_resizable()
            assert dialog.get_default_size()[0] == 540 and view.get_width() == size
            assert view.widget_preview.get_next_sibling() is None
            controls = tuple(descendants(dialog.get_child())) + tuple(descendants(dialog.get_titlebar()))
            sections = {control.get_title(): control for control in controls if isinstance(control, widget.Adw.ExpanderRow)}
            assert list(sections) == ["Appearance", "Widget fields", "Alerts", "Codex connection", "Desktop"]
            assert not any(section.get_expanded() for section in sections.values())
            appearance, fields, alerts = (sections[name] for name in ("Appearance", "Widget fields", "Alerts"))
            appearance.set_expanded(True)
            positions = next(control for control in descendants(appearance)
                             if isinstance(control, widget.Adw.ComboRow) and control.get_title() == "Field labels")
            positions.set_selected(1)
            assert all(tile.get_first_child() == tile.display for tile in view.metrics)
            positions.set_selected(2)
            assert not any(tile.caption.get_visible() for tile in view.metrics)
            assert not view.reset_clock.caption.get_visible()
            fields.set_expanded(True)
            assert fields.get_expanded() and not appearance.get_expanded()
            assert positions.get_selected() == 2 and view.widget_preview.get_mapped()
            toggles = {}
            for control in descendants(fields):
                if isinstance(control, widget.Gtk.Switch) and (label_group := control.get_prev_sibling()) is not None:
                    title = next((node for node in descendants(label_group) if isinstance(node, widget.Gtk.Label)), None)
                    if title is not None:
                        toggles[title.get_text()] = control
            for name, tile in (("Available vouchers", view.voucher_tile), ("Remaining Credits", view.credits_tile),
                               ("Time to run out", view.estimate_tile)):
                toggles[name].set_active(False)
                assert tile.get_parent() is None
            assert view.credits_percentage_tile.get_parent() is view.strip
            toggles["Remaining Credits Percentage"].set_active(False)
            assert view.credits_percentage_tile.get_parent() is None
            view.update_metrics(dict(snapshot, creditsBalance=75))
            assert view.credits_percentage_tile.value == "50%"
            view.update_metrics(snapshot)
            assert view.credits_percentage_tile.value == "100%"
            assert view.voucher_expiry_tile.get_parent() is view.strip
            alerts.set_expanded(True)
            assert alerts.get_expanded() and not fields.get_expanded()
            alert_sound = next(control for control in descendants(alerts)
                               if isinstance(control, widget.Adw.SwitchRow)
                               and control.get_title() == "Play a sound for alerts")
            alert_sound.set_active(False)
            view.state.update(remaining=4)
            view.check_alerts()
            assert view.signal.last_remaining == 4 and len(dings) == 3
            limit = next(control for control in descendants(alerts) if isinstance(control, widget.Gtk.Entry)
                         and control.get_placeholder_text() == "Example: 1000")
            limit.set_text("250")
            assert view.credits_percentage_tile.value == "64%"
            limit.set_text("125")
            assert view.settings["creditsAlertThreshold"] == 125
            assert view.credits_percentage_tile.value == "100%"
            appearance.set_expanded(True)
            assert appearance.get_expanded() and not alerts.get_expanded() and positions.get_selected() == 2
            details = next(control for control in descendants(appearance)
                           if isinstance(control, widget.Adw.SwitchRow) and control.get_title() == "Enable usage details")
            details.set_active(True)
            appearance.set_expanded(False)
            assert not any(section.get_expanded() for section in sections.values())
            assert view.widget_preview.get_mapped()
            view.update_settings(fieldOrder=["forecast", "allowance"])
            done = next(control for control in controls if isinstance(control, widget.Gtk.Button)
                        and control.get_label() == "Done")
            done.emit("clicked")
            assert view.settings_window is None
            stage = 2
        elif stage == 2 and view.saved_settings_revision == view.settings_revision and not view.fit_source:
            assert stored["labelPosition"] == "hidden"
            assert stored["alertSound"] is False and stored["creditsAlertThreshold"] == 125
            assert not any(stored[key] for key in ("showAvailableResets", "showCredits", "showEstimatedTime"))
            assert "credits-percentage" in stored["hiddenFields"]
            assert view.get_width() == size
            assert stored["enableDetailsBar"] and view.menu_button.get_visible() and not view.compact_switch.get_active()
            assert view.strip.get_first_child() == view.detail_tiles[-1]
            assert stored["fieldOrder"] == ["forecast", "allowance"]
            assert view.detail_tiles[0].value == "72%" and view.detail_tiles[1].value == "01h 42m"
            assert view.detail_tiles[2].value == "20%" and view.detail_tiles[3].value == "30/09 10:44"
            assert view.detail_tiles[4].value == "3d 21h" and view.detail_tiles[5].value == "01d 00h"
            assert view.detail_tiles[-1].value == "RUNS OUT 6h BEFORE RESET" and view.detail_tiles[-1].ink != (1, 1, 1)
            view.state["fiveHourAllowance"]["remaining"] = 73
            view.state["forecastCompact"] = "4d"
            view.update_details(int(time.time()))
            assert view.detail_tiles[0].value == "73%" and view.detail_tiles[4].value == "4d"
            assert view.details_key_pressed(None, widget.Gdk.KEY_Escape) and view.compact_switch.get_active()
            tiles = list(widget.WrapLayout.children(view.strip))
            assert tiles == [view.allowance_tile, view.voucher_expiry_tile, view.reset_clock]
            assert not view.signal.get_visible()
            view.update_settings(showAvailableResets=True, showCredits=True, showEstimatedTime=True, hiddenFields=[],
                                 fieldOrder=["forecast", "clock", "allowance"])
            assert list(widget.WrapLayout.children(view.strip)) == [
                view.reset_clock, view.allowance_tile, view.credits_tile, view.credits_percentage_tile,
                view.estimate_tile, view.voucher_tile, view.voucher_expiry_tile]
            view.compact_switch.set_active(False)
            stable_after = time.monotonic() + 0.3
            stage = 2.5
        elif stage == 2.5 and time.monotonic() >= stable_after:
            request_test_width(view, widths_to_check[0])
            stage = 3
        elif stage == 3 and time.monotonic() >= stable_after:
            expected = widths_to_check[len(row_counts)]
            if view.get_surface().get_width() != expected:
                # A compositor configure can supersede a programmatic resize.
                if width_retry_at is not None and time.monotonic() >= width_retry_at:
                    request_test_width(view, expected)
                    width_retry_at = None
                return True
            tiles = list(widget.WrapLayout.children(view.strip))
            assert tiles[0] is view.detail_tiles[-1] and tiles[1] is view.reset_clock
            allocations = [tile.get_allocation() for tile in tiles]
            width = view.strip.get_width()
            assert width > 0 and all(r.x >= 0 and r.x + r.width <= width for r in allocations), (view.get_surface().get_width(), width, view.menu_button.get_width(), view.signal.get_width(), view.get_size_request(), [(r.x, r.width) for r in allocations])
            # Each tile follows its predecessor until the remaining line cannot contain it.
            for previous, current in zip(allocations, allocations[1:]):
                if current.x == 0:
                    assert current.y > previous.y and previous.x + previous.width + 8 + current.width > width
                else:
                    assert current.x == previous.x + previous.width + 8 and current.y == previous.y
            rows = sum(r.x == 0 for r in allocations)
            assert view.menu_button.get_mapped()
            if rows > 1:
                assert view.signal.get_mapped()
            assert view.menu_button.get_allocation().y + view.menu_button.get_height() <= view.get_height()
            assert len({tile.layout.font_size for tile in tiles}) == 1
            row_counts.append(rows)
            if len(row_counts) < len(widths_to_check):
                request_test_width(view, widths_to_check[len(row_counts)])
                return True
            assert row_counts[:6] == sorted(row_counts[:6], reverse=True) and row_counts[0] > row_counts[5] >= 2, row_counts
            assert row_counts[1] == row_counts[-1]
            print(f"Verified window widths {widths_to_check} and row counts {row_counts}")
            stage = 4
            view.compact_switch.set_active(True)
            stable_after = time.monotonic() + 0.3
        elif stage == 4 and time.monotonic() >= stable_after:
            assert view.get_surface().get_width() >= view.get_size_request().width
            assert view.get_surface().get_height() < 100
            tiles = list(widget.WrapLayout.children(view.strip))
            assert all(tile.get_allocation().y == 0 for tile in tiles)
            assert sum(tile.get_width() for tile in tiles) + 8 * (len(tiles) - 1) <= view.strip.get_width()
            view.compact_switch.set_active(False)
            stable_after = time.monotonic() + 0.3
            stage = 5
        elif stage == 5 and time.monotonic() >= stable_after:
            assert view.get_surface().get_width() == widths_to_check[-1]
            assert view.signal.get_mapped() and view.menu_button.get_mapped()
            # Request a taller window before running the periodic layout update.
            view.set_default_size(850, 700)
            stage = 6
        elif stage == 6 and (view.get_surface().get_width(), view.get_surface().get_height()) == (850, 700):
            view.fit_layout(850)
            view.update_details(int(time.time()))
            stable_after = time.monotonic() + 0.3
            stage = 7
        elif stage == 7 and time.monotonic() >= stable_after:
            assert (view.get_surface().get_width(), view.get_surface().get_height()) == (850, 700), (view.get_surface().get_width(), view.get_surface().get_height())
            view.compact_switch.set_active(True)
            stable_after = time.monotonic() + 0.3
            stage = 8
        elif stage == 8 and time.monotonic() >= stable_after:
            assert view.get_surface().get_height() < 100
            assert view.get_surface().get_height() >= view.menu_button.get_height()
            assert not view.widget_preview.get_vscrollbar().get_child_visible()
            assert not view.signal.get_visible()
            view.compact_switch.set_active(False)
            stable_after = time.monotonic() + 0.3
            stage = 9
        elif stage == 9 and time.monotonic() >= stable_after:
            assert (view.get_surface().get_width(), view.get_surface().get_height()) == (850, 700), (view.get_surface().get_width(), view.get_surface().get_height())
            assert view.signal.get_mapped() and view.menu_button.get_mapped()
            view.update_settings(enableDetailsBar=False)
            # Check the wrap boundary for the enabled fields at their natural width.
            chrome_width = view.get_surface().get_width() - view.strip.get_width()
            single_row_width = view.strip.measure(widget.Gtk.Orientation.HORIZONTAL, -1).natural + chrome_width
            view.set_default_size(single_row_width - view.strip.get_layout_manager().spacing, 700)
            stable_after = time.monotonic() + 0.3
            stage = 10
        elif stage == 10 and time.monotonic() >= stable_after:
            tiles = list(widget.WrapLayout.children(view.strip))
            assert len({tile.get_allocation().y for tile in tiles}) == 2
            assert view.signal.get_mapped() and view.menu_button.get_mapped()
            assert view.signal.get_allocation().y <= view.strip.get_allocation().y + view.strip.get_height()
            view.set_default_size(single_row_width + view.strip.get_layout_manager().spacing, 700)
            stable_after = time.monotonic() + 0.3
            stage = 11
        elif stage == 11 and time.monotonic() >= stable_after:
            tiles = list(widget.WrapLayout.children(view.strip))
            assert len({tile.get_allocation().y for tile in tiles}) == 1
            assert view.signal.get_visible()
            assert view.menu_button.get_visible() and not view.compact_switch.get_active()
            # Even a narrow expanded window is a single row when only one metric is enabled.
            view.update_settings(showAvailableResets=False, showCredits=False, showEstimatedTime=False,
                                 hiddenFields=["clock", "first-reset", "credits-percentage"])
            view.set_default_size(450, 700)
            stable_after = time.monotonic() + 0.3
            stage = 12
        elif stage == 12 and time.monotonic() >= stable_after:
            assert view.get_surface().get_width() == 450
            assert list(widget.WrapLayout.children(view.strip)) == [view.allowance_tile]
            assert view.signal.get_visible()
            assert view.menu_button.get_visible() and not view.compact_switch.get_active()
            stage = 13
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
assert result == 0 and stage == 13
print("PASS native GTK resize edges, manual sizing, mini/expanded modes, tile alignment, date transition, alerts and settings")
