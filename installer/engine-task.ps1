# Registers or removes the Engine's sign-in task. Shared by the Inno Setup installer and install.ps1; runs elevated.
#   engine-task.ps1 -Exe "C:\Program Files\Zenit Engine\ZenitEngine.exe"   register and start
#   engine-task.ps1 -Stop                                                    stop the Engine and the app (before an update)
#   engine-task.ps1 -Uninstall                                               stop and remove the task
param([string]$Exe, [switch]$Stop, [switch]$Uninstall)
$ErrorActionPreference = 'Stop'

Get-ScheduledTask -TaskPath '\Zenit\' -TaskName Engine -ErrorAction SilentlyContinue | Stop-ScheduledTask
Get-CimInstance Win32_Process -Filter "Name = 'ZenitEngine.exe' OR Name = 'ZenitEngine.App.exe'" | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
if ($Stop) { return }
if ($Uninstall) {
    Unregister-ScheduledTask -TaskPath '\Zenit\' -TaskName Engine -Confirm:$false -ErrorAction SilentlyContinue
    return
}

# Early builds installed per user under LocalAppData, a folder any unelevated program can write to, which an elevated
# sign-in task must never run from. Remove that copy and its Start menu entry.
Remove-Item (Join-Path $env:LOCALAPPDATA 'Programs\ZenitEngine') -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Zenit Engine.lnk') -Force -ErrorAction SilentlyContinue

# The Engine runs elevated in this user's session at sign-in. Interactive logon type: no password is stored and there
# is no UAC prompt at each sign-in. conhost --headless keeps the console app from opening a window, which would also
# kill the Engine if someone closed it.
# ponytail: registered for the account running setup; a standard user elevating with another account's credentials
# gets the task under that account. Pass the signed-in user from the installer if that case shows up.
$user = "$env:USERDOMAIN\$env:USERNAME"
$action = New-ScheduledTaskAction -Execute (Join-Path $env:WINDIR 'System32\conhost.exe') -Argument "--headless `"$Exe`" --serve"
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew
Register-ScheduledTask -TaskPath '\Zenit\' -TaskName Engine -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskPath '\Zenit\' -TaskName Engine
