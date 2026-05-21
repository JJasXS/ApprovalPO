@echo off
cd /d "%~dp0"
echo Stopping any previous ApprovalPO...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Stop-ApprovalPO.ps1"
echo.
echo Starting LAN mode (phone testing)...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-lan.ps1" %*
