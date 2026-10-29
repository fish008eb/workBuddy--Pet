@echo off
rem ===========================================================================
rem  build-pet.cmd  -  compile WbPet.cs into WbPet.exe, then restart the panel
rem
rem  Why the compiler is driven by a response file (.rsp):
rem    cmd.exe corrupts quoted arguments that are split across "^" line
rem    continuations. The old command line passed
rem        /lib:"C:\...\v4.0.30319;C:\...\v4.0.30319\WPF"
rem    that way, so the compiler received a mangled search path:
rem        warning CS1668: invalid search path ...
rem        error CS0006: metadata file 'WindowsBase.dll' could not be found
rem    This script writes one argument per line into build.rsp and runs
rem        csc.exe @build.rsp
rem    so cmd.exe never has to quote or join anything. Every assembly is also
rem    referenced by its ABSOLUTE path, which removes the need for /lib
rem    altogether - CS1668 cannot come back.
rem
rem  Keep this file pure ASCII (cmd.exe parses it with the OEM code page).
rem ===========================================================================
setlocal enableextensions
cd /d "%~dp0"

set "SRC=WbPet.cs"
set "OUT=WbPet.exe"
set "RSP=build.rsp"
rem Absolute path on purpose: a Git-for-Windows / MSYS "find.exe" earlier in
rem PATH would otherwise shadow the Windows one and always report "not found".
set "FIND=%SystemRoot%\System32\find.exe"
set "FAIL="

echo === WorkBuddy pet panel - build %OUT% ===
echo.

rem ---------- 1/6: locate the C# compiler (64 bit first, then 32 bit) ------
set "FWROOT=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FWROOT%\csc.exe" set "FWROOT=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FWROOT%\csc.exe" goto :nocsc
echo [1/6] compiler    : %FWROOT%\csc.exe

rem ---------- 2/6: locate every referenced assembly ------------------------
rem WPF assemblies live in the WPF sub folder, System.Xaml.dll lives in the
rem framework root. Some installs keep everything in one folder, so both are
rem searched and any miss is reported with its exact path.
set "WPF=%FWROOT%\WPF"
if not exist "%WPF%\PresentationFramework.dll" set "WPF=%FWROOT%"

call :resolve WindowsBase.dll
set "D_WINDOWS_BASE=%RES%"
call :resolve PresentationCore.dll
set "D_PRESENTATION_CORE=%RES%"
call :resolve PresentationFramework.dll
set "D_PRESENTATION_FRAMEWORK=%RES%"
call :resolve System.Xaml.dll
set "D_SYSTEM_XAML=%RES%"
call :resolve System.dll
set "D_SYSTEM=%RES%"
call :resolve System.Core.dll
set "D_SYSTEM_CORE=%RES%"
call :resolve System.Xml.dll
set "D_SYSTEM_XML=%RES%"

if defined FAIL goto :nodeps
echo [2/6] assemblies  : found [%WPF%]

rem ---------- 3/6: WbPet.cs must be UTF-8 WITH BOM -------------------------
rem The source holds Chinese text and emoji literals. Without a BOM the old
rem compiler decodes it as ANSI, the literals turn into garbage and the panel
rem shows mojibake. Check the first three bytes: EF BB BF.
rem Exit code 0 = BOM present, 1 = BOM missing, 9009 = powershell unavailable.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$b=[System.IO.File]::ReadAllBytes('%~dp0%SRC%'); if ($b.Length -ge 3 -and $b[0] -eq 239 -and $b[1] -eq 187 -and $b[2] -eq 191) { exit 0 } else { exit 1 }"
if errorlevel 9009 goto :bomunknown
if errorlevel 1 goto :nobom
echo [3/6] encoding    : UTF-8 with BOM ok

:writeRsp
rem ---------- 4/6: write the compiler response file ------------------------
>"%RSP%" echo /nologo
>>"%RSP%" echo /target:winexe
>>"%RSP%" echo /platform:anycpu
>>"%RSP%" echo /optimize+
>>"%RSP%" echo /out:"%OUT%"
>>"%RSP%" echo /r:"%D_SYSTEM%"
>>"%RSP%" echo /r:"%D_SYSTEM_CORE%"
>>"%RSP%" echo /r:"%D_SYSTEM_XML%"
>>"%RSP%" echo /r:"%D_WINDOWS_BASE%"
>>"%RSP%" echo /r:"%D_PRESENTATION_CORE%"
>>"%RSP%" echo /r:"%D_PRESENTATION_FRAMEWORK%"
>>"%RSP%" echo /r:"%D_SYSTEM_XAML%"
>>"%RSP%" echo "%SRC%"

if not exist "%RSP%" goto :norps
echo [4/6] rsp file    : %RSP%

rem ---------- 5/6: compile ------------------------------------------------
rem A running WbPet.exe locks its own image, so it has to go before linking.
taskkill /f /im WbPet.exe >nul 2>&1
echo [5/6] compiling   : %SRC%
echo.
"%FWROOT%\csc.exe" @%RSP%
if errorlevel 1 goto :buildfail
if not exist "%OUT%" goto :nooutput

