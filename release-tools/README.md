# Unified desktop releases

Both projects read `VoltraVersion` from `Directory.Build.props`. The release examples below target **0.9.8**, with tag **v0.9.8**.
Build the two artifacts from the same commit.

## Build and sign

On Linux x64, with the .NET 10 SDK and the helper prerequisites in the
[Linux README](../LinuxApp/README.md), run:

```bash
bash LinuxApp/Tools/build-release.sh v0.9.8
```

Copy `artifacts/Voltra-linux-x64-0.9.8.tar.gz` to the Windows build machine.
Use the same existing desktop release key kept outside the repository. OpenSSL
must be on PATH, or set `OPENSSL` to its absolute path. With Python and Inno
Setup installed, run in PowerShell:

```powershell
$env:VOLTRA_SIGNING_KEY = 'C:\path\to\existing-release-private.pem'
.\release.bat "C:\path\to\Voltra-linux-x64-0.9.8.tar.gz"
```

The wrapper uses the existing `Build-Installer.ps1`. If both packages already
exist, run `py -3 release-tools/prepare.py INSTALLER ARCHIVE --key PATH` on
either platform. The signing step reads the shared version and verifies both
signatures against the configured public key before writing the release folder.
It creates these seven upload assets:

| Asset | Purpose |
| --- | --- |
| `Voltra-0.9.8-win-x64.exe` | Windows x64 Inno Setup installer |
| `Voltra-0.9.8-win-x64.exe.json` | Windows update manifest |
| `Voltra-0.9.8-win-x64.exe.json.sig` | Windows RSA-PSS signature |
| `Voltra-linux-x64-0.9.8.tar.gz` | Linux x64 portable package |
| `Voltra-linux-x64-0.9.8.tar.gz.json` | Linux update manifest |
| `Voltra-linux-x64-0.9.8.tar.gz.json.sig` | Linux RSA-PSS signature |
| `SHA256SUMS.txt` | Checksums for manual verification |

Both updaters authenticate the platform-specific manifest with its signature.
Use the same tag convention for the shared three-part version,
for example `v0.9.8` or `v1.0.0`. Existing unprefixed Windows tags remain readable
for compatibility. Stable desktop builds skip prereleases. The firmware
updater continues to use the exact `firmware-s3-zero` tag and its own manifest.

## Publish after verification

Use the commit that produced both packages. Create a draft, check all seven
assets, then publish the same draft so clients see both platform packages together.
The local scripts only prepare files.

```powershell
$tag = 'v0.9.8'
$repo = 'zixxi/release-voltra'
git tag -a $tag -m "Voltra $tag"
if ($LASTEXITCODE -ne 0) { throw 'Tag creation failed.' }
git push origin "refs/tags/$tag"
if ($LASTEXITCODE -ne 0) { throw 'Tag upload failed.' }
$files = @(Get-ChildItem -LiteralPath "artifacts/$tag" -File | ForEach-Object FullName)
if ($files.Count -ne 7) { throw 'Expected both packages, four signature/manifest files and SHA256SUMS.txt.' }
gh release create $tag @files --repo $repo --verify-tag --draft --title "Voltra 0.9.8" --notes-file release-notes.md
if ($LASTEXITCODE -ne 0) { throw 'Draft creation failed.' }
gh release view $tag --repo $repo --json tagName,isDraft,assets
# After reviewing the assets and notes:
gh release edit $tag --repo $repo --draft=false --latest
gh api "repos/$repo/releases/latest" --jq .tag_name
```

Create `release-notes.md` before the draft command. To verify the public release,
download its seven assets and compare their hashes with the locally prepared
files. For manual installation, verify the signature against the trusted
desktop public key before comparing the package against the signed digest.

## Dynamic mappings

In Windows, **Settings → Dynamic audio mappings** uses full executable paths.
The Game whitelist uses the previous Known Games setting, so existing saved
entries carry over. Game detection still uses the existing Windows detectors.
A blacklist excludes the associated application identity and all its matching
audio sessions. An empty focused-app whitelist leaves all supported foreground
audio applications eligible. Windows uses WASAPI identities. Linux uses PipeWire
and supported desktop focus providers, so the exact application matching rules
are platform-specific.

## Verify the public Linux 0.9.7 → 0.9.8 update

1. Keep a dedicated, user-owned portable 0.9.7 installation on Ubuntu, outside
   a Git checkout. Keep backups outside the installation directory. Before
   publishing, launch its `./LinuxApp` and confirm 0.9.7 in Settings → About.
   Record the five channel mappings, theme, accent and device connection mode.
2. After publishing, confirm that `gh api repos/zixxi/release-voltra/releases/latest --jq .tag_name`
   prints `v0.9.8`. In the running 0.9.7 app, use Settings → About → Check for updates.
3. Select **Download verified update**. After verification and staging, select
   **Restart and install**. Let Voltra exit and reopen on its own.
4. Confirm **0.9.8** in About. Check for updates again. Check that mappings and
   preferences survived, the ESP32 reconnected, OLED output resumed and the
   knobs still control their assigned PipeWire applications.
5. If restart fails, record the running version and the adjacent
   `*.update-error.log` if present. Preserve the original installation for diagnosis.
