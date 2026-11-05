# Checks quality/lab/install-smoke-gate.ps1 and quality/lab/install-smoke-verdict.ps1, the gate
# and the verdict of the clean-VM install smoke, a scenario of the held lab nightly, offline:
# nothing here installs anything, starts a process, or calls Azure or GitHub, and it writes only
# its own scratch folder, which it removes. A pass here is these scripts' logic over stand-in
# results, never evidence that the product installs.
#
#   pwsh quality/lab/test-install-smoke.ps1
#
# What it guards: the gate refuses every install today, naming every unmet prerequisite and
# missing dependency, and nothing it is given can clear one; it takes no installer path and
# refuses any role, cut, run, relay, installer digest, CI run or ref the nightly's cut didn't
# verify. Neither script can start, install or reach anything. The verdict passes a role only
# when the cut's verified installer installed and the installed product then launched, on that
# role's own lab device, in a run the nightly could count; an installer's exit alone, a skipped
# or missing step, or anything unknown is never pass; and an audio report changes nothing.
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$gate = Join-Path $PSScriptRoot 'install-smoke-gate.ps1'
$verdict = Join-Path $PSScriptRoot 'install-smoke-verdict.ps1'
$scratch = Join-Path ([IO.Path]::GetTempPath()) "lab-install-smoke-$([guid]::NewGuid().ToString('n'))"
$empty = (New-Item -ItemType Directory (Join-Path $scratch 'empty')).FullName
# A checkout where the provisioner, the runner and the nightly have landed: stand-ins, never run.
$dependencies = 'quality/lab/provision.ps1', 'quality/lab/run.ps1', 'quality/lab/nightly-gate.ps1', 'quality/lab/nightly-verdict.ps1'
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

$runId = 'n00000jaqd8b'
$cut = 'v0.1.600'
$sha = '0123456789abcdef0123456789abcdef01234567'
$ciRun = '41973345451'
$x64 = 'sha256:' + ('a' * 64)
$arm64 = 'sha256:' + ('b' * 64)

# --- The gate
$install = @{ Role = 'win-arm64'; CutId = $cut; RunId = $runId; Relay = 'staging'; InstallerDigest = $arm64; CiRun = $ciRun; SourceRef = 'refs/heads/main'; Root = $withAll }
function Get-Refusal([hashtable]$changes) {
    $arguments = $install.Clone()
    foreach ($key in $changes.Keys) { $arguments[$key] = $changes[$key] }
    try { & $gate @arguments | Out-Null } catch { return $_.Exception.Message }
    return $null
}
$prerequisites = 'contract-approval', 'nightly-cleared', 'installer-attestation', 'install-smoke-scenario', 'owner-authorization'

Check 'Today the gate refuses a well-formed install, naming every unmet prerequisite and every missing dependency' {
    $message = Get-Refusal @{ Root = $empty }
    Assert ($null -ne $message) 'the gate let the install through'
    Assert ($message -like '*refused before any installer, VM, OS or Azure operation*') "refused some other way: $message"
    foreach ($id in $prerequisites) { Assert ($message -like "*- $id (*") "doesn't list ${id}: $message" }
    foreach ($path in $dependencies) { Assert ($message -like "*- $path is missing*") "doesn't say $path is missing: $message" }
}

Check 'Once its dependencies land, the prerequisites held in source still refuse, for either role and relay, and no input can clear them or name an installer file' {
    foreach ($change in @{}, @{ Role = 'win-x64'; InstallerDigest = $x64 }, @{ Relay = 'ppe' }, @{ SourceRef = 'refs/tags/v0.1.600' }) {
        $message = Get-Refusal $change
        Assert ($null -ne $message) "$($change | ConvertTo-Json -Compress) went through"
        foreach ($id in $prerequisites) { Assert ($message -like "*- $id (*") "doesn't list ${id}: $message" }
        Assert ($message -notlike '*is missing*') "still reports a missing dependency: $message"
        Assert ($message -notlike '*must be*' -and $message -notlike '*must come*') "refused a well-formed install's inputs: $message"
    }
    # A new parameter is where an approval, force or skip switch, or an installer path, would come in.
    $common = @([System.Management.Automation.Cmdlet]::CommonParameters) + @([System.Management.Automation.Cmdlet]::OptionalCommonParameters)
    $inputs = @((Get-Command $gate).Parameters.Keys | Where-Object { $_ -notin $common })
    $allowed = 'Role', 'CutId', 'RunId', 'Relay', 'InstallerDigest', 'CiRun', 'SourceRef', 'Root'
    Assert (@(Compare-Object $inputs $allowed).Count -eq 0) "the gate's inputs are now $($inputs -join ', '); none may stand in for a receipt or an installer"
}

