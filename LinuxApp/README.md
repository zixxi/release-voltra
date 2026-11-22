# Voltra for Linux

Voltra uses the existing five-knob ESP32-S3-Zero and 128×64 OLED over USB. The Linux app uses Avalonia and controls PipeWire's default output and application playback streams through `pipewire-pulse`. Its settings live in `$XDG_CONFIG_HOME/Voltra/`, falling back to `~/.config/Voltra/`.

## Run on Ubuntu or Fedora x86-64

Use a regular KDE Plasma or GNOME session with PipeWire, WirePlumber and X11 or XWayland. Most desktop installations already have the audio services and display libraries. Install the `pactl` client if missing.

| Distribution | Install `pactl` |
| --- | --- |
| Ubuntu | `sudo apt install pulseaudio-utils` |
| Fedora | `sudo dnf install pulseaudio-utils` |

Confirm that `pactl info` reports **PulseAudio (on PipeWire …)**. For minimal installations, install the distribution's PipeWire Pulse compatibility package and WirePlumber. Avalonia needs X11 libraries and fontconfig, plus XWayland for Wayland sessions. The self-contained package includes .NET.

1. Extract the Linux archive to a permanent directory and run `./LinuxApp` from that directory.
2. Connect the controller using a USB data cable. Voltra automatically checks native Espressif USB serial ports and prefers stable `/dev/serial/by-id/` paths. Select a port in **Settings → Device connection** if you are using a USB-serial bridge.
3. If the Dashboard reports a permissions problem, close other serial tools and check the port's group with `ls -l /dev/ttyACM0`, substituting your actual device. Add your user to the device's serial group if needed. On Ubuntu and Fedora it is commonly `dialout` (`sudo usermod -aG dialout "$USER"`). Log out and back in after changing membership. Run Voltra as your normal user.
4. Start playback in the apps you want to control. On **Dashboard → Edit mappings**, select a running application, or choose **Enter application ID…** to type an ID, executable name, or name. **Save mappings** applies all five rows. **Cancel** keeps your previous mappings. **System volume** follows the current default output. Soft takeover waits for the physical knob to reach or cross the current volume. Immediate takeover acts after the movement threshold.

**OLED** supports mixer levels, clock/date, weather, local images and GIFs, and PC uptime. With an OLED connected, the preview shows the latest idle framebuffer acknowledged over serial. Without an OLED, it shows the local framebuffer. The controller briefly overlays knob movement on the physical display. Windows and Linux share the page layout, mixer bitmap font and image dithering. Linux uses Microsoft Selawik 1.01 under the SIL Open Font License for the Segoe UI layout. Standalone behavior after a disconnect depends on the installed firmware, and weather needs network access. Application updates and firmware version detection are available from **Settings**. Portable releases support signed application updates and restart from Settings. Use **Settings → Device firmware** to check the controller separately.

**Launch Voltra at system startup** creates an XDG Autostart entry at `$XDG_CONFIG_HOME/autostart/voltra.desktop` (normally `~/.config/autostart/voltra.desktop`). It passes `--minimized` to the same executable. A desktop with an active StatusNotifier host, such as KDE Plasma or GNOME with an AppIndicator extension, can restore Voltra from its tray menu. Other sessions open the main window so its controls are still accessible. Keep the executable at its configured path for startup to find it.

### Serial connection troubleshooting

Choose **Automatic** in **Settings → Device connection**. Voltra prefers a persistent
`/dev/serial/by-id/` path when available and checks native Espressif USB devices
(`303a:1001`) for the Voltra serial handshake before connecting. It also checks
`/dev/ttyACM*` and `/dev/ttyUSB*` through sysfs. If a candidate does not answer
the handshake, reconnect it to make it eligible for another automatic probe.
Select a specific port to use manual mode.

The sidebar shows controller and audio readiness. The full connected port path and firmware version appear in Settings.

Use **Copy serial diagnostics** in the same section to see discovered ports and
connection attempts. Diagnostics omit USB serial numbers and are also written to
stderr when running Voltra from a terminal.

The desktop preview scales each 128×64 framebuffer pixel to a whole number of screen pixels, including at fractional display scales. Navigation, volume needles and mapping dialogs respect reduced motion on supported desktops. Set `VOLTRA_REDUCED_MOTION=1` before launching to request reduced motion explicitly.

## Update controller firmware

