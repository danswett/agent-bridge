#Requires -Version 7
# Offline only: every GitHub request must match this stand-in.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'main-red.ps1')
$script:GhCommand = 'Fake-Gh'
# A fake catalog: header comments and non-post-merge suites' tier_jobs must not count.
$tierPath = [IO.Path]::GetTempFileName()
Set-Content -LiteralPath $tierPath -Value @'
#   tier_jobs: [product]
suites:
  - id: pwa-ui
    where: ci.yml pwa-ui
    tier: post-merge  # WebKit after merge
    tier_jobs: ['pwa-ui (webkit 1/3)', pwa-e2e, "probe, light"]
  - id: product
    tier_jobs: [product]
  - id: lab
    tier: blocking
    tier_jobs: [spikes]
'@
$script:CatalogPath = $tierPath
$script:failures = 0
$script:checks = 0
function Assert([bool]$Ok, [string]$Name) {
    $script:checks++
    if ($Ok) { "ok   $Name" } else { "FAIL $Name"; $script:failures++ }
}
function Reset-Scenario {
    $script:s = @{ conclusion = 'failure'; attempt = 1; newest = 100; status = 'completed'; event = 'push'
        branch = 'main'; repo = 'o/r'; path = '.github/workflows/ci.yml'; open = $false
        log = '  Failed A.B.C.Method [1 s]'; unreadable = $false; jobName = 'product'; jobConclusion = 'failure'
        body = ''; comments = @(); writes = @(); calls = @(); foreign = $false; extraJobs = @(); title = ''
        attempts = @{}; listedAttempt = 0; previousJobName = 'product' }
}
function Fake-Run($Id, $Attempt, $Status, $Conclusion) {
    @{ id = $Id; run_number = $Id; conclusion = $Conclusion; status = $Status; run_attempt = $Attempt
        head_sha = 'abcdef0123456789'; head_branch = $s.branch; event = $s.event; path = $s.path
        head_repository = @{ full_name = $s.repo } }
}
function Fake-Gh {
    $a = $args -join ' '
    $s.calls += ,@($args)
    if ($args[0] -eq 'issue' -and $args[1] -in @('list', 'view')) {
        $fields = if ($args[1] -eq 'list') { @('number,body,author', 'number,body,author,title') } else { @('comments') }
        $json = [array]::IndexOf($args, '--json')
        if ($json -lt 0 -or $args.Count -ne $json + 2 -or $args[$json + 1] -isnot [string] -or
            $args[$json + 1] -cnotin $fields) {
            throw "Expected --json '$fields' as one final string argument"
        }
    }
    switch -Regex ($a) {
        '^api repos/o/r/actions/runs/100$' {
            Fake-Run 100 $s.attempt $s.status $s.conclusion | ConvertTo-Json; return
        }
        '^api repos/o/r/actions/runs/100/attempts/(\d+)$' {
            $attempt = [int]$Matches[1]
            if ($s.attempts.ContainsKey($attempt)) { $s.attempts[$attempt] | ConvertTo-Json; return }
            if ($attempt -ge $s.attempt) { throw 'Unexpected future attempt read' }
            Fake-Run 100 $attempt 'completed' 'failure' | ConvertTo-Json; return
        }
        '^api repos/o/r/actions/workflows/ci.yml/runs\?' {
            if ($a -notmatch 'status=completed' -or $a -notmatch 'per_page=1') { throw 'Require one newest finished run' }
            $attempt = if ($s.listedAttempt) { $s.listedAttempt } else { $s.attempt }
            $runs = @(if ($s.newest) { Fake-Run $s.newest $attempt 'completed' $s.conclusion })
            @{ workflow_runs = $runs } | ConvertTo-Json -Depth 5; return
        }
        '^api repos/o/r/actions/runs/100/attempts/(\d+)/jobs\?per_page=100 --paginate --slurp$' {
            $name = if ([int]$Matches[1] -lt $s.attempt) { $s.previousJobName } else { $s.jobName }
            @{ jobs = @(@{ id = 7; name = $name; conclusion = $s.jobConclusion }) + $s.extraJobs } | ConvertTo-Json; return
        }
        '^api repos/o/r/actions/runs/100/jobs' {
            @{ jobs = @(@{ id = 7; name = $s.jobName; conclusion = $s.jobConclusion }) + $s.extraJobs } | ConvertTo-Json; return
        }
        '^api repos/o/r/actions/jobs/\d+/logs --allow-escape-sequences$' {
            if ($s.unreadable) { $global:LASTEXITCODE = 1; return }
            $s.log; return
        }
        '^issue list ' {
            $found = @()
            if ($s.foreign) { $found += @{ number = 5; body = ''; author = @{ login = 'stranger' } } }
            if ($s.open) { $found += @{ number = 9; body = $s.body; title = $s.title; author = @{ login = 'app/github-actions' } } }
            ConvertTo-Json -InputObject $found -Depth 5; return
        }
        '^issue view 9 ' { @{ comments = $s.comments } | ConvertTo-Json -Depth 5; return }
        '^label create main-red ' { $s.writes += 'label'; return }
        '^issue create ' {
            $s.writes += 'create'; $s.open = $true; $s.body = $args[([array]::IndexOf($args, '--body') + 1)]
            $s.title = $args[([array]::IndexOf($args, '--title') + 1)]; return
        }
        '^issue comment 9 ' {
            $s.writes += 'comment'
            $s.comments += @{ body = $args[([array]::IndexOf($args, '--body') + 1)]; author = @{ login = 'github-actions' } }
            return
        }
        '^issue edit 9 ' {
            $s.writes += 'edit'; $s.title = $args[([array]::IndexOf($args, '--title') + 1)]; return
        }
        '^issue close 9 ' { $s.writes += 'close'; $s.open = $false; return }
        default { throw "Unexpected fake gh request: $a" }
    }
}
function Detect { Invoke-MainRed -RunId 100 -Repository 'o/r' | Out-Null }
foreach ($fields in @(@('number', 'body', 'author'), @('number body author'))) {
    Reset-Scenario; $rejected = $false
    try { Fake-Gh issue list --repo o/r --state open --label main-red --limit 100 --json @fields | Out-Null }
    catch {
        if ($_.Exception.Message -notlike 'Expected --json *') { throw }
        $rejected = $true
    }
    Assert $rejected "fake refuses malformed JSON fields: $($fields -join ' ')"
}
Reset-Scenario; $lookupError = $null
try { Get-MainRedIssue 'o/r' | Out-Null } catch { $lookupError = $_.Exception.Message }
Assert ($null -eq $lookupError) "issue lookup sends one comma-joined JSON field argument ($lookupError)"
Reset-Scenario; Detect
Assert (($s.writes -join ',') -eq 'label,create') 'red creates one labelled issue'
Assert ($s.body -match 'product' -and $s.body -match 'A.B.C.Method' -and $s.body -match 'https://github.com/o/r/actions/runs/100') 'issue names failed jobs, tests and run'
Assert (-not (($s.calls | ForEach-Object { $_ -join ' ' }) -join "`n").Contains('--assignee') -and -not $s.body.Contains('@')) 'no assignee or mention'
Detect
Assert (($s.writes -join ',') -eq 'label,create') 'duplicate creation event makes no changes'
Reset-Scenario; $s.open = $true; $s.body = 'previous sha'; $s.attempt = 2; Detect
Assert (($s.writes -join ',') -eq 'comment' -and $s.comments[0].body -match 'attempt=2') 'another attempt updates the existing issue'
Detect
Assert (($s.writes -join ',') -eq 'comment') 'duplicate comment event makes no changes'
Reset-Scenario; $s.open = $true; $s.conclusion = 'success'; $s.attempt = 2; Detect
Assert (($s.writes -join ',') -eq 'comment,close' -and $s.comments[0].body -eq 'main-red: green again, run=100 attempt=2, sha=abcdef01') 'green attempt two comments before closing'
Reset-Scenario; $s.conclusion = 'success'; Detect
Assert ($s.writes.Count -eq 0) 'green without an issue does nothing'
foreach ($c in 'cancelled', 'skipped') {
    Reset-Scenario; $s.open = $true; $s.conclusion = $c; Detect
    Assert ($s.writes.Count -eq 0) "$c neither opens nor closes"
}
foreach ($c in 'startup_failure', 'timed_out') {
    Reset-Scenario; $s.conclusion = $c; $s.jobConclusion = 'timed_out'; Detect
    Assert ($s.writes -contains 'create' -and $s.body -match 'product') "$c is red"
}
foreach ($c in 'failure', 'success') {
    Reset-Scenario; $s.open = $true; $s.newest = 101; $s.conclusion = $c; Detect
    Assert ($s.writes.Count -eq 0) "stale $c event does nothing"
}
Reset-Scenario; $s.log = 'Build FAILED: error CS1002'; Detect
Assert ($s.body -match 'product' -and $s.body -match 'no test name was found') 'build failure still lists its job'
Reset-Scenario; $s.jobName = "@someone `````n# injected"
$s.log = "  1) [webkit] $([char]0x203a) @someone``````.spec.mjs:43:1 $([char]0x203a) title"
Detect
Assert (-not $s.body.Contains('@') -and $s.body.Contains('[at]someone') -and ([regex]::Matches($s.body, '(?m)^```$')).Count -eq 2) 'mentions and backticks cannot escape the test code block'
$huge = [pscustomobject]@{ id = 100; run_attempt = 1; head_sha = 'abcdef0123456789' }
$body = Format-MainRedBody $huge 'o/r' @([pscustomobject]@{ Name = 'x' * 20000; Tests = @('A.B.' + ('x' * 20000)) * 100; LogReadable = $true })
Assert ($body.Length -le 12000 -and $body.EndsWith('```')) 'long evidence is capped without breaking its fence'
Reset-Scenario; $s.unreadable = $true; Detect
Assert ($s.writes -contains 'create' -and $s.body -match 'log was unreadable' -and $LASTEXITCODE -eq 0) 'unreadable log still opens an issue successfully'
Reset-Scenario; $s.foreign = $true; Detect
Assert ($s.writes -contains 'create') 'a foreign authored label does not become the guardian issue'
foreach ($over in @(@{ event = 'pull_request' }, @{ branch = 'topic' }, @{ repo = 'fork/r' }, @{ path = 'other.yml' }, @{ status = 'in_progress' })) {
    Reset-Scenario; foreach ($k in $over.Keys) { $s[$k] = $over[$k] }; Detect
    Assert ($s.writes.Count -eq 0) 'only finished same-repository CI pushes on main can act'
}
# Tiered gating B3a (#3210): a post-merge tier failure alerts, labelled, and never feeds a PR verdict.
Reset-Scenario; $s.conclusion = 'success'; $s.jobConclusion = 'success'; $s.jobName = 'pwa-ui (webkit 1/3)'; Detect
Assert ($s.writes.Count -eq 0) 'a passing post-merge tier job raises no alert'
Reset-Scenario; $s.jobName = 'pwa-ui (webkit 1/3)'; Detect
Assert (($s.writes -join ',') -eq 'label,create' -and $s.title -match 'post-merge tier only' -and
    $s.body -match '(?m)^Job \(post-merge-tier\): pwa-ui \(webkit 1/3\)$') 'one post-merge tier flake writes a main-red entry labelled post-merge-tier'
