"""Sanitized import templates and setup helpers use only project fixtures/temp files."""

import importlib.util
import json
import math
import os
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

PROJECT = Path(__file__).resolve().parents[2]


def load_script(name):
    spec = importlib.util.spec_from_file_location(name, PROJECT / "scripts" / (name + ".py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


preparer = load_script("prepare-scenes")
installer = load_script("install-linux")


def walk(value):
    if isinstance(value, dict):
        for key, item in value.items():
            yield key, item
            yield from walk(item)
    elif isinstance(value, list):
        for item in value:
            yield from walk(item)


class TemplateTests(unittest.TestCase):
    def collections(self):
        for platform in ("linux", "windows"):
            yield platform, json.loads((PROJECT / "Scenes" / f"studio-{platform}.json").read_text())

    def test_scene_schema_references_unique_sources_on_full_hd_canvas(self):
        catalog = json.loads((PROJECT / "Shared/scene-catalog.json").read_text())
        expected_scenes = {scene["name"] for scene in catalog["scenes"]}
        for platform, collection in self.collections():
            with self.subTest(platform=platform):
                self.assertEqual(collection["resolution"], {"x": 1920, "y": 1080})
                sources = collection["sources"]
                by_name = {source["name"]: source for source in sources}
                self.assertEqual(len(by_name), len(sources))
                self.assertEqual(len({source["uuid"] for source in sources}), len(sources))
                scenes = [source for source in sources if source["id"] == "scene"]
                self.assertEqual({scene["name"] for scene in scenes}, expected_scenes)
                for scene in scenes:
                    items = scene["settings"]["items"]
                    self.assertEqual(len({item["id"] for item in items}), len(items))
                    for item in items:
                        self.assertEqual(item["source_uuid"], by_name[item["name"]]["uuid"])
                        self.assertEqual(item["scale_ref"], {"x": 1920, "y": 1080})
                        for field in ("pos", "bounds", "scale"):
                            self.assertTrue(
                                all(math.isfinite(number) for number in item[field].values())
                            )

    def test_import_starts_with_privacy_global_audio_muted_and_devices_unassigned(self):
        for platform, collection in self.collections():
            with self.subTest(platform=platform):
                self.assertEqual(collection["current_scene"], "Privacy")
                self.assertEqual(collection["current_program_scene"], "Privacy")
                by_name = {source["name"]: source for source in collection["sources"]}
                visible = [
                    item["name"]
                    for item in by_name["Privacy"]["settings"]["items"]
                    if item["visible"]
                ]
                self.assertEqual(visible, ["Privacy artwork"])
                for field in ("DesktopAudioDevice1", "AuxAudioDevice1"):
                    self.assertTrue(collection[field]["muted"])
                    self.assertEqual(collection[field]["settings"], {"device_id": "default"})
                for name in ("Logitech Brio", "Sony"):
                    settings = by_name[name]["settings"]
                    self.assertIn(settings.get("video_device_id"), (None, ""))
                    self.assertNotIn("device_id", settings)
                screen = by_name["ASUS ProArt"]["settings"]
                self.assertEqual(screen.get("monitor_id", "DUMMY"), "DUMMY")
                self.assertNotIn("restore_token", screen)

    def test_templates_contain_only_bundled_paths_and_no_private_state(self):
        forbidden_keys = {"password", "server_password", "restore_token", "device_uuid"}
        for platform, collection in self.collections():
            with self.subTest(platform=platform):
                self.assertEqual(collection["saved_projectors"], [])
                self.assertEqual(collection["modules"], {})
                referenced = []
                for key, value in walk(collection):
                    self.assertNotIn(key.lower(), forbidden_keys)
                    if key in {"file", "local_file"}:
                        self.assertTrue(value.startswith(preparer.TOKEN))
                        referenced.append(value[len(preparer.TOKEN) :])
                    if isinstance(value, str):
                        for prefix in ("/home/", "/run/media/", "/tmp/", "C:\\Users\\"):
                            self.assertNotIn(prefix, value)
                    if key == "private_settings":
                        self.assertEqual(value, {})
                self.assertGreater(len(referenced), 10)
                self.assertTrue(all((PROJECT / "Assets" / name).is_file() for name in referenced))


class PreparationTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="studio-scene-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.assets = self.root / "assets"
        self.assets.mkdir()
        self.templates = self.root / "fixture"
        (self.templates / "Scenes").mkdir(parents=True)
        self.output = self.root / "output/import.json"
        self.original = {"sources": [{"settings": {"file": "${ASSET_ROOT}/card.png"}}]}
        self.template = self.templates / "Scenes/studio-linux.json"
        self.template.write_text(json.dumps(self.original))
        (self.assets / "card.png").write_bytes(b"synthetic asset")
        self.patch_root = patch.object(preparer, "PROJECT_ROOT", self.templates)
        self.patch_root.start()
        self.addCleanup(self.patch_root.stop)

    def test_prepared_asset_paths_resolve_and_template_is_unchanged(self):
        self.assertEqual(preparer.prepare("linux", self.assets, self.output), self.output)
        prepared = json.loads(self.output.read_text())
        self.assertEqual(prepared["sources"][0]["settings"]["file"], str(self.assets / "card.png"))
        self.assertEqual(json.loads(self.template.read_text()), self.original)

    def test_existing_output_is_never_clobbered(self):
        self.output.parent.mkdir()
        self.output.write_bytes(b"owner content")
        with self.assertRaises(FileExistsError):
            preparer.prepare("linux", self.assets, self.output)
        self.assertEqual(self.output.read_bytes(), b"owner content")

    def test_missing_asset_or_invalid_platform_does_not_create_output(self):
        with self.assertRaises(ValueError):
            preparer.prepare("../linux", self.assets, self.output)
        (self.assets / "card.png").unlink()
        with self.assertRaises(FileNotFoundError):
            preparer.prepare("linux", self.assets, self.output)
        self.assertFalse(self.output.exists())

    def test_traversal_invalid_placeholder_and_outside_symlink_are_rejected(self):
        outside = self.root / "outside.png"
        outside.write_bytes(b"not a bundled asset")
        (self.assets / "outside-link.png").symlink_to(outside)
        for value in (
            "${ASSET_ROOT}/../outside.png",
            "bad/${ASSET_ROOT}/card.png",
            "${ASSET_ROOT}/outside-link.png",
        ):
            with self.subTest(value=value):
                self.template.write_text(json.dumps({"file": value}))
                with self.assertRaises(ValueError):
                    preparer.prepare("linux", self.assets, self.output)
                self.assertFalse(self.output.exists())

    def test_failed_write_removes_only_its_new_output(self):
        with patch.object(preparer.os, "fsync", side_effect=OSError("synthetic disk failure")):
            with self.assertRaisesRegex(OSError, "synthetic disk failure"):
                preparer.prepare("linux", self.assets, self.output)
        self.assertFalse(self.output.exists())
        self.assertEqual(json.loads(self.template.read_text()), self.original)


class InstallerTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="studio-installer-test-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.fixture = self.root / "source"
        for directory in ("Linux", "Assets", "Scenes", "Windows", "Shared", "Linux/__pycache__"):
            (self.fixture / directory).mkdir(parents=True)
        (self.fixture / "Linux/controlli-brio.py").write_text(
            "raise RuntimeError('The installer must not execute this')"
        )
        (self.fixture / "Assets/VibeWare-logo.png").write_bytes(b"synthetic icon")
        (self.fixture / "Linux/__pycache__/cache.pyc").write_bytes(b"ignore")
        (self.fixture / "Windows/private-test.txt").write_text("exclude platform build")
        (self.fixture / "Shared/scene-catalog.json").write_text('{"scenes": []}')
        (self.fixture / "Shared/build-output.dll").write_bytes(b"exclude .NET build")
        self.data = self.root / "user data"
        self.patch_root = patch.object(installer, "PROJECT", self.fixture)
        self.patch_root.start()
        self.addCleanup(self.patch_root.stop)

    def test_versioned_install_and_launcher_use_only_requested_storage(self):
        with patch("subprocess.Popen", side_effect=AssertionError("No program may be launched")):
            destination, launcher = installer.install(self.data)
        self.assertEqual(
            destination, self.data / "VibeWare/StudioControls/versions" / installer.VERSION
        )
        self.assertTrue((destination / "Linux/controlli-brio.py").is_file())
        self.assertFalse((destination / "Windows").exists())
        self.assertEqual(
            [path.name for path in (destination / "Shared").iterdir()], ["scene-catalog.json"]
        )
        self.assertFalse((destination / "Linux/__pycache__").exists())
        self.assertIn(f'"{destination}/Linux/controlli-brio.py"', launcher.read_text())
        self.assertIn("GDK_BACKEND=x11", launcher.read_text())
        self.assertEqual(sorted(path.name for path in self.root.iterdir()), ["source", "user data"])

    def test_existing_version_and_launcher_are_preserved(self):
        destination, launcher = installer.install(self.data)
        (destination / "owner.txt").write_text("preserve installed edits")
        launcher.write_text("preserve launcher")
        with self.assertRaises(FileExistsError):
            installer.install(self.data)
        self.assertEqual((destination / "owner.txt").read_text(), "preserve installed edits")
        self.assertEqual(launcher.read_text(), "preserve launcher")

    def test_incomplete_copy_never_becomes_an_installed_version(self):
        with patch.object(
            installer.shutil, "copytree", side_effect=OSError("synthetic copy failure")
        ):
            with self.assertRaisesRegex(OSError, "synthetic copy failure"):
                installer.install(self.data)
        versions = self.data / "VibeWare/StudioControls/versions"
        self.assertEqual(list(versions.iterdir()), [])
        self.assertFalse((self.data / "applications").exists())

    def test_installing_inside_checkout_is_rejected_before_writes(self):
        with self.assertRaises(ValueError):
            installer.install(self.fixture / "data")
        self.assertFalse((self.fixture / "data").exists())

    def test_empty_or_relative_xdg_defaults_do_not_choose_checkout(self):
        for value in ("", "relative"):
            with (
                patch.dict(os.environ, {"XDG_DATA_HOME": value}),
                patch.object(Path, "home", return_value=self.root),
            ):
                self.assertEqual(installer.data_home_default(), self.root / ".local/share")

    def test_launcher_path_cannot_inject_a_second_desktop_entry_line(self):
        with self.assertRaises(ValueError):
            installer.install(self.root / "bad\nExec=unexpected")
        self.assertFalse((self.root / "bad\nExec=unexpected").exists())
        self.assertEqual(installer.desktop_argument("a%b"), '"a%%b"')


if __name__ == "__main__":
    unittest.main()
