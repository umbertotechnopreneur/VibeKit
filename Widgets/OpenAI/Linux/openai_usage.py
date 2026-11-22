#!/usr/bin/env python3
"""Native GTK 4 / libadwaita presentation for the shared C# account reader."""
from __future__ import annotations

import ctypes
import ctypes.util
import datetime as dt
import fcntl
import math
import os
import re
import random
from dataclasses import dataclass
from pathlib import Path
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
from gi.repository import Adw, Gdk, GdkPixbuf, GLib, Gtk, Pango, PangoCairo, Gio
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

def request_x11_state(surface, enabled, atom):
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
        event.client.data[0], event.client.data[1], event.client.data[3] = (1 if enabled else 0), x11.XInternAtom(display, atom, False), 1
        if not x11.XSendEvent(display, x11.XDefaultRootWindow(display), False, (1 << 19) | (1 << 20), ctypes.byref(event)):
            raise RuntimeError("Could not request the X11 window state.")
        x11.XFlush(display)
    finally:
        x11.XCloseDisplay(display)

ROOT = Path(__file__).resolve().parents[1]
PROJECT_URL = "https://github.com/umbertotechnopreneur/VibeKit/tree/main/Widgets/OpenAI"
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
.micro .flip-caption { font-size: 12px; opacity: .85; }
.micro.big .flip-caption { font-size: 15px; }
.micro separator.metric-divider { background-color: alpha(@window_fg_color, .26); min-width: 1px; }
.micro menubutton.voucher-count > button { padding: 0; border-width: 0; min-width: 0; min-height: 0; }
.micro.big button.icon-action { padding: 6px; min-width: 23px; min-height: 23px; }
.micro.big button.icon-action image { -gtk-icon-size: 20px; }
.micro button.icon-action { padding: 5px; min-width: 18px; min-height: 18px; }
.micro button.icon-action image { -gtk-icon-size: 16px; }
.micro button.icon-action.brand-action { padding: 0; }
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
        self.caption.set_ellipsize(Pango.EllipsizeMode.END)
        self.caption.add_css_class("flip-caption")
        self.append(self.caption)
        self.append(display)

    def set_label_position(self, position):
        self.caption.set_visible(position != "hidden")
        self.reorder_child_after(self.caption, self.display if position == "below" else None)


@dataclass(frozen=True)
class CreaseShadow:
    spread: float
    color: tuple[float, float, float, float]
    line: tuple[float, float, float]


@dataclass(frozen=True)
class DigitFlip:
    previous: str
    start: float
    duration: float


