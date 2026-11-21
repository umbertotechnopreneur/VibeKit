#!/usr/bin/env python3
"""Native GTK 4 / libadwaita presentation for the shared C# account reader."""
from __future__ import annotations

import ctypes
import ctypes.util
import datetime as dt
import fcntl
import math
import os
from pathlib import Path
import random
import shutil
import sys
import time
from watchdog import start as start_watchdog, record as log_event, state_dir
GUARD = None


def report_unhandled(kind, error, traceback):
    log_event("ui.unhandled", kind.__name__ + ": " + str(error))
    sys.__excepthook__(kind, error, traceback)


sys.excepthook = report_unhandled

import gi
gi.require_version("Gtk", "4.0")
gi.require_version("Adw", "1")
from gi.repository import Adw, Gdk, GLib, Gtk, Pango, PangoCairo, Gio
from tray import StatusTray
import cairo

try:
    gi.require_version("GdkX11", "4.0")
    from gi.repository import GdkX11
except (ImportError, ValueError):
    GdkX11 = None

class XClientMessage(ctypes.Structure):
    _fields_ = [("type", ctypes.c_int), ("serial", ctypes.c_ulong), ("send_event", ctypes.c_int),
                ("display", ctypes.c_void_p), ("window", ctypes.c_ulong), ("message_type", ctypes.c_ulong),
                ("format", ctypes.c_int), ("data", ctypes.c_long * 5)]

class XEvent(ctypes.Union):
    _fields_ = [("client", XClientMessage), ("pad", ctypes.c_long * 24)]

def request_x11_above(surface, enabled):
    library = ctypes.util.find_library("X11")
    if not library:
        raise RuntimeError("X11 library is unavailable.")
    x11 = ctypes.CDLL(library)
    x11.XOpenDisplay.argtypes, x11.XOpenDisplay.restype = [ctypes.c_char_p], ctypes.c_void_p
    x11.XCloseDisplay.argtypes = [ctypes.c_void_p]
    x11.XDefaultRootWindow.argtypes, x11.XDefaultRootWindow.restype = [ctypes.c_void_p], ctypes.c_ulong
    x11.XInternAtom.argtypes, x11.XInternAtom.restype = [ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int], ctypes.c_ulong
    x11.XSendEvent.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_long, ctypes.POINTER(XEvent)]
    x11.XFlush.argtypes = [ctypes.c_void_p]
    display = x11.XOpenDisplay(None)
    if not display:
        raise RuntimeError("Cannot connect to the X11 display.")
    try:
        event = XEvent()
        event.client.type, event.client.display, event.client.window = 33, display, surface.get_xid()
        event.client.message_type = x11.XInternAtom(display, b"_NET_WM_STATE", False)
        event.client.format = 32
        event.client.data[0], event.client.data[1], event.client.data[3] = (1 if enabled else 0), x11.XInternAtom(display, b"_NET_WM_STATE_ABOVE", False), 1
        if not x11.XSendEvent(display, x11.XDefaultRootWindow(display), False, (1 << 19) | (1 << 20), ctypes.byref(event)):
            raise RuntimeError("Could not request the X11 window state.")
        x11.XFlush(display)
    finally:
        x11.XCloseDisplay(display)

ROOT = Path(__file__).resolve().parents[1]
MANIFESTO_URL = "https://umbertogiacobbi.biz/vibeware/manifesto"
from backend import invoke
from settings_ui import SettingsUiMixin
from ui_helpers import text, box, about_mark, about_link


CSS = b"""
window { background-color: @window_bg_color; color: @window_fg_color; }
headerbar { background-color: @window_bg_color; box-shadow: none; border: none; }
notebook, notebook > stack, notebook > header { background: transparent; border: none; box-shadow: none; }
notebook > header > tabs > tab { background: transparent; padding: 10px 20px; }
notebook > header > tabs > tab:checked { box-shadow: inset 0 -3px @accent_bg_color; }
.title { font-size: 21px; font-weight: 650; }
.huge { font-family: monospace; font-size: 64px; font-weight: 700; }
.forecast { font-family: monospace; font-size: 32px; font-weight: 700; }
.metric { font-family: monospace; font-size: 30px; font-weight: 650; }
.learning { font-size: 24px; opacity: .75; color: @window_fg_color; }
.placeholder { font-size: 18px; opacity: .7; color: @window_fg_color; }
.mono-detail { font-family: monospace; font-size: 12px; }
window.micro {
    border-radius: 20px;
    border: 1px solid alpha(#bc683d, .62);
    box-shadow: inset 0 0 3px alpha(#bc683d, .12);
}
window.micro:backdrop { border-color: transparent; box-shadow: none; }
window.micro.details-open { background-color: transparent; border-color: transparent; box-shadow: none; }
.micro.details-open .widget-band, .micro .details-band {
    background-color: @window_bg_color;
    border: 1px solid alpha(#bc683d, .62);
    border-radius: 19px;
}
.micro .details-band { padding: 6px 12px; }
.micro .flip-caption { font-size: 12px; opacity: .85; }
.micro.big .flip-caption { font-size: 15px; }
.micro.big button.icon-action { padding: 6px; min-width: 23px; min-height: 23px; }
.micro.big button.icon-action image { -gtk-icon-size: 20px; }
.micro button.icon-action { padding: 5px; min-width: 18px; min-height: 18px; }
.micro button.icon-action image { -gtk-icon-size: 16px; }
.heading { font-size: 15px; }
.dim { opacity: .70; font-size: 12px; }
.warning { color: @window_fg_color; font-weight: 650; }
.day { font-size: 11px; opacity: .7; }
.bar { background: @accent_bg_color; border-radius: 3px 3px 0 0; }
.missing { background: @window_fg_color; opacity: .35; }
.table-group { font-size: 11px; font-weight: 650; opacity: .6; letter-spacing: 1px; }
.table-number { font-family: monospace; font-size: 21px; font-weight: 650; }
.history-bar trough, .history-bar progress { min-height: 4px; }
.chart-number { font-family: monospace; font-size: 11px; font-weight: 650; }
progressbar trough, progressbar progress { min-height: 7px; }
button.icon-action { background: transparent; border: none; box-shadow: none; padding: 9px; min-width: 20px; color: @window_fg_color; }
button.icon-action:hover, button.icon-action:active { color: @window_fg_color; }
button.icon-action:hover { background: alpha(@window_fg_color, .07); }
button.icon-action:active { background: alpha(@accent_bg_color, .15); }
button.icon-action:checked { color: @accent_bg_color; background: alpha(@accent_bg_color, .15); }
window.widget-about, .widget-about .about-body { background-color: #fff4dc; color: #232a2f; }
.widget-about .about-body { font-family: monospace; font-size: 15px; }
.widget-about .about-body label { color: #232a2f; }
.widget-about .about-body .about-heading { font-size: 23px; font-weight: bold; }
.widget-about .about-body .about-note { color: #526573; font-size: 13px; }
.widget-about .about-body .about-link, .widget-about .about-body .about-link label { color: #3a6a96; }
.widget-about .about-body .about-link { padding: 4px; font-size: 13px; }
.widget-about .about-body .about-link:hover { background-color: alpha(#3a6a96, .10); }
"""


