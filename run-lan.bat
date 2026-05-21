@echo off
REM Phone QR scanning needs HTTPS — see URLs printed when the app starts.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-lan.ps1" %*
