@echo off
setlocal EnableExtensions
REM =============================================================================
REM  ApprovalPO — Windows Service deploy (client server)
REM  Similar flow to ABS_System / ProAccScanner: publish self-contained, sc create.
REM  Prerequisites (run once): Git + .NET 8 SDK (this project targets net8.0).
REM    winget install --id Git.Git -e --source winget
REM    winget install --id Microsoft.DotNet.SDK.8 -e --source winget
REM =============================================================================

set "REPO_DIR=C:\ApprovalPO"
set "PUBLISH_DIR=C:\Apps\ApprovalPO\publish"
set "SERVICE_NAME=ApprovalPO"
set "EXE_NAME=ApprovalPO.exe"
set "GIT_URL=https://github.com/JJasXS/ApprovalPO.git"

echo.
echo === 1) Ensure repo exists: %REPO_DIR% ===
if not exist "%REPO_DIR%\.git" (
  echo Cloning %GIT_URL% ...
  git clone "%GIT_URL%" "%REPO_DIR%"
  if errorlevel 1 goto :fail
) else (
  echo Pulling latest ...
  pushd "%REPO_DIR%"
  git pull
  if errorlevel 1 popd & goto :fail
  popd
)

echo.
echo === 2) Stop service / kill process ===
sc stop %SERVICE_NAME% 2>nul
taskkill /F /IM %EXE_NAME% 2>nul
timeout /t 2 /nobreak >nul
sc delete %SERVICE_NAME% 2>nul

echo.
echo === 3) Clean + remove old publish ===
pushd "%REPO_DIR%"
dotnet clean .\ApprovalPO.csproj -c Release
if errorlevel 1 popd & goto :fail
if exist .\bin rmdir /s /q .\bin
if exist .\obj rmdir /s /q .\obj
popd
if exist "%PUBLISH_DIR%" rmdir /s /q "%PUBLISH_DIR%"
mkdir "%PUBLISH_DIR%" 2>nul

echo.
echo === 4) Publish (self-contained win-x64) ===
pushd "%REPO_DIR%"
dotnet publish .\ApprovalPO.csproj -c Release -r win-x64 --self-contained true -o "%PUBLISH_DIR%"
if errorlevel 1 popd & goto :fail
popd

echo.
echo === 5) .env — Notepad opens; type e.g. TENANT_CODE=TNT10003, Save, then close Notepad ===
notepad "%PUBLISH_DIR%\.env"

echo.
echo === 6) Register Windows Service ===
sc create %SERVICE_NAME% binPath= "\"%PUBLISH_DIR%\%EXE_NAME%\"" start= auto
if errorlevel 1 goto :fail
sc description %SERVICE_NAME% "ApprovalPO — purchase order approvals (ASP.NET Core / Kestrel)."
sc start %SERVICE_NAME%
if errorlevel 1 goto :fail

echo.
echo === 7) Verify ===
sc query %SERVICE_NAME%
tasklist | findstr /I ApprovalPO
netstat -ano | findstr :5288

echo.
echo Done. Default HTTP URL: http://0.0.0.0:5288 (see appsettings.Production.json).
echo Adjust Kestrel URLs or TLS in publish folder as needed.
goto :eof

:fail
echo.
echo FAILED — check messages above.
exit /b 1
