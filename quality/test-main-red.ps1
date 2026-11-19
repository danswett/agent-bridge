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
        body = ''; comments = @(); writes = @(); calls = @(); foreign = $false; extraJobs = @(); title = '' }
}
function Fake-Gh {
    $a = $args -join ' '
    $s.calls += ,@($args)
    if ($args[0] -eq 'issue' -and $args[1] -in @('list', 'view')) {
        $fields = if ($args[1] -eq 'list') { 'number,body,author' } else { 'comments' }
        $json = [array]::IndexOf($args, '--json')
        if ($json -lt 0 -or $args.Count -ne $json + 2 -or $args[$json + 1] -isnot [string] -or
            $args[$json + 1] -cne $fields) {
            throw "Expected --json '$fields' as one final string argument"
        }
    }
    switch -Regex ($a) {
        '^api repos/o/r/actions/runs/100$' {
            @{ id = 100; conclusion = $s.conclusion; status = $s.status; run_attempt = $s.attempt
                head_sha = 'abcdef0123456789'; head_branch = $s.branch; event = $s.event; path = $s.path
                head_repository = @{ full_name = $s.repo } } | ConvertTo-Json; return
        }
        '^api repos/o/r/actions/workflows/ci.yml/runs\?' {
            if ($a -notmatch 'status=completed' -or $a -notmatch 'per_page=1') { throw 'Require one newest finished run' }
            @{ workflow_runs = @(@{ id = $s.newest; run_attempt = $s.attempt }) } | ConvertTo-Json; return
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
            if ($s.open) { $found += @{ number = 9; body = $s.body; author = @{ login = 'app/github-actions' } } }
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
Reset-Scenario; $s.extraJobs = @(@{ id = 8; name = 'pwa-e2e'; conclusion = 'failure' }); Detect
Assert ($s.title -match '^Main red: CI failing' -and $s.body -match '(?m)^Job: product$' -and
    $s.body -match '(?m)^Job \(post-merge-tier\): pwa-e2e$' -and $s.body -notmatch 'post-merge tier only') 'a blocking failure beside a post-merge one stays an ordinary main-red entry'
Reset-Scenario; $s.jobName = 'pwa-e2e'; $s.extraJobs = @(@{ id = 8; name = 'quality-gate'; conclusion = 'failure' }); Detect
Assert ($s.title -match 'post-merge tier only' -and $s.body -match '(?m)^Job: quality-gate$') 'the quality-gate aggregate echoing a post-merge failure keeps it post-merge only'
Reset-Scenario; $s.jobName = 'light'; Detect
Assert ($s.body -match '(?m)^Job: light$' -and $s.body -notmatch 'post-merge tier only') 'a comma inside a quoted tier job name invents no post-merge job'
Reset-Scenario; $s.jobName = 'pwa-ui (webkit 1/3)'; $script:CatalogPath = "$tierPath.missing"; Detect; $script:CatalogPath = $tierPath
Assert ($s.title -match '^Main red: CI failing' -and $s.body -match '(?m)^Job: pwa-ui') 'without catalog tier data every failed job stays blocking'
$names = @((Get-PostMergeJobs $tierPath) | Sort-Object)
$expected = @('pwa-ui (webkit 1/3)', 'pwa-e2e', 'probe, light') | Sort-Object
Assert (($names -join '|') -eq ($expected -join '|')) "only post-merge suites' tier_jobs are post-merge ($($names -join '|'))"
# Tiered gating B3b (#3210): the same post-merge test failing twice in a row proposes one quarantine.
$genDir = Join-Path ([IO.Path]::GetTempPath()) "main-red-gen-$PID"
New-Item -ItemType Directory -Path $genDir -Force | Out-Null
$genLog = Join-Path $genDir 'calls.txt'
Set-Content -LiteralPath (Join-Path $genDir 'quarantine-pr.ps1') -Value @'
param([long]$RunId, [string]$TestName, [string]$Repository, [double]$Hours)
Add-Content -LiteralPath (Join-Path $PSScriptRoot 'calls.txt') -Value "$RunId|$TestName|$Repository|$Hours"
'@
Set-Content -LiteralPath (Join-Path $genDir 'refusing.ps1') -Value "throw 'MAIN_RED_QUARANTINE is not on'"
function Reset-Quarantine([string]$Script = 'quarantine-pr.ps1', [string]$Switch = 'on') {
    Reset-Scenario; $s.jobName = 'pwa-e2e'; Remove-Item -LiteralPath $genLog -ErrorAction SilentlyContinue
    $script:QuarantineScript = Join-Path $genDir $Script; $env:MAIN_RED_QUARANTINE = $Switch
}
function Get-Requests { @((@($s.body) + @($s.comments | ForEach-Object { $_.body })) -split '\r?\n' | Where-Object { $_ -clike 'quarantine-request: *' }) }
function Get-GeneratorCalls { if (Test-Path -LiteralPath $genLog) { @(Get-Content -LiteralPath $genLog) } else { @() } }
Reset-Quarantine; Detect
Assert (@(Get-Requests).Count -eq 0 -and @(Get-GeneratorCalls).Count -eq 0) 'one post-merge tier flake proposes no quarantine'
$s.attempt = 2; Detect
Assert ((@(Get-Requests) -join '|') -ceq 'quarantine-request: test=A.B.C.Method' -and $s.comments[0].body -match 'quarantine-pr\.ps1 -RunId 100 -TestName A\.B\.C\.Method -Repository o/r -Hours 24') 'two post-merge tier flakes of one test propose exactly one quarantine request'
Assert ((@(Get-GeneratorCalls) -join '|') -ceq '100|A.B.C.Method|o/r|24') 'that request goes once through the quarantine generator with a 24 hour expiry'
Assert (-not (($s.calls | ForEach-Object { $_ -join ' ' }) -match 'statuses|check-runs|--add-label|pr create')) 'the request writes no status, check, label or pull request itself, so no merge verdict reads it'
Detect; $s.attempt = 3; Detect
Assert (@(Get-Requests).Count -eq 1 -and @(Get-GeneratorCalls).Count -eq 1 -and $s.comments.Count -eq 2) 'a second tick and a third failure make no duplicate request'
Reset-Quarantine -Switch ''; Detect; $s.attempt = 2; Detect
Assert (@(Get-Requests).Count -eq 1 -and @(Get-GeneratorCalls).Count -eq 0) 'with MAIN_RED_QUARANTINE unset the request is recorded but the generator never runs'
Reset-Quarantine -Script 'absent.ps1'; Detect; $s.attempt = 2; Detect
Assert (@(Get-Requests).Count -eq 1 -and $LASTEXITCODE -eq 0) 'without the generator script the request is still recorded and detection succeeds'
Reset-Quarantine -Script 'refusing.ps1'; Detect; $s.attempt = 2; $refused = $null
try { Detect } catch { $refused = $_.Exception.Message }
Assert ($null -eq $refused -and $s.comments.Count -eq 1 -and $LASTEXITCODE -eq 0) "a generator refusal never fails detection ($refused)"
Reset-Quarantine; $s.jobName = 'product'; Detect; $s.attempt = 2; Detect
Assert (@(Get-Requests).Count -eq 0 -and @(Get-GeneratorCalls).Count -eq 0) 'a blocking job failing twice proposes no quarantine'
Reset-Quarantine; Detect; $s.attempt = 2; $s.log = '  Failed A.B.C.Other [1 s]'; Detect
Assert (@(Get-Requests).Count -eq 0) 'two different post-merge tests in a row propose no quarantine'
Reset-Quarantine; $s.log = "  Failed A.B.C.Method [1 s]`n  Failed A.B.C.Other [1 s]"; Detect; $s.attempt = 2; Detect
Assert (@(Get-Requests).Count -eq 0) 'an entry naming more than one test proposes no quarantine'
Remove-Item -LiteralPath $genDir -Recurse; $env:MAIN_RED_QUARANTINE = $null
Remove-Item -LiteralPath $tierPath
if ($script:failures) { "$script:failures of $script:checks checks failed"; exit 1 }
"main-red: $script:checks checks passed"