Check 'The gate refuses any role, cut, run, relay, installer digest, CI run or ref but those the nightly verified' {
    $cases = @(
        @{ Change = @{ Role = '' }; Says = '*the role must be win-x64 or win-arm64*' }
        @{ Change = @{ Role = 'Win-x64' }; Says = '*the role must be win-x64 or win-arm64*' }
        @{ Change = @{ Role = 'win-x86' }; Says = '*the role must be win-x64 or win-arm64*' }
        @{ Change = @{ Role = 'linux-arm64' }; Says = '*the role must be win-x64 or win-arm64*' }
        @{ Change = @{ CutId = '' }; Says = '*the cut must be a release tag*' }
        @{ Change = @{ CutId = 'latest' }; Says = '*the cut must be a release tag*' }
        @{ Change = @{ CutId = 'v0.1.600-rc' }; Says = '*the cut must be a release tag*' }
        @{ Change = @{ CutId = "v0.1.600`n" }; Says = '*the cut must be a release tag*' }
        @{ Change = @{ RunId = '' }; Says = "*the run id must be the nightly's*" }
        @{ Change = @{ RunId = 'N00000JAQD8B' }; Says = "*the run id must be the nightly's*" }
        @{ Change = @{ RunId = 'n0000jaqd8b' }; Says = "*the run id must be the nightly's*" }
        @{ Change = @{ RunId = 'x00000jaqd8b' }; Says = "*the run id must be the nightly's*" }
        @{ Change = @{ Relay = '' }; Says = '*the relay must be staging or ppe*' }
        @{ Change = @{ Relay = 'production' }; Says = '*the relay must be staging or ppe*' }
        @{ Change = @{ Relay = 'Staging' }; Says = '*the relay must be staging or ppe*' }
        @{ Change = @{ Relay = 'https://relay.example' }; Says = '*the relay must be staging or ppe*' }
        @{ Change = @{ InstallerDigest = '' }; Says = '*the installer must be named by its verified digest*' }
        @{ Change = @{ InstallerDigest = 'C:\installers\AgentBridgeSetup.exe' }; Says = '*the installer must be named by its verified digest*' }
        @{ Change = @{ InstallerDigest = 'sha256:' + ('A' * 64) }; Says = '*the installer must be named by its verified digest*' }
        @{ Change = @{ InstallerDigest = 'sha1:' + ('a' * 40) }; Says = '*the installer must be named by its verified digest*' }
        @{ Change = @{ CiRun = '' }; Says = "*the CI run must be the verified run's id*" }
        @{ Change = @{ CiRun = '0' }; Says = "*the CI run must be the verified run's id*" }
        @{ Change = @{ SourceRef = '' }; Says = '*the installer must come from a protected ref*' }
        @{ Change = @{ SourceRef = 'refs/heads/feature' }; Says = '*the installer must come from a protected ref*' }
        @{ Change = @{ SourceRef = 'refs/pull/1/merge' }; Says = '*the installer must come from a protected ref*' }
        @{ Change = @{ SourceRef = 'refs/tags/v0.1.600-rc' }; Says = '*the installer must come from a protected ref*' }
    )
    foreach ($case in $cases) {
        $message = Get-Refusal $case.Change
        Assert ($message -like $case.Says) "$($case.Change | ConvertTo-Json -Compress) wasn't refused as '$($case.Says)': $message"
    }
}

