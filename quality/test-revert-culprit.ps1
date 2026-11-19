#Requires -Version 7
# Offline only: every gh and git request must match these stand-ins.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'revert-culprit.ps1') -Repository 'o/r'
$script:GhCommand = 'Fake-Gh'
$script:GitCommand = 'Fake-Git'
$script:failures = 0
$script:checks = 0
$script:GreenSha = 'a' * 40
$script:RedSha = 'b' * 40
$script:Token = 'ghs_fakeTokenForRevertCulprit0123'
$script:Basic = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("x-access-token:$script:Token"))
$env:GH_TOKEN = $script:Token
function Assert([bool]$Ok, [string]$Name) {
    $script:checks++
    if ($Ok) { "ok   $Name" } else { "FAIL $Name"; $script:failures++ }
}
function Reset-Scenario {
    $script:s = @{ redAt = '2026-11-09T07:40:00Z'; newest = 'failure'; noGreen = $false; commits = 1; prs = 1; conflict = $false; parent = $script:GreenSha; branchExists = $false; comments = @()
        entry = @('main-red: run=101 attempt=1 sha=bbbbbbbb', '```', 'Job: product', '  A.B.C.Method', '```') -join "`n"
        quarantined = @(); revertPrs = @(); culpritIssues = @(); calls = @(); git = @(); writes = @(); body = ''; result = ''; pushFails = $false; pushAuth = '' }
}
function Fake-Gh {
    $a = $args -join ' '
    $s.calls += $a
    $body = if ($args -contains '--body') { $args[([array]::IndexOf($args, '--body') + 1)] }
    switch -Regex ($a) {
        '^api repos/o/r/actions/workflows/ci.yml/runs\?.*status=completed' {
            if ($s.ContainsKey('runs')) { @{ workflow_runs = $s.runs } | ConvertTo-Json -Depth 5; return }
            $runs = @(@{ id = 101; run_attempt = 1; conclusion = 'failure'; updated_at = $s.redAt; head_sha = $script:RedSha; html_url = 'u101' })
            if (-not $s.noGreen) { $runs += @{ id = 100; run_attempt = 1; conclusion = 'success'; updated_at = '2026-11-09T07:00:00Z'; head_sha = $script:GreenSha; html_url = 'u100' } }
            if ($s.newest -eq 'success') { $runs += @{ id = 102; run_attempt = 1; conclusion = 'success'; updated_at = '2026-11-09T07:55:00Z'; head_sha = 'c' * 40; html_url = 'u102' } }
            if ($s.newest -eq 'failure2') { $runs += @{ id = 102; run_attempt = 1; conclusion = 'failure'; updated_at = '2026-11-09T07:59:00Z'; head_sha = 'd' * 40; html_url = 'u102' } }
            @{ workflow_runs = $runs } | ConvertTo-Json -Depth 5; return
        }
        "^api repos/o/r/compare/$script:GreenSha\.\.\.$script:RedSha$" {
            @{ commits = @(1..$s.commits | ForEach-Object { @{ sha = "$_" * 40; parents = @(@{ sha = $script:GreenSha }) } }) } | ConvertTo-Json -Depth 5; return
        }
        "^api repos/o/r/commits/$script:RedSha/pulls$" {
            $prs = @(1..2 | Select-Object -First $s.prs | ForEach-Object {
                    @{ number = 41 + $_; html_url = "https://github.com/o/r/pull/$(41 + $_)"; merged_at = '2026-11-09T07:30:00Z'; base = @{ ref = 'main' }; merge_commit_sha = $script:RedSha } })
            ConvertTo-Json -InputObject $prs -Depth 5; return
        }
        '^issue list .*--label main-red ' { ConvertTo-Json -InputObject @(@{ number = 9; body = $s.entry; author = @{ login = 'app/github-actions' } }) -Depth 5; return }
        '^issue view 9 ' { @{ comments = @($s.comments) } | ConvertTo-Json -Depth 5; return }
        '^issue list .*--label quarantined ' { ConvertTo-Json -InputObject @($s.quarantined) -Depth 5; return }
        '^issue list .*--label revert-culprit ' { ConvertTo-Json -InputObject @($s.culpritIssues) -Depth 5; return }
        '^pr list .*--head revert-culprit/' { ConvertTo-Json -InputObject @($s.revertPrs | Where-Object { $a -like "*--head $($_.head) *" }) -Depth 5; return }
        '^pr list .*--label revert-culprit ' { ConvertTo-Json -InputObject @($s.revertPrs) -Depth 5; return }
        '^label create revert-culprit ' { $s.writes += 'label'; return }
        '^issue create ' {
            $s.writes += 'issue'; $s.body = $body
            $s.culpritIssues += @{ number = 60; body = $body; author = @{ login = 'app/github-actions' } }; return
        }
        '^pr create --repo o/r --base main --head revert-culprit/bbbbbbbbbbbb ' { $s.writes += 'pr'; $s.body = $body; $s.revertPrs += @{ number = 50; head = 'revert-culprit/bbbbbbbbbbbb'; body = $body; author = @{ login = 'app/github-actions' } }; return }
        default { throw "Unexpected fake gh request: $a" }
    }
}
function Fake-Git {
    $a = $args -join ' '
    $s.git += $a
    if ($a -like '* revert --no-edit *' -and $s.conflict) { $global:LASTEXITCODE = 1; return }
    if ($a -like 'push *') {
        $s.writes += 'push'; $s.pushAuth = "$env:GIT_CONFIG_COUNT|$env:GIT_CONFIG_KEY_0|$env:GIT_CONFIG_VALUE_0"
        # A failing push that echoes the credential proves it is masked wherever the failure is reported.
        if ($s.pushFails) { "fatal: Authentication failed with $script:Token ($script:Basic)"; $global:LASTEXITCODE = 128 }
    }
    if ($a -eq "rev-parse --verify --quiet $script:RedSha^1") { if ($s.parent) { $s.parent } else { $global:LASTEXITCODE = 1 } }
    if ($a -like 'ls-remote --exit-code --heads origin revert-culprit/*') { $global:LASTEXITCODE = if ($s.branchExists) { 0 } else { 2 } }
}
function Tick { $s.result = Invoke-RevertCulprit -Repository 'o/r' -Minutes 15 -Now ([datetime]'2026-11-09T08:00:00Z').ToUniversalTime() }

