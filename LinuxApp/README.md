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
4. Start playback in the apps you want to control. On **Dashboard → Edit mappings**, select an application or enter its ID, executable name, or name, then save the mapping. **Master output** follows the current default output. Soft takeover waits for the physical knob to reach or cross the current volume. Immediate takeover acts after the movement threshold.

**OLED** supports mixer levels, clock/date, weather, local images and GIFs, and PC uptime. With an OLED connected, the preview shows the latest idle framebuffer acknowledged over serial. Without an OLED, it shows the local framebuffer. The controller briefly overlays knob movement on the physical display. Windows and Linux share the page layout, mixer bitmap font and image dithering. Linux uses Microsoft Selawik 1.01 under the SIL Open Font License for the Segoe UI layout. Standalone behavior after a disconnect depends on the installed firmware, and weather needs network access. Application updates and firmware version detection are available from **Settings**. Application updates link to the Linux package for manual installation. Flash the controller with your existing firmware workflow.

**Launch Voltra at system startup** creates an XDG Autostart entry at `$XDG_CONFIG_HOME/autostart/voltra.desktop` (normally `~/.config/autostart/voltra.desktop`). It passes `--minimized` to the same executable. A desktop with an active StatusNotifier host, such as KDE Plasma or GNOME with an AppIndicator extension, can restore Voltra from its tray menu. Other sessions open the main window so its controls are still accessible. Keep the executable at its configured path for startup to find it.

## Build and package

From the repository root, use the .NET 10 SDK.

```sh
dotnet run --project LinuxApp/LinuxApp.csproj
dotnet publish LinuxApp/LinuxApp.csproj -c Release -r linux-x64 --self-contained true -o publish/linux-x64
```

See [RELEASING.md](../RELEASING.md#linux-releases) for the portable archive and GitHub release naming.