class FlipTile(LabeledDisplay):
    def __init__(self, name, caption, width, font_size=21, *, info=False, wrap_value=False, worn=False,
                 sizing_text=None, crease_shadow=None, digit_delay=(0, 0)):
        face = Gtk.DrawingArea()
        display = box(4, True)
        display.set_halign(Gtk.Align.CENTER)
        display.append(face)
        super().__init__(caption, display)
        self.face = face
        self.name = name
        self.base_width = width
        self.base_font_size = font_size
        self.wrap_value = wrap_value
        self.sizing_text = sizing_text
        self.crease_shadow = crease_shadow
        if not 0 <= digit_delay[0] <= digit_delay[1]:
            raise ValueError("Digit delays must be an ascending range in milliseconds.")
        self.digit_delay = digit_delay
        noise = random.Random(name)
        self.grain = [(noise.random(), noise.random(), noise.random()) for _ in range(150)] if worn else ()
        self.progress = None
        self.scale = 1
        self.available_width = width
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
        self.digit_flips = {}
        self.duration = .20
        self.ticking = False
        self.face.set_draw_func(self.draw)
        self.face.update_property([Gtk.AccessibleProperty.LABEL], [f"{name}: unavailable"])
        self.resize(1)

    def resize(self, scale, available=None):
        self.scale = scale
        self.available_width = max(30, round(self.base_width * scale if available is None else min(self.base_width * scale, available)))
        self.face.set_content_width(self.available_width - (22 if self.info is not None else 0))
        self.font_size = self.base_font_size * scale
        self.fit_value_height()
        self.face.queue_draw()

    def fit_value_height(self):
        height = round(34 * self.scale)
        if self.wrap_value:
            budget = max(1, int((self.available_width - 12) / (.61 * self.font_size)))
            lines, used = 1, 0
            for word in self.value.split():
                if used and used + len(word) + 1 > budget:
                    lines, used = lines + 1, 0
                used += len(word) + bool(used)
            height = max(height, round(8 + lines * (self.font_size + 4)))
        self.face.set_content_height(height)

    def set_detail(self, detail):
        if self.face.get_tooltip_text() != detail:
            self.face.set_tooltip_text(detail)
            if self.info is not None:
                self.info.set_tooltip_text(detail)

    def update(self, value, animate, *, flaps=(), duration=.20):
        if value == self.value:
            if not animate and (self.started is not None or self.digit_flips):
                self.started = None
                self.digit_flips.clear()
                self.face.queue_draw()
            return
        now = time.monotonic()
        previous, self.previous = self.value, self.visible_value(now)
        self.value = value
        self.duration = duration
        animate = animate and previous != "—"
        if animate and flaps and not self.wrap_value and len(previous) == len(value):
            self.started = None
            for start, end, flap_duration in flaps:
                for index in range(start, end):
                    if previous[index] == value[index]:
                        continue
                    self.digit_flips.pop(index, None)
                    if self.previous[index] != value[index]:
                        delay = random.randint(*self.digit_delay) / 1000
                        self.digit_flips[index] = DigitFlip(self.previous, now + delay, flap_duration)
        else:
            self.digit_flips.clear()
            self.started = now if animate and self.previous != value else None
        self.fit_value_height()
        accessible_value = "unavailable" if value == "—" else value
        self.face.update_property([Gtk.AccessibleProperty.LABEL], [f"{self.name}: {accessible_value}"])
        self.face.queue_draw()
        if (self.started is not None or self.digit_flips) and not self.ticking:
            self.ticking = True
            self.face.add_tick_callback(self.tick)

    def visible_value(self, now):
        if not self.digit_flips:
            return self.value
        visible = list(self.value)
        for index, flip in self.digit_flips.items():
            if now < flip.start + flip.duration / 2:
                visible[index] = flip.previous[index]
        return "".join(visible)

    def set_colors(self, *, accent=None, warning=False):
        ink = (1, .71, .19) if warning else (1, 1, 1)
        if self.ink == ink and self.accent == accent:
            return
        self.ink, self.accent = ink, accent
        self.face.queue_draw()

    def tick(self, widget, frame):
        now = time.monotonic()
        if self.started is not None:
            widget.queue_draw()
            if now - self.started >= self.duration:
                self.started = None
        if any(now >= flip.start for flip in self.digit_flips.values()):
            widget.queue_draw()
            self.digit_flips = {index: flip for index, flip in self.digit_flips.items()
                               if now < flip.start + flip.duration}
        self.ticking = self.started is not None or bool(self.digit_flips)
        return self.ticking

    def draw(self, area, cr, width, height):
        self.draw_face(cr, width, height, self.value)
        now = time.monotonic()
        if self.started is not None:
            self.draw_flap(cr, width, height, self.previous, (now - self.started) / self.duration)
        if self.digit_flips:
            self.draw_digits(cr, width, height, now)
        self.draw_seam(cr, width, height)

    def draw_digits(self, cr, width, height, now):
        layout = self.layout_for_text(cr, width, self.value)
        line_left = (width - layout.get_size()[0] / Pango.SCALE) / 2
        for index, flip in self.digit_flips.items():
            start = len(self.value[:index].encode("utf-8"))
            end = start + len(self.value[index].encode("utf-8"))
            left = line_left + layout.index_to_pos(start).x / Pango.SCALE
            right = line_left + layout.index_to_pos(end).x / Pango.SCALE
            cr.save()
            cr.rectangle(left, 0, right - left, height)
            cr.clip()
            progress = max(0, (now - flip.start) / flip.duration)
            self.draw_flap(cr, width, height, flip.previous, progress)
            cr.restore()

    def draw_flap(self, cr, width, height, previous, progress):
        if progress >= 1:
            return
        self.draw_half(cr, width, height, previous, False, 1)
        if progress < .5:
            self.draw_half(cr, width, height, previous, True, math.cos(progress * math.pi))
        else:
            self.draw_half(cr, width, height, self.value, False, math.sin((progress - .5) * math.pi))

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
        layout = self.layout_for_text(cr, width, value)
        tw, th = layout.get_pixel_size()
        cr.move_to((width-tw)/2, (height-th)/2)
        cr.set_source_rgb(*self.ink)
        PangoCairo.show_layout(cr, layout)
        if self.grain:
            for shade, opacity in ((0, .24), (1, .12)):
                cr.set_source_rgba(shade, shade, shade, opacity)
                for x, y, tone in self.grain:
                    if int(tone * 2) == shade:
                        cr.rectangle(4 + x * (width - 8), 3 + y * (height - 6), .4 + tone, .7)
                cr.fill()
        cr.restore()

    def draw_seam(self, cr, width, height):
        cr.save()
        if self.crease_shadow is not None:
            shadow = self.crease_shadow
            spread = shadow.spread * self.scale
            center = height / 2
            if spread > 0:
                gradient = cairo.LinearGradient(0, center - spread, 0, center + spread)
                gradient.add_color_stop_rgba(0, *shadow.color[:3], 0)
                gradient.add_color_stop_rgba(.5, *shadow.color)
                gradient.add_color_stop_rgba(1, *shadow.color[:3], 0)
                cr.set_source(gradient)
                cr.rectangle(2, center - spread, width - 4, spread * 2)
                cr.fill()
        cr.set_source_rgb(*(self.crease_shadow.line if self.crease_shadow else (.02, .02, .02)))
        cr.set_line_width(2)
        cr.move_to(2, height/2)
        cr.line_to(width-2, height/2)
        cr.stroke()
        cr.set_source_rgb(.42, .42, .42)
        for x in (1, width-5):
            cr.rectangle(x, height/2-6, 4, 12)
            cr.fill()
        if self.accent and self.progress:
            cr.set_source_rgb(*self.accent)
            cr.set_line_width(2)
            cr.set_line_cap(cairo.LINE_CAP_ROUND)
            cr.move_to(7, height - 3)
            cr.line_to(7 + (width - 14) * self.progress, height - 3)
            cr.stroke()
        cr.restore()

    def layout_for_text(self, cr, width, value):
        layout = PangoCairo.create_layout(cr)
        font = Pango.FontDescription("Monospace Bold")
        font.set_absolute_size(self.font_size * Pango.SCALE)
        layout.set_font_description(font)
        layout.set_text(self.sizing_text or value, -1)
        text_width = layout.get_pixel_size()[0]
        if text_width > width - 10:
            if self.wrap_value:
                layout.set_width(round((width - 10) * Pango.SCALE))
                layout.set_alignment(Pango.Alignment.CENTER)
                layout.set_wrap(Pango.WrapMode.WORD_CHAR)
            else:
                font.set_absolute_size(self.font_size * (width - 10) / text_width * Pango.SCALE)
                layout.set_font_description(font)
        if self.sizing_text:
            layout.set_text(value, -1)
        return layout