class LabeledDisplay(Gtk.Box):
    def __init__(self, caption, display):
        super().__init__(orientation=Gtk.Orientation.VERTICAL, spacing=3, valign=Gtk.Align.CENTER)
        self.display = display
        # The calendar keeps the caption row's height with its four words aligned to the other values.
        self.caption = Gtk.Label(label=caption or "\u00a0", halign=Gtk.Align.CENTER)
        self.caption.add_css_class("flip-caption")
        self.append(self.caption)
        self.append(display)

    def set_label_position(self, position):
        self.caption.set_visible(position != "hidden")
        self.reorder_child_after(self.caption, self.display if position == "below" else None)


class FlipTile(LabeledDisplay):
    def __init__(self, name, caption, width, font_size=21, *, info=False):
        face = Gtk.DrawingArea()
        display = box(4, True)
        display.set_halign(Gtk.Align.CENTER)
        display.append(face)
        super().__init__(caption, display)
        self.face = face
        self.name = name
        self.base_width = width
        self.base_font_size = font_size
        self.info = None
        if info:
            self.info = Gtk.Label(label="ⓘ")
            self.info.add_css_class("dim")
            display.append(self.info)
        self.value = "—"
        self.accent = None
        self.ink = (1, 1, 1)
        self.previous = self.value
        self.started = None
        self.ticking = False
        self.face.set_draw_func(self.draw)
        self.face.update_property([Gtk.AccessibleProperty.LABEL], [f"{name}: unavailable"])
        self.resize(1)

    def resize(self, scale):
        self.face.set_content_width(round(self.base_width * scale))
        self.face.set_content_height(round(34 * scale))
        self.font_size = self.base_font_size * scale
        self.face.queue_draw()

    def set_detail(self, detail):
        if self.face.get_tooltip_text() != detail:
            self.face.set_tooltip_text(detail)
            if self.info is not None:
                self.info.set_tooltip_text(detail)

    def update(self, value, animate, *, reveal=False):
        if value == self.value and not reveal:
            return
        self.previous, self.value = "" if reveal else self.value, value
        accessible_value = "unavailable" if value == "—" else value
        self.face.update_property([Gtk.AccessibleProperty.LABEL], [f"{self.name}: {accessible_value}"])
        self.started = time.monotonic() if animate else None
        self.face.queue_draw()
        if animate and not self.ticking:
            self.ticking = True
            self.face.add_tick_callback(self.tick)

    def set_colors(self, *, accent=None, warning=False):
        ink = (1, .71, .19) if warning else (1, 1, 1)
        if self.ink == ink and self.accent == accent:
            return
        self.ink, self.accent = ink, accent
        self.face.queue_draw()

    def tick(self, widget, frame):
        widget.queue_draw()
        if self.started is None or time.monotonic() - self.started >= .20:
            self.started = None
            self.ticking = False
            return False
        return True

    def draw(self, area, cr, width, height):
        progress = min(1, (time.monotonic()-self.started)/.20) if self.started is not None else 1
        self.draw_face(cr, width, height, self.value)
        if progress < 1 and self.previous != "—":
            # Old bottom stays visible while the top flap falls; new bottom unfolds afterwards.
            self.draw_half(cr, width, height, self.previous, False, 1)
            if progress < .5:
                self.draw_half(cr, width, height, self.previous, True, math.cos(progress*math.pi))
            else:
                self.draw_half(cr, width, height, self.value, False, math.sin((progress-.5)*math.pi))

    def draw_half(self, cr, width, height, value, top, scale):
        cr.save()
        cr.rectangle(0, 0 if top else height/2, width, height/2)
        cr.clip()
        cr.translate(0, height/2)
        cr.scale(1, max(.001, scale))
        cr.translate(0, -height/2)
        self.draw_face(cr, width, height, value)
        cr.restore()

    def draw_face(self, cr, width, height, value):
        cr.save()
        radius = 7
        for cx, cy, a, b in ((width-radius, radius, -math.pi/2, 0),
                              (width-radius, height-radius, 0, math.pi/2),
                              (radius, height-radius, math.pi/2, math.pi),
                              (radius, radius, math.pi, 3*math.pi/2)):
            cr.arc(cx, cy, radius, a, b)
        cr.close_path()
        edge = .02
        gradient = cairo.LinearGradient(0, 0, 0, height)
        gradient.add_color_stop_rgb(0, .15, .15, .15)
        gradient.add_color_stop_rgb(1, .05, .05, .05)
        cr.set_source(gradient)
        cr.fill_preserve()
        cr.set_source_rgb(edge, edge, edge)
        cr.set_line_width(2)
        cr.stroke()
        layout = PangoCairo.create_layout(cr)
        font = Pango.FontDescription("Monospace Bold")
        font.set_absolute_size(self.font_size * Pango.SCALE)
        layout.set_font_description(font)
        layout.set_text(value, -1)
        tw, th = layout.get_pixel_size()
        if tw > width - 10:
            font.set_absolute_size(self.font_size * (width - 10) / tw * Pango.SCALE)
            layout.set_font_description(font)
            tw, th = layout.get_pixel_size()
        cr.move_to((width-tw)/2, (height-th)/2)
        cr.set_source_rgb(*self.ink)
        PangoCairo.show_layout(cr, layout)
        cr.set_source_rgb(edge, edge, edge)
        cr.set_line_width(2)
        cr.move_to(2, height/2)
        cr.line_to(width-2, height/2)
        cr.stroke()
        cr.set_source_rgb(.42, .42, .42)
        for x in (1, width-5):
            cr.rectangle(x, height/2-6, 4, 12)
            cr.fill()
        if self.accent:
            cr.set_source_rgb(*self.accent)
            cr.set_line_width(2)
            cr.set_line_cap(cairo.LINE_CAP_ROUND)
            cr.move_to(width * .19, height - 3)
            cr.line_to(width * .81, height - 3)
            cr.stroke()
        cr.restore()


