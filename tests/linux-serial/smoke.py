"""Run from the repo root after building Voltra.Linux.SerialTests.csproj in Release."""
import base64
import errno
import fcntl
import json
import os
from pathlib import Path
import pty
import select
import subprocess
import sys
import tempfile
import termios
import threading
import time

PROJECT = Path(__file__).resolve().parent
HOST = PROJECT / "bin/Release/net10.0/Voltra.Linux.SerialTests.dll"
DOTNET = sys.argv[1] if len(sys.argv) > 1 else "dotnet"
KNOBS = [0, 12, 43, 74, 100]


def eventually(probe, condition, description, timeout=9):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        result = probe()
        if condition(result):
            return result
        time.sleep(0.06)
    raise AssertionError(f"{description}: {result!r}")


class Controller:
    def __init__(self, dev, sysfs, name, generation, alias=None, vendor="303a", product="1001", answers=True):
        self.node = dev / name
        self.alias = dev / "serial/by-id" / alias if alias else None
        self.entry = sysfs / "class/tty" / name
        self.usb = sysfs / "devices/usb1" / f"1-{generation}"
        interface = self.usb / "1-1:1.0"
        tty = interface / "tty" / name

        self.master, self.slave = pty.openpty()
        self.endpoint = os.ttyname(self.slave)
        self.node.symlink_to(self.endpoint)
        if self.alias:
            self.alias.symlink_to(f"../../{name}")
        # Linux sysfs: the class entry and its device child are both relative links.
        tty.mkdir(parents=True)
        for key, value in (("idVendor", vendor), ("idProduct", product), ("devnum", generation)):
            if value is not None:
                (self.usb / key).write_text(f"{value}\n")
        self.entry.symlink_to(os.path.relpath(tty, self.entry.parent), target_is_directory=True)
        (tty / "device").symlink_to("../..", target_is_directory=True)
        self.answers = answers
        self.pause = False
        self.stop = threading.Event()
        self.commands = []
        self.failure = None
        self.thread = threading.Thread(target=self.emulate, daemon=True)
        self.thread.start()

    def emulate(self):
        buffer = b""
        nonce = ""
        last_packet = 0
        try:
            while not self.stop.is_set():
                replies = []
                if select.select([self.master], [], [], 0.025)[0]:
                    buffer += os.read(self.master, 65536)
                *lines, buffer = buffer.split(b"\n")
                for raw in lines:
                    line = raw.decode("ascii").strip()
                    if not line.startswith("@"):
                        continue
                    self.commands.append(line)
                    if self.pause:
                        continue
                    if line.startswith("@H,"):
                        nonce = line.split(",")[1]
                        replies += ["PURPLEMIXER,1,wrong", "MIX,100,100,100,100,100"]
                        if self.answers:
                            replies.append(f"PURPLEMIXER,1,{nonce}")
                    elif line.startswith("@I,"):
                        replies.append(f"VOLTRA,INFO,{nonce},VOLTRA-S3-ZERO,0.1.1,1,001122334455,4194304,65536,65536")
                    elif line == "@Q":
                        replies.append("OLED,READY,128,64,3")
                    elif line.startswith("@C"):
                        header, data = line.split(",")
                        assert base64.b64decode(data) == b"\xaa" * 256
                        replies.append(f"OLED,ACK,{header[2:]}")
                if nonce and self.answers and not self.pause and time.monotonic() - last_packet >= 0.08:
                    replies.append("MIX," + ",".join(map(str, KNOBS)))
                    last_packet = time.monotonic()
                if replies:
                    os.write(self.master, ("\n".join(replies) + "\n").encode())
        except (OSError, AssertionError, UnicodeError) as error:
            self.failure = error

    def close(self):
        self.stop.set()
        self.thread.join(1)
        if self.alias:
            self.alias.unlink()
        self.node.unlink()
        self.entry.unlink()
        os.close(self.slave)
        os.close(self.master)
        if self.failure:
            raise self.failure


class App:
    def __init__(self, dev, sysfs, config):
        self.process = subprocess.Popen([DOTNET, HOST, dev, sysfs],
            env=dict(os.environ, XDG_CONFIG_HOME=str(config)), stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            text=True)

    def state(self, command="status"):
        process = self.process
        process.stdin.write(command + "\n")
        process.stdin.flush()
        assert select.select([process.stdout], [], [], 4)[0], "Test harness status timeout"
        return json.loads(process.stdout.readline())

    def connected(self, port):
        return eventually(self.state, lambda state: state["Port"] == str(port) and state["Firmware"] == "0.1.1"
            and state["Levels"] == KNOBS and state["Displayed"], f"expected verified connection to {port.name}")

    def open_ports(self):
        ports = []
        for fd in Path(f"/proc/{self.process.pid}/fd").iterdir():
            try:
                ports.append(os.readlink(fd))
            except FileNotFoundError:
                pass
        return [path for path in ports if path.startswith("/dev/pts/")]

    def close(self):
        if self.process.poll() is None:
            self.process.stdin.write("quit\n")
            self.process.stdin.flush()
            try:
                self.process.wait(5)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait()
        assert self.process.returncode == 0


