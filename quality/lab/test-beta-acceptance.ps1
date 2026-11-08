# Offline checks for the Beta acceptance harness (#1185): the simulated agent against a
# stand-in hook, and the workflow's safety properties. No network, no install. Runs on Windows
# and Linux (the lab-scripts suite is a Linux CI job), so paths are built with Join-Path.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$failures = 0
function Check([string]$name, [scriptblock]$body) {
    try { & $body; Write-Host "ok   $name" }
    catch { $script:failures++; Write-Host "FAIL $name : $($_.Exception.Message)" }
}
function Assert($c, $m) { if (-not $c) { throw $m } }

Check 'the simulated agent writes a transcript with the question and runs the hook for each event' {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) "accept-sim-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory $scratch | Out-Null
    try {
        # A stand-in for the hook, which is run as: <hook> <agent> <hook-name>.
        if ($IsWindows) {
            $stand = Join-Path $scratch 'hook.cmd'
            Set-Content $stand "@echo off`r`nif `"%2`"==`"ask_user`" (echo {`"permissionDecision`":`"allow`"}) else echo {}"
        }
        else {
            $stand = Join-Path $scratch 'hook.sh'
            Set-Content $stand "#!/bin/sh`nif [ `"`$2`" = ask_user ]; then echo '{`"permissionDecision`":`"allow`"}'; else echo '{}'; fi"
            chmod +x $stand
        }
        $r = & (Join-Path $PSScriptRoot 'agent-sim.ps1') -Hook $stand -CopilotHome (Join-Path $scratch 'home') -Question 'Q-marker?'
        Assert ($r.Hooks.Count -eq 2 -and ($r.Hooks.Exit | Select-Object -Unique) -eq 0) 'hooks did not run and exit 0'
        Assert (($r.Hooks | Where-Object Hook -eq 'ask_user').Output -match 'allow') 'ask_user reply'
        $lines = Get-Content $r.Transcript | ForEach-Object { $_ | ConvertFrom-Json }
        Assert ($lines[0].type -eq 'session.start' -and $lines[1].type -eq 'user.message') 'event order'
        Assert ((Get-Content $r.Transcript -Raw) -match 'Q-marker\?') 'question missing'
    }
    finally { Remove-Item $scratch -Recurse -Force }
}

Check 'a batch hook in a path with a space runs, and one with a cmd metacharacter is refused' {
    if (-not $IsWindows) { return }
    $scratch = Join-Path ([IO.Path]::GetTempPath()) "accept sim $([guid]::NewGuid().ToString('N'))"
    $bad = Join-Path ([IO.Path]::GetTempPath()) "accept&sim-$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory $scratch, $bad | Out-Null
    try {
        foreach ($d in $scratch, $bad) { Set-Content (Join-Path $d 'hook.cmd') "@echo off`r`necho {}" }
        $r = & (Join-Path $PSScriptRoot 'agent-sim.ps1') -Hook (Join-Path $scratch 'hook.cmd') -CopilotHome (Join-Path $scratch 'home') -Question 'Q?'
        Assert (($r.Hooks.Exit | Select-Object -Unique) -eq 0 -and $r.Hooks[0].Output -eq '{}') 'a spaced path did not run'
        $refused = $false
        try { & (Join-Path $PSScriptRoot 'agent-sim.ps1') -Hook (Join-Path $bad 'hook.cmd') -CopilotHome (Join-Path $bad 'home') -Question 'Q?' | Out-Null } catch { $refused = $true }
        Assert $refused 'a path with & was not refused'
    }
    finally { Remove-Item $scratch, $bad -Recurse -Force }
}

Check 'the uninstall step checks the host logon task under the name the product registers' {
    $harness = Get-Content ([IO.Path]::Combine($PSScriptRoot, 'beta-acceptance.ps1')) -Raw
    $product = Get-Content ([IO.Path]::Combine($PSScriptRoot, '..', '..', 'src', 'AgentBridge.App.Core', 'HostManager.cs')) -Raw
    Assert ($product -match '\$"(AgentBridgeHost)_\{profile\}') 'the product task name prefix changed'
    Assert ($harness -match "Get-ScheduledTask -TaskName 'AgentBridgeHost_\*'") 'uninstall does not look for the host task'
    Assert ($harness -notmatch 'Get-ScheduledTask[^\r\n]*SilentlyContinue') 'the task check must fail closed'
    Assert ($harness -match '(?s)\$left \+= Get-ScheduledTask.*if \(\$left\.Count\) \{ throw') 'the task is not part of the leftovers'
}

Check 'the workflow is dispatch-only, has no cron, secrets or environment, and only the report job writes' {
    $path = [IO.Path]::Combine($PSScriptRoot, '..', '..', '.github', 'workflows', 'beta-acceptance.yml')
    $y = (Get-Content $path | Where-Object { $_ -notmatch '^\s*#' }) -join "`n"
    Assert ($y -match '(?m)^on:\s*\n\s+workflow_dispatch:\s*\n\s*\npermissions') 'trigger is not workflow_dispatch alone'
    Assert ($y -notmatch 'pull_request|schedule|cron') 'a forbidden trigger'
    Assert ($y -notmatch 'secrets\.|environment:') 'the workflow must use no secrets or environment'
    Assert ((([regex]::Matches($y, 'issues: write')).Count -eq 1) -and $y -match '(?s)report:.*issues: write') 'issues: write only on the reporting job'
    Assert ($y -notmatch 'uses: [^\n]+@(?![0-9a-f]{40}\b)') 'an action is not pinned to a SHA'
}

exit ($failures ? 1 : 0)