class VoucherTile(FlipTile):
    def __init__(self):
        super().__init__("Resets available", "Resets available", 82)
        self.display.remove(self.face)
        self.count_button = Gtk.MenuButton(child=self.face, has_frame=False,
                                           tooltip_text="Show reset voucher expirations")
        self.count_button.add_css_class("voucher-count")
        self.display.append(self.count_button)
        self.expirations = Gtk.Label(xalign=0, wrap=True, wrap_mode=Pango.WrapMode.WORD_CHAR,
                                     max_width_chars=46)
        expiration_list = Gtk.ScrolledWindow(child=self.expirations, max_content_height=240,
                                             propagate_natural_width=True, propagate_natural_height=True)
        expiration_list.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        self.count_button.set_popover(Gtk.Popover(child=expiration_list))
        self.show_vouchers(None, (), False)

    def show_vouchers(self, count, expirations, animate):
        self.update(str(count) if count is not None else "—", animate)
        summary = "Reset voucher balance unavailable" if count is None else f"Resets available: {count}"
        details = [f"Reset {index}: {expiry['detail']}" for index, expiry in enumerate(expirations, 1)]
        if count and not details:
            details.append("Expiration dates are unavailable.")
        self.expirations.set_text("\n".join([summary, *details]))
        self.count_button.update_property([Gtk.AccessibleProperty.LABEL],
                                          [summary + ". Show reset voucher expirations."])


