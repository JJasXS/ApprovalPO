# One-time setup: makes "dotnet run" in this repo skip MSBuild when bin\Debug\net8.0\ApprovalPO.dll exists.
# Run:  . .\Register-FastDotnetRun.ps1
# Then restart PowerShell (or reload profile).

$repoRoot = $PSScriptRoot
$marker = Join-Path $repoRoot '.fast-dotnet-run-installed'

if (-not (Test-Path (Join-Path $repoRoot 'ApprovalPO.csproj'))) {
    Write-Error 'Run this script from the ApprovalPO project folder.'
    exit 1
}

$profilePath = $PROFILE.CurrentUserAllHosts
$profileDir = Split-Path $profilePath -Parent
if (-not (Test-Path $profileDir)) {
    New-Item -ItemType Directory -Path $profileDir -Force | Out-Null
}

$block = @"

# ApprovalPO: fast dotnet run (added by Register-FastDotnetRun.ps1)
function global:dotnet {
    param([Parameter(ValueFromRemainingArguments = `$true)][string[]] `$CommandArgs)
    if (`$CommandArgs.Count -ge 1 -and `$CommandArgs[0] -eq 'run' -and (Test-Path (Join-Path (Get-Location).Path 'ApprovalPO.csproj'))) {
        & '$repoRoot\run.ps1' @(`$CommandArgs[1..(`$CommandArgs.Length - 1)])
        return
    }
    & (Get-Command dotnet.exe -CommandType Application | Select-Object -First 1).Source @CommandArgs
}

"@

if (Test-Path $profilePath) {
    $existing = Get-Content $profilePath -Raw
    if ($existing -match 'ApprovalPO: fast dotnet run') {
        Write-Host 'PowerShell profile already has ApprovalPO fast dotnet run.'
    }
    else {
        Add-Content -Path $profilePath -Value $block
        Write-Host "Appended fast dotnet run hook to: $profilePath"
    }
}
else {
    Set-Content -Path $profilePath -Value $block.TrimStart()
    Write-Host "Created PowerShell profile: $profilePath"
}

Set-Content -Path $marker -Value (Get-Date).ToString('o')
Write-Host ''
Write-Host 'Restart PowerShell, then from ApprovalPO:  dotnet run   (fast — uses run.ps1 / dotnet exec)'
Write-Host 'Or type:  .\run.ps1   or   run'
Write-Host 'After code edits: rebuild happens automatically when source is newer than the DLL'
Write-Host 'Avoid plain dotnet run without this hook — it always runs MSBuild and feels slow'
