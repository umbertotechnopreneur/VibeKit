"""Native modal windows for the Studio Controls workflow library and scene steps."""

import gi

from workflow_widgets import TRANSITIONS, add, box, button, icon, margins, text, timecode

gi.require_version("Gtk", "4.0")
from gi.repository import Gdk, Gtk  # noqa: E402


def dialog_for(parent, title):
    dialog = Gtk.Dialog(title=title, transient_for=parent, modal=True, destroy_with_parent=True)
    dialog.add_css_class("umberto-lab")
    dialog.set_default_size(420, -1)
    margins(dialog.get_content_area(), 18)
    dialog.get_content_area().set_spacing(14)
    return dialog


def field(container, label, control):
    caption = text(label)
    caption.set_mnemonic_widget(control)
    control.update_property([Gtk.AccessibleProperty.LABEL], [label])
    container.append(add(box(True, 5), caption, control))


def cancel_save(dialog, tr):
    dialog.add_button(tr("Cancel"), Gtk.ResponseType.CANCEL)
    save = dialog.add_button(tr("Save"), Gtk.ResponseType.OK)
    save.add_css_class("suggested-action")
    dialog.set_default_response(Gtk.ResponseType.OK)
    return save


def name_field(dialog, current, tr, caption):
    name = Gtk.Entry(max_length=100, activates_default=True)
    name.set_text(current)
    name.select_region(0, -1)
    field(dialog.get_content_area(), tr(caption), name)
    return name


def require_name(entry, accept):
    def validate(*_):
        accept.set_sensitive(bool(entry.get_text().strip()))

    entry.connect("changed", validate)
    validate()


def dialog_result(dialog, action, refresh, tr, extra_actions=None):
    """Keep the editor and typed name open if validation or persistence fails."""
    info = text()
    info.set_wrap(True)
    info.set_visible(False)
    dialog.get_content_area().append(info)
    actions = {Gtk.ResponseType.OK: action, **(extra_actions or {})}
    action_area = dialog.get_widget_for_response(Gtk.ResponseType.OK).get_parent()
    if action_area.has_css_class("dialog-action-area"):
        action_area.set_margin_start(18)
        action_area.set_margin_end(18)
        action_area.set_margin_bottom(14)

    def finish(_, response):
        if response in actions:
            try:
                actions[response]()
            except (ValueError, OSError) as error:
                info.set_label(tr(str(error)))
                info.set_visible(True)
                return
            refresh()
        dialog.destroy()

    dialog.connect("response", finish)
    dialog.present()


def step_dialog(owner, identifier):
    step = owner.flow["steps"][owner.index_of(identifier)]
    tr = owner.tr
    dialog = dialog_for(owner.panel.window, tr("Edit step"))
    content = dialog.get_content_area()
    content.append(
        add(
            box(),
            owner.scene_icon(step["scene"], 24),
            text(owner.scene_title(step["scene"]), "workflow-title"),
        )
    )
    fields = box(spacing=18)
    content.append(fields)
    dialog.seconds = Gtk.SpinButton.new_with_range(1, 86400, 1)
    dialog.seconds.set_value(step["seconds"])
    dialog.seconds.set_numeric(True)
    field(fields, tr("Duration (seconds)"), dialog.seconds)
    keys = tuple(TRANSITIONS)
    dialog.transition = Gtk.DropDown.new_from_strings([tr(TRANSITIONS[key]) for key in keys])
    dialog.transition.set_selected(keys.index(step.get("transition", "default")))
    field(fields, tr("Transition"), dialog.transition)
    dialog.note = Gtk.Entry(max_length=500)
    dialog.note.set_text(step["note"])
    field(content, tr("Note"), dialog.note)
    left = button(tr("Move left"), lambda: dialog.response(1))
    right = button(tr("Move right"), lambda: dialog.response(2))
    remove = button(tr("Remove"), lambda: dialog.response(3))
    index = owner.index_of(identifier)
    left.set_sensitive(index > 0)
    right.set_sensitive(index < len(owner.flow["steps"]) - 1)
    content.append(add(box(), left, right, remove))
    cancel_save(dialog, tr)

    def update():
        # Read pending fields before moving the occurrence. Save and reorder atomically.
        dialog.seconds.update()
        current = owner.flow["steps"][owner.index_of(identifier)]
        current.update(seconds=dialog.seconds.get_value_as_int(), note=dialog.note.get_text())
        transition = keys[dialog.transition.get_selected()]
        if transition == "default":
            current.pop("transition", None)
        else:
            current["transition"] = transition

    def move(delta):
        update()
        steps = owner.flow["steps"]
        position = owner.index_of(identifier)
        steps.insert(position + delta, steps.pop(position))
        owner.library.save()

    def delete():
        steps = owner.flow["steps"]
        position = owner.index_of(identifier)
        steps.pop(position)
        owner.library.save()
        owner.selected = steps[min(position, len(steps) - 1)]["id"] if steps else None

    def save():
        update()
        owner.library.save()

    def refresh():
        owner.refresh()
        owner.reveal_step(owner.selected)
        owner.focus_step(owner.selected)

    dialog_result(dialog, save, refresh, tr, {1: lambda: move(-1), 2: lambda: move(1), 3: delete})
    return dialog