echo.
for %%F in ("%OUT%") do echo [OK] %OUT% built, %%~zF bytes
if exist "%RSP%" del /f /q "%RSP%" >nul 2>&1

rem ---------- 6/6: restart the panel ---------------------------------------
rem The PowerShell fallback panel holds the same single-instance mutex, so it
rem must be stopped as well, otherwise the fresh exe exits immediately.
echo [6/6] restarting  : panel
taskkill /f /im WbPet.exe >nul 2>&1
powershell -NoProfile -ExecutionPolicy Bypass -Command "$p=Get-CimInstance Win32_Process | Where-Object { $_.Name -eq 'powershell.exe' -and $_.CommandLine -like '*panel-wpf.ps1*' }; foreach ($x in $p) { Stop-Process -Id $x.ProcessId -Force -ErrorAction SilentlyContinue }" >nul 2>&1
ping -n 2 127.0.0.1 >nul 2>&1
start "" "%~dp0%OUT%"

rem ---------- 6/6b: confirm the fresh process is really alive -----------------
rem Program.Main returns silently when it cannot create the single-instance
rem mutex "Global\WorkBuddyUsagePanel" - one of the reasons is an older panel
rem (or the PowerShell fallback) still holding it. Without this check the
rem script would print "Done." while no window appears at all, which is the
rem hardest failure to diagnose. tasklist ships with every Windows install.
rem The process is polled 4 times (~4 s) because a cold WPF start is slow.
set "ALIVE="
for /l %%i in (1,1,4) do (
  if not defined ALIVE (
    tasklist /fi "imagename eq %OUT%" 2>nul | "%FIND%" /i "%OUT%" >nul && set "ALIVE=1"
    if not defined ALIVE ping -n 2 127.0.0.1 >nul 2>&1
  )
)
if not defined ALIVE goto :startfail

echo.
echo Done.
ping -n 3 127.0.0.1 >nul 2>&1
endlocal
exit /b 0

rem ===========================================================================
rem  subroutines / error paths
rem ===========================================================================

:resolve
rem %1 = assembly file name. Sets RES to its absolute path.
rem Reports the exact paths that were tried when the file is missing.
set "RES="
if exist "%WPF%\%~1" set "RES=%WPF%\%~1"
if not defined RES if exist "%FWROOT%\%~1" set "RES=%FWROOT%\%~1"
if defined RES exit /b 0
echo [ERROR] missing dependency: %~1
echo         not found in: %WPF%
echo         nor in      : %FWROOT%
set "FAIL=1"
exit /b 0

:bomunknown
rem The BOM could not be verified (no powershell). Warn loudly, then continue:
rem blocking the build here would be worse than a possible mojibake warning.
echo [3/6] encoding    : UNVERIFIED - powershell unavailable
echo [WARN] make sure %SRC% is saved as UTF-8 WITH BOM,
echo        otherwise the Chinese text and emoji literals become garbage.
goto :writeRsp

:nocsc
echo [ERROR] csc.exe not found - the .NET Framework 4.x is required.
echo         checked: %WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
echo         checked: %WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
goto :fail

:nodeps
echo.
echo [ERROR] the assemblies listed above are missing, nothing was compiled.
echo         Install the .NET Framework 4.x full profile and run again.
goto :fail

:nobom
echo.
echo [ERROR] %SRC% is NOT saved as UTF-8 with BOM.
echo         The file contains Chinese text and emoji literals. Without a BOM
echo         the compiler decodes it as ANSI and the panel shows mojibake.
echo         Fix: re-save %SRC% as "UTF-8 with BOM" and run this script again.
goto :fail

:norps
echo [ERROR] could not write %RSP% - check the folder write permission.
goto :fail

:buildfail
echo.
echo [FAILED] the compiler reported errors, see the output above.
echo          response file kept for inspection: %~dp0%RSP%
goto :fail

:nooutput
echo [ERROR] the compiler reported success but %OUT% was not created.
echo         response file kept for inspection: %~dp0%RSP%
goto :fail

:startfail
rem %OUT% was launched but is gone again => it lost the single-instance mutex.
rem Tell the user what to do next, not just that something broke.
echo.
echo [WARN] %OUT% was started but exited again immediately.
echo        Another panel instance is most likely still holding the
echo        single-instance mutex "Global\WorkBuddyUsagePanel".
echo.
echo        What to do next:
echo          1. Right-click the old panel window and choose Exit.
echo             If you cannot see any panel window, open Task Manager
echo             ^(Ctrl+Shift+Esc^), find %OUT% and end that task.
echo          2. If you previously ran panel-wpf.ps1, close that
echo             PowerShell window as well.
echo          3. Run build-pet.cmd again.
echo.
echo        The compiled %OUT% is fine - this is not a build error.
pause
endlocal
exit /b 1

:fail
echo.
pause
endlocal
exit /b 1
