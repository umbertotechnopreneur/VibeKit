# SPDX-License-Identifier: MIT
from pathlib import Path
import re
import time

DEVICE = re.compile(r"(?:sd[a-z]+|hd[a-z]+|vd[a-z]+|xvd[a-z]+|nvme\d+n\d+|mmcblk\d+|dasd[a-z]+|pmem\d+)")


class DiskActivity:
    def __init__(self):
        self.previous = {}
        self.at = time.monotonic()

    def sample(self):
        now = time.monotonic()
        current = {}
        try:
            for line in Path("/proc/diskstats").read_text().splitlines():
                fields = line.split()
                if len(fields) >= 8 and DEVICE.fullmatch(fields[2]):
                    current[tuple(fields[:3])] = int(fields[3]) + int(fields[7])
        except (OSError, ValueError):
            self.previous.clear()
            self.at = now
            return 0
        seconds = now - self.at
        operations = sum(max(0, count - self.previous[key]) for key, count in current.items() if key in self.previous)
        self.previous, self.at = current, now
        return operations / seconds if .01 <= seconds <= 2 else 0
