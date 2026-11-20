; Voltra Windows Installer
; Built with Inno Setup

#define MyAppName "Voltra"
#define MyAppVersion "0.9.1"
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
OutputBaseFilename=Voltra-0.9.1-win-x64

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