Reset-Scenario; $s.redAt = '2026-11-09T07:46:00Z'; Tick
Assert ($s.writes.Count -eq 0 -and $s.git.Count -eq 0) 'red for 14 minutes does nothing'
Reset-Scenario; Tick
Assert (($s.writes -join ',') -eq 'label,push,pr') "red for 20 minutes with one culprit opens one revert PR ($($s.result))"
Assert ((@($s.git | Where-Object { $_ -like 'push *' }) -join ';') -eq 'push --quiet origin HEAD:refs/heads/revert-culprit/bbbbbbbbbbbb') 'the revert is pushed to its own branch, never to main'
Assert ($s.git -contains "-c user.name=github-actions[bot] -c user.email=41898282+github-actions[bot]@users.noreply.github.com revert --no-edit $script:RedSha") 'the culprit commit is the one reverted'
Assert ($s.body.Contains('https://github.com/o/r/issues/9') -and $s.body.Contains('https://github.com/o/r/pull/42') -and
    $s.body.Contains('A.B.C.Method') -and $s.body.Contains('Author-model:')) 'the revert PR links the main-red issue and culprit PR, with attribution'
Assert (@($s.calls | Where-Object { $_ -match '^pr (merge|ready)|--auto' }).Count -eq 0) 'the revert PR is never armed or merged'
Tick
Assert (($s.writes -join ',') -eq 'label,push,pr') 'a second tick opens no duplicate revert PR'
foreach ($evidence in 'no test name was found', 'log was unreadable') {
    Reset-Scenario; $s.entry = $s.entry.Replace('A.B.C.Method', $evidence); Tick
    Assert ($s.writes.Count -eq 0 -and $s.git.Count -eq 0) "infra-only red ($evidence) does nothing"
}
Reset-Scenario; $s.quarantined = @(@{ number = 3; title = 'Quarantine A.B.C.Method (main-red)'; body = ''; author = @{ login = 'app/github-actions' } }); Tick
Assert ($s.writes.Count -eq 0) 'a red quarantined test does nothing'
Reset-Scenario; $s.newest = 'success'; Tick
Assert ($s.writes.Count -eq 0 -and $s.calls.Count -eq 1) 'green main does nothing'
Reset-Scenario; $s.entry = $s.entry.Replace('run=101', 'run=99'); Tick
Assert ($s.writes.Count -eq 0) 'a main-red issue describing an older run does nothing'
Reset-Scenario; $s.newest = 'failure2'; $s.comments = @(@{ author = @{ login = 'github-actions' }; body = $s.entry.Replace('run=101', 'run=102') }); Tick
Assert (($s.writes -join ',') -eq 'label,push,pr' -and $s.git -match "revert --no-edit $script:RedSha$") 'a test failing since the first red run reverts that run''s commit'
Reset-Scenario; $s.newest = 'failure2'; $s.comments = @(@{ author = @{ login = 'github-actions' }; body = $s.entry.Replace('run=101', 'run=102') })
$s.entry = $s.entry.Replace('A.B.C.Method', 'no test name was found'); Tick
Assert (($s.writes -join ',') -eq 'label,issue' -and $s.git.Count -eq 0) 'a test that began failing after an infra-only first red run opens an issue, not a revert'
Reset-Scenario; $s.parent = ''; $threw = $false
try { Tick } catch { $threw = $_.Exception.Message -like '*parent is not in the local history*' }
Assert ($threw -and $s.writes.Count -eq 0 -and -not ($s.git -match 'revert')) 'a shallow checkout refuses to revert'
foreach ($case in @(@{ commits = 2 }, @{ prs = 0 }, @{ prs = 2 }, @{ noGreen = $true }, @{ branchExists = $true }, @{ conflict = $true })) {
    Reset-Scenario; foreach ($k in @($case.Keys)) { $s[$k] = $case[$k] }; $name = "$($case.Keys) = $($case.Values)"; Tick
    Assert (($s.writes -join ',') -eq 'label,issue' -and $s.body.Contains('https://github.com/o/r/issues/9') -and
        $s.body.Contains("revert-culprit: first-red=$script:RedSha")) "an unknown or ambiguous culprit opens an issue instead (${name}: $($s.result))"
    Tick
    Assert (($s.writes -join ',') -eq 'label,issue') "a second tick opens no duplicate issue ($name)"
}
Assert ($s.git -contains 'revert --abort') 'a conflicting revert is aborted'
function Run($Id, $At, $Sha, $Attempt = 1) { @{ id = $Id; run_attempt = $Attempt; conclusion = 'failure'; updated_at = "2026-11-09T$At`:00Z"; head_sha = $Sha; html_url = "u$Id" } }
function Entry($Id, $Test, $Attempt = 1) { @{ author = @{ login = 'github-actions' }; body = $s.entry.Replace('run=101 attempt=1', "run=$Id attempt=$Attempt").Replace('A.B.C.Method', $Test) } }
# The 07:40 commit is the culprit only if one test failed in every red run's current attempt since then.
foreach ($middle in 'A.B.C.Method', 'U.Other.Test', $null) {
    Reset-Scenario
    $s.runs = @(@{ id = 100; run_attempt = 1; conclusion = 'success'; updated_at = '2026-11-09T07:00:00Z'; head_sha = $script:GreenSha; html_url = 'u100' },
        (Run 101 '07:40' $script:RedSha), (Run 102 '07:50' ('c' * 40)), (Run 103 '07:59' ('d' * 40)))
    $s.comments = @(@(if ($middle) { Entry 102 $middle }) + (Entry 103 'A.B.C.Method')); Tick
    $expected = if ($middle -eq 'A.B.C.Method') { 'label,push,pr' } else { 'label,issue' }
    Assert (($s.writes -join ',') -eq $expected) "with middle-run evidence '$middle', T at 07:40 and 07:59 gives $expected ($($s.result))"
}
Reset-Scenario
$s.runs = @(@{ id = 100; run_attempt = 1; conclusion = 'success'; updated_at = '2026-11-09T07:00:00Z'; head_sha = $script:GreenSha; html_url = 'u100' },
    (Run 101 '07:40' $script:RedSha 2), (Run 102 '07:59' ('d' * 40)))
