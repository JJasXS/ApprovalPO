@echo off
setlocal EnableExtensions EnableDelayedExpansion
title ApprovalPO Deploy

REM =============================================================================
REM ApprovalPO (e-Approval) -- full Windows deploy
REM Double-click this .cmd (do NOT paste into PowerShell).
REM Auto-elevates. Notepad .env FIRST, then deploy.
REM Log: C:\Temp\ApprovalPO-deploy.log
REM
REM Required .env keys:
REM   TENANT_CODE
REM   FIREBIRD_PASSWORD
REM   AWS_REGION
REM   AWS_ACCESS_KEY_ID
REM   AWS_SECRET_ACCESS_KEY
REM
REM Keep validate-env.ps1 / validate-ApprovalPO-env.ps1 next to this .cmd
REM =============================================================================

REM ------------ EDIT FOR EACH CLIENT ------------
set "APP_NAME=ApprovalPO"
set "EXE_NAME=ApprovalPO.exe"
set "REPO_URL=https://github.com/JJasXS/ApprovalPO.git"
set "GIT_BRANCH=main"
set "CSPROJ=ApprovalPO.csproj"

set "WORK_DIR=C:\ApprovalPO"
set "APP_DIR=C:\Apps\ApprovalPO\publish"
set "HTTP_PORT=2095"
set "ENV_PREP=%TEMP%\ApprovalPO.env.prepared"
set "ENV_BACKUP=%TEMP%\ApprovalPO.env.backup"
set "FW_RULE_NAME=ApprovalPO %HTTP_PORT%"
set "DEPLOY_LOG=C:\Temp\ApprovalPO-deploy.log"
REM ----------------------------------------------

REM --- Auto-elevate: parent exits immediately; work continues in Admin window ---
net session >nul 2>&1
if errorlevel 1 (
  echo.
  echo Not Administrator -- requesting UAC. Click YES.
  echo The deploy continues in a NEW Admin window.
  echo.
  (
    echo Set sh = CreateObject^("Shell.Application"^)
    echo sh.ShellExecute "cmd.exe", "/k cd /d ""%~dp0"" ^& call ""%~f0""", "%~dp0", "runas", 1
  ) > "%TEMP%\elevate_approvalpo.vbs"
  wscript //nologo "%TEMP%\elevate_approvalpo.vbs"
  exit /b 0
)

cd /d "%~dp0"
if not exist "C:\Temp" mkdir "C:\Temp"
echo ===== ApprovalPO deploy started %DATE% %TIME% =====> "%DEPLOY_LOG%"
call :log Script=%~f0
call :log Folder=%~dp0

echo.
echo [%DATE% %TIME%] ApprovalPO deploy (Administrator)
echo Script: %~f0
echo Log:    %DEPLOY_LOG%
echo.

REM Ensure validator sits next to this .cmd
if not exist "%~dp0validate-env.ps1" if exist "%~dp0validate-ApprovalPO-env.ps1" copy /Y "%~dp0validate-ApprovalPO-env.ps1" "%~dp0validate-env.ps1" >nul
if not exist "%~dp0validate-env.ps1" if exist "C:\Temp\validate-ApprovalPO-env.ps1" copy /Y "C:\Temp\validate-ApprovalPO-env.ps1" "%~dp0validate-env.ps1" >nul

REM =============================================================================
REM STEP 0 -- Enter .env FIRST (Notepad). Deploy starts only after Save + Close.
REM =============================================================================
echo ========== STEP 0 / ENV ==========
echo Fill these values, Save, then CLOSE Notepad:
echo   TENANT_CODE=...
echo   FIREBIRD_PASSWORD=...
echo   AWS_REGION=ap-southeast-1
echo   AWS_ACCESS_KEY_ID=...
echo   AWS_SECRET_ACCESS_KEY=...
echo.
echo Tip: AWS keys are usually the same for every client; TENANT_CODE / FIREBIRD_PASSWORD change per site.
echo.
call :log STEP0 notepad env

