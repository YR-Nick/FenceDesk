param(
    [string]$Version = "1.3.54",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$out = Join-Path $root "publish\$Runtime"
$dist = Join-Path $root "dist"
$zip = Join-Path $dist "FenceDesk-v$Version-$Runtime.zip"
$setup = Join-Path $dist "FenceDeskSetup-v$Version.exe"

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
if (Test-Path (Join-Path $root "docs")) {
    Copy-Item (Join-Path $root "docs") $out -Recurse -Force
}

$quickStart = @"
FenceDesk v$Version
===================

安装版（推荐）
1. 只需要 FenceDeskSetup.exe 这一个文件，双击运行并允许管理员权限。
2. 安装程序会把程序装到 Program Files\FenceDesk，注册开机启动、
   开始菜单快捷方式，并登记到"应用和功能"（可从这里卸载）。
3. 安装完直接使用，桌面图标由围栏接管。

免安装版
- 把本文件夹解压到任意目录，直接运行 FenceDesk.exe。
- 双击桌面空白处隐藏 / 显示围栏；Ctrl+Alt+F 搜索；Ctrl+Alt+D 切换围栏。

数据目录：%APPDATA%\FenceDesk
如果 AppData 不可写，会自动回退到 .\data、%LOCALAPPDATA%\FenceDesk 或 %TEMP%\FenceDesk。
运行 FenceDesk.exe --portable 可强制使用程序目录下的 data。
桌面全空时运行：FenceDesk.exe --restore-desktop
详见 README.md。
"@
$quickStart | Set-Content -Path (Join-Path $out "QUICKSTART.txt") -Encoding UTF8

# ---------------------------------------------------------------- 安装包
# 安装包内嵌 zip 负载，单独一个 exe 就能安装，不需要带整个文件夹。
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if (-not (Test-Path $csc)) { throw "C# compiler not found: $csc" }
$fw = Split-Path $csc

$payloadDir = Join-Path $root "obj\payload"
$payloadZip = Join-Path $root "obj\FenceDeskPayload.zip"
if (Test-Path $payloadDir) { Remove-Item $payloadDir -Recurse -Force }
New-Item -ItemType Directory -Force $payloadDir | Out-Null
Get-ChildItem $out -Force | Where-Object {
    $_.Name -notin @("FenceDeskSetup.exe", "QUICKSTART.txt") -and
    $_.Name -notlike "Install-FenceDesk*" -and
    $_.Name -notlike "Uninstall-FenceDesk*"
} | Copy-Item -Destination $payloadDir -Recurse -Force
if (Test-Path $payloadZip) { Remove-Item $payloadZip -Force }
Compress-Archive -Path (Join-Path $payloadDir "*") -DestinationPath $payloadZip -CompressionLevel Optimal

Write-Host "Compiling FenceDeskSetup.exe (single-file, embedded payload)..." -ForegroundColor Cyan
& $csc /nologo /target:winexe /platform:anycpu /optimize+ `
    "/win32manifest:$root\installer\setup.manifest" `
    "/win32icon:$root\Assets\FenceDesk.ico" `
    "/reference:$fw\System.Windows.Forms.dll" `
    "/reference:$fw\System.Drawing.dll" `
    "/reference:$fw\System.Core.dll" `
    "/reference:$fw\System.IO.Compression.dll" `
    "/reference:$fw\System.IO.Compression.FileSystem.dll" `
    "/resource:$payloadZip,FenceDeskPayload.zip" `
    "/out:$out\FenceDeskSetup.exe" `
    "$root\installer\FenceDeskSetup.cs"
if ($LASTEXITCODE -ne 0) { throw "FenceDeskSetup.exe compilation failed." }

Copy-Item "$out\FenceDeskSetup.exe" $setup -Force
Remove-Item $payloadDir -Recurse -Force
Remove-Item $payloadZip -Force

# 便携版 zip 不含安装包，避免同一个程序在包里出现两次
$portableDir = Join-Path $root "obj\portable"
if (Test-Path $portableDir) { Remove-Item $portableDir -Recurse -Force }
New-Item -ItemType Directory -Force $portableDir | Out-Null
Get-ChildItem $out -Force | Where-Object { $_.Name -ne "FenceDeskSetup.exe" } |
    Copy-Item -Destination $portableDir -Recurse -Force
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $portableDir "*") -DestinationPath $zip -CompressionLevel Optimal
Remove-Item $portableDir -Recurse -Force

$exe = Join-Path $out "FenceDesk.exe"
Write-Host ""
Write-Host "EXE  : $exe" -ForegroundColor Green
Write-Host ("Size : {0:N1} MB" -f ((Get-Item $exe).Length / 1MB))
Write-Host "SETUP: $setup" -ForegroundColor Green
Write-Host ("Size : {0:N1} MB" -f ((Get-Item $setup).Length / 1MB))
Write-Host "ZIP  : $zip" -ForegroundColor Green
Write-Host ("Size : {0:N1} MB" -f ((Get-Item $zip).Length / 1MB))
