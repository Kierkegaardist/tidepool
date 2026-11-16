# Runs from the setup exe's temp folder (IExpress): installs Console Desktop for this Windows user, no admin needed.
# %LOCALAPPDATA%\Programs\Console Desktop; Start menu shortcuts; Apps & features entry; WebView2 if missing; then Setup.
# An update keeps the user's own files: config.json, the chat key and chats, web\live (their restyle), extras (yt-dlp, whisper).
param([switch]$Update)   # -Update: from the desktop's updater (Update.cs), run quietly: no Setup at the end, the desktop starts again
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
function Fail($msg) { [System.Windows.Forms.MessageBox]::Show("Console Desktop could not be installed.`n`n$msg", "Console Desktop", 'OK', 'Error') | Out-Null; exit 1 }
try {
    $dest = Join-Path $env:LOCALAPPDATA 'Programs\Console Desktop'
    $exe = Join-Path $dest 'bin\ConsoleDesktop.exe'

    # an older copy running: put Windows back and stop it (the escape hatch restores the taskbar). Only this install's exe, never another copy.
    $mine = @(Get-Process ConsoleDesktop -ErrorAction SilentlyContinue | ? { $_.Path -eq $exe })
    if ($mine) {
        Start-Process $exe '--stock' -Wait -ErrorAction SilentlyContinue
        $mine | Stop-Process -Force -ErrorAction SilentlyContinue
        Start-Sleep -Milliseconds 800
    }

    $tmp = Join-Path $env:TEMP ("console-desktop-install-" + [guid]::NewGuid().ToString('N'))
    Expand-Archive -Path (Join-Path $PSScriptRoot 'payload.zip') -DestinationPath $tmp -Force
    New-Item -ItemType Directory -Force $dest | Out-Null
    if (Test-Path "$dest\bin") { Remove-Item "$dest\bin" -Recurse -Force }   # old program files out; nothing of the user's lives in bin
    $keepLive = Test-Path "$dest\web\live"
    robocopy $tmp $dest /E /NFL /NDL /NJH /NJS /NP $(if ($keepLive) { '/XD'; "$tmp\web\live" }) | Out-Null
    if ($LASTEXITCODE -ge 8) { Fail "Copying the files failed (robocopy $LASTEXITCODE)." }
    Remove-Item $tmp -Recurse -Force

    # Start menu
    $menu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Console Desktop'
    New-Item -ItemType Directory -Force $menu | Out-Null
    $sh = New-Object -ComObject WScript.Shell
    foreach ($l in @(@('Console Desktop', ''), @('Console Desktop Setup', '--setup'), @('Back to stock Windows', '--stock'))) {
        $s = $sh.CreateShortcut((Join-Path $menu ($l[0] + '.lnk'))); $s.TargetPath = $exe; $s.Arguments = $l[1]; $s.WorkingDirectory = "$dest\bin"; $s.IconLocation = "$exe,0"; $s.Save()
    }
    $s = $sh.CreateShortcut((Join-Path $menu 'Uninstall Console Desktop.lnk')); $s.TargetPath = 'powershell.exe'
    $s.Arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$dest\uninstall.ps1`""; $s.Save()

    # Apps & features
    $key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ConsoleDesktop'
    New-Item -Force $key | Out-Null
    $size = [int]((Get-ChildItem $dest -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    @{ DisplayName = 'Console Desktop'; DisplayVersion = (Get-Content "$dest\version.txt" -ErrorAction SilentlyContinue); Publisher = 'Console Desktop'; DisplayIcon = "$exe,0"
       InstallLocation = $dest; UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dest\uninstall.ps1`""
       QuietUninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$dest\uninstall.ps1`" -Quiet" }.GetEnumerator() | % { Set-ItemProperty $key $_.Key $_.Value }
    Set-ItemProperty $key EstimatedSize $size -Type DWord; Set-ItemProperty $key NoModify 1 -Type DWord; Set-ItemProperty $key NoRepair 1 -Type DWord

    # WebView2 (Windows 11 and updated Windows 10 already have it): Microsoft's own small bootstrapper
    $wv = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    $has = $wv | ? { (Get-ItemProperty "HKLM:\$_" -ErrorAction SilentlyContinue).pv -or (Get-ItemProperty "HKCU:\$_" -ErrorAction SilentlyContinue).pv }
    if (-not $has) {
        $boot = Join-Path $env:TEMP 'MicrosoftEdgeWebview2Setup.exe'
        Invoke-WebRequest 'https://go.microsoft.com/fwlink/p/?LinkId=2124703' -OutFile $boot -UseBasicParsing
        Start-Process $boot '/silent /install' -Wait; Remove-Item $boot -ErrorAction SilentlyContinue
    }

    if ($Update -and (Test-Path "$dest\config.json")) { Start-Process $exe -WorkingDirectory "$dest\bin" } else { Start-Process $exe '--setup' -WorkingDirectory "$dest\bin" }
}
catch { Fail $_.Exception.Message }
