@echo off
setlocal
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0repo-radar.ps1" %*
set "_repoRadarExit=%errorlevel%"
if not "%_repoRadarExit%"=="0" pause
exit /b %_repoRadarExit%
