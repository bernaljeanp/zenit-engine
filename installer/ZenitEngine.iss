; Zenit Engine installer. Built by .github/workflows/release.yml from the self-contained publish output:
;   ISCC /DAppVersion=0.1.0 installer\ZenitEngine.iss
; Installs to Program Files (only administrators can write there, which matters: the Engine runs elevated at sign-in),
; registers the Engine's sign-in task, adds the Start menu entry and starts the app in the tray with Windows.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{1D70892E-5696-4629-9F48-F147768C7FD8}
AppName=Zenit Engine
AppVersion={#AppVersion}
AppVerName=Zenit Engine {#AppVersion}
AppPublisher=Zenit Engine contributors
AppPublisherURL=https://github.com/bernaljeanp/zenit-engine
AppSupportURL=https://github.com/bernaljeanp/zenit-engine/issues
AppUpdatesURL=https://github.com/bernaljeanp/zenit-engine/releases
DefaultDirName={autopf}\Zenit Engine
DisableProgramGroupPage=yes
DisableDirPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\dist
OutputBaseFilename=ZenitSetup-{#AppVersion}
SetupIconFile=..\src\ZenitEngine.App\Assets\zenit.ico
UninstallDisplayIcon={app}\ZenitEngine.App.exe
UninstallDisplayName=Zenit Engine
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=no
; The build machine runs on UTC: record file times as UTC so installed files don't get timestamps in the future.
TimeStampsInUTC=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"

[CustomMessages]
en.SettingUp=Setting up the Engine...
es.SettingUp=Configurando el Engine...

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "engine-task.ps1"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Zenit Engine"; Filename: "{app}\ZenitEngine.App.exe"

[Registry]
; The app starts with Windows in the tray: temperatures in the tooltip, profiles one click away.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Zenit Engine"; ValueData: """{app}\ZenitEngine.App.exe"" --tray"; Flags: uninsdeletevalue

[Run]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\engine-task.ps1"" -Exe ""{app}\ZenitEngine.exe"""; Flags: runhidden waituntilterminated; StatusMsg: "{cm:SettingUp}"
Filename: "{app}\ZenitEngine.App.exe"; Description: "{cm:LaunchProgram,Zenit Engine}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\engine-task.ps1"" -Uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveEngineTask"

[Code]
// An update replaces files the running Engine and app hold open: stop both first.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec('powershell.exe', '-NoProfile -Command "Stop-ScheduledTask -TaskPath ''\Zenit\'' -TaskName Engine -ErrorAction SilentlyContinue; Get-Process ZenitEngine,ZenitEngine.App -ErrorAction SilentlyContinue | Stop-Process -Force"',
    '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
