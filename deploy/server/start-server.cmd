@echo off
rem Double-click launcher for the Chest Tracker dedicated server (Windows).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0start-server.ps1" %*
if errorlevel 1 pause
