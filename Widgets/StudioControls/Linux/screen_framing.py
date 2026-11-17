"""Scene-local screen crops; normalized X/Y always stay inside the captured display."""

import math

SCREEN_SOURCE = "ASUS ProArt"
VIGNETTE_FILTER = "Vignette intensity"
VIGNETTES = {
    "screen": ("Screen vignette", SCREEN_SOURCE, "Screen"),
    "camera": ("Webcam vignette", "Logitech Brio", "Webcam"),
    "hardware": ("Hardware vignette", "Sony", "Hardware"),
}


def bounded(value, low, high):
    value = float(value)
    if not math.isfinite(value):
        raise ValueError("Framing values must be finite")
    return max(low, min(high, value))


def screen_state(transform):
    width, height = (round(transform.get(k, 0)) for k in ("sourceWidth", "sourceHeight"))
    if width <= 0 or height <= 0 or transform.get("boundsType") == "OBS_BOUNDS_NONE":
        return None
    left, right, top, bottom = (
        transform.get(k, 0) for k in ("cropLeft", "cropRight", "cropTop", "cropBottom")
    )
    visible_width, visible_height = width - left - right, height - top - bottom
    if visible_width <= 0 or visible_height <= 0:
        return None
    return {
        "zoom": min(width / visible_width, height / visible_height),
        "x": (2 * left / (left + right) - 1) * 100 if left + right else 0,
        "y": (2 * top / (top + bottom) - 1) * 100 if top + bottom else 0,
    }


def screen_crop(transform, zoom, x, y):
    if screen_state(transform) is None:
        raise ValueError("The captured display is not ready or has no fixed viewport")
    zoom, x, y = bounded(zoom, 1, 4), bounded(x, -100, 100), bounded(y, -100, 100)
    width, height = (round(transform[k]) for k in ("sourceWidth", "sourceHeight"))
    crop_x, crop_y = width - max(1, round(width / zoom)), height - max(1, round(height / zoom))
    left, top = round(crop_x * (x + 100) / 200), round(crop_y * (y + 100) / 200)
    # Only crop fields change: the scene's position, bounds and other sources stay fixed.
    return {
        "cropLeft": left,
        "cropRight": crop_x - left,
        "cropTop": top,
        "cropBottom": crop_y - top,
    }


def vignette_settings(enabled, strength):
    strength = round(bounded(strength, 0, 100))
    return {
        "opacity": strength / 100 if enabled else 0.0,
        "lab_enabled": bool(enabled),
        "lab_strength": strength,
    }
