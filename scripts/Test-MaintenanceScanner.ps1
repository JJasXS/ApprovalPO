# End-to-end smoke test for the Maintenance Scanner module in ApprovalPO.
# Runs the real OTP login (dev mode echoes the OTP in JSON) then exercises
# every scanner endpoint: Validate (positive + negative), Locations, and
# Insert (against the running Firebird via the tenant resolver).
#
# Designed to be safe to run repeatedly:
#   * Negative-path tests use clearly synthetic codes ("__APO_TEST_XYZ__").
#   * Insert is gated behind -RunInsert because it writes a real row.

[CmdletBinding()]
param(
    [string]$BaseUrl  = 'http://127.0.0.1:2095',
    [string]$Email    = 'jason.choo2004@gmail.com',
    [string]$SampleCode = '',     # If provided, also runs a positive Validate.
    [string]$Operator = 'apo-test',
    [switch]$RunInsert            # Only set when you want a real INSERT row.
)

$ErrorActionPreference = 'Stop'
$ProgressPreference   = 'SilentlyContinue'

function Write-Section($t) {
    Write-Host ''
    Write-Host ('=== ' + $t + ' ===') -ForegroundColor Cyan
}

function Get-AntiForgery($html) {
    if ($html -match 'name="__RequestVerificationToken"[^>]+value="([^"]+)"') {
        return $matches[1]
    }
    return $null
}

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

# ---------------------------------------------------------------
# 1) GET /Login - prime cookies (Login uses [IgnoreAntiforgeryToken])
# ---------------------------------------------------------------
Write-Section '1) GET /Login (prime cookies)'
$loginGet = Invoke-WebRequest -Uri "$BaseUrl/Login" -WebSession $session -UseBasicParsing
Write-Host ("  HTTP {0} | bytes={1}" -f $loginGet.StatusCode, $loginGet.Content.Length)

# ---------------------------------------------------------------
# 2) POST /Login?handler=SendOtp - dev mode returns devOtp
# ---------------------------------------------------------------
Write-Section "2) POST /Login?handler=SendOtp ($Email)"
$sendBody = @{ LoginId = $Email }
$sendResp = Invoke-RestMethod -Method Post -Uri "$BaseUrl/Login?handler=SendOtp" `
    -WebSession $session `
    -Body $sendBody `
    -ContentType 'application/x-www-form-urlencoded'

$sendResp | ConvertTo-Json -Depth 5 | Write-Host
if (-not $sendResp.success) { throw "SendOtp failed: $($sendResp.message)" }
$devOtp = $sendResp.devOtp
if (-not $devOtp) { throw 'SendOtp did not return a devOtp - is ASPNETCORE_ENVIRONMENT=Development?' }

# ---------------------------------------------------------------
# 3) POST /Login?handler=VerifyOtp - completes sign-in
# ---------------------------------------------------------------
Write-Section "3) POST /Login?handler=VerifyOtp (OTP=$devOtp)"
$verifyBody = @{ LoginId = $Email; Otp = $devOtp }
$verifyResp = Invoke-RestMethod -Method Post -Uri "$BaseUrl/Login?handler=VerifyOtp" `
    -WebSession $session `
    -Body $verifyBody `
    -ContentType 'application/x-www-form-urlencoded'
$verifyResp | ConvertTo-Json -Depth 5 | Write-Host
if (-not $verifyResp.success) { throw "VerifyOtp failed: $($verifyResp.message)" }
Write-Host ('  Auth cookie now present: ' +
    ([bool]($session.Cookies.GetCookies("$BaseUrl") | Where-Object { $_.Name -like '*Cookies*' }))) -ForegroundColor Green

