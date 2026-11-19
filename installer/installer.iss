; Byterake installer (Inno Setup 6/7). Build everything with build.ps1, or by hand:
;   dotnet publish src\Byterake.App -c Release -o dist      -> dist\Byterake.exe
;   ISCC.exe installer\installer.iss                          -> Output\Byterake_Setup_v<version>.exe
#define MyAppName "Byterake"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Sarath"
#define MyAppURL "https://github.com/sarathlal-12/byterake"
#define MyAppExeName "Byterake.exe"

[Setup]
AppId={{6C3E9A51-4F0B-4B7D-9E2A-B7A1D3C58E42}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
VersionInfoVersion={#MyAppVersion}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
OutputDir=..\Output
OutputBaseFilename=Byterake_Setup_v{#MyAppVersion}
SetupIconFile=..\src\Byterake.App\Assets\byterake.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Installs to Program Files, so setup asks for admin once. Byterake itself still starts with normal rights.
PrivilegesRequired=admin
MinVersion=10.0.17763
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\dist\Byterake.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\catalog.json"; DestDir: "{app}"; DestName: "catalog.example.json"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\KNOWN-LIMITATIONS.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
; runasoriginaluser: Byterake starts with normal rights, never inherited admin from setup.
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM Byterake.exe"; Flags: runhidden; RunOnceId: "StopByterake"

; Activity logs in %LOCALAPPDATA%\Byterake\Logs are left in place on purpose: they are your record
; of what was cleaned. Delete that folder by hand if you don't need them.

[Messages]
FinishedLabel=Byterake is installed.%n%nIt runs with normal rights and asks before using admin rights. Nothing is cleaned until you confirm the exact count and size.
