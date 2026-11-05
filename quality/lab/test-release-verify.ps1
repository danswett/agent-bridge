# Checks quality/lab/release-gate.ps1 and quality/lab/release-verify.ps1, the gate and the verdict
# of the held lab release verification (.github/workflows/lab-release-verify.yml), and that
# workflow's shape, offline: nothing here calls Azure or GitHub, and it writes only its own scratch
# folder, which it removes.
#
#   pwsh quality/lab/test-release-verify.ps1
#
# What it guards: the gate refuses today's verification, naming every unmet prerequisite and
# missing dependency, and nothing a run is given can clear one; it refuses anything but a
# first-attempt dispatch from main naming a release tag and staging or ppe. The verdict passes a
# release only when the nightly passed it and both roles' results are that release's, from one lab
# run of one commit and CI run, and passed; anything else that can't count is invalid, never pass.
# And the workflow can't reach the nightly but through gate on a first attempt, pins the nightly by
# commit, passes it no secret, and grants nothing beyond what the nightly's jobs ask.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$gate = Join-Path $PSScriptRoot 'release-gate.ps1'
$verify = Join-Path $PSScriptRoot 'release-verify.ps1'
$workflow = Join-Path $PSScriptRoot '..' '..' '.github' 'workflows' 'lab-release-verify.yml'
$scratch = Join-Path ([IO.Path]::GetTempPath()) "lab-release-verify-$([guid]::NewGuid().ToString('n'))"
$empty = (New-Item -ItemType Directory (Join-Path $scratch 'empty')).FullName
# A checkout where the nightly, the provisioner and the runner have landed: stand-ins, never run.
$dependencies = '.github/workflows/lab-nightly.yml', 'quality/lab/nightly-gate.ps1', 'quality/lab/nightly-verdict.ps1', 'quality/lab/provision.ps1', 'quality/lab/run.ps1'
$withAll = (New-Item -ItemType Directory (Join-Path $scratch 'with-all')).FullName
foreach ($path in $dependencies) {
    $file = Join-Path $withAll $path
    New-Item -ItemType Directory -Force (Split-Path $file) | Out-Null
    Set-Content -LiteralPath $file -Value '# stand-in'
}

$failures = 0
function Check([string]$name, [scriptblock]$body) {
    try { & $body; Write-Output "ok   $name" }
    catch { $script:failures++; Write-Output "FAIL $name`n     $($_.Exception.Message)" }
}
function Assert([bool]$condition, [string]$message) { if (-not $condition) { throw $message } }

# --- The gate
$dispatch = @{ Release = 'v0.1.600'; Relay = 'staging'; EventName = 'workflow_dispatch'; Ref = 'refs/heads/main'; Repository = 'danswett/agent-bridge'; RunAttempt = '1'; Root = $withAll }
function Get-Refusal([hashtable]$changes) {
    $arguments = $dispatch.Clone()
    foreach ($key in $changes.Keys) { $arguments[$key] = $changes[$key] }
    try { & $gate @arguments | Out-Null } catch { return $_.Exception.Message }
    return $null
}
$prerequisites = 'contract-approval', 'nightly-landed', 'nightly-cleared', 'release-consumer'

Check "Today the gate refuses a well-formed dispatch, naming every unmet prerequisite and every missing dependency" {
    $message = Get-Refusal @{ Root = $empty }
    Assert ($null -ne $message) 'the gate let the verification through'
    Assert ($message -like '*refused before the nightly is called*') "refused some other way: $message"
    foreach ($id in $prerequisites) { Assert ($message -like "*- $id (*") "doesn't list ${id}: $message" }
    foreach ($path in $dependencies) { Assert ($message -like "*- $path is missing*") "doesn't say $path is missing: $message" }
}

Check "Once its dependencies land, the prerequisites held in source still refuse, and the gate has no input that could clear them" {
    $message = Get-Refusal @{}
    Assert ($null -ne $message) 'a dispatch went through'
    foreach ($id in $prerequisites) { Assert ($message -like "*- $id (*") "doesn't list ${id}: $message" }
    Assert ($message -notlike '*is missing*') "still reports a missing dependency: $message"
    Assert ($message -notlike '*must be*' -and $message -notlike '*runs only*' -and $message -notlike '*re-runs*') "refused a well-formed dispatch's inputs: $message"
    # A new parameter is where an approval, force or skip switch would come in.
    $common = @([System.Management.Automation.Cmdlet]::CommonParameters) + @([System.Management.Automation.Cmdlet]::OptionalCommonParameters)
    $inputs = @((Get-Command $gate).Parameters.Keys | Where-Object { $_ -notin $common })
    $allowed = 'Release', 'Relay', 'EventName', 'Ref', 'Repository', 'RunAttempt', 'Root'
    Assert (@(Compare-Object $inputs $allowed).Count -eq 0) "the gate's inputs are now $($inputs -join ', '); none may stand in for a receipt"
}

