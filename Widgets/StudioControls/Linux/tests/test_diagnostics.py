from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

import diagnostics


class DiagnosticsTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.directory = Path(self.temporary.name)
        self.native_capture = patch.multiple(
            diagnostics.faulthandler, enable=lambda **_: None, disable=lambda: None
        )
        self.native_capture.start()
        self.session = None

    def tearDown(self):
        if self.session:
            self.session.close()
        self.native_capture.stop()
        self.temporary.cleanup()

    def start(self):
        self.session = diagnostics.configure_diagnostics(self.directory)
        return self.session

    def test_exception_keeps_stack_and_redacts_credentials(self):
        self.start()
        try:
            raise RuntimeError('server_password="private word" token=abc123')
        except RuntimeError:
            diagnostics.get_logger("test").exception("OBS operation failed: SetCurrentProgramScene")
        text = (self.directory / "panel.log").read_text()
        self.assertIn("Traceback", text)
        self.assertIn("RuntimeError", text)
        self.assertIn("SetCurrentProgramScene", text)
        self.assertNotIn("private word", text)
        self.assertNotIn("abc123", text)

    def test_credential_redaction_covers_authorization_and_signed_urls(self):
        original = "Authorization: Bearer secret-token https://user:pass@example.org/v?sig=signed-value&other=1"
        redacted = diagnostics.redact(original)
        for secret in ["secret-token", "user:pass", "signed-value"]:
            self.assertNotIn(secret, redacted)
        self.assertIn("example.org/v", redacted)

    def test_log_rotation_limits_retained_files(self):
        with patch.object(diagnostics, "MAX_LOG_BYTES", 256):
            session = self.start()
        for index in range(20):
            session.logger.info("Diagnostic event %d %s", index, "x" * 100)
        paths = list(self.directory.glob("panel.log*"))
        self.assertLessEqual(len(paths), diagnostics.LOG_BACKUPS + 1)
        self.assertTrue((self.directory / "panel.log.1").exists())

    def test_close_restores_exception_hook_and_is_idempotent(self):
        previous = sys.excepthook
        session = self.start()
        self.assertEqual(sys.excepthook, session.record_uncaught)
        self.assertIs(diagnostics.configure_diagnostics(self.directory), session)
        session.close()
        session.close()
        self.assertIs(sys.excepthook, previous)

    def test_previous_native_crash_is_preserved_and_bounded(self):
        (self.directory / "crash.log").write_text("a" * 100 + "last frame")
        with patch.object(diagnostics, "MAX_CRASH_BYTES", 24):
            self.start()
        backup = self.directory / "crash.log.1"
        self.assertEqual(backup.stat().st_size, 24)
        self.assertTrue(backup.read_text().endswith("last frame"))
        self.assertEqual((self.directory / "crash.log").stat().st_size, 0)

    def test_export_excludes_configuration_and_refuses_overwrite(self):
        self.start()
        (self.directory / "config.json").write_text('{"password":"do-not-export"}')
        diagnostics.get_logger("test").warning("password=hidden")
        destination = self.directory / "report.zip"
        diagnostics.export_diagnostics(destination, self.directory)
        with zipfile.ZipFile(destination) as archive:
            self.assertNotIn("config.json", archive.namelist())
            text = archive.read("panel.log").decode()
            self.assertNotIn("hidden", text)
            self.assertIn("diagnostics.json", archive.namelist())
        with self.assertRaises(FileExistsError):
            diagnostics.export_diagnostics(destination, self.directory)

    def test_sys_exception_hook_records_the_exception(self):
        session = self.start()
        try:
            raise ValueError("Invalid state")
        except ValueError:
            session.record_uncaught(*sys.exc_info())
        self.assertIn("Unhandled application exception", session.log_file.read_text())


if __name__ == "__main__":
    unittest.main()
