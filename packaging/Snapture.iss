; Inno Setup script for Snapture.
; Built by packaging\release.ps1, which passes MyAppVersion / SourceDir / OutputDir.
; Per-user install (no admin/UAC) so the in-app updater can install silently.

#define MyAppName "Snapture"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "publish"
#endif
#ifndef OutputDir
  #define OutputDir "dist"
#endif

[Setup]
; Keep this AppId stable across versions so updates upgrade in place.
AppId={{B7E9C3A2-5D41-4E8F-9A6C-2F1E0D3B8C74}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher=Snapture
DefaultDirName={localappdata}\Programs\Snapture
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=lowest
OutputDir={#OutputDir}
OutputBaseFilename=Snapture-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\src\Snapture.App\Snapture.ico
; Let the installer replace a running Snapture and restart it afterwards.
CloseApplications=yes
RestartApplications=yes
UninstallDisplayIcon={app}\Snapture.exe

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion

[Icons]
Name: "{autoprograms}\Snapture"; Filename: "{app}\Snapture.exe"
Name: "{autodesktop}\Snapture"; Filename: "{app}\Snapture.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Run]
; Relaunch after install. No skipifsilent, so an updater-triggered /SILENT
; install also brings Snapture back up.
Filename: "{app}\Snapture.exe"; Description: "Launch Snapture"; Flags: nowait postinstall
