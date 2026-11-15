# Builds dist\ConsoleDesktop-Setup.exe: a self-contained publish (no .NET needed on the other PC) + web\ + tools, zipped,
# wrapped by IExpress (built into Windows) with install.ps1 as the command it runs after unpacking.
# Run: powershell -ExecutionPolicy Bypass -File installer\build.ps1 [-Version 0.1.0]
param([string]$Version = "0.1.0")
$ErrorActionPreference = 'Stop'
$app = Split-Path $PSScriptRoot
$dist = Join-Path $app 'dist'
$stage = Join-Path $dist 'stage'
$pack = Join-Path $dist 'pack'
Remove-Item $stage, $pack -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage, $pack | Out-Null

$dotnet = 'dotnet'
if (Test-Path 'E:\Library\dotnet\dotnet.exe') { $env:DOTNET_ROOT = 'E:\Library\dotnet'; $env:NUGET_PACKAGES = 'E:\Library\nuget-packages'; $dotnet = 'E:\Library\dotnet\dotnet.exe' }
"publish..."
& $dotnet publish "$app\src" -c Release -r win-x64 --self-contained true -p:Version=$Version -o "$stage\bin" *> "$dist\publish.log"
if ($LASTEXITCODE -ne 0) { Get-Content "$dist\publish.log" -Tail 30; throw "publish failed (dist\publish.log)" }

# the page, minus the test mocks and dev previews (_*.html); the tools the desktop runs; the uninstaller. Never config.json, .chatkey, chats, logs or tts\ (this PC's own)
robocopy "$app\web" "$stage\web" /E /XD tools /XF _*.html /NFL /NDL /NJH /NJS /NP | Out-Null
New-Item -ItemType Directory -Force "$stage\tools" | Out-Null
Copy-Item "$app\tools\restart-host.ps1" "$stage\tools\"
Copy-Item "$PSScriptRoot\uninstall.ps1" $stage
Set-Content "$stage\version.txt" $Version -Encoding ascii
"stage: {0:N0} MB" -f ((Get-ChildItem $stage -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)

Compress-Archive -Path "$stage\*" -DestinationPath "$pack\payload.zip" -CompressionLevel Optimal
Copy-Item "$PSScriptRoot\install.ps1" $pack

# IExpress: unpack to %TEMP%, run install.ps1 hidden, clean up after it returns
$out = Join-Path $dist 'ConsoleDesktop-Setup.exe'
$sed = Join-Path $dist 'setup.sed'
@"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$out
FriendlyName=Console Desktop $Version
AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File install.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
SourceFiles=SourceFiles
[SourceFiles]
SourceFiles0=$pack\
[SourceFiles0]
%FILE0%=
%FILE1%=
[Strings]
FILE0="payload.zip"
FILE1="install.ps1"
"@ | Set-Content $sed -Encoding ascii
Remove-Item $out -ErrorAction SilentlyContinue
"iexpress..."
Start-Process "$env:WINDIR\System32\iexpress.exe" -ArgumentList '/N', '/Q', $sed -Wait -NoNewWindow
if (-not (Test-Path $out)) { throw "IExpress made no exe" }
Remove-Item $stage, $pack, $sed -Recurse -Force
"done: $out ({0:N0} MB)" -f ((Get-Item $out).Length / 1MB)
