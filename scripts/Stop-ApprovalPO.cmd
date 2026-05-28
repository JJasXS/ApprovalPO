@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Stop-ApprovalPO.ps1"
exit /b %ERRORLEVEL%
