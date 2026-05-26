# Inspect PH_GR / PH_GRDTL columns via tenant Firebird connection.
# Usage: .\scripts\Inspect-PhGrSchema.ps1
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)

$dll = Join-Path $PWD 'bin\Debug\net8.0\FirebirdSql.Data.FirebirdClient.dll'
if (-not (Test-Path $dll)) {
    Write-Host 'Building...'
    dotnet build -v q -nologo | Out-Null
}

Add-Type -Path $dll

# Load .env
$envFile = Join-Path $PWD '.env'
if (Test-Path $envFile) {
    Get-Content $envFile | ForEach-Object {
        if ($_ -match '^\s*([^#=]+)=(.*)$') {
            [Environment]::SetEnvironmentVariable($matches[1].Trim(), $matches[2].Trim(), 'Process')
        }
    }
}

$tenant = $env:TENANT_CODE
if (-not $tenant) { $tenant = 'TNT10003' }

$baseUrl = 'https://v2wwsho311.execute-api.ap-southeast-1.amazonaws.com/default/proacc-tenant-config-api'
$uri = "$baseUrl" + '?tenantCode=' + [Uri]::EscapeDataString($tenant)
$json = Invoke-RestMethod -Uri $uri -Method Get
$root = $json
if ($json.body) { $root = $json.body | ConvertFrom-Json }

$host_ = $root.dbHost
$path = $root.dbPath
$port = if ($root.port) { $root.port } else { '3050' }
$user = 'SYSDBA'
$pass = 'masterkey'

$cs = "User=$user;Password=$pass;Database=${host_}/${port}:${path};Charset=UTF8;"
$conn = New-Object FirebirdSql.Data.FirebirdClient.FbConnection($cs)
$conn.Open()

foreach ($table in @('PH_GR', 'PH_GRDTL')) {
    Write-Host "`n=== $table columns ===" -ForegroundColor Cyan
    $cmd = $conn.CreateCommand()
    $cmd.CommandText = @"
SELECT TRIM(rf.RDB`$FIELD_NAME) AS COL_NAME
FROM RDB`$RELATION_FIELDS rf
WHERE rf.RDB`$RELATION_NAME = @T
ORDER BY rf.RDB`$FIELD_POSITION
"@
    $p = $cmd.Parameters.Add('@T', [FirebirdSql.Data.FirebirdClient.FbDbType]::Char)
    $p.Value = $table
    $r = $cmd.ExecuteReader()
    while ($r.Read()) {
        Write-Host ('  ' + $r.GetString(0).Trim())
    }
    $r.Close()
}

$conn.Close()
Write-Host "`nDone." -ForegroundColor Green
