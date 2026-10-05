@echo off
setlocal
set "PS1=%~dp0install-prerequisites.ps1"
if not exist "%PS1%" (
  echo [x] install-prerequisites.ps1 not found in this folder.
  pause
  exit /b 1
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%" %*
