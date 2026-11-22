# Windows dynamic mappings

Choose **Game** or **Currently Focused** for any knob in **Dashboard → Edit mappings**. The adjacent display name remains editable. The dashboard shows the resolved application on a second line. An inactive channel shows a dash for volume. The OLED uses the existing short-label protocol. The firmware's temporary knob overlay continues to show physical position.

**Game** recognizes matching Unity player/data directories and typical Unreal Win64 shipping builds. Minecraft Java recognition requires a Java process with a matching GLFW or LWJGL game window and title. Focus the game once if it uses that path. The focused recognized game wins. After switching to another application, Voltra retains the most recently selected eligible game. With several candidates and no selection history, focus the game you want.

Engine signatures can miss custom builds and can match non-game software built with a game engine. **Recognized games… → Add game…** accepts the exact executable that owns playback. The outer **Save mappings** saves the list. A generic Java, Python, Node or Electron runtime can run unrelated applications, so its path alone cannot register a game.

**Currently Focused** matches the foreground process to playback sessions across active render endpoints using its Windows application ID or full executable path. It also recognizes neighboring audio, renderer or helper executables with matching product and company metadata. Generic runtimes use process-specific identities. Unidentifiable helpers, inaccessible processes, desktop windows, system dialogs and applications without a matching playback session leave the channel inactive. Browser tabs and profiles using one executable can share a target.

**Soft takeover** re-arms when the binding changes or Windows adjusts a member session. The gauge follows a newly joined session or the last session changed externally. With multiple new sessions, endpoint and session identifiers determine the initial order. After pickup, the knob moves all member sessions by the same percentage-point delta, up to each session's 0–100% limit. **Immediate** sets the member sessions to the knob position on qualifying movement. A focus switch alone never sends a dynamic volume write.

## Verification

Run the portable tests with the .NET 10 SDK from the repository root.

```powershell
dotnet run --project tests/WindowsMappings -c Release
```

The tests use the production resolver, session bindings, takeover rules and arc geometry with simulated playback sessions. The separate paint test opens a WinForms window and exercises the production gauge during resize, minimize/restore, theme changes and rapid value/label changes.

```powershell
dotnet run --project tests/WindowsPaint -c Release
```

For a desktop acceptance test, run the paint test at 100%, 125% and 150% Windows display scaling. Open Voltra with the physical mixer, switch between Dashboard and Settings, and verify its tray and window icons. Assign the two dynamic modes, save, restart, and alternate between two audio-producing applications and the desktop. In Soft mode, change a target from 20% to 80% in Windows Volume Mixer while the knob is at 20%. Verify that the level holds until pickup. Repeat in Immediate mode, then close and reopen the target while moving the knob. Compare the dashboard, Windows Volume Mixer and physical OLED.
