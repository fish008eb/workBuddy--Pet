@echo off
rem ===========================================================================
rem  pet-hook.cmd  -  forward one WorkBuddy hook event to the pet panel
rem
rem  Usage (registered in settings.json):
rem      pet-hook.cmd <EventName>
rem
rem  Why cmd + curl instead of node:
rem    PreToolUse fires on EVERY tool call. A long turn can emit hundreds of
rem    events, so the per-event cost has to stay tiny. node.exe costs 50-100 ms
rem    of cold start each time; curl.exe wins at roughly 10-15 ms and needs no
rem    runtime at all. The JSON body is piped straight from the hook's stdin
rem    into curl with --data-binary @-, so nothing is parsed on this side.
rem
rem  Every failure path exits 0 on purpose: a hook that returns non-zero can
rem  surface an error inside the session, and a missing panel must never look
rem  like a broken agent. If pet-bus.port is absent the panel is not running
rem  (or the PowerShell fallback host is in use) and the event is dropped.
rem
rem  Keep this file pure ASCII - cmd.exe parses it with the OEM code page.
rem ===========================================================================
setlocal enableextensions

if "%~1"=="" exit /b 0

set "PPORT="
set "PF=%~dp0pet-bus.port"
if exist "%PF%" set /p PPORT=<"%PF%"
if not defined PPORT exit /b 0

"%SystemRoot%\System32\curl.exe" -s -m 2 --connect-timeout 1 -o NUL -X POST -H "Content-Type: application/json" --data-binary @- "http://127.0.0.1:%PPORT%/e?n=%~1" >nul 2>&1

exit /b 0
