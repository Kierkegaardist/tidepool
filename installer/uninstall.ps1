# Removes Console Desktop for this user: Windows' own taskbar back first, then the files, shortcuts and the Apps & features entry.
# Copied into the install folder; it runs from a temp copy so it can delete that folder. -Quiet: no questions or messages.
# Only this install is touched: another copy of the desktop (a dev build) keeps running, and its shortcuts stay.
param([switch]$Quiet)
$ErrorActionPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Windows.Forms
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Console Desktop'
if ($PSScriptRoot -eq $dest) {   # re-run from %TEMP%, so the folder is free to go
    $copy = Join-Path $env:TEMP 'console-desktop-uninstall.ps1'; Copy-Item $PSCommandPath $copy -Force
    Start-Process powershell.exe "-NoProfile -ExecutionPolicy Bypass -File `"$copy`" $(if ($Quiet) { '-Quiet' })"; exit
}
if (-not $Quiet -and [System.Windows.Forms.MessageBox]::Show("Remove Console Desktop and its settings?", "Console Desktop", 'YesNo', 'Question') -ne 'Yes') { exit }
$exe = Join-Path $dest 'bin\ConsoleDesktop.exe'
$mine = @(Get-Process ConsoleDesktop | ? { $_.Path -eq $exe })
if ($mine) { Start-Process $exe '--stock' -Wait; $mine | Stop-Process -Force; Start-Sleep 1 }   # taskbar and wallpaper back
$sh = New-Object -ComObject WScript.Shell
$ours = { param($lnk) (Test-Path $lnk) -and $sh.CreateShortcut($lnk).TargetPath -eq $exe }
$boot = Join-Path ([Environment]::GetFolderPath('Startup')) 'Console Desktop.lnk'
if (& $ours $boot) { Remove-Item $boot -Force }
$menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Console Desktop'
Get-ChildItem $menu -Filter *.lnk | ? { (& $ours $_.FullName) -or $_.Name -eq 'Uninstall Console Desktop.lnk' } | Remove-Item -Force
if (-not (Get-ChildItem $menu)) { Remove-Item $menu -Force }
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ConsoleDesktop' -Recurse -Force
for ($i = 0; $i -lt 5 -and (Test-Path $dest); $i++) { Remove-Item $dest -Recurse -Force; if (Test-Path $dest) { Start-Sleep 1 } }   # WebView2 may hold its profile for a moment
if (-not $Quiet) { [System.Windows.Forms.MessageBox]::Show($(if (Test-Path $dest) { "Mostly removed. Delete this folder by hand: $dest" } else { "Console Desktop is removed." }), "Console Desktop") | Out-Null }
Remove-Item $PSCommandPath -Force