Check "The gate refuses anything but a first-attempt dispatch from main naming a release tag and staging or ppe" {
    $cases = @(
        @{ Change = @{ Repository = 'someone/agent-bridge' }; Says = '*runs only in danswett/agent-bridge*' }
        @{ Change = @{ Ref = 'refs/heads/feature' }; Says = '*runs only from refs/heads/main*' }
        @{ Change = @{ Ref = 'refs/pull/1/merge' }; Says = '*runs only from refs/heads/main*' }
        @{ Change = @{ EventName = 'schedule' }; Says = '*runs only by a dispatch*' }
        @{ Change = @{ EventName = 'workflow_call' }; Says = '*runs only by a dispatch*' }
        @{ Change = @{ EventName = 'pull_request_target' }; Says = '*runs only by a dispatch*' }
        @{ Change = @{ RunAttempt = '2' }; Says = '*re-runs are refused*' }
        @{ Change = @{ Release = '' }; Says = '*the release must be a release tag*' }
        @{ Change = @{ Release = 'latest' }; Says = '*the release must be a release tag*' }
        @{ Change = @{ Release = 'v0.1' }; Says = '*the release must be a release tag*' }
        @{ Change = @{ Release = 'V0.1.600' }; Says = '*the release must be a release tag*' }
        @{ Change = @{ Release = 'v0.1.600-rc' }; Says = '*the release must be a release tag*' }
        @{ Change = @{ Release = "v0.1.600`n" }; Says = '*the release must be a release tag*' }
        @{ Change = @{ Relay = '' }; Says = '*the relay must be staging or ppe*' }
        @{ Change = @{ Relay = 'production' }; Says = '*the relay must be staging or ppe*' }
        @{ Change = @{ Relay = 'Staging' }; Says = '*the relay must be staging or ppe*' }
    )
    foreach ($case in $cases) {
        $message = Get-Refusal $case.Change
        Assert ($message -like $case.Says) "$($case.Change | ConvertTo-Json -Compress) wasn't refused as '$($case.Says)': $message"
    }
}

