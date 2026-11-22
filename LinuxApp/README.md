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

**OLED** supports mixer levels, clock/date, weather, local images and GIFs, and PC uptime. With an OLED connected, the preview shows the latest idle framebuffer acknowledged over serial. Without an OLED, it shows the local framebuffer. The controller briefly overlays knob movement on the physical display. Windows and Linux share the page layout, mixer bitmap font and image dithering. Linux uses Microsoft Selawik 1.01 under the SIL Open Font License for the Segoe UI layout. Standalone behavior after a disconnect depends on the installed firmware, and weather needs network access. Application updates and firmware version detection are available from **Settings**. Application updates link to the Linux package for manual installation. Use **Settings → Device firmware** to check the controller separately.

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
