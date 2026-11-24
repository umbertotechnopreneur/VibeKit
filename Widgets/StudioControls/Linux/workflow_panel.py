"""Native linear workflow editor and detachable timer for the GTK control desk."""

import math
from copy import deepcopy
from threading import Event

import gi

from panel_theme import SCENE_COLORS
from workflows import Playback, new_step

gi.require_version("Gtk", "4.0")
from gi.repository import Gdk, GLib, Gtk  # noqa: E402


def box(vertical=False, spacing=8):
    return Gtk.Box(
        orientation=Gtk.Orientation.VERTICAL if vertical else Gtk.Orientation.HORIZONTAL,
        spacing=spacing,
    )


def button(label, action):
    item = Gtk.Button(label=label)
    item.connect("clicked", lambda _: action())
    return item


def add(row, *children):
    for child in children:
        row.append(child)
    return row


def timecode(value):
    seconds = math.ceil(value) if value >= 0 else math.floor(-value)
    return ("+" if value < 0 else "") + f"{seconds // 60}:{seconds % 60:02d}"


class WorkflowPanel:
    def __init__(self, panel, library, scenes):
        self.panel = panel
        self.library = library
        self.scenes = {name: (icon, title) for name, icon, title, _ in scenes}
        self.player = Playback()
        self.current = 0
        self.selected = None
        self.busy = False
        self.checking = False
        self.ticker = None
        self.cancel = Event()
        self.session = None
        self.dispatched = None
        self.confirmed_scene = None
        self.recording_seen = False
        self.timer_window = None
        self.control_rows = []
        self.build()
        self.refresh()

    @property
    def flow(self):
        return self.library.project["workflows"][self.current]

    def build(self):
        self.page = box(True, 14)
        self.panel.add_tab(self.panel.tabs, self.page, "Workflow")
        self.editor = box(True, 12)
        self.page.append(self.editor)
        self.projects = Gtk.DropDown()
        self.flows = Gtk.DropDown()
        # Selector names are user data, including names that match a UI string.
        self.projects.skip_localization = True
        self.flows.skip_localization = True
        self.projects.connect("notify::selected", self.select_project)
        self.flows.connect("notify::selected", self.select_flow)
        add(
            self.editor,
            add(
                box(),
                Gtk.Label(label="Project"),
                self.projects,
                button("New project", lambda: self.name_dialog("New project")),
            ),
        )
        add(
            self.editor,
            add(
                box(),
                Gtk.Label(label="Workflow"),
                self.flows,
                button("New workflow", lambda: self.name_dialog("New workflow")),
                button("Duplicate", lambda: self.name_dialog("Duplicate")),
                button("Rename workflow", lambda: self.name_dialog("Rename workflow")),
            ),
        )
        self.total = Gtk.Label(xalign=0)
        self.total.add_css_class("heading")
        self.target = Gtk.SpinButton.new_with_range(0, 86400, 1)
        self.target.set_tooltip_text("Use zero for no target.")
        self.target.connect("value-changed", self.change_target)
        add(self.editor, add(box(), self.total, Gtk.Label(label="Target (seconds)"), self.target))

        strip = Gtk.ScrolledWindow()
        strip.set_policy(Gtk.PolicyType.AUTOMATIC, Gtk.PolicyType.NEVER)
        strip.set_min_content_height(72)
        self.timeline = box()
        self.timeline.skip_localization = True
        strip.set_child(self.timeline)
        self.editor.append(strip)
        self.drop_on(self.timeline, None)
        self.rehearse = button("Rehearse", lambda: self.start(False))
        self.live = button("Play in OBS", lambda: self.start(True))
        add(self.page, add(box(), self.rehearse, self.live, button("Open timer", self.open_timer)))
        self.status = Gtk.Label(xalign=0, wrap=True)
        add(self.page, self.status, self.controls())
        self.notice = Gtk.Label(xalign=0, wrap=True)
        self.page.append(self.notice)
        self.options = box(True, 10)
        self.page.append(self.options)
        self.inspector = Gtk.Expander(label="Selected step", expanded=True)
        step_options = box(True)
        self.seconds = Gtk.SpinButton.new_with_range(1, 86400, 1)
        self.note = Gtk.Entry(max_length=500, hexpand=True)
        for widget, label in (
            (self.seconds, "Duration (seconds)"),
            (self.note, "Note"),
            (self.target, "Target (seconds)"),
            (self.projects, "Project"),
            (self.flows, "Workflow"),
        ):
            self.panel.localizer.accessible(widget, label)
        add(
            step_options,
            add(
                box(),
                Gtk.Label(label="Duration (seconds)"),
                self.seconds,
                Gtk.Label(label="Note"),
                self.note,
                button("Save step", self.save_step),
            ),
        )
        self.left = button("Move left", lambda: self.move(-1))
        self.right = button("Move right", lambda: self.move(1))
        add(step_options, add(box(), self.left, self.right, button("Remove", self.remove)))
        self.inspector.set_child(step_options)
        self.options.append(self.inspector)

        palette = Gtk.Expander(label="Scene palette", expanded=True)
        grid = Gtk.Grid(row_spacing=6, column_spacing=8, column_homogeneous=True)
        for index, (name, (icon, title)) in enumerate(self.scenes.items()):
            item = Gtk.Button()
            image = Gtk.Image.new_from_icon_name(icon)
            image.add_css_class("scene-" + SCENE_COLORS[name][0])
            item.set_child(add(box(), image, Gtk.Label(label=title)))
            item.connect("clicked", lambda _, scene=name: self.insert_scene(scene))
            self.drag_from(item, "scene:" + name, Gdk.DragAction.COPY)
            grid.attach(item, index % 3, index // 3, 1, 1)
        palette.set_child(grid)
        self.options.append(palette)

    def name_dialog(self, action):
        dialog = Gtk.Dialog(
            title=self.panel.localizer.text(action),
            transient_for=self.panel.window,
            modal=True,
            destroy_with_parent=True,
        )
        entry = Gtk.Entry(max_length=100, activates_default=True)
        if action in ("Rename workflow", "Duplicate"):
            entry.set_text(self.flow["name"] + (" (2)" if action == "Duplicate" else ""))
            entry.select_region(0, -1)
        dialog.get_content_area().append(entry)
        dialog.add_button(self.panel.localizer.text("Cancel"), Gtk.ResponseType.CANCEL)
        accept = dialog.add_button(self.panel.localizer.text("Save"), Gtk.ResponseType.OK)
        accept.set_sensitive(bool(entry.get_text().strip()))
        entry.connect("changed", lambda field: accept.set_sensitive(bool(field.get_text().strip())))
        dialog.set_default_response(Gtk.ResponseType.OK)

        def finish(_, response):
            name = entry.get_text().strip()
            dialog.destroy()
            if response != Gtk.ResponseType.OK or not name:
                return
            if action == "New project" and self.panel.applying_titles:
                self.notice.set_label("Wait for project cards to finish.")
                return
            try:
                if action == "New project":
                    self.library.create_project(name)
                    self.current = 0
                    self.panel.switch_card_project()
                elif action == "Rename workflow":
                    self.flow["name"] = name
                    self.library.save()
                else:
                    created = self.library.create_workflow(
                        name, self.flow if action == "Duplicate" else None
                    )
                    self.current = self.library.project["workflows"].index(created)
                self.selected = None
                self.notice.set_label(
                    "Apply the project to update the cards in OBS."
                    if action == "New project"
                    else ""
                )
            except (OSError, ValueError) as error:
                self.notice.set_label(str(error))
            self.refresh()

        dialog.connect("response", finish)
        dialog.present()

    def select_project(self, dropdown, _):
        index = dropdown.get_selected()
        if self.busy or index >= len(self.library.data["projects"]):
            return
        if self.library.project["id"] == self.library.data["projects"][index]["id"]:
            return
        if self.panel.applying_titles:
            self.notice.set_label("Wait for project cards to finish.")
        else:
            try:
                self.library.select_project(self.library.data["projects"][index]["id"])
                self.current = 0
                self.selected = None
                self.panel.switch_card_project()
                self.notice.set_label("Apply the project to update the cards in OBS.")
            except (OSError, ValueError) as error:
                self.notice.set_label(str(error))
        self.refresh()

    def select_flow(self, dropdown, _):
        index = dropdown.get_selected()
        if self.busy or index >= len(self.library.project["workflows"]):
            return
        if index == self.current:
            return
        self.current = index
        self.selected = None
        self.refresh()

    @staticmethod
    def set_names(dropdown, records):
        names = [r["name"] for r in records]
        model = dropdown.get_model()
        if model is None or [s.get_string() for s in model] != names:
            dropdown.set_model(Gtk.StringList.new(names))

    def refresh(self):
        self.busy = True
        try:
            projects = self.library.data["projects"]
            flows = self.library.project["workflows"]
            self.set_names(self.projects, projects)
            self.projects.set_selected(projects.index(self.library.project))
            self.set_names(self.flows, flows)
            self.flows.set_selected(self.current)
            self.target.set_value(self.flow["targetSeconds"] or 0)
            self.render_steps()
        finally:
            self.busy = False
        self.display()

    def render_steps(self):
        while child := self.timeline.get_first_child():
            self.timeline.remove(child)
        self.step_widgets = {}
        steps = self.flow["steps"]
        if not steps:
            self.timeline.append(
                Gtk.Label(
                    label=self.panel.localizer.text("Drag scenes here or choose a scene below.")
                )
            )
        for index, step in enumerate(steps):
            identifier = step["id"]
            title = self.scene_title(step["scene"])
            item = button(
                f"{index + 1}. {title}\n{timecode(step['seconds'])}",
                lambda key=identifier: self.choose_step(key),
            )
            self.drag_from(item, "step:" + identifier, Gdk.DragAction.MOVE)
            self.drop_on(item, identifier)
            self.timeline.append(item)
            self.step_widgets[identifier] = item
        self.choose_step(self.selected)
        self.summary()

    def scene_title(self, name):
        return self.panel.localizer.text(self.scenes.get(name, (None, name))[1])

    def index_of(self, key):
        return next((i for i, s in enumerate(self.flow["steps"]) if s["id"] == key), None)

    def choose_step(self, key):
        steps = self.flow["steps"]
        index = self.index_of(key)
        if index is None and steps:
            index = 0
        self.selected = steps[index]["id"] if index is not None else None
        self.inspector.set_sensitive(bool(steps))
        for identifier, widget in self.step_widgets.items():
            if identifier == self.selected:
                widget.add_css_class("suggested-action")
            else:
                widget.remove_css_class("suggested-action")
        if index is not None:
            step = steps[index]
            self.seconds.set_value(step["seconds"])
            self.note.set_text(step["note"])
            self.left.set_sensitive(index > 0)
            self.right.set_sensitive(index + 1 < len(steps))

    def summary(self):
        total = sum(s["seconds"] for s in self.flow["steps"])
        target = self.flow["targetSeconds"]
        tr = self.panel.localizer.text
        message = f"{tr('Total')} {timecode(total)}"
        if target is not None and total != target:
            message += (
                " · "
                + timecode(abs(total - target))
                + " "
                + tr("over target" if total > target else "under target")
            )
        self.total.set_label(message)

    def persist(self):
        try:
            self.library.save()
            self.notice.set_label("")
        except (ValueError, OSError) as error:
            self.notice.set_label(str(error))
        self.refresh()

    def change_target(self, control):
        if not self.busy:
            self.flow["targetSeconds"] = control.get_value_as_int() or None
            self.persist()

    def save_step(self):
        index = self.index_of(self.selected)
        if index is not None:
            self.flow["steps"][index].update(
                seconds=self.seconds.get_value_as_int(), note=self.note.get_text()
            )
            self.persist()

    def insert_scene(self, name, index=None):
        steps = self.flow["steps"]
        step = new_step(name)
        steps.insert(len(steps) if index is None else index, step)
        self.selected = step["id"]
        self.persist()

    def remove(self):
        index = self.index_of(self.selected)
        if index is not None:
            self.flow["steps"].pop(index)
            self.persist()

    def move(self, delta):
        steps = self.flow["steps"]
        index = self.index_of(self.selected)
        if index is not None and 0 <= index + delta < len(steps):
            steps.insert(index + delta, steps.pop(index))
            self.persist()

    @staticmethod
    def drag_from(widget, payload, action):
        source = Gtk.DragSource(actions=action)
        source.connect("prepare", lambda *_: Gdk.ContentProvider.new_for_value(payload))
        widget.add_controller(source)

    def drop_on(self, widget, target_key):
        target = Gtk.DropTarget.new(str, Gdk.DragAction.COPY | Gdk.DragAction.MOVE)

        def drop(_, payload, x, _y):
            steps = self.flow["steps"]
            index = len(steps) if target_key is None else self.index_of(target_key)
            if index is None:
                return False
            if target_key is not None and x >= widget.get_width() / 2:
                index += 1
            kind, _, key = payload.partition(":")
            if kind == "scene" and key in self.scenes:
                self.insert_scene(key, index)
            elif kind == "step" and (source := self.index_of(key)) is not None:
                steps.insert(index - (source < index), steps.pop(source))
                self.selected = key
                self.persist()
            else:
                return False
            return True

        target.connect("drop", drop)
        widget.add_controller(target)

    def controls(self):
        pause = button("Pause", self.toggle_pause)
        advance = button("Next", self.next_step)
        stop = button("Stop", self.stop)
        self.control_rows.append((pause, advance, stop))
        return add(box(), pause, advance, stop)

    def open_timer(self):
        if self.timer_window is None:
            window = Gtk.Window(
                title="Timer",
                transient_for=self.panel.window,
                destroy_with_parent=True,
                hide_on_close=True,
            )
            self.timer_window = window
            window.add_css_class("umberto-lab")
            window.set_default_size(440, 260)
            body = box(True, 10)
            for edge in ("top", "bottom", "start", "end"):
                getattr(body, "set_margin_" + edge)(18)
            self.clock_label = Gtk.Label()
            self.clock_label.add_css_class("title-1")
            self.scene_label = Gtk.Label(wrap=True)
            self.next_label = Gtk.Label(wrap=True)
            self.cue_label = Gtk.Label(wrap=True)
            self.cue_label.skip_localization = True
            self.record_label = Gtk.Label()
            self.free_seconds = Gtk.SpinButton.new_with_range(1, 86400, 1)
            self.free_seconds.set_value(180)
            self.panel.localizer.accessible(self.free_seconds, "Free timer (seconds)")
            self.free_button = button("Start timer", self.start_free)
            add(
                body,
                self.scene_label,
                self.clock_label,
                self.next_label,
                self.cue_label,
                self.record_label,
                self.controls(),
                add(
                    box(),
                    Gtk.Label(label="Free timer (seconds)"),
                    self.free_seconds,
                    self.free_button,
                ),
            )
            window.set_child(body)
            self.panel.localizer.attach(window)
        self.display()
        self.timer_window.present()

    @property
    def active(self):
        return self.checking or self.player.state in ("switching", "running", "paused")

    def start_free(self):
        if self.active:
            return
        self.player.start_timer(self.free_seconds.get_value_as_int())
        self.schedule()

    def start(self, live):
        if self.active or not self.flow["steps"]:
            return
        steps = deepcopy(self.flow["steps"])
        self.cancel = Event()
        self.dispatched = None
        if live:
            if self.panel.applying_titles:
                self.notice.set_label("Wait for project cards to finish.")
                return
            if not self.panel.obs_connected:
                self.notice.set_label("Connect to OBS before playing the workflow.")
                return
            self.checking = True
            self.recording_seen = False
            cancel = self.cancel

            def ready(result, error, _):
                if cancel.is_set():
                    return
                self.checking = False
                if error:
                    self.stop(str(error))
                    return
                socket, collection, self.confirmed_scene, recording = result
                self.recording_seen |= recording
                self.session = socket, collection
                self.player.start(steps, live=True)
                self.schedule()

            self.panel.submit_task(
                self.panel.obs.workflow_preflight,
                ([s["scene"] for s in steps],),
                ready,
                operation="workflow_preflight",
            )
        else:
            self.player.start(steps)
            self.schedule()
        self.open_timer()

    def schedule(self):
        self.dispatch_pending()
        self.display()
        if self.ticker is None and self.player.state == "running":
            self.ticker = GLib.timeout_add(200, self.tick)

    def dispatch_pending(self):
        if not self.player.pending:
            return
        token, scene = self.player.pending
        if token == self.dispatched:
            return
        self.dispatched = token
        cancel = self.cancel

        def complete(result, error, _):
            if cancel.is_set() or not self.player.is_pending(token):
                return
            if error or result is None:
                self.stop(str(error) if error else "Workflow stopped.")
                return
            self.confirmed_scene, recording = result
            self.recording_seen |= recording
            self.player.confirm(token, self.confirmed_scene)
            self.schedule()

        self.panel.submit_task(
            self.panel.obs.workflow_switch,
            (self.session, self.confirmed_scene, scene, cancel, self.recording_seen),
            complete,
            operation="workflow_switch",
        )

    def observe(self, result, error=None):
        if (self.player.mode == "live" and self.active) or self.checking:
            if (
                error
                or not result.get("connectionAvailable", True)
                or not result.get("statusFresh", True)
                or type(result.get("outputActive")) is not bool
                or type(result.get("outputPaused")) is not bool
            ):
                self.stop("OBS status is unconfirmed.")
            elif result.get("outputPaused") or (self.recording_seen and not result["outputActive"]):
                self.stop("Recording was paused or stopped.")
            else:
                self.recording_seen |= result["outputActive"]
                if self.player.state in ("running", "paused"):
                    self.player.observe(result["currentProgramSceneName"])
                    if self.player.state == "stopped":
                        self.cancel.set()
        self.display()

    def manual_override(self):
        if self.checking or (self.player.mode == "live" and self.active):
            self.stop("Manual control.")

    def toggle_pause(self):
        if self.player.state == "running":
            self.player.pause()
        elif self.player.state == "paused":
            self.player.resume()
            self.schedule()
        self.display()

    def next_step(self):
        self.player.next()
        self.dispatch_pending()
        self.display()

    def stop(self, reason=""):
        self.cancel.set()
        self.checking = False
        self.player.stop(reason)
        self.display()

    def tick(self):
        self.player.tick()
        self.dispatch_pending()
        self.display()
        if self.player.state != "running":
            self.ticker = None
            return GLib.SOURCE_REMOVE
        return GLib.SOURCE_CONTINUE

    def display(self):
        player = self.player
        active = self.active
        tr = self.panel.localizer.text
        self.editor.set_sensitive(not active)
        self.options.set_sensitive(not active)
        self.rehearse.set_sensitive(not active and bool(self.flow["steps"]))
        self.live.set_sensitive(
            not active
            and not self.panel.applying_titles
            and self.panel.obs_connected
            and bool(self.flow["steps"])
        )
        for pause, advance, stop in self.control_rows:
            pause.set_label(tr("Resume" if player.state == "paused" else "Pause"))
            pause.set_sensitive(not self.checking and player.state in ("running", "paused"))
            advance.set_sensitive(
                not self.checking and player.mode != "timer" and player.state == "running"
            )
            stop.set_sensitive(active)
        state = {
            "idle": "Ready",
            "switching": "Changing scene…",
            "running": "Running",
            "paused": "Paused",
            "finished": "Finished",
            "stopped": "Stopped",
        }[player.state]
        title = tr("Checking OBS…" if self.checking else state)
        subtitle = ""
        cue = ""
        if not self.checking and player.mode == "timer":
            title += " · " + tr("Free timer")
        elif not self.checking and player.steps and 0 <= player.index < len(player.steps):
            step = player.steps[player.index]
            title = ("OBS" if player.mode == "live" else tr("Rehearsal")) + " · " + title
            title += " · " + self.scene_title(step["scene"])
            if player.index + 1 < len(player.steps):
                subtitle = (
                    tr("Next") + " · " + self.scene_title(player.steps[player.index + 1]["scene"])
                )
            subtitle += (
                ("\n" if subtitle else "")
                + tr("Remaining")
                + " · "
                + timecode(player.total_remaining)
            )
            cue = step["note"]
        if player.reason and not self.checking:
            title += " · " + tr(player.reason)
        self.status.set_label(
            title + (" · " + timecode(player.remaining) if active and not self.checking else "")
        )
        if self.timer_window is not None:
            self.free_button.set_sensitive(not active)
            self.free_seconds.set_sensitive(not active)
            self.clock_label.set_label(
                timecode(player.remaining) if active and not self.checking else "0:00"
            )
            self.scene_label.set_label(title)
            self.next_label.set_label(subtitle)
            self.cue_label.set_label(cue)
            self.cue_label.set_visible(bool(cue))
            self.record_label.set_label(self.panel.obs_status.get_label())

    def close(self):
        self.cancel.set()
        if self.ticker is not None:
            GLib.source_remove(self.ticker)
            self.ticker = None
        if self.timer_window is not None:
            self.timer_window.destroy()
