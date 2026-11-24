"""Local rundown library and transport-independent, monotonic playback for GTK."""

import fcntl
import json
import os
import tempfile
import time
from copy import deepcopy
from functools import cache
from pathlib import Path
from uuid import UUID, uuid4

LEGACY_PROJECT_ID = str(UUID(int=0))
MAX_SECONDS = 86400
MAX_FILE_BYTES = 512 * 1024


@cache
def _scenes():
    path = Path(__file__).resolve().parents[1] / "Shared" / "scene-catalog.json"
    return frozenset(scene["name"] for scene in json.loads(path.read_bytes())["scenes"])


def _text(value, limit, field, optional=False):
    if not isinstance(value, str) or (not optional and not value.strip()):
        raise ValueError(f"Invalid {field}")
    # The WinUI fields use UTF-16 code units for their maximum length.
    if len(value.encode("utf-16-le")) > limit * 2:
        raise ValueError(f"Invalid {field}")


def _seconds(value):
    if type(value) is not int or not 1 <= value <= MAX_SECONDS:
        raise ValueError("Duration must be between 1 and 86400 seconds")


def _id(value):
    if not isinstance(value, str) or str(UUID(value)) != value:
        raise ValueError("Invalid workflow library identifier")


def _fields(value, names):
    if not isinstance(value, dict) or set(value) != set(names):
        raise ValueError("Invalid workflow library fields")


def _entries(values):
    if not isinstance(values, list):
        raise ValueError("Expected a list in workflow library")
    seen = set()
    for item in values:
        if not isinstance(item, dict):
            raise ValueError("Invalid workflow library entry")
        key = item.get("id")
        _id(key)
        if key in seen:
            raise ValueError("Duplicate identifier in workflow library")
        seen.add(key)
        yield item


def _validate_steps(steps):
    for step in _entries(steps):
        fields = ("id", "scene", "seconds", "note")
        _fields(step, (*fields, "transition") if "transition" in step else fields)
        if step.get("transition", "default") not in ("default", "cut", "fade"):
            raise ValueError("Invalid workflow transition")
        if not isinstance(step["scene"], str) or step["scene"] not in _scenes():
            raise ValueError("Unknown workflow scene")
        _text(step["note"], 500, "step note", optional=True)
        _seconds(step["seconds"])


def _validate(data):
    _fields(data, ("version", "activeProjectId", "projects"))
    if type(data["version"]) is not int or data["version"] != 1:
        raise ValueError("Unsupported workflow library version")
    for project in _entries(data["projects"]):
        _fields(project, ("id", "name", "workflows"))
        _text(project["name"], 100, "project name")
        for flow in _entries(project["workflows"]):
            _fields(flow, ("id", "name", "targetSeconds", "steps"))
            _text(flow["name"], 100, "workflow name")
            if flow["targetSeconds"] is not None:
                _seconds(flow["targetSeconds"])
            _validate_steps(flow["steps"])
        if not project["workflows"]:
            raise ValueError("Project requires a workflow")
    if not any(project["id"] == data["activeProjectId"] for project in data["projects"]):
        raise ValueError("Active project is missing")


def new_step(scene, seconds=15, note="", transition="default"):
    step = {"id": str(uuid4()), "scene": scene, "seconds": seconds, "note": note}
    if transition != "default":
        step["transition"] = transition
    _validate_steps([step])
    return step


def _workflow(name):
    _text(name, 100, "workflow name")
    return {"id": str(uuid4()), "name": name, "targetSeconds": 180, "steps": []}


def _project(key, name):
    _text(name, 100, "project name")
    return {"id": key, "name": name, "workflows": [_workflow("Workflow 1")]}