# --- The verdict
$release = 'v0.1.600'
$runId = 'n00000jaqd8b'
$sha = '0123456789abcdef0123456789abcdef01234567'
$ciRun = '41973345451'
$results = 0
# Writes both roles' result.json as the nightly's passing v1 run would, after $change(role, result)
# edits either; a $change that returns $false leaves that role's result out.
function Write-Results([scriptblock]$change = { }) {
    $dir = Join-Path $scratch "results-$((++$script:results))"
    foreach ($role in 'win-x64', 'win-arm64') {
        $architecture = if ($role -eq 'win-x64') { 'x64' } else { 'arm64' }
        $result = [ordered]@{
            schemaVersion   = 1
            runId           = $runId
            cutId           = $release
            source          = [ordered]@{ run = [ordered]@{ id = [long]$ciRun; attempt = 1; workflow = 'ci.yml'; ref = 'refs/heads/main' } }
            build           = [ordered]@{ version = '0.1.600'; sha = $sha }
            role            = $role
            architecture    = $architecture
            agentMode       = 'simulated'
            coverageVersion = 'v1'
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
function Get-Verdict([string]$dir, [string]$nightly) { & $verify -Release $release -NightlyOutcome $nightly -ResultsDir $dir }

Check 'A release passes when the nightly passed it and both roles passed it, and an audio report beside them changes nothing' {
    $dir = Write-Results
    $v = Get-Verdict $dir 'pass'
    Assert ($v.outcome -ceq 'pass' -and @($v.reasons).Count -eq 0) "$($v.outcome): $($v.reasons -join '; ')"
    Assert ($v.release -ceq $release -and $v.coverageVersion -ceq 'v1') "a pass about $($v.release), $($v.coverageVersion)"
    foreach ($role in 'win-x64', 'win-arm64') {
        Set-Content -LiteralPath (Join-Path $dir "lab-result-$role" 'audio-acceptance.json') -Value '{"schemaVersion":1,"verdict":"fail"}'
    }
    $v = Get-Verdict $dir 'pass'
    Assert ($v.outcome -ceq 'pass') "an audio report changed the release to $($v.outcome): $($v.reasons -join '; ')"
}

Check "No outcome, an invalid one, a result from another release, run, commit, CI run or coverage, or a pass the results don't bear out is invalid" {
    $cases = [ordered]@{
        'the nightly gave no outcome*'                              = @{ Nightly = '' }
        "the nightly's outcome is 'PASS'*"                          = @{ Nightly = 'PASS' }
        "the nightly's outcome is 'skipped'*"                       = @{ Nightly = 'skipped' }
        'the nightly judged v0.1.600 invalid*'                      = @{ Nightly = 'invalid' }
        'win-arm64 has no result.json*'                             = @{ Change = { param($role, $r) if ($role -eq 'win-arm64') { return $false } } }
        "win-x64's cutId is 'v0.1.599'*"                            = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.cutId = 'v0.1.599' } } }
        "win-arm64's role is 'win-x64'*"                            = @{ Change = { param($role, $r) if ($role -eq 'win-arm64') { $r.role = 'win-x64' } } }
        "win-arm64's architecture is 'x64'*"                        = @{ Change = { param($role, $r) if ($role -eq 'win-arm64') { $r.architecture = 'x64' } } }
        "win-x64's schemaVersion is '2'*"                           = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.schemaVersion = 2 } } }
        "win-x64's coverageVersion is 'v2'*"                        = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.coverageVersion = 'v2' } } }
        "win-x64's agentMode is 'real'*"                            = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.agentMode = 'real' } } }
        "the roles' runId differ*"                                  = @{ Change = { param($role, $r) if ($role -eq 'win-arm64') { $r.runId = 'n00000other1' } } }
        "the roles' build.sha differ*"                              = @{ Change = { param($role, $r) if ($role -eq 'win-arm64') { $r.build.sha = 'f' * 40 } } }
        "the roles' source.run.id differ*"                          = @{ Change = { param($role, $r) if ($role -eq 'win-arm64') { $r.source.run.id = 41973345452 } } }
        "win-x64's result has no source.run.id*"                    = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.Remove('source') } } }
        "the nightly passed v0.1.600, but win-x64's result is fail*" = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.outcome = 'fail' } } }
        "win-x64's outcome is 'PASS'*"                              = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.outcome = 'PASS' } } }
        "the nightly failed v0.1.600, but neither role's result says fail*" = @{ Nightly = 'fail' }
    }
    foreach ($says in $cases.Keys) {
        $case = $cases[$says]
        $nightly = if ($case.ContainsKey('Nightly')) { $case.Nightly } else { 'pass' }
        $dir = if ($case.ContainsKey('Change')) { Write-Results $case.Change } else { Write-Results }
        $v = Get-Verdict $dir $nightly
        Assert ($v.outcome -ceq 'invalid') "'$says' gave $($v.outcome): $($v.reasons -join '; ')"
        Assert (@($v.reasons | Where-Object { $_ -like $says }).Count -eq 1) "no reason like '$says': $($v.reasons -join '; ')"
    }
    $dir = Write-Results { param($role, $r) if ($role -eq 'win-x64') { return $false } }
    New-Item -ItemType Directory (Join-Path $dir 'lab-result-win-x64') | Out-Null
    Set-Content -LiteralPath (Join-Path $dir 'lab-result-win-x64' 'result.json') -Value '{"schemaVersion":1,'
    $v = Get-Verdict $dir 'pass'
    Assert ($v.outcome -ceq 'invalid' -and ($v.reasons -join ';') -like "*win-x64's result.json isn't JSON*") "malformed JSON gave $($v.outcome): $($v.reasons -join '; ')"
}

Check 'A release the nightly failed, with a role that failed, fails; and invalid outranks fail' {
    $failX64 = { param($role, $r) if ($role -eq 'win-x64') { $r.outcome = 'fail' } }
    $v = Get-Verdict (Write-Results $failX64) 'fail'
    Assert ($v.outcome -ceq 'fail' -and ($v.reasons -join ';') -like '*the nightly failed v0.1.600 (win-x64)*') "a failed role gave $($v.outcome): $($v.reasons -join '; ')"
    $failAndOtherCut = { param($role, $r) if ($role -eq 'win-x64') { $r.outcome = 'fail' } else { $r.cutId = 'v0.1.599' } }
    $v = Get-Verdict (Write-Results $failAndOtherCut) 'fail'
    Assert ($v.outcome -ceq 'invalid') "a failed role beside another cut's result gave $($v.outcome): $($v.reasons -join '; ')"
}

Check 'Without a release tag to hold the results to there is no verdict at all' {
    $dir = Write-Results
    foreach ($bad in '', 'latest', 'v0.1.600 ', 'refs/tags/v0.1.600') {
        $given = $null
        try { $given = & $verify -Release $bad -NightlyOutcome 'pass' -ResultsDir $dir } catch { continue }
        throw "release '$bad' gave a verdict: $($given.outcome)"
    }
}