# ---------------------------------------------------------------
# 4) GET /Dashboard - sanity check + look for the Maintenance Scanner card
# ---------------------------------------------------------------
Write-Section '4) GET /Dashboard (look for Maintenance Scanner card)'
$dash = Invoke-WebRequest -Uri "$BaseUrl/Dashboard" -WebSession $session -UseBasicParsing
$hasCard = $dash.Content -match 'Maintenance Scanner'
$hasLink = $dash.Content -match '/MaintenanceScanner'
Write-Host ("  HTTP {0} | card-text={1} | card-link={2}" -f $dash.StatusCode, $hasCard, $hasLink) `
    -ForegroundColor $(if ($hasCard -and $hasLink) {'Green'} else {'Yellow'})

# ---------------------------------------------------------------
# 5) GET /MaintenanceScanner - load module + get its CSRF
# ---------------------------------------------------------------
Write-Section '5) GET /MaintenanceScanner'
$scanPage = Invoke-WebRequest -Uri "$BaseUrl/MaintenanceScanner" -WebSession $session -UseBasicParsing
$scanCsrf = Get-AntiForgery $scanPage.Content
Write-Host ("  HTTP {0} | csrf={1} | size={2} bytes" -f $scanPage.StatusCode,
    ($(if ($scanCsrf) {'present'} else {'missing'})), $scanPage.Content.Length)
if (-not $scanCsrf) { throw 'MaintenanceScanner page did not render antiforgery token.' }

# Confirm key UI hooks are in the page
$expected = @(
    'id="ms-config"',
    'id="ms-start"',
    'id="ms-video-wrap"',
    'id="ms-manual-code"',
    'id="ms-location"',
    'id="ms-manual-location"',
    'id="ms-update"',
    'data-validate-url=',
    'data-locations-url=',
    'data-insert-url=',
    'maintenance-scanner.js',
    'name="__RequestVerificationToken"'
)
$missing = @()
foreach ($needle in $expected) {
    $ok = $scanPage.Content.Contains($needle)
    if (-not $ok) { $missing += $needle }
    Write-Host ("  hook {0,-40} -> {1}" -f $needle, $ok) -ForegroundColor $(if ($ok) {'Green'} else {'Red'})
}
if ($missing.Count -gt 0) { throw ('Missing UI hooks: ' + ($missing -join ', ')) }

# ---------------------------------------------------------------
# 6) GET /MaintenanceScanner?handler=Locations
# ---------------------------------------------------------------
Write-Section '6) GET /MaintenanceScanner?handler=Locations'
try {
    $locs = Invoke-RestMethod -Uri "$BaseUrl/MaintenanceScanner?handler=Locations" -WebSession $session
    $count = if ($locs.locations) { $locs.locations.Count } else { 0 }
    Write-Host ("  ok={0} | count={1}" -f $locs.success, $count) -ForegroundColor Green
    if ($count -gt 0) {
        $locs.locations | Select-Object -First 5 | Format-Table -AutoSize | Out-String | Write-Host
    }
} catch {
    Write-Host ("  Locations error: {0}" -f $_.Exception.Message) -ForegroundColor Red
}

# ---------------------------------------------------------------
# 7) POST /MaintenanceScanner?handler=Validate (negative path)
# ---------------------------------------------------------------
Write-Section '7) POST /MaintenanceScanner?handler=Validate (synthetic code => not found)'
$negBody = @{ code = '__APO_TEST_NO_SUCH_CODE__' } | ConvertTo-Json
$negResp = Invoke-RestMethod -Method Post -Uri "$BaseUrl/MaintenanceScanner?handler=Validate" `
    -WebSession $session `
    -Body $negBody `
    -ContentType 'application/json' `
    -Headers @{ 'X-CSRF-TOKEN' = $scanCsrf; 'X-Requested-With' = 'XMLHttpRequest' }
$negResp | ConvertTo-Json -Depth 5 | Write-Host
Write-Host ("  expected: exists=False | got: exists={0}" -f $negResp.exists) -ForegroundColor $(
    if ($negResp.exists -eq $false) {'Green'} else {'Yellow'})

# ---------------------------------------------------------------
# 8) POST /MaintenanceScanner?handler=Validate (positive path, optional)
# ---------------------------------------------------------------
if ($SampleCode) {
    Write-Section "8) POST /MaintenanceScanner?handler=Validate (real code: $SampleCode)"
    $posBody = @{ code = $SampleCode } | ConvertTo-Json
    $posResp = Invoke-RestMethod -Method Post -Uri "$BaseUrl/MaintenanceScanner?handler=Validate" `
        -WebSession $session `
        -Body $posBody `
        -ContentType 'application/json' `
        -Headers @{ 'X-CSRF-TOKEN' = $scanCsrf; 'X-Requested-With' = 'XMLHttpRequest' }
    $posResp | ConvertTo-Json -Depth 5 | Write-Host
}

# ---------------------------------------------------------------
# 9) POST /MaintenanceScanner?handler=InsertDetail (gated by -RunInsert)
# ---------------------------------------------------------------
if ($RunInsert -and $SampleCode) {
    Write-Section "9) POST /MaintenanceScanner?handler=InsertDetail (REAL WRITE for $SampleCode)"
    $insBody = @{
        code         = $SampleCode
        locationCode = $null
        operatorName = $Operator
        remark       = 'apo-smoke-test'
    } | ConvertTo-Json
    $insResp = Invoke-RestMethod -Method Post -Uri "$BaseUrl/MaintenanceScanner?handler=InsertDetail" `
        -WebSession $session `
        -Body $insBody `
        -ContentType 'application/json' `
        -Headers @{ 'X-CSRF-TOKEN' = $scanCsrf; 'X-Requested-With' = 'XMLHttpRequest' }
    $insResp | ConvertTo-Json -Depth 5 | Write-Host
} elseif ($RunInsert) {
    Write-Host 'Skipping Insert: pass -SampleCode <code> to perform a real write.' -ForegroundColor Yellow
}

Write-Section 'DONE'

