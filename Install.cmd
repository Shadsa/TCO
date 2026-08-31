@echo off
setlocal
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-TERA-Complete.ps1" -Action Apply
set "exitCode=%ERRORLEVEL%"
echo.
if not "%exitCode%"=="0" pause
exit /b %exitCode%
