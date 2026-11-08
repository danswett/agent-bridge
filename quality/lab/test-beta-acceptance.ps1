# Offline checks for the Beta acceptance harness (#1185): the secret handling, the simulated
# agent against a stand-in hook, and the workflow's safety properties. No network, no install.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'accept-secrets.ps1')

$failures = 0
function Check([string]$name, [scriptblock]$body) {
    try { & $body; Write-Host "ok   $name" }
    catch { $script:failures++; Write-Host "FAIL $name : $($_.Exception.Message)" }
}
function Assert($c, $m) { if (-not $c) { throw $m } }

Check 'TOTP matches the RFC 6238 SHA-1 vectors' {
    $secret = 'GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ' # "12345678901234567890"
    Assert ((New-TotpCode $secret ([DateTimeOffset]::FromUnixTimeSeconds(59))) -eq '287082') 'T=59'
    Assert ((New-TotpCode $secret ([DateTimeOffset]::FromUnixTimeSeconds(1111111109))) -eq '081804') 'T=1111111109'
    Assert ((New-TotpCode $secret ([DateTimeOffset]::FromUnixTimeSeconds(2000000000))) -eq '279037') 'T=2000000000'
}

Check 'a missing or blank secret is reported by name, so the login steps stay NOT RUN' {
    foreach ($n in 'ACCEPT_GH_USER', 'ACCEPT_GH_PASSWORD', 'ACCEPT_GH_TOTP_SECRET') { [Environment]::SetEnvironmentVariable($n, $null) }
    Assert ((Get-MissingAcceptSecrets).Count -eq 3) 'all three should be missing'
    [Environment]::SetEnvironmentVariable('ACCEPT_GH_USER', 'x')
    [Environment]::SetEnvironmentVariable('ACCEPT_GH_PASSWORD', '  ')
    Assert ((Get-MissingAcceptSecrets) -join ',' -eq 'ACCEPT_GH_PASSWORD,ACCEPT_GH_TOTP_SECRET') 'blank counts as missing'
    [Environment]::SetEnvironmentVariable('ACCEPT_GH_USER', $null); [Environment]::SetEnvironmentVariable('ACCEPT_GH_PASSWORD', $null)
}

Check 'every secret value is masked and never printed otherwise' {
    [Environment]::SetEnvironmentVariable('ACCEPT_GH_PASSWORD', 'hunter2-test')
    $out = Protect-AcceptSecrets 6>&1 | Out-String
    [Environment]::SetEnvironmentVariable('ACCEPT_GH_PASSWORD', $null)
    Assert ($out -match '::add-mask::hunter2-test') 'the value was not masked'
}

Check 'the simulated agent writes a transcript with the question and runs the hook for each event' {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) "accept-sim-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory $scratch | Out-Null
    try {
        $stand = Join-Path $scratch 'hook.cmd'
        Set-Content $stand "@echo off`r`nif `"%2`"==`"ask_user`" (echo {`"permissionDecision`":`"allow`"}) else echo {}"
        $r = & (Join-Path $PSScriptRoot 'agent-sim.ps1') -Hook $stand -CopilotHome (Join-Path $scratch 'home') -Question 'Q-marker?'
        Assert ($r.Hooks.Count -eq 2 -and ($r.Hooks.Exit | Select-Object -Unique) -eq 0) 'hooks did not run and exit 0'
        Assert (($r.Hooks | Where-Object Hook -eq 'ask_user').Output -match 'allow') 'ask_user reply'
        $lines = Get-Content $r.Transcript | ForEach-Object { $_ | ConvertFrom-Json }
        Assert ($lines[0].type -eq 'session.start' -and $lines[1].type -eq 'user.message') 'event order'
        Assert ((Get-Content $r.Transcript -Raw) -match 'Q-marker\?') 'question missing'
    }
    finally { Remove-Item $scratch -Recurse -Force }
}

Check 'the workflow is dispatch-only, has no cron, and gives secrets only to the run step' {
    $y = (Get-Content (Join-Path $PSScriptRoot '..\..\.github\workflows\beta-acceptance.yml') | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
    Assert ($y -match '(?m)^on:\s*\n\s+workflow_dispatch:\s*\n\s*\npermissions') 'trigger is not workflow_dispatch alone'
    Assert ($y -notmatch 'pull_request|schedule|cron') 'a forbidden trigger'
    Assert (([regex]::Matches($y, 'secrets\.ACCEPT_')).Count -eq 3) 'secrets must appear once each, in one step'
    Assert ($y -notmatch 'uses: [^\n]+@(?![0-9a-f]{40}\b)') 'an action is not pinned to a SHA'
}

exit ($failures ? 1 : 0)
