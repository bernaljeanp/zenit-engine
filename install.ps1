# Installs Zenit Engine: publishes it self-contained (no .NET runtime needed afterwards) to
# %LOCALAPPDATA%\Programs\ZenitEngine and registers the Engine to start elevated at sign-in.
# Needs admin rights once per run; it elevates itself.
#   .\install.ps1              install or update
#   .\install.ps1 -Uninstall   remove the program and the sign-in task (keeps %LOCALAPPDATA%\Zenit)
param(
    [switch]$Uninstall,
    [switch]$Elevated  # internal: set on the self-elevated relaunch
)
$ErrorActionPreference = 'Stop'

# Check the Administrator role, not the group name: the name is localized ("Administradores" on Spanish Windows).
$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    # Relaunch elevated exactly once; a relaunch that still isn't admin stops instead of relaunching again.
    if ($Elevated) { throw 'Elevation did not grant administrator rights; aborting.' }
    $relaunch = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Elevated')
    if ($Uninstall) { $relaunch += '-Uninstall' }
    Start-Process powershell -Verb RunAs -Wait -ArgumentList $relaunch
    Get-Content (Join-Path $env:TEMP 'zenit-install.log') -ErrorAction SilentlyContinue
    return
}

$Log = Join-Path $env:TEMP 'zenit-install.log'
trap { "ERROR: $_" | Out-File $Log; throw } # the elevated window closes on failure; the caller reads the log

$Target = Join-Path $env:LOCALAPPDATA 'Programs\ZenitEngine'
$Exe = Join-Path $Target 'ZenitEngine.exe'

# Stop the running Engine so its files can be replaced or removed.
Get-ScheduledTask -TaskPath '\Zenit\' -TaskName Engine -ErrorAction SilentlyContinue | Stop-ScheduledTask
Get-CimInstance Win32_Process -Filter "Name = 'ZenitEngine.exe'" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }

if ($Uninstall) {
    Unregister-ScheduledTask -TaskPath '\Zenit\' -TaskName Engine -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item $Target -Recurse -Force -ErrorAction SilentlyContinue
    "Zenit Engine removed. Your settings in $env:LOCALAPPDATA\Zenit were kept." | Tee-Object -FilePath $Log
    return
}

dotnet publish (Join-Path $PSScriptRoot 'src\ZenitEngine') -c Release -r win-x64 --self-contained -o $Target -nologo -v q
if ($LASTEXITCODE) { throw 'dotnet publish failed (is the .NET 10 SDK installed?)' }

# The Engine runs elevated in this user's session at sign-in. Interactive logon type: no password is stored and
# there is no UAC prompt at each sign-in. conhost --headless keeps the console app from opening a window, which
# would also kill the Engine if someone closed it.
$action = New-ScheduledTaskAction -Execute (Join-Path $env:WINDIR 'System32\conhost.exe') -Argument "--headless `"$Exe`" --serve"
$trigger = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$principal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskPath '\Zenit\' -TaskName Engine -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskPath '\Zenit\' -TaskName Engine

"Zenit Engine installed in $Target and running." | Tee-Object -FilePath $Log
