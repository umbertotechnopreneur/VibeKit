"""Appearance configuration and readable light/dark palettes without a display."""

import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

from panel_theme import (
    DEFAULT_THEME,
    PALETTES,
    SCENE_COLORS,
    THEMES,
    load_theme,
    resolve_theme,
    save_theme,
    theme_css,
)


def luminance(hex_color):
    channels = [int(hex_color[index : index + 2], 16) / 255 for index in (1, 3, 5)]
    linear = [
        value / 12.92 if value <= 0.04045 else ((value + 0.055) / 1.055) ** 2.4
        for value in channels
    ]
    return sum(
        value * weight for value, weight in zip(linear, (0.2126, 0.7152, 0.0722), strict=True)
    )


def contrast(first, second):
    values = sorted((luminance(first), luminance(second)))
    return (values[1] + 0.05) / (values[0] + 0.05)


class AppearanceTests(unittest.TestCase):
    def test_missing_and_invalid_preferences_start_light(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "panel-settings.json"
            self.assertEqual(load_theme(path), DEFAULT_THEME)
            for value in ([], {"theme": "unknown"}, {"theme": None}, {"theme": ["dark"]}):
                path.write_text(json.dumps(value))
                with self.assertLogs("umberto_lab.panel_theme", level="WARNING"):
                    self.assertEqual(load_theme(path), "light")
            path.write_text("{malformed")
            with self.assertLogs("umberto_lab.panel_theme", level="WARNING"):
                self.assertEqual(load_theme(path), "light")

    def test_each_choice_survives_relaunch(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "settings" / "panel-settings.json"
            for theme in THEMES:
                save_theme(theme, path)
                self.assertEqual(load_theme(path), theme)

    def test_invalid_choice_does_not_replace_saved_preference(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "panel-settings.json"
            save_theme("dark", path)
            with self.assertRaises(ValueError):
                save_theme("sepia", path)
            self.assertEqual(load_theme(path), "dark")

    def test_save_failure_is_not_silently_reported_as_persistent(self):
        with patch("panel_theme.atomic_bytes", side_effect=OSError("Read-only filesystem")):
            with self.assertRaises(OSError):
                save_theme("light", Path("/unused"))

    def test_system_follows_scheme_without_overriding_manual_choices(self):
        self.assertEqual(resolve_theme("system", "prefer-dark"), "dark")
        self.assertEqual(resolve_theme("system", "prefer-light", fallback_dark=True), "light")
        self.assertEqual(resolve_theme("system", "default", fallback_dark=True), "dark")
        self.assertEqual(resolve_theme("system", "default", fallback_dark=False), "light")
        self.assertEqual(resolve_theme("light", "prefer-dark"), "light")
        self.assertEqual(resolve_theme("dark", "prefer-light"), "dark")

    def test_palette_text_and_disabled_controls_remain_readable(self):
        for name, palette in PALETTES.items():
            with self.subTest(theme=name):
                self.assertGreaterEqual(contrast(palette["foreground"], palette["background"]), 4.5)
                self.assertGreaterEqual(contrast(palette["muted"], palette["background"]), 4.5)
                self.assertGreaterEqual(
                    contrast(palette["disabled_foreground"], palette["disabled_background"]), 4.5
                )
        self.assertGreaterEqual(contrast("#29201c", "#eba295"), 4.5)

    def test_css_covers_header_forms_tabs_and_popovers(self):
        for theme in PALETTES:
            stylesheet = theme_css(theme)
            for selector in (
                "headerbar",
                "button:disabled",
                "entry",
                "textview text",
                "notebook",
                "dropdown",
                "scale",
            ):
                self.assertIn(selector, stylesheet)
            self.assertIn(PALETTES[theme]["background"], stylesheet)

    def test_scene_icons_have_distinct_accessible_colours_in_each_state(self):
        for index, theme in ((1, "light"), (2, "dark")):
            colours = [entry[index] for entry in SCENE_COLORS.values()]
            self.assertEqual(len(colours), len(set(colours)))
            for colour in colours:
                for surface in ("surface", "hover"):
                    self.assertGreaterEqual(contrast(colour, PALETTES[theme][surface]), 3)
        for _, selected_colour, _ in SCENE_COLORS.values():
            self.assertGreaterEqual(contrast(selected_colour, "#eba295"), 3)


if __name__ == "__main__":
    unittest.main()
