[CmdletBinding()]
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'FenceDesk')
)

$ErrorActionPreference = 'Stop'

function Assert-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'Administrator privileges are required. Run Uninstall-FenceDesk.cmd and approve the UAC prompt.'
    }
}

Assert-Administrator

Write-Host ''
Write-Host 'FenceDesk Uninstall' -ForegroundColor Yellow
Write-Host '===================' -ForegroundColor Yellow

Get-Process -Name FenceDesk -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

# Remove the machine-wide startup entry.
try {
    Remove-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'FenceDesk' -Force -ErrorAction SilentlyContinue
    Write-Host '  Removed HKLM startup entry.'
}
catch {
    Write-Host ('  Could not remove HKLM startup entry: ' + $_.Exception.Message) -ForegroundColor Red
}

$startMenu = [Environment]::GetFolderPath('CommonPrograms')
$menuFolder = Join-Path $startMenu 'FenceDesk'
if (Test-Path -LiteralPath $menuFolder) {
    Remove-Item -LiteralPath $menuFolder -Recurse -Force
    Write-Host '  Removed Start Menu folder.'
}

$desktopLink = Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'FenceDesk.lnk'
if (Test-Path -LiteralPath $desktopLink) {
    Remove-Item -LiteralPath $desktopLink -Force
    Write-Host '  Removed desktop shortcut.'
}

Write-Host ''
Write-Host 'Application removed. User layouts in %APPDATA%\FenceDesk were kept.' -ForegroundColor Green
Write-Host ('Install directory will be deleted after this process exits: ' + $InstallDir)
Write-Host ''

# The running PowerShell has the uninstall script open; remove the folder after exit.
$command = 'timeout /t 2 /nobreak >nul & rmdir /s /q "' + $InstallDir + '"'
Start-Process -FilePath 'cmd.exe' -ArgumentList '/c', $command -WindowStyle Hidden
