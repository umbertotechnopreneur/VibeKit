#!/usr/bin/env python3
"""Native GTK 4 / libadwaita presentation for the shared C# account reader."""
from __future__ import annotations

import ctypes
import ctypes.util
import datetime as dt
import fcntl
import locale
import math
import os
import re
import random
from dataclasses import dataclass
from decimal import Decimal, InvalidOperation, ROUND_HALF_UP
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
.micro .flip-caption { font-size: 12px; opacity: .85; }
.micro.big .flip-caption { font-size: 14px; }
.micro separator.metric-divider { background-color: alpha(@window_fg_color, .26); min-width: 1px; }
.micro menubutton.voucher-count > button { padding: 0; border-width: 0; min-width: 0; min-height: 0; }
.micro menubutton.widget-menu > button { padding: 5px; min-width: 18px; min-height: 18px; }
.micro menubutton.widget-menu > button image { color: @accent_color; -gtk-icon-size: 18px; }
.micro.big menubutton.widget-menu > button { padding: 6px; min-width: 21px; min-height: 21px; }
.micro.big menubutton.widget-menu > button image { -gtk-icon-size: 21px; }
.widget-menu-popup button, .widget-menu-popup .menu-switch-row { padding: 6px 10px; }
.widget-menu-popup .menu-pin { color: @blue_3; }
.widget-menu-popup .menu-compact { color: @purple_2; }
.widget-menu-popup .menu-settings { color: @orange_3; }
.widget-menu-popup .menu-about { color: @green_4; }
.widget-menu-popup .menu-vibeware { color: @yellow_4; }
.widget-menu-popup .menu-exit { color: @red_3; }
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


def flap_grain(seed):
    # Keep the two stamped textures as vector paths, shared by all display sizes.
    randomizer = random.Random(seed)
    cr = cairo.Context(cairo.RecordingSurface(cairo.CONTENT_ALPHA, None))
    for _ in range(135):
        cr.rectangle(randomizer.random() * 150, randomizer.random() * 44,
                     .35 + randomizer.random() * 1.4, .35 + randomizer.random() * .8)
    return cr.copy_path()


