# Changes to the existing Inno Setup script

Copy `VoltraIntegration.iss` next to the working `.iss` file and make these
edits in that script. The script was absent from the Voltra source archive.

1. Set the existing `MyAppVersion` definition to `"0.9.3"` and point
   `[Files]` at the freshly published 0.9.3 x64 payload.
2. In `[Setup]`, keep the existing AppId
   `{{605834B7-403B-4FAC-9BF3-04D5C5A90AE7}`, the per-user
   `DefaultDirName={localappdata}\Programs\Voltra` and
   `PrivilegesRequired=lowest`. Set the following directives and keep
   the existing installation mode.

   ```ini
   AppMutex=PurpleMixer.Desktop
   SetupMutex=Voltra.Setup
   UsePreviousAppDir=yes
   ArchitecturesAllowed=x64compatible
   ```

   Keep the existing output directory and set
   `OutputBaseFilename=Voltra-{#MyAppVersion}-win-x64`.
3. Add `Parameters: "--after-setup"` to the existing **Launch Voltra**
   entry in `[Run]` and retain its `postinstall nowait skipifsilent`
   flags so the checkbox controls the restart.
4. Add the include below at the end of the existing `[Code]` section.
   Add the `[Code]` heading once if the script has no such section.

   ```pascal
   #include "VoltraIntegration.iss"
   ```

Replace the old `VoltraIntegration.iss` with the supplied file. If the main
script also has a separate startup synchronization entry or hook, remove that
duplicate. Keep the existing launch checkbox from step 3.

The new include runs `Voltra.exe --sync-startup --result-file "<file>"` in
post-install with a reply file under Setup's temporary directory. Setup logs
the result and continues after the reply or after about three seconds. The
command can also be run directly as `Voltra.exe --sync-startup`. It returns
`0` on success, `1` on a synchronization or reply-file error, and `2` on
invalid arguments. The next normal launch reconciles startup again.

Keep the Run value under the app's control and retain
`{localappdata}\Voltra` during updates and uninstall. The uninstall hook
removes the Run value when it matches that installation's startup command.