def main(root):
    dev, sysfs, config = (root / part for part in ("dev", "sys", "settings"))
    (dev / "serial/by-id").mkdir(parents=True)
    (sysfs / "class/tty").mkdir(parents=True)
    active = []
    app = None

    def add(*args, **kwargs):
        port = Controller(dev, sysfs, *args, **kwargs)
        active.append(port)
        return port

    def unplug(port):
        active.remove(port)
        port.close()

    alias = "usb-Espressif_USB_JTAG_serial_debug_unit_AC:27:6E:B0:FB:EC-if00"
    try:
        # A known USB mismatch overrides even a misleading Espressif-by-id name.
        other = add("ttyUSB5", 1, alias=alias.replace("EC", "FF"), vendor="10c4", product="ea60")
        spoof = add("ttyACM8", 2, answers=False)
        app = App(dev, sysfs, config)
        eventually(app.state, lambda state: "identity was not confirmed" in state["Diagnostics"], "wrong nonce rejected")
        before = len(spoof.commands)
        assert not other.commands
        assert app.state()["Port"] is None

        # Hot plug a working controller and prefer its persistent alias.
        real = add("ttyACM0", 3, alias)
        state = app.connected(real.alias)
        assert state["Ports"].count(str(real.alias)) == 1
        assert str(real.node) not in state["Ports"]
        assert len(spoof.commands) == before
        assert "303a:1001" in state["Diagnostics"]
        assert "AC:27:6E:B0:FB:EC" not in state["Diagnostics"]

        # While connected, only one session has opened that PTY.
        assert app.open_ports() == [real.endpoint]
        try:
            fd = os.open(real.endpoint, os.O_RDWR | os.O_NONBLOCK)
        except OSError as error:
            assert error.errno == errno.EBUSY, error
        else:
            os.close(fd)
            raise AssertionError("Serial port did not have an exclusive handle")

        # The updater must actually close the descriptor before handing off to the ROM helper.
        app.state("hold")
        assert not app.open_ports()
        time.sleep(1.5)
        assert not app.open_ports()
        fcntl.ioctl(real.slave, termios.TIOCNXCL)
        app.state("resume")
        app.connected(real.alias)

        # Unplug and replug with another kernel name while retaining the by-id identity.
        unplug(real)
        eventually(app.state, lambda state: state["Port"] is None, "disconnect released link")
        real = add("ttyACM1", 4, alias)
        app.connected(real.alias)
        assert not other.commands

        # Boot with a connected controller, retaining Automatic and a manual preference.
        app.state(f"manual {other.node}")
        app.connected(other.alias)
        assert real.endpoint not in app.open_ports()
        fcntl.ioctl(real.slave, termios.TIOCNXCL)
        app.state("automatic")
        assert app.connected(real.alias)["Preferred"] == str(other.node)
        app.close()
        # The test holds its own slave FD, so clear the PTY's exclusive marker before reopening.
        fcntl.ioctl(real.slave, termios.TIOCNXCL)
        fcntl.ioctl(spoof.slave, termios.TIOCNXCL)
        app = App(dev, sysfs, config)
        state = app.connected(real.alias)
        assert state["AutoDetect"] and state["Preferred"] == str(other.node)

        # Fall back to the kernel node when there is no by-id link.
        unplug(real)
        unplug(spoof)
        raw = add("ttyACM2", 5)
        fcntl.ioctl(raw.slave, termios.TIOCEXCL)
        eventually(app.state, lambda state: state["Status"].startswith("ttyACM2:") and "port busy" in state["Status"].lower(),
            "busy port diagnosed")
        fcntl.ioctl(raw.slave, termios.TIOCNXCL)
        app.connected(raw.node)
        # Exercise the reset path when USB stays enumerated and knob data stops.
        raw.pause = True
        eventually(app.state, lambda state: state["Port"] is None and "stopped sending" in state["Diagnostics"], "reset detected")
        fcntl.ioctl(raw.slave, termios.TIOCNXCL)
        raw.pause = False
        app.connected(raw.node)
        unplug(raw)

        # With missing sysfs USB IDs, the conventional alias is only a shortlist hint.
        fallback = add("ttyUSB6", 6, alias, vendor=None, product=None)
        if os.geteuid() != 0:
            original_mode = os.stat(fallback.endpoint).st_mode & 0o777
            os.chmod(fallback.endpoint, 0)
            eventually(app.state, lambda state: state["Status"].startswith("ttyUSB6:") and "access denied" in state["Status"].lower(),
                "permission failure diagnosed")
            os.chmod(fallback.endpoint, original_mode)
        app.connected(fallback.alias)
        # Legacy settings without AutoDetect retain the manually selected port.
        app.close()
        fcntl.ioctl(other.slave, termios.TIOCNXCL)
        (config / "Voltra/settings.json").write_text(json.dumps({"Port": str(other.node)}))
        app = App(dev, sysfs, config)
        assert app.connected(other.alias)["AutoDetect"] is False
        print("PASS: cold plug, hot plug, renumbering, reset, busy recovery, firmware handshake, "
              "exclusive port open, OLED data, manual selection, Auto persistence and legacy preferences")
        print("PASS: permission recovery" if os.geteuid() != 0 else "SKIP: permission recovery requires a non-root user")
    finally:
        if app:
            app.close()
        for port in active[:]:
            unplug(port)


if __name__ == "__main__":
    with tempfile.TemporaryDirectory(prefix="voltra-serial-") as directory:
        main(Path(directory))
