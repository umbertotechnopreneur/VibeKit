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
from settings_ui import LARGER_WIDGET_PERCENT, SettingsUiMixin
from ui_helpers import text, box, about_mark, about_link


CSS = b"""
window { background-color: @window_bg_color; color: @window_fg_color; }
headerbar { background-color: @window_bg_color; box-shadow: none; border: none; }
notebook, notebook > stack, notebook > header { background: transparent; border: none; box-shadow: none; }
notebook > header > tabs > tab { background: transparent; padding: 10px 20px; }
notebook > header > tabs > tab:checked { box-shadow: inset 0 -3px @accent_bg_color; }
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
window.micro.settings-open { background: transparent; border-color: transparent; box-shadow: none; }
.settings-open .widget-preview, .settings-panel {
    background: @window_bg_color;
    color: @window_fg_color;
    border-radius: 20px;
    box-shadow: inset 0 0 0 1px alpha(@window_fg_color, .18);
}
.micro .flip-caption { font-size: 12px; opacity: .85; }
.micro.big .flip-caption { font-size: 14px; }
.micro separator.metric-divider { background-color: alpha(@window_fg_color, .26); min-width: 1px; }
.micro menubutton.voucher-count > button { padding: 0; border-width: 0; min-width: 0; min-height: 0; }
.micro.big button.icon-action { padding: 6px; min-width: 21px; min-height: 21px; }
.micro.big button.icon-action image { -gtk-icon-size: 18px; }
.micro button.icon-action { padding: 5px; min-width: 18px; min-height: 18px; }
.micro button.icon-action image { -gtk-icon-size: 16px; }
.micro button.compact-action { background: alpha(@window_fg_color, .10); border: 1px solid alpha(@window_fg_color, .35); }
.micro button.icon-action.close-action { color: #e01b24; }
.micro button.icon-action.close-action image { -gtk-icon-size: 22px; }
.micro.big button.icon-action.close-action image { -gtk-icon-size: 24px; }
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
    before: str
    start: float
    duration: float

    def visible(self, now, target):
        return self.before if now < self.start + self.duration / 2 else target


@dataclass(frozen=True)
class SlotLayout:
    width: int
    font_size: float
    pitch: float
    columns: int
    rows: int
    row_height: int

    @property
    def start(self):
        return (self.width - self.columns * self.pitch) / 2


class FlipTile(LabeledDisplay):
    DEFAULT_SHADOW = CreaseShadow(5, (52/255, 10/255, 13/255, .44), (24/255, 6/255, 7/255))
    CHARACTER_ADVANCE = .62
    VALUE_FONT_SIZE = 20
    MINIMUM_WIDTH = 30
    FACE_PADDING = 12
    INFO_WIDTH = 22
    VERTICAL_PADDING = 2

    def __init__(self, name, caption, width, *, info=False, wrap_value=False, worn=False,
                 sizing_text=None, crease_shadow=DEFAULT_SHADOW, digit_delay=(300, 1000), compact_accent=False):
        face = Gtk.DrawingArea()
        display = box(4, True)
        display.set_halign(Gtk.Align.CENTER)
        display.append(face)
        super().__init__(caption, display)
        self.face = face
        self.name = name
        self.base_width = width
        self.wrap_value = wrap_value
        self.sizing_text = sizing_text
        self.crease_shadow = crease_shadow
        self.compact_accent = compact_accent
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
        self.layout = None
        self.characters = []
        self.line_spans = []
        self.digit_flips = {}
        self.ticking = False
        self.face.set_draw_func(self.draw)
        self.face.update_property([Gtk.AccessibleProperty.LABEL], [f"{name}: unavailable"])
        self.resize(1)

    def text_columns(self, value):
        return 1 if self.wrap_value else max(1, len(value), len(self.sizing_text or ""))

    def face_insets(self):
        return self.FACE_PADDING + (self.INFO_WIDTH if self.info is not None else 0)

    def font_limit(self, available, value):
        return max(1, available - self.face_insets()) / (self.CHARACTER_ADVANCE * self.text_columns(value))

    def width_for_font(self, font_size):
        text_width = font_size * self.CHARACTER_ADVANCE * self.text_columns(self.value)
        return max(self.MINIMUM_WIDTH, math.ceil(text_width + self.face_insets()))

    def resize(self, scale, available=None, font_size=None):
        available = round(self.base_width * scale) if available is None else available
        available = max(self.MINIMUM_WIDTH, available)
        font_size = self.VALUE_FONT_SIZE * scale if font_size is None else font_size
        if (scale, available, font_size) == (self.scale, self.available_width, getattr(self, "font_size", None)):
            return
        self.scale, self.available_width, self.font_size = scale, available, font_size
        self.update_slots(self.value, False, (), .20)
        self.face.queue_draw()

    @staticmethod
    def wrap(value, columns):
        while len(value) > columns:
            split = value.rfind(" ", 0, columns + 1)
            end = split if split > 0 else columns
            yield value[:end]
            value = value[end:].lstrip(" ")
        yield value

    def update_slots(self, value, animate, flaps, duration):
        width = self.available_width - (self.INFO_WIDTH if self.info is not None else 0)
        space = max(1, width - self.FACE_PADDING)
        minimum = 1 if self.wrap_value else max(1, len(value), len(self.sizing_text or ""))
        font_size = min(self.font_size, self.font_limit(self.available_width, value))
        pitch = font_size * self.CHARACTER_ADVANCE
        columns = max(minimum, math.floor(space / pitch + .00001))
        lines = tuple(self.wrap(value, columns)) if self.wrap_value else (value,)
        row_height = round(26 * self.scale) + 2 * self.VERTICAL_PADDING
        layout = SlotLayout(width, font_size, pitch, columns, len(lines), row_height)
        now = time.monotonic()
        rebuild = layout != self.layout
        previous_columns = self.layout.columns if self.layout else 0
        previous = [self.visible_character(i, now) for i in range(len(self.characters))] if rebuild and animate else []
        if rebuild:
            self.layout = layout
            self.characters = [" "] * (columns * len(lines))
            self.digit_flips.clear()
            self.face.set_content_width(width)
            self.face.set_content_height(layout.row_height * layout.rows)

        self.line_spans = []
        for row, line in enumerate(lines):
            first = (columns - len(line)) // 2
            self.line_spans.append((first, first + len(line)))
            for column in range(columns):
                index = column - first
                target = line[index] if 0 <= index < len(line) else " "
                slot = row * columns + column
                if not rebuild and self.characters[slot] == target:
                    continue
                if rebuild:
                    old_column = column + math.trunc((previous_columns - columns) / 2)
                    old_slot = row * previous_columns + old_column
                    before = previous[old_slot] if 0 <= old_column < previous_columns and old_slot < len(previous) else " "
                else:
                    before = self.visible_character(slot, now)
                self.digit_flips.pop(slot, None)
                self.characters[slot] = target
                if animate and before != target:
                    flap_duration = next((length for start, end, length in flaps if start <= index < end), duration)
                    delay = random.randint(*self.digit_delay) / 1000
                    self.digit_flips[slot] = DigitFlip(before, now + delay, flap_duration)

    def visible_character(self, slot, now):
        target = self.characters[slot]
        flip = self.digit_flips.get(slot)
        return flip.visible(now, target) if flip else target

    def set_detail(self, detail):
        if self.face.get_tooltip_text() != detail:
            self.face.set_tooltip_text(detail)
            self.caption.set_tooltip_text(f"{self.name}\n{detail}")
            if self.info is not None:
                self.info.set_tooltip_text(detail)

    def update(self, value, animate, *, flaps=(), duration=.20):
        if not animate and self.digit_flips:
            self.digit_flips.clear()
            self.face.queue_draw()
        if value == self.value:
            return
        self.update_slots(value, animate and self.value not in ("—", "--"), flaps, duration)
        self.value = value
        accessible_value = "unavailable" if value == "—" else value
        self.face.update_property([Gtk.AccessibleProperty.LABEL], [f"{self.name}: {accessible_value}"])
        self.face.queue_draw()
        if self.digit_flips and not self.ticking:
            self.ticking = True
            self.face.add_tick_callback(self.tick)

    def set_colors(self, *, accent=None, warning=False):
        ink = (1, .71, .19) if warning else (1, 1, 1)
        if self.ink == ink and self.accent == accent:
            return
        self.ink, self.accent = ink, accent
        self.face.queue_draw()

    def tick(self, widget, frame):
        now = time.monotonic()
        if any(now >= flip.start for flip in self.digit_flips.values()):
            widget.queue_draw()
            self.digit_flips = {index: flip for index, flip in self.digit_flips.items()
                               if now < flip.start + flip.duration}
        self.ticking = bool(self.digit_flips)
        return self.ticking

    def draw(self, area, cr, width, height):
        self.draw_face(cr, width, height)
        layout = self.layout
        font = Pango.FontDescription("Monospace Bold")
        font.set_absolute_size(layout.font_size * Pango.SCALE)
        glyph = PangoCairo.create_layout(cr)
        glyph.set_font_description(font)
        now = time.monotonic()
        for row, (first, end) in enumerate(self.line_spans):
            y = row * layout.row_height
            for column in range(layout.columns):
                x = layout.start + column * layout.pitch
                slot = row * layout.columns + column
                char = self.characters[slot]
                flip = self.digit_flips.get(slot)
                if flip and flip.start < now < flip.start + flip.duration:
                    self.draw_flip(cr, glyph, char, flip, now, x, y)
                else:
                    self.draw_character(cr, glyph, self.visible_character(slot, now), x, y)
                if first <= column < end - 1:
                    cr.set_source_rgba(.02, .016, .016, .9)
                    cr.rectangle(x + layout.pitch - 1, y + 4, 1, layout.row_height - 8)
                    cr.fill()
            self.draw_seam(cr, width, y, layout.row_height)
        self.draw_progress(cr, width, height)

    def draw_character(self, cr, glyph, char, x, y):
        if char == " ":
            return
        glyph.set_text(char, -1)
        text_width, text_height = glyph.get_size()
        cr.move_to(x + (self.layout.pitch - text_width / Pango.SCALE) / 2,
                   y + (self.layout.row_height - text_height / Pango.SCALE) / 2)
        cr.set_source_rgb(*self.ink)
        PangoCairo.show_layout(cr, glyph)

    def draw_flip(self, cr, glyph, char, flip, now, x, y):
        phase = (now - flip.start) / flip.duration
        self.draw_half(cr, glyph, char, x, y, True, 1)
        self.draw_half(cr, glyph, flip.before, x, y, False, 1)
        if phase < .5:
            self.draw_half(cr, glyph, flip.before, x, y, True, math.cos(phase * math.pi))
        else:
            self.draw_half(cr, glyph, char, x, y, False, math.sin((phase - .5) * math.pi))

    def draw_half(self, cr, glyph, char, x, y, top, scale):
        height = self.layout.row_height / 2
        center = y + height
        cr.save()
        cr.rectangle(x, y if top else center, self.layout.pitch, height)
        cr.clip()
        cr.translate(0, center)
        cr.scale(1, max(.001, scale))
        cr.translate(0, -center)
        shade = .12 if top else .06
        cr.set_source_rgb(shade, shade, shade)
        cr.rectangle(x, y if top else center, self.layout.pitch, height)
        cr.fill()
        self.draw_character(cr, glyph, char, x, y)
        cr.restore()

    def draw_face(self, cr, width, height):
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
        if self.grain:
            for shade, opacity in ((0, .24), (1, .12)):
                cr.set_source_rgba(shade, shade, shade, opacity)
                for x, y, tone in self.grain:
                    if int(tone * 2) == shade:
                        cr.rectangle(4 + x * (width - 8), 3 + y * (height - 6), .4 + tone, .7)
                cr.fill()
        cr.restore()

    def draw_seam(self, cr, width, y, height):
        cr.save()
        center = y + height / 2
        shadow = self.crease_shadow
        if shadow is not None and shadow.spread > 0:
            spread = shadow.spread * self.scale
            gradient = cairo.LinearGradient(0, center - spread, 0, center + spread)
            gradient.add_color_stop_rgba(0, *shadow.color[:3], 0)
            gradient.add_color_stop_rgba(.5, *shadow.color)
            gradient.add_color_stop_rgba(1, *shadow.color[:3], 0)
            cr.set_source(gradient)
            cr.rectangle(2, center - spread, width - 4, spread * 2)
            cr.fill()
        cr.set_source_rgb(*(shadow.line if shadow else (.02, .02, .02)))
        cr.set_line_width(1.5)
        cr.move_to(2, center)
        cr.line_to(width - 2, center)
        cr.stroke()
        cr.set_source_rgb(.42, .42, .42)
        for x in (1, width - 4):
            cr.rectangle(x, center - 4.5, 3, 9)
            cr.fill()
        cr.restore()

    def draw_progress(self, cr, width, height):
        if self.accent and self.progress:
            y = height - (2 if self.compact_accent else 3)
            cr.save()
            cr.set_source_rgb(*self.accent)
            cr.set_line_width(1 if self.compact_accent else 2)
            cr.set_line_cap(cairo.LINE_CAP_ROUND)
            cr.move_to(7, y)
            cr.line_to(7 + (width - 14) * self.progress, y)
            cr.stroke()
            cr.restore()


class VoucherTile(FlipTile):
    def __init__(self):
        super().__init__("Available vouchers", "Vouchers", 104)
        self.display.remove(self.face)
        self.count_button = Gtk.MenuButton(child=self.face, has_frame=False,
                                           tooltip_text="See when available vouchers expire")
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
        summary = "Voucher balance unavailable" if count is None else f"Vouchers available to use now: {count}"
        details = [f"Voucher {index}: {expiry['detail']}" for index, expiry in enumerate(expirations, 1)]
        if count and not details:
            details.append("Expiration dates are unavailable.")
        self.expirations.set_text("\n".join([summary, *details]))
        self.count_button.update_property([Gtk.AccessibleProperty.LABEL],
                                          [summary + ". Show voucher expirations."])


class WrapLayout(Gtk.LayoutManager):
    def __init__(self, spacing=8, center=False):
        super().__init__()
        self.spacing = spacing
        self.center = center
        self.tile_widths = {}
        self.single_row = False

    def do_get_request_mode(self, widget):
        return Gtk.SizeRequestMode.HEIGHT_FOR_WIDTH

    def do_measure(self, widget, orientation, for_size):
        if orientation == Gtk.Orientation.HORIZONTAL:
            sizes = [self.tile_widths.get(child) or child.measure(orientation, -1)[1] for child in self.children(widget)]
            natural = sum(sizes) + max(0, len(sizes) - 1) * self.spacing
            requested = widget.get_size_request().width
            if requested > 0:
                natural = min(natural, requested)
            return 0, natural, -1, -1
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
            w = self.tile_widths.get(child)
            if w is None:
                w = max(1, min(width, child.measure(Gtk.Orientation.HORIZONTAL, -1)[1]))
            h = child.measure(Gtk.Orientation.VERTICAL, w)[1]
            if row and not self.single_row and x + self.spacing + w > width:
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
        super().__init__(valign=Gtk.Align.CENTER)
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
    APP_ICON_SIZE = 64
    MANIFESTO_ICON_SIZE = 84
    BRAND_SPACING = 8

    def __init__(self, app):
        # Adw.ApplicationWindow imposes a minimum height larger than the strip.
        super().__init__(application=app, title="OpenAI Usage", default_width=1240, default_height=80,
                         icon_name="biz.umbertogiacobbi.OpenAIUsage")
        self.state = {}
        self.clock_cycle_start = None
        self.settings = {}
        self.settings_ready = False
        self.settings_panel = None
        self.settings_window_size = None
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
        self.fit_source = 0
        self.expanded_size = None
        self.initial_fit_done = False
        self.layout_width = 1240
        self.resize_surface = None
        self.highest_credits = 0
        self.connect("close-request", self.on_close)

        strip = self.strip = wrap_box()
        strip.set_hexpand(True)
        strip.set_valign(Gtk.Align.START)
        self.brand = Gtk.Image.new_from_file(str(ROOT / "Branding/app-icon-512.png"))
        self.brand.set_pixel_size(self.APP_ICON_SIZE)
        self.project_link = Gtk.LinkButton(uri=PROJECT_URL, child=self.brand, valign=Gtk.Align.CENTER,
                                           tooltip_text="OpenAI Usage project page\nConnecting to Codex…")
        self.manifesto_mark = about_mark("VibeWare.png", self.MANIFESTO_ICON_SIZE)
        self.manifesto_mark.set_pixel_size(self.MANIFESTO_ICON_SIZE)
        self.manifesto_mark.set_size_request(self.MANIFESTO_ICON_SIZE, self.MANIFESTO_ICON_SIZE)
        self.manifesto_mark.set_halign(Gtk.Align.CENTER)
        self.manifesto_mark.set_valign(Gtk.Align.CENTER)
        self.manifesto_mark.set_can_target(False)
        manifesto = Gtk.LinkButton(uri=MANIFESTO_URL, child=self.manifesto_mark, valign=Gtk.Align.CENTER,
                                   tooltip_text="VibeWare manifesto")
        for link, label in ((self.project_link, "OpenAI Usage project page"), (manifesto, "VibeWare manifesto")):
            link.add_css_class("icon-action")
            link.add_css_class("brand-action")
            link.update_property([Gtk.AccessibleProperty.LABEL], [label])

        self.signal = TrafficSignal()
        self.signal.set_valign(Gtk.Align.START)
        self.allowance_tile = FlipTile("Allowance remaining", "Allowance left", 115, worn=True, compact_accent=True)
        self.credits_tile = FlipTile("Remaining Credits", "Remaining Credits", 108, sizing_text="000,000", worn=True, compact_accent=True)
        self.estimate_tile = FlipTile("Time to run out", "Time to run out", 152)
        self.voucher_tile = VoucherTile()
        self.voucher_expiry_tile = FlipTile("Next voucher expiry", "Next voucher expiry", 160, info=True, sizing_text="00/00 00:00")
        self.metrics = (self.allowance_tile, self.credits_tile, self.estimate_tile, self.voucher_tile, self.voucher_expiry_tile)
        self.reset_clock = FlipTile("Auto reset clock", "Auto reset clock", 403,
                                    sizing_text="WEDNESDAY 00 SEPTEMBER 00")

        actions = self.actions = box(3, True)
        actions.set_halign(Gtk.Align.END)
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
        compact_icon = Gtk.DrawingArea(content_width=16, content_height=16, can_target=False)
        def draw_compact_icon(_area, cr, _width, _height):
            color = self.get_color()
            cr.set_source_rgba(color.red, color.green, color.blue, color.alpha)
            for x, height in ((2, 6), (7, 10), (12, 15)):
                cr.rectangle(x, 16 - height, 3, height)
            cr.fill()
        compact_icon.set_draw_func(draw_compact_icon)
        self.details_button = Gtk.ToggleButton(child=compact_icon, valign=Gtk.Align.CENTER)
        self.details_button.add_css_class("icon-action")
        self.details_button.add_css_class("compact-action")
        self.details_button.set_active(True)
        actions.append(self.pin)
        self.connect("map", self.on_window_mapped)
        for icon,title,callback in [("emblem-system-symbolic","Settings",self.toggle_settings),
                                    ("help-about-symbolic","About OpenAI Usage",self.show_about)]:
            button=Gtk.Button(icon_name=icon,tooltip_text=title)
            button.add_css_class("icon-action")
            button.connect("clicked",lambda _,cb=callback:cb())
            actions.append(button)
        close = Gtk.Button(icon_name="window-close-symbolic", tooltip_text="Close")
        close.add_css_class("icon-action")
        close.add_css_class("close-action")
        close.connect("clicked", lambda _: self.close())
        actions.append(self.details_button)
        actions.append(close)
        brands = self.brands = box(self.BRAND_SPACING, True)
        brands.set_halign(Gtk.Align.CENTER)
        brands.append(self.project_link)
        brands.append(manifesto)
        rail = self.rail = box(6)
        rail.set_valign(Gtk.Align.START)
        rail.append(actions)
        rail.append(brands)
        self.build_details()
        self.modules = [
            ("allowance", self.allowance_tile, "Percentage still available in the selected Codex quota."),
            ("credits", self.credits_tile, "Remaining credit balance. The bar compares it with the highest balance seen this session."),
            ("estimate", self.estimate_tile, "Estimated time until the selected quota runs out at your observed usage rate."),
            ("vouchers", self.voucher_tile, "Vouchers you can use now for a manual quota reset. Click the count for their expiration dates."),
            # Keep the existing field ID for saved visibility and ordering.
            ("first-reset", self.voucher_expiry_tile, "When the next available voucher with a reported date expires. A dash means no date is available."),
            ("clock", self.reset_clock, "Alternates between a countdown and the date when the selected quota renews automatically."),
            *[(key, tile, purpose) for (key, purpose), tile in zip((
                ("five-hour", "Percentage remaining in the five-hour quota."),
                ("five-reset", "Hours and minutes until the five-hour quota renews automatically."),
                ("weekly", "Percentage remaining in the weekly quota."),
                ("weekly-reset", "Local date and time when the weekly quota renews automatically."),
                ("detail-estimate", "Same usage estimate as Time to run out, shown in the expanded details."),
                ("time-to-reset", "Time left until the selected quota renews automatically. The Auto reset clock also shows this countdown."),
                ("forecast", "Compares the estimated time to run out with the automatic renewal of the selected quota.")
            ), self.detail_tiles)]
        ]
        body = self.body = box(8, True)
        body.set_margin_start(8)
        body.set_margin_end(8)
        body.set_margin_top(8)
        body.set_margin_bottom(8)
        body.append(self.signal)
        body.append(strip)
        body.append(rail)
        surface = self.widget_preview = Gtk.ScrolledWindow(child=body, overlay_scrolling=True, hscrollbar_policy=Gtk.PolicyType.NEVER,
                                    vscrollbar_policy=Gtk.PolicyType.AUTOMATIC)
        drag = Gtk.GestureClick(button=Gdk.BUTTON_PRIMARY)
        drag.set_propagation_phase(Gtk.PropagationPhase.CAPTURE)
        drag.connect("pressed", self.begin_move)
        surface.add_css_class("widget-preview")
        surface.set_vexpand(True)
        self.window_content = box(6)
        self.window_content.append(surface)
        self.window_content.add_controller(drag)
        motion = Gtk.EventControllerMotion()
        motion.connect("motion", self.edge_cursor)
        motion.connect("leave", lambda controller: controller.get_widget().set_cursor(None))
        self.window_content.add_controller(motion)
        self.set_child(self.window_content)
        self.details_button.connect("toggled", self.details_toggled)
        self.problem.connect("notify::visible", lambda *_: self.fit_layout(self.layout_width))
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
            surface.connect("layout", self.on_surface_layout)
        if surface is not None:
            self.on_surface_layout(surface, surface.get_width(), surface.get_height())
        self.fit_initial_size()

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
        self.fit_layout(self.layout_width)
        self.fit_initial_size()
        return False

    def update_metrics(self, state):
        animate = self.animations_enabled()
        remaining = state.get("remaining")
        self.allowance_tile.update(f"{remaining:g}%" if remaining is not None else "—", animate)
        self.allowance_tile.set_detail(f"Percentage available in the {self.selected_quota_name()}. "
                                       + state.get("resetLabel", "Renewal time unavailable."))
        forecast = state.get("forecastCompact", "—") if state.get("forecastAvailable", False) else "—"
        self.estimate_tile.update(forecast, animate and self.estimate_tile.get_mapped())
        estimate_detail = (f"Estimated time until the {self.selected_quota_name()} runs out at your observed usage rate. "
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

    def update_voucher_expiry(self, now):
        expirations = self.state.get("resetVoucherExpirations") or []
        upcoming = (entry for entry in expirations if (entry.get("expiresAt") or 0) > now)
        count = self.state.get("resetVoucherCount")
        first = min(upcoming, default=None, key=lambda entry: entry["expiresAt"]) if count else None
        self.voucher_expiry_tile.update(first["value"] if first else "—", False)
        if first:
            detail = "Earliest reported expiration of an available voucher: " + first["detail"]
        elif count == 0:
            detail = "No vouchers are available."
        else:
            detail = "No upcoming expiration date was reported for an available voucher."
        self.voucher_expiry_tile.set_detail(detail)

    def selected_quota_name(self):
        key = self.state.get("selectedWindow")
        selected = next((window for window in self.state.get("windows", []) if window["key"] == key), {})
        return {300: "five-hour quota", 10080: "weekly quota"}.get(selected.get("minutes"), "selected quota")

    def animations_enabled(self):
        return self.get_mapped() and Gtk.Settings.get_default().get_property("gtk-enable-animations")

    def build_details(self):
        self.detail_tiles = (
            FlipTile("5-hour allowance", "5-hour allowance", 112),
            FlipTile("5-hour auto reset in", "5-hour reset in", 125),
            FlipTile("Weekly allowance", "Weekly allowance", 118),
            FlipTile("Weekly auto reset", "Weekly auto reset", 152),
            FlipTile("Run-out estimate", "Run-out estimate", 158),
            FlipTile("Selected auto reset in", "Auto reset in", 132),
            FlipTile("Will the quota last?", "Will the quota last?", 442, wrap_value=True),
        )

    def details_toggled(self, button):
        surface = self.get_surface()
        restore = self.expanded_size if button.get_active() else None
        if not button.get_active() and surface is not None:
            self.expanded_size = self.settings_window_size or (surface.get_width(), surface.get_height())
        elif button.get_active():
            self.expanded_size = None
        self.apply_size()
        if self.settings_panel is None:
            self.queue_fit_height(restore)
        elif restore:
            self.settings_window_size = restore
        elif not button.get_active() and self.settings_window_size is not None:
            self.settings_window_size = (self.settings_window_size[0], 0)

    def details_key_pressed(self, _, key, *_args):
        if key == Gdk.KEY_Escape and self.settings_panel is not None:
            self.hide_settings()
            return True
        if key == Gdk.KEY_Escape and self.details_button.get_active():
            self.details_button.set_active(False)
            return True
        return False

    def update_details(self, now):
        if not any(tile.get_parent() is self.strip for tile in self.detail_tiles):
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
                value = f"{minutes // 60:02}h {minutes % 60:02}m"
            reset_tile.update(value or "—", animate)
            quota = "five-hour quota" if countdown else "weekly quota"
            timestamp = allowance.get("resetCompact")
            zone = self.settings.get("timeZoneId", "local time")
            reset_tile.set_detail(f"The {quota} renews automatically on {timestamp} ({zone})." if timestamp
                                  else f"Automatic renewal time for the {quota} is unavailable.")
            percent_tile.set_detail(f"Percentage remaining in the {quota}.")

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
        quota = self.selected_quota_name()
        reset_tile.set_detail(f"Time left until the {quota} renews automatically. "
                              + self.state.get("resetLabel", "Renewal time unavailable."))
        estimate.set_detail(f"Estimated time until the {quota} runs out at your observed usage rate. "
                            + self.state.get("forecastDetail", "Collecting quota history."))
        waiting = passed and (remaining is None or remaining > 0)
        value = "WAITING FOR RESET DATA" if waiting else prediction.get("message", "ALLOWANCE UNAVAILABLE")
        message.update(re.sub(r"(?<=\d)[DHM]\b", lambda match: match[0].lower(), value.upper()), animate)
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
        now = int(time.time())
        end=self.state.get("resetsAt")
        seconds=max(0,end-now) if end is not None else None
        self.update_clock(seconds)
        self.update_details(now)
        self.update_voucher_expiry(now)
        if self.signal.get_mapped():
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
            flaps = (
                (0, len(days), .30), (hour, hour + 3, .24),
                (hour + 4, hour + 7, .19), (hour + 8, hour + 11, .145),
            )
            self.reset_clock.update(value, animate, flaps=flaps, duration=.32)
        else:
            self.reset_clock.update("—", False)
        quota = self.selected_quota_name()
        self.reset_clock.set_detail(
            f"Automatic renewal of the {quota}. "
            + self.state.get("resetLabel", "Renewal time unavailable.")
            + " This display alternates between the date and the time remaining."
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
        expanded = self.details_button.get_active()
        details = expanded and self.settings.get("enableDetailsBar", False)
        visible = [widget for key, widget, _ in self.ordered_modules()
                   if self.module_enabled(key) and (widget not in self.detail_tiles or details)]
        label = "Compact mode" if expanded else "Expand widget"
        self.details_button.set_tooltip_text(label)
        self.details_button.update_property([Gtk.AccessibleProperty.LABEL], [label])
        current = list(WrapLayout.children(self.strip))
        if current != visible:
            while self.strip.get_first_child() is not None:
                self.strip.remove(self.strip.get_first_child())
            for child in visible:
                self.strip.append(child)
            self.update_details(int(time.time()))
        self.fit_layout(self.layout_width)

    def fit_layout(self, width):
        layout = self.strip.get_layout_manager()
        expanded = self.details_button.get_active()
        big = self.settings.get("bigSize", False) and width >= 375
        scale = 1 + LARGER_WIDGET_PERCENT / 100 if big else 1
        (self.add_css_class if big else self.remove_css_class)("big")
        opacity = self.settings.get("windowOpacityPercent", 100) / 100
        settings_open = self.settings_panel is not None
        self.set_opacity(1 if settings_open else opacity)
        self.widget_preview.set_opacity(opacity if settings_open else 1)
        tiles = list(WrapLayout.children(self.strip))
        preferred = {tile: round(tile.base_width * scale) for tile in tiles}

        command_width = self.actions.measure(Gtk.Orientation.HORIZONTAL, -1)[1]
        gaps = max(0, len(tiles) - 1) * layout.spacing
        margins = self.body.get_margin_start() + self.body.get_margin_end() + 2
        spacing = self.body.get_spacing()
        plain_width = command_width + margins + spacing
        # Reserve the real command width before deciding whether metrics wrap.
        decorate = expanded and len(tiles) > 1 and sum(preferred.values()) + gaps > width - plain_width
        self.signal.set_visible(decorate)
        self.brands.set_visible(decorate)
        signal_width = 30 if width < 530 else 44
        rail_width = self.rail.measure(Gtk.Orientation.HORIZONTAL, -1)[1]
        chrome = rail_width + margins + spacing
        if decorate:
            chrome += signal_width + spacing

        minimum = max(300, chrome + FlipTile.MINIMUM_WIDTH)
        if not expanded:
            minimum_tiles = len(tiles) * FlipTile.MINIMUM_WIDTH
            minimum = max(minimum, chrome + gaps + round(max(minimum_tiles, sum(preferred.values()) * .6)))
            surface = self.get_surface()
            monitor = self.get_display().get_monitor_at_surface(surface) if surface else None
            if monitor:
                minimum = min(minimum, monitor.get_geometry().width)
        available = max(FlipTile.MINIMUM_WIDTH, max(width, minimum) - chrome)
        if expanded:
            # On narrow windows, leave room for two ordinary fields per row.
            # The calendar and the long forecast still get the full row.
            half_row = max(100, (available - layout.spacing) // 2)
            widths = {tile: min(available, half_row if len(tiles) > 2 and available < 320 and tile.base_width <= 160
                               else available, preferred[tile]) for tile in tiles}
        else:
            widths = self.fit_compact_widths(preferred, available - gaps, scale)
        if layout.tile_widths != widths or layout.single_row != (not expanded):
            layout.tile_widths, layout.single_row = widths, not expanded
            self.strip.queue_resize()
        position = self.settings.get("labelPosition", "above")
        for tile in (*self.metrics, self.reset_clock, *self.detail_tiles):
            tile.set_label_position(position)
        self.fit_tile_fonts(scale, widths)

        strip_height = self.strip.measure(Gtk.Orientation.VERTICAL, available)[1]
        rail_height = self.rail.measure(Gtk.Orientation.VERTICAL, rail_width)[1]
        if decorate:
            self.signal.set_size_request(signal_width, min(128, strip_height))
        vertical_insets = self.body.get_margin_top() + self.body.get_margin_bottom() + 2
        # Keep the controls and both logos in view even when a resized window needs scrolling.
        height = max(strip_height, rail_height) + vertical_insets
        if settings_open:
            surface = self.get_surface()
            monitor = self.get_display().get_monitor_at_surface(surface) if surface else None
            preview_cap = min(240, monitor.get_geometry().height // 3) if monitor else 240
            preview = max(rail_height + vertical_insets, min(height, preview_cap))
            self.widget_preview.set_size_request(-1, preview - 2)
            self.set_size_request(minimum, preview + 124)
            height = preview + 450
        else:
            self.widget_preview.set_size_request(-1, -1)
            self.set_size_request(minimum, rail_height + vertical_insets)
        return max(width, minimum), height

    @staticmethod
    def fit_compact_widths(preferred, available, scale):
        # Reserve enough room for the same text size in every field before
        # distributing spare space towards the preferred widths.
        low, high = 0, FlipTile.VALUE_FONT_SIZE * scale
        for tile, width in preferred.items():
            high = min(high, tile.font_limit(width, tile.value))
        for _ in range(8):
            size = (low + high) / 2
            if sum(min(preferred[tile], tile.width_for_font(size)) for tile in preferred) <= available:
                low = size
            else:
                high = size
        widths = {tile: min(width, tile.width_for_font(low)) for tile, width in preferred.items()}
        used = sum(widths.values())
        flexible = sum(preferred.values()) - used
        fraction = max(0, min(1, (available - used) / flexible)) if flexible else 0
        return {tile: width + int((preferred[tile] - width) * fraction) for tile, width in widths.items()}

    @staticmethod
    def fit_tile_fonts(scale, widths):
        font_size = FlipTile.VALUE_FONT_SIZE * scale
        for tile, width in widths.items():
            font_size = min(font_size, tile.font_limit(width, tile.value))
        for tile, width in widths.items():
            tile.resize(scale, width, font_size)

    def fit_initial_size(self):
        if self.settings_panel is None and not self.initial_fit_done and self.settings_ready and self.get_mapped():
            self.initial_fit_done = True
            self.queue_fit_height()

    def cancel_fit_height(self):
        if self.fit_source:
            GLib.source_remove(self.fit_source)
            self.fit_source = 0

    def queue_fit_height(self, restore=None, minimum_width=0):
        self.cancel_fit_height()

        def fit():
            self.fit_source = 0
            surface = self.get_surface()
            if self.closed or not self.get_mapped() or surface is None:
                return False
            width = restore[0] if restore else surface.get_width()
            width, height = self.fit_layout(max(width, minimum_width))
            if restore:
                height = max(height, restore[1])
            monitor = self.get_display().get_monitor_at_surface(surface)
            if monitor:
                height = min(height, monitor.get_geometry().height - 48)
            self.set_default_size(width, height)

            def finish_resize():
                self.fit_source = 0
                if not self.closed and self.get_mapped() and self.get_surface() == surface:
                    if abs(surface.get_width() - width) > 2 or abs(surface.get_height() - height) > 12:
                        self.set_default_size(width, height)
                return False

            # GTK can receive a configure for the new minimum size before the
            # requested size takes effect, particularly when both axes change.
            self.fit_source = GLib.timeout_add(100, finish_resize)
            return False

        self.fit_source = GLib.idle_add(fit)

    def on_surface_layout(self, surface, width, height):
        if width > 1 and width != self.layout_width:
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

    @staticmethod
    def resize_edge(widget, x, y):
        west, east = x < 8, x >= widget.get_width() - 8
        north, south = y < 8, y >= widget.get_height() - 8
        if north:
            return Gdk.SurfaceEdge.NORTH_WEST if west else Gdk.SurfaceEdge.NORTH_EAST if east else Gdk.SurfaceEdge.NORTH
        if south:
            return Gdk.SurfaceEdge.SOUTH_WEST if west else Gdk.SurfaceEdge.SOUTH_EAST if east else Gdk.SurfaceEdge.SOUTH
        return Gdk.SurfaceEdge.WEST if west else Gdk.SurfaceEdge.EAST if east else None

    def edge_cursor(self, controller, x, y):
        widget = controller.get_widget()
        edge = self.resize_edge(widget, x, y)
        cursor = {
            Gdk.SurfaceEdge.NORTH: "n-resize",
            Gdk.SurfaceEdge.SOUTH: "s-resize",
            Gdk.SurfaceEdge.WEST: "w-resize",
            Gdk.SurfaceEdge.EAST: "e-resize",
            Gdk.SurfaceEdge.NORTH_WEST: "nw-resize",
            Gdk.SurfaceEdge.NORTH_EAST: "ne-resize",
            Gdk.SurfaceEdge.SOUTH_WEST: "sw-resize",
            Gdk.SurfaceEdge.SOUTH_EAST: "se-resize",
        }.get(edge)
        widget.set_cursor_from_name(cursor)

    def begin_move(self, gesture, presses, x, y):
        if self.closed or presses != 1:
            return
        widget = gesture.get_widget()
        edge = self.resize_edge(widget, x, y)
        if edge is None:
            target = widget.pick(x, y, Gtk.PickFlags.DEFAULT)
            while target is not None:
                if (target is self.settings_panel
                        or isinstance(target, (Gtk.Button, Gtk.MenuButton, Gtk.Scrollbar))):
                    gesture.set_state(Gtk.EventSequenceState.DENIED)
                    return
                target = target.get_parent()
        device = gesture.get_current_event_device()
        surface = self.get_surface()
        if surface is None or device is None:
            return
        point = widget.translate_coordinates(self, x, y)
        if point is None:
            return
        # GTK native coordinates include the translation from the GDK surface.
        offset_x, offset_y = self.get_surface_transform()
        sx, sy = point[0] - offset_x, point[1] - offset_y
        timestamp = gesture.get_current_event_time()
        gesture.set_state(Gtk.EventSequenceState.CLAIMED)
        if edge is not None:
            self.cancel_fit_height()
            surface.begin_resize(edge, device, Gdk.BUTTON_PRIMARY, sx, sy, timestamp)
        else:
            surface.begin_move(device, Gdk.BUTTON_PRIMARY, sx, sy, timestamp)

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
