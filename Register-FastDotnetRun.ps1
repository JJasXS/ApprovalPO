# One-time setup: "dotnet run" in this repo uses fast scripts (run.ps1 / run-lan.ps1).
# Run:  . .\Register-FastDotnetRun.ps1
# Then restart PowerShell.

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
        `$lan = `$false
        `$pass = [System.Collections.Generic.List[string]]::new()
        for (`$i = 1; `$i -lt `$CommandArgs.Length; `$i++) {
            if (`$CommandArgs[`$i] -in '--launch-profile', '-lp') {
                if (`$i + 1 -lt `$CommandArgs.Length -and `$CommandArgs[`$i + 1] -eq 'lan') {
                    `$lan = `$true
                    `$i++
                    continue
                }
            }
            `$pass.Add(`$CommandArgs[`$i])
        }
        if (`$lan) {
            & '$repoRoot\scripts\run-lan.ps1' @(`$pass)
        } else {
            & '$repoRoot\scripts\run.ps1' @(`$pass)
        }
        return
    }
    & (Get-Command dotnet.exe -CommandType Application | Select-Object -First 1).Source @CommandArgs
}

"@

if (Test-Path $profilePath) {
    $existing = Get-Content $profilePath -Raw
    if ($existing -match 'ApprovalPO: fast dotnet run') {
        $updated = $existing -replace '(?s)# ApprovalPO: fast dotnet run.*?^}\r?\n', $block.TrimEnd()
        if ($updated -eq $existing) {
            Write-Host 'Replacing ApprovalPO dotnet hook in profile...'
            $updated = ($existing -replace '(?s)# ApprovalPO: fast dotnet run.*', $block.TrimEnd())
        }
        Set-Content -Path $profilePath -Value $updated -NoNewline
        Write-Host "Updated fast dotnet run hook in: $profilePath"
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
Write-Host 'Restart PowerShell, then from ApprovalPO:' -ForegroundColor Cyan
Write-Host '  dotnet run --launch-profile lan   (phone / Wi-Fi — same as .\scripts\run-lan.ps1)'
Write-Host '  dotnet run                        (PC only — localhost)'
Write-Host 'Or:  .\scripts\run-lan.ps1   .\scripts\run.ps1'
