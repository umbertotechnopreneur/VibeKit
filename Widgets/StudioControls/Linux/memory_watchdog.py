# SPDX-License-Identifier: MIT
"""Protect the panel process without invoking a camera or OBS operation."""

import json
import os
from pathlib import Path
import threading
import time


def process_tree():
    """Return current RSS for the panel and living descendants from procfs."""
    page_size = os.sysconf("SC_PAGE_SIZE")
    children = {}
    root = None
    for directory in Path("/proc").iterdir():
        if not directory.name.isdecimal():
            continue
        try:
            stat = (directory / "stat").read_text(errors="replace")
            name_end = stat.rindex(")")
            fields = stat[name_end + 2:].split()
            if fields[0] == "Z":
                continue
            pid, parent = int(directory.name), int(fields[1])
            process = {"pid": pid, "parentPid": parent, "name": stat[stat.index("(") + 1:name_end],
                       "rssBytes": max(0, int(fields[21])) * page_size}
        except (OSError, ValueError, IndexError):
            continue
        if pid == os.getpid():
            root = process
        else:
            children.setdefault(parent, []).append(process)
    if root is None:
        return []
    result = [root]
    seen = {root["pid"]}
    for process in result:
        for child in children.get(process["pid"], ()):
            if child["pid"] not in seen:
                seen.add(child["pid"])
                result.append(child)
    return result


def start_watchdog(log, request_quit, limit_bytes=1_000_000_000):
    """Sample off the GTK thread and return an event that cancels pending shutdown."""
    stopped = threading.Event()

    def watch():
        previous_over = False
        while not stopped.wait(2):
            try:
                processes = process_tree()
            except OSError:
                previous_over = False
                continue
            total = sum(process["rssBytes"] for process in processes)
            over = total > limit_bytes
            if over and previous_over and not stopped.is_set():
                event = {"atUtc": time.time(), "kind": "guard.memory-limit", "bytes": total,
                         "limitBytes": limit_bytes, "processes": processes}
                try:
                    log.warning("%s", json.dumps(event))
                finally:
                    try:
                        request_quit()
                    finally:
                        if not stopped.wait(5):
                            os._exit(137)
                return
            previous_over = over

    threading.Thread(target=watch, name="Studio Controls RAM watchdog", daemon=True).start()
    return stopped
