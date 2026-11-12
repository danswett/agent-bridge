#Requires -Version 7
# The Pages deploy waits for green main CI (#1313, #582). Offline: reads .github/workflows/pages.yml
# as text and fails when the deploy could run before, or without, a green CI run of the same commit.
$ErrorActionPreference = 'Stop'
$text = Get-Content -Raw (Join-Path $PSScriptRoot '..' '.github' 'workflows' 'pages.yml')
$failures = [System.Collections.Generic.List[string]]::new()
function Require([bool]$ok, [string]$what) { if (-not $ok) { $failures.Add($what) } }

$on = [regex]::Match($text, '(?ms)^on:\r?\n(.*?)^\S').Groups[1].Value
Require ($on -notmatch '(?m)^  push:') 'pages.yml still triggers on push; it must deploy only from workflow_run'
Require ($on -match '(?m)^  workflow_run:') 'pages.yml has no workflow_run trigger'
Require ($on -match '(?m)^\s+workflows: \[CI\]') 'workflow_run must watch the CI workflow'
Require ($on -match '(?m)^\s+types: \[completed\]') 'workflow_run must fire on completed'
Require ($on -match '(?m)^\s+branches: \[main\]') 'workflow_run must be limited to main'
Require ($on -match '(?ms)^  workflow_dispatch:.*?^\s+ref:') 'workflow_dispatch needs a ref input, for rollback'
Require ($on -match "cron: '\*/5 \* \* \* \*'") 'Pages needs a five-minute named-job check'
Require ($text -match '(?m)^run-name: .*Pages rollback.*Pages resume' -and $text -match 'event=workflow_dispatch&per_page=1' -and $text -match '-ManualRun \$manual -RollbackStatus') 'manual rollback must fence automatic deployment until a newer head or successful explicit resume'

$build = [regex]::Match($text, '(?ms)^  build:\r?\n(.*?)^  deploy:').Groups[1].Value
$deploy = [regex]::Match($text, '(?ms)^  deploy:\r?\n(.*)').Groups[1].Value
Require ($text -match 'quality/pages-gate\.ps1' -and $text -notmatch 'commits/main') 'the gate must skip only a commit already live or older, not one that is not main''s tip'
Require ($text -match 'quality/pages-candidate\.ps1' -and $text -match 'per_page=20' -and $text -notmatch 'workflow_run\.conclusion') 'Pages must select bounded same-SHA named-job evidence, not the whole CI conclusion'
Require ($build -match 'TARGET_SHA: \$\{\{ needs\.gate\.outputs\.sha') 'the build job must build the selected tested SHA'
Require ($build -match 'merge-base --is-ancestor \"\$TARGET_SHA\" origin/main') 'the build job must prove the commit is on main before building it'
Require ($build -notmatch '(?m)^\s+ref: ') 'the build job must not check out an event-supplied ref (CodeQL cache-poisoning)'
Require ($build -notmatch '(?m)^\s+(pages|id-token): write') 'the build job must hold no publishing token'
Require ($deploy -match '(?m)^\s+pages: write' -and $deploy -match '(?m)^\s+id-token: write') 'the deploy job must keep pages: write and id-token: write'
$at = $deploy.IndexOf('deploy-pages')
Require ($at -ge 0 -and $deploy.IndexOf('sha256', [Math]::Max($at, 0)) -gt $at) 'no sha256 check of the live files after deploy-pages'
Require ($deploy -match 'config\.js' -and $deploy -match 'sw\.js') 'the live check must cover config.js and sw.js'

if ($failures.Count) { $failures | ForEach-Object { Write-Host "FAIL: $_" }; exit 1 }
Write-Host 'pages.yml waits for green main CI and verifies the live site: ok'
