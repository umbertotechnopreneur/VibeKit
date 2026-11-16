"""Native GTK exit/save check. Run on an isolated display, session bus and XDG state."""
import argparse
from pathlib import Path
import sys
import threading
import time

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "Linux"))
import openai_usage as widget

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--fail-first", action="store_true")
args = parser.parse_args()
release = threading.Event()
saved = []
attempts = 0
fail = args.fail_first


def invoke(command="snapshot", value=None):
    global attempts
    if command == "settings":
        return {"codexPath": "codex", "timeZoneId": "UTC", "alwaysOnTop": False, "bigSize": False}
    if command == "save-settings":
        attempts += 1
        if attempts == 1 and not release.wait(10):
            raise RuntimeError("Test did not release the first writer")
        if fail:
            raise OSError("Synthetic write failure")
        saved.append(dict(value))
        return {"ok": True}
    return {"connected": False, "status": "Isolated native save/exit check"}


widget.invoke = invoke
app = widget.UsageApp()
app.set_flags(widget.Gio.ApplicationFlags.NON_UNIQUE)
phase = 0
failed = False
started = time.monotonic()
retry_at = None


def poll():
    global phase, fail, failed, retry_at
    window = app.window
    try:
        if time.monotonic() - started > 12:
            raise AssertionError("Native save/exit check timed out")
        if phase == 0 and window is not None and window.settings_ready and not window.busy:
            window.settings["bigSize"] = True
            window.settings_revision += 1
            window.save_async()
            window.settings["bigSize"] = False
            window.settings_revision += 1
            window.save_async()
            window.exit_application()
            assert not window.closed
            phase = 1
            widget.GLib.timeout_add(100, lambda: (release.set(), False)[1])
        elif phase == 1 and args.fail_first and not window.exit_pending and not window.saving:
            assert not window.closed and window.get_visible() and window.problem.get_visible()
            phase = 2
            retry_at = time.monotonic() + 1.2
            window.state = {"connected": True, "status": "Synthetic successful account refresh"}
            window.tick()
        elif phase == 2 and time.monotonic() >= retry_at:
            assert not window.closed and window.problem.get_visible()
            assert "Synthetic write failure" in window.problem.get_tooltip_text()
            fail = False
            phase = 3
            window.exit_application()
        return True
    except Exception as error:
        failed = True
        print("FAIL:", error)
        release.set()
        app.quit()
        return False


widget.GLib.timeout_add(50, poll)
result = app.run(["openai-usage-gtk-check"])
assert not failed and result == 0
assert app.window.closed and app.window.saved_settings_revision == 2
assert saved[-1]["bigSize"] is False
assert phase == (3 if args.fail_first else 1)
print("PASS native GTK latest-revision save, deferred close" + (", visible error and explicit retry" if args.fail_first else ""))
