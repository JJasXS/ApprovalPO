# Exercises the Admin / Maintenance separation end-to-end against a running
# ApprovalPO instance. Logs in as one email (dev OTP), then probes the admin-
# only and maintenance pages and prints the resulting HTTP status of each.
#
# This script assumes the tenant currently has NO userRoles block configured
# in AWS (legacy mode), in which case every user is granted Admin and should
# be able to reach every page. Use it as the baseline regression check.

[CmdletBinding()]
param(
    [string]$BaseUrl = 'http://127.0.0.1:2095',
    [string]$Email   = 'jason.choo2004@gmail.com'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference   = 'SilentlyContinue'

function Write-Section($t) {
    Write-Host ''
    Write-Host ('=== ' + $t + ' ===') -ForegroundColor Cyan
}

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

Write-Section "Login as $Email"
Invoke-WebRequest -Uri "$BaseUrl/Login" -WebSession $session -UseBasicParsing | Out-Null
$send = Invoke-RestMethod -Method Post -Uri "$BaseUrl/Login?handler=SendOtp" `
    -WebSession $session -Body @{ LoginId = $Email } -ContentType 'application/x-www-form-urlencoded'
if (-not $send.success) { throw "SendOtp failed: $($send.message)" }
$verify = Invoke-RestMethod -Method Post -Uri "$BaseUrl/Login?handler=VerifyOtp" `
    -WebSession $session -Body @{ LoginId = $Email; Otp = $send.devOtp } -ContentType 'application/x-www-form-urlencoded'
if (-not $verify.success) { throw "VerifyOtp failed: $($verify.message)" }
Write-Host ("  signed in -> redirectUrl={0}" -f $verify.redirectUrl) -ForegroundColor Green

function Get-Status($path) {
    try {
        $r = Invoke-WebRequest -Uri "$BaseUrl$path" -WebSession $session -UseBasicParsing -MaximumRedirection 0 -ErrorAction Stop
        return [int]$r.StatusCode
    } catch [System.Net.WebException] {
        $resp = $_.Exception.Response
        if ($resp -ne $null) { return [int]$resp.StatusCode }
        throw
    } catch {
        # Invoke-WebRequest in PS5 throws on redirects with MaximumRedirection=0; surface the status from the inner response.
        if ($_.Exception.Response) { return [int]$_.Exception.Response.StatusCode }
        throw
    }
}

Write-Section 'Page access matrix'
$rows = @(
    @{ Page='/Dashboard';               Expect='200 (always for signed in)' },
    @{ Page='/PurchaseOrders';          Expect='200 if Admin, 302 to /Login if not' },
    @{ Page='/SalesOrders';             Expect='200 if Admin, 302 to /Login if not' },
    @{ Page='/ScanPO';                  Expect='200 if Admin, 302 to /Login if not' },
    @{ Page='/MaintenanceScanner';      Expect='200 for Admin or Maintenance' }
)
foreach ($row in $rows) {
    $st = Get-Status $row.Page
    $color = if ($st -eq 200) {'Green'} elseif ($st -ge 300 -and $st -lt 400) {'Yellow'} else {'Red'}
    Write-Host ("  {0,-22} -> HTTP {1}   [{2}]" -f $row.Page, $st, $row.Expect) -ForegroundColor $color
}

Write-Section 'Dashboard cards visible'
$dash = Invoke-WebRequest -Uri "$BaseUrl/Dashboard" -WebSession $session -UseBasicParsing
$cards = @(
    @{ Name='Purchase Approval';    Pattern='Purchase Approval' },
    @{ Name='Sales Approval';       Pattern='Sales Approval' },
    @{ Name='Scan Purchase Orders'; Pattern='Scan PO' },
    @{ Name='Received Goods';       Pattern='Received Goods' },
    @{ Name='Maintenance Scanner';  Pattern='Maintenance Scanner' }
)
foreach ($c in $cards) {
    $present = $dash.Content -match $c.Pattern
    Write-Host ("  {0,-22} -> visible={1}" -f $c.Name, $present) -ForegroundColor $(if ($present) {'Green'} else {'Yellow'})
}

Write-Section 'DONE'
