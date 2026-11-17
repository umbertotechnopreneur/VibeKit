"""Small shared UI catalog; never translate project data or OBS source identifiers."""

import json
import os
from pathlib import Path

from lab_titles import atomic_bytes
from diagnostics import get_logger

LOGGER = get_logger(__name__)
CATALOG = Path(__file__).resolve().parents[1] / "Shared/ui-translations.json"
LANGUAGES = (
    ("it", "Italiano"),
    ("en", "English"),
    ("vi", "Tiếng Việt"),
    ("fr", "Français"),
    ("de", "Deutsch"),
    ("ko", "한국어"),
    ("zh", "中文"),
    ("ja", "日本語"),
)
CODES = tuple(code for code, _ in LANGUAGES)


def preference_path():
    configured = os.environ.get("XDG_CONFIG_HOME", "")
    base = (
        Path(configured)
        if configured and Path(configured).is_absolute()
        else Path.home() / ".config"
    )
    return base / "umbertogiacobbi/studio-language.json"


def load_language(path=None):
    try:
        source = Path(path or preference_path())
        if source.stat().st_size > 4096:
            raise ValueError("Language preference is too large")
        code = json.loads(source.read_text(encoding="utf-8"))["language"]
        if code not in CODES:
            raise ValueError("Unsupported panel language")
        return code
    except FileNotFoundError:
        return "en"
    except (OSError, ValueError, KeyError, TypeError):
        LOGGER.warning("Could not read panel language; using English")
        return "en"


def save_language(code, path=None):
    if code not in CODES:
        raise ValueError("Unsupported panel language")
    atomic_bytes(Path(path or preference_path()), (json.dumps({"language": code}) + "\n").encode())


class Translations:
    def __init__(self, path=CATALOG):
        try:
            path = Path(path)
            if path.stat().st_size > 1024 * 1024:
                raise ValueError("UI catalog is too large")
            data = json.loads(path.read_text(encoding="utf-8"))
            if not isinstance(data, dict) or data.get("languages") != list(CODES):
                raise ValueError("UI catalog language order is invalid")
            self.strings = data["strings"]
            if not isinstance(self.strings, dict) or any(
                not isinstance(key, str)
                or not isinstance(values, list)
                or len(values) != len(CODES)
                or any(not isinstance(value, str) or not value for value in values)
                for key, values in self.strings.items()
            ):
                raise ValueError("Invalid UI catalog")
        except (OSError, ValueError, KeyError, TypeError):
            LOGGER.warning("UI catalog unavailable; using English")
            self.strings = {}
        self.prefixes = sorted(
            (key for key in self.strings if key.endswith((":", ": ", "…", "."))),
            key=len,
            reverse=True,
        )

    def text(self, source, language):
        if language == "en" or language not in CODES or not source:
            return source
        index = CODES.index(language)
        if source in self.strings:
            return self.strings[source][index]
        trimmed = source.strip()
        if trimmed in self.strings:
            start = len(source) - len(source.lstrip())
            return source[:start] + self.strings[trimmed][index] + source[len(source.rstrip()) :]
        # Protect the complete variable remainder, including newlines and paths.
        for prefix in self.prefixes:
            if source.startswith(prefix):
                return self.strings[prefix][index] + source[len(prefix) :]
        if "\n" in source:
            return "\n".join(self.text(line, language) for line in source.split("\n"))
        if " · " in source:
            return " · ".join(self.text(part, language) for part in source.split(" · "))
        head, separator, number = source.rpartition(" ")
        if (
            separator
            and number
            and any(char.isdecimal() for char in number)
            and all(char in "0123456789:.,+-×%" for char in number)
        ):
            return self.text(head, language) + separator + number
        return source
