; Voltra Windows Installer
; Built with Inno Setup
; Version 0.9.2

#define MyAppName "Voltra"
#define MyAppVersion "0.9.3"
#define MyAppPublisher "importsys"
#define MyAppURL "https://github.com/zixxi/release-voltra"
#define MyAppExeName "Voltra.exe"

[Setup]

; IMPORTANT: Never change this AppId between releases.
AppId={{605834B7-403B-4FAC-9BF3-04D5C5A90AE7}

AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}

AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; Per-user installation
DefaultDirName={localappdata}\Programs\{#MyAppName}
PrivilegesRequired=lowest

; Preserve installation location between upgrades
UsePreviousAppDir=yes

; Windows x64
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

; Application information
UninstallDisplayIcon={app}\{#MyAppExeName}
DisableProgramGroupPage=yes

; License
LicenseFile=C:\Users\Isys\Documents\github\release-voltra\LICENSE

; Handle running applications during upgrades
CloseApplications=yes
RestartApplications=yes

; Installer output
OutputDir=C:\Users\Isys\Documents\github\release-voltra\Installer\Output
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-win-x64

; Installer appearance
SetupIconFile=C:\Users\Isys\Documents\github\release-voltra\WindowsApp\Voltra.ico
WizardStyle=modern
SolidCompression=yes
Compression=lzma2

[Languages]

Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]

Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]

; Include all published Voltra files, dependencies and firmware flasher.
Source: "C:\Users\Isys\Documents\github\release-voltra\publish\win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]

; Start Menu
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

; Optional desktop shortcut
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]

; Offer to launch Voltra after installation
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Voltra"; Flags: nowait postinstall skipifsilent

[Code]

const
  VoltraAppMutex = 'PurpleMixer.Desktop';
  VoltraRunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  VoltraRunValue = 'Voltra';

  SynchronizeAccess = $00100000;
  ProcessSignaled = 0;
  ErrorInvalidParameter = 87;


function VoltraOpenProcess(
  Access: Cardinal;
  InheritHandle: Integer;
  ProcessId: Cardinal
): NativeInt;
  external 'OpenProcess@kernel32.dll stdcall setuponly';


function VoltraWaitForProcess(
  Handle: NativeInt;
  Milliseconds: Cardinal
): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall setuponly';


function VoltraCloseHandle(
  Handle: NativeInt
): Integer;
  external 'CloseHandle@kernel32.dll stdcall setuponly';


// Wait for the previous Voltra process during automatic updates.

function InitializeSetup: Boolean;
var
  Argument: String;
  ProcessId: Integer;
  ProcessHandle: NativeInt;
begin
  Argument := ExpandConstant('{param:VOLTRAPID|}');

  if Argument = '' then
  begin
    Result := True;
    Exit;
  end;

  Result := False;
  ProcessId := StrToIntDef(Argument, 0);

  if ProcessId > 0 then
  begin
    ProcessHandle := VoltraOpenProcess(
      SynchronizeAccess,
      0,
      Cardinal(ProcessId)
    );

    if ProcessHandle = 0 then
    begin
      Result := DLLGetLastError = ErrorInvalidParameter;
    end
    else
    begin
      try
        Result := VoltraWaitForProcess(
          ProcessHandle,
          30000
        ) = ProcessSignaled;
      finally
        VoltraCloseHandle(ProcessHandle);
      end;
    end;
  end;

  if not Result then
  begin
    SuppressibleMsgBox(
      'Setup could not confirm that Voltra closed. Exit Voltra from the system tray and run Setup again.',
      mbError,
      MB_OK,
      IDOK
    );
  end;
end;


// Verify that Voltra is not running before installation.

function PrepareToInstall(
  var NeedsRestart: Boolean
): String;
begin
  Result := '';

  if CheckForMutexes(VoltraAppMutex) then
  begin
    Result :=
      'Exit Voltra from the system tray and click Install again.';
  end
  else if
    (ExpandConstant('{param:VOLTRAPID|}') <> '') and
    (CompareText(
      RemoveBackslashUnlessRoot(WizardDirValue),
      RemoveBackslashUnlessRoot(
        ExpandConstant('{param:DIR|}')
      )
    ) <> 0)
  then
  begin
    Result :=
      'Select the existing Voltra installation folder to continue the update.';
  end;
end;


// Synchronize Windows startup settings after installation.
// Never wait indefinitely for the application to exit.

procedure CurStepChanged(
  CurStep: TSetupStep
);
var
  Started: Integer;
  Attempt: Integer;
  ResultFile: String;
  Status: AnsiString;
begin
  if CurStep <> ssPostInstall then
    Exit;

  ResultFile := ExpandConstant('{tmp}\voltra-startup-result.txt');

  // Remove any stale result from an earlier attempt.
  if FileExists(ResultFile) then
    DeleteFile(ResultFile);

  if not Exec(
    ExpandConstant('{app}\Voltra.exe'),
    '--sync-startup --result-file "' + ResultFile + '"',
    ExpandConstant('{app}'),
    SW_HIDE,
    ewNoWait,
    Started
  ) then
  begin
    Log(
      'Could not start Voltra startup synchronization. Code: ' +
      IntToStr(Started)
    );
    Exit;
  end;

  // Maximum wait: 60 * 50 ms = 3 seconds.
  for Attempt := 1 to 60 do
  begin
    if LoadStringFromFile(ResultFile, Status) then
    begin
      if (Status = '0') or (Status = '1') then
      begin
        DeleteFile(ResultFile);

        if Status = '0' then
        begin
          Log('Voltra Windows startup synchronized.');
        end
        else
        begin
          Log('Voltra could not synchronize Windows startup.');
        end;

        Exit;
      end;
    end;

    Sleep(50);
  end;

  Log(
    'Voltra startup synchronization did not reply within 3 seconds. ' +
    'Startup will be checked on the next normal launch.'
  );
end;


// Remove Windows startup registration after uninstallation.

procedure CurUninstallStepChanged(
  CurStep: TUninstallStep
);
var
  Command: String;
begin
  if CurStep <> usPostUninstall then
    Exit;

  if RegQueryStringValue(
    HKCU,
    VoltraRunKey,
    VoltraRunValue,
    Command
  ) then
  begin
    if CompareText(
      Command,
      '"' + ExpandConstant('{app}\Voltra.exe') +
      '" --minimized'
    ) = 0 then
    begin
      if not RegDeleteValue(
        HKCU,
        VoltraRunKey,
        VoltraRunValue
      ) then
      begin
        SuppressibleMsgBox(
          'Setup could not remove Voltra from Windows startup.',
          mbInformation,
          MB_OK,
          IDOK
        );
      end;
    end;
  end;
end;