if exist "%APP_DIR%\.env" (
  copy /Y "%APP_DIR%\.env" "%ENV_PREP%" >nul
  echo Prefill: copied from %APP_DIR%\.env
) else if exist "%ENV_BACKUP%" (
  copy /Y "%ENV_BACKUP%" "%ENV_PREP%" >nul
  echo Prefill: copied from %ENV_BACKUP%
) else if exist "C:\Temp\ApprovalPO.env.backup" (
  copy /Y "C:\Temp\ApprovalPO.env.backup" "%ENV_PREP%" >nul
  echo Prefill: copied from C:\Temp\ApprovalPO.env.backup
) else if exist "C:\Users\sqlsupport\ApprovalPO\.env" (
  copy /Y "C:\Users\sqlsupport\ApprovalPO\.env" "%ENV_PREP%" >nul
  echo Prefill: copied from repo .env
) else (
  (
    echo # ApprovalPO / e-Approval
    echo TENANT_CODE=
    echo FIREBIRD_PASSWORD=
    echo AWS_REGION=ap-southeast-1
    echo AWS_ACCESS_KEY_ID=
    echo AWS_SECRET_ACCESS_KEY=
  ) > "%ENV_PREP%"
  echo Prefill: new template created
)

echo.
echo Opening Notepad: %ENV_PREP%
echo After you Save and CLOSE Notepad, deploy continues...
echo.
notepad "%ENV_PREP%"

