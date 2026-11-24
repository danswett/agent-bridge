#Requires -Version 7
# Offline only: a stub gh answers from files and records every call; nothing reaches GitHub.
$ErrorActionPreference = 'Stop'
$script:failures = 0
$script:checks = 0
function Assert([bool]$Ok, [string]$Name) {
    $script:checks++
    if ($Ok) { "ok   $Name" } else { "FAIL $Name"; $script:failures++ }
}
$work = Join-Path ([IO.Path]::GetTempPath()) ("full-red-test-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null
$script = Join-Path $PSScriptRoot 'full-red.ps1'
$sha = 'abcdef0123456789abcdef0123456789abcdef01'
$other = '1111111111111111111111111111111111111111'

$stub = Join-Path $work 'gh-stub.ps1'
Set-Content -Path $stub -Value @'
$dir = $env:FULL_RED_STUB
$line = ($args -join ' ') -replace "[`r`n]+", ' ~ '
Add-Content -Path (Join-Path $dir 'calls.log') -Value $line
$a = $args -join ' '
function Flag([string]$name) { Test-Path (Join-Path $dir $name) }
$out = $null
if ($a -match '^api repos/o/r/actions/runs/101$') { $out = Get-Content (Join-Path $dir 'run.json') -Raw }
elseif ($a -match 'actions/runs/101/attempts/\d+/jobs') { $out = Get-Content (Join-Path $dir 'jobs.json') -Raw }
elseif ($a -match '^api repos/o/r/issues\?state=open') { if (Flag 'fail-search') { Write-Output ('gh: HTTP 500'); exit 1 }; $out = Get-Content (Join-Path $dir 'issues.json') -Raw }
elseif ($a -match '-X POST repos/o/r/labels') { if (Flag 'label-exists') { Write-Output ('gh: Validation Failed (HTTP 422) already_exists'); exit 1 }; if (Flag 'label-denied') { Write-Output ('gh: HTTP 403 Resource not accessible'); exit 1 }; $out = '{}' }
elseif ($a -match '-X POST repos/o/r/issues( |$)') { if (Flag 'fail-create') { Write-Output ('gh: HTTP 500'); exit 1 }; $out = '{"number": 9}' }
elseif ($a -match '-X (POST|PATCH) repos/o/r/issues') { $out = '{"number": 9}' }
else { Write-Output ("stub: unexpected call $a"); exit 1 }
$out
'@

function Job([string]$name, [string]$conclusion) { @{ name = $name; conclusion = $conclusion; html_url = "https://github.com/o/r/actions/runs/101/job/$($name.Length)" } }
function Issue([int]$number, [string]$key, [string]$last = $other, [string]$state = 'red', $labels = @(@{ name = 'full-red' }), $user = @{ login = 'github-actions[bot]'; type = 'Bot' }) {
    @{ number = $number; state = 'open'; labels = $labels; user = $user; body = "<!-- full-red-job:$key -->`n<!-- full-red-last:$last -->`n<!-- full-red-state:$state -->`nold body" }
}
# Runs the script on one case; the flags are files the stub looks for. Returns the exit code and every call.
function Run-Case($jobs, $issues = @(), [hashtable]$run = @{}, [string[]]$flags = @()) {
    $d = Join-Path $work ('case-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $d | Out-Null
    $r = @{ id = 101; status = 'completed'; conclusion = 'failure'; head_sha = $sha; head_branch = 'main'; event = 'workflow_run'; run_attempt = 1
        head_repository = @{ full_name = 'o/r' }; html_url = 'https://github.com/o/r/actions/runs/101' }
    foreach ($key in $run.Keys) { $r[$key] = $run[$key] }
    $r | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $d 'run.json')
    ConvertTo-Json -Depth 6 -InputObject @(, @{ jobs = @($jobs) }) | Set-Content (Join-Path $d 'jobs.json')
    ConvertTo-Json -Depth 6 -InputObject @(, @($issues)) | Set-Content (Join-Path $d 'issues.json')
    foreach ($flag in $flags) { Set-Content (Join-Path $d $flag) 'x' }
    $env:FULL_RED_STUB = $d
    try { $text = (& pwsh -NoProfile -File $script -RunId 101 -Repository 'o/r' -GhCommand $stub 2>&1 | Out-String) }
    finally { $env:FULL_RED_STUB = $null }
    $log = Join-Path $d 'calls.log'
    [pscustomobject]@{ exit = $LASTEXITCODE; text = $text; calls = @(if (Test-Path $log) { Get-Content $log }) }
}
function Writes($r) { , @($r.calls | Where-Object { $_ -match '-X (POST|PATCH)' }) }
function Creates($r) { , @($r.calls | Where-Object { $_ -match '-X POST repos/o/r/issues( |$)' }) }
function Comments($r) { , @($r.calls | Where-Object { $_ -match 'issues/\d+/comments' }) }

try {
    $green = @((Job 'trigger' 'success'), (Job 'spikes' 'success'), (Job 'full-gate' 'success'))
    $r = Run-Case $green -run @{ conclusion = 'success' }
    Assert ($r.exit -eq 0 -and (Writes $r).Count -eq 0) 'a green FULL run opens nothing'

    $spikesRed = @((Job 'trigger' 'success'), (Job 'spikes' 'failure'), (Job 'pwa-e2e' 'success'), (Job 'full-gate' 'failure'))
    $r = Run-Case $spikesRed
    $create = Creates $r
    Assert ($r.exit -eq 0 -and $create.Count -eq 1 -and (Writes $r).Count -eq 2) 'a red job opens exactly one issue (and the label is made first)'
    Assert ($create[0] -match 'title=FULL lane red: spikes' -and ([regex]::Matches($create[0], 'labels\[\]=')).Count -eq 1 -and $create[0] -match 'labels\[\]=full-red( |$)') 'the title names the job and the only label is full-red, exactly'
    Assert ($create[0] -match '<!-- full-red-job:spikes -->' -and $create[0] -match "<!-- full-red-last:$sha -->" -and $create[0] -match 'actions/runs/101/job/') 'the body carries the job marker, the commit and a link to the failed job'
    Assert ($create[0] -match 'Blocking' -and $create[0] -notmatch 'full-gate\]') 'a blocking failure says so, and the aggregate full-gate is not a cause'

    $two = @((Job 'trigger' 'success'), (Job 'spikes' 'failure'), (Job 'pwa-e2e' 'timed_out'), (Job 'full-gate' 'failure'))
    $r = Run-Case $two
    Assert ((Creates $r).Count -eq 2) 'two red jobs open two issues, one per job'

    $r = Run-Case $spikesRed @((Issue 7 'spikes'))
    $w = Writes $r
    Assert ((Creates $r).Count -eq 0 -and @($w | Where-Object { $_ -match '-X PATCH repos/o/r/issues/7 ' }).Count -eq 1 -and (Comments $r).Count -eq 1) 'the same job red on a newer commit is a comment on its open issue, not a second issue'
    $r = Run-Case $spikesRed @((Issue 7 'spikes' $sha))
    Assert ((Writes $r).Count -eq 0) 'a re-run of the commit already recorded adds nothing'
    $r = Run-Case $spikesRed @((Issue 7 'spikes' $other 'red' @()))
    Assert ((Creates $r).Count -eq 0 -and (Comments $r).Count -eq 1) 'the issue is found by its marker, not its label: with the label removed there is still no second issue'
    $r = Run-Case $spikesRed @((Issue 7 'pwa-e2e'), @{ number = 8; state = 'open'; pull_request = @{ url = 'x' }; body = '<!-- full-red-job:spikes --> a pull request' })
    Assert ((Creates $r).Count -eq 1) 'another job''s issue is not reused, and a pull request carrying the text is ignored'

    # Only the workflow's own issues count: a person's issue that carries the marker must never have its body rewritten or a comment added (#4503, R4zzB827).
    $alice = @{ login = 'alice'; type = 'User' }
    $r = Run-Case $spikesRed @((Issue 7 'spikes' $other 'red' @(@{ name = 'full-red' }) $alice))
    Assert ((Creates $r).Count -eq 1 -and @((Writes $r) | Where-Object { $_ -match 'issues/7' }).Count -eq 0) 'a person''s issue carrying the marker is not touched: a red job opens its own issue instead'
    $noUser = Issue 7 'spikes'; $noUser.Remove('user')
    $r = Run-Case $spikesRed @($noUser)
    Assert ((Creates $r).Count -eq 1 -and @((Writes $r) | Where-Object { $_ -match 'issues/7' }).Count -eq 0) 'an issue with no author is not the workflow''s: it is left alone'
    $r = Run-Case $spikesRed @((Issue 7 'spikes' $other 'red' @(@{ name = 'full-red' }) @{ login = 'github-actions[bot]'; type = 'User' }))
    Assert ((Creates $r).Count -eq 1) 'the bot''s login with a user type is not the bot'
    $r = Run-Case $spikesRed @((Issue 7 'spikes' $other 'red' @(@{ name = 'full-red' }) @{ login = 'dependabot[bot]'; type = 'Bot' }))
    Assert ((Creates $r).Count -eq 1 -and @((Writes $r) | Where-Object { $_ -match 'issues/7' }).Count -eq 0) 'another app''s bot is not the workflow''s token either: only github-actions[bot]'
    $r = Run-Case @((Job 'spikes' 'success')) @((Issue 7 'spikes' $other 'red' @(@{ name = 'full-red' }) $alice)) -run @{ conclusion = 'success' }
    Assert ((Writes $r).Count -eq 0) 'a person''s issue is not commented or rewritten when its job is green again'
    $r = Run-Case $spikesRed @((Issue 7 'spikes' $other 'red' @(@{ name = 'full-red' }) $alice), (Issue 8 'spikes'))
    Assert ((Creates $r).Count -eq 0 -and @((Writes $r) | Where-Object { $_ -match 'issues/8' }).Count -ge 1 -and @((Writes $r) | Where-Object { $_ -match 'issues/7' }).Count -eq 0) 'the workflow''s own issue is found past a person''s with the same marker'

    $waivedOnly = @((Job 'trigger' 'success'), (Job 'app-selftest (win-x64)' 'success'), (Job 'app-installer (win-arm64)' 'failure'), (Job 'full-gate' 'success'))
    $r = Run-Case $waivedOnly -run @{ conclusion = 'success' }
    $create = Creates $r
    Assert ($create.Count -eq 1 -and $create[0] -match 'Waived' -and $create[0] -match 'publish continues' -and $create[0] -match 'title=FULL lane Arm64 result red \(waived\): app-installer \(win-arm64\)') 'a red Arm64 installer still opens an issue, as waived, though the run succeeded'
    $pack = @((Job 'trigger' 'success'), (Job 'app-pack (win-arm64)' 'failure'), (Job 'full-gate' 'failure'))
    $create = Creates (Run-Case $pack)
    Assert ($create.Count -eq 1 -and $create[0] -match 'Blocking' -and $create[0] -notmatch 'Waived') 'the Arm64 pack is blocking, never waived'

    $gateOnly = @((Job 'trigger' 'success'), (Job 'spikes' 'skipped'), (Job 'full-gate' 'failure'))
    $create = Creates (Run-Case $gateOnly)
    Assert ($create.Count -eq 1 -and $create[0] -match '<!-- full-red-job:\(whole run\) -->' -and $create[0] -match 'skipped or cancelled') 'a failed run with no failed job (a job skipped) is red as the whole run'
    $create = Creates (Run-Case @() -run @{ conclusion = 'startup_failure' })
    Assert ($create.Count -eq 1 -and $create[0] -match 'startup failure') 'a run that failed with no jobs at all (startup failure) is red'

    $r = Run-Case $green @((Issue 7 'spikes' $other 'red')) -run @{ conclusion = 'success' }
    $w = Writes $r
    Assert ($w.Count -eq 2 -and ($w -match 'full-red-state:green') -and (Comments $r).Count -eq 1 -and ($w -match 'Close this if the cause is fixed')) 'a job that passes again gets one comment on its open issue, which a person closes'
    $r = Run-Case $green @((Issue 7 'spikes' $other 'green')) -run @{ conclusion = 'success' }
    Assert ((Writes $r).Count -eq 0) 'a green job whose issue already says green adds nothing'

    $hostile = @((Job 'trigger' 'success'), (Job '@org/team <b>x</b> [l](http://e.test) -->' 'failure'), (Job 'full-gate' 'failure'))
    $create = Creates (Run-Case $hostile)
    Assert ($create.Count -eq 1 -and $create[0] -notmatch '@org' -and $create[0] -notmatch '<b>' -and $create[0] -notmatch '\]\(http://e\.test' -and $create[0] -notmatch 'x</b> -->') 'a job name is cut to a safe alphabet: no mention, tag, link or comment end reaches an issue'

    foreach ($case in @(
            @{ n = 'a run of a fork'; run = @{ head_repository = @{ full_name = 'evil/r' } } },
            @{ n = 'a pull_request run'; run = @{ event = 'pull_request' } },
            @{ n = 'a fork''s branch named main'; run = @{ head_repository = @{ full_name = 'evil/r' }; head_branch = 'main'; event = 'pull_request' } },
            @{ n = 'a run with no head repository'; run = @{ head_repository = $null } },
            @{ n = 'a cancelled run'; run = @{ conclusion = 'cancelled' } },
            @{ n = 'a run on another branch'; run = @{ head_branch = 'feature' } })) {
        $r = Run-Case $spikesRed -run $case.run
        Assert ($r.exit -eq 0 -and (Writes $r).Count -eq 0 -and @($r.calls | Where-Object { $_ -match 'attempts|issues' }).Count -eq 0) "$($case.n) is passed over: no job read, no issue"
    }
    $r = Run-Case $spikesRed -run @{ event = 'workflow_dispatch' }
    Assert ((Creates $r).Count -eq 1) 'a run by hand on main (workflow_dispatch) still counts'

    $r = Run-Case $spikesRed -flags 'fail-search'
    Assert ($r.exit -ne 0 -and (Writes $r).Count -eq 0) 'a failed issue search fails the run and writes nothing (it must not be swallowed into a duplicate)'
    $r = Run-Case $spikesRed -flags 'fail-create'
    Assert ($r.exit -ne 0) 'a failed issue create fails the run'
    $r = Run-Case $spikesRed -flags 'label-exists'
    Assert ($r.exit -eq 0 -and (Creates $r).Count -eq 1) 'a label that already exists (422) is no failure'
    $r = Run-Case $spikesRed -flags 'label-denied'
    Assert ($r.exit -ne 0 -and (Creates $r).Count -eq 0) 'any other label error (403) fails the run, before an issue is opened'

    $r = Run-Case $spikesRed -run @{ head_sha = 'not-a-sha' }
    Assert ($r.exit -ne 0 -and (Writes $r).Count -eq 0) 'a commit that is not 40 hex digits fails the run'
}
finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }

"$script:checks checks, $script:failures failed"
if ($script:failures -gt 0) { exit 1 }
