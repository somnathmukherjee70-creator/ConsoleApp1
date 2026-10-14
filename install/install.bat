@echo off
rem Double-click to install the Revit Geo Export add-in. Run "install.bat -Uninstall" to remove it.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
pause
