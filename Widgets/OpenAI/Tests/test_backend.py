"""Exercise only synthetic child processes; never launch the real account reader."""
import importlib.util
from pathlib import Path
import sys
import unittest

spec = importlib.util.spec_from_file_location("widget_backend", Path(__file__).resolve().parents[1] / "Linux/backend.py")
backend = importlib.util.module_from_spec(spec)
spec.loader.exec_module(backend)


@unittest.skipUnless(sys.platform.startswith("linux"), "Linux pipe transport")
class BackendBoundsChecks(unittest.TestCase):
    def test_short_response_and_input(self):
        code = "import sys; data=sys.stdin.buffer.read(); sys.stdout.buffer.write(data)"
        self.assertEqual(backend.exchange([sys.executable, "-c", code], b'{"ok":true}'), b'{"ok":true}')

    def test_oversized_response_is_rejected(self):
        with self.assertRaisesRegex(RuntimeError, "exceeds"):
            backend.exchange([sys.executable, "-c", "print('x' * 10000)"], b"", output_limit=128)

    def test_blocked_child_times_out(self):
        with self.assertRaisesRegex(RuntimeError, "timed out"):
            backend.exchange([sys.executable, "-c", "import time; time.sleep(30)"], b"", timeout=.05)


if __name__ == "__main__":
    unittest.main()
