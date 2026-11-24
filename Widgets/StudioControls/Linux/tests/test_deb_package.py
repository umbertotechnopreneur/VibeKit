"""Build/extract package fixtures; never install a package or run the application."""

import hashlib
import importlib.util
import io
import json
import os
import shutil
import subprocess
import sys
import tarfile
import tempfile
import unittest
from pathlib import Path

PROJECT = Path(__file__).resolve().parents[2]
SCRIPT = PROJECT / "scripts/build-linux-deb.py"
SPEC = importlib.util.spec_from_file_location("build_linux_deb", SCRIPT)
builder = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(builder)


class DebStagingTests(unittest.TestCase):
    def test_invalid_version_or_control_metadata_rejected_before_writes(self):
        with tempfile.TemporaryDirectory(prefix="studio-deb-test-") as directory:
            output = Path(directory) / "invalid"
            for values in (
                {"version": "../../escape"},
                {"epoch": -1},
                {"maintainer": "Name\nPostinst: bad"},
            ):
                with self.subTest(values=values), self.assertRaises(ValueError):
                    builder.stage_package(output, **values)
                self.assertFalse(output.exists())

    def test_existing_stage_is_not_modified(self):
        with tempfile.TemporaryDirectory(prefix="studio-deb-test-") as directory:
            destination = Path(directory)
            marker = destination / "owner.txt"
            marker.write_text("keep")
            with self.assertRaises(FileExistsError):
                builder.stage_package(destination)
            self.assertEqual(marker.read_text(), "keep")

    def test_project_input_symlinks_are_rejected(self):
        with tempfile.TemporaryDirectory(prefix="studio-deb-test-") as directory:
            root = Path(directory)
            source = root / "source.txt"
            source.write_text("not a package input")
            link = root / "link.txt"
            link.symlink_to(source)
            with self.assertRaises(ValueError):
                builder._copy(link, root / "copy.txt")
            self.assertFalse((root / "copy.txt").exists())


