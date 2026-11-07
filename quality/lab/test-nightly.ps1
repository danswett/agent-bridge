# Checks quality/lab/nightly-gate.ps1 and quality/lab/nightly-verdict.ps1, the gate and the cut
# verdict of the held nightly clean-VM run (.github/workflows/lab-nightly.yml), offline: nothing
# here calls Azure or GitHub, and it writes only its own scratch folder, which it removes.
#
#   pwsh quality/lab/test-nightly.ps1
#
# What it guards: the gate refuses today's nightly, naming every unmet prerequisite, and nothing
# a run is given can clear one; it refuses runs from anywhere but main's schedule or a dispatch,
# re-runs, any relay but staging or ppe, and a cut that isn't a release tag; and every job after
# it refuses a re-run attempt itself, since a re-run of failed jobs reuses gate's old outputs. The
# verdict passes a cut only when both roles passed it against one unchanged relay, with a real
# update, installing the installers and CI run the cut verified; anything that can't count is
# invalid, never pass; and an audio report beside the results changes nothing.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$gate = Join-Path $PSScriptRoot 'nightly-gate.ps1'
$verdict = Join-Path $PSScriptRoot 'nightly-verdict.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) "lab-nightly-$([guid]::NewGuid().ToString('n'))"
$empty = (New-Item -ItemType Directory (Join-Path $scratch 'empty')).FullName
# A checkout where #849's provisioner and #736's runner have landed: stand-ins, never run.
$withScripts = (New-Item -ItemType Directory (Join-Path $scratch 'with-scripts')).FullName
New-Item -ItemType Directory (Join-Path $withScripts 'quality' 'lab') | Out-Null
foreach ($name in 'provision.ps1', 'run.ps1') { Set-Content -LiteralPath (Join-Path $withScripts 'quality' 'lab' $name) -Value '# stand-in' }

$failures = 0
function Check([string]$name, [scriptblock]$body) {
    try { & $body; Write-Output "ok   $name" }
    catch { $script:failures++; Write-Output "FAIL $name`n     $($_.Exception.Message)" }
}
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }

# --- The gate
$nightly = @{ Relay = 'staging'; EventName = 'schedule'; Ref = 'refs/heads/main'; Repository = 'danswett/agent-bridge'; GitHubRunId = '41973345451'; RunAttempt = '1'; Root = $withScripts }
function Get-Refusal([hashtable]$changes) {
    $arguments = $nightly.Clone()
    foreach ($key in $changes.Keys) { $arguments[$key] = $changes[$key] }
    try { & $gate @arguments | Out-Null } catch { return $_.Exception.Message }
    return $null
}
$prerequisites = 'contract-approval', 'owner-authorization', 'bootstrap', 's0-snapshot', 'arm64-feasibility', 'run-registration', 'expiry-sweeper', 'cost-alerts', 'owner-device-flow', 'v1-scenario'

Check "Today the gate refuses a well-formed nightly, naming every unmet prerequisite and the missing provisioner and runner" {
    $message = Get-Refusal @{ Root = $empty }
    Assert ($null -ne $message) 'the gate let the run through'
    Assert ($message -like '*refused before any sign-in, cut or Azure call*') "refused some other way: $message"
    foreach ($id in $prerequisites) { Assert ($message -like "*- $id (*") "doesn't list ${id}: $message" }
    foreach ($path in 'quality/lab/provision.ps1', 'quality/lab/run.ps1') { Assert ($message -like "*- $path is missing*") "doesn't say $path is missing: $message" }
    Assert ($message -notlike '*ppe-relay*') "a staging run was held for PPE: $message"
}

Check "Once the provisioner and runner land, the prerequisites held in source still refuse, and the gate has no input that could clear them" {
    foreach ($eventName in 'schedule', 'workflow_dispatch') {
        $message = Get-Refusal @{ EventName = $eventName }
        Assert ($null -ne $message) "a $eventName run went through"
        foreach ($id in $prerequisites) { Assert ($message -like "*- $id (*") "$eventName doesn't list ${id}: $message" }
        Assert ($message -notlike '*is missing*') "$eventName still reports a missing script: $message"
    }
    # A new parameter is where an approval, force or skip switch would come in.
    $common = @([System.Management.Automation.Cmdlet]::CommonParameters) + @([System.Management.Automation.Cmdlet]::OptionalCommonParameters)
    $inputs = @((Get-Command $gate).Parameters.Keys | Where-Object { $_ -notin $common })
    $allowed = 'Relay', 'Cut', 'EventName', 'Ref', 'Repository', 'GitHubRunId', 'RunAttempt', 'Root'
    Assert (@(Compare-Object $inputs $allowed).Count -eq 0) "the gate's inputs are now $($inputs -join ', '); none may stand in for a receipt"
}

