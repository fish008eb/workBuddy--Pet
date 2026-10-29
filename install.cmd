@echo off
setlocal
title WorkBuddy Usage Pet - Installer
cd /d "%~dp0"

echo ==========================================================
echo   WorkBuddy usage pet  -  installer
echo ==========================================================
echo.

node --version >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Node.js not found.
  echo.
  echo         The pet's hooks are written in JavaScript, so Node.js 18+
  echo         is required. Opening the download page for you ...
  start "" https://nodejs.org/en/download
  echo.
  echo         Install Node.js, then run this file again.
  pause
  exit /b 1
)
for /f "delims=" %%v in ('node --version') do echo [1/3] Node.js %%v detected

if not exist "%USERPROFILE%\.workbuddy\settings.json" (
  echo.
  echo [ERROR] WorkBuddy desktop has never been started on this machine.
  echo         Start WorkBuddy once, close it, then run this file again.
  pause
  exit /b 1
)
echo [2/3] WorkBuddy config found
echo.

echo [3/3] Installing files and registering hooks ...
echo ----------------------------------------------------------
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1"
echo ----------------------------------------------------------
echo.

echo Done.
echo.
echo   - The pet should now sit at the bottom-right corner of your screen.
echo   - If you cannot see it, restart WorkBuddy and send it one message.
echo   - Right-click the pet for skins, persona, verbosity and sound options.
echo.
pause