class FlipTile(LabeledDisplay):
    DEFAULT_SHADOW = CreaseShadow(5, (52/255, 10/255, 13/255, .44), (24/255, 6/255, 7/255))
    CHARACTER_ADVANCE = .62
    CHARACTER_PADDING = 3
    VALUE_FONT_SIZE = 20
    MINIMUM_WIDTH = 30
    FACE_PADDING = 12
    INFO_WIDTH = 22
    VERTICAL_PADDING = 2
    INK = (1, 250 / 255, 240 / 255)
    GRAIN = (flap_grain(41), flap_grain(67))
    FLAP_TONES = (
        (0, 45, 46, 49), (.08, 37, 38, 41), (.14, 67, 68, 72), (.48, 56, 57, 61),
        (.52, 45, 46, 50), (.82, 47, 48, 52), (.88, 29, 30, 33), (.93, 48, 49, 53), (1, 29, 30, 33),
    )

    def __init__(self, name, caption, width, *, info=False, wrap_value=False, worn=False,
                 sizing_text=None, crease_shadow=DEFAULT_SHADOW, digit_delay=(300, 1000), compact_accent=False):
        face = Gtk.DrawingArea()
        display = box(4, True)
        display.set_halign(Gtk.Align.CENTER)
        display.append(face)
        super().__init__(caption, display)
        self.face = face
        self.name = name
        self.wrap_value = wrap_value
        self.sizing_text = sizing_text
        self.crease_shadow = crease_shadow
        self.compact_accent = compact_accent
        if not 0 <= digit_delay[0] <= digit_delay[1]:
            raise ValueError("Digit delays must be an ascending range in milliseconds.")
        self.digit_delay = digit_delay
        self.worn = worn
        self.progress = None
        self.scale = 1
        self.info = None
        if info:
            self.info = Gtk.Label(label="ⓘ")
            self.info.add_css_class("dim")
            display.append(self.info)
        pitch = self.VALUE_FONT_SIZE * self.CHARACTER_ADVANCE + 2 * self.CHARACTER_PADDING
        self.base_width = max(width, self.width_for_face(math.ceil(self.FACE_PADDING + len(sizing_text or "") * pitch), 1))
        self.available_width = self.base_width
        self.value = "—"
        self.accent = None
        self.ink = self.INK
        self.layout = None
        self.characters = []
        self.digit_flips = {}
        self.ticking = False
        self.face.set_draw_func(self.draw)
        self.face.update_property([Gtk.AccessibleProperty.LABEL], [f"{name}: unavailable"])
        self.resize(1)

    def text_columns(self, value):
        return 1 if self.wrap_value else max(1, len(value), len(self.sizing_text or ""))

    def face_width(self, available, scale):
        return available - (self.INFO_WIDTH if self.info is not None else 0)

    def width_for_face(self, face, scale):
        return face + (self.INFO_WIDTH if self.info is not None else 0)

    def effective_padding(self, pitch, scale):
        # In a very narrow tile, leave at least half of each slot for the glyph.
        return min(self.CHARACTER_PADDING * scale, pitch / 4)

    def font_limit(self, available, value, scale):
        pitch = max(1, self.face_width(available, scale) - self.FACE_PADDING) / self.text_columns(value)
        return (pitch - 2 * self.effective_padding(pitch, scale)) / self.CHARACTER_ADVANCE

    def width_for_font(self, font_size, scale):
        pitch = font_size * self.CHARACTER_ADVANCE + 2 * self.CHARACTER_PADDING * scale
        text_width = pitch * self.text_columns(self.value)
        return max(self.MINIMUM_WIDTH, self.width_for_face(math.ceil(text_width + self.FACE_PADDING), scale))

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
        width = self.face_width(self.available_width, self.scale)
        space = max(1, width - self.FACE_PADDING)
        minimum = self.text_columns(value)
        font_size = min(self.font_size, self.font_limit(self.available_width, value, self.scale))
        desired_pitch = font_size * self.CHARACTER_ADVANCE + 2 * self.effective_padding(space / minimum, self.scale)
        columns = max(minimum, math.floor(space / desired_pitch + .00001))
        # Distribute the remaining width across complete flaps, including blanks.
        pitch = space / columns
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

        for row, line in enumerate(lines):
            first = (columns - len(line)) // 2
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
        ink = (1, 194 / 255, 51 / 255) if warning else self.INK
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
        font = Pango.FontDescription()
        font.set_family("Consolas, Ubuntu Sans Mono, monospace")
        font.set_weight(Pango.Weight.SEMIBOLD)
        font.set_absolute_size(layout.font_size * Pango.SCALE)
        glyph = PangoCairo.create_layout(cr)
        glyph.set_font_description(font)
        glyph.set_text("0", -1)
        digit_ink, _ = glyph.get_extents()
        text_top = layout.row_height / 2 - (digit_ink.y + digit_ink.height / 2) / Pango.SCALE
        flap = self.flap_gradient(layout.row_height)
        flap_width = layout.columns * layout.pitch
        left = (width - flap_width) / 2
        for row in range(layout.rows):
            cr.save()
            cr.translate(0, row * layout.row_height)
            cr.set_source(flap)
            cr.rectangle(left, 1, flap_width, layout.row_height - 2)
            cr.fill()
            cr.restore()
        now = time.monotonic()
        for row in range(layout.rows):
            y = row * layout.row_height
            for column in range(layout.columns):
                x = left + column * layout.pitch
                slot = row * layout.columns + column
                char = self.characters[slot]
                flip = self.digit_flips.get(slot)
                cr.save()
                cr.rectangle(x, y + 1, layout.pitch, layout.row_height - 2)
                cr.clip()
                if flip and flip.start < now < flip.start + flip.duration:
                    self.draw_flip(cr, glyph, char, flip, now, x, y, y + text_top, flap)
                else:
                    self.draw_character(cr, glyph, self.visible_character(slot, now), x, y + text_top)
                cr.restore()
            cr.set_source_rgba(100 / 255, 102 / 255, 106 / 255, .75)
            for column in range(layout.columns):
                x = left + column * layout.pitch
                cr.rectangle(x + 1, y + 4, max(0, layout.pitch - 3), 1)
            cr.fill()
            cr.set_source_rgba(0, 0, 0, .5)
            for column in range(1, layout.columns):
                cr.rectangle(left + column * layout.pitch - 1, y + 4, 1, layout.row_height - 8)
            cr.fill()
            self.draw_seam(cr, width, y, layout.row_height)
        self.draw_grain(cr, width, height)
        self.draw_progress(cr, width, height)

    def flap_gradient(self, height):
        flap = cairo.LinearGradient(0, 0, 0, height)
        for offset, r, g, b in self.FLAP_TONES:
            flap.add_color_stop_rgb(offset, r / 255, g / 255, b / 255)
        return flap

    def draw_character(self, cr, glyph, char, x, y):
        if char == " ":
            return
        glyph.set_text(char, -1)
        ink, _ = glyph.get_extents()
        cr.move_to(x + self.layout.pitch / 2 - (ink.x + ink.width / 2) / Pango.SCALE, y)
        cr.set_source_rgb(*self.ink)
        PangoCairo.show_layout(cr, glyph)

    def draw_flip(self, cr, glyph, char, flip, now, x, y, text_y, flap):
        phase = (now - flip.start) / flip.duration
        half = self.layout.row_height / 2
        hinge = y + half
        self.draw_character(cr, glyph, char, x, text_y)
        cr.save()
        cr.rectangle(x, hinge, self.layout.pitch, half)
        cr.clip()
        self.paint_flap(cr, glyph, flip.before, x, y, text_y, flap)
        cr.restore()

        top = phase < .5
        angle = math.pi * (phase if top else 1 - phase)
        cosine = math.cos(angle)
        if cosine < .001:
            return
        # Project the rotating plate around its hinge. A few narrow affine bands
        # give Cairo the taper of WinUI's PlaneProjection at any device scale.
        depth = math.sin(angle) / 1000
        direction = -1 if top else 1
        center_x = x + self.layout.pitch / 2
        cr.save()
        cr.rectangle(x, y if top else hinge, self.layout.pitch, half)
        cr.clip()
        cr.push_group()
        self.paint_flap(cr, glyph, flip.before if top else char, x, y, text_y, flap)
        paper = cr.pop_group()
        near = projected_near = 0
        for band in range(1, 5):
            far = half * band / 4
            projected_far = far * cosine / (1 - far * depth)
            band_top = hinge - projected_far if top else hinge + projected_near
            cr.save()
            cr.set_antialias(cairo.ANTIALIAS_NONE)
            cr.rectangle(x, band_top, self.layout.pitch, projected_far - projected_near)
            cr.clip()
            cr.translate(center_x, hinge + direction * projected_near)
            cr.scale(1 / (1 - (near + far) / 2 * depth), (projected_far - projected_near) / (far - near))
            cr.translate(-center_x, -(hinge + direction * near))
            cr.set_source(paper)
            cr.paint()
            cr.restore()
            near, projected_near = far, projected_far
        cr.restore()

    def paint_flap(self, cr, glyph, char, x, y, text_y, flap):
        cr.save()
        cr.translate(0, y)
        cr.set_source(flap)
        cr.paint()
        self.draw_character(cr, glyph, char, x, text_y - y)
        cr.restore()

    @staticmethod
    def rounded_rectangle(cr, x, y, width, height, radius):
        radius = min(radius, width / 2, height / 2)
        cr.new_sub_path()
        for cx, cy, a, b in ((x + width - radius, y + radius, -math.pi / 2, 0),
                              (x + width - radius, y + height - radius, 0, math.pi / 2),
                              (x + radius, y + height - radius, math.pi / 2, math.pi),
                              (x + radius, y + radius, math.pi, 3 * math.pi / 2)):
            cr.arc(cx, cy, radius, a, b)
        cr.close_path()

    def draw_face(self, cr, width, height):
        cr.save()
        self.rounded_rectangle(cr, .5, .5, width - 1, height - 1, 5.5)
        gradient = cairo.LinearGradient(0, 0, 0, height)
        gradient.add_color_stop_rgb(0, 45 / 255, 46 / 255, 49 / 255)
        gradient.add_color_stop_rgb(1, 29 / 255, 30 / 255, 33 / 255)
        cr.set_source(gradient)
        cr.fill_preserve()
        cr.set_source_rgb(20 / 255, 21 / 255, 23 / 255)
        cr.set_line_width(1)
        cr.stroke()
        cr.restore()

    def draw_grain(self, cr, width, height):
        cr.save()
        cr.translate(4, 2)
        cr.scale((width - 8) / 150, (height - 4) / 44)
        for shade, opacity in enumerate((.28, .13) if self.worn else (.18, .09)):
            cr.set_source_rgba(shade, shade, shade, opacity)
            cr.append_path(self.GRAIN[shade])
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
            cr.rectangle(1, center - spread, width - 2, spread * 2)
            cr.fill()
        line = shadow.line if shadow else (.02, .02, .02)
        cr.set_source_rgba(*line, .85)
        cr.set_line_width(1.5)
        cr.move_to(1, center)
        cr.line_to(width - 1, center)
        cr.stroke()
        cr.set_source_rgb(114 / 255, 116 / 255, 119 / 255)
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


