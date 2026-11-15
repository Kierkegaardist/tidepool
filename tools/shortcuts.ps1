# Creates (or with -Remove deletes) the two per-user Start menu shortcuts, plus a Startup shortcut so it starts at login. No admin.
param([switch]$Remove)
$dir = Join-Path ([Environment]::GetFolderPath('Programs')) 'Console Desktop'
$boot = Join-Path ([Environment]::GetFolderPath('Startup')) 'Console Desktop.lnk'
if ($Remove) { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item $boot -Force -ErrorAction SilentlyContinue; "removed $dir and $boot"; return }
$exe = Join-Path (Split-Path $PSScriptRoot -Parent) 'bin\ConsoleDesktop.exe'
New-Item -ItemType Directory -Force $dir | Out-Null
$sh = New-Object -ComObject WScript.Shell
foreach ($s in @(@{ n = 'Console Desktop'; a = ''; d = 'Start the console desktop layer (Ctrl+Alt+Win+S or Stock Windows to leave)' },
                 @{ n = 'Stock Windows'; a = '--stock'; d = 'Escape hatch: stop the console desktop and bring the Windows taskbar back' })) {
  $l = $sh.CreateShortcut((Join-Path $dir "$($s.n).lnk")); $l.TargetPath = $exe; $l.Arguments = $s.a; $l.Description = $s.d
  $l.WorkingDirectory = Split-Path $exe; $l.Save(); "made $($s.n).lnk"
}
$l = $sh.CreateShortcut($boot); $l.TargetPath = $exe; $l.Description = 'Console Desktop at login (remove: shortcuts.ps1 -Remove, or delete this shortcut)'
$l.WorkingDirectory = Split-Path $exe; $l.Save(); "made $boot"