set "ENV_VALIDATOR=%~dp0validate-env.ps1"
if not exist "%ENV_VALIDATOR%" set "ENV_VALIDATOR=%~dp0validate-ApprovalPO-env.ps1"
if not exist "%ENV_VALIDATOR%" set "ENV_VALIDATOR=C:\Temp\validate-ApprovalPO-env.ps1"
if not exist "%ENV_VALIDATOR%" set "ENV_VALIDATOR=C:\Temp\validate-env.ps1"
if not exist "%ENV_VALIDATOR%" (
  echo ERROR: validate-env.ps1 not found next to this script or in C:\Temp.
  call :log ERROR missing validate-env.ps1
  goto :fail
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%ENV_VALIDATOR%" -EnvPath "%ENV_PREP%"
if errorlevel 1 (
  echo.
  echo .env is incomplete. Fix the values and run this script again.
  call :log ERROR env validate failed
  goto :fail
)

copy /Y "%ENV_PREP%" "%ENV_BACKUP%" >nul
copy /Y "%ENV_PREP%" "C:\Temp\ApprovalPO.env.backup" >nul
echo Env saved. Starting deploy...
echo.
call :log Env OK

where git >nul 2>&1
if errorlevel 1 (
  echo Installing Git via winget...
  winget install --id Git.Git -e --source winget --accept-package-agreements --accept-source-agreements
)
where dotnet >nul 2>&1
if errorlevel 1 (
  echo Installing .NET SDK 8 via winget...
  winget install --id Microsoft.DotNet.SDK.8 -e --source winget --accept-package-agreements --accept-source-agreements
)
REM Refresh PATH for this window after winget
set "PATH=%PATH%;%ProgramFiles%\Git\cmd;%ProgramFiles%\dotnet;%ProgramFiles(x86)%\dotnet"
where git >nul 2>&1
if errorlevel 1 (
  echo ERROR: Git still not in PATH. Open a new Admin CMD and retry.
  call :log ERROR git missing
  goto :fail
)
where dotnet >nul 2>&1
if errorlevel 1 (
  echo ERROR: dotnet still not in PATH. Open a new Admin CMD and retry.
  call :log ERROR dotnet missing
  goto :fail
)

echo [0/10] Firewall allow inbound TCP %HTTP_PORT%...
call :log STEP0 firewall
netsh advfirewall firewall show rule name="%FW_RULE_NAME%" >nul 2>&1
if errorlevel 1 (
  netsh advfirewall firewall add rule name="%FW_RULE_NAME%" dir=in action=allow protocol=TCP localport=%HTTP_PORT%
) else (
  echo   Rule already exists: %FW_RULE_NAME%
)

echo [1/10] Stop / remove old service...
call :log STEP1 stop service
sc query "%APP_NAME%" >nul 2>&1
if not errorlevel 1 (
  sc stop "%APP_NAME%" >nul 2>&1
  timeout /t 5 /nobreak >nul
)
taskkill /F /IM "%EXE_NAME%" >nul 2>&1
sc delete "%APP_NAME%" >nul 2>&1
timeout /t 3 /nobreak >nul

echo [2/10] Free port %HTTP_PORT%...
call :log STEP2 free port
for /f "tokens=5" %%P in ('netstat -ano ^| findstr ":%HTTP_PORT%" ^| findstr LISTENING') do (
  echo   Ending PID %%P
  taskkill /F /PID %%P >nul 2>&1
)
timeout /t 2 /nobreak >nul

echo [3/10] Wipe old source / publish folders...
call :log STEP3 wipe
if exist "%WORK_DIR%" rmdir /s /q "%WORK_DIR%"
if exist "%APP_DIR%" rmdir /s /q "%APP_DIR%"
timeout /t 1 /nobreak >nul
if not exist "C:\Apps\ApprovalPO" mkdir "C:\Apps\ApprovalPO"
if not exist "%APP_DIR%" mkdir "%APP_DIR%"

echo [4/10] Clone GitHub branch %GIT_BRANCH%...
call :log STEP4 clone
git clone --depth 1 -b %GIT_BRANCH% "%REPO_URL%" "%WORK_DIR%"
if errorlevel 1 (
  echo ERROR: git clone failed.
  call :log ERROR clone failed
  goto :fail
)
if not exist "%WORK_DIR%\%CSPROJ%" (
  echo ERROR: clone missing %CSPROJ%
  call :log ERROR missing csproj
  goto :fail
)

echo [5/10] Clean + publish self-contained win-x64...
echo   This can take 1-3 minutes -- leave this window open.
call :log STEP5 publish start
pushd "%WORK_DIR%"
dotnet clean "%CSPROJ%" -c Release
if exist bin rmdir /s /q bin
if exist obj rmdir /s /q obj
dotnet restore "%CSPROJ%"
if errorlevel 1 (
  popd
  echo ERROR: dotnet restore failed.
  call :log ERROR restore failed
  goto :fail
)
dotnet publish "%CSPROJ%" -c Release -r win-x64 --self-contained true -o "%APP_DIR%"
if errorlevel 1 (
  popd
  echo ERROR: dotnet publish failed.
  call :log ERROR publish failed
  goto :fail
)
popd
call :log STEP5 publish ok

if not exist "%APP_DIR%\%EXE_NAME%" (
  echo ERROR: %EXE_NAME% missing after publish.
  call :log ERROR exe missing
  goto :fail
)

echo [6/10] Install prepared .env...
call :log STEP6 env
copy /Y "%ENV_PREP%" "%APP_DIR%\.env" >nul
if not exist "%APP_DIR%\.env" (
  echo ERROR: failed to write %APP_DIR%\.env
  call :log ERROR env copy failed
  goto :fail
)

echo [7/10] Register Windows Service...
call :log STEP7 sc create
sc create "%APP_NAME%" binPath= "\"%APP_DIR%\%EXE_NAME%\"" start= auto
if errorlevel 1 (
  echo ERROR: sc create failed.
  call :log ERROR sc create failed
  goto :fail
)
sc description "%APP_NAME%" "ApprovalPO purchase order approvals (ASP.NET Core / Kestrel)."
sc failure "%APP_NAME%" reset= 86400 actions= restart/60000/restart/60000/restart/60000 >nul 2>&1

echo [8/10] Start service...
call :log STEP8 start
sc start "%APP_NAME%"
timeout /t 6 /nobreak >nul

echo [9/10] Cleanup clone...
call :log STEP9 cleanup
cd /d C:\
if exist "%WORK_DIR%" rmdir /s /q "%WORK_DIR%"

echo [10/10] Verify...
call :log STEP10 verify
echo.
echo ========== VERIFY ==========
sc query "%APP_NAME%"
echo.
tasklist | findstr /I "%EXE_NAME%"
echo.
echo Listening on %HTTP_PORT%:
netstat -ano | findstr ":%HTTP_PORT%"
echo.
dir "%APP_DIR%\*.exe"
echo.
if exist "%APP_DIR%\%EXE_NAME%" (echo EXE: OK) else (echo EXE: MISSING)
if exist "%APP_DIR%\.env" (echo .env: OK) else (echo .env: MISSING)
echo.
echo Browser: http://127.0.0.1:%HTTP_PORT%/Login
echo Log: %DEPLOY_LOG%
echo.
start "" "http://127.0.0.1:%HTTP_PORT%/Login"
call :log DONE OK
echo Done. Press any key to close.
pause
endlocal
exit /b 0

:log
>>"%DEPLOY_LOG%" echo [%DATE% %TIME%] %*
exit /b 0

:fail
echo.
echo Deploy FAILED. See log: %DEPLOY_LOG%
call :log FAILED
echo Press any key to close.
pause
endlocal
exit /b 1
