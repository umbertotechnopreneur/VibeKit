"""Validated screen presets shared by the GTK panel and offline tests."""

import json
from pathlib import Path
from diagnostics import get_logger
from screen_framing import bounded

logger = get_logger(__name__)
SCREEN_PRESET_PATH = Path.home() / ".config/umbertogiacobbi/screen-presets.json"
DEFAULT_SCREEN_PRESETS = {"Overview": [1, 0, 0], "Detail A": [2, -50, 0], "Detail B": [2, 50, 0]}


def load_screen_presets(path=SCREEN_PRESET_PATH):
    presets = {name: list(values) for name, values in DEFAULT_SCREEN_PRESETS.items()}
    try:
        saved = json.loads(Path(path).read_text())
        if not isinstance(saved, dict):
            raise ValueError("Screen presets must be an object")
        for name in presets:
            values = saved.get(name)
            if values is None:
                continue
            if (
                not isinstance(values, list)
                or len(values) != 3
                or any(type(value) not in (int, float) for value in values)
            ):
                raise ValueError("Invalid screen position")
            presets[name] = [
                bounded(value, low, high)
                for value, low, high in zip(values, (1, -100, -100), (4, 100, 100), strict=False)
            ]
    except FileNotFoundError:
        pass
    except (OSError, ValueError, TypeError, OverflowError):
        logger.warning("Invalid screen presets; using defaults", exc_info=True)
        return {name: list(values) for name, values in DEFAULT_SCREEN_PRESETS.items()}
    return presets