Check 'A run from a fork, a branch, a pull request or a push, a re-run, or without a GitHub run id is refused' {
    $cases = @(
        @{ Change = @{ Repository = 'someone/agent-bridge' }; Says = '*runs only in danswett/agent-bridge, not someone/agent-bridge*' }
        @{ Change = @{ Ref = 'refs/heads/platform/lab' }; Says = '*runs only from refs/heads/main, not refs/heads/platform/lab*' }
        @{ Change = @{ Ref = 'refs/pull/849/merge' }; Says = '*runs only from refs/heads/main*' }
        @{ Change = @{ EventName = 'pull_request' }; Says = '*by its schedule or a dispatch, not pull_request*' }
        @{ Change = @{ EventName = 'push' }; Says = '*by its schedule or a dispatch, not push*' }
        @{ Change = @{ RunAttempt = '2' }; Says = '*re-runs are refused (attempt 2)*' }
        @{ Change = @{ GitHubRunId = '12a' }; Says = '*the GitHub run id must be a number*' }
        @{ Change = @{ GitHubRunId = '012' }; Says = '*the GitHub run id must be a number*' }
    )
    foreach ($case in $cases) {
        $message = Get-Refusal $case.Change
        Assert ($null -ne $message -and $message -like $case.Says) "$($case.Change | ConvertTo-Json -Compress) wasn't refused as $($case.Says): $message"
    }
}

Check 'Only staging or ppe is a relay, ppe waits for PPE, and a cut is empty or a release tag' {
    foreach ($relay in 'production', 'Staging', 'https://relay.example', '') {
        $message = Get-Refusal @{ Relay = $relay }
        Assert ($message -like '*the relay must be staging or ppe*') "relay '$relay' wasn't refused as a relay: $message"
    }
    $message = Get-Refusal @{ Relay = 'ppe' }
    Assert ($message -like '*- ppe-relay (*' -and $message -notlike '*the relay must be*') "ppe wasn't held for PPE alone: $message"
    foreach ($cut in 'main', 'v0.1', 'v0.1.x', 'V0.1.600', 'refs/pull/849/head', '41973345451', 'v0.1.600;x') {
        Assert ((Get-Refusal @{ Cut = $cut }) -like '*the cut must be*') "cut '$cut' wasn't refused as a cut"
    }
    foreach ($cut in '', 'v0.1.600') {
        Assert ((Get-Refusal @{ Cut = $cut }) -notlike '*the cut must be*') "cut '$cut' was refused as a cut"
    }
}

