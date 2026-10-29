' Launch the usage panel with no console window.
' wscript.exe is a GUI-subsystem host, so nothing flashes on screen;
' the inner cmd/powershell windows are started with WindowStyle 0 (hidden).
Set sh = CreateObject("WScript.Shell")
hookDir = Left(WScript.ScriptFullName, InStrRev(WScript.ScriptFullName, "\"))
ps = "powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File """ & hookDir & "panel-wpf.ps1"""
cmd = "cmd.exe /c " & ps & " >> """ & hookDir & "panel.log"" 2>&1"
sh.Run cmd, 0, False
