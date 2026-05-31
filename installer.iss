; Mucka installer script — built by CI via:
;   ISCC.exe /DMyAppVersion=0.4.3 /DMyAppDir=path\to\publish installer.iss

#define MyAppName      "Mucka"
#define MyAppPublisher "Oliver 'kfsone' Smith"
#define MyAppExeName   "Mucka.exe"
#define MyAppURL       "https://github.com/kfsone/mucka"

[Setup]
AppId={{6E8A4F2C-3B1D-4E9A-8C7F-2A5D0E3B6F1C}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir=.
OutputBaseFilename=mucka-setup
SetupIconFile=Platforms\Windows\muckabase.ico
Compression=lzma
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyAppDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Windows App Runtime — required by MAUI/WinUI 3. Silent install; no-ops if already present.
Source: "windowsappruntimeinstall-x64.exe"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{tmp}\windowsappruntimeinstall-x64.exe"; Parameters: "--quiet"; StatusMsg: "Installing Windows App Runtime..."; Flags: waituntilterminated runhidden
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
