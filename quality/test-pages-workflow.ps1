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
Require ($text -match '(?m)^run-name: .*Pages rollback.*Pages resume' -and $text -match 'event=workflow_dispatch&per_page=100' -and $text -match 'quality/pages-rollback\.ps1' -and $text -match '-RollbackStatus \$rollbackStatus') 'manual rollback must fence automatic deployment until a newer head or successful explicit resume'

$gate = [regex]::Match($text, '(?ms)^  gate:\r?\n(.*?)^  build:').Groups[1].Value
$gateOutputs = [regex]::Match($gate, '(?ms)^    outputs:\r?\n(.*?)(?=^    \S)').Groups[1].Value
Require ($gateOutputs -cmatch '(?m)^      deploy: \$\{\{ steps\.decide\.outputs\.deploy \}\}\r?$' -and $gateOutputs -cmatch '(?m)^      sha: \$\{\{ steps\.decide\.outputs\.sha \}\}\r?$') 'the gate must export the deciding step deploy and SHA outputs'
$permissions = [regex]::Match($gate, '(?ms)^    permissions:\r?\n(.*?)(?=^    \S)').Groups[1].Value
$scopes = @($permissions -split '\r?\n' | ForEach-Object { ($_ -replace '\s+#.*$', '').Trim() } | Where-Object { $_ -and $_ -notmatch '^#' } | Sort-Object)
Require ($text -match '(?m)^permissions: \{\}\r?$' -and $scopes.Count -eq 3 -and ($scopes -join ',') -ceq 'actions: read,contents: read,pages: read') 'the gate must have exactly actions:read, contents:read and pages:read with no inherited or added writes'
$build = [regex]::Match($text, '(?ms)^  build:\r?\n(.*?)^  deploy:').Groups[1].Value
Require ($build -cmatch "(?m)^    if: needs\.gate\.outputs\.deploy == 'true'\r?$") 'the build must wait for the gate deploy output'
$deploy = [regex]::Match($text, '(?ms)^  deploy:\r?\n(.*)').Groups[1].Value
$guard = [regex]::Match($deploy, '(?m)^\s+run: (if \(\$env:GITHUB_EVENT_NAME[^\r\n]+)').Groups[1].Value
Require ($guard -ne '' -and $deploy.IndexOf($guard) -lt $deploy.IndexOf('configure-pages')) 'a rollback rerun after a later resume must be refused before publishing, including failed-job reruns'
foreach ($case in @(@('workflow_dispatch', '1', $false), @('workflow_dispatch', '2', $true), @('schedule', '2', $false))) {
    $env:GITHUB_EVENT_NAME = $case[0]; $env:GITHUB_RUN_ATTEMPT = $case[1]; $refused = $false
    try { & ([scriptblock]::Create($guard)) } catch { $refused = $_.Exception.Message -eq 'manual-rerun-refused-dispatch-again' }
    Require ($refused -eq $case[2]) 'the publishing guard must refuse only a repeated manual deployment'
}
Require ($text -match 'quality/pages-gate\.ps1' -and $text -notmatch 'commits/main') 'the gate must skip only a commit already live or older, not one that is not main''s tip'
Require ($text -match 'quality/pages-candidate\.ps1' -and $text -match 'per_page=20' -and $text -notmatch 'workflow_run\.conclusion') 'Pages must select bounded same-SHA named-job evidence, not the whole CI conclusion'
Require ($build -cmatch '(?m)^          TARGET_SHA: \$\{\{ needs\.gate\.outputs\.sha \|\| inputs\.ref \|\| github\.sha \}\}\r?$') 'the build must prefer the tested SHA, then the manual rollback ref, then the current SHA'
Require ($build -match 'merge-base --is-ancestor \"\$TARGET_SHA\" origin/main') 'the build job must prove the commit is on main before building it'
Require ($build -notmatch '(?m)^\s+ref: ') 'the build job must not check out an event-supplied ref (CodeQL cache-poisoning)'
Require ($build -notmatch '(?m)^\s+(pages|id-token): write') 'the build job must hold no publishing token'
Require ($deploy -match '(?m)^\s+pages: write' -and $deploy -match '(?m)^\s+id-token: write') 'the deploy job must keep pages: write and id-token: write'
$at = $deploy.IndexOf('deploy-pages')
Require ($at -ge 0 -and $deploy.IndexOf('sha256', [Math]::Max($at, 0)) -gt $at) 'no sha256 check of the live files after deploy-pages'
Require ($deploy -match 'config\.js' -and $deploy -match 'sw\.js') 'the live check must cover config.js and sw.js'

$ci = Get-Content -Raw (Join-Path $PSScriptRoot '..' '.github' 'workflows' 'ci.yml')
$ciBlock = [regex]::Match($ci, '(?ms)^      - name: The Pages deploy waits for green main CI and checks the live site\r?\n.*?^        run: \|\r?\n(.*?)(?=^      - )')
Require $ciBlock.Success 'the CI Pages test step must be executable offline'
$ciStep = Join-Path ([IO.Path]::GetTempPath()) "pages-ci-$([guid]::NewGuid().ToString('N')).ps1"
$savedExit = $global:LASTEXITCODE
function Test-PagesCi([int[]]$exits) {
    $state = @{ Index = 0; Files = @() }
    function pwsh {
        if ($args.Count -ne 3 -or $args[0] -ne '-NoProfile' -or $args[1] -ne '-File' -or $state.Index -ge 2) { throw 'unexpected CI test command' }
        $state.Files += "$($args[2])"
        $global:LASTEXITCODE = $exits[$state.Index]; $state.Index++
    }
    $global:LASTEXITCODE = 0
    & $ciStep
    $code = $LASTEXITCODE
    Require (($state.Files -join ',') -ceq 'quality/test-pages-workflow.ps1,quality/test-pages-gate.ps1') 'CI must execute both Pages suites'
    Require (($code -ne 0) -eq ($exits[0] -ne 0 -or $exits[1] -ne 0)) "CI must preserve either suite failure ($($exits -join '/'))"
    Write-Host "CI Pages suite exits $($exits -join '/') => $code"
}
try {
    Set-Content -LiteralPath $ciStep -Value ($ciBlock.Groups[1].Value -replace '(?m)^          ', '')
    foreach ($pair in @(@(0, 0), @(1, 0), @(0, 1), @(1, 1))) { Test-PagesCi $pair }
} finally {
    Remove-Item -LiteralPath $ciStep
    $global:LASTEXITCODE = $savedExit
}
if ($failures.Count) { $failures | ForEach-Object { Write-Host "FAIL: $_" }; exit 1 }
Write-Host 'pages.yml waits for green main CI and verifies the live site: ok'
