# Creates "Handshake" shortcuts on the Desktop and in the Start Menu.
# Usage: npm run shortcut   (or)   powershell -ExecutionPolicy Bypass -File tools\create-shortcut.ps1

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$electron = Join-Path $root 'node_modules\electron\dist\electron.exe'
$icon = Join-Path $root 'assets\icon.ico'

if (-not (Test-Path $electron)) { throw "Chưa cài Electron. Chạy 'npm install' trước." }
if (-not (Test-Path $icon)) { throw "Thiếu assets\icon.ico. Chạy 'npx electron tools/make-icon.js' trước." }

$shell = New-Object -ComObject WScript.Shell
$targets = @(
  [Environment]::GetFolderPath('Desktop'),
  (Join-Path ([Environment]::GetFolderPath('Programs')) '')
)

foreach ($dir in $targets) {
  $path = Join-Path $dir 'Handshake.lnk'
  $lnk = $shell.CreateShortcut($path)
  $lnk.TargetPath = $electron
  $lnk.Arguments = '"' + $root + '"'
  $lnk.WorkingDirectory = $root
  $lnk.IconLocation = "$icon,0"
  $lnk.Description = 'Handshake - PC ⇄ iPhone'
  $lnk.WindowStyle = 1
  $lnk.Save()
  Write-Output "Đã tạo: $path"
}
