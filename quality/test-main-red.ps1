#Requires -Version 7
# Offline only: every GitHub request is handled by the stand-in.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'main-red.ps1')
$script:failures = 0
function Assert([bool]$Ok, [string]$Name) { if ($Ok) { "ok   $Name" } else { "FAIL $Name"; $script:failures++ } }
$script:GhCommand = 'Fake-Gh'
function Fake-Gh {
    $script:Calls.Add(@($args))
    $a = $args -join ' '
    $s = $script:Scenario
    switch -Regex ($a) {
        '^api repos/o/r/actions/runs/100$' { $s.run | ConvertTo-Json -Depth 5; return }
        '^api repos/o/r/actions/workflows/ci.yml/runs\?' {
            @{ workflow_runs = @(@{ id = $s.latest }) } | ConvertTo-Json -Depth 5; return }
        '^api repos/o/r/actions/runs/100/jobs' {
            if ($s.ContainsKey('jobPages')) { ConvertTo-Json -InputObject $s.jobPages -Depth 6; return }
            @(@{ jobs = @($s.jobs) }) | ConvertTo-Json -Depth 6 -AsArray; return }
        '^api repos/o/r/actions/jobs/\d+/logs' {
            if ($s.logFails) { $global:LASTEXITCODE = 1; return }
            if ('--allow-escape-sequences' -notin $args) { throw 'Log request must preserve ANSI sequences' }
            $s.log; return }
        '^issue list' { ConvertTo-Json -InputObject @($s.issues) -Depth 6; return }
        '^issue view' { @{ comments = @($s.comments) } | ConvertTo-Json -Depth 6; return }
        '^label create' { return }
        '^issue create' {
            $s.issues = @(@{ number = 4; body = $args[([array]::IndexOf($args, '--body') + 1)] }); return }
        '^issue comment' {
            $s.comments += @{ body = $args[([array]::IndexOf($args, '--body') + 1)]; author = @{ login = 'github-actions[bot]' } }; return }
        '^issue close' { $s.issues = @(); return }
        default { throw "Unexpected gh request: $a" }
    }
}
function Reset-Scenario {
    $script:Calls = [Collections.Generic.List[object]]::new()
    $script:Scenario = @{
        run = @{ id = 100; path = '.github/workflows/ci.yml'; event = 'push'; head_branch = 'main'
            head_repository = @{ full_name = 'o/r' }; status = 'completed'; conclusion = 'failure'
            run_attempt = 1; head_sha = ('abcdef01' * 5) }
        latest = 100; issues = @(); comments = @(); logFails = $false
        jobs = @(@{ id = 7; name = 'product (windows-latest)'; conclusion = 'failure' }
            @{ id = 8; name = 'other'; conclusion = 'success' })
        log = "2026-11-18T07:00:00Z   `e[31mFailed A.B.C.Method`e[0m [1 s]"
    }
}
function Writes { ,@($script:Calls | Where-Object { $_[0] -eq 'label' -or ($_[0] -eq 'issue' -and $_[1] -in 'create','comment','close','edit') }) }
function Requests([string]$Start) { ,@($script:Calls | Where-Object { ($_ -join ' ').StartsWith($Start) }) }
function Last-Body {
    $write = (Requests 'issue create') + (Requests 'issue comment')
    $last = $write[-1]
    $last[([array]::IndexOf($last, '--body') + 1)]
}
function Detect { Invoke-MainRed -RunId 100 -Repository 'o/r' | Out-Null }

Reset-Scenario; Detect
$body = Last-Body
Assert ((Requests 'issue create').Count -eq 1 -and $body -match 'product \(windows-latest\)' -and $body -match 'A.B.C.Method') 'red opens one issue naming failed jobs and tests'
Assert (($script:Calls | ConvertTo-Json -Depth 6) -notmatch '--assignee|--mention|@someone') 'opening uses neither assignees nor mentions'
Assert ((Requests 'issue create')[0] -contains 'main-red') 'the new issue carries the main-red label'
Assert ($body -match 'https://github.com/o/r/actions/runs/100' -and $body -match 'attempt=1' -and $body -match ('abcdef01' * 5)) 'evidence binds the run URL, attempt and full sha'
$before = (Writes).Count; Detect
Assert ((Writes).Count -eq $before) 'a duplicate event for the issue-opening run makes no writes'

Reset-Scenario
$script:Scenario.issues = @(@{ number = 4; body = 'previous sha' })
Detect
Assert ((Requests 'issue comment').Count -eq 1 -and (Requests 'issue create').Count -eq 0) 'another red run comments without opening a second issue'
$before = (Writes).Count; Detect
Assert ((Writes).Count -eq $before) 'a duplicate event for an update makes no writes'
$script:Scenario.run.run_attempt = 2; Detect
Assert ((Requests 'issue comment').Count -eq 2) 'a new attempt of the same run gets its own evidence'
Assert ((Requests 'issue list')[0] -contains 'github-actions[bot]') 'lookup restricts the label to the Actions author'

