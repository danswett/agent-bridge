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
            Set-Content $stand "#!/bin/sh`ncat >/dev/null`nif [ `"`$2`" = ask_user ]; then echo '{`"permissionDecision`":`"allow`"}'; else echo '{}'; fi"
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

function Invoke-PairFixture([string]$Failure = '', [switch]$MissingInstall) {
    $scratch = Join-Path ([IO.Path]::GetTempPath()) "accept-pair-$([guid]::NewGuid().ToString('N'))"
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'beta-acceptance.ps1'), [ref]$tokens, [ref]$errors)
    foreach ($name in 'Step', 'Invoke-PairAndDrive') {
        $definition = $ast.Find({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name }, $true)
        if (-not $definition) { throw "the harness has no $name implementation" }
        . ([scriptblock]::Create($definition.Extent.Text))
    }
    $steps = [ordered]@{}
    foreach ($name in '2-install-first-run', '3-simulated-agent', '4-pair', '5-drive-from-pwa') {
        $steps[$name] = @{ result = 'PASS'; detail = '' }
    }
    if ($MissingInstall) { $steps['2-install-first-run'].result = 'FAIL' }
    $Arch = 'fake'
    $context = @{ Calls = [Collections.Generic.List[string]]::new(); Failure = $Failure; Identity = $scratch }
    try {
        Invoke-PairAndDrive $context {
            param($operation, $c)
            $c.Calls.Add($operation)
            if ($operation -eq 'cleanup') {
                if (Test-Path -LiteralPath $c.Identity) { Remove-Item -LiteralPath $c.Identity -Recurse -Force }
            }
            elseif ($operation -eq 'start-relay') { New-Item -ItemType Directory -Path $c.Identity | Out-Null }
            if ($operation -eq $c.Failure) { throw "fake $operation failure" }
        }
        [pscustomobject]@{ Steps = $steps; Calls = $context.Calls.ToArray(); Leftover = Test-Path -LiteralPath $scratch }
    }
    finally { if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force } }
}

Check 'pairing and driving the PWA both pass only after their driver operations and always clean up' {
    $r = Invoke-PairFixture
    Assert ($r.Steps['4-pair'].result -eq 'PASS' -and $r.Steps['5-drive-from-pwa'].result -eq 'PASS') 'steps 4/5 did not pass'
    Assert (($r.Calls -join ',') -eq 'start-relay,sign-in,start-host,presence,drive,cleanup') 'driver order or cleanup'
    Assert (-not $r.Leftover) 'isolated identity survived success'
}

Check 'relay, sign-in, host, presence and PWA failures report FAIL rather than NOT RUN and clean up' {
    foreach ($failure in 'start-relay', 'sign-in', 'start-host', 'presence', 'drive') {
        $r = Invoke-PairFixture $failure
        $failed = if ($failure -eq 'drive') { '5-drive-from-pwa' } else { '4-pair' }
        Assert ($r.Steps[$failed].result -eq 'FAIL') "$failure was not failed"
        Assert ($r.Steps[$failed].detail -eq "fake $failure failure") "$failure reason was lost"
        Assert ($r.Steps['5-drive-from-pwa'].result -ne 'NOT RUN') 'step 5 was left NOT RUN'
        Assert ($r.Calls[-1] -eq 'cleanup' -and -not $r.Leftover) "$failure did not clean up"
        if ($failure -ne 'drive') { Assert ($r.Calls -notcontains 'drive') 'PWA ran without pairing' }
    }
}

Check 'unmet install prerequisites and cleanup failures cannot look like successful acceptance' {
    $r = Invoke-PairFixture -MissingInstall
    Assert ($r.Steps['4-pair'].result -eq 'FAIL' -and $r.Steps['5-drive-from-pwa'].result -eq 'FAIL') 'unmet prerequisites were not failed'
    Assert (($r.Calls -join ',') -eq 'cleanup') 'a product operation ran without an install'
    $r = Invoke-PairFixture 'cleanup'
    Assert ($r.Steps['4-pair'].result -eq 'FAIL' -and $r.Steps['4-pair'].detail -match 'cleanup') 'cleanup failure was hidden'
}

exit ($failures ? 1 : 0)
