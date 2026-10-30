[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'FenceDesk'),
    [string]$SourceDir = $PSScriptRoot,
    [switch]$SkipStartup,
    [switch]$SkipStartMenu,
    [switch]$SkipDesktopShortcut,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator privileges are required. Run Install-FenceDesk.cmd and approve the UAC prompt.'
    }
}

function Write-Step {
    param([string]$Message)
    Write-Host ('  ' + $Message) -ForegroundColor Cyan
}

Assert-Administrator

$root = $SourceDir
$sourceExe = Join-Path $root 'FenceDesk.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw 'FenceDesk.exe was not found next to Install-FenceDesk.ps1. Extract the whole release zip first.'
}

Write-Host ''
Write-Host 'FenceDesk Setup' -ForegroundColor Green
Write-Host '===============' -ForegroundColor Green
Write-Step ('Source : ' + $root)
Write-Step ('Target : ' + $InstallDir)

# Stop an already running copy so files can be replaced.
$running = Get-Process -Name FenceDesk -ErrorAction SilentlyContinue
if ($running) {
    Write-Step 'Stopping the running FenceDesk instance...'
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 400
}

if (-not (Test-Path -LiteralPath $InstallDir)) {
    New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
}

# Copy the application files (skip installer scripts and user data).
$exclude = @('FenceDeskSetup.exe', 'Install-FenceDesk.ps1', 'Install-FenceDesk.cmd', 'Uninstall-FenceDesk.ps1', 'Uninstall-FenceDesk.cmd')
Get-ChildItem -LiteralPath $root -File | Where-Object { $exclude -notcontains $_.Name } | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $InstallDir -Force
}

$docs = Join-Path $root 'docs'
if (Test-Path -LiteralPath $docs) {
    $targetDocs = Join-Path $InstallDir 'docs'
    if (Test-Path -LiteralPath $targetDocs) {
        Remove-Item -LiteralPath $targetDocs -Recurse -Force
    }
    Copy-Item -LiteralPath $docs -Destination $targetDocs -Recurse -Force
}

# The uninstaller lives inside the install directory.
$uninstallSource = Join-Path $root 'Uninstall-FenceDesk.ps1'
if (-not (Test-Path -LiteralPath $uninstallSource)) {
    $uninstallSource = Join-Path $PSScriptRoot 'Uninstall-FenceDesk.ps1'
}
$uninstallCmdSource = Join-Path $root 'Uninstall-FenceDesk.cmd'
if (-not (Test-Path -LiteralPath $uninstallCmdSource)) {
    $uninstallCmdSource = Join-Path $PSScriptRoot 'Uninstall-FenceDesk.cmd'
}
Copy-Item -LiteralPath $uninstallSource -Destination $InstallDir -Force
Copy-Item -LiteralPath $uninstallCmdSource -Destination $InstallDir -Force

$installedExe = Join-Path $InstallDir 'FenceDesk.exe'
if (-not (Test-Path -LiteralPath $installedExe)) {
    throw 'FenceDesk.exe was not copied to the install directory.'
}

# Machine-wide Run entry. This runs for every user at logon with that user's
# normal (non-elevated) token. The installer itself needs admin to write HKLM.
if (-not $SkipStartup) {
    $runKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
    $command = '"' + $installedExe + '" --autostart'
    New-ItemProperty -Path $runKey -Name 'FenceDesk' -Value $command -PropertyType String -Force | Out-Null
    Write-Step 'Registered machine-wide startup: HKLM\...\Run\FenceDesk'
}
else {
    Write-Step 'Skipped startup registration (test mode).'
}

function New-Shortcut {
    param(
        [string]$ShortcutPath,
        [string]$TargetPath,
        [string]$WorkingDirectory,
        [string]$Description
    )
    $parent = Split-Path -Parent $ShortcutPath
    if (-not (Test-Path -LiteralPath $parent)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }
    $shell = New-Object -ComObject WScript.Shell
    try {
        $shortcut = $shell.CreateShortcut($ShortcutPath)
        $shortcut.TargetPath = $TargetPath
        $shortcut.WorkingDirectory = $WorkingDirectory
        $shortcut.IconLocation = $TargetPath + ',0'
        $shortcut.Description = $Description
        $shortcut.Save()
    }
    finally {
        [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)
    }
}

if (-not $SkipStartMenu) {
    $startMenu = [Environment]::GetFolderPath('CommonPrograms')
    $menuFolder = Join-Path $startMenu 'FenceDesk'
    New-Item -ItemType Directory -Path $menuFolder -Force | Out-Null
    New-Shortcut -ShortcutPath (Join-Path $menuFolder 'FenceDesk.lnk') `
        -TargetPath $installedExe -WorkingDirectory $InstallDir `
        -Description 'FenceDesk desktop fences'
    New-Shortcut -ShortcutPath (Join-Path $menuFolder 'Uninstall FenceDesk.lnk') `
        -TargetPath (Join-Path $InstallDir 'Uninstall-FenceDesk.cmd') -WorkingDirectory $InstallDir `
        -Description 'Uninstall FenceDesk'
    Write-Step 'Created Start Menu shortcuts.'
}

if (-not $SkipDesktopShortcut) {
    $desktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
    New-Shortcut -ShortcutPath (Join-Path $desktop 'FenceDesk.lnk') `
        -TargetPath $installedExe -WorkingDirectory $InstallDir `
        -Description 'FenceDesk desktop fences'
    Write-Step 'Created desktop shortcut.'
}

Write-Host ''
Write-Host 'Installation complete.' -ForegroundColor Green
Write-Host 'FenceDesk will start at Windows logon for all users.' -ForegroundColor Green
Write-Host 'User layouts are stored in %APPDATA%\FenceDesk (or the portable fallback).'
Write-Host ''
Write-Host 'To remove: Start Menu -> FenceDesk -> Uninstall FenceDesk.' -ForegroundColor Yellow
Write-Host ''

if (-not $NoLaunch) {
    Start-Process -FilePath $installedExe -WorkingDirectory $InstallDir
}
