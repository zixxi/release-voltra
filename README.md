# Voltra

Voltra controls application and master volume from a five-knob ESP32-S3 mixer.
The controller connects over USB serial and displays the selected idle page on
its 128×64 OLED. After a hardware volume adjustment the firmware briefly shows
the knob's label and percentage.

Voltra has a Windows Forms app in `WindowsApp/` and an Avalonia app in
`LinuxApp/`. See the [Linux setup and build guide](LinuxApp/README.md) for
PipeWire audio control, dependencies, and deployment.

## Visual Studio Community 2026

1. In **Visual Studio Installer**, select **Visual Studio Community 2026 → Modify**.
   Select the **.NET desktop development** workload and the **.NET 10** individual
   component. Install the selected components.
2. Extract this ZIP into a new, empty folder. Open **`Voltra.slnx`** in Visual Studio. The
   **`WindowsApp`** folder contains the C# source.
3. Visual Studio restores **NAudio.Wasapi** and **System.IO.Ports** from NuGet.
   If restore does not start, right-click the solution in Solution Explorer and
   select **Restore NuGet Packages**. The project already references both packages.
4. For a new ESP32, upload the included **`VoltraFirmware/VoltraFirmware.ino`** using Arduino IDE and your
   working ESP32-S3 board settings. This enables fast knob reports and the
   existing ESP32 identity handshake. Leave `OLED_MODE` set to
   `OLED_I2C_SETUP` for the four-pin display. If you adjusted calibration or knob
   direction in your local sketch, copy those settings into the new sketch first.
   An ESP32 already working with the previous app can use its current firmware.
5. Close Arduino Serial Monitor and stop the Python mixer so the COM port is free.
   Press **Ctrl+F5** in Visual Studio. The sidebar shows **Connected** after the
   app finds the serial port carrying the mixer data. In **Settings**, select
   a **Preferred port** to override automatic detection.

## Using Voltra

- **Apps**. Open **Edit mappings** on the Dashboard to edit all five knobs.
  Play audio in an app to have it appear in the list, type executable names
  manually, or choose **System volume** to control the default playback device.
  Save when done. Separate multiple executables with commas or
  semicolons. If the same executable is on multiple rows, the first row controls
  it. The label sets the name shown during knob movement. Automatic app name
  formatting is optional in **Settings**.
- **Appearance**. Choose Dark, Light, or System, then select an accent or enter
  a custom `#RRGGBB` color.
- **Windows Volume Sync**. Enabled by default in **Settings → Knob mapping**.
  The dashboard shows Windows volume. A short tick marks physical position when
  a knob is waiting to take control. With **Soft takeover**, move the knob to
  or across the current Windows level to regain control. With **Immediate**,
  move it by at least the configured **Knob movement threshold**. Turning off
  sync gives the physical knobs priority again. A dash means the mapped app has
  no audio session. When a knob controls multiple sessions, the gauge follows
  the most recently changed session until a hardware adjustment sets the group.
- **Idle page**. Choose mixer levels, clock and date, current weather, an image
  or GIF, or PC uptime. For weather, enter a city and click **Set city**.
  Weather uses Open-Meteo with Celsius and refreshes every 15 minutes while its
  page is active. The clock uses Windows local time.
- **Mixer channel separator**. In **OLED → Mixer levels**, choose **Automatic
  (Master)**, **Always separate channel 5**, or **Disabled**. The default places
  lines between Master and its neighboring rows. The preview and the physical
  OLED use the same image, including the last frame held after closing the app.
- **Images and GIFs**. Choose a local PNG, JPEG, BMP or GIF up to 20 MB and
  4096×4096. The image fits inside 128×64 with its aspect ratio preserved and is
  converted to monochrome. GIFs loop at up to about 5–6 OLED updates per second.
  Keep the source file in place for the app to load it again on restart.
- **Tray**. Minimize or click **X** to hide the window while keeping the ESP32
  connected. Double-click the tray icon to reopen it, or right-click the tray icon
  and choose **Exit** to release the port and quit.
- **Run again**. The app stores its settings in
  `%LOCALAPPDATA%\Voltra\settings.json` and reconnects automatically or to
  your selected port. On first launch, Voltra imports the previous
  `%LOCALAPPDATA%\PurpleMixer` settings and last known port if there are no
  Voltra settings. The previous files are kept and existing Voltra settings
  take precedence.
  You can also build the **Release** configuration
  and run `WindowsApp\bin\Release\net10.0-windows\Voltra.exe` from its
  output folder. A framework-dependent build uses the .NET 10 Desktop Runtime.

## About and updates

In an installed copy, **Settings → General → Launch Voltra at Windows startup**
controls whether Voltra starts in the system tray when you sign in. Open its
window from the tray icon or launch Voltra manually. Setup reuses the saved
startup preference on upgrades.

**Settings → About** shows the executable version and update status. Builds
configured with a public GitHub repository check shortly after startup and every
six hours. For a newer release with signed assets for your architecture,
the update dialog shows formatted release notes. **Update now** verifies the
package, closes Voltra and opens the Inno Setup wizard. See
[Release configuration](RELEASING.md) for build and release instructions.

## Device firmware updates

**Settings → Device firmware** shows the installed version on a compatible,
versioned Voltra controller. It checks the separately versioned firmware release
on GitHub. If an update is available, choose **Update firmware** and follow the
BOOT and RESET prompt. Keep USB connected during writing and verification.

