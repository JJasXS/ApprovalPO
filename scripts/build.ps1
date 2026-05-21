# Stop running app, then build (avoids ApprovalPO.dll locked errors).
Set-Location (Split-Path $PSScriptRoot -Parent)
. (Join-Path $PSScriptRoot 'Stop-ApprovalPO.ps1')
dotnet build @args
exit $LASTEXITCODE
