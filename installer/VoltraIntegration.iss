// Include this file in the existing installer's [Code] section.
const
  VoltraAppMutex = 'PurpleMixer.Desktop';
  VoltraRunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  VoltraRunValue = 'Voltra';
  SynchronizeAccess = $00100000;
  ProcessSignaled = 0;
  ErrorInvalidParameter = 87;

function VoltraOpenProcess(Access: Cardinal; InheritHandle: Integer; ProcessId: Cardinal): NativeInt;
  external 'OpenProcess@kernel32.dll stdcall setuponly';
function VoltraWaitForProcess(Handle: NativeInt; Milliseconds: Cardinal): Cardinal;
  external 'WaitForSingleObject@kernel32.dll stdcall setuponly';
function VoltraCloseHandle(Handle: NativeInt): Integer;
  external 'CloseHandle@kernel32.dll stdcall setuponly';

<event('InitializeSetup')>
function WaitForPreviousVoltra: Boolean;
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
    ProcessHandle := VoltraOpenProcess(SynchronizeAccess, 0, Cardinal(ProcessId));
    if ProcessHandle = 0 then
      { Voltra can exit before the wizard opens its process handle. }
      Result := DLLGetLastError = ErrorInvalidParameter
    else
    try
      Result := VoltraWaitForProcess(ProcessHandle, 30000) = ProcessSignaled;
    finally
      VoltraCloseHandle(ProcessHandle);
    end;
  end;

  if not Result then
    SuppressibleMsgBox('Setup could not confirm that Voltra closed. Exit Voltra from the system tray and run Setup again.',
      mbError, MB_OK, IDOK);
end;

<event('PrepareToInstall')>
function CheckVoltraBeforeInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  if CheckForMutexes(VoltraAppMutex) then
    Result := 'Exit Voltra from the system tray and click Install again.'
  else if (ExpandConstant('{param:VOLTRAPID|}') <> '') and
    (CompareText(RemoveBackslashUnlessRoot(WizardDirValue),
      RemoveBackslashUnlessRoot(ExpandConstant('{param:DIR|}'))) <> 0) then
    Result := 'Select the existing Voltra installation folder to continue the update.';
end;

<event('CurStepChanged')>
procedure SyncVoltraStartupAfterInstall(CurStep: TSetupStep);
var
  Started: Integer;
  Attempt: Integer;
  ResultFile: String;
  Status: AnsiString;
begin
  if CurStep <> ssPostInstall then
    Exit;

  ResultFile := ExpandConstant('{tmp}\voltra-startup-result.txt');
  if not Exec(ExpandConstant('{app}\Voltra.exe'), '--sync-startup --result-file "' + ResultFile + '"',
    ExpandConstant('{app}'), SW_HIDE, ewNoWait, Started) then
  begin
    Log(Format('Could not start Voltra startup synchronization. Code: %d', [Started]));
    Exit;
  end;

  // Never wait indefinitely on the installed executable, even if the payload is outdated.
  for Attempt := 1 to 60 do
  begin
    if LoadStringFromFile(ResultFile, Status) and ((Status = '0') or (Status = '1')) then
    begin
      DeleteFile(ResultFile);
      if Status = '0' then
        Log('Voltra Windows startup synchronized.')
      else
        Log('Voltra could not synchronize Windows startup.');
      Exit;
    end;
    Sleep(50);
  end;
  Log('Voltra startup synchronization did not reply within 3 seconds. Startup will be checked on the next normal launch.');
end;

<event('CurUninstallStepChanged')>
procedure RemoveVoltraStartupAfterUninstall(CurStep: TUninstallStep);
var
  Command: String;
begin
  if CurStep <> usPostUninstall then
    Exit;
  if RegQueryStringValue(HKCU, VoltraRunKey, VoltraRunValue, Command) and
    (CompareText(Command, '"' + ExpandConstant('{app}\Voltra.exe') + '" --minimized') = 0) then
    if not RegDeleteValue(HKCU, VoltraRunKey, VoltraRunValue) then
      SuppressibleMsgBox('Setup could not remove Voltra from Windows startup.',
        mbInformation, MB_OK, IDOK);
end;
