<#
    Carries out the flake hunt's issue plan (quality/README.md §5), which
    `agent-bridge-quality flakes --plan` wrote: opens an issue for each new flaky or always-failing
    test, and comments the new counts on one already open, adding the `flaky` label to an issue
    filed by hand before the hunt existed. Creates the `flaky` label if the repository lacks it.

    The plan is read as data only: issue numbers must be numbers, labels must be ones the hunt
    uses, and every text goes to gh as a file or a single argument, never through a shell.

    -DryRun prints what would be done instead of doing it, for a run from a branch.
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Plan,
    [Parameter(Mandatory)][string]$Repository,
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$allowedLabels = @('flaky', 'bug', 'area:testing')
$work = Join-Path ([IO.Path]::GetTempPath()) "flake-hunt-$PID"
New-Item -ItemType Directory -Force -Path $work | Out-Null

function Invoke-Gh {
    param([string[]]$Arguments)
    if ($DryRun) { Write-Host "would run: gh $($Arguments -join ' ')"; return '' }
    $out = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "gh $($Arguments[0]) $($Arguments[1]) failed ($LASTEXITCODE)" }
    $out
}

$actions = @(Get-Content -LiteralPath $Plan -Raw | ConvertFrom-Json)
if ($actions.Count -eq 0) { Write-Host 'Nothing to file.'; exit 0 }

$labels = @(& gh label list --repo $Repository --limit 500 --json name | ConvertFrom-Json | ForEach-Object name)
if ($LASTEXITCODE -ne 0) { throw 'gh label list failed' }
if ($labels -notcontains 'flaky') {
    Invoke-Gh @('label', 'create', 'flaky', '--repo', $Repository, '--color', 'fbca04',
        '--description', 'Passed and failed on the same code; the flake record (quality/README.md §5)') | Out-Null
    Write-Host "$(if ($DryRun) { 'Would create' } else { 'Created' }) the flaky label."
}

$i = 0
foreach ($action in $actions) {
    $i++
    foreach ($label in @($action.labels)) {
        if ($allowedLabels -cnotcontains $label) { throw "plan item $i asks for label '$label', which the hunt never uses" }
    }
    $bodyFile = Join-Path $work "body-$i.md"
    Set-Content -LiteralPath $bodyFile -Value ([string]$action.body)
    switch ($action.action) {
        'create' {
            $title = [string]$action.title
            if ($title.Length -eq 0 -or $title.Length -gt 200) { throw "plan item $i has no usable title" }
            $ghArgs = @('issue', 'create', '--repo', $Repository, '--title', $title, '--body-file', $bodyFile)
            foreach ($label in @($action.labels)) { $ghArgs += @('--label', $label) }
            $url = Invoke-Gh $ghArgs
            Write-Host "$(if ($DryRun) { "Would open: $title" } else { "Opened $url" })"
        }
        'comment' {
            $number = [string]$action.issue
            if ($number -cnotmatch '^[0-9]+$') { throw "plan item $i names issue '$number', not a number" }
            Invoke-Gh @('issue', 'comment', $number, '--repo', $Repository, '--body-file', $bodyFile) | Out-Null
            $add = @($action.labels)
            if ($add.Count -gt 0) {
                Invoke-Gh @('issue', 'edit', $number, '--repo', $Repository, '--add-label', ($add -join ',')) | Out-Null
            }
            Write-Host "$(if ($DryRun) { 'Would comment' } else { 'Commented' }) on #$number$(if ($add.Count -gt 0) { ", adding $($add -join ', ')" })."
        }
        default { throw "plan item $i has action '$($action.action)'" }
    }
}
Remove-Item -LiteralPath $work -Recurse -Force
