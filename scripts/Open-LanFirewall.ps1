# Run once as Administrator: allows phones on your Wi-Fi to reach ApprovalPO ports.
# Right-click PowerShell -> Run as administrator, then:
#   cd C:\Users\sqlsupport\ApprovalPO
#   .\scripts\Open-LanFirewall.ps1

$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'Run this script in an elevated (Administrator) PowerShell window.'
}

foreach ($port in 5057, 5058) {
    $name = "ApprovalPO LAN TCP $port"
    $existing = Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue
    if ($existing) {
        Write-Host "Firewall rule already exists: $name"
        continue
    }
    New-NetFirewallRule -DisplayName $name -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow -Profile Private | Out-Null
    Write-Host "Added inbound allow: TCP $port (Private network profile)"
}

Write-Host ''
Write-Host 'Done. Start the app with:  .\run-lan.ps1' -ForegroundColor Green
