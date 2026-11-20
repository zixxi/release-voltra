# Inno Setup integration

Build from the repository root.

```powershell
.\Build-Installer.ps1
```

The script supplies `MyAppVersion` from `Directory.Build.props` to the
existing `Voltra.iss`. The installer reads its icon, license, and
`publish\win-x64` payload through paths relative to the installer source.
A successful build writes `Installer\Output\Voltra-VERSION-win-x64.exe`.

`Voltra.iss` retains the AppId `{605834B7-403B-4FAC-9BF3-04D5C5A90AE7}`
for upgrades and uses per-user installation. Its [Code] section handles
waiting for the previous Voltra process, Windows startup synchronization,
and removal of the startup entry on uninstall. The existing launch checkbox
and optional desktop shortcut are part of the same script.

Setup runs `Voltra.exe --sync-startup --result-file "<file>"`. It logs
the result or continues after about three seconds. The command also accepts
`Voltra.exe --sync-startup` on its own. It returns `0` on success, `1` on a
synchronization or reply-file error, and `2` on invalid arguments. The next
normal launch reconciles startup again. Keep user settings in
`{localappdata}\Voltra` across updates and uninstall.

See [RELEASING.md](../RELEASING.md) for manifest signing and publication.