Reset-Scenario
$script:Scenario.issues = @(@{ number = 4; body = 'old red' })
$script:Scenario.comments = @(@{ body = '<!-- main-red run=100 attempt=1 -->'; author = @{ login = 'stranger' } })
Detect
Assert ((Requests 'issue comment').Count -eq 1) 'an untrusted comment cannot spoof delivery evidence'
Reset-Scenario
$script:Scenario.issues = @(@{ number = 4; body = "job: <!-- main-red run=100 attempt=1 -->`n" })
$script:Scenario.comments = @(@{ body = "job: <!-- main-red run=100 attempt=1 -->`n"; author = @{ login = 'github-actions[bot]' } })
Detect
Assert ((Requests 'issue comment').Count -eq 1) 'a marker embedded in published untrusted names is not a receipt'

Reset-Scenario
$script:Scenario.run.conclusion = 'success'; $script:Scenario.run.run_attempt = 2
$script:Scenario.issues = @(@{ number = 4; body = 'old red' })
Detect
Assert ((Requests 'issue comment').Count -eq 1 -and (Requests 'issue close').Count -eq 1) 'green attempt two comments and closes'
Assert ((Last-Body) -match 'main-red: green again, run=100 attempt=2, sha=abcdef01') 'recovery comment identifies its run, attempt and sha'
Assert (($script:Calls[-2] -join ' ') -like 'issue comment*' -and ($script:Calls[-1] -join ' ') -like 'issue close*') 'the recovery comment precedes closing'
Reset-Scenario; $script:Scenario.run.conclusion = 'success'; Detect
Assert ((Writes).Count -eq 0) 'green without an open incident changes nothing'
foreach ($conclusion in 'cancelled','skipped','neutral','action_required') {
    Reset-Scenario; $script:Scenario.run.conclusion = $conclusion
    $script:Scenario.issues = @(@{ number = 4; body = '' }); Detect
    Assert ((Writes).Count -eq 0) "$conclusion neither opens nor closes"
}
foreach ($conclusion in 'startup_failure','timed_out') {
    Reset-Scenario; $script:Scenario.run.conclusion = $conclusion; Detect
    Assert ((Requests 'issue create').Count -eq 1) "$conclusion is red"
}
foreach ($conclusion in 'failure','success') {
    Reset-Scenario; $script:Scenario.latest = 101; $script:Scenario.run.conclusion = $conclusion; Detect
    Assert ((Writes).Count -eq 0) "a stale $conclusion event cannot act"
}
Reset-Scenario; $script:Scenario.run.status = 'in_progress'; Detect
Assert ((Writes).Count -eq 0) 'an attempt still running cannot act'
foreach ($field in 'event','head_branch','path') {
    Reset-Scenario; $script:Scenario.run[$field] = 'untrusted'; Detect
    Assert ((Writes).Count -eq 0) "a non-main CI push ($field) cannot act"
}
Reset-Scenario; $script:Scenario.run.head_repository.full_name = 'someone/fork'; Detect
Assert ((Writes).Count -eq 0) 'a fork run cannot act'
Reset-Scenario; $script:Scenario.log = 'Build FAILED. error CS1002'; Detect
Assert ((Last-Body) -match 'product \(windows-latest\)' -and (Last-Body) -match 'no test name found') 'build errors still identify the job without inventing a test'
Reset-Scenario; $script:Scenario.logFails = $true; Detect
Assert ((Requests 'issue create').Count -eq 1 -and (Last-Body) -match 'log unreadable') 'unreadable logs still open an incident without failing detection'
Reset-Scenario
$script:Scenario.jobs = @(@{ id = 7; name = 'hung'; conclusion = 'timed_out' })
$script:Scenario.log = ''; Detect
Assert ((Last-Body) -match 'hung' -and (Last-Body) -match 'no test name found') 'timed-out jobs are included'
Reset-Scenario; $script:Scenario.jobs = @(); Detect
Assert ((Last-Body) -match 'no failed jobs reported') 'startup failures without jobs remain visible'
Reset-Scenario
$script:Scenario.jobPages = @(@{ jobs = @($script:Scenario.jobs) }, @{ jobs = @(@{ id = 9; name = 'second page'; conclusion = 'failure' }) })
Detect
Assert ((Last-Body) -match 'second page' -and (Requests 'api repos/o/r/actions/jobs/9/logs').Count -eq 1) 'failed jobs on later API pages are not lost'
Reset-Scenario
$script:Scenario.jobs[0].name = "@someone```n" + ('x' * 20000)
$script:Scenario.log = '  1) [webkit] › @someone```spec.mjs:43:1 › title'
Detect; $body = Last-Body
Assert ($body -notmatch '@' -and ([regex]::Matches($body, '```')).Count -eq 2 -and $body.Length -le 16000) 'untrusted mentions, fences and long names cannot escape the capped body'
$run = [pscustomobject]$script:Scenario.run
$many = 1..40 | ForEach-Object { [pscustomobject]@{ Name = ('j' * 1000); Tests = @('t' * 1000); LogUnreadable = $false } }
$body = Format-MainRedBody $run 'o/r' $many
Assert ($body.Length -le 16000 -and $body.EndsWith('```') -and $body -match 'truncated') 'aggregate truncation preserves the closing code fence'
if ($script:failures) { throw "$($script:failures) main-red checks failed" }
'main-red: all checks passed'
