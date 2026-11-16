"""Exercise change selection with real multi-commit and rename Git diffs."""

import os
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

from select_projects import PROJECTS, changed_projects, select_paths


class ProjectSelectionTests(unittest.TestCase):
    def test_app_isolation_and_shared_changes(self):
        self.assertEqual(select_paths(["Apps/WorkTrail/WorkTrail/MainWindow.cs"]), ["worktrail"])
        self.assertEqual(select_paths(["Apps/ThermalRight Panel/src/file.cs"]), ["thermalright"])
        self.assertEqual(select_paths(["Apps/RamTaskman/src/ramtaskman/locales/it.json"]), ["ramtaskman"])
        self.assertEqual(select_paths([".github/workflows/ci-ramtaskman.yml"]), ["ramtaskman"])
        self.assertEqual(select_paths(["README.md", "docs/project-imports.md"]), [])
        self.assertEqual(select_paths([".github/workflows/ci-helpme.yml"]), ["helpme"])
        self.assertEqual(select_paths([".github/workflows/agentinbox-debug-msix.yml"]), ["agentinbox"])
        self.assertEqual(select_paths(["scripts/powershell/Common.ps1"]), list(PROJECTS))
        self.assertEqual(select_paths(["Widgets/WaterRipple/README.md"]), [])
        self.assertEqual(select_paths(["Widgets/WaterRipple/Linux/extension.js"]), ["desktop-ripple"])
        self.assertEqual(select_paths(["Widgets/OpenAI/Shared/Analytics.cs"]), ["openai-usage"])
        self.assertEqual(select_paths(["Widgets/RetroTerminal/Linux/extension/retro-terminal@vibeware.local/persistence.js"]), ["retro-terminal"])
        self.assertEqual(select_paths(["Widgets/RetroTerminal/Windows/Main.cpp"]), ["retro-terminal"])
        self.assertEqual(select_paths([".github/workflows/ci-retro-terminal.yml"]), ["retro-terminal"])
        self.assertEqual(select_paths(["Widgets/Shared/WidgetAbout.cs"]), ["openai-usage", "desktop-ripple"])

    def test_complete_push_and_cross_app_move(self):
        original = Path.cwd()
        with tempfile.TemporaryDirectory(prefix="vibekit-diff-") as directory:
            try:
                os.chdir(directory)

                def git(*args):
                    return subprocess.check_output(["git", *args], stderr=subprocess.DEVNULL, text=True).strip()

                def commit(path):
                    file = Path(path)
                    file.parent.mkdir(parents=True, exist_ok=True)
                    file.write_text("source\n")
                    git("add", ".")
                    git("-c", "user.name=CI", "-c", "user.email=ci@example.test", "commit", "-qm", "change")
                    return git("rev-parse", "HEAD")

                git("init", "-q")
                base = commit("README.md")
                commit("Apps/AgentInbox/python/engine.py")
                head = commit("Apps/WorkTrail/source.cs")
                self.assertEqual(changed_projects(base, head, "push"), ["agentinbox", "worktrail"])
                Path("Apps/VoiceDucker").mkdir()
                git("mv", "Apps/WorkTrail/source.cs", "Apps/VoiceDucker/source.cs")
                git("-c", "user.name=CI", "-c", "user.email=ci@example.test", "commit", "-qm", "move")
                self.assertEqual(changed_projects(head, "HEAD", "push"), ["voiceducker", "worktrail"])
                self.assertEqual(changed_projects("0" * 40, "HEAD", "push"), list(PROJECTS))
                with patch("sys.stderr"):
                    self.assertEqual(changed_projects("missing-base", "HEAD", "push"), list(PROJECTS))
            finally:
                os.chdir(original)


if __name__ == "__main__":
    unittest.main()
