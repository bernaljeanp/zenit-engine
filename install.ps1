# Developer install from source: publishes Zenit Engine self-contained to Program Files and registers the Engine to
# start elevated at sign-in, the same layout as the release installer (installer\ZenitEngine.iss). Needs the .NET 10
# SDK and admin rights once per run; it elevates itself. Users should run the setup from GitHub Releases instead.
#   .\install.ps1              install or update
#   .\install.ps1 -Uninstall   remove the program and the sign-in task (keeps %LOCALAPPDATA%\Zenit)
param(
    [switch]$Uninstall,
    [switch]$Elevated  # internal: set on the self-elevated relaunch
)
$ErrorActionPreference = 'Stop'
$Log = Join-Path $env:TEMP 'zenit-install.log'
# trap covers the whole script wherever it sits, so $Log must exist before anything can fail.
trap { "ERROR: $_" | Out-File $Log; throw } # the elevated window closes on failure; the caller reads the log

# Check the Administrator role, not the group name: the name is localized ("Administradores" on Spanish Windows).
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    # Relaunch elevated exactly once; a relaunch that still isn't admin stops instead of relaunching again.
    if ($Elevated) { throw 'Elevation did not grant administrator rights; aborting.' }
    $relaunch = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated')
    if ($Uninstall) { $relaunch += '-Uninstall' }
    # WaitForExit, not -Wait: -Wait also waits for every descendant, and dotnet leaves its build servers running.
    (Start-Process powershell -Verb RunAs -PassThru -ArgumentList $relaunch).WaitForExit()
    Get-Content $Log -ErrorAction SilentlyContinue
    return
}

# Program Files: only administrators can write there, and the Engine runs elevated at sign-in.
$Target = Join-Path $env:ProgramFiles 'Zenit Engine'
$Task = Join-Path $PSScriptRoot 'installer\engine-task.ps1'
$Shortcut = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Zenit Engine.lnk'
$RunKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'

if ($Uninstall) {
    & $Task -Uninstall
    Remove-Item $Target -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $Shortcut -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty $RunKey -Name 'Zenit Engine' -ErrorAction SilentlyContinue
    "Zenit Engine removed. Your settings in $env:LOCALAPPDATA\Zenit were kept." | Tee-Object -FilePath $Log
    return
}

& $Task -Stop # files in use can't be replaced
# Start from an empty folder: publish copies a file only when it is newer than the one in place, and a previous
# install can leave files that look newer (installer timestamps), which would keep stale DLLs next to the new exes.
# The setup's uninstaller (unins000.*) stays: Settings > Apps uses it.
Get-ChildItem $Target -Exclude 'unins*' -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
foreach ($project in 'ZenitEngine', 'ZenitEngine.App') {
    dotnet publish (Join-Path $PSScriptRoot "src\$project") -c Release -r win-x64 --self-contained -o $Target -nologo -v q
    if ($LASTEXITCODE) { throw "dotnet publish $project failed (is the .NET 10 SDK installed?)" }
}
& $Task -Exe (Join-Path $Target 'ZenitEngine.exe')

$link = (New-Object -ComObject WScript.Shell).CreateShortcut($Shortcut)
$link.TargetPath = Join-Path $Target 'ZenitEngine.App.exe'
$link.WorkingDirectory = $Target
$link.Save()
# The app starts with Windows in the tray: temperature in the tooltip, profiles one click away.
Set-ItemProperty $RunKey -Name 'Zenit Engine' -Value "`"$(Join-Path $Target 'ZenitEngine.App.exe')`" --tray"

"Zenit Engine installed in $Target and running. Open it from the Start menu." | Tee-Object -FilePath $Log