class WrapLayout(Gtk.LayoutManager):
    def __init__(self, spacing=8, center=False):
        super().__init__()
        self.spacing = spacing
        self.center = center

    def do_get_request_mode(self, widget):
        return Gtk.SizeRequestMode.HEIGHT_FOR_WIDTH

    def do_measure(self, widget, orientation, for_size):
        if orientation == Gtk.Orientation.HORIZONTAL:
            sizes = [child.measure(orientation, -1)[1] for child in self.children(widget)]
            return 0, sum(sizes) + max(0, len(sizes) - 1) * self.spacing, -1, -1
        width = for_size if for_size >= 0 else 1240
        height = self.rows(widget, width)[1]
        return height, height, -1, -1

    def do_allocate(self, widget, width, height, baseline):
        rows, _ = self.rows(widget, width)
        y = 0
        for row, row_width, row_height in rows:
            shift = (width - row_width) // 2 if self.center else 0
            for child, x, w, h in row:
                bounds = Gdk.Rectangle()
                bounds.x, bounds.y, bounds.width, bounds.height = x + shift, y, w, h
                child.size_allocate(bounds, -1)
            y += row_height + self.spacing

    @staticmethod
    def children(widget):
        child = widget.get_first_child()
        while child is not None:
            if child.get_visible():
                yield child
            child = child.get_next_sibling()

    def rows(self, widget, width):
        rows, row = [], []
        x = row_height = height = 0
        for child in self.children(widget):
            w = max(1, min(width, child.measure(Gtk.Orientation.HORIZONTAL, -1)[1]))
            h = child.measure(Gtk.Orientation.VERTICAL, w)[1]
            if row and x + self.spacing + w > width:
                rows.append((row, x, row_height))
                height += row_height + self.spacing
                row, x, row_height = [], 0, 0
            if row:
                x += self.spacing
            row.append((child, x, w, h))
            x += w
            row_height = max(row_height, h)
        if row:
            rows.append((row, x, row_height))
        return rows, height + row_height


def wrap_box(spacing=8, center=False):
    container = Gtk.Box()
    container.set_layout_manager(WrapLayout(spacing, center))
    return container


class TrafficSignal(Gtk.Box):
    LIGHTS = ((1, .26, .19), (1, .76, .16), (.27, .91, .38))

    def __init__(self):
        super().__init__(valign=Gtk.Align.CENTER, margin_start=12, margin_end=10, margin_top=8)
        self.set_size_request(44, 128)
        self.glow = Gtk.DrawingArea(can_target=False)
        overlay = Gtk.Overlay(hexpand=True, vexpand=True)
        self.signal_pictures = Gtk.Overlay(can_target=False)
        self.signal_pictures.set_child(Gtk.Box())
        overlay.set_child(self.signal_pictures)
        self.append(overlay)
        self.glow.set_draw_func(self.draw_glow)
        self.layers = []
        for color in ("green", "amber", "red"):
            source = GdkPixbuf.Pixbuf.new_from_file_at_scale(str(ROOT / f"Branding/signal-{color}.png"), -1, 384, True)
            picture = Gtk.Picture(paintable=Gdk.Texture.new_for_pixbuf(source), can_shrink=True, can_target=False)
            self.signal_pictures.add_overlay(picture)
            self.layers.append(picture)
        overlay.add_overlay(self.glow)
        self.weights = (1, 0, 0)
        self.last_remaining = None
        self.next_flicker = time.monotonic() + 300
        self.apply_weights(self.weights)
        self.set_opacity(.35)

    def apply_weights(self, weights):
        self.weights = weights
        for layer, level in zip(self.layers, weights):
            layer.set_opacity(level)
        self.glow.queue_draw()

    def draw_glow(self, area, cr, width, height):
        _, amber, red = self.weights
        strengths = (red, amber * (1 - red), (1 - amber) * (1 - red))
        for i, (color, power) in enumerate(zip(self.LIGHTS, strengths)):
            if not power or self.last_remaining is None:
                continue
            x, y, radius = width / 2, height * (.20 + i * .29), width * .70
            glow = cairo.RadialGradient(x, y, 0, x, y, radius)
            power = math.sqrt(power)
            glow.add_color_stop_rgba(0, *color, .67 * power)
            glow.add_color_stop_rgba(.33, *color, .49 * power)
            glow.add_color_stop_rgba(.55, *color, .29 * power)
            glow.add_color_stop_rgba(1, *color, 0)
            cr.set_source(glow)
            cr.paint()

    def update(self, remaining, detail, animate):
        self.set_tooltip_text(detail)
        self.update_property([Gtk.AccessibleProperty.LABEL], [detail])
        if self.last_remaining == remaining:
            return
        self.last_remaining = remaining
        self.set_opacity(1 if remaining is not None else .35)
        normalized = max(0, min(100, remaining if remaining is not None else 100))
        target = (1, min(1, (100 - normalized) / 50), max(0, (50 - normalized) / 50))
        if not animate:
            self.apply_weights(target)
            return
        start, previous = time.monotonic(), self.weights

        def tick(*_):
            if self.last_remaining != remaining:
                return False
            t = min(1, (time.monotonic() - start) / .26)
            self.apply_weights(tuple(a + (b - a) * t for a, b in zip(previous, target)))
            return t < 1

        self.add_tick_callback(tick)

    def maybe_flicker(self, animate):
        now = time.monotonic()
        if now < self.next_flicker:
            return
        self.next_flicker = now + 300
        if not animate or self.last_remaining is None:
            return

        def tick(*_):
            if self.last_remaining is None:
                self.signal_pictures.set_opacity(1)
                return False
            elapsed = min(1, (time.monotonic() - now) / .28)
            self.signal_pictures.set_opacity(1 - .09 * math.sin(2 * math.pi * elapsed) ** 2 * (1 - elapsed))
            return elapsed < 1

        self.add_tick_callback(tick)


