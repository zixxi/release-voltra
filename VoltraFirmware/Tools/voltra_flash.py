"""Frozen Windows helper for Voltra's ESP32-S3-Zero app-only firmware updates."""

import hashlib
import hmac
import re
import sys
import time
from pathlib import Path

import esptool
import serial
from esptool import cmds
from esptool.bin_image import LoadFirmwareImage
from esptool.targets.esp32s3 import ESP32S3ROM
from serial.tools import list_ports

ESPTOOL_VERSION = "5.1.0"
APP_OFFSET = 0x10000
APP_CAPACITY = 0x140000
TABLE_OFFSET = 0x8000
TABLE_LENGTH = 0xC00
TABLE_SHA256 = "148b959cbff1c38aa8e1d5c0ba9d612c54997b945e56a63f41223eef650653a1"
NATIVE_USB = (0x303A, 0x1001)


def status(message):
    print(f"VOLTRA_STATUS {message}", flush=True)


def confirmed_controller(port, expected_mac):
    chip = ESP32S3ROM(port)
    chip.connect("no-reset", attempts=1, detecting=True)
    security = chip.get_security_info()
    if security["chip_id"] != ESP32S3ROM.IMAGE_CHIP_ID:
        return None

    flags = security["parsed_flags"]
    if flags["SECURE_DOWNLOAD_ENABLE"]:
        return None
    mac = bytes(chip.read_mac()).hex().upper()
    if mac != expected_mac:
        return None
    if flags["SECURE_BOOT_EN"] or chip.get_flash_encryption_enabled():
        raise RuntimeError("This controller requires a secure firmware recovery process.")
    chip._post_connect()  # Configure USB/watchdogs only on the verified target.
    return chip


def flash(chip, data):
    status("Checking the controller's flash size and partition table…")
    chip = chip.run_stub()
    cmds.attach_flash(chip)
    if cmds.detect_flash_size(chip) != "4MB":
        raise RuntimeError("The controller does not have the required 4 MB flash.")
    table = cmds.read_flash(chip, TABLE_OFFSET, TABLE_LENGTH, flash_size="4MB", no_progress=True)
    if not hmac.compare_digest(hashlib.sha256(table).hexdigest(), TABLE_SHA256):
        raise RuntimeError("The installed flash partition table does not match the required layout.")

    status("Flashing the application partition… Keep USB connected.")
    cmds.write_flash(chip, [(APP_OFFSET, data)], flash_size="4MB", no_progress=True)
    status("Verifying the flashed application…")
    cmds.verify_flash(chip, [(APP_OFFSET, data)], flash_size="4MB")
    status("Restarting the ESP32…")
    try:
        cmds.reset_chip(chip, reset_mode="watchdog-reset")
    except (esptool.FatalError, serial.SerialException, OSError):
        status("Press RESET once if the controller does not reconnect.")
    print("VOLTRA_VERIFIED", flush=True)


def main(expected_mac, image_path, expected_hash):
    if esptool.__version__ != ESPTOOL_VERSION:
        raise RuntimeError("Unexpected esptool version.")
    if not re.fullmatch(r"[0-9A-F]{12}", expected_mac) or not re.fullmatch(r"[0-9A-F]{64}", expected_hash):
        raise RuntimeError("Invalid controller identity or package hash.")
    with Path(image_path).open("rb") as file:
        data = file.read(APP_CAPACITY + 1)
    if not 0 < len(data) <= APP_CAPACITY or not hmac.compare_digest(
        hashlib.sha256(data).hexdigest().upper(), expected_hash
    ):
        raise RuntimeError("The firmware image failed its size or integrity check.")
    image = LoadFirmwareImage("esp32s3", data)
    if image.chip_id != ESP32S3ROM.IMAGE_CHIP_ID or data[32:36] != bytes.fromhex("3254CDAB"):
        raise RuntimeError("Expected an ESP32-S3 app-only image.")

    status("Looking for the selected controller in USB download mode…")
    deadline = time.monotonic() + 40
    while time.monotonic() < deadline:
        for candidate in list_ports.comports():
            if (candidate.vid, candidate.pid) != NATIVE_USB or time.monotonic() >= deadline:
                continue
            with serial.Serial(baudrate=115200) as port:
                port.dtr = False
                port.rts = False
                port.port = candidate.device
                try:
                    port.open()
                    chip = confirmed_controller(port, expected_mac)
                except (serial.SerialException, esptool.FatalError):
                    continue
                if chip is not None:
                    # All reads, writes and verification use this same open port.
                    flash(chip, data)
                    return
        time.sleep(0.4)
    raise RuntimeError("The matching ESP32 was not found. Hold BOOT, tap RESET, release BOOT, and retry.")


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    try:
        main(*sys.argv[1:])
    except (RuntimeError, ValueError, TypeError, esptool.FatalError, OSError) as error:
        print(f"VOLTRA_ERROR {error}", flush=True)
        sys.exit(1)