def workflow_details(owner, parent, identifier=None, *, duplicate=False):
    tr = owner.tr
    flows = owner.library.project["workflows"]
    original = next((flow for flow in flows if flow["id"] == identifier), None)
    title = "Duplicate" if duplicate else "New workflow" if original is None else "Edit workflow"
    dialog = dialog_for(parent, tr(title))
    initial = (original["name"] + " (2)" if duplicate else original["name"]) if original else ""
    name = name_field(dialog, initial, tr, "Workflow")
    target = Gtk.SpinButton.new_with_range(0, 86400, 1)
    target.set_value((original["targetSeconds"] or 0) if original else 180)
    target.set_numeric(True)
    field(dialog.get_content_area(), tr("Target (seconds)"), target)
    dialog.get_content_area().append(text(tr("Use zero for no target."), "dim-label"))
    require_name(name, cancel_save(dialog, tr))

    def save():
        value = name.get_text().strip()
        target.update()
        duration = target.get_value_as_int() or None
        if original is None or duplicate:
            # The library persists new workflows (and any copied steps) as a single edit.
            source = (
                dict(original, targetSeconds=duration)
                if duplicate
                else {"targetSeconds": duration, "steps": []}
            )
            created = owner.library.create_workflow(value, source)
            owner.current = owner.library.project["workflows"].index(created)
            owner.selected = None
        else:
            current = next(
                flow for flow in owner.library.project["workflows"] if flow["id"] == identifier
            )
            current.update(name=value, targetSeconds=duration)
            owner.library.save()

    def refresh():
        owner.refresh()
        if owner.manager is not None:
            owner.manager.populate()

    dialog_result(dialog, save, refresh, tr)
    return dialog