Check 'Neither script can start a process, install, sign in or reach the network: they call only what reads files and JSON' {
    $allowed = @{
        $gate    = 'Join-Path', 'Test-Path', 'Where-Object', 'ForEach-Object'
        $verdict = 'Join-Path', 'Test-Path', 'Get-Content', 'ConvertFrom-Json', 'Where-Object', 'Get-Field'
    }
    foreach ($source in $allowed.Keys) {
        $errors = $null
        $ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$null, [ref]$errors)
        Assert (@($errors).Count -eq 0) "$source doesn't parse: $($errors -join '; ')"
        foreach ($command in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.CommandAst] }, $true)) {
            Assert ($command.InvocationOperator -eq [System.Management.Automation.Language.TokenKind]::Unknown) "$source runs something by & or .: $command"
            $name = $command.GetCommandName()
            Assert ($null -ne $name -and $name -cin $allowed[$source]) "$source calls $command"
        }
        foreach ($member in $ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.InvokeMemberExpressionAst] -and $n.Static }, $true)) {
            Assert ("$($member.Expression)" -ceq '[Collections.Generic.List[string]]' -and "$($member.Member)" -ceq 'new') "$source calls .NET directly: $member"
        }
    }
}

# --- The verdict
$results = 0
function Set-Step($result, [string]$name, [string]$outcome) {
    foreach ($step in $result.steps) { if ($step['name'] -ceq $name) { $step['outcome'] = $outcome } }
}
# Writes both roles' result.json as a passing v1 nightly run with the install smoke would, after
# $change(role, result) edits either; a $change that returns $false leaves that role's result out.
function Write-Results([scriptblock]$change = { }) {
    $dir = Join-Path $scratch "results-$((++$script:results))"
    foreach ($role in 'win-x64', 'win-arm64') {
        $architecture = if ($role -eq 'win-x64') { 'x64' } else { 'arm64' }
        $digest = if ($role -eq 'win-x64') { $x64 } else { $arm64 }
        $result = [ordered]@{
            schemaVersion   = 1
            runId           = $runId
            cutId           = $cut
            source          = [ordered]@{ run = [ordered]@{ id = [long]$ciRun; attempt = 1; workflow = 'ci.yml'; ref = 'refs/heads/main' } }
            build           = [ordered]@{ version = '0.1.600'; channel = 'pre-release'; sha = $sha; digests = @($digest) }
            role            = $role
            architecture    = $architecture
            device          = "lab-$role-$runId"
            agentMode       = 'simulated'
            coverageVersion = 'v1'
            updatePair      = [ordered]@{ from = [ordered]@{ version = '0.1.599' }; to = [ordered]@{ version = '0.1.600' } }
            relay           = [ordered]@{ target = 'staging'; digestAtStart = 'sha256:' + ('c' * 64); digestAtEnd = 'sha256:' + ('c' * 64) }
            steps           = @(foreach ($step in 'install', 'launch', 'reply', 'answer', 'stop', 'update') { [ordered]@{ name = $step; outcome = 'pass' } })
            outcome         = 'pass'
        }
        if ((& $change $role $result) -eq $false) { continue }
        $folder = New-Item -ItemType Directory -Force (Join-Path $dir "lab-result-$role")
        $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $folder.FullName 'result.json')
    }
    New-Item -ItemType Directory -Force $dir | Out-Null
    $dir
}
function Get-Verdict([string]$dir, [string]$nightly) {
    & $verdict -ResultsDir $dir -RunId $runId -CutId $cut -Sha $sha -Relay staging -CiRun $ciRun -X64Digest $x64 -Arm64Digest $arm64 -NightlyOutcome $nightly
}

Check 'Both roles installing and then launching the verified installer is a pass, scoped to the install smoke, and an audio report beside them changes nothing' {
    $dir = Write-Results
    $v = Get-Verdict $dir 'pass'
    Assert ($v.outcome -ceq 'pass' -and @($v.reasons).Count -eq 0) "$($v.outcome): $($v.reasons -join '; ')"
    Assert ($v.roles['win-x64'] -ceq 'pass' -and $v.roles['win-arm64'] -ceq 'pass') "roles: $($v.roles | ConvertTo-Json -Compress)"
    Assert ($v.scenario -ceq 'install-smoke' -and $v.coverageVersion -ceq 'v1' -and $v.scope -clike "*Not the nightly's cut verdict*") "a pass that doesn't say what it is: $($v.scenario), $($v.scope)"
    foreach ($role in 'win-x64', 'win-arm64') {
        Set-Content -LiteralPath (Join-Path $dir "lab-result-$role" 'audio-acceptance.json') -Value '{"schemaVersion":1,"verdict":"fail"}'
    }
    $v = Get-Verdict $dir 'pass'
    Assert ($v.outcome -ceq 'pass') "an audio report changed the install smoke to $($v.outcome): $($v.reasons -join '; ')"
}

