"""Native linear workflow editor and detachable timer for the GTK control desk."""

from copy import deepcopy
from threading import Event

import gi

from obs_remote import WorkflowTransitionRecoveryError
from panel_theme import SCENE_COLORS
from presenter_timer import PresenterTimer
from timer_options import TimerOptions
from workflow_dialogs import LibraryDialog, step_dialog, workflow_details
from workflow_widgets import (
    TRANSITIONS,
    add,
    box,
    button,
    icon,
    icon_button,
    text,
    timecode,
    tool_button,
)
from workflows import Playback, new_step

gi.require_version("Gtk", "4.0")
from gi.repository import Gdk, GLib, Gtk  # noqa: E402


class WorkflowPanel:
    def __init__(self, panel, library, scenes):
        self.panel = panel
        self.library = library
        self.scenes = {name: (icon, title) for name, icon, title, _ in scenes}
        self.player = Playback()
        self.current = 0
        self.selected = None
        self.manager = None
        self.checking = False
        self.ticker = None
        self.cancel = Event()
        self.session = None
        self.dispatched = None
        self.confirmed_scene = None
        self.recording_seen = False
        self.timer_options = TimerOptions.load()
        self.timer_window = None
        self.record_cue = None
        self.control_rows = []
        self.build()
        self.refresh()

    @property
    def flow(self):
        return self.library.project["workflows"][self.current]

    def build(self):
        self.page = box(True, 14)
        self.panel.add_tab(self.panel.tabs, self.page, "Workflow")
        self.editor = box(True, 0)
        self.workflow_name = text(style="workflow-title", truncate=True)
        self.project_name = text(style="dim-label", truncate=True)
        for label in (self.project_name, self.workflow_name):
            label.skip_localization = True
        names = add(box(True, 2), self.project_name, self.workflow_name)
        names.set_hexpand(True)
        self.library_button = tool_button("folder-open-symbolic", "Library", self.open_library)
        self.library_button.set_valign(Gtk.Align.CENTER)
        self.page.append(add(self.editor, add(box(), names, self.library_button)))

        board = box(True, 0)
        board.add_css_class("workflow-board")
        heading = text("Sequence", "heading")
        heading.set_hexpand(True)
        self.total = text(style="workflow-metric")
        self.target_display = text(style="dim-label")
        self.target_button = icon_button(
            "document-edit-symbolic", self.tr("Edit workflow"), self.edit_workflow
        )
        self.target_button.add_css_class("workflow-small")
        head = add(box(spacing=10), heading, self.total, self.target_display, self.target_button)
        head.add_css_class("workflow-board-header")
        self.strip = Gtk.ScrolledWindow(hexpand=True)
        self.strip.set_policy(Gtk.PolicyType.AUTOMATIC, Gtk.PolicyType.NEVER)
        self.strip.set_overlay_scrolling(False)
        self.strip.set_size_request(-1, 174)
        self.timeline = box(spacing=3)
        self.timeline.set_hexpand(True)
        self.timeline.set_margin_start(12)
        self.timeline.set_margin_end(12)
        self.timeline.skip_localization = True
        self.strip.set_child(self.timeline)
        self.drop_on(self.timeline, None)

        self.rehearse = tool_button(
            "media-playback-start-symbolic", "Rehearse", lambda: self.start(False)
        )
        self.live = button("Play in OBS", lambda: self.start(True))
        self.live.add_css_class("suggested-action")
        self.launch_actions = add(box(), self.rehearse, self.live)
        self.transport = self.controls()
        spacer = box()
        spacer.set_hexpand(True)
        self.timer_button = tool_button(
            "preferences-system-time-symbolic", "Open timer", self.open_timer
        )
        footer = add(box(), self.launch_actions, self.transport, spacer, self.timer_button)
        footer.add_css_class("workflow-board-footer")
        add(board, head, self.strip, footer)
        self.page.append(board)

        self.status = Gtk.Label(xalign=0, wrap=True)
        self.status.add_css_class("dim-label")
        self.notice = Gtk.Label(xalign=0, wrap=True, visible=False)
        self.notice.connect(
            "notify::label", lambda note, _: note.set_visible(bool(note.get_label()))
        )
        add(self.page, self.status, self.notice)
        self.options = Gtk.Expander(label="Scene palette", expanded=True)
        self.options.add_css_class("workflow-palette")
        grid = Gtk.Grid(row_spacing=6, column_spacing=8, column_homogeneous=True, margin_top=10)
        for index, (name, (_, title)) in enumerate(self.scenes.items()):
            item = Gtk.Button()
            item.add_css_class("workflow-source")
            image = self.scene_icon(name, 17)
            item.set_child(add(box(spacing=9), image, text(title, truncate=True)))
            item.connect("clicked", lambda _, scene=name: self.insert_scene(scene))
            self.drag_from(item, "scene:" + name, Gdk.DragAction.COPY)
            grid.attach(item, index % 3, index // 3, 1, 1)
        self.options.set_child(grid)
        self.page.append(self.options)

    @property
    def tr(self):
        return self.panel.localizer.text

    def open_library(self):
        if self.active:
            return None
        if self.manager is None:
            self.manager = LibraryDialog(self)
        self.manager.present()
        return self.manager

    def edit_workflow(self):
        if self.active:
            return None
        return workflow_details(self, self.panel.window, self.flow["id"])

    def edit_step(self, identifier):
        if self.active or self.index_of(identifier) is None:
            return None
        self.choose_step(identifier)
        return step_dialog(self, identifier)

    def select_library_item(self, project_id, flow_id=None):
        if self.active:
            return
        if self.library.project["id"] != project_id:
            if self.panel.applying_titles:
                raise ValueError("Wait for project cards to finish.")
            self.library.select_project(project_id)
            self.current = 0
            self.panel.switch_card_project()
            self.notice.set_label("Apply the project to update the cards in OBS.")
        if flow_id is not None:
            self.current = next(
                n
                for n, item in enumerate(self.library.project["workflows"])
                if item["id"] == flow_id
            )
        self.selected = None
        self.refresh()

    def refresh(self):
        self.current = min(self.current, len(self.library.project["workflows"]) - 1)
        self.workflow_name.set_label(self.flow["name"])
        self.project_name.set_label(self.library.project["name"])
        self.project_name.set_tooltip_text(self.library.project["name"])
        self.workflow_name.set_tooltip_text(self.flow["name"])
        self.render_steps()
        self.display()

    def scene_icon(self, name, size):
        image = icon(self.scenes[name][0], size)
        image.add_css_class("workflow-scene-icon")
        image.add_css_class("scene-" + SCENE_COLORS[name][0])
        return image

    def render_steps(self):
        while child := self.timeline.get_first_child():
            self.timeline.remove(child)
        self.step_widgets = {}
        self.highlighted = None
        self.track_locked = None
        steps = self.flow["steps"]
        self.timeline.set_spacing(3 if steps else 0)
        if not steps:
            empty = box(True, 7)
            empty.set_halign(Gtk.Align.CENTER)
            empty.set_valign(Gtk.Align.CENTER)
            empty.set_hexpand(True)
            empty_icon = icon("list-add-symbolic", 24)
            empty_icon.add_css_class("dim-label")
            hint = Gtk.Label(label=self.tr("Drag scenes here or choose a scene below."), wrap=True)
            add(empty, empty_icon, hint)
            self.timeline.append(empty)
        elapsed = 0
        for index, step in enumerate(steps):
            if index:
                arrow = icon("go-next-symbolic", 15)
                arrow.add_css_class("dim-label")
                self.timeline.append(arrow)
            card = self.step_card(index, step, elapsed)
            self.timeline.append(card)
            self.step_widgets[step["id"]] = card
            elapsed += step["seconds"]
        self.choose_step(self.selected)
        self.total.set_label(f"{self.tr('Total')}  {timecode(elapsed)}")
        target = self.flow["targetSeconds"]
        self.target_display.set_visible(target is not None)
        if target is not None:
            self.target_display.set_label(f"{self.tr('Target')}  {timecode(target)}")
            self.target_display.set_tooltip_text(
                f"{timecode(abs(elapsed - target))} "
                + self.tr("over target" if elapsed > target else "under target")
                if elapsed != target
                else None
            )
            if elapsed > target:
                self.target_display.add_css_class("workflow-over")
            else:
                self.target_display.remove_css_class("workflow-over")

    def step_card(self, index, step, elapsed):
        name = step["scene"]
        title = self.scene_title(name)
        identifier = step["id"]
        card = Gtk.Button(valign=Gtk.Align.CENTER)
        card.set_hexpand(False)
        card.add_css_class("workflow-step")
        card.set_size_request(154, -1)
        color_name = SCENE_COLORS[name][0]
        card.add_css_class("step-" + color_name)
        number = text(f"{index + 1:02d}", "workflow-position")
        number.set_hexpand(True)
        edit = icon("document-edit-symbolic", 13)
        edit.add_css_class("workflow-step-edit")
        top = add(box(), number, edit, self.scene_icon(name, 23))
        name_label = text(title, "workflow-step-name", truncate=True)
        name_label.set_max_width_chars(16)
        duration = text(timecode(step["seconds"]), "workflow-step-time")
        meta = text(f"{self.tr('Starts at')} {timecode(elapsed)}", "workflow-secondary")
        content = add(box(True, 4), top, name_label, duration, meta)
        transition = step.get("transition", "default")
        if transition != "default":
            meta.set_label(f"{timecode(elapsed)}   ·   {self.tr(TRANSITIONS[transition])}")
        card.set_child(content)
        card.set_tooltip_text(f"{self.tr('Edit step')} · {title}")
        card.update_property(
            [Gtk.AccessibleProperty.LABEL],
            [
                f"{index + 1}. {title}, {self.tr('Starts at')} {timecode(elapsed)}, {timecode(step['seconds'])}"
            ],
        )
        card.connect("clicked", lambda _: self.edit_step(identifier))
        self.drag_from(card, "step:" + identifier, Gdk.DragAction.MOVE)
        self.drop_on(card, identifier)
        return card

    def scene_title(self, name):
        return self.tr(self.scenes.get(name, (None, name))[1])

    def index_of(self, key):
        return next((i for i, s in enumerate(self.flow["steps"]) if s["id"] == key), None)

    def choose_step(self, key):
        self.selected = key if key in self.step_widgets else None
        for identifier, widget in self.step_widgets.items():
            if identifier == self.selected:
                widget.add_css_class("workflow-selected")
            else:
                widget.remove_css_class("workflow-selected")

    def focus_step(self, identifier):
        if identifier in self.step_widgets:
            self.step_widgets[identifier].grab_focus()

    def reveal_step(self, identifier):
        card = self.step_widgets.get(identifier)
        if card is None:
            return

        def after_layout(widget, _clock):
            if (
                self.step_widgets.get(identifier) is not widget
                or (self.highlighted or self.selected) != identifier
            ):
                return GLib.SOURCE_REMOVE
            if widget.get_width() == 0:
                return GLib.SOURCE_CONTINUE
            ok, bounds = widget.compute_bounds(self.timeline)
            if ok:
                margin = self.timeline.get_margin_start()
                left = bounds.get_x() + margin
                # Include the two-pixel focus ring and the track's side inset.
                padding = margin + 2
                self.strip.get_hadjustment().clamp_page(
                    left - padding, left + bounds.get_width() + padding
                )
            return GLib.SOURCE_REMOVE

        card.add_tick_callback(after_layout)

    def persist(self):
        try:
            self.library.save()
            self.notice.set_label("")
            saved = True
        except (ValueError, OSError) as error:
            self.notice.set_label(str(error))
            saved = False
        self.refresh()
        if saved:
            self.reveal_step(self.selected)
        return saved

    def insert_scene(self, name, index=None):
        if self.active:
            return
        steps = self.flow["steps"]
        step = new_step(name)
        steps.insert(len(steps) if index is None else index, step)
        self.selected = step["id"]
        self.persist()

    def drag_from(self, widget, payload, action):
        source = Gtk.DragSource(actions=action)
        source.connect(
            "prepare",
            lambda *_: None if self.active else Gdk.ContentProvider.new_for_value(payload),
        )
        widget.add_controller(source)

    def drop_on(self, widget, target_key):
        target = Gtk.DropTarget.new(str, Gdk.DragAction.COPY | Gdk.DragAction.MOVE)

        def drop(_, payload, x, _y):
            if self.active:
                return False
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
            self.timer_window = PresenterTimer(self)
        self.display()
        self.timer_window.present()
        return self.timer_window

    def apply_timer_options(self, options):
        options.save()
        self.timer_options = options
        if self.timer_window is not None:
            self.timer_window.configure()
        self.display()

    def show_record_cue(self, color, seconds):
        first = self.record_cue is None
        self.record_cue = color, seconds
        if first:
            self.open_timer()
        else:
            self.display()

    def clear_record_cue(self):
        self.record_cue = None
        self.display()

    @property
    def active(self):
        return self.checking or self.player.state in ("switching", "running", "paused")

    def start_free(self):
        if self.active:
            return
        self.player.start_timer(self.timer_options.free_seconds)
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
                ([s["scene"] for s in steps], [s.get("transition", "default") for s in steps]),
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
                if isinstance(error, WorkflowTransitionRecoveryError):
                    self.notice.set_label(str(error))
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
            (
                self.session,
                self.confirmed_scene,
                scene,
                cancel,
                self.recording_seen,
                self.player.steps[self.player.index].get("transition", "default"),
            ),
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

    def display_track(self, active):
        if self.track_locked != active:
            self.track_locked = active
            if active:
                self.timeline.add_css_class("workflow-playing")
            else:
                self.timeline.remove_css_class("workflow-playing")
            for card in self.step_widgets.values():
                card.set_focusable(not active)
        identifier = None
        if active and not self.checking and self.player.steps:
            identifier = self.player.steps[self.player.index]["id"]
        if identifier == self.highlighted:
            return
        if previous := self.step_widgets.get(self.highlighted):
            previous.remove_css_class("workflow-now")
        self.highlighted = identifier
        if card := self.step_widgets.get(identifier):
            card.add_css_class("workflow-now")
            self.reveal_step(identifier)

    def display(self):
        player = self.player
        active = self.active
        tr = self.panel.localizer.text
        self.editor.set_sensitive(not active)
        self.target_button.set_sensitive(not active)
        self.display_track(active)
        self.transport.set_visible(active)
        self.launch_actions.set_visible(not active)
        self.status.set_visible(active or bool(player.reason) or player.state == "finished")
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
        if not self.checking and player.mode == "timer":
            title += " · " + tr("Free timer")
        elif not self.checking and player.steps and 0 <= player.index < len(player.steps):
            mode = "OBS" if player.mode == "live" else tr("Rehearsal")
            title = mode + " · " + title + " · " + self.scene_title(player.steps[player.index]["scene"])
        if player.reason and not self.checking:
            title += " · " + tr(player.reason)
        self.status.set_label(
            title + (" · " + timecode(player.remaining) if active and not self.checking else "")
        )
        if self.timer_window is not None:
            self.timer_window.update_display("Checking OBS…" if self.checking else state)

    def close(self):
        self.cancel.set()
        if self.ticker is not None:
            GLib.source_remove(self.ticker)
            self.ticker = None
        if self.timer_window is not None:
            self.timer_window.destroy()
