"""Portable asset resolution uses isolated storage and never overwrites artwork."""

from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import lab_titles
import obs_remote
import studio_paths
import diagnostics


class PathsTests(unittest.TestCase):
    def test_missing_defaults_copied_and_custom_artwork_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "bundle"
            source.mkdir()
            (source / "Intro-card.png").write_bytes(b"default")
            data = Path(directory) / "data"
            with (
                patch.object(studio_paths, "bundled_assets", return_value=source),
                patch.dict(studio_paths.os.environ, {"XDG_DATA_HOME": str(data)}),
            ):
                target = studio_paths.working_assets()
                self.assertEqual((target / "Intro-card.png").read_bytes(), b"default")
                (target / "Intro-card.png").write_bytes(b"custom")
                studio_paths.working_assets()
                self.assertEqual((target / "Intro-card.png").read_bytes(), b"custom")
                self.assertEqual((source / "Intro-card.png").read_bytes(), b"default")

    def test_empty_or_relative_xdg_path_uses_home_not_current_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            home = Path(directory)
            for configured in ("", "relative/path"):
                with (
                    self.subTest(configured=configured),
                    patch.dict(studio_paths.os.environ, {"XDG_DATA_HOME": configured}),
                    patch.object(studio_paths.Path, "home", return_value=home),
                ):
                    self.assertEqual(studio_paths.user_data_home(), home / ".local/share")

    def test_flat_legacy_installation_preserves_its_existing_asset_location(self):
        with tempfile.TemporaryDirectory() as directory:
            legacy = Path(directory)
            (legacy / "Intro-card.png").write_bytes(b"existing owner artwork")
            with (
                patch.object(studio_paths, "__file__", str(legacy / "studio_paths.py")),
                patch.object(studio_paths, "user_data_home") as data_home,
            ):
                self.assertEqual(studio_paths.working_assets(), legacy)
                data_home.assert_not_called()

    def test_relative_log_destination_cannot_write_into_checkout(self):
        with tempfile.TemporaryDirectory() as directory:
            with (
                patch.dict(diagnostics.os.environ, {"XDG_STATE_HOME": "relative"}),
                patch.object(diagnostics.Path, "home", return_value=Path(directory)),
            ):
                self.assertEqual(
                    diagnostics.default_state_directory(),
                    Path(directory) / ".local/state/umberto-lab",
                )

    def test_packaged_assets_resolve_to_sibling_assets_directory(self):
        with tempfile.TemporaryDirectory() as directory:
            package = Path(directory)
            (package / "Assets").mkdir()
            (package / "Linux").mkdir()
            with patch.object(studio_paths, "__file__", str(package / "Linux/studio_paths.py")):
                self.assertEqual(studio_paths.bundled_assets(), package / "Assets")

    def test_incomplete_default_copy_is_removed_and_existing_files_preserved(self):
        with tempfile.TemporaryDirectory() as directory:
            source = Path(directory) / "bundle"
            source.mkdir()
            (source / "Intro-card.png").write_bytes(b"default")
            with (
                patch.object(studio_paths, "bundled_assets", return_value=source),
                patch.object(studio_paths, "user_data_home", return_value=Path(directory) / "data"),
                patch.object(
                    studio_paths.shutil, "copyfileobj", side_effect=OSError("synthetic failure")
                ),
            ):
                with self.assertRaisesRegex(OSError, "synthetic failure"):
                    studio_paths.working_assets()
                target = Path(directory) / "data/VibeWare/StudioControls/working-assets"
                self.assertFalse((target / "Intro-card.png").exists())

    def test_obs_read_only_construction_does_not_create_working_assets(self):
        with patch.object(obs_remote, "working_assets") as working:
            controller = obs_remote.OBS(config_path="unused-config.json")
            working.assert_not_called()
            working.return_value = Path("synthetic-working-assets")
            self.assertEqual(controller.assets_root, Path("synthetic-working-assets"))
            self.assertEqual(controller.assets_root, Path("synthetic-working-assets"))
            working.assert_called_once_with()

    def test_default_card_preparation_uses_working_assets_not_checkout(self):
        with tempfile.TemporaryDirectory() as directory:
            working = Path(directory) / "working"
            config = Path(directory) / "project.json"
            with (
                patch.object(lab_titles, "working_assets", return_value=working),
                patch.object(lab_titles, "make_cards", return_value=[]) as render,
                patch.object(lab_titles, "extra_cards", return_value={}),
                patch.object(lab_titles, "_commit_files") as commit,
            ):
                lab_titles.prepare_cards(lab_titles.DEFAULTS, config_path=config)
                self.assertEqual(render.call_args.args[1], working)
                self.assertEqual(set(commit.call_args.args[0]), {config})
