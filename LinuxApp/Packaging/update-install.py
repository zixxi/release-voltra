"""Install a verified Voltra portable package on Linux. Uses only the Python standard library."""
import ctypes
import fcntl
import hashlib
import json
import os
import re
import select
import shutil
import stat
import subprocess
import sys
import tarfile
import tempfile
import time
from pathlib import Path

MARKER = "voltra-portable.json"
ROOT = "Voltra-linux-x64"
LIMIT = 2 * 1024**3
REQUIRED = {"LinuxApp", "LinuxApp.dll", "libcoreclr.so", "update-install.py", "tools/VoltraFlasher"}


def contents(directory):
    entries = set()
    for root, dirs, files in os.walk(directory, onerror=raise_error):
        for name in dirs + files:
            item = Path(root, name)
            mode = item.lstat().st_mode
            if not (stat.S_ISDIR(mode) or stat.S_ISREG(mode)):
                raise ValueError(f"Unsupported file type in installation: {item.name}")
            entries.add(item.relative_to(directory).as_posix())
    return entries - {MARKER}


def raise_error(error):
    raise error


def inventory(directory, version):
    entries = contents(directory)
    if not REQUIRED <= entries:
        raise ValueError("Incomplete Voltra portable package")
    (directory / MARKER).write_text(json.dumps({"format": 1, "version": version, "files": sorted(entries)}))


def validate(directory, version):
    if directory.is_symlink() or directory.resolve() != directory or (directory / MARKER).is_symlink():
        raise ValueError("Linked installations require a manual update")
    marker = json.loads((directory / MARKER).read_text())
    files = set(marker["files"])
    if marker["format"] != 1 or marker["version"] != version or not REQUIRED <= files or files != contents(directory):
        raise ValueError("This directory differs from a dedicated Voltra release. Extract the update to a new folder.")
    if not os.access(directory / "LinuxApp", os.X_OK) or not os.access(directory / "tools/VoltraFlasher", os.X_OK):
        raise ValueError("Missing executable permissions")


def writable_install(directory, version):
    if sys.version_info < (3, 9) or not hasattr(os, "pidfd_open"):
        raise ValueError("Automatic installation requires Python 3.9 or newer with Linux pidfd support")
    protected = ("/usr", "/etc", "/bin", "/sbin", "/var", "/lib", "/lib64", "/opt", "/boot")
    if directory == Path.home() or directory == directory.parent or any(directory.is_relative_to(p) for p in protected):
        raise ValueError("System-managed locations require a manual update")
    if any((parent / ".git").exists() for parent in (directory, *directory.parents)):
        raise ValueError("Development checkouts require a manual update")
    if directory.stat().st_uid != os.getuid() or directory.parent.stat().st_uid != os.getuid() or not os.access(directory.parent, os.W_OK | os.X_OK):
        raise PermissionError("Use a user-owned installation and parent directory, or update manually")
    validate(directory, version)


def unpack(stream, destination):
    seen = set()
    total = 0
    with tarfile.open(fileobj=stream, mode="r|gz") as archive:
        for member in archive:
            parts = member.name.split("/")
            if (parts[0] != ROOT or any(p in ("", ".", "..") for p in parts) or
                    any(ord(c) < 32 for c in member.name) or "\\" in member.name or
                    not (member.isdir() or member.isreg()) or member.issparse() or
                    member.name in seen or len(member.name) > 2048 or len(seen) >= 16000):
                raise ValueError("Unsafe or ambiguous archive member")
            seen.add(member.name)
            target = destination.joinpath(*parts[1:])
            if member.isdir():
                target.mkdir(mode=0o755, parents=True, exist_ok=True)
                continue
            total += member.size
            if total > LIMIT or member.size < 0 or member.size > 512 * 1024**2:
                raise ValueError("Unpacked size limit exceeded")
            target.parent.mkdir(parents=True, exist_ok=True)
            with archive.extractfile(member) as src, target.open("xb") as dst:
                shutil.copyfileobj(src, dst, 128 * 1024)
            target.chmod(0o755 if member.mode & 0o111 else 0o644)


