@echo off
rem VBWR B
rem Project: VibeKit ^| Creator: Umberto Giacobbi
rem Created with AI: OpenAI Codex; interactive Windows signing kit launcher
rem Copyright (c) 2026 Umberto Giacobbi ^| License: MIT (see LICENSE)
rem VBWR E
setlocal

"%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy RemoteSigned -File "%~dp0Import-VibeWareDevelopmentSigning.ps1"
set "setupExitCode=%ERRORLEVEL%"
echo(
if not "%setupExitCode%"=="0" echo Signing kit setup stopped with exit code %setupExitCode%.
echo Press any key to close this window.
pause >nul
exit /b %setupExitCode%