# --- The verdict
$runId = 'n00000jaqd8b'
$cutId = 'v0.1.600'
$sha = '0123456789abcdef0123456789abcdef01234567'
$ciRun = '41973345451'
$cutDigests = @{ 'win-x64' = 'sha256:' + ('a' * 64); 'win-arm64' = 'sha256:' + ('b' * 64) }
# Another file the run installed. Built apart, since in an array literal a , binds before a +.
$otherDigest = 'sha256:' + ('c' * 64)
$results = 0
# Writes both roles' result.json as a passing v1 run would, after $change(role, result) edits
# either; a $change that returns $false leaves that role's result out.
function Write-Results([scriptblock]$change = { }) {
    $dir = Join-Path $scratch "results-$((++$script:results))"
    foreach ($role in 'win-x64', 'win-arm64') {
        $result = [ordered]@{
            schemaVersion   = 1
            runId           = $runId
            cutId           = $cutId
            source          = [ordered]@{ run = [ordered]@{ id = [long]$ciRun; attempt = 1; workflow = 'ci.yml'; ref = 'refs/heads/main' } }
            build           = [ordered]@{ version = '0.1.600'; channel = $(if ($role -eq 'win-x64') { 'win' } else { 'win-arm64' }); sha = $sha; digests = @($otherDigest, $cutDigests[$role]) }
            role            = $role
            architecture    = $(if ($role -eq 'win-x64') { 'x64' } else { 'arm64' })
            agentMode       = 'simulated'
            coverageVersion = 'v1'
            updatePair      = [ordered]@{ from = [ordered]@{ version = '0.1.599'; digest = 'sha256:aa' }; to = [ordered]@{ version = '0.1.600'; digest = 'sha256:bb' } }
            relay           = [ordered]@{ target = 'staging'; digestAtStart = 'sha256:relay1'; digestAtEnd = 'sha256:relay1' }
            steps           = @(foreach ($step in 'launch', 'reply', 'answer', 'stop', 'update') { [ordered]@{ name = $step; outcome = 'pass' } })
            outcome         = 'pass'
        }
        if ((& $change $role $result) -eq $false) { continue }
        $folder = New-Item -ItemType Directory -Force (Join-Path $dir "lab-result-$role")
        $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $folder.FullName 'result.json')
    }
    New-Item -ItemType Directory -Force $dir | Out-Null
    $dir
}
function Get-Verdict([string]$dir) {
    & $verdict -ResultsDir $dir -RunId $runId -CutId $cutId -Sha $sha -Relay 'staging' -CiRun $ciRun -X64Digest $cutDigests['win-x64'] -Arm64Digest $cutDigests['win-arm64']
}
function Step($result, [string]$name) { @($result.steps | Where-Object { $_.name -eq $name })[0] }

Check 'A cut passes when both roles pass it, and an audio report beside them changes nothing' {
    $dir = Write-Results
    $v = Get-Verdict $dir
    Assert ($v.outcome -ceq 'pass' -and @($v.reasons).Count -eq 0) "$($v.outcome): $($v.reasons -join '; ')"
    foreach ($role in 'win-x64', 'win-arm64') {
        Set-Content -LiteralPath (Join-Path $dir "lab-result-$role" 'audio-acceptance.json') -Value '{"schemaVersion":1,"verdict":"fail"}'
    }
    $v = Get-Verdict $dir
    Assert ($v.outcome -ceq 'pass') "an audio report changed the cut to $($v.outcome): $($v.reasons -join '; ')"
}

Check 'A lost role, an invalid run, relay churn, or a result from another run, cut, build, CI run, installer or coverage makes the cut invalid' {
    $cases = [ordered]@{
        'win-arm64 has no result.json*'           = { param($role, $r) if ($role -eq 'win-arm64') { return $false } }
        "win-x64's run is invalid*"                = { param($role, $r) if ($role -eq 'win-x64') { $r.outcome = 'invalid' } }
        "win-x64's relay digest changed*"          = { param($role, $r) if ($role -eq 'win-x64') { $r.relay.digestAtEnd = 'sha256:relay2' } }
        'the roles ran against different relay*'   = { param($role, $r) if ($role -eq 'win-arm64') { $r.relay.digestAtStart = 'sha256:relay2'; $r.relay.digestAtEnd = 'sha256:relay2' } }
        "win-x64's cutId is 'v0.1.599'*"           = { param($role, $r) if ($role -eq 'win-x64') { $r.cutId = 'v0.1.599' } }
        "win-arm64's runId*"                       = { param($role, $r) if ($role -eq 'win-arm64') { $r.runId = 'n00000other1' } }
        "win-arm64's build.sha*"                   = { param($role, $r) if ($role -eq 'win-arm64') { $r.build.sha = 'f' * 40 } }
        "win-arm64's architecture is 'x64'*"       = { param($role, $r) if ($role -eq 'win-arm64') { $r.architecture = 'x64' } }
        "win-x64's coverageVersion is 'v2'*"       = { param($role, $r) if ($role -eq 'win-x64') { $r.coverageVersion = 'v2' } }
        "win-x64's agentMode is 'real'*"           = { param($role, $r) if ($role -eq 'win-x64') { $r.agentMode = 'real' } }
        "win-x64's relay.target is 'production'*"  = { param($role, $r) if ($role -eq 'win-x64') { $r.relay.target = 'production' } }
        # What cut verified: the role's own installer digest, and the CI run that built it.
        "win-arm64's build.digests don't include*" = { param($role, $r) if ($role -eq 'win-arm64') { $r.build.digests = @($otherDigest, $cutDigests['win-x64']) } }
        "win-x64's build.digests don't include*"   = { param($role, $r) if ($role -eq 'win-x64') { $r.build.Remove('digests') } }
        "win-x64's source.run.id is '41973345452'*" = { param($role, $r) if ($role -eq 'win-x64') { $r.source.run.id = 41973345452 } }
        "win-arm64's source.run.id is ''*"         = { param($role, $r) if ($role -eq 'win-arm64') { $r.Remove('source') } }
        # Invalid outranks fail: a cut with an invalid role can't count either way.
        "win-arm64's run is invalid*"              = { param($role, $r) if ($role -eq 'win-arm64') { $r.outcome = 'invalid' } else { $r.outcome = 'fail'; (Step $r 'reply').outcome = 'fail' } }
    }
    foreach ($says in $cases.Keys) {
        $v = Get-Verdict (Write-Results $cases[$says])
        Assert ($v.outcome -ceq 'invalid') "'$says' gave $($v.outcome): $($v.reasons -join '; ')"
        Assert (@($v.reasons | Where-Object { $_ -like $says }).Count -eq 1) "no reason like '$says': $($v.reasons -join '; ')"
    }
    $dir = Write-Results { param($role, $r) if ($role -eq 'win-x64') { return $false } }
    New-Item -ItemType Directory (Join-Path $dir 'lab-result-win-x64') | Out-Null
    Set-Content -LiteralPath (Join-Path $dir 'lab-result-win-x64' 'result.json') -Value '{"schemaVersion":1,'
    $v = Get-Verdict $dir
    Assert ($v.outcome -ceq 'invalid' -and ($v.reasons -join ';') -like "*win-x64's result.json isn't JSON*") "malformed JSON gave $($v.outcome): $($v.reasons -join '; ')"
}

