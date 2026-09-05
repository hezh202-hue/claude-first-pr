@echo off
setlocal
title Stellaris LAN Helper
cd /d "%~dp0"
set "PS1=%~dp0StellarisLAN.ps1"
if not exist "%PS1%" (
  echo [ERROR] StellarisLAN.ps1 not found in this folder.
  echo Keep the .bat and the .ps1 together in the same folder.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -STA -File "%PS1%"
set RC=%ERRORLEVEL%
if not "%RC%"=="0" (
  echo.
  echo ==========================================
  echo   Exit code: %RC%
  echo   Screenshot this window and send it over.
  echo ==========================================
  pause
)
exit /b %RC%
