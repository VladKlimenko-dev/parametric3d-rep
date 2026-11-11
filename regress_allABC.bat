@echo off
setlocal
chcp 65001 >nul
rem Run allA, allB and allC on the current checkout, without git pull.
cd /d "%~dp0"
if not exist "%~dp0regress.ps1" (
    echo Place this BAT next to regress.ps1 in the project root.
    pause
    exit /b 1
)
where powershell.exe >nul 2>nul
if errorlevel 1 (
    echo PowerShell was not found.
    pause
    exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0regress.ps1" -Only abc -NoPull %*
set "test_exit=%errorlevel%"
endlocal & exit /b %test_exit%
