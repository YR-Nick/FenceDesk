@echo off
setlocal
fltmc >nul 2>&1
if errorlevel 1 (
    if defined FENCEDESK_ELEVATED (
        echo Administrator privileges are required.
        echo Run this file from a normal Windows desktop session and approve UAC.
        pause
        exit /b 1
    )
    set "FENCEDESK_ELEVATED=1"
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-FenceDesk.ps1" %*
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
    echo.
    echo Uninstall failed. See the message above.
    pause
)
exit /b %RC%
