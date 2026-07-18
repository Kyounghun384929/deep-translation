; Deep Translation installer script (Inno Setup 6)
; Build: ISCC.exe setup.iss   (or run ..\build.ps1 -Installer)

#define MyAppName "Deep Translation"
#define MyAppVersion "1.6.4"
#define MyAppPublisher "DeepTranslation"
#define MyAppExeName "DeepTranslation.exe"

[Setup]
AppId={{B7E19F3C-9C64-4C1A-A3D2-4F0E2A7C9D11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={userpf}\Deep Translation
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\dist
OutputBaseFilename=DeepTranslation-Setup-{#MyAppVersion}
SetupIconFile=..\DeepTranslation\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional tasks:"
Name: "autostart"; Description: "Start automatically with Windows"; GroupDescription: "Additional tasks:"

[Files]
Source: "..\dist\DeepTranslation.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\Deep Translation"; Filename: "{app}\{#MyAppExeName}"
Name: "{userdesktop}\Deep Translation"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "DeepTranslation"; ValueData: """{app}\{#MyAppExeName}"" --autostart"; \
    Flags: uninsdeletevalue; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch Deep Translation"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 사용자 설정 파일은 남겨둔다 (%APPDATA%\DeepTranslation) — 재설치 시 유지
Type: filesandordirs; Name: "{app}"
