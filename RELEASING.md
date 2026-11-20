# Releasing Voltra

Set the release version in the `Version` property in the root `Directory.Build.props`.
The SDK uses it for executable metadata and About reads it from the assembly.
Tag the matching stable GitHub release `vMAJOR.MINOR.PATCH`.

The Windows application reads these defaults from the same file. A release build
can override them with MSBuild properties.

| Property | Value |
| --- | --- |
| `VoltraUpdateRepository` | `zixxi/release-voltra` |
| `VoltraUpdatePublicKey` | The existing RSA-4096 release-signing public key, encoded as base64 DER SubjectPublicKeyInfo |
| `VoltraProjectLicense` | `PolyForm-Noncommercial-1.0.0` |

The build embeds these values in its metadata. Keep the private signing key
outside the repository and distribution environment.

## GitHub release assets

Upload these assets to the stable release. Replace `VERSION` and `ARCH` with the
exact three-part version and one of `x64`, `arm64`, or `x86`, matching the
Windows build that will receive the update.

- `Voltra-VERSION-win-ARCH.exe`
- `Voltra-VERSION-win-ARCH.exe.json`
- `Voltra-VERSION-win-ARCH.exe.json.sig`

The UTF-8 manifest contains the following fields. Compute the hash and byte
length from the final installer, after any Authenticode signing.

| JSON field | Value |
| --- | --- |
| `product` | `Voltra` |
| `repository` | Exact `owner/repository` value embedded in the build |
| `version` | Exact version without the `v` prefix |
| `asset` | Installer filename |
| `size` | Installer size in bytes, as a JSON number |
| `sha256` | Hex SHA-256 digest of the installer |

Sign the exact manifest bytes using RSA-PSS with SHA-256 and a 32-byte salt.
Upload the raw binary signature as the `.sig` asset. For example, with OpenSSL:

```sh
openssl pkey -in release-private.pem -pubout -outform DER | openssl base64 -A
openssl dgst -sha256 -sigopt rsa_padding_mode:pss -sigopt rsa_pss_saltlen:digest \
  -sign release-private.pem -out "$manifest.sig" "$manifest"
```

The public-key pin is embedded at build time. A changed signing key requires a
release that establishes the new trust before it is used to sign subsequent
updates. Downloads use the configured repository's GitHub asset IDs and verify
the signed manifest before storing a complete package. A failed transfer or
integrity check never replaces a file.

## Windows release 0.9.3

On Windows, use the .NET 10 SDK and the existing Inno Setup project. Build the
flasher with the existing script and publish the x64 payload from the repository
root. Point Setup at `publish\win-x64`, including its `tools` subdirectory.

```powershell
powershell -NoProfile -File .\VoltraFirmware\Tools\Build-VoltraFlasher.ps1
dotnet publish .\WindowsApp\Voltra.csproj -c Release -r win-x64 --self-contained true -p:PlatformTarget=x64 -o .\publish\win-x64
```

Check that the published `Voltra.dll` reports assembly version `0.9.3.0`
before compiling the installer.

```powershell
[Reflection.AssemblyName]::GetAssemblyName((Resolve-Path .\publish\win-x64\Voltra.dll).Path).Version
```

Apply the [installer edits](Installer/INTEGRATION.md) to the existing script
before compiling it. The include waits for the previous Voltra process to
exit, synchronizes startup registration with a limited wait and cleans up the
Run entry on uninstall. Setup reports installation errors in its wizard. Select **Launch
Voltra** on the completion page to reopen the app after success. After
cancellation or a failure, reopen Voltra from Start to retry.

Once the installer is final, including any Authenticode signing, generate the
manifest and sign its exact bytes with the existing private key. Set
`$VoltraPrivateKeyPath` to that key's location outside the repository, then
run these commands in PowerShell with OpenSSL available.

```powershell
$package = Get-Item -LiteralPath .\Installer\Output\Voltra-0.9.3-win-x64.exe
$manifest = $package.FullName + '.json'
$release = [ordered]@{
  product = 'Voltra'
  repository = 'zixxi/release-voltra'
  version = '0.9.3'
  asset = $package.Name
  size = $package.Length
  sha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash
}
[IO.File]::WriteAllText($manifest, ($release | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
openssl dgst -sha256 -sign $VoltraPrivateKeyPath -sigopt rsa_padding_mode:pss -sigopt rsa_pss_saltlen:32 -out "$manifest.sig" $manifest
if ($LASTEXITCODE -ne 0) { throw 'Voltra manifest signing failed.' }
```

In `zixxi/release-voltra`, attach these three assets to a draft tagged
**`v0.9.3`**, then publish it as the latest stable app release.

- `Voltra-0.9.3-win-x64.exe`
- `Voltra-0.9.3-win-x64.exe.json`
- `Voltra-0.9.3-win-x64.exe.json.sig`

