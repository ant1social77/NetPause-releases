#Requires -RunAsAdministrator
# Устанавливает NetPause в Program Files, добавляет ярлык в меню «Пуск»
# и включает автозапуск.
param([Parameter(Mandatory = $true)][string]$Src)

$ErrorActionPreference = 'Stop'
$dst = Join-Path $env:ProgramFiles 'NetPause'

# останавливаем работающую копию и ждём, пока она действительно завершится
$old = Get-Process -Name NetPause -ErrorAction SilentlyContinue
if ($old) {
    $old | Stop-Process -Force
    $old | Wait-Process -Timeout 15
}

# программу остановили принудительно, поэтому её блокировки, строгий режим и пометку
# «лимитная сеть» снимаем сами; после запуска новая версия вернёт всё по настройкам
$oldExe = Join-Path $dst 'NetPause.exe'
if (Test-Path $oldExe) {
    try { Start-Process $oldExe -ArgumentList '--cleanup' -Wait } catch { }
}
Remove-NetFirewallRule -Group 'NetPause' -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force $dst | Out-Null
Copy-Item (Join-Path $Src '*') $dst -Recurse -Force

$ws = New-Object -ComObject WScript.Shell
$lnk = $ws.CreateShortcut((Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\NetPause.lnk'))
$lnk.TargetPath = Join-Path $dst 'NetPause.exe'
$lnk.WorkingDirectory = $dst
$lnk.Save()

Start-Process (Join-Path $dst 'NetPause.exe') -ArgumentList '--autostart-on'
