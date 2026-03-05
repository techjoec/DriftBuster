@echo off
setlocal
set "DRIFTBUSTER_DEBUG=1"
start "" "%~dp0DriftBuster.Gui.exe" %*
endlocal
