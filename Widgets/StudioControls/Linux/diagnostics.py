"""Local, bounded diagnostics. Never log OBS credentials or request payloads."""

from __future__ import annotations

import atexit
import faulthandler
import json
import logging
from logging.handlers import RotatingFileHandler
import os
from pathlib import Path
import platform
import re
import sys
import threading
from typing import TextIO
import zipfile


LOGGER_NAME = "umberto_lab"
MAX_LOG_BYTES = 1024 * 1024
LOG_BACKUPS = 3
MAX_CRASH_BYTES = 1024 * 1024
_active: Diagnostics | None = None

_SECRET_PAIR = re.compile(
    r"(?i)([\"']?(?:password|server_password|passwd|token|access_token|"
    r"refresh_token|api[_-]?key|secret|authorization|authentication)[\"']?"
    r"\s*[:=]\s*)(\"[^\"]*\"|'[^']*'|[^\s,;}]+)"
)
_AUTH_HEADER = re.compile(r"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+")
_URL_CREDENTIALS = re.compile(r"(https?://)[^\s/@]+:[^\s/@]+@", re.IGNORECASE)
_URL_QUERY = re.compile(r"(https?://[^\s?#]+)\?[^\s]+", re.IGNORECASE)


def redact(text: str) -> str:
    """Remove common credential forms as a second line of defence.

    Callers must still log operation names rather than request/response bodies,
    titles, configuration dictionaries or arbitrary user content.
    """
    text = _URL_CREDENTIALS.sub(r"\1[redacted]@", text)
    text = _URL_QUERY.sub(r"\1?[redacted]", text)
    text = _AUTH_HEADER.sub(r"\1 [redacted]", text)
    return _SECRET_PAIR.sub(r"\1[redacted]", text)


class RedactingFormatter(logging.Formatter):
    def format(self, record: logging.LogRecord) -> str:
        return redact(super().format(record))


def get_logger(module_name: str) -> logging.Logger:
    return logging.getLogger(f"{LOGGER_NAME}.{module_name}")


def default_state_directory() -> Path:
    state_home = os.environ.get("XDG_STATE_HOME")
    base = (
        Path(state_home)
        if state_home and Path(state_home).is_absolute()
        else Path.home() / ".local" / "state"
    )
    return base / "umberto-lab"


class Diagnostics:
    """Keep the crash file alive for faulthandler and restore hooks on close."""

    def __init__(self, directory: Path):
        self.directory = directory
        self.log_file = directory / "panel.log"
        self.crash_file = directory / "crash.log"
        self.logger = logging.getLogger(LOGGER_NAME)
        self.handler: logging.Handler | None = None
        self.crash_stream: TextIO | None = None
        self.previous_sys_hook = sys.excepthook
        self.previous_thread_hook = threading.excepthook
        self.closed = False

    def record_uncaught(self, kind, error, traceback):
        if issubclass(kind, (KeyboardInterrupt, SystemExit)):
            self.previous_sys_hook(kind, error, traceback)
            return
        self.logger.critical("Unhandled application exception", exc_info=(kind, error, traceback))

    def record_thread_exception(self, args):
        if args.exc_type is SystemExit:
            return
        self.logger.critical(
            "Unhandled worker exception",
            exc_info=(args.exc_type, args.exc_value, args.exc_traceback),
        )

    def close(self):
        global _active
        if self.closed:
            return
        self.closed = True
        if sys.excepthook == self.record_uncaught:
            sys.excepthook = self.previous_sys_hook
        if threading.excepthook == self.record_thread_exception:
            threading.excepthook = self.previous_thread_hook
        if self.crash_stream:
            faulthandler.disable()
            self.crash_stream.close()
        if self.handler:
            self.logger.removeHandler(self.handler)
            self.handler.close()
        if _active is self:
            _active = None


def _open_crash_file(path: Path) -> TextIO:
    # A fatal crash normally writes once. On each launch retain just one previous
    # report, limited to its final MiB, rather than accumulating crash files.
    if path.exists() and path.stat().st_size:
        with path.open("rb") as previous:
            previous.seek(max(0, path.stat().st_size - MAX_CRASH_BYTES))
            tail = previous.read(MAX_CRASH_BYTES)
        backup = path.with_suffix(".log.1")
        backup.write_bytes(tail)
        backup.chmod(0o600)
    stream = path.open("w", encoding="utf-8")
    path.chmod(0o600)
    return stream


def configure_diagnostics(state_directory: Path | None = None) -> Diagnostics:
    """Start local diagnostics once. A log failure does not block the controls."""
    global _active
    if _active is not None:
        return _active
    diagnostics = Diagnostics(
        Path(state_directory) if state_directory else default_state_directory()
    )
    logger = diagnostics.logger
    logger.setLevel(logging.INFO)
    logger.propagate = False
    formatter = RedactingFormatter("%(asctime)s %(levelname)s %(name)s: %(message)s")
    try:
        diagnostics.directory.mkdir(parents=True, exist_ok=True, mode=0o700)
        handler = RotatingFileHandler(
            diagnostics.log_file,
            maxBytes=MAX_LOG_BYTES,
            backupCount=LOG_BACKUPS,
            encoding="utf-8",
        )
        diagnostics.log_file.chmod(0o600)
    except OSError:
        handler = logging.StreamHandler()
    handler.setFormatter(formatter)
    logger.addHandler(handler)
    diagnostics.handler = handler
    try:
        diagnostics.crash_stream = _open_crash_file(diagnostics.crash_file)
        faulthandler.enable(file=diagnostics.crash_stream, all_threads=True)
    except (OSError, RuntimeError):
        if diagnostics.crash_stream:
            diagnostics.crash_stream.close()
            diagnostics.crash_stream = None
        logger.warning("Native crash capture unavailable; application logging remains active")
    sys.excepthook = diagnostics.record_uncaught
    threading.excepthook = diagnostics.record_thread_exception
    _active = diagnostics
    logger.info("Control panel started; Python %s", platform.python_version())
    return diagnostics


def export_diagnostics(destination: Path, state_directory: Path | None = None) -> Path:
    """Export only our bounded, redacted logs; exclude OBS config and media.

    The destination must be chosen by the user. Existing files are never replaced.
    No upload or network access is performed.
    """
    directory = Path(state_directory) if state_directory else default_state_directory()
    destination = Path(destination)
    names = [
        "panel.log",
        *(f"panel.log.{i}" for i in range(1, LOG_BACKUPS + 1)),
        "crash.log",
        "crash.log.1",
    ]
    with zipfile.ZipFile(destination, "x", compression=zipfile.ZIP_DEFLATED) as archive:
        for name in names:
            source = directory / name
            if not source.is_file() or source.is_symlink():
                continue
            with source.open("rb") as stream:
                stream.seek(max(0, source.stat().st_size - MAX_LOG_BYTES))
                content = stream.read(MAX_LOG_BYTES).decode("utf-8", errors="replace")
            archive.writestr(name, redact(content))
        metadata = {
            "application": "Umberto's Lab Controls",
            "python": platform.python_version(),
            "system": platform.system(),
            "architecture": platform.machine(),
            "contains": "Application logs only; no OBS configuration, credentials or media",
        }
        archive.writestr("diagnostics.json", json.dumps(metadata, indent=2) + "\n")
    destination.chmod(0o600)
    return destination


def _close_at_exit():
    if _active:
        _active.close()


atexit.register(_close_at_exit)
