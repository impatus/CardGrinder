#ifndef AppVersion
  #error AppVersion must be provided by scripts/build-release.ps1
#endif

[Setup]
AppId={{D7F46D04-CF21-47D4-B864-9498DC9E0FA2}
AppName=CardGrinder
AppVersion={#AppVersion}
AppVerName=CardGrinder {#AppVersion}
AppPublisher=impatus
AppPublisherURL=https://github.com/impatus/CardGrinder
AppSupportURL=https://github.com/impatus/CardGrinder/issues
AppUpdatesURL=https://github.com/impatus/CardGrinder/releases
DefaultDirName={localappdata}\Programs\CardGrinder
DefaultGroupName=CardGrinder
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\artifacts\release
OutputBaseFilename=CardGrinder-Setup
SetupIconFile=..\src\CardGrinder\Assets\app.ico
UninstallDisplayIcon={app}\CardGrinder.exe
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
DisableProgramGroupPage=yes
AllowNoIcons=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
AppMutex=Local\CardGrinder.SingleInstance
CloseApplications=no
RestartApplications=no
Uninstallable=yes
UninstallDisplayName=CardGrinder
VersionInfoVersion={#AppVersion}
VersionInfoProductName=CardGrinder
VersionInfoDescription=CardGrinder installer
VersionInfoCopyright=Copyright (c) 2026 impatus

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\artifacts\CardGrinder-win-x64\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\CardGrinder"; Filename: "{app}\CardGrinder.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\CardGrinder"; Filename: "{app}\CardGrinder.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\CardGrinder.exe"; Description: "Launch CardGrinder"; Flags: nowait postinstall skipifsilent unchecked

; Saved Steam sessions/settings live outside {app}. Upgrades and uninstall preserve them.
; Sign out in the app before uninstalling to remove the remembered Steam session.
