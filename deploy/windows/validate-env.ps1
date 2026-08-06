param(
    [Parameter(Mandatory = $true)]
    [string]$EnvPath
)

if (-not (Test-Path -LiteralPath $EnvPath)) {
    Write-Host "ERROR: .env file missing: $EnvPath"
    exit 2
}

$map = @{}
Get-Content -LiteralPath $EnvPath | ForEach-Object {
    $line = $_
    if ($line -match '^\s*#') { return }
    if ($line -match '^\s*([A-Za-z0-9_]+)\s*=\s*(.*)\s*$') {
        $map[$matches[1]] = $matches[2].Trim().Trim('"')
    }
}

$need = @('TENANT_CODE', 'AWS_REGION', 'AWS_ACCESS_KEY_ID', 'AWS_SECRET_ACCESS_KEY')
$bad = @()
foreach ($k in $need) {
    if (-not $map.ContainsKey($k) -or [string]::IsNullOrWhiteSpace($map[$k])) {
        $bad += $k
    }
}

if ($bad.Count -gt 0) {
    Write-Host ("ERROR: missing/empty: " + ($bad -join ', '))
    exit 3
}

Write-Host ("OK: TENANT_CODE=" + $map['TENANT_CODE'] + "  AWS_REGION=" + $map['AWS_REGION'] + "  AWS keys: present")
exit 0
