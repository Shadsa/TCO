@echo off
setlocal
title TCO Installer
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-TCO.ps1"
set "TCO_EXIT_CODE=%ERRORLEVEL%"
echo.
if not "%TCO_EXIT_CODE%"=="0" (
    echo TCO installation failed. Review the latest file in "%~dp0logs".
) else (
    echo TCO installation finished. The log is available in "%~dp0logs".
)
pause
exit /b %TCO_EXIT_CODE%
