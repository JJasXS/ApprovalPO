# Fast start: rebuild only when needed, then run DLL directly (no "dotnet run" / MSBuild).
Set-Location $PSScriptRoot

. (Join-Path $PSScriptRoot 'scripts\Stop-ApprovalPO.ps1')

$dll = Join-Path $PSScriptRoot 'bin\Debug\net8.0\ApprovalPO.dll'
$needBuild = -not (Test-Path $dll)

if (-not $needBuild) {
    $dllTime = (Get-Item $dll).LastWriteTime
    $newer = Get-ChildItem -Recurse -Include *.cs, *.cshtml, *.csproj -ErrorAction SilentlyContinue |
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

$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ASPNETCORE_URLS = 'https://localhost:5058;http://localhost:5057'

Write-Host 'Starting ApprovalPO...' -ForegroundColor DarkGray
dotnet exec $dll @args
