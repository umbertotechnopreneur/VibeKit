import json
import os
import shutil
import tempfile
import threading
import unittest
from concurrent.futures import CancelledError
from pathlib import Path
from unittest.mock import patch

from PIL import Image, ImageDraw

import lab_titles
from lab_catalog import CARD_SOURCES


class TitleTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory(prefix="lab-titles-test-")
        self.addCleanup(self.directory.cleanup)
        self.root = Path(self.directory.name)
        self.config = self.root / "settings" / "titles.json"
        shutil.copyfile(lab_titles.ROOT / "OBS-logo.png", self.root / "OBS-logo.png")

    def snapshot(self):
        return {
            path.relative_to(self.root): path.read_bytes()
            for path in self.root.rglob("*")
            if path.is_file()
        }

    def test_malformed_settings_recover_without_overwriting_original(self):
        for content in (
            "[]",
            "null",
            '{"sound_volume": Infinity}',
            '{"name": null}',
            "broken JSON",
        ):
            with self.subTest(content=content):
                self.config.parent.mkdir(exist_ok=True)
                self.config.write_text(content)
                with self.assertLogs("umberto_lab.lab_titles", level="WARNING"):
                    restored = lab_titles.load_config(self.config)
                self.assertEqual(restored["name"], lab_titles.DEFAULTS["name"])
                self.assertEqual(self.config.read_text(), content)

    def test_project_name_limit_is_not_reused_as_chapter_eyebrow_limit(self):
        for length in (71, 100):
            with self.subTest(length=length):
                saved = lab_titles.prepare_cards(
                    dict(lab_titles.DEFAULTS, name="X" * length), self.root, self.config
                )
                self.assertEqual(saved["name"], "X" * length)
                self.assertEqual(json.loads(self.config.read_text())["name"], "X" * length)
                for _, filename in CARD_SOURCES:
                    with Image.open(self.root / filename) as image:
                        self.assertEqual(image.size, (2560, 1440))

    def test_long_word_starts_after_previous_word_without_joining_them(self):
        drawing = ImageDraw.Draw(Image.new("RGB", (100, 100)))
        word = "abcdefghijklmnopqrstuvwxyz"
        lines = lab_titles.wrapped(drawing, "Read " + word, lab_titles.font(20), 100)
        self.assertEqual(lines[0], "Read")
        self.assertEqual("".join(lines[1:]), word)

    def test_render_failure_preserves_existing_logo_cards_and_settings(self):
        Image.new("RGB", (20, 20), "blue").save(self.root / "Project-logo.png")
        Image.new("RGB", (20, 20), "red").save(self.root / "new-logo.png")
        lab_titles.prepare_cards(lab_titles.DEFAULTS, self.root, self.config)
        before = self.snapshot()
        values = dict(
            lab_titles.DEFAULTS,
            logo=str(self.root / "new-logo.png"),
            description="\n".join(["x"] * 100),
        )
        with self.assertRaisesRegex(ValueError, "does not fit"):
            lab_titles.prepare_cards(values, self.root, self.config)
        self.assertEqual(self.snapshot(), before)

    def test_failed_final_config_commit_restores_all_assets(self):
        lab_titles.prepare_cards(lab_titles.DEFAULTS, self.root, self.config)
        before = self.snapshot()
        real_replace = os.replace

        def fail_config_once(source, target):
            if Path(target) == self.config:
                raise OSError("Simulated unavailable configuration volume")
            return real_replace(source, target)

        with patch("lab_titles.os.replace", side_effect=fail_config_once):
            with self.assertRaisesRegex(OSError, "configuration volume"):
                lab_titles.prepare_cards(
                    dict(lab_titles.DEFAULTS, name="A new project"), self.root, self.config
                )
        self.assertEqual(self.snapshot(), before)

    def test_cancelled_preparation_preserves_files(self):
        cancelled = threading.Event()
        cancelled.set()
        before = self.snapshot()
        with self.assertRaises(CancelledError):
            lab_titles.prepare_cards(lab_titles.DEFAULTS, self.root, self.config, cancelled)
        self.assertEqual(self.snapshot(), before)

    def test_cancel_after_first_promotion_restores_prior_files(self):
        lab_titles.prepare_cards(lab_titles.DEFAULTS, self.root, self.config)
        before = self.snapshot()
        cancelled = threading.Event()
        real_replace = os.replace

        def cancel_after_replace(source, target):
            result = real_replace(source, target)
            cancelled.set()
            return result

        with patch("lab_titles.os.replace", side_effect=cancel_after_replace):
            with self.assertRaises(CancelledError):
                lab_titles.prepare_cards(
                    dict(lab_titles.DEFAULTS, name="Cancelled project"),
                    self.root,
                    self.config,
                    cancelled,
                )
        self.assertEqual(self.snapshot(), before)


if __name__ == "__main__":
    unittest.main()
