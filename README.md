<div align="center">

# Voltra

![Voltra Dashboard](assets/dashboard.png)

### Open Hardware. Personal Audio Control.

**A customizable, ESP32-powered desktop audio mixer for Windows.**

Control your applications, manage audio levels, and personalize your desktop experience using five physical knobs and an OLED display.

[Download](https://github.com/zixxi/release-voltra/releases) · [Source Code](https://github.com/zixxi/release-voltra) · [Report a Bug](https://github.com/zixxi/release-voltra/issues)

</div>

---

## About Voltra

Voltra is a DIY desktop audio controller designed for makers, developers, and technology enthusiasts.

Powered by the Waveshare ESP32-S3-Zero, Voltra combines physical volume controls with a customizable Windows application.

Each of the five potentiometers can be assigned to an audio source, allowing you to adjust volume levels without switching windows or opening the Windows volume mixer.

A compact 128×64 OLED display provides additional information directly on the device.

Voltra is designed to be affordable, customizable, and easy to modify.

The source code is available for personal projects, learning, and non-commercial modifications.

---

## Features

### Physical Audio Control

- Five independently configurable potentiometers.
- Control application audio levels directly from hardware.
- Assign different applications to individual channels.
- Control Windows master volume.
- Persistent application mappings.
- Responsive volume adjustments.

### Windows Volume Synchronization

Voltra supports synchronization between physical potentiometers and software volume controls.

When Windows changes an application's volume, Voltra can preserve that value until the corresponding physical knob is moved again.

Available control modes include:

- **Immediate:** The physical knob takes control immediately.
- **Soft Takeover:** The knob must reach the current software value before taking control.

These options help prevent unexpected volume jumps.

### OLED Display

Voltra supports a monochrome 128×64 OLED display.

Available pages include:

- Mixer Levels
- Time & Date
- Weather

![Voltra OLED Configuration](assets/oled.png)

The OLED interface is customizable through the desktop application.

When the Windows application closes, the ESP32 can continue displaying supported standalone pages.

### Desktop Application

- Modern Windows interface.
- Dark, Light, and System themes.
- Custom accent colors.
- Built-in accent color picker.
- Custom system tray integration.
- Automatic ESP32 connection.
- Configurable device connection settings.
- Windows startup integration.
- Persistent user settings.

### Updates

Voltra supports two independent update systems.

**Desktop application updates**

- GitHub Releases integration.
- Update notifications.
- Automatic installer downloads.
- Cryptographic release verification.
- Integrated Windows installer.

**ESP32 firmware updates**

- Firmware version detection.
- Firmware update notifications.
- USB firmware installation.
- Firmware integrity verification.
- Hardware compatibility checks.

Firmware updates are only performed when required.

---

## Hardware Requirements

| Component | Quantity | Description |
|---|---:|---|
| Waveshare ESP32-S3-Zero | 1 | Main microcontroller |
| B10K potentiometer | 5 | 10 kΩ linear potentiometer |
| OLED display | 1 | 128×64 monochrome I²C display |
| USB cable | 1 | Data-capable USB cable |
| Wires or custom PCB | As needed | Electrical connections |
| Knobs | 5 | Optional potentiometer caps |
| Enclosure | 1 | Optional 3D-printed housing |

### Supported Microcontroller

The current hardware configuration targets:

**Waveshare ESP32-S3-Zero**

- ESP32-S3 microcontroller
- 4 MB Flash
- 2 MB PSRAM
- Native USB connectivity

Other ESP32 variants may require firmware changes.

---

## Wiring Guide

### Potentiometers

Voltra uses five B10K linear potentiometers.

Each potentiometer requires three connections:

1. **3.3V** — supply voltage
2. **GPIO** — analog signal from the center terminal
3. **GND** — ground

Connect the center terminal (wiper) of each potentiometer to the corresponding GPIO.

### GPIO Mapping

The potentiometers are numbered from left to right when viewing the device in its normal operating orientation.

| Potentiometer | GPIO | Function |
|---|---|---|
| Potentiometer 1 | GPIO11 | Channel 1 |
| Potentiometer 2 | GPIO7 | Channel 2 |
| Potentiometer 3 | GPIO8 | Channel 3 |
| Potentiometer 4 | GPIO9 | Channel 4 |
| Potentiometer 5 | GPIO10 | Channel 5 |

All potentiometers share the ESP32's 3.3V and GND connections.

### Potentiometer Connection

```text
          B10K Potentiometer

             [ Knob ]
                |
          ┌───────────┐
          │           │
          └───────────┘
             |  |  |
             1  2  3
             |  |  |
            3V3 | GND
                |
               GPIO
```

Terminal 2 is the center terminal (wiper).

For consistent behavior, connect the outer terminals in the same orientation on every potentiometer.

Reversing the outer terminals reverses the direction of volume adjustment.

**Important:** Use 3.3V, not 5V, for the potentiometer signal circuit.

---

## OLED Display Wiring

Voltra supports a 128×64 monochrome OLED connected through I²C.

### Pin Connections

| OLED Pin | ESP32-S3-Zero |
|---|---|
| VCC | 3V3 |
| GND | GND |
| SCL | GPIO12 |
| SDA | GPIO13 |

### Connection Diagram

```text
OLED 128×64              ESP32-S3-Zero

VCC  ───────────────────  3V3
GND  ───────────────────  GND
SCL  ───────────────────  GPIO12
SDA  ───────────────────  GPIO13
```

Ensure that your OLED module supports 3.3V operation.

The exact OLED controller and I²C address must match the firmware configuration.

---

## Complete GPIO Reference

| Component | ESP32 GPIO |
|---|---|
| Potentiometer 1 | GPIO11 |
| Potentiometer 2 | GPIO7 |
| Potentiometer 3 | GPIO8 |
| Potentiometer 4 | GPIO9 |
| Potentiometer 5 | GPIO10 |
| OLED SCL | GPIO12 |
| OLED SDA | GPIO13 |

The GPIO assignments are defined in the firmware source and can be modified for custom hardware configurations.

---

## Software Installation

### Requirements

- Windows 10 or Windows 11
- USB-connected compatible ESP32 controller
- Voltra desktop application

### Installation

1. Open the [GitHub Releases](https://github.com/zixxi/release-voltra/releases) page.
2. Download the latest Windows installer.
3. Run the installer.
4. Follow the installation instructions.
5. Connect your ESP32-S3-Zero using a USB data cable.
6. Launch Voltra.

Voltra should automatically detect the connected controller.

If automatic detection fails, select the appropriate COM port in the application settings.

### First-Time Setup

After launching Voltra:

1. Open the Dashboard.
2. Confirm that the device shows as connected.
3. Assign your preferred applications to the available channels.
4. Rotate the potentiometers to verify volume control.
5. Customize the OLED display and application appearance.

Your settings are saved automatically.

---

## Installing ESP32 Firmware

The microcontroller requires Voltra firmware before it can communicate with the desktop application.

### Arduino IDE

1. Install Arduino IDE.
2. Install the ESP32 board support package.
3. Connect the Waveshare ESP32-S3-Zero.
4. Open:

   `VoltraFirmware/VoltraFirmware.ino`

5. Select the appropriate ESP32-S3 board configuration.
6. Configure the board's flash and PSRAM settings.
7. Install any libraries required by the sketch.
8. Select the correct COM port.
9. Compile and upload the firmware.

If the board does not enter programming mode automatically, use its BOOT and RESET buttons to enter download mode.

After uploading the firmware, restart the ESP32.

The device should now be detected by Voltra.

### Firmware Updates

After the initial firmware installation, compatible firmware updates can be installed through the Voltra desktop application.

Navigate to:

**Settings → Device firmware**

Select **Check for updates** to check for available firmware versions.

Follow the on-screen instructions when an update is available.

Do not disconnect the USB cable during firmware installation.

---

## Building Voltra from Source

Voltra consists of two main components:

- A Windows desktop application written in C#.
- ESP32 firmware developed using Arduino C++.

### Windows Application

Requirements:

- .NET 10 SDK
- Windows
- Visual Studio or another compatible development environment

Clone the repository:

```bash
git clone https://github.com/zixxi/release-voltra.git
```

Open the solution:

```text
Voltra.slnx
```

Or build using the .NET CLI:

```powershell
dotnet build .\WindowsApp\Voltra.csproj -c Release
```

To publish a self-contained Windows x64 build:

```powershell
dotnet publish .\WindowsApp\Voltra.csproj -c Release -r win-x64 --self-contained true -o .\publish\win-x64
```

### ESP32 Firmware

The firmware source is located at:

```text
VoltraFirmware/VoltraFirmware.ino
```

GPIO assignments and other hardware configuration values can be adjusted in the source code.

---

## Project Structure

```text
release-voltra/
│
├── WindowsApp/
│   ├── Voltra.csproj
│   ├── Program.cs
│   ├── DeviceLink.cs
│   ├── UpdateService.cs
│   └── ...
│
├── VoltraFirmware/
│   ├── VoltraFirmware.ino
│   └── Tools/
│
├── Installer/
│   └── Voltra.iss
│
├── Directory.Build.props
├── Voltra.slnx
├── README.md
├── RELEASING.md
└── LICENSE
```

---

## Custom Hardware

Voltra is designed to be customizable.

You can modify the firmware to support different GPIO assignments, alternative hardware layouts, or custom enclosures.

The project is intended to encourage experimentation with electronics, microcontrollers, and desktop software.

If you create a custom Voltra build, feel free to share your results with the community.

---

## Troubleshooting

### ESP32 is not detected

- Ensure that the USB cable supports data transfer.
- Verify that the correct firmware is installed.
- Check the selected COM port.
- Disconnect and reconnect the device.
- Check Windows Device Manager.

### Potentiometer does not respond

- Verify the GPIO assignment.
- Check the potentiometer's center terminal.
- Ensure a common ground connection.
- Verify the 3.3V supply.
- Inspect solder joints and wiring.

### OLED display remains blank

- Verify SDA and SCL connections.
- Check the display's power supply.
- Confirm that the module uses the correct I²C address.
- Ensure that the display controller is supported by the firmware.

### Firmware update fails

- Keep the USB cable connected.
- Follow the BOOT/RESET instructions.
- Ensure no other application is using the COM port.
- Retry after reconnecting the controller.

---

## Contributing

Contributions, bug reports, and suggestions are welcome.

You can:

- Report bugs through GitHub Issues.
- Suggest new features.
- Submit improvements.
- Share custom hardware designs.
- Create non-commercial modifications.

Please review the project's license before redistributing modified versions.

Commercial use of the source code requires separate permission from the copyright holder.

---

## License

Voltra is distributed under the:

**PolyForm Noncommercial License 1.0.0**

The license permits use, modification, and redistribution for non-commercial purposes, subject to its terms.

Commercial use is not granted under this license.

See the [LICENSE](LICENSE) file for the complete terms.

**Copyright © 2026 importsys.**

---

## Disclaimer

Voltra is an independent maker project.

The software and firmware are provided without warranty.

Building or modifying electronic hardware involves potential risks. Verify all wiring and electrical connections before powering the device.

Never connect 5V directly to an ESP32 GPIO.

---

<div align="center">

### Built for makers, by a maker.

**Voltra — Take control of your audio.**

Developed by **importsys**.

[GitHub](https://github.com/zixxi/release-voltra) · [Releases](https://github.com/zixxi/release-voltra/releases) · [Issues](https://github.com/zixxi/release-voltra/issues)

</div>