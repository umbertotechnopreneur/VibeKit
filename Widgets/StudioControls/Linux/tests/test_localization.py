"""Synthetic catalog and preference regressions, without GTK or hardware access."""

import json
import tempfile
import unittest
from pathlib import Path

from localization import CATALOG, CODES, Translations, load_language, save_language


class LocalizationTests(unittest.TestCase):
    def test_shared_catalog_has_all_eight_languages_and_complete_messages(self):
        data = json.loads(CATALOG.read_text(encoding="utf-8"))
        self.assertEqual(data["languages"], list(CODES))
        self.assertGreater(len(data["strings"]), 200)
        for source, values in data["strings"].items():
            self.assertEqual(len(values), 8, source)
            self.assertTrue(all(isinstance(value, str) and value for value in values), source)
            self.assertEqual(values[1], source)

    def test_status_localizes_but_paths_and_identifiers_remain_data(self):
        catalog = Translations()
        for code in CODES:
            path = "/synthetic/Screen · Software Demo/Connect\nScreen.mkv"
            message = catalog.text("Video saved: " + path, code)
            self.assertTrue(message.endswith(path), message)
            self.assertEqual(catalog.text(path, code), path)
            self.assertEqual(catalog.text("unrecognized detail", code), "unrecognized detail")
            self.assertEqual(
                catalog.text("Screen zoom · 2.5×", code),
                catalog.text("Screen zoom", code) + " · 2.5×",
            )
        self.assertEqual(catalog.text("Connect", "it"), "Connetti")
        self.assertEqual(catalog.text("Connect", "ja"), "接続")
        self.assertEqual(catalog.text("Connect", "unknown"), "Connect")

    def test_preferences_roundtrip_unknown_values_preserved_and_rejected_saves(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "language.json"
            self.assertEqual(load_language(path), "en")
            for code in CODES:
                save_language(code, path)
                self.assertEqual(load_language(path), code)
            original = '{"language":"unsupported"}'
            path.write_text(original)
            with self.assertLogs("umberto_lab.localization", level="WARNING"):
                self.assertEqual(load_language(path), "en")
            self.assertEqual(path.read_text(), original)
            with self.assertRaises(ValueError):
                save_language("unsupported", path)
            self.assertEqual(path.read_text(), original)

    def test_bad_or_oversized_catalog_falls_back_without_replacing_file(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / "catalog.json"
            for content in ['{"strings": null}', "[]", "null", "x" * (1024 * 1024 + 1)]:
                path.write_text(content)
                with self.assertLogs("umberto_lab.localization", level="WARNING"):
                    catalog = Translations(path)
                self.assertEqual(catalog.text("Connect", "ja"), "Connect")
                self.assertEqual(path.read_text(), content)
