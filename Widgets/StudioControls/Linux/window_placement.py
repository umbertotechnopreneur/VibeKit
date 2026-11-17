"""Optional X11 placement; Wayland window positioning stays with the compositor."""

import ctypes as C
import re
import subprocess
from gi.repository import GdkX11


class ClientMessage(C.Structure):
    _fields_ = [
        ("type", C.c_int),
        ("serial", C.c_ulong),
        ("send_event", C.c_int),
        ("display", C.c_void_p),
        ("window", C.c_ulong),
        ("message_type", C.c_ulong),
        ("format", C.c_int),
        ("data", C.c_long * 5),
    ]


class XEvent(C.Union):
    _fields_ = [("message", ClientMessage), ("pad", C.c_long * 24)]


def move_to_side_monitor(window):
    surface = window.get_surface()
    if not isinstance(surface, GdkX11.X11Surface):
        return False
    output = subprocess.check_output(["xrandr", "--listmonitors"], text=True, timeout=2)
    secondary = next((line for line in output.splitlines()[1:] if "*" not in line), "")
    geometry = re.search(r"\d+/\d+x\d+/\d+([+-]\d+)([+-]\d+)", secondary)
    if geometry is None:
        return False
    x, y = (int(v) for v in geometry.groups())
    x11 = C.CDLL("libX11.so.6")
    x11.XOpenDisplay.argtypes = [C.c_char_p]
    x11.XOpenDisplay.restype = C.c_void_p
    x11.XDefaultRootWindow.argtypes = [C.c_void_p]
    x11.XDefaultRootWindow.restype = C.c_ulong
    x11.XInternAtom.argtypes = [C.c_void_p, C.c_char_p, C.c_int]
    x11.XInternAtom.restype = C.c_ulong
    x11.XSendEvent.argtypes = [C.c_void_p, C.c_ulong, C.c_int, C.c_long, C.POINTER(XEvent)]
    x11.XCloseDisplay.argtypes = [C.c_void_p]
    display = x11.XOpenDisplay(None)
    if not display:
        return False
    try:
        event = XEvent()
        event.message.type = 33
        event.message.display = display
        event.message.window = surface.get_xid()
        event.message.message_type = x11.XInternAtom(display, b"_NET_MOVERESIZE_WINDOW", 0)
        event.message.format = 32
        event.message.data[:] = (1 | (1 << 8) | (1 << 9) | (1 << 12), x + 64, y + 96, 0, 0)
        return bool(
            x11.XSendEvent(
                display, x11.XDefaultRootWindow(display), 0, (1 << 19) | (1 << 20), C.byref(event)
            )
        )
    finally:
        x11.XCloseDisplay(display)