In-app flashing requires the packaged flashing helper and the pinned release
signing key. An older sketch that reports no firmware version needs one manual
USB upload of the included sketch before it can receive in-app updates.
See [firmware releases and recovery](RELEASING.md#waveshare-esp32-s3-zero-firmware).

## OLED with the app closed

- **Mixer levels** shows the last received levels with a `HOLD` indicator.
- **Clock & date** keeps time from the last PC sync while the ESP32 stays powered.
  After loss of power it asks for a new sync.
- **Weather** falls back to Clock & date and returns to live weather on reconnect.
- **Image or GIF** holds the last received frame while powered. After a restart
  it can restore a saved still frame of the last synced media file.
- **PC uptime** shows that the PC is unavailable.

A normal tray exit signals the firmware immediately. After a crash or lost
connection, the ESP32 uses a 6.5-second timeout. The selected page is stored in
flash shortly after a change. This behavior requires firmware that supports the
standalone OLED protocol, such as the included sketch.

If knob data stops, exit Voltra from the tray and open Arduino Serial
Monitor at 115200. Turn a knob and check whether the `MIX,...` lines continue
and their values change. Close Serial Monitor before reconnecting Voltra.

For a connection or latency investigation, set
`VOLTRA_DIAGNOSTICS=1` in the app's environment before launching it.
The bounded log is `%LOCALAPPDATA%\Voltra\mixer-diagnostics.log`.
It records discovery, port opening, identity verification, reconnect errors,
input processing timing, page transition timing, and takeover changes. Desktop processing timing
starts at the received knob packet and excludes physical ADC and USB latency.

## Wiring

For each potentiometer, connect one outer leg to **3V3**, the other to **GND**,
and the middle leg (wiper) to its GPIO. All parts share ESP32 ground.

| Knob | ESP32-S3 GPIO | Example app |
| --- | --- | --- |
| 1 | 11 | Spotify |
| 2 | 7 | Discord |
| 3 | 8 | Minecraft Java |
| 4 | 9 | Chrome |
| 5 | 10 | System volume |

These assignments are for the Waveshare ESP32-S3-Zero.

### 128×64 OLED wiring

Check the display listing for its **controller, interface and supply voltage**.
The table assumes a module with a 3.3 V-compatible power input. Use the module
pin labels, since their order can vary.

If your 1.96-inch panel is listed as a 410×502 QSPI AMOLED (for example with a
CO5300 driver), use `OLED_OFF` to run the mixer while you identify the breakout.
That panel requires its own driver and wiring.

| Module signal | I²C OLED | SPI SSD1309 OLED |
| --- | --- | --- |
| VCC | 3V3 | 3V3 |
| GND | GND | GND |
| SDA / DIN / MOSI | GPIO13 (SDA) | GPIO8 (DIN / MOSI) |
| SCL / CLK / SCK | GPIO12 (SCL) | GPIO9 (CLK / SCK) |
| CS | — | GPIO10 |
| DC / A0 | — | GPIO11 |
| RES / RST | — | GPIO12 |

On some SPI modules, the clock and data pins are printed as SCL and SDA. Use the
SPI column if the module also exposes CS and DC and is configured for SPI.

For the four-pin I²C module, leave `OLED_MODE` set to `OLED_I2C_SETUP`. The
sketch checks 0x3C and 0x3D and starts with the SSD1306 driver unless a different
driver was saved. An I²C reply confirms that the display communicates. The
address alone cannot identify its controller.

1. Close the Windows mixer, then open Serial Monitor at 115200 after upload.
2. Send `?` to check the display. Look for an `OLED:` line among the knob data.
3. If an address replies but the screen is blank or garbled, send `1` for
   SSD1306, `2` for SH1106, or `3` for SSD1309. Wait to see the five bars after
   each try. When they look right, send `s` to save the selection across resets.
4. If neither address replies, check that VCC is on 3V3, GND on GND, SDA on
   GPIO13 and SCL on GPIO12. If the wiring matches, check the display's model and
   its specified power and bus requirements.

Close Serial Monitor before starting the Windows mixer again. For a 128×64
SSD1309 with SPI, select `OLED_SSD1309_SPI`. For testing without display
support, choose `OLED_OFF`. If using the optional SPI configuration on the
S3-Zero, remap its pins to avoid the potentiometer GPIOs.

## Arduino IDE

1. Install **esp32 by Espressif Systems** in Boards Manager and **U8g2** in
   Library Manager.
2. Open `VoltraFirmware/VoltraFirmware.ino`. You can put or rename the outer
   project folder anywhere. Arduino IDE requires the inner sketch folder and
   its primary `.ino` file to have the same name.
3. Select your ESP32-S3 board (or **ESP32S3 Dev Module** for a generic S3).
   For a native USB connector, enable **Tools → USB CDC On Boot**. If using a
   USB-to-UART bridge, use its COM port and leave USB CDC On Boot disabled.
4. Connect a USB data cable, select the COM port, then upload.
5. Optional test: set Serial Monitor to 115200 baud. You should see lines such
   as `MIX,0,32,50,75,100`. Close Serial Monitor before running the PC script.

## Optional Python mixer

Open PowerShell in the extracted folder and run the following with Python installed.
Use this as a fallback if you only want USB volume control.

```powershell
py -m pip install pyserial pycaw
py pc_mixer.py --list-apps
py pc_mixer.py COM5
```

Start audio in the apps before running `--list-apps`. Set their executable names
in `APPS` at the top of `pc_mixer.py` and change `COM5` to your board's port.
To rename the OLED rows, edit `APP_LABELS` in the sketch (up to seven ASCII
characters per row) and upload again. The example row named Game maps to
`javaw.exe`, which also covers other Java apps with that executable name.

Keep PowerShell open while using the mixer. If a knob works backward, set its
`REVERSED` entry to `true`. If it cannot reach 0% or 100%, adjust that knob's
`RAW_MIN` and `RAW_MAX`. Some travel near the top can read as 100% because
3.3 V exceeds the ESP32-S3 ADC's measurable range at its highest attenuation.