Assert ($s.body -match 'post-merge tier only' -and $s.body -match 'merge verdict' -and
    -not (($s.calls | ForEach-Object { $_ -join ' ' }) -match 'statuses|check-runs|--add-label|fixes-main')) 'that entry says it never feeds a pull request merge verdict and sets no status or label'
$s.attempt = 2; Detect
Assert (($s.writes -join ',') -eq 'label,create,comment' -and $s.open -and
    $s.comments[0].body -match '(?m)^Job \(post-merge-tier\): pwa-ui') 'a second post-merge tier flake adds another labelled entry and leaves the issue open'
$s.attempt = 3; $s.jobName = 'product'; Detect
Assert (($s.writes -join ',') -eq 'label,create,comment,edit,comment' -and
    $s.title -match '^Main red: CI failing' -and $s.comments[-1].body -match '(?m)^Job: product$') 'a later blocking failure promotes an existing post-merge-only issue title before commenting'
Detect
Assert (($s.writes -join ',') -eq 'label,create,comment,edit,comment') 'a duplicate blocking event neither re-edits the title nor repeats evidence'
Reset-Scenario; $s.extraJobs = @(@{ id = 8; name = 'pwa-e2e'; conclusion = 'failure' }); Detect
Assert ($s.title -match '^Main red: CI failing' -and $s.body -match '(?m)^Job: product$' -and
    $s.body -match '(?m)^Job \(post-merge-tier\): pwa-e2e$' -and $s.body -notmatch 'post-merge tier only') 'a blocking failure beside a post-merge one stays an ordinary main-red entry'
