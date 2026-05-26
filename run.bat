@echo off

cd /d "%~dp0"



REM Rebuild when source is newer than the DLL, then start without MSBuild (dotnet exec).

set "DLL=bin\Debug\net8.0\ApprovalPO.dll"

set "NEED_BUILD=0"

if not exist "%DLL%" set "NEED_BUILD=1"

if "%NEED_BUILD%"=="0" (

  powershell -NoProfile -Command ^

    "$d=(Get-Item '%DLL%').LastWriteTime; $n=Get-ChildItem -Recurse -Include *.cs,*.cshtml,*.csproj -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' -and $_.LastWriteTime -gt $d } | Select-Object -First 1; if ($n) { exit 1 } else { exit 0 }"

  if errorlevel 1 set "NEED_BUILD=1"

)

if "%NEED_BUILD%"=="1" (

  echo Building...

  dotnet build -v q -nologo

  if errorlevel 1 exit /b 1

)



set ASPNETCORE_ENVIRONMENT=Development

set ASPNETCORE_URLS=https://localhost:5058;http://localhost:5057

echo Starting ApprovalPO...

"%ProgramFiles%\dotnet\dotnet.exe" exec "%DLL%" %*