Check 'A failed role fails the cut, and a pass with a step not passed or no real update is never a pass' {
    $v = Get-Verdict (Write-Results { param($role, $r) if ($role -eq 'win-x64') { $r.outcome = 'fail'; (Step $r 'reply').outcome = 'fail' } })
    Assert ($v.outcome -ceq 'fail' -and ($v.reasons -join ';') -like '*win-x64 failed (reply)*') "a failed role gave $($v.outcome): $($v.reasons -join '; ')"
    $claims = [ordered]@{
        'a skipped update'          = { param($role, $r) if ($role -eq 'win-arm64') { (Step $r 'update').outcome = 'skipped' } }
        'no update step'            = { param($role, $r) if ($role -eq 'win-arm64') { $r.steps = @($r.steps | Where-Object { $_.name -ne 'update' }) } }
        'an update to itself'       = { param($role, $r) if ($role -eq 'win-arm64') { $r.updatePair.to.version = '0.1.599' } }
        'no update pair'            = { param($role, $r) if ($role -eq 'win-arm64') { $r.Remove('updatePair') } }
        'no steps'                  = { param($role, $r) if ($role -eq 'win-arm64') { $r.steps = @() } }
    }
    foreach ($claim in $claims.Keys) {
        $v = Get-Verdict (Write-Results $claims[$claim])
        Assert ($v.outcome -ceq 'invalid' -and ($v.reasons -join ';') -like '*win-arm64 claims pass*') "a pass with $claim gave $($v.outcome): $($v.reasons -join '; ')"
    }
}

Check 'A claimed pass is invalid when either role omits any mandatory v1 step' {
    foreach ($targetRole in 'win-x64', 'win-arm64') {
        foreach ($missingStep in 'launch', 'reply', 'answer', 'stop', 'update') {
            $dir = Write-Results {
                param($role, $r)
                if ($role -ceq $targetRole) { $r.steps = @($r.steps | Where-Object { $_.name -cne $missingStep }) }
            }
            $v = Get-Verdict $dir
            Assert ($v.outcome -ceq 'invalid' -and ($v.reasons -join ';') -clike "*$targetRole claims pass*($missingStep)*") "missing $targetRole/$missingStep gave $($v.outcome): $($v.reasons -join '; ')"
        }
    }
}