def exchange(a, b):
    libc = ctypes.CDLL(None, use_errno=True)
    if not hasattr(libc, "renameat2"):
        raise OSError("Automatic updating requires Linux renameat2")
    if libc.renameat2(-100, os.fsencode(a), -100, os.fsencode(b), 2) != 0:
        code = ctypes.get_errno()
        raise OSError(code, os.strerror(code))
    # The exchange already succeeded. A subsequent directory sync error cannot
    # be treated as a failed swap, which would invert the rollback decision.
    for parent in {a.parent, b.parent}:
        try:
            fd = os.open(parent, os.O_RDONLY | os.O_DIRECTORY)
            try:
                os.fsync(fd)
            finally:
                os.close(fd)
        except OSError:
            pass


def install(directory, installed, archive, expected, version, parent_pid):
    if not re.fullmatch(r"(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*)){2}", version) or tuple(map(int, version.split("."))) <= tuple(map(int, installed.split("."))):
        raise ValueError("Expected a newer stable version")
    writable_install(directory, installed)
    lock = directory.parent / ("." + directory.name + ".update.lock")
    lock_fd = os.open(lock, os.O_RDWR | os.O_CREAT | os.O_NOFOLLOW, 0o600)
    fcntl.lockf(lock_fd, fcntl.LOCK_EX | fcntl.LOCK_NB)
    if os.getppid() != parent_pid:
        raise ValueError("Original application already closed")
    parent = os.pidfd_open(parent_pid)
    job = Path(tempfile.mkdtemp(prefix="." + directory.name + ".update-", dir=directory.parent))
    replacement = job / "package"
    exchanged = False
    stopped = False
    try:
        replacement.mkdir()
        with archive.open("rb") as stream:
            digest = hashlib.sha256()
            for block in iter(lambda: stream.read(256 * 1024), b""):
                digest.update(block)
            if digest.hexdigest() != expected.lower():
                raise ValueError("Downloaded archive changed after verification")
            stream.seek(0)
            unpack(stream, replacement)
        validate(replacement, version)
        print("READY", flush=True)
        if sys.stdin.readline().strip() != "COMMIT":
            return
        # Finish only after the main process has closed its serial and audio handles.
        if not select.select([parent], [], [], 90)[0]:
            raise TimeoutError("Voltra did not exit")
        stopped = True
        writable_install(directory, installed)
        exchange(directory, replacement)
        exchanged = True
        ack = job / "started"
        env = dict(os.environ, VOLTRA_UPDATE_ACK=str(ack), VOLTRA_UPDATE_VERSION=version)
        try:
            child = subprocess.Popen([str(directory / "LinuxApp")], cwd=directory, env=env, start_new_session=True,
                                     stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            deadline = time.monotonic() + 60
            while child.poll() is None and time.monotonic() < deadline:
                if ack.exists() and ack.read_text() == version:
                    exchanged = False
                    return
                time.sleep(.1)
            if child.poll() is None:
                child.terminate()
                try:
                    child.wait(5)
                except subprocess.TimeoutExpired:
                    child.kill()
                    child.wait()
            raise RuntimeError("New version did not confirm startup")
        except (OSError, RuntimeError):
            exchange(directory, replacement)
            exchanged = False
            raise
    except (OSError, RuntimeError, ValueError) as error:
        if stopped:
            log = directory.parent / ("." + directory.name + ".update-error.log")
            try:
                with tempfile.NamedTemporaryFile(mode="w", dir=directory.parent, delete=False) as report:
                    report.write(f"{error}\nTransaction directory: {job}\n")
                os.replace(report.name, log)
            except OSError:
                pass
            if not exchanged:
                subprocess.Popen([str(directory / "LinuxApp")], cwd=directory, start_new_session=True,
                                 env=dict(os.environ, VOLTRA_UPDATE_ROLLBACK="1"), stdin=subprocess.DEVNULL,
                                 stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        raise
    finally:
        if not exchanged:
            shutil.rmtree(job, ignore_errors=True)
        os.close(parent)
        os.close(lock_fd)


if __name__ == "__main__":
    try:
        if sys.argv[1] == "inventory":
            inventory(Path(sys.argv[2]), sys.argv[3])
        elif sys.argv[1] == "check":
            writable_install(Path(sys.argv[2]), sys.argv[3])
        elif sys.argv[1] == "install":
            install(Path(sys.argv[2]), sys.argv[3], Path(sys.argv[4]), sys.argv[5], sys.argv[6], int(sys.argv[7]))
        else:
            sys.exit(2)
    except (OSError, KeyError, ValueError, RuntimeError, EOFError, tarfile.TarError) as error:
        sys.exit(str(error))
