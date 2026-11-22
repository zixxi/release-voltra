"""Portable update transactions against a local filesystem. No desktop or USB device needed."""
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
import time
import unittest
from pathlib import Path
from unittest.mock import patch

helper = Path(__file__).resolve().parents[2] / "LinuxApp/Packaging/update-install.py"
spec = importlib.util.spec_from_file_location("voltra_install", helper)
update = importlib.util.module_from_spec(spec)
spec.loader.exec_module(update)


class Installation(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.current = self.root / "Voltra installed"
        self.next = self.root / "new"
        self.archive = self.root / "archive"
        self.tree(self.current, "1.0.0", "#!/bin/sh\necho restored > \"$PWD/../restored\"\n")
        self.tree(self.next, "1.0.1", """#!/bin/sh
printf '%s' "$VOLTRA_UPDATE_VERSION" > "$VOLTRA_UPDATE_ACK"
exec sleep 0.4
""")

    def tree(self, directory, version, exe):
        directory.mkdir()
        for name in update.REQUIRED:
            path = directory / name
            path.parent.mkdir(exist_ok=True)
            path.write_text(exe if name == "LinuxApp" else "file")
            path.chmod(0o755)
        update.inventory(directory, version)

    def package(self):
        with tarfile.open(self.archive, "w:gz") as tar:
            tar.add(self.next, arcname=update.ROOT)
        return hashlib.sha256(self.archive.read_bytes()).hexdigest()

    def run_transaction(self, command, digest=None):
        checksum = self.package()
        # Simulate a separately exiting application parent; the helper must wait on its pidfd.
        parent = """import os, subprocess, sys
from pathlib import Path
child = subprocess.Popen([sys.executable, '-I', sys.argv[1], 'install', *sys.argv[2:6],
                         '1.0.1', str(os.getpid())], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
ready = child.stdout.readline().strip()
if ready != 'READY': sys.exit(23)
child.stdin.write(sys.argv[6]+'\\n'); child.stdin.close()
"""
        return subprocess.run([sys.executable, "-c", parent, str(helper), str(self.current), "1.0.0",
            str(self.archive), digest or checksum, command], capture_output=True, text=True, timeout=15)

    def test_commit(self):
        self.assertEqual(self.run_transaction("COMMIT").returncode, 0)
        update.validate(self.current, "1.0.1")
        self.assertFalse(list(self.root.glob(".*.update-*")))

    def test_cancel(self):
        self.assertEqual(self.run_transaction("CANCEL").returncode, 0)
        update.validate(self.current, "1.0.0")
        self.assertFalse(list(self.root.glob(".*.update-*")))

    def test_rollback(self):
        (self.next / "LinuxApp").write_text("#!/bin/sh\nexit 1\n")
        self.assertEqual(self.run_transaction("COMMIT").returncode, 0)
        update.validate(self.current, "1.0.0")
        for _ in range(20):
            if (self.root / "restored").exists():
                break
            time.sleep(.05)
        self.assertTrue((self.root / "restored").exists())
        self.assertIn("confirm startup", next(self.root.glob("*.update-error.log")).read_text())

    def test_bad_digest(self):
        self.assertNotEqual(self.run_transaction("COMMIT", "0" * 64).returncode, 0)
        update.validate(self.current, "1.0.0")

    def test_install_location(self):
        (self.current / "user-data").touch()
        with self.assertRaises(ValueError):
            update.writable_install(self.current, "1.0.0")
        (self.current / "user-data").unlink()
        with patch.object(os, "access", return_value=False), self.assertRaises(PermissionError):
            update.writable_install(self.current, "1.0.0")
        (self.root / ".git").mkdir()
        with self.assertRaises(ValueError):
            update.writable_install(self.current, "1.0.0")
        with self.assertRaises(ValueError):
            update.writable_install(Path("/usr/local/voltra"), "1.0.0")

    def test_unsafe_archives(self):
        directory = self.root / "stage"
        directory.mkdir()
        for name, kind in [("../escape", tarfile.REGTYPE), ("/escape", tarfile.REGTYPE),
                           ("Voltra-linux-x64/../escape", tarfile.REGTYPE), ("wrong/app", tarfile.REGTYPE),
                           ("Voltra-linux-x64/link", tarfile.SYMTYPE), ("Voltra-linux-x64/hard", tarfile.LNKTYPE),
                           ("Voltra-linux-x64/fifo", tarfile.FIFOTYPE)]:
            with self.subTest(name=name):
                buffer = io.BytesIO()
                with tarfile.open(fileobj=buffer, mode="w:gz") as tar:
                    info = tarfile.TarInfo(name)
                    info.type = kind
                    tar.addfile(info)
                buffer.seek(0)
                with self.assertRaises(ValueError):
                    update.unpack(buffer, directory)
        self.assertEqual(list(directory.iterdir()), [])


if __name__ == "__main__":
    unittest.main()
