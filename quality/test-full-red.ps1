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
$out = $null
if ($a -match '^api repos/o/r/actions/runs/101$') { $out = Get-Content (Join-Path $dir 'run.json') -Raw }
elseif ($a -match 'actions/runs/101/attempts/\d+/jobs') { $out = Get-Content (Join-Path $dir 'jobs.json') -Raw }
elseif ($a -match '^api repos/o/r/issues\?labels=full-red') { $out = Get-Content (Join-Path $dir 'issues.json') -Raw }
elseif ($a -match '-X POST repos/o/r/labels') { if (Test-Path (Join-Path $dir 'label-exists')) { [Console]::Error.WriteLine('gh: Validation Failed (HTTP 422) already_exists'); exit 1 }; $out = '{}' }
elseif ($a -match '-X (POST|PATCH) repos/o/r/issues') { $out = '{"number": 9}' }
else { [Console]::Error.WriteLine("stub: unexpected call $a"); exit 1 }
$out
'@

function Job([string]$name, [string]$conclusion) { @{ name = $name; conclusion = $conclusion; html_url = "https://github.com/o/r/actions/runs/101/job/$($name.Length)" } }

# $jobs: the jobs of the run; $issues: issues already carrying the label; returns the calls and the exit code.
function Run-Case($jobs, $issues = @(), [string]$conclusion = 'failure', [string]$branch = 'main', [bool]$labelExists = $false, [string]$runSha = $sha) {
    $d = Join-Path $work ('case-' + [guid]::NewGuid().ToString('N')); New-Item -ItemType Directory -Path $d | Out-Null
    @{ id = 101; status = 'completed'; conclusion = $conclusion; head_sha = $runSha; head_branch = $branch; run_attempt = 1; html_url = 'https://github.com/o/r/actions/runs/101' } | ConvertTo-Json | Set-Content (Join-Path $d 'run.json')
    ConvertTo-Json -Depth 6 -InputObject @(, @{ jobs = @($jobs) }) | Set-Content (Join-Path $d 'jobs.json')
    ConvertTo-Json -Depth 6 -InputObject @(, @($issues)) | Set-Content (Join-Path $d 'issues.json')
    if ($labelExists) { Set-Content (Join-Path $d 'label-exists') 'x' }
    $env:FULL_RED_STUB = $d
    try { $text = (& pwsh -NoProfile -File $script -RunId 101 -Repository 'o/r' -GhCommand $stub 2>&1 | Out-String) }
    finally { $env:FULL_RED_STUB = $null }
    $log = Join-Path $d 'calls.log'
    [pscustomobject]@{ exit = $LASTEXITCODE; text = $text; calls = @(if (Test-Path $log) { Get-Content $log }) }
}
function Writes($r) { @($r.calls | Where-Object { $_ -match '-X (POST|PATCH)' }) }
function Issue([int]$number, [string]$state, [string]$forSha) { @{ number = $number; state = $state; body = "<!-- full-red:$forSha -->`nold body" } }

