"""Small native controls shared by the GTK workflow editor and its dialogs."""

import math

import gi

gi.require_version("Gtk", "4.0")
from gi.repository import Gtk, Pango  # noqa: E402


def box(vertical=False, spacing=8):
    return Gtk.Box(
        orientation=Gtk.Orientation.VERTICAL if vertical else Gtk.Orientation.HORIZONTAL,
        spacing=spacing,
    )


def button(label, action):
    widget = Gtk.Button(label=label)
    widget.connect("clicked", lambda _: action())
    return widget


def add(row, *children):
    for child in children:
        row.append(child)
    return row


def text(value="", style=None, truncate=False):
    widget = Gtk.Label(label=value, xalign=0)
    if style:
        widget.add_css_class(style)
    if truncate:
        widget.set_ellipsize(Pango.EllipsizeMode.END)
        widget.set_max_width_chars(32)
    return widget


def icon(name, size=16):
    widget = Gtk.Image.new_from_icon_name(name)
    widget.set_pixel_size(size)
    return widget


def icon_button(glyph, label, action):
    widget = Gtk.Button(icon_name=glyph, tooltip_text=label)
    widget.update_property([Gtk.AccessibleProperty.LABEL], [label])
    widget.connect("clicked", lambda _: action())
    return widget


def tool_button(glyph, label, action):
    widget = Gtk.Button()
    widget.set_child(add(box(spacing=7), icon(glyph), text(label)))
    widget.connect("clicked", lambda _: action())
    return widget


def margins(widget, amount):
    for edge in ("start", "end", "top", "bottom"):
        getattr(widget, "set_margin_" + edge)(amount)


def timecode(value):
    seconds = math.ceil(value) if value >= 0 else math.floor(-value)
    return ("+" if value < 0 else "") + f"{seconds // 60}:{seconds % 60:02d}"


TRANSITIONS = {"default": "OBS default", "cut": "Cut", "fade": "Fade"}