@unittest.skipUnless(
    shutil.which("dpkg-deb"), "dpkg-deb is needed for package build/extraction checks"
)
class DebArchiveTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.temporary = tempfile.TemporaryDirectory(prefix="studio-deb-archive-test-")
        cls.addClassCleanup(cls.temporary.cleanup)
        cls.root = Path(cls.temporary.name)
        cls.archive = builder.build(
            output_dir=cls.root / "first", version="0.3.0", epoch=1_794_873_600
        )
        cls.extracted = cls.root / "extracted"
        subprocess.run(
            ["dpkg-deb", "--raw-extract", str(cls.archive), str(cls.extracted)],
            check=True,
            timeout=30,
        )

    def test_metadata_dependencies_ownership_and_no_maintainer_actions(self):
        fields = subprocess.check_output(
            ["dpkg-deb", "--field", str(self.archive)], text=True, timeout=10
        )
        self.assertIn("Architecture: all", fields)
        self.assertIn("Version: 0.3.0", fields)
        for dependency in builder.DEPENDENCIES:
            self.assertIn(dependency, fields)
        self.assertEqual(
            sorted(path.name for path in (self.extracted / "DEBIAN").iterdir()),
            ["control", "md5sums"],
        )
        payload = subprocess.check_output(
            ["dpkg-deb", "--fsys-tarfile", str(self.archive)], timeout=15
        )
        with tarfile.open(fileobj=io.BytesIO(payload)) as archive:
            for member in archive.getmembers():
                self.assertEqual((member.uid, member.gid), (0, 0))
                self.assertTrue(member.isdir() or member.isfile())
                self.assertNotIn("..", Path(member.name).parts)
                self.assertFalse(Path(member.name).is_absolute())
                self.assertEqual(member.mode & 0o7000, 0)
                self.assertTrue(
                    member.name in (".", "./", "./usr") or member.name.startswith("./usr/")
                )

    def test_runtime_manifest_excludes_private_state_tests_and_build_outputs(self):
        application = self.extracted / builder.PREFIX
        for path in application.rglob("*"):
            relative = path.relative_to(application)
            self.assertFalse(
                {"__pycache__", "tests", ".git", "obj", "bin", "Windows"}.intersection(
                    relative.parts
                )
            )
            self.assertNotIn(
                path.name, {"project.json", "Project-logo.png", "panel.log", "crash.log"}
            )
            if path.suffix in {".py", ".json", ".txt", ".md"}:
                text = path.read_text()
                self.assertNotIn("/home/umberto/", text)
                self.assertNotIn("/run/media/umberto/", text)
        self.assertEqual(
            sorted(path.relative_to(application / "Assets").as_posix()
                   for path in (application / "Assets").rglob("*") if path.is_file()),
            sorted(builder.ASSET_NAMES),
        )
        self.assertTrue(
            (
                application / "Linux/_vendor/websocket_client-1.9.2.dist-info/licenses/LICENSE"
            ).is_file()
        )
        self.assertTrue((self.extracted / "usr/share/doc/studio-controls/copyright").is_file())
        self.assertTrue(
            (self.extracted / "usr/share/pixmaps/biz.vibeware.StudioControls.png").is_file()
        )

    def test_launchers_disable_bytecode_and_reference_only_installed_prefix(self):
        for name in ("studio-controls", "studio-controls-prepare-scenes"):
            launcher = self.extracted / "usr/bin" / name
            text = launcher.read_text()
            self.assertIn("/usr/bin/python3 -B /usr/lib/studio-controls/", text)
            self.assertNotIn(str(PROJECT), text)
            self.assertEqual(launcher.stat().st_mode & 0o777, 0o755)
            subprocess.run(["sh", "-n", str(launcher)], check=True, timeout=5)
        if shutil.which("desktop-file-validate"):
            desktop = self.extracted / "usr/share/applications/biz.vibeware.StudioControls.desktop"
            subprocess.run(["desktop-file-validate", str(desktop)], check=True, timeout=5)

    def test_same_inputs_and_epoch_produce_identical_deb_bytes(self):
        previous = os.umask(0o077)
        try:
            second = builder.build(
                output_dir=self.root / "second", version="0.3.0", epoch=1_794_873_600
            )
        finally:
            os.umask(previous)
        self.assertEqual(
            hashlib.sha256(self.archive.read_bytes()).digest(),
            hashlib.sha256(second.read_bytes()).digest(),
        )

    def test_existing_release_output_is_preserved(self):
        before = self.archive.read_bytes()
        with self.assertRaises(FileExistsError):
            builder.build(output_dir=self.archive.parent, version="0.3.0", epoch=1_794_873_600)
        self.assertEqual(self.archive.read_bytes(), before)

    def test_extracted_runtime_uses_only_isolated_user_storage_and_manual_import(self):
        application = self.extracted / builder.PREFIX
        home = self.root / "synthetic-home"
        home.mkdir()
        environment = dict(
            os.environ,
            HOME=str(home),
            XDG_DATA_HOME=str(home / "data"),
            XDG_CONFIG_HOME=str(home / "config"),
            XDG_STATE_HOME=str(home / "state"),
            PYTHONDONTWRITEBYTECODE="1",
        )
        code = """
import json, sys
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0, sys.argv[1])
import studio_paths
from obs_remote import OBS
with patch('obs_remote.working_assets', side_effect=AssertionError('read-only client wrote assets')):
    OBS(config_path='unused-synthetic-config.json')
print(json.dumps({'bundled':str(studio_paths.bundled_assets()), 'working':str(studio_paths.working_assets())}))
"""
        result = subprocess.run(
            [sys.executable, "-B", "-c", code, str(application / "Linux")],
            env=environment,
            text=True,
            capture_output=True,
            check=True,
            timeout=15,
        )
        paths = json.loads(result.stdout)
        self.assertEqual(paths["bundled"], str(application / "Assets"))
        self.assertEqual(
            paths["working"], str(home / "data/VibeWare/StudioControls/working-assets")
        )
        self.assertFalse((home / ".config/obs-studio").exists())
        self.assertFalse(list(application.rglob("__pycache__")))
        output = home / "manual-import.json"
        subprocess.run(
            [
                sys.executable,
                "-B",
                str(application / "scripts/prepare-scenes.py"),
                "--platform",
                "linux",
                "--assets",
                paths["working"],
                "--output",
                str(output),
            ],
            env=environment,
            check=True,
            capture_output=True,
            timeout=10,
        )
        prepared = json.loads(output.read_text())
        self.assertEqual(prepared["current_program_scene"], "Privacy")
        self.assertTrue(prepared["DesktopAudioDevice1"]["muted"])
        self.assertNotIn("${ASSET_ROOT}", output.read_text())


if __name__ == "__main__":
    unittest.main()