try {
    $green = @((Job 'trigger' 'success'), (Job 'spikes' 'success'), (Job 'full-gate' 'success'))
    $r = Run-Case $green
    Assert ($r.exit -eq 0 -and (Writes $r).Count -eq 0) 'a green FULL run opens nothing'

    $spikesRed = @((Job 'trigger' 'success'), (Job 'spikes' 'failure'), (Job 'pwa-e2e' 'success'), (Job 'full-gate' 'failure'))
    $r = Run-Case $spikesRed
    $create = @(Writes $r | Where-Object { $_ -match '-X POST repos/o/r/issues( |$)' })
    Assert ($r.exit -eq 0 -and $create.Count -eq 1) 'a red job opens exactly one issue'
    Assert ($create[0] -match 'title=FULL lane red on abcdef0: spikes' -and $create[0] -match 'labels\[\]=full-red') 'the issue is titled with the short commit and the failed job, and labelled full-red'
    Assert ($create[0] -match '<!-- full-red:abcdef0123456789abcdef0123456789abcdef01 -->' -and $create[0] -match 'actions/runs/101/job/') 'it carries the per-commit marker and a link to the failed job'
    Assert ($create[0] -match 'Blocking' -and $create[0] -notmatch 'Waived' -and $create[0] -notmatch 'full-gate\]') 'a blocking failure is listed as blocking, and the aggregate full-gate is not a cause'
    Assert (@(Writes $r | Where-Object { $_ -match 'repos/o/r/labels' }).Count -eq 1) 'the label is created when a new issue is opened'

    $r = Run-Case $spikesRed -labelExists $true
    Assert ($r.exit -eq 0 -and @(Writes $r | Where-Object { $_ -match '-X POST repos/o/r/issues( |$)' }).Count -eq 1) 'a label that already exists (422) is no failure'

    $waivedOnly = @((Job 'trigger' 'success'), (Job 'app-selftest (win-x64)' 'success'), (Job 'app-installer (win-arm64)' 'failure'), (Job 'app-selftest (win-arm64)' 'timed_out'), (Job 'full-gate' 'success'))
    $r = Run-Case $waivedOnly -conclusion 'success'
    $create = @(Writes $r | Where-Object { $_ -match '-X POST repos/o/r/issues( |$)' })
    Assert ($create.Count -eq 1 -and $create[0] -match 'Waived' -and $create[0] -notmatch 'Blocking') 'a red Arm64 installer or self-test still opens an issue, as waived, though the run succeeded'
    Assert ($create[0] -match 'title=FULL lane Arm64 results waived on abcdef0' -and $create[0] -match 'publish continues') 'its title and text say publish continues'

    $both = @((Job 'trigger' 'success'), (Job 'app-pack (win-arm64)' 'failure'), (Job 'app-installer (win-arm64)' 'failure'), (Job 'full-gate' 'failure'))
    $r = Run-Case $both
    $create = @(Writes $r | Where-Object { $_ -match '-X POST repos/o/r/issues( |$)' })
    Assert ($create[0] -match 'Blocking' -and $create[0] -match 'Waived' -and $create[0] -match 'app-pack \(win-arm64\)') 'the Arm64 pack is blocking, never waived, beside a waived installer'

    $gateOnly = @((Job 'trigger' 'success'), (Job 'spikes' 'skipped'), (Job 'full-gate' 'failure'))
    $r = Run-Case $gateOnly
    $create = @(Writes $r | Where-Object { $_ -match '-X POST repos/o/r/issues( |$)' })
    Assert ($create.Count -eq 1 -and $create[0] -match 'full-gate' -and $create[0] -match 'skipped or cancelled') 'full-gate red with no failed job (a skipped one) is still reported'

    $r = Run-Case $spikesRed @((Issue 7 'open' $sha))
    $w = @(Writes $r)
    Assert ($w.Count -eq 1 -and $w[0] -match '-X PATCH repos/o/r/issues/7' -and $w[0] -notmatch 'state=') 'an open issue for the commit is updated, not duplicated'

    $r = Run-Case $spikesRed @((Issue 7 'closed' $sha))
    $w = @(Writes $r)
    Assert ($w.Count -eq 2 -and ($w -match 'issues/7 ' -and $w -match 'state=open') -and ($w -match 'issues/7/comments')) 'a closed issue for the commit is reopened with a comment'

    $r = Run-Case $spikesRed @((Issue 7 'open' $other))
    Assert (@(Writes $r | Where-Object { $_ -match '-X POST repos/o/r/issues( |$)' }).Count -eq 1) 'another commit''s issue is not reused'

    foreach ($case in @(@{ c = 'cancelled'; b = 'main' }, @{ c = 'failure'; b = 'feature' })) {
        $r = Run-Case $spikesRed -conclusion $case.c -branch $case.b
        Assert ($r.exit -eq 0 -and (Writes $r).Count -eq 0 -and @($r.calls | Where-Object { $_ -match 'attempts' }).Count -eq 0) "a $($case.c) run on $($case.b) is passed over with no job read"
    }

    $r = Run-Case $spikesRed -runSha 'not-a-sha'
    Assert ($r.exit -ne 0 -and (Writes $r).Count -eq 0) 'a commit that is not 40 hex digits fails the run'
}
finally { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }

"$script:checks checks, $script:failures failed"
if ($script:failures -gt 0) { exit 1 }
