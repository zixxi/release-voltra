"""Private Linux firmware helper. Keep the verified ROM handle through write and readback."""
import fcntl
import hashlib
import hmac
import os
import re
import sys
import termios
import time
from pathlib import Path

import esptool
import serial
from serial.tools import list_ports
from voltra_flash import (APP_CAPACITY, ESPTOOL_VERSION, ESP32S3ROM, LoadFirmwareImage,
                          RejectedPort, confirmed_controller, emit, flash)


def candidates(topology):
    result = set()
    for port in list_ports.comports():
        if (port.vid, port.pid) != (0x303A, 0x1001):
            continue
        tty = Path("/sys/class/tty", Path(port.device).name).resolve()
        if topology in tty.parents:
            result.add(port.device)
    return sorted(result)


class ProgressStream:
    """Once writing starts, a closed parent UI must not interrupt flash I/O."""
    def __init__(self, stream):
        self.stream = stream

    def __getattr__(self, name):
        return getattr(self.stream, name)

    def write(self, data):
        try:
            return self.stream.write(data)
        except BrokenPipeError:
            self.detach_pipe()
            return len(data)

    def flush(self):
        try:
            self.stream.flush()
        except BrokenPipeError:
            self.detach_pipe()

    def detach_pipe(self):
        with open(os.devnull, "w") as sink:
            os.dup2(sink.fileno(), self.stream.fileno())


def identify_and_write(expected_mac, topology, data):
    deadline = time.monotonic() + 90
    last_error = "No matching native USB interface on the original USB socket."
    emit("STAGE", "waiting")
    while time.monotonic() < deadline:
        ports = candidates(topology)
        if len(ports) > 1:
            raise RuntimeError("Several bootloader interfaces match the USB socket. Disconnect other controllers.")
        if ports:
            # Set modem signals before open. Neither scanning nor sync should request a reset.
            with serial.Serial(port=None, baudrate=115200, timeout=.25, write_timeout=1, exclusive=True) as port:
                port.dtr = port.rts = False
                port.port = ports[0]
                try:
                    port.open()
                    fcntl.ioctl(port.fileno(), termios.TIOCEXCL)
                    chip = confirmed_controller(port, expected_mac)
                except RejectedPort as error:
                    raise RuntimeError("Bootloader identity or security state does not match the selected controller.") from error
                except (esptool.FatalError, OSError) as error:
                    last_error = f"Cannot open or synchronize the bootloader ({type(error).__name__}). Check BOOT, serial permissions and other serial tools."
                else:
                    emit("STAGE", "target-verified")
                    if sys.stdin.readline().strip() != "WRITE":
                        raise RuntimeError("No authorization to write.")
                    # This checks physical flash size and partition hash on the same handle,
                    # writes just the application, verifies flash and requests watchdog reset.
                    from contextlib import redirect_stderr, redirect_stdout
                    with redirect_stdout(ProgressStream(sys.stdout)), redirect_stderr(ProgressStream(sys.stderr)):
                        flash(chip, data)
                    return
                finally:
                    if port.is_open:
                        try:
                            fcntl.ioctl(port.fileno(), termios.TIOCNXCL)
                        except OSError:
                            pass
        time.sleep(.5)
    raise RuntimeError(last_error)


def main(args):
    if esptool.__version__ != ESPTOOL_VERSION:
        raise RuntimeError("Unexpected esptool version.")
    emit("PROTOCOL", "LINUX-1")
    if args == ["--self-test"]:
        from esptool.loader import StubFlasher
        StubFlasher(ESP32S3ROM)
        return
    mac, filename, digest, size, location = args
    if not re.fullmatch("[A-F0-9]{12}", mac) or not re.fullmatch("[A-F0-9]{64}", digest):
        raise ValueError("Invalid controller identity or checksum.")
    topology = Path(location)
    if not topology.is_absolute() or not topology.is_relative_to("/sys/devices") or ".." in topology.parts:
        raise ValueError("Invalid USB topology.")
    with open(filename, "rb") as stream:
        data = stream.read(APP_CAPACITY + 1)
    if not 0 < len(data) == int(size) <= APP_CAPACITY or not hmac.compare_digest(hashlib.sha256(data).hexdigest().upper(), digest):
        raise ValueError("Firmware size or SHA-256 mismatch.")
    image = LoadFirmwareImage("esp32s3", data)
    if image.chip_id != ESP32S3ROM.IMAGE_CHIP_ID or data[32:36] != bytes.fromhex("3254CDAB"):
        raise ValueError("Expected an ESP32-S3 application image.")
    identify_and_write(mac, topology, data)


if __name__ == "__main__":
    try:
        main(sys.argv[1:])
    except Exception as error:
        emit("ERROR", str(error))
        sys.exit(1)
