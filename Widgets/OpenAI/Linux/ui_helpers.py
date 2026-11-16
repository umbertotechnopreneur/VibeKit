"""Small native widget builders shared by the strip and its dialogs."""
from pathlib import Path
from gi.repository import Gtk, GdkPixbuf, Gdk

ROOT = Path(__file__).resolve().parents[1]

def text(value, style=None):
    label = Gtk.Label(label=value, xalign=0, wrap=True)
    if style:
        label.add_css_class(style)
    return label


def box(spacing=8, horizontal=False):
    return Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL if horizontal else Gtk.Orientation.VERTICAL, spacing=spacing)


def about_mark(filename, size):
    image = GdkPixbuf.Pixbuf.new_from_file_at_scale(str(ROOT / "Branding" / filename), size, size, True)
    return Gtk.Image.new_from_paintable(Gdk.Texture.new_for_pixbuf(image))


def about_link(label, url, child):
    link = Gtk.LinkButton(uri=url, halign=Gtk.Align.CENTER, tooltip_text=label)
    link.set_child(child)
    link.add_css_class("about-link")
    link.update_property([Gtk.AccessibleProperty.LABEL], [label])
    return link


def metric(label, number):
    content = box(4)
    content.append(text(label, "dim"))
    content.append(number)
    return content
