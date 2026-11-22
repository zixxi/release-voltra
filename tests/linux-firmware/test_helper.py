"""Run with the helper build dependencies. All USB, esptool and ioctl calls use mocks."""
import hashlib
import io
import sys
import unittest
from contextlib import redirect_stdout
from pathlib import Path
from unittest.mock import MagicMock, patch

root = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(root / "LinuxApp/Tools"), str(root / "VoltraFirmware/Tools")]
import linux_flash as helper
import voltra_flash as shared

SOCKET = Path("/sys/devices/pci0000/usb1/1-3")
MAC = "001122334455"


class HelperTests(unittest.TestCase):
    def setUp(self):
        self.enterContext(redirect_stdout(io.StringIO()))
        self.ports = self.enterContext(patch.object(helper, "candidates", return_value=["/dev/ttyACM1"]))
        self.serial = self.enterContext(patch.object(helper.serial, "Serial"))
        self.port = self.serial.return_value.__enter__.return_value
        self.enterContext(patch.object(helper.fcntl, "ioctl"))
        self.chip = self.enterContext(patch.object(helper, "confirmed_controller"))
        self.write = self.enterContext(patch.object(helper, "flash"))

    def run_helper(self, command):
        with patch.object(sys, "stdin", io.StringIO(command)):
            helper.identify_and_write(MAC, SOCKET, b"signed image")

    def test_renumber_and_explicit_write(self):
        self.run_helper("WRITE\n")
        self.assertEqual(self.port.port, "/dev/ttyACM1")
        self.chip.assert_called_once_with(self.port, MAC)
        self.write.assert_called_once_with(self.chip.return_value, b"signed image")

    def test_eof_before_write(self):
        with self.assertRaisesRegex(RuntimeError, "authorization"):
            self.run_helper("")
        self.write.assert_not_called()

    def test_wrong_mac(self):
        self.chip.side_effect = shared.RejectedPort("identity", "wrong MAC")
        with self.assertRaisesRegex(RuntimeError, "does not match"):
            self.run_helper("WRITE\n")
        self.write.assert_not_called()

    def test_ambiguous_topology(self):
        self.ports.return_value.append("/dev/ttyACM2")
        with self.assertRaisesRegex(RuntimeError, "Several"):
            self.run_helper("WRITE\n")
        self.serial.assert_not_called()

    def test_bootloader_timeout(self):
        self.ports.return_value = []
        with patch.object(helper.time, "monotonic", side_effect=[0, 1, 95]), patch.object(helper.time, "sleep"):
            with self.assertRaisesRegex(RuntimeError, "No matching"):
                self.run_helper("WRITE\n")
        self.write.assert_not_called()

    def test_partition_and_readback_on_same_chip(self):
        rom = MagicMock()
        stub = rom.run_stub.return_value
        with patch.object(shared, "cmds") as commands, patch.object(shared.log, "set_logger"):
            commands.detect_flash_size.return_value = "4MB"
            commands.read_flash.return_value = b"wrong table"
            with self.assertRaisesRegex(RuntimeError, "partition"):
                shared.flash(rom, b"data")
            commands.write_flash.assert_not_called()
            with patch.object(shared, "TABLE_SHA256", hashlib.sha256(b"wrong table").hexdigest()):
                shared.flash(rom, b"data")
            commands.write_flash.assert_called_once_with(stub, [(0x10000, b"data")], flash_size="4MB")
            commands.verify_flash.assert_called_once_with(stub, [(0x10000, b"data")], flash_size="4MB")
            self.assertEqual(stub.WRITE_FLASH_ATTEMPTS, 1)


if __name__ == "__main__":
    unittest.main()
