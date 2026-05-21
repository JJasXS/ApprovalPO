# Run ApprovalPO so phones on the same Wi-Fi can connect (HTTP + HTTPS on all interfaces).
Set-Location $PSScriptRoot

# Stop a previous run so dotnet build can copy ApprovalPO.dll
Get-Process -Name ApprovalPO -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($port in 5057, 5058) {
    Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
}
Start-Sleep -Seconds 1

$dll = Join-Path $PSScriptRoot 'bin\Debug\net8.0\ApprovalPO.dll'
$needBuild = -not (Test-Path $dll)

if (-not $needBuild) {
    $dllTime = (Get-Item $dll).LastWriteTime
    $newer = Get-ChildItem -Recurse -Include *.cs, *.cshtml, *.csproj, *.js, *.css -ErrorAction SilentlyContinue |
        Where-Object {
            $_.FullName -notmatch '\\(bin|obj|artifacts|_lanbuild|_routechk|_verifybuild|_audit_build|_build_verify|_bc\d+|_out_[^\\]+)\\' -and
            $_.LastWriteTime -gt $dllTime
        } |
        Select-Object -First 1
    if ($newer) { $needBuild = $true }
}

if ($needBuild) {
    Write-Host 'Building...'
    dotnet build -v q -nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$ip = (Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -notlike '127.*' -and $_.PrefixOrigin -ne 'WellKnown' } |
    Select-Object -First 1).IPAddress

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:APPROVALPO_LISTEN_LAN = 'true'
Remove-Item Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue

Write-Host 'LAN mode: HTTP *:5057, HTTPS *:5058' -ForegroundColor Cyan
if ($ip) {
    Write-Host "On your phone (same Wi-Fi):" -ForegroundColor Green
    Write-Host "  http://${ip}:5057/ScanPO   (Scan QR -> Take photo)"
    Write-Host "  https://${ip}:5058/ScanPO  (live camera; optional)"
}
Write-Host ''

dotnet exec $dll @args
