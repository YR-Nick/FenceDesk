param(
    [string]$Version = "1.3.44",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "publish\$Runtime"
$dist = Join-Path $root "dist"
$zip = Join-Path $dist "FenceDesk-v$Version-$Runtime.zip"

Write-Host "Publishing FenceDesk $Version ($Runtime)..." -ForegroundColor Cyan
if (Test-Path (Join-Path $root "publish")) {
    Remove-Item (Join-Path $root "publish") -Recurse -Force
}
New-Item -ItemType Directory -Force $out | Out-Null
New-Item -ItemType Directory -Force $dist | Out-Null

dotnet publish (Join-Path $root "FenceDesk.csproj") `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=false `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $out

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

Copy-Item (Join-Path $root "README.md") $out -Force
Copy-Item (Join-Path $root "LICENSE") $out -Force
Copy-Item (Join-Path $root "CHANGELOG.md") $out -Force
$installer = Join-Path $root "installer"
if (Test-Path $installer) {
    Get-ChildItem $installer -File | Where-Object { $_.Extension -in ".cmd", ".ps1" } | Copy-Item -Destination $out -Force
}

$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "C# compiler not found: $csc" }
$fw = Split-Path $csc
Write-Host "Compiling FenceDeskSetup.exe..." -ForegroundColor Cyan
& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
    "/win32manifest:$root\installer\setup.manifest" `
    "/win32icon:$root\Assets\FenceDesk.ico" `
    "/reference:$fw\System.Windows.Forms.dll" `
    "/reference:$fw\System.Drawing.dll" `
    "/reference:$fw\System.Core.dll" `
    "/out:$out\FenceDeskSetup.exe" `
    "$root\installer\FenceDeskSetup.cs"
if ($LASTEXITCODE -ne 0) { throw "FenceDeskSetup.exe compilation failed." }
if (Test-Path (Join-Path $root "docs")) {
    Copy-Item (Join-Path $root "docs") $out -Recurse -Force
}

$quickStart = @"
FenceDesk v$Version
===================

Recommended:
1. Copy this whole folder to a normal directory (Desktop or C:\Temp).
   Do not run it from D:\项目: that workspace has a Low integrity label.
2. Run FenceDeskSetup.exe and approve the administrator prompt.
3. The installer copies the app to Program Files, registers HKLM startup,
   and creates Start Menu / desktop shortcuts.

Portable mode:
- Run FenceDesk.exe from this folder.
- Double-click an empty desktop area to hide/show fences.
- Ctrl+Alt+F opens search. Ctrl+Alt+D toggles fences.

Data folder: %APPDATA%\FenceDesk
If AppData is not writable, FenceDesk automatically falls back to .\data,
%LOCALAPPDATA%\FenceDesk, or %TEMP%\FenceDesk. Run FenceDesk.exe --portable
to force a portable data folder next to the executable.
Keep FenceDesk.exe together with the native DLL files in this folder.
See README.md for details.
"@
$quickStart | Set-Content -Path (Join-Path $out "QUICKSTART.txt") -Encoding UTF8

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal

$exe = Join-Path $out "FenceDesk.exe"
Write-Host ""
Write-Host "EXE: $exe" -ForegroundColor Green
Write-Host ("Size: {0:N1} MB" -f ((Get-Item $exe).Length / 1MB))
Write-Host "ZIP: $zip" -ForegroundColor Green
Write-Host ("Size: {0:N1} MB" -f ((Get-Item $zip).Length / 1MB))
