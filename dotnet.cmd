@echo off
REM In this folder, "dotnet run" skips MSBuild when bin output exists (see run.bat).
if /i "%~1"=="run" (
  shift
  call "%~dp0scripts\run.bat" %*
  exit /b %ERRORLEVEL%
)
"%ProgramFiles%\dotnet\dotnet.exe" %*
