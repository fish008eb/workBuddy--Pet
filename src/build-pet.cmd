@echo off
setlocal
cd /d "%~dp0"

set FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319
if not exist "%FW%\csc.exe" set FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319
if not exist "%FW%\csc.exe" (
  echo [ERROR] csc.exe not found - .NET Framework 4.x required.
  pause
  exit /b 1
)

echo Compiling WbPet.exe with %FW%\csc.exe ...
"%FW%\csc.exe" /nologo /target:winexe /platform:anycpu /optimize+ ^
  /out:WbPet.exe ^
  /lib:"%FW%;%FW%\WPF" ^
  /r:System.dll /r:System.Core.dll /r:System.Xml.dll ^
  /r:WindowsBase.dll /r:PresentationCore.dll /r:PresentationFramework.dll /r:System.Xaml.dll ^
  WbPet.cs

if errorlevel 1 (
  echo.
  echo [FAILED] see errors above.
  pause
  exit /b 1
)

echo.
echo [OK] WbPet.exe built: %~dp0WbPet.exe
echo Restarting panel ...
taskkill /f /im WbPet.exe >nul 2>&1
rem the PowerShell fallback panel holds the same single-instance mutex, stop it too
wmic process where "CommandLine like '%%panel-wpf.ps1%%' and Name='powershell.exe'" delete >nul 2>&1
timeout /t 1 >nul
start "" "%~dp0WbPet.exe"
echo Done.
timeout /t 2 >nul