class TrafficSignal(LabeledDisplay):
    LIGHTS = ((.99, .20, .15), (1, .68, .06), (.22, .81, .39))

    def __init__(self):
        self.face = Gtk.DrawingArea(halign=Gtk.Align.CENTER)
        super().__init__("Alert", self.face)
        self.active = None
        self.flicker_started = None
        self.next_flicker = time.monotonic() + random.uniform(480, 1080)
        self.face.set_draw_func(self.draw)
        self.resize(1)
        self.update(None, "Waiting for allowance or credit data")

    def resize(self, scale):
        self.face.set_content_width(round(30 * scale))
        self.face.set_content_height(round(52 * scale))

    def update(self, active, detail):
        if self.active != active:
            self.active = active
            self.flicker_started = None
            self.face.queue_draw()
        if self.face.get_tooltip_text() != detail:
            self.face.set_tooltip_text(detail)
            self.face.update_property([Gtk.AccessibleProperty.LABEL], [detail])

    def maybe_flicker(self, animations):
        now = time.monotonic()
        if not animations and self.flicker_started is not None:
            self.flicker_started = None
            self.face.queue_draw()
        if now < self.next_flicker:
            return
        self.next_flicker = now + random.uniform(480, 1080)
        if animations and self.active is not None:
            self.flicker_started = now
            self.face.add_tick_callback(self.flicker_frame)

    def flicker_frame(self, area, _):
        if self.flicker_started is not None and time.monotonic() - self.flicker_started >= .55:
            self.flicker_started = None
        area.queue_draw()
        return self.flicker_started is not None

    def draw(self, area, cr, width, height):
        radius = width * .22
        cr.new_sub_path()
        for x, y, angle in ((width - radius - 1, radius + 1, -90), (width - radius - 1, height - radius - 1, 0),
                            (radius + 1, height - radius - 1, 90), (radius + 1, radius + 1, 180)):
            cr.arc(x, y, radius, math.radians(angle), math.radians(angle + 90))
        cr.close_path()
        housing = cairo.LinearGradient(0, 0, width, height)
        housing.add_color_stop_rgb(0, .17, .17, .17)
        housing.add_color_stop_rgb(.55, .08, .08, .08)
        housing.add_color_stop_rgb(1, .12, .12, .12)
        cr.set_source(housing)
        cr.fill_preserve()
        cr.set_line_width(1.25)
        cr.set_source_rgb(.015, .015, .015)
        cr.stroke()

        radius = height * .117
        x = width / 2
        elapsed = time.monotonic() - self.flicker_started if self.flicker_started is not None else 1
        level = 1
        if elapsed < .17:
            level -= .40 * math.sin(math.pi * elapsed / .17)
        elif .28 <= elapsed < .46:
            level -= .25 * math.sin(math.pi * (elapsed - .28) / .18)
        for index, color in enumerate(self.LIGHTS):
            y = height * (.20 + index * .30)
            lit = index == self.active
            if lit:
                glow = cairo.RadialGradient(x, y, radius * .68, x, y, radius * 1.85)
                glow.add_color_stop_rgba(0, *color, .72 * level)
                glow.add_color_stop_rgba(1, *color, 0)
                cr.set_source(glow)
                cr.arc(x, y, radius * 1.85, 0, math.tau)
                cr.fill()

            cr.set_source_rgb(.012, .012, .012)
            cr.arc(x, y, radius * 1.17, 0, math.tau)
            cr.fill()
            lens = cairo.RadialGradient(x - radius * .24, y - radius * .30, radius * .05, x, y, radius)
            for offset, brightness in ((0, 1.30), (.55, 1), (1, .23)):
                intensity = brightness * (level if lit else .36)
                lens.add_color_stop_rgb(offset, *(min(1, channel * intensity) for channel in color))
            cr.set_source(lens)
            cr.arc(x, y, radius, 0, math.tau)
            cr.fill()
            cr.set_source_rgba(1, 1, 1, .54 if lit else .25)
            cr.set_line_width(radius * .16)
            cr.arc(x, y + radius * .03, radius * .72, math.radians(224), math.radians(294))
            cr.stroke()
            cr.fill()


