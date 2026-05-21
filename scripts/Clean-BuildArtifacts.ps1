# Remove bloated local build/publish folders (safe to re-create with dotnet build).
$repoRoot = Split-Path $PSScriptRoot -Parent
Set-Location $repoRoot

Get-Process -Name ApprovalPO -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($port in 5057, 5058) {
    Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
}
Start-Sleep -Seconds 1

$dirs = @(
    'bin', 'obj', 'artifacts',
    '_lanbuild', '_routechk', '_verifybuild', '_audit_build', '_build_verify',
    '_bc10', '_bc11', '_bc12',
    '_out_auth', '_out_hint', '_out_lines', '_out_nomock', '_out_noprobe', '_out_pendingfix'
)

foreach ($name in $dirs) {
    $path = Join-Path $repoRoot $name
    if (-not (Test-Path $path)) { continue }
    Write-Host "Removing $name ..."
    cmd /c "rmdir /s /q `"$path`""
}

Write-Host 'Done. Run:  dotnet build   then  .\run.ps1' -ForegroundColor Green
