"""Exercise the same runtime contract used by widget ZIP and MSI packaging."""

import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "packaging"))
from vibeware_msi import read_runtime


class WidgetRuntimeTests(unittest.TestCase):
    def setUp(self):
        folder = tempfile.TemporaryDirectory()
        self.addCleanup(folder.cleanup)
        self.payload = Path(folder.name)
        self.deps = self.payload / "Widget.deps.json"
        self.deps.write_text(json.dumps({"runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/win-arm64"}}))
        self.core = {"name": "Microsoft.NETCore.App", "version": "10.0.0"}
        self.desktop = {"name": "Microsoft.WindowsDesktop.App", "version": "10.0.2"}

    def runtime(self, runtime_type, **options):
        (self.payload / "Widget.runtimeconfig.json").write_text(json.dumps({
            "runtimeOptions": {"tfm": "net10.0", **options},
        }))
        return read_runtime(self.payload, "Widget.exe", "arm64", runtime_type)

    def test_framework_and_frameworks(self):
        self.assertEqual(self.runtime("core", framework=self.core), "10.0.0")
        self.assertEqual(self.runtime("desktop", frameworks=[self.core, self.desktop]), "10.0.2")
        with self.assertRaises(ValueError):
            self.runtime("desktop", framework=self.core)
        with self.assertRaises(ValueError):
            self.runtime("core", frameworks=[self.core, self.desktop])

    def test_rejects_bundled_runtime_and_major_rollforward(self):
        for options in ({"includedFrameworks": [self.core]}, {"framework": self.core, "rollForward": "Major"}):
            with self.subTest(options=options), self.assertRaises(ValueError):
                self.runtime("core", **options)
        nested = self.payload / "runtimes"
        nested.mkdir()
        (nested / "CoreCLR.dll").touch()
        with self.assertRaisesRegex(ValueError, "CoreCLR"):
            self.runtime("core", framework=self.core)

    def test_rejects_wrong_architecture_and_runtime_pack(self):
        self.deps.write_text(json.dumps({"runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/win-x64"}}))
        with self.assertRaises(ValueError):
            self.runtime("core", framework=self.core)
        self.deps.write_text(json.dumps({
            "runtimeTarget": {"name": ".NETCoreApp,Version=v10.0/win-arm64"},
            "libraries": {"runtimepack.Microsoft.NETCore.App.Runtime.win-arm64/10.0.0": {}},
        }))
        with self.assertRaisesRegex(ValueError, "runtime pack"):
            self.runtime("core", framework=self.core)