class WorkflowLibrary:
    """Single-owner editable document. Failed edits roll back to the last saved snapshot."""

    def __init__(self, path, legacy_name="My project"):
        self.path = Path(path)
        self._revision = self._read()
        if self._revision is None:
            self.data = {
                "version": 1,
                "activeProjectId": LEGACY_PROJECT_ID,
                "projects": [_project(LEGACY_PROJECT_ID, legacy_name)],
            }
        else:
            try:
                self.data = json.loads(self._revision)
            except RecursionError as error:
                raise ValueError("Workflow library nesting is too deep") from error
            _validate(self.data)
        self._saved = deepcopy(self.data)

    def _read(self):
        try:
            with self.path.open("rb") as stream:
                raw = stream.read(MAX_FILE_BYTES + 1)
        except FileNotFoundError:
            return None
        if len(raw) > MAX_FILE_BYTES:
            raise ValueError("Workflow library is too large")
        return raw

    def save(self):
        try:
            _validate(self.data)
            payload = (json.dumps(self.data, ensure_ascii=False, indent=2) + "\n").encode()
            if len(payload) > MAX_FILE_BYTES:
                raise ValueError("Workflow library is too large")
            self.path.parent.mkdir(parents=True, exist_ok=True)
            # App instances share this lock even when atomic replacement changes the inode.
            lock = self.path.with_suffix(self.path.suffix + ".lock")
            with lock.open("a+b") as stream:
                try:
                    fcntl.flock(stream, fcntl.LOCK_EX | fcntl.LOCK_NB)
                except BlockingIOError as error:
                    raise ValueError("Another instance is saving the workflow library.") from error
                if self._read() != self._revision:
                    raise ValueError("Workflow library changed on disk. Reopen it before editing.")
                staged = None
                try:
                    with tempfile.NamedTemporaryFile(dir=self.path.parent, delete=False) as output:
                        staged = Path(output.name)
                        output.write(payload)
                        output.flush()
                        os.fsync(output.fileno())
                    os.replace(staged, self.path)
                finally:
                    if staged is not None:
                        staged.unlink(missing_ok=True)
                self._revision = payload
        except (OSError, TypeError, ValueError):
            self.data = deepcopy(self._saved)
            raise
        self._saved = deepcopy(self.data)

    @property
    def project(self):
        return next(p for p in self.data["projects"] if p["id"] == self.data["activeProjectId"])

    def select_project(self, project_id):
        self.data["activeProjectId"] = project_id
        self.save()

    def create_project(self, name):
        project = _project(str(uuid4()), name)
        self.data["projects"].append(project)
        self.select_project(project["id"])
        return project

    @staticmethod
    def _find(entries, entry_id):
        for entry in entries:
            if entry["id"] == entry_id:
                return entry
        raise ValueError("The selected project or workflow no longer exists.")

    def rename_project(self, project_id, name):
        _text(name, 100, "project name")
        self._find(self.data["projects"], project_id)["name"] = name
        self.save()

    def remove_project(self, project_id):
        projects = self.data["projects"]
        if len(projects) <= 1:
            raise ValueError("Keep at least one project.")
        projects.remove(self._find(projects, project_id))
        if self.data["activeProjectId"] == project_id:
            self.data["activeProjectId"] = projects[0]["id"]
        self.save()

    def create_workflow(self, name, source=None):
        workflow = _workflow(name)
        if source is not None:
            workflow["targetSeconds"] = source["targetSeconds"]
            workflow["steps"] = [
                new_step(
                    step["scene"], step["seconds"], step["note"], step.get("transition", "default")
                )
                for step in source["steps"]
            ]
        self.project["workflows"].append(workflow)
        self.save()
        return workflow

    def rename_workflow(self, workflow_id, name):
        _text(name, 100, "workflow name")
        self._find(self.project["workflows"], workflow_id)["name"] = name
        self.save()

    def remove_workflow(self, workflow_id):
        workflows = self.project["workflows"]
        if len(workflows) <= 1:
            raise ValueError("Keep at least one workflow in this project.")
        workflows.remove(self._find(workflows, workflow_id))
        self.save()


class Playback:
    """Own this object on the main loop. Mutations to OBS are dispatched by the adapter.

    A unique pending token represents exactly one live transition. Only a positive
    acknowledgement of the current token can arm the next deadline. The adapter
    serializes mutations, discards stale work before sending, and never retries.
    """

    def __init__(self, clock=time.monotonic):
        self.clock = clock
        self.state = "idle"
        self.mode = "rehearsal"
        self.steps = []
        self.index = 0
        self.reason = ""
        self.pending = None
        self._serial = 0
        self._deadline = 0.0
        self._held = 0.0
        self._after_current = 0

    def start(self, steps, live=False):
        _validate_steps(steps)
        if not steps:
            raise ValueError("Add at least one scene")
        self.steps = deepcopy(steps)
        self._after_current = sum(step["seconds"] for step in self.steps)
        self.mode = "live" if live else "rehearsal"
        self.reason = ""
        self._enter(0, self.clock())

    def start_timer(self, seconds):
        _seconds(seconds)
        self.steps = []
        self.mode = "timer"
        self._after_current = 0
        self.reason = ""
        self.index = 0
        self.pending = None
        self._run(seconds, self.clock())

    def _enter(self, index, start):
        self.index = index
        duration = self.steps[index]["seconds"]
        self._after_current -= duration
        if self.mode == "live":
            self._serial += 1
            self.pending = (self._serial, self.steps[index]["scene"])
            self._held = duration
            self.state = "switching"
        else:
            self.pending = None
            self._run(duration, start)

    def _run(self, duration, start):
        self._deadline = start + duration
        self.state = "running"

    @property
    def remaining(self):
        value = self._deadline - self.clock() if self.state == "running" else self._held
        return value if self.mode == "timer" else max(0.0, value)

    @property
    def total_remaining(self):
        if self.state in ("idle", "finished", "stopped"):
            return 0.0
        return self.remaining + self._after_current

    def is_pending(self, token):
        pending = self.pending
        return pending is not None and token == pending[0]

    def confirm(self, token, scene):
        if not self.is_pending(token):
            return
        if self.pending[1] != scene:
            self.stop("Scene change was not confirmed")
            return
        self.pending = None
        self._run(self.steps[self.index]["seconds"], self.clock())

    def fail(self, token, reason):
        if self.is_pending(token):
            self.stop(reason)

    def observe(self, scene):
        if self.mode == "live" and self.state in ("running", "paused"):
            if self.steps[self.index]["scene"] != scene:
                self.stop("Program scene changed outside the workflow")

    def _advance(self, start):
        if self.index + 1 < len(self.steps):
            self._enter(self.index + 1, start)
        else:
            self.state = "finished"
            self._held = 0.0

    def tick(self):
        if self.mode == "timer":
            return
        now = self.clock()
        # In rehearsal elapsed time covers all intervening steps. A live transition
        # immediately enters switching and waits for OBS, so late ticks never burst.
        while self.state == "running" and now >= self._deadline:
            self._advance(self._deadline)

    def next(self):
        if self.mode != "timer" and self.state == "running":
            self._advance(self.clock())

    def pause(self):
        if self.state == "running":
            self._held = self.remaining
            self.state = "paused"

    def resume(self):
        if self.state == "paused":
            self._run(self._held, self.clock())

    def stop(self, reason=""):
        self.pending = None
        self.steps = []
        self.index = 0
        self.state = "stopped"
        self.reason = reason
        self._held = 0.0
