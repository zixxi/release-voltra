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
from esptool.logger import EsptoolLogger, log
from esptool.targets.esp32s3 import ESP32S3ROM
from esptool.util import UnsupportedCommandError
from serial.tools import list_ports

ESPTOOL_VERSION = "5.1.0"
PROTOCOL = 2
APP_OFFSET = 0x10000
APP_CAPACITY = 0x140000
TABLE_OFFSET = 0x8000
TABLE_LENGTH = 0xC00
TABLE_SHA256 = "148b959cbff1c38aa8e1d5c0ba9d612c54997b945e56a63f41223eef650653a1"
NATIVE_USB = (0x303A, ESP32S3ROM.USB_JTAG_SERIAL_PID)


def emit(kind, message):
    print(f"VOLTRA_{kind} {' '.join(str(message).splitlines())}", flush=True)


class FlashProgress(EsptoolLogger):
    def __new__(cls):
        # set_logger swaps the existing logger's class and keeps its state.
        return object.__new__(cls)

    def progress_bar(self, cur_iter, total_iters, **kwargs):
        if total_iters > 0:
            emit("PROGRESS", cur_iter * 100 // total_iters)


class RejectedPort(Exception):
    def __init__(self, reason, message):
        super().__init__(message)
        self.reason = reason


def confirmed_controller(port, expected_mac):
    chip = ESP32S3ROM(port)
    chip.connect("no-reset", attempts=1, detecting=True, warnings=False)
    try:
        security = chip.get_security_info()
    except UnsupportedCommandError as error:
        raise RejectedPort("chip", "The ROM does not support the ESP32-S3 identity command.") from error
    if security["chip_id"] != ESP32S3ROM.IMAGE_CHIP_ID:
        raise RejectedPort("chip", f"ROM chip ID {security['chip_id']!s} is not ESP32-S3.")

    flags = security["parsed_flags"]
    if flags["SECURE_DOWNLOAD_ENABLE"]:
        raise RejectedPort("security", "Secure download mode blocks controller identity verification.")
    mac = bytes(chip.read_mac()).hex().upper()
    if mac != expected_mac:
        raise RejectedPort("identity", f"ROM MAC ending {mac[-6:]} does not match the selected controller.")
    if flags["SECURE_BOOT_EN"] or chip.get_flash_encryption_enabled():
        raise RuntimeError("This controller requires a secure firmware recovery process.")
    chip._post_connect()  # Configure USB/watchdogs only on the verified target.
    return chip


def flash(chip, data):
    emit("STAGE", "checking-layout")
    chip = chip.run_stub()
    cmds.attach_flash(chip)
    if cmds.detect_flash_size(chip) != "4MB":
        raise RuntimeError("The controller does not have the required 4 MB flash.")
    table = cmds.read_flash(chip, TABLE_OFFSET, TABLE_LENGTH, flash_size="4MB", no_progress=True)
    if not hmac.compare_digest(hashlib.sha256(table).hexdigest(), TABLE_SHA256):
        raise RuntimeError("The installed flash partition table does not match the required layout.")

    # esptool normally reopens the COM port and resets the chip on write
    # errors. A new port handle would have to be identified again first.
    chip.WRITE_FLASH_ATTEMPTS = 1
    log.set_logger(FlashProgress())
    emit("STAGE", "writing")
    cmds.write_flash(chip, [(APP_OFFSET, data)], flash_size="4MB")
    emit("STAGE", "verifying-flash")
    cmds.verify_flash(chip, [(APP_OFFSET, data)], flash_size="4MB")
    emit("STAGE", "restarting")
    try:
        cmds.reset_chip(chip, reset_mode="watchdog-reset")
    except (esptool.FatalError, OSError) as error:
        emit("DIAG", f"Reset did not acknowledge: {error}")
        emit("STAGE", "reset-needed")
    print("VOLTRA_VERIFIED", flush=True)


def main(expected_mac, image_path, expected_hash, previous_port):
    if esptool.__version__ != ESPTOOL_VERSION:
        raise RuntimeError("Unexpected esptool version.")
    if not re.fullmatch(r"[0-9A-F]{12}", expected_mac) or not re.fullmatch(r"[0-9A-F]{64}", expected_hash):
        raise RuntimeError("Invalid controller identity or package hash.")
    if not re.fullmatch(r"COM[1-9][0-9]*", previous_port, re.IGNORECASE):
        raise RuntimeError("Invalid COM port for the selected controller.")
    with Path(image_path).open("rb") as file:
        data = file.read(APP_CAPACITY + 1)
    if not 0 < len(data) <= APP_CAPACITY or not hmac.compare_digest(
        hashlib.sha256(data).hexdigest().upper(), expected_hash
    ):
        raise RuntimeError("The firmware image failed its size or integrity check.")
    image = LoadFirmwareImage("esp32s3", data)
    if image.chip_id != ESP32S3ROM.IMAGE_CHIP_ID or data[32:36] != bytes.fromhex("3254CDAB"):
        raise RuntimeError("Expected an ESP32-S3 app-only image.")

    discover_and_flash(expected_mac, previous_port.upper(), data)


def usb_id(identity):
    return ":".join(f"{number:04X}" if number is not None else "?" for number in identity)


def is_candidate(name, identity, previous_port):
    return identity == NATIVE_USB or (name == previous_port and all(
        actual in (None, expected) for actual, expected in zip(identity, NATIVE_USB)
    ))


def discover_and_flash(expected_mac, previous_port, data):
    emit("STAGE", "waiting")
    deadline = time.monotonic() + 40
    inventory = None
    enumeration_error = None
    previous_failure = {}
    rejected_until = {}
    outcomes = set()
    connecting = False
    while time.monotonic() < deadline:
        try:
            ports = {port.device.upper(): (port.vid, port.pid) for port in list_ports.comports()}
            enumeration_error = None
        except OSError as error:
            ports = {}
            if str(error) != enumeration_error:
                enumeration_error = str(error)
                emit("DIAG", f"COM enumeration failed: {error}")
        snapshot = tuple(sorted(ports.items()))
        if inventory != snapshot:
            inventory = snapshot
            emit("DIAG", "COM inventory: " + (", ".join(f"{name} [{usb_id(identity)}]" for name, identity in snapshot) or "none"))
            rejected_until.clear()
        # Windows may expose the already identified COM handle before SetupAPI
        # reports the USB interface again. Probe that exact port without reset.
        names = (previous_port, *(name for name, _ in snapshot if name != previous_port))
        for name in names:
            listed = name in ports
            identity = ports.get(name, (None, None))
            if not is_candidate(name, identity, previous_port):
                continue
            key = (name, identity, listed)
            now = time.monotonic()
            if now < rejected_until.get(key, 0) or now >= deadline:
                continue
            port = serial.Serial(baudrate=115200, timeout=0.25, write_timeout=1.0)
            opened = False
            try:
                try:
                    port.dtr = False
                    port.rts = False
                    port.port = name
                    port.open()
                    opened = True
                    if not connecting:
                        emit("STAGE", "connecting")
                        connecting = True
                    chip = confirmed_controller(port, expected_mac)
                except RejectedPort as error:
                    reason, message = error.reason, str(error)
                    rejected_until[key] = time.monotonic() + 3
                except (esptool.FatalError, OSError) as error:
                    reason = "handshake" if opened else "open"
                    message = str(error)
                else:
                    emit("DIAG", f"{name} [{usb_id(identity)}]: ESP32-S3 and selected ROM MAC verified.")
                    flash(chip, data)
                    return
            finally:
                if port.is_open:
                    port.close()
            # An unlisted port that cannot be opened may simply no longer exist.
            if listed or reason != "open":
                outcomes.add(reason)
            failure = (reason, message)
            if previous_failure.get(key) != failure:
                previous_failure[key] = failure
                source = "" if listed else " (previous port; not enumerated)"
                emit("DIAG", f"{name} [{usb_id(identity)}]{source} {reason}: {message}")
        time.sleep(0.4)
    reasons = (
        ("identity", "A bootloader responded, but its controller identity did not match."),
        ("security", "The bootloader's security settings prevent identifying the controller."),
        ("chip", "A bootloader responded, but it was not an ESP32-S3."),
        ("handshake", "The serial port opened, but the ESP32 bootloader did not respond or disconnected."),
        ("open", "The USB serial port was detected but could not be opened. Close any serial monitors."),
    )
    message = next((message for reason, message in reasons if reason in outcomes), None)
    if message is None:
        message = ("No ESP32-S3 USB serial interface was found." if inventory
                   else "Windows did not report any serial ports. Check the USB cable.")
    raise RuntimeError("Bootloader search timed out. " + message)


if __name__ == "__main__":
    sys.stdout.reconfigure(encoding="utf-8")
    sys.stderr.reconfigure(encoding="utf-8")
    emit("PROTOCOL", PROTOCOL)
    if sys.argv[1:] == ["--protocol-version"]:
        sys.exit(0)
    try:
        main(*sys.argv[1:])
    except (RuntimeError, ValueError, TypeError, esptool.FatalError, OSError) as error:
        emit("ERROR", error)
        sys.exit(1)
