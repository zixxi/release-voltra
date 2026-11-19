"""Set Windows app session volumes from the five ESP32 knobs."""

import argparse
import re
import time

import psutil
import serial
from _ctypes import COMError
from pycaw.pycaw import AudioUtilities

# One tuple per knob. Each knob can control several executable names.
# Edit these to match the output of: py pc_mixer.py --list-apps
APPS = (
    ("Spotify.exe",),                 # GPIO 4
    ("Discord.exe",),                 # GPIO 5
    ("chrome.exe",),                  # GPIO 6
    ("javaw.exe",),                   # GPIO 7, e.g. Minecraft Java
    ("vlc.exe",),                     # GPIO 15
)

PACKET = re.compile(rb"MIX(?:,[0-9]{1,3}){5}\r?\n")
REFRESH_SECONDS = 0.75


def parse_levels(line):
    if PACKET.fullmatch(line) is None:
        return None
    levels = tuple(int(field) for field in line.split(b",")[1:])
    return levels if max(levels) <= 100 else None


def app_sessions():
    for session in AudioUtilities.GetAllSessions():
        try:
            process = session.Process
            if process is not None:
                name = process.name()
                yield name, session
        except (psutil.Error, COMError):
            # The app can close between discovery and lookup.
            continue


def apply_levels(sessions, levels):
    for channel, volume in sessions:
        try:
            target = levels[channel] / 100.0
            if abs(volume.GetMasterVolume() - target) >= 0.005:
                volume.SetMasterVolume(target, None)
        except COMError:
            # A session or output device disappeared. The next scan rebuilds the list.
            continue


def run_mixer(port):
    channels = {
        exe.casefold(): index for index, names in enumerate(APPS) for exe in names
    }
    while True:
        try:
            with serial.Serial(port, 115200, timeout=0.5) as device:
                print(f"Connected to {port}. Waiting for knob data.", flush=True)
                sessions = []
                previous = None
                next_refresh = 0
                while True:
                    levels = parse_levels(device.read_until(size=32))
                    if levels is None:
                        continue
                    now = time.monotonic()
                    refreshed = now >= next_refresh
                    if refreshed:
                        next_refresh = now + REFRESH_SECONDS
                        sessions = []
                        try:
                            for name, session in app_sessions():
                                channel = channels.get(name.casefold())
                                if channel is not None:
                                    sessions.append((channel, session.SimpleAudioVolume))
                        except COMError:
                            # Windows can rebuild the audio endpoint during a device switch.
                            pass
                    if previous is None:
                        print("Receiving knob data. Ctrl+C to stop.", flush=True)
                    if refreshed or levels != previous:
                        apply_levels(sessions, levels)
                        previous = levels
        except serial.SerialException as error:
            print(f"{port}: {error}. Retrying in 2 seconds.", flush=True)
            time.sleep(2)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("port", nargs="?", help="ESP32 serial port, e.g. COM5")
    parser.add_argument("--list-apps", action="store_true", help="Show audio-session executable names")
    args = parser.parse_args()

    if args.list_apps:
        names = sorted({name for name, _ in app_sessions()}, key=str.casefold)
        print("\n".join(names) if names else "No app audio sessions. Start playback and retry.")
    elif args.port:
        run_mixer(args.port)
    else:
        parser.error("pass a COM port or --list-apps")


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        print("\nMixer stopped.")