class UsageWindow(SettingsUiMixin, Gtk.ApplicationWindow):
    def __init__(self, app):
        # Adw.ApplicationWindow imposes a minimum height larger than the strip.
        super().__init__(application=app, title="OpenAI Usage", default_height=62,
                         icon_name="biz.umbertogiacobbi.OpenAIUsage")
        self.state = {}
        self.clock_cycle_start = None
        self.settings = {}
        self.settings_ready = False
        self.settings_dialog = None
        self.settings_status_label = None
        self.settings_save_source = 0
        self.backend = invoke
        self.notified_thresholds = set()
        self.alert_window = None
        self.credits_alert_threshold = None
        self.credits_alert_notified = False
        self.sound_process = None
        self.busy = False
        self.refresh_pending = False
        self.settings_revision = 0
        self.saved_settings_revision = 0
        self.settings_error = None
        self.exit_pending = False
        self.saving = False
        self.closed = False
        self.last_reset_refresh = 0.0
        self.allow_close = False
        self.close_dialog = None
        self.about_dialog = None
        self.tray = None
        self.add_css_class("micro")
        self.set_decorated(False)
        self.set_resizable(False)
        self.connect("close-request", self.on_close)

        strip=box(12,True)
        self.strip=strip
        for setter in (strip.set_margin_start,strip.set_margin_end):
            setter(10)
        for setter in (strip.set_margin_top,strip.set_margin_bottom):
            setter(7)
        strip.set_valign(Gtk.Align.CENTER)
        self.brand=Gtk.Image.new_from_file(str(ROOT/"Branding/app-icon-512.png"))
        self.brand.set_pixel_size(25)
        self.brand.set_tooltip_text("Connecting to Codex…")
        strip.append(self.brand)

        self.signal = TrafficSignal()
        strip.append(self.signal)
        self.allowance_tile = FlipTile("Allowance remaining", "Allowance remaining", 115)
        self.voucher_tile = FlipTile("Available resets", "Available resets", 62)
        self.credits_tile = FlipTile("Credits left", "Credits left", 101)
        self.estimate_tile = FlipTile("Est. time Left", "Est. time Left", 100, 18)
        self.reset_tile = FlipTile("First reset", "First reset", 123, 17, info=True)
        self.metrics = (self.allowance_tile, self.voucher_tile, self.credits_tile, self.estimate_tile, self.reset_tile)
        for tile in self.metrics:
            strip.append(tile)

        self.tile_row = box(5, True)
        self.tile_row.set_halign(Gtk.Align.CENTER)
        self.tiles = [FlipTile(name, caption, 63) for name, caption in
                      (("Days", "DAYS"), ("Hours", "HRS"), ("Minutes", "MIN"), ("Seconds", "SEC"))]
        for tile in self.tiles:
            self.tile_row.append(tile)
        self.calendar_row = box(5, True)
        self.calendar_row.set_halign(Gtk.Align.CENTER)
        self.calendar_tiles = [FlipTile(name, None, width, 16) for name, width in
                               (("Weekday", 96), ("Day of month", 32), ("Month", 96), ("Year", 52))]
        for tile in self.calendar_tiles:
            self.calendar_row.append(tile)
        self.clock_views = Gtk.Stack(hhomogeneous=True, vhomogeneous=True, valign=Gtk.Align.CENTER,
                                    transition_type=Gtk.StackTransitionType.CROSSFADE, transition_duration=400)
        self.clock_views.add_child(self.tile_row)
        self.clock_views.add_child(self.calendar_row)
        strip.append(self.clock_views)

        actions=box(0,True)
        self.problem=Gtk.Button(icon_name="dialog-warning-symbolic",tooltip_text="Connection status")
        self.problem.add_css_class("icon-action")
        self.problem.connect("clicked",self.show_settings)
        self.problem.set_visible(False)
        actions.append(self.problem)
        self.pin = Gtk.ToggleButton(icon_name="view-pin-symbolic")
        self.pin.add_css_class("icon-action")
        self.pin.set_sensitive(False)
        self.pin.set_tooltip_text("Always on top is available on X11 with a compatible window manager")
        self.syncing_pin = False
        self.pin.connect("toggled", self.on_pin_toggled)
        self.details_icon = Gtk.DrawingArea(content_width=16, content_height=16,
                                            halign=Gtk.Align.CENTER, valign=Gtk.Align.CENTER)
        self.details_icon.set_draw_func(self.draw_details_icon)
        self.details_button = Gtk.ToggleButton(child=self.details_icon, tooltip_text="Usage details", valign=Gtk.Align.CENTER)
        self.details_button.add_css_class("icon-action")
        self.details_button.update_property([Gtk.AccessibleProperty.LABEL], ["Usage details"])
        self.details_button.set_visible(False)
        actions.append(self.details_button)
        actions.append(self.pin)
        self.connect("map", self.on_window_mapped)
        for icon,title,callback in [("emblem-system-symbolic","Settings",self.show_settings),
                                    ("help-about-symbolic","About OpenAI Usage",self.show_about)]:
            button=Gtk.Button(icon_name=icon,tooltip_text=title)
            button.add_css_class("icon-action")
            button.connect("clicked",lambda _,cb=callback:cb())
            actions.append(button)
        manifesto = Gtk.LinkButton(uri=MANIFESTO_URL, tooltip_text="VibeWare manifesto")
        manifesto.set_child(about_mark("VibeWare.png", 20))
        manifesto.add_css_class("icon-action")
        manifesto.update_property([Gtk.AccessibleProperty.LABEL], ["VibeWare manifesto"])
        actions.append(manifesto)
        close = Gtk.Button(icon_name="window-close-symbolic", tooltip_text="Close")
        close.add_css_class("icon-action")
        close.connect("clicked", lambda _: self.close())
        actions.append(close)
        strip.append(actions)
        handle=Gtk.WindowHandle(child=strip)
        # Capture decorative content too, while preserving every button's click handling.
        drag=Gtk.GestureClick(button=Gdk.BUTTON_PRIMARY)
        drag.set_propagation_phase(Gtk.PropagationPhase.CAPTURE)
        drag.connect("pressed",self.begin_move)
        handle.add_controller(drag)
        handle.add_css_class("widget-band")
        bands = box(4)
        bands.append(handle)
        bands.append(self.build_details())
        self.set_child(bands)
        self.details_button.connect("toggled", self.details_toggled)
        keys = Gtk.EventControllerKey()
        keys.connect("key-pressed", self.details_key_pressed)
        self.add_controller(keys)
        self.apply_size()
        try:
            self.tray=StatusTray({"open":self.present,"about":self.show_about,"settings":self.show_settings,
                                 "website":lambda:Gio.AppInfo.launch_default_for_uri("https://umbertogiacobbi.biz",None),
                                 "manifesto":lambda:Gio.AppInfo.launch_default_for_uri(MANIFESTO_URL,None),
                                 "exit":self.exit_application})
        except Exception as error:
            log_event("tray.unavailable",type(error).__name__)
        self.clock_id=GLib.timeout_add(1000,self.tick)
        self.refresh_id=GLib.timeout_add_seconds(60,self.refresh)
        self.refresh()

    def x11_surface(self):
        surface = self.get_surface()
        return surface if GdkX11 is not None and isinstance(surface, GdkX11.X11Surface) else None

    def on_window_mapped(self, *_):
        self.sync_pin()

    def sync_pin(self):
        surface = self.x11_surface()
        self.pin.set_sensitive(surface is not None and self.settings_ready)
        if surface is None:
            self.pin.set_tooltip_text("Always on top is unavailable on this display; use your desktop's window controls")
            return
        if not self.settings_ready:
            return
        enabled = self.settings.get("alwaysOnTop", False)
        self.syncing_pin = True
        self.pin.set_active(enabled)
        self.syncing_pin = False
        self.pin.set_tooltip_text("Always on top: " + ("on" if enabled else "off") + " (window manager permitting)")
        try:
            request_x11_above(surface, enabled)
        except (OSError, RuntimeError) as error:
            self.pin.set_sensitive(False)
            self.pin.set_tooltip_text(str(error))
            log_event("window.topmost.failed", str(error))

    def on_pin_toggled(self, button):
        if self.syncing_pin or not self.settings_ready:
            return
        surface = self.x11_surface()
        if surface is None:
            return
        enabled = button.get_active()
        try:
            request_x11_above(surface, enabled)
        except (OSError, RuntimeError) as error:
            self.syncing_pin = True
            button.set_active(not enabled)
            self.syncing_pin = False
            button.set_tooltip_text(str(error))
            log_event("window.topmost.failed", str(error))
            return
        button.set_tooltip_text("Always on top: " + ("on" if enabled else "off") + " (window manager permitting)")
        self.update_settings(alwaysOnTop=enabled)

    def received(self,settings,state,revision):
        self.busy=False
        if self.closed:
            return False
        if self.state.get("connected")!=state.get("connected") or self.state.get("status")!=state.get("status"):
            log_event("connection.ready" if state.get("connected") else "connection.failed",state.get("status",""))
            if not state.get("connected"):
                notification=Gio.Notification.new("OpenAI Usage connection error")
                notification.set_body(state.get("status","Connection unavailable"))
                self.get_application().send_notification("connection-error",notification)
        self.state=state
        if self.refresh_pending:
            self.refresh_pending=False
            self.refresh()
        self.update_metrics(state)
        self.check_alerts()
        self.tick()
        return False

    def update_metrics(self, state):
        animate = self.animations_enabled()
        remaining = state.get("remaining")
        self.allowance_tile.update(f"{remaining:g}%" if remaining is not None else "—", animate)
        self.allowance_tile.set_detail("Percentage remaining for the selected Codex allowance. "
                                       + state.get("resetLabel", "Reset unavailable"))
        forecast = state.get("forecastCompact", "—") if state.get("forecastAvailable", False) else "—"
        self.estimate_tile.update(forecast, animate and self.estimate_tile.get_mapped())
        estimate_detail = ("Estimated time until the displayed allowance is exhausted at the observed usage rate; the allowance may reset first. "
                           + state.get("forecastDetail", "Collecting quota history."))
        self.estimate_tile.set_detail(estimate_detail)
        credits = state.get("creditsLabel", "Unavailable")
        self.credits_tile.update({"Unavailable": "—", "Unlimited": "∞"}.get(credits, credits),
                                 animate and self.credits_tile.get_mapped())
        self.credits_tile.set_detail(f"Credits remaining as reported by Codex: {credits}. The displayed balance is rounded to a whole number.")
        vouchers = state.get("resetVoucherCount")
        self.voucher_tile.update(f"{vouchers:02}" if vouchers is not None else "—",
                                 animate and self.voucher_tile.get_mapped())
        voucher_detail = ["Earned vouchers that can reset the Codex allowance manually."]
        voucher_detail.extend(expiry["detail"] for expiry in state.get("resetVoucherExpirations", []))
        self.voucher_tile.set_detail("\n".join(voucher_detail))
        scheduled = state.get("resetDisplays", [])
        first = scheduled[0] if scheduled else {}
        self.reset_tile.update(first.get("value") or state.get("selectedResetCompact") or "—", animate)
        details = [item["detail"] for item in scheduled] or [state.get("resetLabel", "Reset unavailable")]
        self.reset_tile.set_detail("\n".join(details))

    def animations_enabled(self):
        return self.get_mapped() and Gtk.Settings.get_default().get_property("gtk-enable-animations")

    def build_details(self):
        self.detail_tiles = (
            FlipTile("5-hour allowance", "5-hour allowance", 97),
            FlipTile("Resets in", "Resets in", 88),
            FlipTile("Weekly allowance", "Weekly allowance", 106),
            FlipTile("Resets at", "Resets at", 130, 18),
            FlipTile("Est. time Left", "Est. time Left", 104, 18),
            FlipTile("Time to reset", "Time to reset", 103, 18),
            FlipTile("At the current rate", "At the current rate", 348, 20),
        )
        self.detail_stack = Gtk.Stack(hhomogeneous=True, vhomogeneous=True,
                                      transition_type=Gtk.StackTransitionType.CROSSFADE, transition_duration=160)
        for name, tiles in (("quotas", self.detail_tiles[:4]), ("forecast", self.detail_tiles[4:])):
            row = box(9, True)
            row.set_halign(Gtk.Align.CENTER)
            for index, tile in enumerate(tiles):
                if name == "quotas" and index == 2:
                    row.append(Gtk.Separator(orientation=Gtk.Orientation.VERTICAL))
                row.append(tile)
            self.detail_stack.add_named(row, name)
        selector = Gtk.DropDown.new_from_strings(["Allowances", "Reset forecast"])
        selector.update_property([Gtk.AccessibleProperty.LABEL], ["Usage details view"])
        selector.set_valign(Gtk.Align.CENTER)
        selector.connect("notify::selected", self.details_view_changed)
        surface = box(12, True)
        surface.add_css_class("details-band")
        surface.append(selector)
        self.detail_stack.set_hexpand(True)
        surface.append(self.detail_stack)
        self.details_band = surface
        surface.set_visible(False)
        return surface

    @staticmethod
    def draw_details_icon(area, cr, width, height):
        color = area.get_color()
        cr.set_source_rgba(color.red, color.green, color.blue, color.alpha)
        for index, fraction in enumerate((.38, .62, .88)):
            cr.rectangle(width * (.15 + .26 * index), height * (.94 - fraction), width * .18, height * fraction)
        cr.fill()

    def details_view_changed(self, selector, _):
        self.detail_stack.set_visible_child_name("forecast" if selector.get_selected() == 1 else "quotas")
        self.update_details(int(time.time()))

    def details_toggled(self, button):
        opened = button.get_active() and self.settings.get("enableDetailsBar", False)
        self.details_band.set_visible(opened)
        (self.add_css_class if opened else self.remove_css_class)("details-open")
        self.set_default_size(-1, -1)
        if opened:
            self.update_details(int(time.time()))

    def details_key_pressed(self, _, key, *_args):
        if key == Gdk.KEY_Escape and self.details_button.get_active():
            self.details_button.set_active(False)
            return True
        return False

    def update_details(self, now):
        if not self.details_band.get_visible():
            return
        animate = self.animations_enabled()
        if self.detail_stack.get_visible_child_name() == "quotas":
            for name, percent_tile, reset_tile, countdown in (
                ("fiveHourAllowance", *self.detail_tiles[:2], True),
                ("weeklyAllowance", *self.detail_tiles[2:4], False),
            ):
                allowance = self.state.get(name) or {}
                remaining, reset = allowance.get("remaining"), allowance.get("resetsAt")
                percent_tile.update(f"{remaining:.1f}".removesuffix(".0") + "%" if remaining is not None else "—", animate)
                active = [threshold for threshold in (5, 20, 50) if self.settings.get(f"alert{threshold}", False)
                          and remaining is not None and remaining <= threshold]
                lamp = 0 if 5 in active else 1 if active else 2
                percent_tile.set_colors(accent=TrafficSignal.LIGHTS[lamp] if remaining is not None else None)
                value = allowance.get("resetCompact")
                if countdown and reset is not None:
                    minutes = (max(0, reset - now) + 59) // 60
                    value = f"{minutes // 60:02}:{minutes % 60:02}"
                reset_tile.update(value or "—", animate)
                reset_tile.set_detail(allowance.get("resetCompact") or "Reset time unavailable")
            return

        estimate, reset_tile, message = self.detail_tiles[4:]
        reset = self.state.get("resetsAt")
        prediction = self.state.get("resetForecast") or {}
        remaining = self.state.get("remaining")
        passed = reset is not None and reset <= now
        estimate.update(self.state.get("forecastCompact", "—") if self.state.get("forecastAvailable") and not passed else "—", animate)
        if reset is not None:
            minutes = (max(0, reset - now) + 59) // 60
            hours = minutes // 60
            reset_value = f"{hours // 24:02}d {hours % 24:02}h" if hours >= 24 else f"{hours:02}h {minutes % 60:02}m"
        else:
            reset_value = "—"
        reset_tile.update(reset_value, animate)
        waiting = passed and (remaining is None or remaining > 0)
        message.update("WAITING FOR RESET DATA" if waiting else prediction.get("message", "ALLOWANCE UNAVAILABLE"), animate)
        message.set_colors(warning=not waiting and prediction.get("warning", False))
        message.set_detail(self.state.get("forecastDetail", "Allowance data is unavailable."))

    def check_alerts(self):
        connected = self.state.get("connected")
        window = self.state.get("selectedWindow")
        remaining = self.state.get("remaining") if connected and window is not None else None
        balance = self.state.get("creditsBalance") if connected else None
        if remaining is not None and self.alert_window != window:
            self.alert_window = window
            self.notified_thresholds.clear()
        enabled = {threshold for threshold in (50, 20, 5) if self.settings.get(f"alert{threshold}", False)}
        self.notified_thresholds.intersection_update(enabled)
        active = {threshold for threshold in enabled if remaining is not None and remaining <= threshold}
        if remaining is not None:
            self.notified_thresholds.intersection_update(active)
        crossed = active - self.notified_thresholds
        if remaining is None:
            lamp, detail = None, "Waiting for allowance data"
        else:
            lamp = 0 if 5 in active else 1 if active else 2
            detail = f"Allowance remaining: {remaining:g}%"
            if active:
                detail += f" (threshold: {min(active)}%)"
        credit_limit = self.settings.get("creditsAlertThreshold")
        if credit_limit != self.credits_alert_threshold:
            self.credits_alert_threshold = credit_limit
            self.credits_alert_notified = False
        low_credits = credit_limit is not None and balance is not None and balance <= credit_limit
        credits_crossed = low_credits and not self.credits_alert_notified
        if balance is not None:
            self.credits_alert_notified = low_credits
        if low_credits:
            detail += f"\nLow credits: {balance:g} remaining"
        self.signal.update(lamp, detail)
        if crossed:
            threshold = min(crossed)
            notification = Gio.Notification.new("OpenAI Usage · Allowance alert")
            notification.set_body(f"{self.state.get('allowanceLabel', 'Allowance remaining')}: {remaining:g}% remaining (at or below {threshold}%).")
            self.get_application().send_notification("allowance-alert", notification)
            self.notified_thresholds.update(crossed)
        if credits_crossed:
            notification = Gio.Notification.new("OpenAI Usage · Credits alert")
            notification.set_body(f"{balance:g} credits remaining (at or below {credit_limit}).")
            self.get_application().send_notification("credits-alert", notification)
        if (crossed or credits_crossed) and self.settings.get("alertSound", True):
            self.play_alert_sound()

    def play_alert_sound(self):
        if self.sound_process is not None:
            return
        sound = ROOT / "Branding" / "alert-ding.wav"
        players = (shutil.which(command) for command in ("pw-play", "paplay", "aplay")) if sound.is_file() else ()

        def try_next(players):
            for player in players:
                if player is None:
                    continue
                try:
                    self.sound_process = Gio.Subprocess.new(
                        [player, str(sound)], Gio.SubprocessFlags.STDOUT_SILENCE | Gio.SubprocessFlags.STDERR_SILENCE)
                except GLib.Error:
                    continue
                self.sound_process.wait_check_async(None, on_finished, players)
                return
            if (display := self.get_display()) is not None:
                display.beep()

        def on_finished(process, result, players):
            self.sound_process = None
            try:
                process.wait_check_finish(result)
            except GLib.Error:
                if not self.closed and self.settings.get("alertSound", True):
                    try_next(players)

        try_next(iter(players))

    def tick(self):
        if self.closed:
            return False
        if GUARD is not None and GUARD.poll() is not None:
            log_event("guard.failure","Separate guard exited unexpectedly; closing the widget.")
            self.get_application().quit()
            return False
        end=self.state.get("resetsAt")
        seconds=max(0,int(end-time.time())) if end is not None else None
        self.update_clock(seconds)
        self.update_details(int(time.time()))
        self.signal.maybe_flicker(self.animations_enabled())
        status=self.state.get("status","Connecting to Codex…")
        updated=self.state.get("updatedAt")
        stale=False
        if updated:
            timestamp=dt.datetime.fromisoformat(updated.replace("Z","+00:00"))
            stale=time.time()-timestamp.timestamp()>180
            status += " · " + timestamp.astimezone().strftime("%H:%M:%S")
        if stale:
            status += " · STALE"
        self.brand.set_tooltip_text(status)
        problem_status = "Saving settings before closing…" if self.exit_pending else self.settings_error or status
        self.problem.set_tooltip_text(problem_status)
        self.problem.set_visible(self.exit_pending or bool(self.settings_error) or not self.state.get("connected",False) or stale)
        if seconds==0 and time.monotonic()-self.last_reset_refresh>=60:
            self.last_reset_refresh=time.monotonic()
            self.refresh()
        return True

    def update_clock(self, seconds):
        now = time.monotonic()
        date = (self.state.get("resetCalendarDate") or "").split()
        if seconds is None or len(date) != 4:
            self.clock_cycle_start = None
        elif self.clock_cycle_start is None:
            self.clock_cycle_start = now
        show_date = self.clock_cycle_start is not None and int((now - self.clock_cycle_start) / 10) % 2 == 1
        view = self.calendar_row if show_date else self.tile_row
        switched = view != self.clock_views.get_visible_child()
        if switched:
            self.clock_views.set_visible_child(view)
        animate = self.animations_enabled()
        if show_date:
            for tile, value in zip(self.calendar_tiles, date):
                tile.update(value, animate, reveal=switched)
        else:
            values = [f"{seconds//86400:02}", f"{seconds//3600%24:02}", f"{seconds//60%60:02}", f"{seconds%60:02}"] if seconds is not None else ["—"] * 4
            for tile, value in zip(self.tiles, values):
                tile.update(value, animate)

    def apply_size(self):
        big = self.settings.get("bigSize", False)
        (self.add_css_class if big else self.remove_css_class)("big")
        self.brand.set_pixel_size(31 if big else 25)
        self.strip.set_spacing(15 if big else 12)
        for setter in (self.strip.set_margin_start, self.strip.set_margin_end):
            setter(12 if big else 10)
        for setter in (self.strip.set_margin_top, self.strip.set_margin_bottom):
            setter(9 if big else 7)
        self.tile_row.set_spacing(6 if big else 5)
        self.calendar_row.set_spacing(6 if big else 5)
        position = self.settings.get("labelPosition", "above")
        scale = 1.25 if big else 1
        for tile in (self.signal, *self.metrics, *self.tiles, *self.calendar_tiles):
            tile.set_label_position(position)
            tile.resize(scale)
        self.voucher_tile.set_visible(self.settings.get("showAvailableResets", True))
        self.credits_tile.set_visible(self.settings.get("showCredits", True))
        self.estimate_tile.set_visible(self.settings.get("showEstimatedTime", True))
        details_enabled = self.settings.get("enableDetailsBar", False)
        icon_size = 20 if big else 16
        self.details_icon.set_content_width(icon_size)
        self.details_icon.set_content_height(icon_size)
        self.details_button.set_visible(details_enabled)
        if not details_enabled:
            self.details_button.set_active(False)
        for tile in self.detail_tiles:
            tile.set_label_position(position)
            tile.resize(scale)
        self.set_default_size(-1, round((50 if position == "hidden" else 62) * scale))

    def show_about(self, *_):
        self.present()
        if self.about_dialog is not None:
            self.about_dialog.present()
            return

        dialog = Gtk.Window(application=self.get_application(), title="About OpenAI Usage",
                            transient_for=self, destroy_with_parent=True, modal=True,
                            default_width=640, default_height=620, icon_name="biz.umbertogiacobbi.OpenAIUsage")
        self.about_dialog = dialog
        dialog.add_css_class("widget-about")
        content = box(12)
        content.add_css_class("about-body")
        content.set_halign(Gtk.Align.CENTER)
        for set_margin in (content.set_margin_start, content.set_margin_end, content.set_margin_top, content.set_margin_bottom):
            set_margin(24)

        def centered(value, style=None):
            label = Gtk.Label(label=value, wrap=True, justify=Gtk.Justification.CENTER,
                              max_width_chars=60, halign=Gtk.Align.CENTER)
            if style:
                label.add_css_class(style)
            return label

        website = "https://umbertogiacobbi.biz"
        content.append(about_mark("app-icon-512.png", 100))
        identity = box(5)
        identity.append(centered("OpenAI Usage", "about-heading"))
        identity.append(centered("Version 0.4.4", "about-note"))
        content.append(identity)
        content.append(centered("I built OpenAI Usage to see my Codex allowance, tokens and credits at a glance."))
        content.append(centered("I provide this software independently and as-is, without warranty.", "about-note"))
        content.append(centered("I'm Umberto Giacobbi · Proprietary"))

        details = Gtk.Expander(label="Technical details", halign=Gtk.Align.CENTER)
        diagnostic = centered("Separate watchdog: 1 GB aggregate RSS limit.\nLogs: " + str(state_dir() / "logs"), "about-note")
        diagnostic.set_selectable(True)
        details.set_child(diagnostic)
        content.append(details)
        content.append(about_link("VibeWare manifesto", website + "/vibeware/manifesto", about_mark("VibeWare.png", 64)))

        links = Gtk.FlowBox(selection_mode=Gtk.SelectionMode.NONE, min_children_per_line=1, max_children_per_line=4,
                            column_spacing=8, row_spacing=4, halign=Gtk.Align.CENTER)
        for label, url, icon in (
            ("UmbertoGiacobbi.biz", website, "web-browser-symbolic"),
            ("Source", "https://github.com/umbertotechnopreneur/VibeKit/tree/main/Widgets/OpenAI", "folder-remote-symbolic"),
            ("Terms", website + "/terms/", "text-x-generic-symbolic"),
            ("Privacy", website + "/privacy/", "changes-prevent-symbolic"),
        ):
            pair = box(6, horizontal=True)
            pair.append(Gtk.Image.new_from_icon_name(icon))
            pair.append(Gtk.Label(label=label))
            link = about_link(label, url, pair)
            if label == "Source":
                link.set_tooltip_text("View this utility in the VibeKit repository.")
            links.append(link)
        content.append(links)

        dialog.set_child(content)

        def forget_dialog(*_):
            self.about_dialog = None
            return False

        def dismiss(_controller, key, *_):
            if key == Gdk.KEY_Escape:
                dialog.close()
                return True
            return False

        dialog.connect("close-request", forget_dialog)
        keys = Gtk.EventControllerKey()
        keys.connect("key-pressed", dismiss)
        dialog.add_controller(keys)
        dialog.present()

    def begin_move(self, gesture, presses, x, y):
        """Ask the compositor to drag from any non-button part of the micro strip."""
        if self.closed or presses != 1:
            return
        target=gesture.get_widget().pick(x,y,Gtk.PickFlags.DEFAULT)
        while target is not None:
            if isinstance(target,Gtk.Button):
                gesture.set_state(Gtk.EventSequenceState.DENIED)
                return
            target=target.get_parent()
        event=gesture.get_current_event()
        surface=self.get_surface()
        if event is None or surface is None:
            return
        valid, surface_x, surface_y=event.get_position()
        device=event.get_device()
        if valid and device is not None:
            gesture.set_state(Gtk.EventSequenceState.CLAIMED)
            surface.begin_move(device,Gdk.BUTTON_PRIMARY,surface_x,surface_y,event.get_time())

    def exit_application(self):
        self.exit_pending = True
        self.allow_close = True
        self.close()

    def on_close(self, *_):
        if not self.allow_close:
            if self.close_dialog is not None:
                return True
            tray_available = self.tray is not None and self.tray.available
            message = "Keep monitoring in the notification area, or exit the app." if tray_available else "Your desktop's notification area is unavailable. You can exit or keep the window open."
            dialog = Adw.MessageDialog(transient_for=self, modal=True, heading="Close OpenAI Usage?", body=message)
            if tray_available:
                dialog.add_response("tray", "Minimize to tray")
            dialog.add_response("exit", "Exit")
            dialog.add_response("cancel", "Cancel")
            dialog.set_close_response("cancel")
            dialog.set_default_response("tray" if tray_available else "cancel")
            def response(_, result):
                self.close_dialog = None
                if result == "exit":
                    self.exit_application()
                elif result == "tray" and self.tray is not None and self.tray.available:
                    self.set_visible(False)
            dialog.connect("response", response)
            self.close_dialog = dialog
            dialog.present()
            return True
        if self.saving or self.saved_settings_revision != self.settings_revision:
            self.exit_pending = True
            self.problem.set_visible(True)
            self.problem.set_tooltip_text("Saving settings before closing…")
            if not self.saving:
                self.save_async()
            return True
        self.closed = True
        if self.sound_process is not None:
            self.sound_process.force_exit()
        if self.tray:
            self.tray.close()
        GLib.source_remove(self.clock_id)
        GLib.source_remove(self.refresh_id)
        return False


class UsageApp(Adw.Application):
    def __init__(self):
        super().__init__(application_id="biz.umbertogiacobbi.OpenAIUsage")
        self.window = None

    def do_activate(self):
        if self.window is None:
            manager = Adw.StyleManager.get_default()
            manager.set_color_scheme(Adw.ColorScheme.DEFAULT)
            provider = Gtk.CssProvider()
            provider.load_from_data(CSS)
            Gtk.StyleContext.add_provider_for_display(Gdk.Display.get_default(), provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
            Gtk.IconTheme.get_for_display(Gdk.Display.get_default()).add_search_path(str(ROOT/"Branding/icons"))
            self.window = UsageWindow(self)
        self.window.present()


if __name__ == "__main__":
    lock_dir = Path.home() / ".cache" / "openai-usage"
    lock_dir.mkdir(mode=0o700, parents=True, exist_ok=True)
    with (lock_dir / "instance.lock").open("a") as instance_lock:
        try:
            fcntl.flock(instance_lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError:
            raise SystemExit(0) from None
        GUARD = start_watchdog()
        raise SystemExit(UsageApp().run(sys.argv))