class LibraryDialog(Gtk.Window):
    def __init__(self, owner):
        super().__init__(
            title=owner.tr("Library"),
            transient_for=owner.panel.window,
            modal=True,
            destroy_with_parent=True,
        )
        self.owner = owner
        self.tr = owner.tr
        self.changing = False
        self.add_css_class("umberto-lab")
        self.set_default_size(550, 500)
        header = Gtk.HeaderBar()
        self.set_titlebar(header)
        body = box(True, 14)
        margins(body, 16)
        scroll = Gtk.ScrolledWindow(vexpand=True)
        scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        sections = box(True, 12)
        scroll.set_child(sections)
        body.append(scroll)
        self.projects, self.project_section = self.section(sections, "Projects")
        self.flows, self.flow_section = self.section(sections, "Workflows")
        self.project_actions = self.actions(
            self.project_section,
            [
                ("New project", lambda: self.project_details(True)),
                ("Edit project", lambda: self.project_details(False)),
                ("Remove", lambda: self.confirm_remove(True)),
            ],
        )
        self.flow_actions = self.actions(
            self.flow_section,
            [
                ("New workflow", lambda: workflow_details(owner, self)),
                ("Edit workflow", lambda: workflow_details(owner, self, owner.flow["id"])),
                (
                    "Duplicate",
                    lambda: workflow_details(owner, self, owner.flow["id"], duplicate=True),
                ),
                ("Remove", lambda: self.confirm_remove(False)),
            ],
        )
        self.message = text()
        self.message.set_wrap(True)
        self.message.set_visible(False)
        body.append(self.message)
        self.done = button(self.tr("Close"), self.close)
        self.done.set_halign(Gtk.Align.END)
        body.append(self.done)
        self.set_child(body)
        self.projects.connect("row-selected", self.project_selected)
        self.flows.connect("row-selected", self.flow_selected)
        self.connect("close-request", self.on_close)
        escape = Gtk.ShortcutController()
        escape.add_shortcut(
            Gtk.Shortcut.new(
                Gtk.KeyvalTrigger.new(Gdk.KEY_Escape, 0),
                Gtk.CallbackAction.new(lambda *_: self.close() or True),
            )
        )
        self.add_controller(escape)
        self.populate()

    def on_close(self, _window):
        self.owner.manager = None
        return False

    def section(self, parent, title):
        section = Gtk.Expander(label=self.tr(title), expanded=True)
        section.add_css_class("workflow-library-section")
        content = box(True, 8)
        content.set_margin_top(8)
        rows = Gtk.ListBox(selection_mode=Gtk.SelectionMode.SINGLE, activate_on_single_click=True)
        rows.add_css_class("workflow-library-list")
        rows.set_show_separators(True)
        rows.update_property([Gtk.AccessibleProperty.LABEL], [self.tr(title)])
        content.append(rows)
        section.set_child(content)
        parent.append(section)
        return rows, content

    def actions(self, content, actions):
        group = Gtk.FlowBox(
            selection_mode=Gtk.SelectionMode.NONE,
            row_spacing=2,
            column_spacing=4,
            homogeneous=False,
            max_children_per_line=4,
            min_children_per_line=1,
        )
        group.set_halign(Gtk.Align.START)
        controls = []
        for label, callback in actions:
            control = button(self.tr(label), callback)
            group.insert(control, -1)
            controls.append(control)
        content.append(group)
        return controls

    def populate(self):
        self.changing = True
        try:
            self.fill(
                self.projects,
                self.owner.library.data["projects"],
                self.owner.library.project["id"],
                True,
            )
            self.fill(
                self.flows, self.owner.library.project["workflows"], self.owner.flow["id"], False
            )
            self.project_actions[-1].set_sensitive(len(self.owner.library.data["projects"]) > 1)
            self.flow_actions[-1].set_sensitive(len(self.owner.library.project["workflows"]) > 1)
        finally:
            self.changing = False

    def fill(self, rows, records, selected_id, project):
        while child := rows.get_first_child():
            rows.remove(child)
        for item in records:
            row = Gtk.ListBoxRow()
            row.identifier = item["id"]
            name = text(item["name"], truncate=True)
            name.set_hexpand(True)
            subtitle = (
                f"{self.tr('Workflows')} · {len(item['workflows'])}"
                if project
                else timecode(sum(s["seconds"] for s in item["steps"]))
            )
            content = add(
                box(spacing=10),
                icon("folder-symbolic" if project else "view-list-symbolic"),
                name,
                text(subtitle, "dim-label"),
            )
            margins(content, 8)
            row.set_child(content)
            rows.append(row)
            if item["id"] == selected_id:
                rows.select_row(row)

    def project_selected(self, _rows, row):
        if row is None or self.changing or self.owner.library.project["id"] == row.identifier:
            return
        try:
            self.owner.select_library_item(row.identifier)
        except (OSError, ValueError) as error:
            self.message.set_label(self.tr(str(error)))
            self.message.set_visible(True)
        self.populate()

    def flow_selected(self, _rows, row):
        if row is None or self.changing or self.owner.flow["id"] == row.identifier:
            return
        self.owner.select_library_item(self.owner.library.project["id"], row.identifier)

    def project_details(self, create):
        owner = self.owner
        tr = self.tr
        project = owner.library.project
        dialog = dialog_for(self, tr("New project" if create else "Edit project"))
        name = name_field(dialog, "" if create else project["name"], tr, "Project")
        require_name(name, cancel_save(dialog, tr))

        def save():
            if owner.panel.applying_titles:
                raise ValueError("Wait for project cards to finish.")
            if create:
                owner.library.create_project(name.get_text().strip())
                owner.current = 0
                owner.selected = None
                owner.panel.switch_card_project()
                owner.notice.set_label("Apply the project to update the cards in OBS.")
            else:
                owner.library.rename_project(project["id"], name.get_text().strip())

        dialog_result(dialog, save, self.refresh_editor, tr)
        return dialog

    def refresh_editor(self):
        self.owner.refresh()
        self.populate()
        self.message.set_visible(False)

    def confirm_remove(self, project):
        owner = self.owner
        tr = self.tr
        item = owner.library.project if project else owner.flow
        dialog = dialog_for(self, tr("Remove project" if project else "Remove workflow"))
        content = dialog.get_content_area()
        name = text(item["name"], "heading", truncate=True)
        content.append(name)
        if project:
            content.append(text(f"{tr('Workflows')} · {len(item['workflows'])}"))
        message = Gtk.Label(
            label=tr(
                "Remove this project and its workflows from the library?"
                if project
                else "Remove this workflow from the project?"
            ),
            xalign=0,
            wrap=True,
        )
        content.append(message)
        dialog.add_button(tr("Cancel"), Gtk.ResponseType.CANCEL)
        dialog.add_button(tr("Remove"), Gtk.ResponseType.OK).add_css_class("destructive-action")
        dialog.set_default_response(Gtk.ResponseType.CANCEL)

        def remove():
            if owner.panel.applying_titles:
                raise ValueError("Wait for project cards to finish.")
            if project:
                owner.library.remove_project(item["id"])
                owner.current = 0
                owner.panel.switch_card_project()
                owner.notice.set_label("Apply the project to update the cards in OBS.")
            else:
                owner.library.remove_workflow(item["id"])
                owner.current = min(owner.current, len(owner.library.project["workflows"]) - 1)
            owner.selected = None

        dialog_result(dialog, remove, self.refresh_editor, tr)
        return dialog