class UsageWindow(SettingsUiMixin, Gtk.ApplicationWindow):
    def __init__(self, app):
        # Adw.ApplicationWindow imposes a minimum height larger than the strip.
        super().__init__(application=app, title="OpenAI Usage", default_width=1240, default_height=110,
                         icon_name="biz.umbertogiacobbi.OpenAIUsage")
        self.state = {}
        self.clock_cycle_start = None
        self.clock_shows_date = False
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
        self.set_resizable(True)
        self.set_size_request(300, -1)
        self.layout_pending = False
        self.layout_width = 1240
        self.resize_surface = None
        self.highest_credits = 0
        self.connect("close-request", self.on_close)

        strip = self.strip = wrap_box()
        strip.set_hexpand(True)
        strip.set_valign(Gtk.Align.START)
        self.brand = Gtk.Image.new_from_file(str(ROOT / "Branding/app-icon-512.png"))
        self.project_link = Gtk.LinkButton(uri=PROJECT_URL, child=self.brand, valign=Gtk.Align.CENTER,
                                           tooltip_text="OpenAI Usage project page\nConnecting to Codex…")
        self.manifesto_mark = about_mark("VibeWare.png", 206)
        self.manifesto_mark.set_halign(Gtk.Align.CENTER)
        self.manifesto_mark.set_valign(Gtk.Align.CENTER)
        self.manifesto_mark.set_can_target(False)
        # Extend the artwork past the button without changing its layout or click area.
        self.manifesto_face = Gtk.Overlay(overflow=Gtk.Overflow.VISIBLE)
        self.manifesto_face.add_overlay(self.manifesto_mark)
        manifesto = Gtk.LinkButton(uri=MANIFESTO_URL, child=self.manifesto_face, valign=Gtk.Align.CENTER,
                                   overflow=Gtk.Overflow.VISIBLE,
                                   tooltip_text="VibeWare manifesto")
        for link, label in ((self.project_link, "OpenAI Usage project page"), (manifesto, "VibeWare manifesto")):
            link.add_css_class("icon-action")
            link.add_css_class("brand-action")
            link.update_property([Gtk.AccessibleProperty.LABEL], [label])

        self.signal = TrafficSignal()
        self.allowance_tile = FlipTile("Allowance remaining", "Allowance remaining", 115, 20, worn=True)
        self.credits_tile = FlipTile("Remaining Credits", "Remaining Credits", 108, 20, worn=True)
        self.estimate_tile = FlipTile("Estimated time remaining", "Estimated time remaining", 152, 18)
        self.voucher_tile = VoucherTile()
        self.reset_tile = FlipTile("First reset", "First reset", 123, 17, info=True)
        self.metrics = (self.allowance_tile, self.credits_tile, self.estimate_tile, self.voucher_tile, self.reset_tile)
        self.reset_clock = FlipTile("Next reset date", "Next reset date", 403, 24,
                                    sizing_text="WEDNESDAY 00 SEPTEMBER 00",
                                    crease_shadow=CreaseShadow(5, (52/255, 10/255, 13/255, .44), (24/255, 6/255, 7/255)),
                                    digit_delay=(300, 1000))

        actions = self.actions = wrap_box(3, center=True)
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
        actions.append(self.pin)
        actions.append(self.details_button)
        self.connect("map", self.on_window_mapped)
        for icon,title,callback in [("emblem-system-symbolic","Settings",self.show_settings),
                                    ("help-about-symbolic","About OpenAI Usage",self.show_about)]:
            button=Gtk.Button(icon_name=icon,tooltip_text=title)
            button.add_css_class("icon-action")
            button.connect("clicked",lambda _,cb=callback:cb())
            actions.append(button)
        close = Gtk.Button(icon_name="window-close-symbolic", tooltip_text="Close")
        close.add_css_class("icon-action")
        close.connect("clicked", lambda _: self.close())
        actions.append(close)
        brands = self.brands = box(4, True)
        brands.set_halign(Gtk.Align.CENTER)
        brands.append(self.project_link)
        brands.append(manifesto)
        rail = box(6)
        rail.set_valign(Gtk.Align.START)
        rail.append(actions)
        rail.append(brands)
        self.build_details()
        self.modules = [
            ("allowance", self.allowance_tile, "Percentage left in the allowance currently selected by Codex."),
            ("credits", self.credits_tile, "Available credits compared with the highest balance seen since launch."),
            ("estimate", self.estimate_tile, "Estimated time before the current allowance runs out."),
            ("vouchers", self.voucher_tile, "Number of reset vouchers. Select the tile to see expiration dates."),
            ("first-reset", self.reset_tile, "Date and time of the first upcoming allowance reset."),
            ("clock", self.reset_clock, "Switches between the countdown and the date of the next reset."),
            *[(key, tile, purpose) for (key, purpose), tile in zip((
                ("five-hour", "Percentage left in the rolling five-hour allowance."),
                ("five-reset", "Time left before the five-hour allowance resets."),
                ("weekly", "Percentage left in the weekly allowance."),
                ("weekly-reset", "Date and time when the weekly allowance resets."),
                ("detail-estimate", "Time the allowance is expected to last at the observed rate."),
                ("time-to-reset", "Time until the current allowance resets."),
                ("forecast", "Whether the allowance will last to the reset and how early it could run out.")
            ), self.detail_tiles)]
        ]
        body = self.body = box(8, True)
        body.set_margin_start(8)
        body.set_margin_end(8)
        body.set_margin_top(8)
        body.set_margin_bottom(4)
        body.append(self.signal)
        body.append(strip)
        body.append(rail)
        surface = Gtk.ScrolledWindow(child=body, hscrollbar_policy=Gtk.PolicyType.NEVER,
                                    vscrollbar_policy=Gtk.PolicyType.AUTOMATIC)
        drag = Gtk.GestureClick(button=Gdk.BUTTON_PRIMARY)
        drag.set_propagation_phase(Gtk.PropagationPhase.CAPTURE)
        drag.connect("pressed", self.begin_move)
        surface.add_controller(drag)
        self.set_child(surface)
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
        self.sync_taskbar()
        surface = self.get_surface()
        if surface is not None and surface is not self.resize_surface:
            self.resize_surface = surface
            self.layout_width = self.get_width()
            surface.connect("layout", self.on_surface_layout)
        self.fit_layout(max(300, self.get_width()))

    def sync_taskbar(self):
        surface = self.x11_surface()
        if surface is None or not self.settings_ready:
            return
        try:
            request_x11_state(surface, not self.settings.get("showInTaskbar", True), b"_NET_WM_STATE_SKIP_TASKBAR")
        except (OSError, RuntimeError) as error:
            log_event("window.taskbar.failed", str(error))

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
            request_x11_state(surface, enabled, b"_NET_WM_STATE_ABOVE")
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
            request_x11_state(surface, enabled, b"_NET_WM_STATE_ABOVE")
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
        balance = state.get("creditsBalance")
        if balance is not None:
            self.highest_credits = max(self.highest_credits, balance)
        self.set_progress(self.allowance_tile, remaining)
        self.set_progress(self.credits_tile, balance / self.highest_credits * 100 if balance is not None and self.highest_credits else None)
        self.credits_tile.update("—" if credits == "Unavailable" else credits.upper(),
                                 animate and self.credits_tile.get_mapped())
        self.credits_tile.set_detail(f"Credits remaining as reported by Codex: {credits}. The displayed balance is rounded to a whole number.")
        self.voucher_tile.show_vouchers(state.get("resetVoucherCount"), state.get("resetVoucherExpirations", []),
                                       animate and self.voucher_tile.get_mapped())
        scheduled = state.get("resetDisplays", [])
        first = scheduled[0] if scheduled else {}
        self.reset_tile.update(first.get("value") or state.get("selectedResetCompact") or "—", animate)
        details = [item["detail"] for item in scheduled] or [state.get("resetLabel", "Reset unavailable")]
        self.reset_tile.set_detail("\n".join(details))

    def animations_enabled(self):
        return self.get_mapped() and Gtk.Settings.get_default().get_property("gtk-enable-animations")

    def build_details(self):
        self.detail_tiles = (
            FlipTile("5-hour allowance", "5-hour allowance", 112),
            FlipTile("5-hour reset in", "5-hour reset in", 105),
            FlipTile("Weekly allowance", "Weekly allowance", 118),
            FlipTile("Weekly reset at", "Weekly reset at", 152),
            FlipTile("Estimated time remaining", "Estimated time remaining", 158),
            FlipTile("Time to reset", "Time to reset", 111),
            FlipTile("At the current rate", "At the current rate", 442, wrap_value=True),
        )

    @staticmethod
    def draw_details_icon(area, cr, width, height):
        color = area.get_color()
        cr.set_source_rgba(color.red, color.green, color.blue, color.alpha)
        for index, fraction in enumerate((.38, .62, .88)):
            cr.rectangle(width * (.15 + .26 * index), height * (.94 - fraction), width * .18, height * fraction)
        cr.fill()

    def details_toggled(self, button):
        opened = button.get_active() and self.settings.get("enableDetailsBar", False)
        self.apply_size()
        if opened:
            self.update_details(int(time.time()))

    def details_key_pressed(self, _, key, *_args):
        if key == Gdk.KEY_Escape and self.details_button.get_active():
            self.details_button.set_active(False)
            return True
        return False

    def update_details(self, now):
        if not self.details_button.get_active():
            return
        animate = self.animations_enabled()
        for name, percent_tile, reset_tile, countdown in (
            ("fiveHourAllowance", *self.detail_tiles[:2], True),
            ("weeklyAllowance", *self.detail_tiles[2:4], False),
        ):
            allowance = self.state.get(name) or {}
            remaining, reset = allowance.get("remaining"), allowance.get("resetsAt")
            percent_tile.update(f"{remaining:.1f}".removesuffix(".0") + "%" if remaining is not None else "—", animate)
            self.set_progress(percent_tile, remaining)
            value = allowance.get("resetCompact")
            if countdown and reset is not None:
                minutes = (max(0, reset - now) + 59) // 60
                value = f"{minutes // 60:02}:{minutes % 60:02}"
            reset_tile.update(value or "—", animate)
            reset_tile.set_detail(allowance.get("resetCompact") or "Reset time unavailable")

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
        value = "WAITING FOR RESET DATA" if waiting else prediction.get("message", "ALLOWANCE UNAVAILABLE")
        message.update(re.sub(r"(?<=\d)[DHM]\b", lambda match: match[0].lower(), value.upper()), animate)
        self.queue_fit_height()
        message.set_colors(warning=not waiting and prediction.get("warning", False))
        message.set_detail(self.state.get("forecastDetail", "Allowance data is unavailable."))

    @staticmethod
    def set_progress(tile, remaining):
        progress = None if remaining is None else max(0, min(1, remaining / 100))
        if tile.progress != progress:
            tile.progress = progress
            tile.face.queue_draw()
        color = None if remaining is None else TrafficSignal.LIGHTS[0 if remaining <= 20 else 1 if remaining <= 50 else 2]
        tile.set_colors(accent=color)

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
        self.signal.update(remaining, detail, self.animations_enabled())
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
        self.project_link.set_tooltip_text("OpenAI Usage project page\n" + status)
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
        animate = self.animations_enabled() and self.reset_clock.get_mapped()
        if show_date:
            value = " ".join((*date[:3], date[3][-2:])).upper()
            self.reset_clock.update(value, animate, duration=.32)
        elif seconds is not None:
            days = f"{seconds // 86400:02}d"
            value = f"{days} {seconds // 3600 % 24:02}h {seconds // 60 % 60:02}m {seconds % 60:02}s"
            hour = len(days) + 1
            flaps = () if self.clock_shows_date else (
                (0, len(days), .30), (hour, hour + 3, .24),
                (hour + 4, hour + 7, .19), (hour + 8, hour + 11, .145),
            )
            self.reset_clock.update(value, animate, flaps=flaps, duration=.32)
        else:
            self.reset_clock.update("—", False)
        self.clock_shows_date = show_date
        self.reset_clock.set_detail(
            f"Reset date: {self.state['resetCalendarDate']}" if show_date
            else "Countdown until the selected allowance resets."
        )

    LEGACY_MODULE_KEYS = {"credits": "showCredits", "estimate": "showEstimatedTime", "vouchers": "showAvailableResets"}

    def ordered_modules(self):
        available = {module[0]: module for module in self.modules}
        ordered = [available.pop(key) for key in self.settings.get("fieldOrder") or [] if key in available]
        return ordered + list(available.values())

    def module_enabled(self, key):
        setting = self.LEGACY_MODULE_KEYS.get(key)
        if setting is not None:
            return self.settings.get(setting, True)
        return key not in (self.settings.get("hiddenFields") or [])

    def set_module_enabled(self, key, enabled):
        setting = self.LEGACY_MODULE_KEYS.get(key)
        if setting is not None:
            self.update_settings(**{setting: enabled})
            return
        hidden = [entry for entry in self.settings.get("hiddenFields") or [] if entry != key]
        self.update_settings(hiddenFields=hidden if enabled else hidden + [key])

    def apply_size(self):
        enabled = self.settings.get("enableDetailsBar", False)
        self.details_button.set_visible(enabled)
        details = enabled and self.details_button.get_active()
        visible = [widget for key, widget, _ in self.ordered_modules()
                   if self.module_enabled(key) and (widget not in self.detail_tiles or details)]
        current = list(WrapLayout.children(self.strip))
        if current != visible:
            while self.strip.get_first_child() is not None:
                self.strip.remove(self.strip.get_first_child())
            for child in visible:
                self.strip.append(child)
        self.fit_layout(max(300, self.get_width() or 1240))

    def fit_layout(self, width):
        narrow = width < 530
        big = self.settings.get("bigSize", False) and width >= 375
        scale = 1.25 if big else 1
        (self.add_css_class if big else self.remove_css_class)("big")
        self.set_opacity(self.settings.get("windowOpacityPercent", 100) / 100)
        self.brand.set_pixel_size(64)
        self.manifesto_face.set_size_request(64, 64)
        self.manifesto_mark.set_pixel_size(84)
        self.brands.set_orientation(Gtk.Orientation.VERTICAL if narrow else Gtk.Orientation.HORIZONTAL)
        rail = 64 if narrow else (186 if self.problem.get_visible() else 158) + (32 if big else 0)
        self.actions.set_size_request(rail, -1)
        signal_width = 30 if narrow else 44
        self.signal.set_size_request(signal_width, 92 if narrow else 128)
        available = width - 56 - rail - signal_width
        position = self.settings.get("labelPosition", "above")
        for tile in (*self.metrics, *self.detail_tiles):
            tile.set_label_position(position)
            tile.resize(scale, available)
        self.reset_clock.set_label_position("hidden" if position == "hidden" else "below")
        self.reset_clock.resize(scale, available)
        if self.get_mapped():
            self.queue_fit_height()

    def queue_fit_height(self):
        if self.layout_pending:
            return
        self.layout_pending = True

        def fit():
            self.layout_pending = False
            if self.closed or not self.get_mapped():
                return False
            width = max(300, self.get_width())
            desired = self.body.measure(Gtk.Orientation.VERTICAL, width)[1]
            display = self.get_display()
            surface = self.get_surface()
            monitor = display.get_monitor_at_surface(surface) if surface is not None else None
            height = min(desired, monitor.get_geometry().height - 48) if monitor is not None else desired
            if abs(height - self.get_height()) > 1:
                self.set_default_size(width, height)
            return False

        GLib.idle_add(fit)

    def on_surface_layout(self, surface, width, height):
        if width != self.layout_width:
            self.layout_width = width
            self.fit_layout(width)

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
            ("Source", PROJECT_URL, "folder-remote-symbolic"),
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
            if isinstance(target, (Gtk.Button, Gtk.MenuButton)):
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
            if surface_x < 8 or surface_x >= self.get_width() - 8:
                edge = Gdk.SurfaceEdge.WEST if surface_x < 8 else Gdk.SurfaceEdge.EAST
                surface.begin_resize(edge, device, Gdk.BUTTON_PRIMARY, surface_x, surface_y, event.get_time())
            else:
                surface.begin_move(device, Gdk.BUTTON_PRIMARY, surface_x, surface_y, event.get_time())

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