class CreditsPercentageTile(FlipTile):
    DESCRIPTION = ("Current credit balance divided by the Credit alert threshold in Settings. "
                   "The meter shows up to 100% and needs a positive threshold.")
    GREEN_LENSES = 9
    AMBER_LENSES = 5
    RED_LENSES = 2
    LENS_COUNT = GREEN_LENSES + AMBER_LENSES + RED_LENSES
    MINIMUM_WIDTH = 120
    GAP = 8
    TRACK_INSETS = 8
    VALUE_SHARE = .34
    LENS_IMAGE_SIZE = 96
    LENS_IMAGE_RADIUS = 30
    OFF = (0x65666B, 0x37383C, 0x17171A)
    LENS_COLORS = (((0xA4F9C4, 0x31C67D, 0x065239),) * GREEN_LENSES
                   + ((0xFFD997, 0xFF922E, 0x963100),) * AMBER_LENSES
                   + ((0xFFB1AB, 0xFF4640, 0x8B1218),) * RED_LENSES)

    def __init__(self):
        self.meter = Gtk.DrawingArea()
        self.active_lenses = 0
        self.lens_images = {palette: self.make_lens(palette) for palette in {self.OFF, *self.LENS_COLORS}}
        sizing_text = "99.9%"
        pitch = self.VALUE_FONT_SIZE * self.CHARACTER_ADVANCE + 2 * self.CHARACTER_PADDING
        self.value_preferred = math.ceil(self.FACE_PADDING + len(sizing_text) * pitch)
        self.preferred = self.value_preferred + self.GAP + self.TRACK_INSETS + self.LENS_COUNT * 12
        super().__init__("Remaining Credits Percentage", "Remaining Credits Percentage", self.preferred,
                         sizing_text=sizing_text)
        self.display.append(self.meter)
        self.meter.set_draw_func(self.draw_meter)
        self.set_detail(self.DESCRIPTION)
        self.meter.set_tooltip_text(self.DESCRIPTION)

    def face_width(self, available, scale):
        share = (available - round(self.GAP * scale)) * self.VALUE_SHARE
        return round(min(self.value_preferred * scale, max(40 * scale, share)))

    def width_for_face(self, face, scale):
        total = round(self.GAP * scale) + math.ceil(face / self.VALUE_SHARE)
        return max(self.MINIMUM_WIDTH, min(round(self.preferred * scale), total))

    def resize(self, scale, available=None, font_size=None):
        super().resize(scale, available, font_size)
        gap = round(self.GAP * scale)
        self.display.set_spacing(gap)
        meter_width = self.available_width - self.layout.width - gap
        if self.meter.get_content_width() != meter_width:
            self.meter.set_content_width(meter_width)
        if self.meter.get_content_height() != self.layout.row_height:
            self.meter.set_content_height(self.layout.row_height)

    def set_percentage(self, percent, animate):
        if percent is not None:
            percent = Decimal(str(percent))
        if percent is None:
            self.update("—", animate)
        elif percent == 0 or percent == 100:
            self.update(f"{int(percent)}%", animate)
        else:
            rounded = min(Decimal("99.9"), max(Decimal("0.1"),
                          percent.quantize(Decimal("0.1"), rounding=ROUND_HALF_UP)))
            self.update(locale.format_string("%g", rounded) + "%", animate)
        active_lenses = math.ceil(percent * self.LENS_COUNT / 100) if percent else 0
        if self.active_lenses != active_lenses:
            self.active_lenses = active_lenses
            self.meter.queue_draw()

    @classmethod
    def make_lens(cls, palette):
        # A small supersampled optical surface is shared by all lamps of a color.
        # Its texture stays attached to the glass when a lamp switches on or off.
        image = cairo.ImageSurface(cairo.FORMAT_ARGB32, cls.LENS_IMAGE_SIZE, cls.LENS_IMAGE_SIZE)
        cr = cairo.Context(image)
        cr.translate(cls.LENS_IMAGE_SIZE / 2, cls.LENS_IMAGE_SIZE / 2)
        cr.scale(cls.LENS_IMAGE_RADIUS, cls.LENS_IMAGE_RADIUS)
        highlight, body, edge = [tuple(((rgb >> shift) & 255) / 255 for shift in (16, 8, 0))
                                 for rgb in palette]
        lit = palette != cls.OFF
        if lit:
            glow = cairo.RadialGradient(0, .08, .67, 0, .08, 1.55)
            for offset, opacity in ((0, .52), (.42, .30), (.73, .09), (1, 0)):
                glow.add_color_stop_rgba(offset, *body, opacity)
            cr.set_source(glow)
            cr.paint()

        socket = cairo.LinearGradient(-.20, -.95, .20, .95)
        socket.add_color_stop_rgb(0, .035, .036, .04)
        socket.add_color_stop_rgb(.50, .10, .105, .11)
        socket.add_color_stop_rgb(1, .41, .40, .39)
        cr.set_source(socket)
        cr.arc(0, 0, 1, 0, math.tau)
        cr.fill()
        cr.arc(0, 0, .86, 0, math.tau)
        cr.clip()
        glass = cairo.RadialGradient(.10, .20, 0, 0, 0, .90)
        glass.add_color_stop_rgb(0, *highlight)
        glass.add_color_stop_rgb(.44, *body)
        glass.add_color_stop_rgb(.76, *body)
        glass.add_color_stop_rgb(1, *edge)
        cr.set_source(glass)
        cr.paint()

        # Molded pinpricks catch light on one edge and leave a shallow pit on the other.
        specks = random.Random(41)
        for _ in range(58):
            x, y = specks.uniform(-.85, .85), specks.uniform(-.85, .85)
            size = specks.uniform(.026, .052)
            cr.new_sub_path()
            cr.arc(x, y, size, 0, math.tau)
        pits = cr.copy_path()
        cr.set_source_rgba(0, 0, 0, .25)
        cr.fill()
        cr.save()
        cr.translate(-.025, -.035)
        cr.append_path(pits)
        cr.set_source_rgba(1, 1, 1, .18)
        cr.fill()
        cr.restore()

        # Cool reflection at the crown and a small amber fringe on the opposite rim.
        cr.set_line_cap(cairo.LINE_CAP_ROUND)
        cr.set_line_width(.058)
        cr.set_source_rgba(.52, .84, 1, .56 if lit else .34)
        cr.arc(.015, .025, .76, 1.05 * math.pi, 1.53 * math.pi)
        cr.stroke()
        cr.set_source_rgba(1, .65, .40, .43 if lit else .16)
        cr.arc(-.025, -.02, .77, .05 * math.pi, .38 * math.pi)
        cr.stroke()

        cr.translate(-.26, -.40)
        cr.rotate(-.38)
        cr.scale(.40, .18)
        glint = cairo.RadialGradient(0, 0, 0, 0, 0, 1)
        glint.add_color_stop_rgba(0, 1, 1, 1, .89 if lit else .60)
        glint.add_color_stop_rgba(.45, .93, .97, 1, .46 if lit else .27)
        glint.add_color_stop_rgba(1, .86, .94, 1, 0)
        cr.set_source(glint)
        cr.paint()
        return image

    def draw_meter(self, area, cr, width, height):
        self.draw_face(cr, width, height)
        scale = self.scale
        pitch = (width - self.TRACK_INSETS * scale) / self.LENS_COUNT
        track_height = height - 6 * scale
        radius = min(15 * scale, pitch - 2 * scale, track_height) / 2
        if radius <= 0:
            return
        cr.save()
        cr.translate(self.TRACK_INSETS * scale / 2, 3 * scale)
        flap = self.flap_gradient(track_height)
        y = track_height / 2
        for i, palette in enumerate(self.LENS_COLORS):
            left = i * pitch + .5 * scale
            cr.set_source(flap)
            cr.rectangle(left, 0, pitch - scale, track_height)
            cr.fill()
            cr.set_source_rgba(24/255, 6/255, 7/255, 217/255)
            cr.rectangle(left, y - .6 * scale, pitch - scale, 1.2 * scale)
            cr.fill()
            x = (i + .5) * pitch
            glass = self.lens_images[palette if i >= self.LENS_COUNT - self.active_lenses else self.OFF]
            cr.save()
            cr.rectangle(left, 0, pitch - scale, track_height)
            cr.clip()
            cr.translate(x, y)
            cr.scale(radius / self.LENS_IMAGE_RADIUS, radius / self.LENS_IMAGE_RADIUS)
            cr.set_source_surface(glass, -self.LENS_IMAGE_SIZE / 2, -self.LENS_IMAGE_SIZE / 2)
            cr.get_source().set_filter(cairo.FILTER_BEST)
            cr.paint()
            cr.restore()
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
    LIGHTS = ((1, 67 / 255, 48 / 255), (1, 195 / 255, 54 / 255), (68 / 255, 232 / 255, 94 / 255))
    LENS_CENTERS = (.242, .506, .769)

    def __init__(self):
        super().__init__(valign=Gtk.Align.CENTER)
        self.set_size_request(44, 128)
        self.picture = self.load_picture()
        self.face = Gtk.DrawingArea(can_target=False, hexpand=True, vexpand=True)
        self.face.set_draw_func(self.draw)
        self.append(self.face)
        self.weights = (1, 0, 0)
        self.flicker = 1
        self.last_remaining = None
        self.next_flicker = time.monotonic() + 300
        self.set_opacity(.35)

    @staticmethod
    def load_picture():
        source = cairo.ImageSurface.create_from_png(str(ROOT / "Branding/signal-graphite.png"))
        # Keep a 3× raster for the 128px widget instead of holding the master artwork in memory.
        scale = 384 / source.get_height()
        picture = cairo.ImageSurface(cairo.FORMAT_ARGB32, round(source.get_width() * scale), 384)
        context = cairo.Context(picture)
        context.scale(scale, scale)
        context.set_source_surface(source)
        context.get_source().set_filter(cairo.FILTER_BEST)
        context.paint()
        return picture

    def apply_weights(self, weights):
        self.weights = weights
        self.face.queue_draw()

    def draw(self, area, cr, width, height):
        if not width or not height:
            return
        asset_width = self.picture.get_width()
        asset_height = self.picture.get_height()
        scale = min(width / asset_width, height / asset_height)
        fitted_width, fitted_height = asset_width * scale, asset_height * scale
        left, top = (width - fitted_width) / 2, (height - fitted_height) / 2
        cr.save()
        cr.translate(left, top)
        cr.scale(scale, scale)
        cr.set_source_surface(self.picture)
        cr.paint()
        cr.restore()

        if self.last_remaining is None:
            return
        _, amber, red = self.weights
        strengths = (red, amber * (1 - red), (1 - amber) * (1 - red))
        for lens, color, power in zip(self.LENS_CENTERS, self.LIGHTS, strengths):
            if not power:
                continue
            x, y = width / 2, top + fitted_height * lens
            power = math.sqrt(power) * self.flicker
            bloom = cairo.RadialGradient(x, y, 0, x, y, fitted_width * .64)
            for stop, alpha in ((0, .40), (.32, .29), (.58, .12), (1, 0)):
                bloom.add_color_stop_rgba(stop, *color, alpha * power)
            cr.set_source(bloom)
            cr.paint()

            # Screen lights the lens without covering its etched texture.
            light = cairo.RadialGradient(x, y, 0, x, y, fitted_width * .37)
            for stop, alpha in ((0, 225 / 255), (.55, .72), (.84, .40), (1, 0)):
                light.add_color_stop_rgba(stop, *color, alpha * power)
            cr.save()
            cr.set_operator(cairo.OPERATOR_SCREEN)
            cr.set_source(light)
            cr.paint()
            cr.restore()

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
                self.flicker = 1
                return False
            elapsed = min(1, (time.monotonic() - now) / .28)
            self.flicker = 1 - .09 * math.sin(2 * math.pi * elapsed) ** 2 * (1 - elapsed)
            self.face.queue_draw()
            return elapsed < 1

        self.add_tick_callback(tick)