# --- The workflow
Check 'The workflow reaches the nightly only through gate, on a first attempt, pinned by commit, with no secret and no more than it asks' {
    $lines = Get-Content -LiteralPath $workflow
    $text = $lines -join "`n"
    Assert (@($lines | Where-Object { $_ -cmatch '^permissions:\s*\{\}\s*$' }).Count -eq 1) 'its top-level permissions aren''t {}'
    Assert ($text -cnotmatch '(?m)^\s*(schedule|workflow_call|workflow_run|push|pull_request|pull_request_target):') 'it has a trigger besides a dispatch'
    Assert ($text -cnotmatch '(?m)^\s*(environment|secrets):' -and $text -cnotmatch 'secrets\.|azure/login') 'it reads an environment or a secret, or signs in itself'

    $start = @(for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -ceq 'jobs:') { $i } })
    Assert ($start.Count -eq 1) 'lab-release-verify.yml has no single jobs: line'
    $start = $start[0]
    $jobs = [ordered]@{}
    $current = $null
    foreach ($line in $lines[($start + 1)..($lines.Count - 1)]) {
        if ($line -cmatch '^  ([A-Za-z0-9_-]+):\s*$') { $current = $Matches[1]; $jobs[$current] = [Collections.Generic.List[string]]::new() }
        elseif ($null -ne $current) { $jobs[$current].Add($line) }
    }
    Assert (@($jobs.Keys) -contains 'gate' -and @($jobs.Keys) -contains 'nightly' -and $jobs.Count -ge 3) "jobs found: $($jobs.Keys -join ', ')"

    # A job's own permissions, as "name: level" lines.
    function Get-Permissions($body) {
        $grants = [Collections.Generic.List[string]]::new()
        $inside = $false
        foreach ($line in $body) {
            if ($line -cmatch '^    permissions:\s*$') { $inside = $true; continue }
            if ($inside -and $line -cmatch '^      ([a-z-]+):\s*([a-z]+)\s*(#.*)?$') { $grants.Add("$($Matches[1]): $($Matches[2])"); continue }
            if ($inside -and $line -cnotmatch '^\s*(#.*)?$') { $inside = $false }
        }
        @($grants | Sort-Object)
    }
    foreach ($job in $jobs.Keys) {
        $grants = Get-Permissions $jobs[$job]
        if ($job -ceq 'nightly') {
            # Exactly what the nightly's own jobs ask; GitHub refuses a call granted less.
            $expected = @('actions: read', 'attestations: read', 'contents: read', 'id-token: write')
            Assert (($grants -join ',') -ceq ($expected -join ',')) "nightly is granted $($grants -join ', '), not $($expected -join ', ')"
        }
        else {
            Assert (($grants -join ',') -ceq 'contents: read') "$job is granted $($grants -join ', '), not contents: read alone"
        }
    }

    $gateJob = $jobs['gate'] -join "`n"
    Assert ($gateJob -cnotmatch '(?m)^    (uses|needs|if):') 'gate calls a workflow, needs a job or is conditional'

    # Re-running a run's failed jobs reuses gate's outputs from attempt 1 without running gate
    # again, so every job after it must refuse a later attempt itself.
    foreach ($job in @($jobs.Keys | Where-Object { $_ -cne 'gate' })) {
        $condition = @($jobs[$job] | Where-Object { $_ -cmatch '^    if:\s' })
        Assert ($condition.Count -eq 1) "$job has $($condition.Count) job-level if: lines, not one"
        $if = $condition[0] -creplace '^    if:\s*', ''
        Assert ($if -clike "*github.run_attempt == '1'*" -and $if -clike "*needs.gate.outputs.cleared == 'true'*") "$job may run without gate's first attempt: if: $if"
        Assert ($if -cnotlike '*||*') "$job's if: has an || that could route around gate: $if"
        Assert (@($jobs[$job] | Where-Object { $_ -cmatch '^    needs:.*\bgate\b' }).Count -eq 1) "$job doesn't need gate"
    }

    # The nightly at a commit (never a branch or tag that could move), or main's own once it lands.
    $nightly = $jobs['nightly']
    $uses = @($nightly | Where-Object { $_ -cmatch '^    uses:\s' })
    Assert ($uses.Count -eq 1) "nightly has $($uses.Count) uses: lines"
    Assert ($uses[0] -cmatch '^    uses: (danswett/agent-bridge/\.github/workflows/lab-nightly\.yml@[0-9a-f]{40}|\./\.github/workflows/lab-nightly\.yml)\s*(#.*)?$') "nightly isn't the lab nightly pinned by commit: $($uses[0])"
    Assert (@($nightly | Where-Object { $_ -cmatch '^      cut: \$\{\{ inputs\.release \}\}\s*$' }).Count -eq 1) 'nightly isn''t given the release as its cut'
    Assert (@($nightly | Where-Object { $_ -cmatch '^      relay: \$\{\{ inputs\.relay \}\}\s*$' }).Count -eq 1) 'nightly isn''t given the relay asked for'
}

Remove-Item -LiteralPath $scratch -Recurse -Force
if ($failures) { Write-Output "$failures failed"; exit 1 }
Write-Output 'all passed'
