@echo off
REM Phone testing - bypasses PowerShell script policy. Double-click or: stop-and-run-lan.bat
cd /d "%~dp0.."
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-lan.ps1" %*