### Testing the installer and accent picker on Windows

1. Update an installed 0.9.2 with the 0.9.3 installer. Test a manual install
   and, once the signed release is available, **Update now** in 0.9.2. Launch
   the resulting app and check **Version 0.9.3** in About.
2. Check that Setup reaches its completion page with **Launch Voltra**
   unchecked and checked. Run Setup with `/LOG` to inspect the startup
   synchronization result. Verify that the saved startup preference and
   mixer settings survived the upgrade.
3. With normal Voltra running, run the command below. Expected results are
   exit code zero, the same running mixer instance and no extra window or
   tray icon. Repeat with the startup setting off. Check the
   `Voltra` value in `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`
   against the saved preference.
4. In Settings, open the accent picker from the preview square. Change hue
   and brightness, edit HEX, and apply. Verify that Settings and the tray
   follow the selection after reopening the app. Repeat with **Cancel**,
   Escape and an outside click. Try invalid HEX, Dark, Light and System
   themes, and a monitor edge.
5. Confirm tray restore, audio, OLED and the existing ESP32 firmware flow
   with the test device. Enable startup and uninstall. Check that the Run
   value was removed and the settings file was retained.

```powershell
$exe = Join-Path $env:LOCALAPPDATA 'Programs\Voltra\Voltra.exe'
$sync = Start-Process -FilePath $exe -ArgumentList '--sync-startup' -PassThru
if (!$sync.WaitForExit(5000)) { throw 'Startup synchronization did not exit in five seconds.' }
if ($sync.ExitCode -ne 0) { throw "Startup synchronization returned $($sync.ExitCode)." }
```

## Waveshare ESP32-S3-Zero firmware

Set `FIRMWARE_VERSION` in `VoltraFirmware/VoltraFirmware.ino`. This is independent
of the Windows version. Leave the hardware and protocol identifiers in place
unless you are shipping a deliberately incompatible device generation.

Install Espressif's Arduino ESP32 core **3.3.7** and the **U8g2** library. Build the
sketch from the project root, enabling native Hardware CDC/JTAG, USB CDC on boot,
4 MB flash, QSPI PSRAM and the default partition layout:

```sh
arduino-cli compile --fqbn esp32:esp32:esp32s3:USBMode=hwcdc,CDCOnBoot=cdc,FlashSize=4M,PartitionScheme=default,PSRAM=enabled --build-path firmware-build VoltraFirmware
```

Use **`VoltraFirmware.ino.bin`** as the update image. The generated `flash_args`
records the initial-install flash locations and settings. This configuration
uses DIO at 80 MHz and the following offsets.

| Offset | Image | Purpose |
| --- | --- | --- |
| `0x0000` | `VoltraFirmware.ino.bootloader.bin` | Bootloader for initial installation |
| `0x8000` | `VoltraFirmware.ino.partitions.bin` | Partition table for initial installation |
| `0xE000` | `boot_app0.bin` from the Arduino core | Boot selection data for initial installation |
| `0x10000` | `VoltraFirmware.ino.bin` | Application update, capacity `0x140000` bytes |

The updater reads the ROM MAC and 4 MB flash size, authenticates the installed
partition table by comparing the SHA-256 of its first `0xC00` bytes with the
built table, and writes only the application at `0x10000`. The running and boot
partitions must both be at that offset before the device enters download mode.
The shipped layout has a 3072-byte partition table with digest
`148b959cbff1c38aa8e1d5c0ba9d612c54997b945e56a63f41223eef650653a1`.
A layout change requires a separate migration and compatibility decision.
The `.merged.bin` image is for full installations and cannot be a release asset
for in-app updates. The app-only approach preserves the NVS partition and its
OLED settings, but does not provide automatic rollback.

### Publish the signed firmware release

Use a dedicated public, stable GitHub Release with tag **`firmware-s3-zero`** in
`zixxi/release-voltra`. Set **Latest release** to off (REST `make_latest: "false"`) so
the Windows updater's latest release remains independent. Publish or refresh
these three assets on that release. The signed manifest provides the firmware
version, so neither the release tag nor the desktop version changes to publish
a firmware update.

- `Voltra-S3-Zero-Firmware-VERSION.bin` · renamed application image
- `firmware-manifest.json` · UTF-8 JSON
- `firmware-manifest.json.sig` · raw signature

Use the actual version, byte count, and digest. Calculate SHA-256 with
`Get-FileHash -Algorithm SHA256` on Windows or `sha256sum` on Linux.
Use the same release-signing key and RSA-PSS SHA-256 command as above,
with `manifest=firmware-manifest.json` when running the shell example.