class UsageWindow(SettingsUiMixin, Gtk.ApplicationWindow):
    def __init__(self, app):
        # Adw.ApplicationWindow imposes a minimum height larger than the strip.
        super().__init__(application=app, title="OpenAI Usage", default_width=1240, default_height=80,
                         icon_name="biz.umbertogiacobbi.OpenAIUsage")
        self.state = {}
        self.clock_cycle_start = None
        self.settings = {}
        self.settings_ready = False
        self.settings_window = None
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
        self.connect("close-request", self.on_close)

        strip = self.strip = wrap_box()
        strip.set_hexpand(True)
        strip.set_valign(Gtk.Align.START)

        self.signal = TrafficSignal()
        self.signal.set_valign(Gtk.Align.START)
        self.allowance_tile = FlipTile("Allowance remaining", "Allowance left", 115, worn=True, compact_accent=True)
        self.credits_tile = FlipTile("Remaining Credits", "Remaining Credits", 108, sizing_text="000,000", worn=True)
        self.credits_percentage_tile = CreditsPercentageTile()
        self.estimate_tile = FlipTile("Time to run out", "Time to run out", 152)
        self.voucher_tile = VoucherTile()
        self.voucher_expiry_tile = FlipTile("Next voucher expiry", "Next voucher expiry", 160, info=True, sizing_text="00/00 00:00")
        self.metrics = (self.allowance_tile, self.credits_tile, self.credits_percentage_tile,
                        self.estimate_tile, self.voucher_tile, self.voucher_expiry_tile)
        self.reset_clock = FlipTile("Auto reset clock", "Auto reset clock", 403,
                                    sizing_text="WEDNESDAY 00 SEPTEMBER 00")

        self.connect("map", self.on_window_mapped)
        self.menu_button = self.build_widget_menu()
        self.build_details()
        self.modules = [
            ("allowance", self.allowance_tile, "Percentage still available in the selected Codex quota."),
            ("credits", self.credits_tile, "Remaining credit balance reported by Codex."),
            ("credits-percentage", self.credits_percentage_tile, CreditsPercentageTile.DESCRIPTION),
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
        body.append(self.menu_button)
        surface = self.widget_preview = Gtk.ScrolledWindow(child=body, overlay_scrolling=True, hscrollbar_policy=Gtk.PolicyType.NEVER,
                                    vscrollbar_policy=Gtk.PolicyType.AUTOMATIC)
        drag = Gtk.GestureClick(button=Gdk.BUTTON_PRIMARY)
        drag.set_propagation_phase(Gtk.PropagationPhase.CAPTURE)
        drag.connect("pressed", self.begin_move)
        surface.set_vexpand(True)
        self.window_content = box(6)
        self.window_content.append(surface)
        self.window_content.add_controller(drag)
        motion = Gtk.EventControllerMotion()
        motion.connect("motion", self.edge_cursor)
        motion.connect("leave", lambda controller: controller.get_widget().set_cursor(None))
        self.window_content.add_controller(motion)
        self.set_child(self.window_content)
        self.compact_switch.connect("notify::active", self.details_toggled)
        keys = Gtk.EventControllerKey()
        keys.connect("key-pressed", self.details_key_pressed)
        self.add_controller(keys)
        self.apply_size()
        try:
            self.tray=StatusTray({"open":self.present,"about":self.show_about,"settings":self.show_settings,
                                 "website":lambda:Gio.AppInfo.launch_default_for_uri("https://umbertogiacobbi.biz",None),
                                 "manifesto":self.open_manifesto,
                                 "exit":self.exit_application})
        except Exception as error:
            log_event("tray.unavailable",type(error).__name__)
        self.clock_id=GLib.timeout_add(1000,self.tick)
        self.refresh_id=GLib.timeout_add_seconds(60,self.refresh)
        self.refresh()

    def build_widget_menu(self):
        menu = self.widget_menu = Gtk.Popover()
        menu.add_css_class("widget-menu-popup")
        rows = box(2)
        menu.set_child(rows)

        def row(title, icon_name, style):
            icon = Gtk.Image(icon_name=icon_name)
            icon.add_css_class("menu-" + style)
            label = Gtk.Label(label=title, xalign=0, hexpand=True)
            content = box(10, horizontal=True)
            content.append(icon)
            content.append(label)
            return content, icon, label

        def item(title, icon_name, style, callback=None, toggle=False):
            content, icon, label = row(title, icon_name, style)
            button = (Gtk.ToggleButton if toggle else Gtk.Button)(child=content)
            button.add_css_class("flat")

            def on_clicked(_):
                menu.popdown()
                if callback is not None:
                    callback()

            button.connect("clicked", on_clicked)
            rows.append(button)
            return button, icon, label

        self.pin, _, self.pin_label = item("Pin", "view-pin-symbolic", "pin", toggle=True)
        self.pin.set_sensitive(False)
        self.syncing_pin = False
        self.pin.connect("toggled", self.on_pin_toggled)
        compact_row, _, _ = row("Compact mode", "view-restore-symbolic", "compact")
        compact_row.add_css_class("menu-switch-row")
        self.compact_switch = Gtk.Switch(valign=Gtk.Align.CENTER)
        self.compact_switch.update_property([Gtk.AccessibleProperty.LABEL], ["Compact mode"])
        compact_row.append(self.compact_switch)
        rows.append(compact_row)
        self.settings_item, self.settings_icon, _ = item("Settings", "emblem-system-symbolic", "settings", self.show_settings)
        item("About", "help-about-symbolic", "about", self.show_about)
        item("VibeWare", "web-browser-symbolic", "vibeware", self.open_manifesto)
        item("Exit", "system-log-out-symbolic", "exit", self.exit_application)
        trigger = Gtk.MenuButton(icon_name="view-more-symbolic", valign=Gtk.Align.START, popover=menu)
        trigger.add_css_class("widget-menu")
        trigger.set_tooltip_text("Widget menu\nConnecting to Codex…")
        trigger.update_property([Gtk.AccessibleProperty.LABEL], ["Widget menu"])
        return trigger

    @staticmethod
    def open_manifesto():
        Gio.AppInfo.launch_default_for_uri(MANIFESTO_URL, None)

    def update_menu_status(self, status, warning=False):
        if self.settings_item.get_tooltip_text() != status:
            self.menu_button.set_tooltip_text("Widget menu\n" + status)
            self.settings_item.set_tooltip_text(status)
            self.settings_item.update_property([Gtk.AccessibleProperty.DESCRIPTION], [status])
        icon = "dialog-warning-symbolic" if warning else "emblem-system-symbolic"
        if self.settings_icon.get_icon_name() != icon:
            self.settings_icon.set_from_icon_name(icon)

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
        self.pin_label.set_label("Unpin" if enabled else "Pin")
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
        self.pin_label.set_label("Unpin" if enabled else "Pin")
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
        self.set_progress(self.allowance_tile, remaining)
        self.credits_tile.update("—" if credits == "Unavailable" else credits.upper(),
                                 animate and self.credits_tile.get_mapped())
        self.credits_tile.set_detail(f"Credits remaining as reported by Codex: {credits}. The displayed balance is rounded to a whole number.")
        self.update_credit_percentage(state, animate)
        self.voucher_tile.show_vouchers(state.get("resetVoucherCount"), state.get("resetVoucherExpirations", []),
                                       animate and self.voucher_tile.get_mapped())

    def update_credit_percentage(self, state, animate):
        balance = state.get("creditsBalanceExact", state.get("creditsBalance"))
        budget = self.settings.get("creditsAlertThreshold")
        percent = None
        if type(budget) is int and budget > 0 and balance is not None:
            try:
                amount = Decimal(str(balance))
            except InvalidOperation:
                amount = None
            if amount is not None and amount.is_finite() and amount >= 0:
                percent = min(amount, budget) / budget * 100
        tile = self.credits_percentage_tile
        tile.set_percentage(percent, animate and tile.get_mapped())

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
            FlipTile("5-hour auto reset in", "5-hour reset in", 125, sizing_text="00h 00m"),
            FlipTile("Weekly allowance", "Weekly allowance", 118),
            FlipTile("Weekly auto reset", "Weekly auto reset", 152, sizing_text="00/00 00:00"),
            FlipTile("Run-out estimate", "Run-out estimate", 158),
            FlipTile("Selected auto reset in", "Auto reset in", 132, sizing_text="00d 00h"),
            FlipTile("Will the quota last?", "Will the quota last?", 442,
                     wrap_value=True, sizing_text="EXPECTED TO LAST UNTIL RESET"),
        )

    def details_toggled(self, switch, _):
        surface = self.get_surface()
        compact = switch.get_active()
        restore = None if compact else self.expanded_size
        if compact and surface is not None:
            self.expanded_size = (surface.get_width(), surface.get_height())
        elif not compact:
            self.expanded_size = None
        self.apply_size()
        self.queue_fit_height(restore)

    def details_key_pressed(self, _, key, *_args):
        if key == Gdk.KEY_Escape and not self.widget_menu.get_mapped() and not self.compact_switch.get_active():
            self.compact_switch.set_active(True)
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
        problem_status = "Saving settings before closing…" if self.exit_pending else self.settings_error or status
        warning = self.exit_pending or bool(self.settings_error) or not self.state.get("connected", False) or stale
        self.update_menu_status(problem_status, warning)
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
        saved = self.settings.get("fieldOrder") or []
        ordered = [available.pop(key) for key in saved if key in available] + list(available.values())
        if "credits-percentage" not in saved:
            percentage_at = next(i for i, module in enumerate(ordered) if module[0] == "credits-percentage")
            percentage = ordered.pop(percentage_at)
            credits_at = next(i for i, module in enumerate(ordered) if module[0] == "credits")
            ordered.insert(credits_at + 1, percentage)
        return ordered

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
        expanded = not self.compact_switch.get_active()
        details = expanded and self.settings.get("enableDetailsBar", False)
        visible = [widget for key, widget, _ in self.ordered_modules()
                   if self.module_enabled(key) and (widget not in self.detail_tiles or details)]
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
        expanded = not self.compact_switch.get_active()
        big = self.settings.get("bigSize", False) and width >= 375
        scale = 1 + LARGER_WIDGET_PERCENT / 100 if big else 1
        (self.add_css_class if big else self.remove_css_class)("big")
        opacity = self.settings.get("windowOpacityPercent", 100) / 100
        self.set_opacity(opacity)
        tiles = list(WrapLayout.children(self.strip))
        preferred = {tile: round(tile.base_width * scale) for tile in tiles}

        menu_width = self.menu_button.measure(Gtk.Orientation.HORIZONTAL, -1)[1]
        gaps = max(0, len(tiles) - 1) * layout.spacing
        margins = self.body.get_margin_start() + self.body.get_margin_end() + 2
        spacing = self.body.get_spacing()
        plain_width = menu_width + margins + spacing
        self.signal.set_visible(expanded)
        signal_width = 30 if width < 530 else 44
        chrome = plain_width
        if expanded:
            chrome += signal_width + spacing

        minimum = max(300, chrome + FlipTile.MINIMUM_WIDTH)
        if not expanded:
            minimum_tiles = sum(tile.MINIMUM_WIDTH for tile in tiles)
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
        menu_height = self.menu_button.measure(Gtk.Orientation.VERTICAL, menu_width)[1]
        signal_height = min(128, max(menu_height, strip_height)) if expanded else 0
        if expanded:
            self.signal.set_size_request(signal_width, signal_height)
        vertical_insets = self.body.get_margin_top() + self.body.get_margin_bottom() + 2
        height = max(strip_height, menu_height) + vertical_insets
        self.set_size_request(minimum, menu_height + vertical_insets)
        return max(width, minimum), height

    @staticmethod
    def fit_compact_widths(preferred, available, scale):
        # Reserve enough room for the same text size in every field before
        # distributing spare space towards the preferred widths.
        low, high = 0, FlipTile.VALUE_FONT_SIZE * scale
        for tile, width in preferred.items():
            high = min(high, tile.font_limit(width, tile.value, scale))
        for _ in range(8):
            size = (low + high) / 2
            if sum(min(preferred[tile], tile.width_for_font(size, scale)) for tile in preferred) <= available:
                low = size
            else:
                high = size
        widths = {tile: min(width, tile.width_for_font(low, scale)) for tile, width in preferred.items()}
        used = sum(widths.values())
        flexible = sum(preferred.values()) - used
        fraction = max(0, min(1, (available - used) / flexible)) if flexible else 0
        return {tile: width + int((preferred[tile] - width) * fraction) for tile, width in widths.items()}

    @staticmethod
    def fit_tile_fonts(scale, widths):
        font_size = FlipTile.VALUE_FONT_SIZE * scale
        for tile, width in widths.items():
            font_size = min(font_size, tile.font_limit(width, tile.value, scale))
        for tile, width in widths.items():
            tile.resize(scale, width, font_size)

    def fit_initial_size(self):
        if not self.initial_fit_done and self.settings_ready and self.get_mapped():
            self.initial_fit_done = True
            self.queue_fit_height()

    def cancel_fit_height(self):
        if self.fit_source:
            GLib.source_remove(self.fit_source)
            self.fit_source = 0

    def queue_fit_height(self, restore=None):
        self.cancel_fit_height()

        def fit():
            self.fit_source = 0
            surface = self.get_surface()
            if self.closed or not self.get_mapped() or surface is None:
                return False
            width = restore[0] if restore else surface.get_width()
            width, height = self.fit_layout(width)
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
                if isinstance(target, (Gtk.Button, Gtk.MenuButton, Gtk.Popover, Gtk.Scrollbar, Gtk.Switch)):
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
                    self.hide_settings()
                    self.set_visible(False)
            dialog.connect("response", response)
            self.close_dialog = dialog
            dialog.present()
            return True
        self.hide_settings()
        if self.saving or self.saved_settings_revision != self.settings_revision:
            self.exit_pending = True
            self.update_menu_status("Saving settings before closing…", warning=True)
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
