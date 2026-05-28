# Stop ApprovalPO and dotnet exec hosts so builds can replace ApprovalPO.dll.
Get-Process -Name ApprovalPO -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -like '*ApprovalPO.dll*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

$settingsPath = Join-Path $PSScriptRoot '..\appsettings.json'
$ports = @(2095, 2096)
if (Test-Path $settingsPath) {
    try {
        $cfg = Get-Content $settingsPath -Raw | ConvertFrom-Json
        if ($cfg.Approval.PublicHttpPort) { $ports[0] = [int]$cfg.Approval.PublicHttpPort }
        if ($cfg.Approval.PublicHttpsPort) { $ports[1] = [int]$cfg.Approval.PublicHttpsPort }
    } catch { }
}

# Legacy dev ports (before 2095/2096)
$ports += 5057, 5058

foreach ($port in ($ports | Select-Object -Unique)) {
    Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue |
        ForEach-Object { Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }
}

Start-Sleep -Seconds 1
Write-Host "Stopped ApprovalPO / dotnet hosts on ports $($ports[0])-$($ports[1])." -ForegroundColor DarkGray