Check 'A claimed pass is invalid when either role repeats any mandatory v1 step' {
    foreach ($targetRole in 'win-x64', 'win-arm64') {
        foreach ($duplicateStep in 'launch', 'reply', 'answer', 'stop', 'update') {
            $dir = Write-Results {
                param($role, $r)
                if ($role -ceq $targetRole) { $r.steps = @($r.steps) + @((Step $r $duplicateStep)) }
            }
            $v = Get-Verdict $dir
            Assert ($v.outcome -ceq 'invalid' -and ($v.reasons -join ';') -clike "*$targetRole claims pass*($duplicateStep)*") "duplicate $targetRole/$duplicateStep gave $($v.outcome): $($v.reasons -join '; ')"
        }
    }
}

Check "Without the cut's verified CI run and both roles' digests there is no verdict at all" {
    $dir = Write-Results
    $good = @{ ResultsDir = $dir; RunId = $runId; CutId = $cutId; Sha = $sha; Relay = 'staging'; CiRun = $ciRun; X64Digest = $cutDigests['win-x64']; Arm64Digest = $cutDigests['win-arm64'] }
    foreach ($bad in @(@{ CiRun = '' }, @{ CiRun = 'run' }, @{ X64Digest = '' }, @{ X64Digest = 'sha256:00' }, @{ Arm64Digest = 'SHA256:' + ('B' * 64) })) {
        $arguments = $good.Clone()
        foreach ($key in $bad.Keys) { $arguments[$key] = $bad[$key] }
        $given = $null
        try { $given = & $verdict @arguments } catch { continue }
        throw "$($bad | ConvertTo-Json -Compress) gave a verdict: $($given.outcome)"
    }
}

Check 'The nightly neither injects a reusable lab credential nor treats a runner plan as execution' {
    $workflow = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot '..' '..' '.github' 'workflows' 'lab-nightly.yml')
    Assert ($workflow -cnotmatch 'LAB_GITHUB_TOKEN|secrets\.LAB_') 'the nightly provisions a lab credential instead of waiting for owner device flow'
    Assert ($workflow -cmatch '(?m)^\s+-Scenario nightly-v1 .+ -Apply\s*$') 'the nightly requests a successful plan instead of the refusing execution entrypoint'
}

# Re-running a run's failed jobs reuses gate's outputs from attempt 1 without running gate again,
# so the jobs after it must refuse a later attempt themselves: nothing signs in, uploads or judges.
Check "Every job after gate refuses a re-run attempt in its own if:, before any step" {
    $lines = Get-Content -LiteralPath (Join-Path $PSScriptRoot '..' '..' '.github' 'workflows' 'lab-nightly.yml')
    $start = [array]::IndexOf($lines, 'jobs:')
    Assert ($start -ge 0) 'lab-nightly.yml has no jobs: line'
    $jobs = [ordered]@{}
    $current = $null
    foreach ($line in $lines[($start + 1)..($lines.Count - 1)]) {
        if ($line -cmatch '^  ([A-Za-z0-9_-]+):\s*$') { $current = $Matches[1]; $jobs[$current] = [Collections.Generic.List[string]]::new() }
        elseif ($null -ne $current) { $jobs[$current].Add($line) }
    }
    Assert (@($jobs.Keys) -contains 'gate' -and $jobs.Count -ge 4) "jobs found: $($jobs.Keys -join ', ')"
    foreach ($job in @($jobs.Keys | Where-Object { $_ -cne 'gate' })) {
        $condition = @($jobs[$job] | Where-Object { $_ -cmatch '^    if:\s' })
        Assert ($condition.Count -eq 1) "$job has $($condition.Count) job-level if: lines, not one"
        $text = $condition[0] -creplace '^    if:\s*', ''
        Assert ($text -clike "*github.run_attempt == '1'*") "$job runs on a re-run: if: $text"
        # A conjunct only: any || could route around it.
        Assert ($text -cnotlike '*||*') "$job's if: has an || that could route around the attempt check: $text"
        Assert (@($jobs[$job] | Where-Object { $_ -cmatch '^    needs:.*\bgate\b' }).Count -eq 1) "$job doesn't need gate"
    }
    $gateJob = $jobs['gate'] -join "`n"
    Assert ($gateJob -cnotmatch 'id-token|environment:|secrets\.') 'gate has a sign-in, environment or secret'
}

Remove-Item -LiteralPath $scratch -Recurse -Force
if ($failures) { Write-Output "$failures failed"; exit 1 }
Write-Output 'all passed'
