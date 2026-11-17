"""Live GTK presentation bindings; selection never reconnects OBS or changes media."""

import gi
from localization import Translations

gi.require_version("Gtk", "4.0")
from gi.repository import Gtk, Pango  # noqa: E402 — GTK version must be selected first


class PanelLocalization:
    def __init__(self, language="en"):
        self.catalog = Translations()
        self.language = language
        # Own Python wrappers until shutdown: GTK may otherwise recreate a wrapper
        # while its C widget and notification handlers are still alive.
        self.bindings = {}
        self.accessibility = {}
        self.changing = False

    def text(self, source):
        return self.catalog.text(source, self.language)

    def bind(self, widget, property_name):
        properties = self.bindings.setdefault(widget, {})
        if property_name in properties:
            return
        properties[property_name] = [widget.get_property(property_name), None]

        def changed(target, _):
            if not self.changing:
                self.bindings[target][property_name][0] = target.get_property(property_name)
                self.update(target, property_name)

        properties[property_name][1] = widget.connect("notify::" + property_name, changed)
        self.update(widget, property_name)

    def update(self, widget, property_name):
        source = self.bindings[widget][property_name][0]
        if not isinstance(source, str):
            return
        if property_name == "label":
            context = widget.get_pango_context()
            current = context.get_language()
            if current is None or current.to_string() != self.language:
                context.set_language(Pango.Language.from_string(self.language))
        translated = self.text(source)
        if widget.get_property(property_name) == translated:
            return
        self.changing = True
        try:
            widget.set_property(property_name, translated)
        finally:
            self.changing = False

    def attach(self, root):
        pending = [root]
        while pending:
            widget = pending.pop()
            if isinstance(widget, Gtk.Label):
                self.bind(widget, "label")
            if isinstance(widget, Gtk.Window):
                self.bind(widget, "title")
            if isinstance(widget, Gtk.Entry):
                self.bind(widget, "placeholder-text")
            self.bind(widget, "tooltip-text")
            child = widget.get_first_child()
            while child:
                pending.append(child)
                child = child.get_next_sibling()

    def select(self, language):
        self.language = language
        for widget, properties in list(self.bindings.items()):
            for property_name in properties:
                self.update(widget, property_name)
        for widget, label in list(self.accessibility.items()):
            widget.update_property([Gtk.AccessibleProperty.LABEL], [self.text(label)])

    def accessible(self, widget, label):
        self.accessibility[widget] = label
        widget.update_property([Gtk.AccessibleProperty.LABEL], [self.text(label)])

    def dropdown(self, values):
        dropdown = Gtk.DropDown.new_from_strings(values)

        def factory():
            result = Gtk.SignalListItemFactory()
            result.connect("setup", lambda _, item: item.set_child(Gtk.Label(xalign=0)))

            def bind(_, item):
                label = item.get_child()
                label.set_label(item.get_item().get_string())
                self.bind(label, "label")

            result.connect("bind", bind)
            result.connect("unbind", lambda _, item: self.unbind(item.get_child()))
            return result

        dropdown.set_factory(factory())
        dropdown.set_list_factory(factory())
        return dropdown

    def close(self):
        for widget in list(self.bindings):
            self.unbind(widget)
        self.accessibility.clear()

    def unbind(self, widget):
        for _, handler in self.bindings.pop(widget, {}).values():
            widget.disconnect(handler)
        self.accessibility.pop(widget, None)
