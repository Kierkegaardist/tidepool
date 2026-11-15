# Rebuilds the host into bin\ (only where the source is, i.e. the dev tree) and restarts the desktop. An installed copy just restarts.
# Run from inside the Claude panel's own claude (or an agent it started), restarting now would kill that chat,
# so it only queues: the host runs this again with -Now once the panel's reply is finished. -Now forces it.
param([switch]$Now)
$app = Split-Path $PSScriptRoot
if (-not $Now) {
    $procs = @{}; Get-CimInstance Win32_Process | % { $procs[[int]$_.ProcessId] = $_ }
    $id = $PID
    while ($procs.ContainsKey($id) -and $id) {
        $p = $procs[$id]; $parent = $procs[[int]$p.ParentProcessId]
        if ($parent -and $parent.Name -eq 'ConsoleDesktop.exe') {
            New-Item -ItemType File -Force "$app\logs\restart.pending" | Out-Null
            "Restart queued: the desktop rebuilds and restarts as soon as the panel's current reply finishes."
            exit 0
        }
        if ([int]$p.ParentProcessId -eq $id) { break }
        $id = [int]$p.ParentProcessId
    }
}
Get-Process ConsoleDesktop -ErrorAction SilentlyContinue | ? { $_.Path -eq "$app\bin\ConsoleDesktop.exe" } | Stop-Process -Force   # this copy only, never another install
Start-Sleep 2
if (Test-Path "$app\src\ConsoleDesktop.csproj") {   # the dev tree: rebuild first; an installed copy has no source and just restarts
    $dotnet = 'dotnet'
    if (Test-Path 'E:\Library\dotnet\dotnet.exe') { $env:DOTNET_ROOT = 'E:\Library\dotnet'; $env:NUGET_PACKAGES = 'E:\Library\nuget-packages'; $dotnet = 'E:\Library\dotnet\dotnet.exe' }
    & $dotnet build "$app\src" -c Release -o "$app\bin" *> "$app\logs\restart-build.log"
}
Start-Process "$app\bin\ConsoleDesktop.exe" -WorkingDirectory "$app\bin"
