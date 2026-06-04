# Run Firebird PO -> GR transfer test (default: first approved PO, excluding PO-00027).
# Usage:
#   .\scripts\Test-PoGrTransfer.ps1
#   .\scripts\Test-PoGrTransfer.ps1 -PoNumber PO-00026
param(
    [string]$PoNumber = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
Set-Location $root

. (Join-Path $PSScriptRoot 'Stop-ApprovalPO.ps1')

Write-Host 'Building ApprovalPO...' -ForegroundColor Cyan
dotnet build -v q -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$args = @()
if ($PoNumber) { $args += @('--po', $PoNumber) }
$args += @('--exclude', 'PO-00027')

Write-Host 'Running Firebird transfer test...' -ForegroundColor Cyan
dotnet run --project (Join-Path $PSScriptRoot 'TestPoGrTransfer\TestPoGrTransfer.csproj') -c Debug -- @args
$code = $LASTEXITCODE
if ($code -ne 0) { exit $code }

Write-Host 'Starting ApprovalPO...' -ForegroundColor Cyan
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$dll = Join-Path $root 'bin\Debug\net8.0\ApprovalPO.dll'
Start-Process -FilePath 'dotnet' -ArgumentList @('exec', $dll) -WorkingDirectory $root -WindowStyle Minimized
Start-Sleep -Seconds 3
Write-Host 'ApprovalPO started at http://localhost:2095' -ForegroundColor Green