Use the native USB connector on a Waveshare ESP32-S3-Zero with working Voltra firmware, 4 MB flash, and its original application partition. Connect it to the same USB socket throughout. Close serial monitors and make sure your account can open the serial port after USB re-enumeration. The published x64 bundle includes its own Python runtime and flashing helper.

1. In **Settings → Device firmware**, check the installed version and select **Check for updates**. Voltra reads the dedicated GitHub tag `firmware-s3-zero`. It offers an update only for a newer compatible version from a verified manifest.
2. Select **Update firmware**, read the signed notes and select **Prepare update**. Voltra downloads and checks the application image before releasing its normal USB connection.
3. When prompted, hold **BOOT**, press and release **RESET**, then release **BOOT**. Voltra searches the original USB socket even if Linux assigns a different tty. It matches the ROM chip and MAC to the identity from the normal Voltra handshake. See the [Waveshare board guide](https://docs.waveshare.com/ESP32-S3-Zero).
4. After the ROM identity matches, select **Write firmware** to authorize writing. You can cancel up to this point. Keep power and USB connected while writing, checking flash, and reconnecting. If prompted, tap **RESET** with BOOT released.
5. Success requires the original controller to reconnect and report the expected version. The mixer and OLED resume through the normal connection manager.

### Verification and recovery

Voltra uses the same trusted public key, GitHub asset transport, SHA-256 and RSA-PSS verification as the Windows updater. It requires a signed manifest, its detached signature and the named application image. It checks product, repository, board ID, protocol, minimum app version, flash layout, byte length and digest. The packaged esptool 5.1.0 helper checks its copy of the image, the physical flash capacity and the SHA-256 of the installed partition table. Writes start at `0x10000` and are limited to the `0x140000`-byte application partition. The helper verifies flash contents before reset.

- **No update offered.** Check the installed version and the signed release version. Boards with a different layout, or without a readable hardware identity and native USB topology, cannot use this updater.
- **Bootloader not detected.** Keep the original USB port and cable, close other tools, check serial permissions and repeat the BOOT/RESET sequence. If canceled in download mode, release BOOT and tap RESET.
- **Missing or damaged helper.** Extract the complete Linux package again. The executable must match the digest embedded at build time. The temporary directory must allow execution.
- **Write interrupted or version unconfirmed.** Leave USB connected, release BOOT, tap RESET and check the installed version. A pending-write marker persists across restarts until the same MAC reports the expected version. If the old application still responds, retry the signed update. If the application no longer responds, this in-app updater cannot establish the required identity. Use your supervised USB recovery procedure and the known-good board-specific image and layout. Do not erase the chip as a recovery shortcut.
- **Diagnostics.** Read the reason in the dialog, use **Copy serial diagnostics** in Settings or launch from a terminal for helper errors. Firmware updates never run at startup.

## Build and package

From the repository root, use the .NET 10 SDK. Building the helper for packaging also requires Linux x64, CPython 3.12 with venv and pip, and Internet access to the pinned Python build inputs.

```sh
bash LinuxApp/Tools/build-flasher.sh
dotnet build LinuxApp/LinuxApp.csproj -c Release
dotnet publish LinuxApp/LinuxApp.csproj -c Release -r linux-x64 --self-contained true -o publish/linux-x64
```

To run the Linux serial regression test without hardware, use a regular Linux user:

```sh
dotnet build tests/linux-serial/Voltra.Linux.SerialTests.csproj -c Release
python3 tests/linux-serial/smoke.py
```

The helper build freezes the Linux launcher, the existing Voltra chip/layout/write verification code, esptool and its ESP32-S3 stub. Build inputs and wheel/source hashes are listed in `LinuxApp/Tools/flasher-requirements.lock`. The executable and build-specific digest live under `LinuxApp/obj/firmware-helper`. Publishing requires both and checks they match. The installed app also verifies a private copy before each use. Third-party notices are under `tools/notices` in the package.

To run the firmware regression tests:

```sh
dotnet run --project tests/linux-firmware/Voltra.Linux.FirmwareTests.csproj -c Release
# Optional: add -- /path/to/Voltra-S3-Zero-Firmware-0.1.1.bin for the genuine signed image test.
# In a CPython 3.12 environment with the locked helper dependencies installed:
python3 tests/linux-firmware/test_helper.py
```

The firmware tests use an existing signed public fixture, mock processes, and simulated serial hardware. They do not write to a physical controller.

## Dynamic audio mappings

Assign **Game** or **Currently Focused** to any knob in **Dashboard → Edit mappings**.
With an eligible stream, the Dashboard shows its name and volume. Both the mixer framebuffer and the movement overlay use the active label within the existing OLED character limit.

| Session | Currently Focused |
| --- | --- |
| X11 with EWMH | Reads the active window, its PID and optional GTK application ID through libxcb. Desktop and dock windows are ignored. |
| Sway on Wayland | Requires the session's SWAYSOCK and swaymsg. Reads the focused native or XWayland node from the compositor's IPC tree. |
| GNOME Wayland, Plasma Wayland and other compositors without an implemented provider | Shows “Focus unavailable”. Static mappings and independently identified games remain usable. |

Voltra uses the window's process lifetime, exact application IDs, Flatpak identity or corroborated executable identity to find related playback streams. It leaves an unresolved or ambiguous target inactive. Shared runtimes such as Wine and Java require additional identifying metadata. Sandboxed applications and launchers can hide it. There is no cross-desktop foreground window portal used by Voltra. Consult the [EWMH specification](https://specifications.freedesktop.org/wm/latest-single/) and [Sway IPC reference](https://man.archlinux.org/man/sway-ipc.7.en).

Game recognition uses the stream's game role or a Steam application ID corroborated by the game installation path. Wine/Proton games can also supply a game image path through the Wine command line. Launchers, non-Steam games and restricted processes can need a manual whitelist entry. The focused eligible game wins. Otherwise the last selected eligible game stays active. With multiple candidates and no selection history, move focus to the desired game or restrict the candidates with rules.

**Settings → Dynamic audio mappings** offers four independent lists. Game whitelist entries extend automatic recognition. An empty focused-app whitelist allows all identifiable apps. Each blacklist overrides its matching whitelist. Choose an audio-producing app, an installed desktop application ID, or browse to a native executable. Desktop IDs only match when exported by the application or its sandbox. Executable rules require a process whose executable can be identified. Running entries show the exact identity to use.

Volume is the arithmetic mean of the target's playback streams. A target or stream-membership change rebinds the existing takeover state at the current hardware position. Soft pickup preserves relative stream levels when applying a delta, limited to 0–100%. Immediate takeover writes the requested level to each stream after physical movement. External changes are observed through the existing PipeWire subscription and refresh. Voltra checks group identity and volume again before writing and rechecks focus before individual writes. The separate compositor and Pulse APIs cannot guarantee an atomic system-wide focus-and-volume operation.

## Desktop application updates

**Settings → About → Check for updates** reads the latest stable desktop release
and shows its notes. Choose **Download verified update**, then **Restart and
install** once preparation is complete. Voltra verifies the detached RSA-PSS
signature with the embedded desktop public key, then checks the exact archive
name, repository, version, byte count and SHA-256 digest. The installer from
the running application prepares a new directory. It waits for the running
process to exit, swaps the directories atomically on the same filesystem and
waits for the new version to confirm startup. A failed startup switches back
and restarts the previous copy. Settings stay in the normal XDG config folder.

| Installation | Update method |
| --- | --- |
| Dedicated, user-owned directory extracted from a signed release starting with 0.9.7 | In-app updates. Requires a writable parent, Python 3.9+ at `/usr/bin/python3`, Linux pidfds and atomic directory exchange on the installation filesystem. |
| Older archives, development builds, system or read-only directories | Download the verified release manually, extract it to a new directory, close the previous copy and launch the new application. |
| Directory with extra files or symbolic links | Move personal files outside the installation, or install the release in a fresh directory. |

If preparation fails, the running copy stays in place. If restart fails, check
the hidden `.<directory>.update-error.log` next to the installation. For recovery
after an interrupted swap, a retained `.<directory>.update-*/package` can contain
the previous installation. Close Voltra before moving an intact tree back into
place, or extract an official release into a new directory. Rerun
`./install-desktop.sh` if the installation path changes.

See [Unified desktop releases](../release-tools/README.md) to build, sign and
publish both packages in one release. Run the mapping tests with
`dotnet run --project tests/linux-mappings/Voltra.Linux.MappingTests.csproj -c Release`
and the portable transaction tests with `python3 tests/linux-update/test_install.py`.
