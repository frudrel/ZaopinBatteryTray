# Installs ZaopinBatteryTray.exe (from this folder) to %LOCALAPPDATA%\ZaopinBatteryTray,
# starts it with Windows, and launches it.
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'ZaopinBatteryTray.exe'
$dir = Join-Path $env:LOCALAPPDATA 'ZaopinBatteryTray'
$exe = Join-Path $dir 'ZaopinBatteryTray.exe'

Get-Process ZaopinBatteryTray -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item $src $exe -Force
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name ZaopinBatteryTray -Value ('"' + $exe + '"')

# Launch through Explorer so the app doesn't belong to this terminal.
explorer.exe $exe
Start-Sleep 3
$running = Get-Process ZaopinBatteryTray -ErrorAction SilentlyContinue
"installed to:  $exe"
"startup entry: " + (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run').ZaopinBatteryTray
"running:       " + [bool]$running
