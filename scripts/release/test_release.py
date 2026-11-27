import json
import tempfile
import unittest
import zipfile
from pathlib import Path
from unittest.mock import patch

from release import (CATALOG, REPO, ReleaseError, choose_version, extract_packages,
                     get_build, publish, select_artifacts, trusted_run, verify_external_runtime)


class ReleaseTests(unittest.TestCase):
    def setUp(self):
        self.sha = "1" * 40
        self.run = {"id": 17, "head_repository": {"full_name": REPO},
                    "repository": {"full_name": REPO}, "event": "push", "head_branch": "main",
                    "head_sha": self.sha, "path": ".github/workflows/monorepo.yml",
                    "conclusion": "success"}
        self.specs = [{"artifact": "output-x64", "file": "tool.zip"}]
        self.art = {"id": 24, "name": "output-x64", "expired": False, "digest": "sha256:"+"3"*64,
                    "workflow_run": {"id": 17, "head_sha": self.sha}}

    def test_trust_requires_exact_branch_commit_workflow_event_and_repository(self):
        def trust(run):
            return trusted_run(run, "ci-tool.yml", "main", self.sha, True)
        self.assertTrue(trust(self.run))
        for change in [{"head_repository": {"full_name": "other/repo"}}, {"event": "pull_request"},
                       {"path": ".github/workflows/other.yml"}, {"head_branch": "dev"},
                       {"head_sha": "2"*40}, {"conclusion": "failure"}]:
            self.assertFalse(trust(self.run | change))
        self.assertFalse(trusted_run(self.run, "ci-tool.yml", "main", self.sha, False))

    def test_reuse_skips_incomplete_build_and_never_dispatches_for_explicit_invalid_id(self):
        self.assertEqual(select_artifacts([self.art | {"expired": True}], self.specs, 17, self.sha), {})
        self.assertEqual(select_artifacts([self.art], self.specs+[{"artifact":"output-arm64"}], 17, self.sha), {})
        project = {"workflow":"ci-tool.yml"}
        with patch("release.api", return_value=self.run | {"event":"pull_request"}), patch("release.gh") as cli:
            with self.assertRaises(ReleaseError):
                get_build(project, self.specs, "main", self.sha, {}, 17)
            cli.assert_not_called()
        with patch("release.pages", side_effect=[[self.run], [self.art]]), patch("release.gh") as cli:
            self.assertEqual(get_build(project, self.specs, "main", self.sha, {}, None), (17,{24:self.art}))
            cli.assert_not_called()

    def test_paths_inside_artifact_cannot_replace_files(self):
        specs = [{"artifact": "win", "file": "*.msi"}]
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp)/"artifact.zip"
            with zipfile.ZipFile(path, "w") as archive:
                archive.writestr("../outside.msi", b"bad")
                archive.writestr("other/OUTSIDE.msi", b"bad")
            with self.assertRaises(ReleaseError):
                extract_packages(path, "win", specs, Path(tmp), set())

    def test_public_assets_reject_store_packages_even_with_a_broad_catalog_pattern(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = Path(folder)
            for extension in ("msix", "msixbundle", "msixupload"):
                archive = directory / "download.zip"
                with zipfile.ZipFile(archive, "w") as bundle:
                    bundle.writestr(f"store.{extension}", b"test")
                with self.assertRaises(ReleaseError):
                    extract_packages(archive, "win", [{"artifact": "win", "file": "*"}], directory, set())

    def test_managed_zips_require_external_framework_and_exclude_nested_runtime_files(self):
        core = {"name": "Microsoft.NETCore.App", "version": "10.0.14"}
        desktop = {"name": "Microsoft.WindowsDesktop.App", "version": "10.0.14"}
        cases = [
            ({"frameworks": [core, desktop]}, [], desktop["name"], True),
            ({"framework": core}, [], core["name"], True),
            ({"framework": core}, [], desktop["name"], False),
            ({"includedFrameworks": [core]}, [], core["name"], False),
            ({"framework": core}, ["nested/CoreCLR.dll"], core["name"], False),
        ]
        with tempfile.TemporaryDirectory() as folder:
            for options, files, expected, valid in cases:
                path = Path(folder) / "app.zip"
                with zipfile.ZipFile(path, "w") as bundle:
                    bundle.writestr("app/app.runtimeconfig.json", json.dumps({"runtimeOptions": options}))
                    for name in files:
                        bundle.writestr(name, b"test")
                with self.subTest(options=options, files=files, expected=expected), zipfile.ZipFile(path) as bundle:
                    if valid:
                        verify_external_runtime(bundle, expected)
                    else:
                        with self.assertRaises(ReleaseError):
                            verify_external_runtime(bundle, expected)

    def test_markvibe_reuse_is_bound_to_requested_version_and_handles_directories(self):
        project = json.loads(CATALOG.read_text())["markvibe"]
        specs = [{"artifact":s["artifact"].format(version="0.1.0.64"),
                  "file":s["file"].format(version="0.1.0.64")} for s in project["assets"]]
        wrong = self.art | {"name":"MarkVibe-0.1.0.63-Release-Windows"}
        right = self.art | {"name":"MarkVibe-0.1.0.64-Release-Windows"}
        self.assertFalse(select_artifacts([wrong], specs, 17, self.sha))
        self.assertEqual(select_artifacts([right], specs, 17, self.sha), {24:right})
        with tempfile.TemporaryDirectory() as temp:
            folder = Path(temp)
            bundle = folder/"artifact.zip"
            expected = "MarkVibe-0.1.0.64-release-win-x64.msi"
            with zipfile.ZipFile(bundle,"w") as zip_file:
                zip_file.writestr("build/"+expected, bytes.fromhex("d0cf11e0a1b11ae1"))
                zip_file.writestr("build/MarkVibe-0.1.0.63-release-win-x64.msi", b"old")
            matched = extract_packages(bundle, right["name"], specs, folder, set())
            self.assertEqual(matched, {0})
            self.assertTrue((folder/expected).is_file())
            self.assertEqual(len(list(folder.iterdir())), 2)

    def test_stable_source_version_and_build_revision(self):
        with self.assertRaises(ReleaseError):
            choose_version("worktrail", "0.0.1", None, "stable")
        with self.assertRaises(ReleaseError):
            choose_version("helpme", "1.0.7", "1.0.8", "stable")
        with self.assertRaises(ReleaseError):
            choose_version("markvibe", "0.1.0.63", "0.1.0.63", "test")
        self.assertEqual(choose_version("markvibe", "0.1.0.63", "0.1.0.64", "test"), "0.1.0.64")

    def test_different_tag_commit_aborts_before_any_write(self):
        existing = {"ref": "refs/tags/release/tool/v1.0.0", "object":{"type":"commit","sha":"2"*40}}
        with patch("release.pages",return_value=[]), patch("release.api",return_value=[existing]), patch("release.gh") as cli:
            with self.assertRaises(ReleaseError):
                publish("release/tool/v1.0.0",self.sha,17,"Tool","stable",Path("."),{})
            cli.assert_not_called()

    def test_catalog_covers_all_projects_with_registered_workflows(self):
        root = CATALOG.parents[2]
        projects = json.loads(CATALOG.read_text())
        self.assertEqual(set(projects), set(json.loads((root/"scripts/ci/projects.json").read_text())))
        for project in projects.values():
            self.assertTrue((root/".github/workflows"/project["workflow"]).is_file())
            self.assertTrue(project["assets"])

    def test_windows_release_matrix_and_helpme_distribution(self):
        projects = json.loads(CATALOG.read_text())
        for slug, config in projects.items():
            public = [item["file"].lower() for item in config["assets"]]
            if slug not in {"ulanzideck", "ghostty-shaders"}:
                framework = "Microsoft.WindowsDesktop.App" if slug == "vignette" else "Microsoft.NETCore.App"
                self.assertEqual(config["windows_framework"], framework)
            self.assertFalse(any(".msix" in name or "debug" in name for name in public), slug)
            if slug == "ghostty-shaders":
                self.assertTrue(all("linux" in name for name in public))
            elif slug == "helpme":
                self.assertTrue(all(name.endswith(".deb") for name in public))
                self.assertEqual({a["artifact"] for a in config["linked_artifacts"]},
                                 {f"helpme-windows-{kind}-{arch}" for kind in ("msi", "portable")
                                  for arch in ("x64", "arm64")})
            else:
                for arch in ("x64", "arm64"):
                    for extension in (".zip", ".msi"):
                        hits = [name for name in public if arch in name and name.endswith(extension)]
                        self.assertEqual(len(hits), 1, (slug, arch, extension, hits))


if __name__ == "__main__":
    unittest.main()
