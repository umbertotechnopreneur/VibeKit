"""V4L2 camera controls and local presets; no video feed is opened."""

import fcntl
import json
import os
import struct
from pathlib import Path
from lab_titles import atomic_bytes
from diagnostics import get_logger

logger = get_logger(__name__)

GET_CONTROL = 0xC008561B
SET_CONTROL = 0xC008561C
QUERY_CONTROL = 0xC0445624
CONTROLS = (
    ("Zoom", 0x009A090D, 100, "×", 2),
    ("Pan", 0x009A0908, 3600, "°", 0),
    ("Tilt", 0x009A0909, 3600, "°", 0),
)
CAMERA_PATTERN = "usb-046d_Logitech_BRIO_*-video-index0"


def camera_fd():
    path = next(Path("/dev/v4l/by-id").glob(CAMERA_PATTERN), None)
    if path is None:
        raise FileNotFoundError("Logitech Brio is not connected")
    return os.open(path, os.O_RDWR | os.O_NONBLOCK)


def control(fd, command, control_id, value=0):
    data = bytearray(struct.pack("Ii", control_id, value))
    fcntl.ioctl(fd, command, data, True)
    return struct.unpack("Ii", data)[1]


def query(fd, control_id):
    data = bytearray(68)
    struct.pack_into("I", data, 0, control_id)
    fcntl.ioctl(fd, QUERY_CONTROL, data, True)
    return struct.unpack_from("iiii", data, 40)


PRESET_PATH = Path.home() / ".config/umbertogiacobbi/brio-presets.json"
PRESETS = ("Wide", "Medium", "Close-up")


def load_presets(path=PRESET_PATH):
    defaults = {
        name: {str(c[1]): (zoom if i == 0 else 0) for i, c in enumerate(CONTROLS)}
        for name, zoom in zip(PRESETS, (100, 140, 180), strict=False)
    }
    try:
        saved = json.loads(path.read_text())
        for name in PRESETS:
            values = saved.get(name)
            if isinstance(values, dict) and all(
                type(values.get(str(c[1]))) is int for c in CONTROLS
            ):
                defaults[name] = {str(c[1]): values[str(c[1])] for c in CONTROLS}
    except FileNotFoundError:
        pass
    except (OSError, ValueError, AttributeError):
        logger.warning("Invalid camera presets; using defaults", exc_info=True)
    return defaults


def save_presets(data, path=PRESET_PATH):
    atomic_bytes(path, (json.dumps(data, indent=2) + "\n").encode())


def set_values(values):
    fd = camera_fd()
    try:
        before = {str(c[1]): control(fd, GET_CONTROL, c[1]) for c in CONTROLS}
        try:
            for _, cid, *_ in CONTROLS:
                minimum, maximum, step, _ = query(fd, cid)
                value = minimum + round((values[str(cid)] - minimum) / step) * step
                control(fd, SET_CONTROL, cid, max(minimum, min(maximum, value)))
        except OSError:
            for key, value in before.items():
                try:
                    control(fd, SET_CONTROL, int(key), value)
                except OSError:
                    logger.exception("Camera preset rollback failed")
            raise
        return {str(c[1]): control(fd, GET_CONTROL, c[1]) for c in CONTROLS}
    finally:
        os.close(fd)


def snapshot():
    """Read ranges and values using one short-lived device descriptor."""
    descriptor = camera_fd()
    try:
        return {
            identifier: (
                *query(descriptor, identifier)[:3],
                control(descriptor, GET_CONTROL, identifier),
            )
            for _, identifier, *_ in CONTROLS
        }
    finally:
        os.close(descriptor)


def set_one(identifier, value):
    descriptor = camera_fd()
    try:
        lower, upper, step, _ = query(descriptor, identifier)
        stepped = lower + round((value - lower) / step) * step
        control(descriptor, SET_CONTROL, identifier, max(lower, min(upper, stepped)))
    finally:
        os.close(descriptor)