| JSON field | Value |
| --- | --- |
| `product` | `Voltra` |
| `repository` | `zixxi/release-voltra`, or the exact repository configured for a fork build |
| `hardware` | `VOLTRA-S3-ZERO` |
| `protocolVersion` | JSON number `1` |
| `firmwareVersion` | Exact firmware version, in `MAJOR.MINOR.PATCH` form |
| `minimumAppVersion` | Earliest Windows version with this updater and compatible protocol |
| `flashSize` | JSON number `4194304` |
| `appOffset` | JSON number `65536` |
| `appCapacity` | JSON number `1310720` |
| `partitionTableOffset` | JSON number `32768` |
| `partitionTableSha256` | Lowercase hex SHA-256 from `VoltraFirmware.ino.partitions.bin` |
| `asset` | Exact application asset filename |
| `size` | Application asset length in bytes, as a JSON number |
| `sha256` | Actual hex SHA-256 of the application asset |
| `notes` | Optional firmware release notes as plain text |

Upload the new application asset first, then replace the two manifest assets.
Checks that encounter incomplete or mismatched assets fail verification and
will not offer an unverified binary. Keep previous assets as needed for service
recovery. Publish ordinary stable Windows releases separately.

### Package the flashing helper for Windows

Build the helper on a Windows packaging machine. This example uses Python 3.12
on the packaging machine and bundles the runtime into the executable:

```powershell
powershell -NoProfile -File .\VoltraFirmware\Tools\Build-VoltraFlasher.ps1
```

The script smoke-tests the frozen helper, copies it into the project, and builds
Voltra with the SHA-256 of the exact executable. MSBuild rejects a supplied
hash that differs from the bundled file. Normal builds also embed the bundled
file's hash automatically when present. The script accepts `-UpdatePublicKey`
to override the public key configured in `Directory.Build.props`.

For a manual or Authenticode-signed build, use these commands:

```powershell
py -3.12 -m venv .voltra-flasher
.\.voltra-flasher\Scripts\python.exe -m pip install esptool==5.1.0 pyserial==3.5 pyinstaller==6.16.0
.\.voltra-flasher\Scripts\python.exe -m PyInstaller --onefile --name VoltraFlasher --collect-data esptool --hidden-import serial.tools.list_ports_windows VoltraFirmware\Tools\voltra_flash.py
New-Item -ItemType Directory -Force WindowsApp\tools | Out-Null
Copy-Item dist\VoltraFlasher.exe WindowsApp\tools\VoltraFlasher.exe
.\WindowsApp\tools\VoltraFlasher.exe --protocol-version
$flasherHash = (Get-FileHash WindowsApp\tools\VoltraFlasher.exe -Algorithm SHA256).Hash
dotnet build WindowsApp\Voltra.csproj -c Release "-p:VoltraFlasherSha256=$flasherHash"
```

If the helper is Authenticode-signed, sign it before calculating that hash. Embed
the resulting hash in the desktop build using `VoltraFlasherSha256`. Place the
exact hashed executable alongside Voltra at `tools/VoltraFlasher.exe`. Package
the matching source and third-party licenses for the packaged esptool and its
dependencies through the official distribution. Preserve the helper path in the
installer. Use protected installation paths appropriate to the installer.
Voltra never downloads a flashing utility. The source archive ships the helper
source. A built Windows helper must be supplied by the release packager before
in-app flashing is enabled.

The current desktop build expects `VOLTRA_PROTOCOL 2` from the helper. Rebuild
the executable whenever `voltra_flash.py` changes. The build script handles the
hash and desktop rebuild. The signed GitHub release format is unchanged.

### USB recovery

A controller with the previous unversioned sketch needs one USB upload of the
current sketch in Arduino IDE. Use the normal board and flash settings above.
Subsequent in-app updates use the device's native USB download mode.

If Voltra is waiting for download mode, keep the board connected, hold **BOOT**,
tap **RESET**, and release **BOOT**. Voltra looks for the selected controller by
its ROM MAC even if Windows gives it a different COM port. If flashing has
finished but the board stays in download mode, tap **RESET** with BOOT released.
Voltra reports success only after the selected board reconnects and reports the
expected version.

If discovery fails, select **Copy details** in the update dialog. The report
includes the handed-off COM port, detected USB VID/PID pairs and ROM probe
results. For a persistent debug log, launch Voltra with
`VOLTRA_DIAGNOSTICS=1`. Voltra writes the log to
`%LOCALAPPDATA%\Voltra\mixer-diagnostics.log`.

If writing was interrupted and the old sketch no longer starts, close Voltra,
enter download mode using the same buttons, and upload a known-good Voltra
sketch using Arduino IDE and the same board and partition configuration. Leave
**Erase All Flash Before Sketch Upload** disabled to preserve NVS where possible.
If the updater rejects flash layout, secure boot or encryption, use the
recovery procedure for that device's actual provisioning. A standard app-only
update is not suitable for that device.
