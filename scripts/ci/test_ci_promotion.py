import unittest
from unittest.mock import Mock

from reuse_pr import successful_run
from select_projects import select_paths


class PromotionTests(unittest.TestCase):
    def test_reuse_requires_passing_result_for_matching_base_and_tested_tree(self):
        repo = "owner/repo"
        pr = {"head": {"repo": {"full_name": repo}, "ref": "dev", "sha": "devhead"},
              "number": 10, "merged_at": "now", "base": {"ref": "main"}, "merge_commit_sha": "merged"}
        run = {"head_branch": "dev", "head_repository": {"full_name": repo}, "id": 42, "run_attempt": 1}
        for stamp, expected in [("Verificato PR 10 base tree", 42), ("Verificato PR 10 old tree", None),
                                ("Verificato PR 10 base other", None), ("Verificato PR 9 base tree", None)]:
            job = {"name": "VibeKit CI required", "conclusion": "success",
                   "steps": [{"name": stamp, "conclusion": "success"}]}
            api = Mock(side_effect=[[pr], {"workflow_runs": [run]}, {"jobs": [job]}])
            self.assertEqual(successful_run(repo, "merged", "base", "tree", api), expected)
        self.assertIsNone(successful_run(repo, "direct", "base", "tree", Mock(return_value=[])))

    def test_docs_across_apps_with_one_packaging_edit(self):
        paths = ["Apps/HelpMe/docs/ARCHITECTURE.md", "Apps/VoiceDucker/docs/images/screen.png",
                 "scripts/README.md", "Apps/MarkVibe/scripts/package-windows.ps1"]
        self.assertEqual(select_paths(paths), ["markvibe"])

    def test_new_projects_and_shared_consumer(self):
        self.assertEqual(select_paths(["scripts/release/release.py"]), [])
        self.assertEqual(set(select_paths([".github/workflows/package-missing.yml"])), {"ghostty-shaders", "worktrail"})
        self.assertEqual(select_paths(["Apps/RepoRadar/build.ps1"]), ["reporadar"])
        self.assertEqual(select_paths(["Widgets/Vignette/Linux/build-package.py"]), ["vignette"])
        self.assertIn("vignette", select_paths(["Widgets/Shared/WindowsSignIn.cs"]))
