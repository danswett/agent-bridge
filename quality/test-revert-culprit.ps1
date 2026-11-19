#Requires -Version 7
# Offline only: every gh and git request must match these stand-ins.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'revert-culprit.ps1') -Repository 'o/r'
$script:GhCommand = 'Fake-Gh'
$script:GitCommand = 'Fake-Git'
$script:failures = 0
$script:checks = 0
$Green = 'a' * 40
$Red = 'b' * 40
function Assert([bool]$Ok, [string]$Name) {
    $script:checks++
    if ($Ok) { "ok   $Name" } else { "FAIL $Name"; $script:failures++ }
}
function Reset-Scenario {
    $script:s = @{ redAt = '2026-11-09T07:40:00Z'; newest = 'failure'; noGreen = $false; commits = 1; prs = 1; conflict = $false
        entry = @('main-red: run=101 attempt=1 sha=bbbbbbbb', '```', 'Job: product', '  A.B.C.Method', '```') -join "`n"
        quarantined = @(); revertPrs = @(); culpritIssues = @(); calls = @(); git = @(); writes = @(); body = '' }
}
function Fake-Gh {
    $a = $args -join ' '
    $s.calls += $a
    $body = if ($args -contains '--body') { $args[([array]::IndexOf($args, '--body') + 1)] }
    switch -Regex ($a) {
        '^api repos/o/r/actions/workflows/ci.yml/runs\?.*status=completed' {
            $runs = @(@{ id = 101; run_attempt = 1; conclusion = 'failure'; updated_at = $s.redAt; head_sha = $Red; html_url = 'u101' })
            if (-not $s.noGreen) { $runs += @{ id = 100; run_attempt = 1; conclusion = 'success'; updated_at = '2026-11-09T07:00:00Z'; head_sha = $Green; html_url = 'u100' } }
            if ($s.newest -eq 'success') { $runs += @{ id = 102; run_attempt = 1; conclusion = 'success'; updated_at = '2026-11-09T07:55:00Z'; head_sha = 'c' * 40; html_url = 'u102' } }
            @{ workflow_runs = $runs } | ConvertTo-Json -Depth 5; return
        }
        "^api repos/o/r/compare/$Green\.\.\.$Red$" {
            @{ commits = @(1..$s.commits | ForEach-Object { @{ sha = "$_" * 40; parents = @(@{ sha = $Green }) } }) } | ConvertTo-Json -Depth 5; return
        }
        "^api repos/o/r/commits/$Red/pulls$" {
            $prs = @(1..2 | Select-Object -First $s.prs | ForEach-Object {
                    @{ number = 41 + $_; html_url = "https://github.com/o/r/pull/$(41 + $_)"; merged_at = '2026-11-09T07:30:00Z'; base = @{ ref = 'main' }; merge_commit_sha = $Red } })
            ConvertTo-Json -InputObject $prs -Depth 5; return
        }
        '^issue list .*--label main-red ' { ConvertTo-Json -InputObject @(@{ number = 9; body = $s.entry; author = @{ login = 'app/github-actions' } }) -Depth 5; return }
        '^issue view 9 ' { @{ comments = @() } | ConvertTo-Json; return }
        '^issue list .*--label quarantined ' { ConvertTo-Json -InputObject @($s.quarantined) -Depth 5; return }
        '^issue list .*--label revert-culprit ' { ConvertTo-Json -InputObject @($s.culpritIssues) -Depth 5; return }
        '^pr list .*--head revert-culprit/bbbbbbbbbbbb ' { ConvertTo-Json -InputObject @($s.revertPrs) -Depth 5; return }
        '^label create revert-culprit ' { $s.writes += 'label'; return }
        '^issue create ' {
            $s.writes += 'issue'; $s.body = $body
            $s.culpritIssues += @{ number = 60; body = $body; author = @{ login = 'app/github-actions' } }; return
        }
        '^pr create --repo o/r --base main --head revert-culprit/bbbbbbbbbbbb ' { $s.writes += 'pr'; $s.body = $body; $s.revertPrs += @{ number = 50 }; return }
        default { throw "Unexpected fake gh request: $a" }
    }
}
function Fake-Git {
    $a = $args -join ' '
    $s.git += $a
    if ($a -like '* revert --no-edit *' -and $s.conflict) { $global:LASTEXITCODE = 1; return }
    if ($a -like 'push *') { $s.writes += 'push' }
}
function Tick { $s.result = Invoke-RevertCulprit -Repository 'o/r' -Minutes 15 -Now ([datetime]'2026-11-09T08:00:00Z').ToUniversalTime() }

Reset-Scenario; $s.redAt = '2026-11-09T07:46:00Z'; Tick
Assert ($s.writes.Count -eq 0 -and $s.git.Count -eq 0) 'red for 14 minutes does nothing'
Reset-Scenario; Tick
Assert (($s.writes -join ',') -eq 'push,pr') "red for 20 minutes with one culprit opens one revert PR ($($s.result))"
Assert ((@($s.git | Where-Object { $_ -like 'push *' }) -join ';') -eq 'push --quiet origin HEAD:refs/heads/revert-culprit/bbbbbbbbbbbb') 'the revert is pushed to its own branch, never to main'
Assert ($s.git -contains "-c user.name=github-actions[bot] -c user.email=41898282+github-actions[bot]@users.noreply.github.com revert --no-edit $Red") 'the culprit commit is the one reverted'
Assert ($s.body.Contains('https://github.com/o/r/issues/9') -and $s.body.Contains('https://github.com/o/r/pull/42') -and
    $s.body.Contains('A.B.C.Method') -and $s.body.Contains('Author-model:')) 'the revert PR links the main-red issue and culprit PR, with attribution'
Assert (@($s.calls | Where-Object { $_ -match '^pr (merge|ready)|--auto' }).Count -eq 0) 'the revert PR is never armed or merged'
Tick
Assert (($s.writes -join ',') -eq 'push,pr') 'a second tick opens no duplicate revert PR'
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
foreach ($case in @(@{ commits = 2 }, @{ prs = 0 }, @{ prs = 2 }, @{ noGreen = $true }, @{ conflict = $true })) {
    Reset-Scenario; foreach ($k in @($case.Keys)) { $s[$k] = $case[$k] }; $name = "$($case.Keys) = $($case.Values)"; Tick
    Assert (($s.writes -join ',') -eq 'label,issue' -and $s.body.Contains('https://github.com/o/r/issues/9') -and
        $s.body.Contains("revert-culprit: first-red=$Red")) "an unknown or ambiguous culprit opens an issue instead ($name: $($s.result))"
    Tick
    Assert (($s.writes -join ',') -eq 'label,issue') "a second tick opens no duplicate issue ($name)"
}
Assert ($s.git -contains 'revert --abort') 'a conflicting revert is aborted'
if ($script:failures) { "$script:failures of $script:checks checks failed"; exit 1 }
"revert-culprit: $script:checks checks passed"
