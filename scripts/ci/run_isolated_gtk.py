#!/usr/bin/env python3
"""Run native GTK checks with a private display, session bus and XDG directories."""

import argparse
import os
from pathlib import Path
import signal
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--timeout", type=int, default=90)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    command = args.command[1:] if args.command[:1] == ["--"] else args.command
    if not command:
        parser.error("Supply the GTK check command after --.")
    with tempfile.TemporaryDirectory(prefix="vibekit-gtk-check-") as directory:
        environment = os.environ.copy()
        for key, name in (
            ("XDG_CONFIG_HOME", "config"), ("XDG_DATA_HOME", "data"),
            ("XDG_CACHE_HOME", "cache"), ("XDG_STATE_HOME", "state"),
            ("XDG_RUNTIME_DIR", "runtime"),
        ):
            path = Path(directory) / name
            path.mkdir(mode=0o700)
            environment[key] = str(path)
        # Never connect test widgets or activated services to the live desktop.
        for key in ("DISPLAY", "WAYLAND_DISPLAY", "DBUS_SESSION_BUS_ADDRESS", "SESSION_MANAGER"):
            environment.pop(key, None)
        environment.update(GSETTINGS_BACKEND="memory", GDK_BACKEND="x11",
                           GSK_RENDERER="cairo", GTK_A11Y="none", PYTHONDONTWRITEBYTECODE="1")
        process = subprocess.Popen(
            ["xvfb-run", "-a", "dbus-run-session", "--", *command],
            env=environment, start_new_session=True,
        )
        try:
            return process.wait(timeout=args.timeout)
        finally:
            # Xvfb and the private bus can outlive their launcher after a timeout.
            # Only signal this test's newly created process group.
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(process.pid, signal.SIGKILL)
                process.wait()


if __name__ == "__main__":
    raise SystemExit(main())