Check 'The install smoke judges install and launch only: a later step failing the nightly leaves it a pass' {
    $v = Get-Verdict (Write-Results { param($role, $r) if ($role -eq 'win-x64') { Set-Step $r 'reply' 'fail'; $r.outcome = 'fail' } }) 'fail'
    Assert ($v.outcome -ceq 'pass') "a failed reply gave the install smoke $($v.outcome): $($v.reasons -join '; ')"
}

Check "An installer that failed, or installed a product that didn't launch, fails; an installer's exit alone never passes" {
    $v = Get-Verdict (Write-Results { param($role, $r) if ($role -eq 'win-arm64') { Set-Step $r 'install' 'fail'; Set-Step $r 'launch' 'fail'; $r.outcome = 'fail' } }) 'fail'
    Assert ($v.outcome -ceq 'fail' -and $v.roles['win-arm64'] -ceq 'fail' -and ($v.reasons -join ';') -like "*win-arm64's installer failed*") "a failed installer gave $($v.outcome): $($v.reasons -join '; ')"
    $v = Get-Verdict (Write-Results { param($role, $r) if ($role -eq 'win-x64') { Set-Step $r 'launch' 'fail'; $r.outcome = 'fail' } }) 'fail'
    Assert ($v.outcome -ceq 'fail' -and ($v.reasons -join ';') -like "*win-x64 installed, but the installed product didn't launch*") "a product that didn't launch gave $($v.outcome): $($v.reasons -join '; ')"
    # An installer that exited 0, recorded every way a runner might, with no launch after it.
    $exitOnly = { param($role, $r) $r.steps = @([ordered]@{ name = 'install'; outcome = 'pass'; detail = 'exit 0' }); $r['installerExitCode'] = 0 }
    $v = Get-Verdict (Write-Results $exitOnly) 'pass'
    Assert ($v.outcome -ceq 'invalid' -and ($v.reasons -join ';') -like '*win-x64 has 0 launch steps, not one*') "an installer's exit alone gave $($v.outcome): $($v.reasons -join '; ')"
}