$s.comments = @((Entry 101 'U.Other.Test' 2), (Entry 102 'A.B.C.Method')); Tick
Assert ($s.writes -notcontains 'pr') "an obsolete attempt of the first red run does not authorize a revert ($($s.result))"
Reset-Scenario; $s.conflict = $true; Tick; $s.conflict = $false; Tick
Assert (($s.writes -join ',') -eq 'label,issue') 'a culprit issue stops a later revert PR for the same culprit'
Reset-Scenario; Tick; $s.newest = 'failure2'; $s.comments = @((Entry 102 'U.Other.Test')); Tick
Assert (($s.writes -join ',') -eq 'label,push,pr') 'a revert PR stops a later culprit issue for the same culprit'
Reset-Scenario; Tick; $s.runs = @(@{ id = 100; run_attempt = 1; conclusion = 'success'; updated_at = '2026-11-09T07:00:00Z'; head_sha = $script:GreenSha; html_url = 'u100' }, (Run 102 '07:44' ('c' * 40)))
$s.comments = @((Entry 102 'A.B.C.Method')); try { Tick } catch { $s.result = "threw: $_" }
Assert ($s.result -eq 'revert PR already exists') "a re-run that moves the spell's first red run opens nothing more for that main-red issue ($($s.result))"
# Checkout persists no credentials, so the push must authenticate itself from GH_TOKEN without exposing it.
function Seen { (@($s.result, $s.body) + $s.git + $s.calls) -join "`n" }
Reset-Scenario; $log = & { try { Tick } catch { "threw: $_" } } *>&1 | Out-String
Assert ($s.pushAuth -ceq "1|http.https://github.com/.extraheader|AUTHORIZATION: basic $script:Basic" -and -not $env:GIT_CONFIG_VALUE_0 -and
    ($s.writes -join ',') -eq 'label,push,pr') 'the push carries GH_TOKEN as a basic auth header for that one git command only'
$seen = $log + (Seen)
Reset-Scenario; $s.pushFails = $true; $log = & { try { Tick } catch { "threw: $_" } } *>&1 | Out-String
Assert (($s.writes -join ',') -eq 'label,push,issue' -and $s.body.Contains('could not be pushed') -and $s.body.Contains('***')) "a failed push opens the fallback issue ($($s.result))"
$seen += $log + (Seen)
Assert (-not $seen.Contains($script:Token) -and -not $seen.Contains($script:Basic) -and -not ($s.git -match 'remote set-url|config ')) 'the token never appears in output, errors, the issue, git arguments or the remote URL'
Reset-Scenario; Remove-Item Env:GH_TOKEN; $threw = $null
try { Tick } catch { $threw = "$_" }
$env:GH_TOKEN = $script:Token
Assert ($threw -like '*GH_TOKEN is not set*' -and $s.writes.Count -eq 0 -and -not ($s.git -match '^push ')) "a missing GH_TOKEN fails clearly before any write ($threw)"
if ($script:failures) { "$script:failures of $script:checks checks failed"; exit 1 }
"revert-culprit: $script:checks checks passed"
