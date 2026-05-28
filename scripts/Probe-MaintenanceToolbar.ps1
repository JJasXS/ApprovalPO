$ErrorActionPreference = 'Stop'
$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession
Invoke-WebRequest -Uri 'http://127.0.0.1:2095/Login' -WebSession $session -UseBasicParsing | Out-Null
$send = Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:2095/Login?handler=SendOtp' `
    -WebSession $session -Body @{ LoginId = 'jason.choo2004@gmail.com' } -ContentType 'application/x-www-form-urlencoded'
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:2095/Login?handler=VerifyOtp' `
    -WebSession $session -Body @{ LoginId = 'jason.choo2004@gmail.com'; Otp = $send.devOtp } `
    -ContentType 'application/x-www-form-urlencoded' | Out-Null

$ms = Invoke-WebRequest -Uri 'http://127.0.0.1:2095/MaintenanceScanner' -WebSession $session -UseBasicParsing
$lines = $ms.Content -split "`n"
$any = $false
for ($i = 0; $i -lt $lines.Length; $i++) {
    if ($lines[$i] -match 'ms-orientation|ms-toolbar|ms-flash|ms-start|ms-scan-top') {
        Write-Host ("L{0:0000}: {1}" -f ($i + 1), $lines[$i].Trim())
        $any = $true
    }
}
if (-not $any) { Write-Host 'No scanner-control markers found in rendered HTML.' -ForegroundColor Red }

# Also confirm CSS is updated
$css = Invoke-WebRequest -Uri 'http://127.0.0.1:2095/css/maintenance-scanner.css' -UseBasicParsing
$hasNewPrimary = $css.Content -match 'ms-scan-top__primary'
$hasOldToolbar = $css.Content -match '\.ms-toolbar\b'
Write-Host ''
Write-Host ('CSS has new ms-scan-top__primary    : ' + $hasNewPrimary)  -ForegroundColor $(if ($hasNewPrimary) {'Green'} else {'Red'})
Write-Host ('CSS still has old .ms-toolbar rule  : ' + $hasOldToolbar)  -ForegroundColor $(if (-not $hasOldToolbar) {'Green'} else {'Yellow'})