Check "Anything missing, unknown, not exercised, or not this run's, its cut's verified installer or its own lab device is invalid, never pass" {
    $cases = [ordered]@{
        'the nightly gave no outcome for v0.1.600*'                  = @{ Nightly = '' }
        "the nightly's outcome is 'PASS'*"                           = @{ Nightly = 'PASS' }
        "the nightly's outcome is 'skipped'*"                        = @{ Nightly = 'skipped' }
        'the nightly judged v0.1.600 invalid*'                       = @{ Nightly = 'invalid' }
        'win-arm64 has no result.json*'                              = @{ Change = { param($role, $r) if ($role -eq 'win-arm64') { return $false } } }
        "win-x64's device is '', not 'lab-win-x64-n00000jaqd8b'*"    = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.Remove('device') } } }
        "win-x64's device is 'owner-desktop'*"                       = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.device = 'owner-desktop' } } }
        "win-arm64's device is 'lab-win-arm64-n00000other1'*"        = @{ Change = { param($role, $r) if ($role -eq 'win-arm64') { $r.device = 'lab-win-arm64-n00000other1' } } }
        "win-arm64's architecture is 'x64'*"                         = @{ Change = { param($role, $r) if ($role -eq 'win-arm64') { $r.architecture = 'x64' } } }
        "win-arm64's role is 'win-x64'*"                             = @{ Change = { param($role, $r) if ($role -eq 'win-arm64') { $r.role = 'win-x64' } } }
        "win-x64's runId is 'n00000other1'*"                           = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.runId = 'n00000other1' } } }
        "win-x64's cutId is 'v0.1.599'*"                             = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.cutId = 'v0.1.599' } } }
        "win-x64's build.sha is*"                                    = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.build.sha = 'f' * 40 } } }
        "win-x64's source.run.id is '41973345452'*"                  = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.source.run.id = 41973345452 } } }
        "win-x64's source.run.ref is 'refs/pull/1/merge'*"           = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.source.run.ref = 'refs/pull/1/merge' } } }
        "win-x64's source.run.ref is '', not refs/heads/main*"       = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.source.run.Remove('ref') } } }
        "win-arm64's build.digests don't include the installer*"     = @{ Change = { param($role, $r) if ($role -eq 'win-arm64') { $r.build.digests = @($x64) } } }
        "win-x64's build.digests don't include the installer*"       = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.build.Remove('digests') } } }
        "win-x64's relay.target is 'production'*"                    = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.relay.target = 'production' } } }
        "win-x64's agentMode is 'real'*"                             = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.agentMode = 'real' } } }
        "win-x64's coverageVersion is 'v2'*"                         = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.coverageVersion = 'v2' } } }
        "win-x64's schemaVersion is '2'*"                            = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.schemaVersion = 2 } } }
        "win-x64's run is invalid*"                                  = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.outcome = 'invalid' } } }
        "win-x64's outcome is 'PASS'*"                               = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.outcome = 'PASS' } } }
        "win-x64's outcome is '', not pass*"                         = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.Remove('outcome') } } }
        'win-x64 has 0 install steps, not one*'                      = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.steps = @($r.steps | Where-Object { $_['name'] -cne 'install' }) } } }
        'win-x64 has 2 install steps, not one*'                      = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { $r.steps = @([ordered]@{ name = 'install'; outcome = 'fail' }) + @($r.steps) } } }
        'win-arm64 launched before it installed*'                    = @{ Change = { param($role, $r) if ($role -eq 'win-arm64') { $r.steps = @($r.steps[1], $r.steps[0]) + @($r.steps[2..5]) } } }
        "win-x64's install step was skipped: not exercised*"         = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { Set-Step $r 'install' 'skipped' } } }
        "win-arm64's launch step was skipped: not exercised*"        = @{ Change = { param($role, $r) if ($role -eq 'win-arm64') { Set-Step $r 'launch' 'skipped' } } }
        "win-x64's install step's outcome is 'ok'*"                  = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { Set-Step $r 'install' 'ok' } } }
        "win-x64's launch step's outcome is ''*"                     = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { foreach ($s in $r.steps) { if ($s['name'] -ceq 'launch') { $s.Remove('outcome') } } } } }
        'win-x64 launched after its installer failed*'               = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { Set-Step $r 'install' 'fail'; $r.outcome = 'fail' } }; Nightly = 'fail' }
        'win-x64 claims pass, but its install smoke is fail*'        = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { Set-Step $r 'launch' 'fail' } } }
        "the nightly passed v0.1.600, but win-x64's install smoke is fail*" = @{ Change = { param($role, $r) if ($role -eq 'win-x64') { Set-Step $r 'launch' 'fail'; $r.outcome = 'fail' } } }
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

Check 'Invalid outranks fail' {
    $failAndProduction = { param($role, $r) if ($role -eq 'win-x64') { Set-Step $r 'launch' 'fail'; $r.outcome = 'fail' } else { $r.relay.target = 'production' } }
    $v = Get-Verdict (Write-Results $failAndProduction) 'fail'
    Assert ($v.outcome -ceq 'invalid') "a failed launch beside a production relay gave $($v.outcome): $($v.reasons -join '; ')"
}

Check 'Without the run, cut, commit, relay, CI run and verified installers to hold the results to there is no verdict at all' {
    $dir = Write-Results
    $good = @{ ResultsDir = $dir; RunId = $runId; CutId = $cut; Sha = $sha; Relay = 'staging'; CiRun = $ciRun; X64Digest = $x64; Arm64Digest = $arm64; NightlyOutcome = 'pass' }
    $bad = @(
        @{ RunId = 'latest' }, @{ CutId = '' }, @{ CutId = 'refs/tags/v0.1.600' }, @{ Sha = 'abc123' }, @{ Relay = 'production' },
        @{ Relay = 'Staging' }, @{ CiRun = '' }, @{ X64Digest = 'sha256:' }, @{ Arm64Digest = 'C:\installers\AgentBridgeSetup.exe' }
    )
    foreach ($change in $bad) {
        $arguments = $good.Clone()
        foreach ($key in $change.Keys) { $arguments[$key] = $change[$key] }
        $given = $null
        try { $given = & $verdict @arguments } catch { continue }
        throw "$($change | ConvertTo-Json -Compress) gave a verdict: $($given.outcome)"
    }
}

Remove-Item -LiteralPath $scratch -Recurse -Force
if ($failures) { Write-Output "$failures failed"; exit 1 }
Write-Output 'all passed'