Reset-Scenario; $s.jobName = 'pwa-e2e'; $s.extraJobs = @(@{ id = 8; name = 'quality-gate'; conclusion = 'failure' }); Detect
Assert ($s.title -match 'post-merge tier only' -and $s.body -match '(?m)^Job: quality-gate$') 'the quality-gate aggregate echoing a post-merge failure keeps it post-merge only'
Reset-Scenario; $s.jobName = 'light'; Detect
Assert ($s.body -match '(?m)^Job: light$' -and $s.body -notmatch 'post-merge tier only') 'a comma inside a quoted tier job name invents no post-merge job'
Reset-Scenario; $s.jobName = 'pwa-ui (webkit 1/3)'; $script:CatalogPath = "$tierPath.missing"; Detect; $script:CatalogPath = $tierPath
Assert ($s.title -match '^Main red: CI failing' -and $s.body -match '(?m)^Job: pwa-ui') 'without catalog tier data every failed job stays blocking'
Reset-Scenario; $s.jobName = 'pwa-ui (webkit 1/3)'; $catalogError = $null
$lock = [IO.File]::Open($tierPath, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
try { Detect } catch [IO.IOException] { $catalogError = $_.Exception } finally { $lock.Dispose() }
Assert ($null -eq $catalogError -and $s.writes -contains 'create' -and
    $s.title -match '^Main red: CI failing' -and $s.body -match '(?m)^Job: pwa-ui') 'a locked catalog still writes an issue with every failure blocking'
foreach ($status in 'queued', 'in_progress') {
    Reset-Scenario; $s.attempt = 2; $s.status = $status; $s.conclusion = $null; $s.newest = 99
    $s.jobName = 'running rerun job'; Detect
    Assert ($s.writes -contains 'create' -and $s.body -match 'run=100 attempt=1' -and
        $s.body -match '(?m)^Job: product$') "an attempt-two $status rerun reports the last completed red attempt immediately"
    Assert (@($s.calls | Where-Object { ($_ -join ' ') -match '/attempts/1/jobs\?' }).Count -eq 1) 'red evidence is pinned to completed attempt one, not the live rerun'
    Detect
    Assert (($s.writes -join ',') -eq 'label,create') 'duplicate rerun event does not repeat the completed red attempt'
}
Reset-Scenario; $s.attempt = 2; $s.status = 'in_progress'; $s.conclusion = $null; $s.newest = 0; Detect
Assert ($s.writes -contains 'create' -and $s.body -match 'attempt=1') 'a rerun hiding the only completed run still reports its completed red attempt'
Reset-Scenario; $s.attempt = 3; $s.status = 'queued'; $s.conclusion = $null; $s.newest = 99
$s.attempts[2] = Fake-Run 100 2 'completed' 'failure'; Detect
Assert ($s.body -match 'attempt=2') 'the most recent completed rerun is selected, not always attempt one'
Reset-Scenario; $s.attempt = 2; $s.status = 'in_progress'; $s.conclusion = $null; $s.newest = 99; $s.open = $true
$s.attempts[1] = Fake-Run 100 1 'completed' 'success'; Detect
Assert (($s.writes -join ',') -eq 'comment,close' -and $s.comments[0].body -match 'attempt=1') 'an incomplete rerun preserves the last completed green result'
Reset-Scenario; $s.listedAttempt = 2; $s.open = $true
$s.attempts[2] = Fake-Run 100 2 'completed' 'success'; Detect
Assert (($s.writes -join ',') -eq 'comment,close' -and $s.comments[0].body -match 'attempt=2') 'a newer completed attempt discovered during detection supersedes the initial red snapshot'
$names = @((Get-PostMergeJobs $tierPath) | Sort-Object)
$expected = @('pwa-ui (webkit 1/3)', 'pwa-e2e', 'probe, light') | Sort-Object
Assert (($names -join '|') -eq ($expected -join '|')) "only post-merge suites' tier_jobs are post-merge ($($names -join '|'))"
Remove-Item -LiteralPath $tierPath
if ($script:failures) { "$script:failures of $script:checks checks failed"; exit 1 }
"main-red: $script:checks checks passed"
