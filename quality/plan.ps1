#Requires -Version 7.4
<#
.SYNOPSIS
  CI's plan (quality/README.md §4.7): which ci.yml jobs, suites and .NET test projects this run needs.

.DESCRIPTION
  Runs the selector (tools/AgentBridge.Quality, built beforehand) and writes, to GITHUB_OUTPUT:
    <job>=true|false     for each job in -Jobs: whether a selected suite's where: names it
    suites               the selected suite ids, as a JSON array
    test-projects        the test project folders of the suites ci.yml's product job runs, as JSON
    whole-solution       true when the whole ring runs, so product tests AgentBridge.slnx as it
                         always did, rather than a filter of the selected projects
    product-os           product's runners, from its selected suites' platforms (all three when the
                         whole ring runs), as a JSON array
    test-filter          the dotnet test --filter product adds: ring 0's in-process filter under
                         -FastRing, otherwise empty
  and a "what runs and why" table to GITHUB_STEP_SUMMARY. Locally, without those variables, it
  prints both, so `pwsh quality/plan.ps1 -Ring 1 -Base HEAD~1 -Head HEAD` shows what CI would plan.

.EXAMPLE
  pwsh quality/plan.ps1 -Ring 1 -Base origin/main -Head HEAD -Jobs product,app,pwa-ui
  pwsh quality/plan.ps1 -FastRing -Base origin/main -Head HEAD -Jobs product,light   # a draft's plan
#>
[CmdletBinding()]
param(
    [ValidateRange(0, 4)]
    [int] $Ring = 1,
    # A draft pull request's run (quality/README.md §2): ring 0's suites, on Linux only, with ring
    # 0's test filter. The summary says plainly that it's partial; ready for review runs ring 1.
    [switch] $FastRing,
    [Parameter(Mandatory)]
    [string] $Base,
    [Parameter(Mandatory)]
    [string] $Head,
    # The ci.yml jobs the plan gates; each gets a true or false output.
    [Parameter(Mandatory)]
    [string[]] $Jobs,
    # A ring-1 selection to report beside a ring-2 run, as what ring 1 would have chosen: when a
    # whole-ring main run fails, it says at once whether ring 1 would have run the failing job (§4.8).
    [switch] $CompareRing1,
    # Jobs a ring-2 run gives true only when ring 1 selected them for a reason of their own, not
    # because the whole ring runs (#1249: the app's packaging legs on a pull request that only
    # touches workflows or the quality tool). Main never passes it: there they all run.
    [string[]] $Narrow = @(),
    # Platforms product doesn't run on this event (#1249: win-arm64 runs on main, not on a pull
    # request head). The app job still packages both architectures whenever it runs.
    [string[]] $ExcludePlatform = @()
)

$ErrorActionPreference = 'Stop'
# pwsh -File passes "a,b" as one string, so split it here.
$Jobs = @($Jobs -split '[,\s]+' | Where-Object { $_ })
$root = Split-Path -Parent $PSScriptRoot
$tool = Join-Path $root 'tools/AgentBridge.Quality/bin/Release/net10.0/agent-bridge-quality.dll'
if (-not (Test-Path $tool)) { throw "Build the selector first: dotnet build tools/AgentBridge.Quality -c Release" }
if ($FastRing) {
    if ($CompareRing1) { throw '-CompareRing1 is for a ring-2 run, not -FastRing.' }
    $Ring = 0
}
# The fast ring runs on Linux only. Otherwise no --platform: CI plans for every platform at once,
# and each job's runners come from that.
$platform = if ($FastRing) { 'linux-x64' } else { $null }

function Select-Suites([int] $ring) {
    $platformArgs = if ($platform) { @('--platform', $platform) } else { @() }
    $json = (dotnet $tool select --repo $root --ring $ring --base $Base --head $Head @platformArgs) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "The selector failed (exit $LASTEXITCODE)." }
    $json | ConvertFrom-Json -AsHashtable
}

function ConvertTo-JsonArray($items) { ConvertTo-Json -InputObject @($items) -Compress }

$selection = Select-Suites $Ring
$ci = $selection.jobs['ci.yml'] ?? @{}
$suites = @($selection.suites)
$ids = @($suites | ForEach-Object { $_.id })
$productIds = @($ci['product'] ?? @())
$productSuites = @($suites | Where-Object { $_.id -in $productIds })
$testProjects = @($productSuites | Where-Object { $_.project } | ForEach-Object { $_.project } | Sort-Object -Unique)
$whole = $selection.mode -in 'full', 'everything'
# GitHub's runner for each platform a product suite declares, in the order the matrix always had.
$runners = [ordered]@{ 'linux-x64' = 'ubuntu-latest'; 'win-x64' = 'windows-latest'; 'win-arm64' = 'windows-11-arm' }
$platforms = @($productSuites | ForEach-Object { $_.platforms })
# The whole ring runs as product always did: everything, on every runner. The fast ring is Linux's.
$productOs = @($runners.Keys | Where-Object { ($whole -or $_ -in $platforms) -and (-not $platform -or $_ -eq $platform) -and $_ -notin $ExcludePlatform } | ForEach-Object { $runners[$_] })
# Never an empty matrix: a suite that only the excluded platform runs falls back to every runner.
if ($productOs.Count -eq 0 -and $productIds.Count -gt 0) { $productOs = @($runners.Keys | Where-Object { -not $platform -or $_ -eq $platform } | ForEach-Object { $runners[$_] }) }

function Test-WholeRing([string] $rule) { $rule.StartsWith('global_triggers ') -or $rule.StartsWith('unclaimed path') }

# Build inputs every project compiles from: a pull request changing one still packages the app.
$appInputs = @('AgentBridge.slnx', 'nuget.config', 'global.json', '**/Directory.Build.*', '**/Directory.Packages.props')
function Test-NotOwn([string] $rule) {
    if ($rule.StartsWith('global_triggers ')) { return $rule.Substring('global_triggers '.Length) -notin $appInputs }
    $rule.StartsWith('unclaimed path')
}

$Narrow = @($Narrow -split '[,\s]+' | Where-Object { $_ })
# Ring 1's selection, for -Narrow and -CompareRing1 both.
$ring1 = $null
if ($Ring -gt 1 -and ($Narrow.Count -gt 0 -or $CompareRing1)) { $ring1 = Select-Suites 1 }
$ring1Ci = if ($ring1) { $ring1.jobs['ci.yml'] ?? @{} } else { @{} }
# ci.yml holds the app job's packaging and SBOM steps, and install-trivy feeds them: a pull request
# editing either must still package the app, which the workflows-wide global trigger can't say.
$packagingEdited = $false
if ($ring1) {
    $edited = @(git -C $root diff --name-only "$Base...$Head")
    $packagingEdited = @($edited | Where-Object { $_ -eq '.github/workflows/ci.yml' -or $_ -like '.github/actions/install-trivy/*' }).Count -gt 0
}
function Test-OwnReason([string] $job) {
    if ($packagingEdited -and $job -in 'app', 'app-installer') { return $true }
    $own = @($ring1Ci[$job] ?? @())
    @($ring1.suites | Where-Object { $_.id -in $own -and @($_.reasons | Where-Object { -not (Test-NotOwn $_.rule) }).Count -gt 0 }).Count -gt 0
}

$outputs = [ordered]@{}
foreach ($job in $Jobs) {
    $runs = if ($job -in $Narrow -and $ring1) { Test-OwnReason $job } else { $ci.ContainsKey($job) }
    $outputs[$job] = if ($runs) { 'true' } else { 'false' }
}
$outputs['suites'] = ConvertTo-JsonArray $ids
$outputs['test-projects'] = ConvertTo-JsonArray $testProjects
$outputs['whole-solution'] = if ($whole) { 'true' } else { 'false' }
$outputs['product-os'] = ConvertTo-JsonArray $productOs
$outputs['test-filter'] = "$($selection.testFilter)"
$lines = $outputs.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" }
if ($env:GITHUB_OUTPUT) { $lines | Add-Content -Path $env:GITHUB_OUTPUT -Encoding utf8 } else { $lines | Write-Output }

# The summary. Text from the catalog and file names goes into table cells, where | would split one.
function Cell([string] $text) { $text -replace '\|', '\|' -replace "`r?`n", ' ' }
# Outside code spans, a glob's * would turn into emphasis.
function Text([string] $text) { (Cell $text) -replace '\*', '\*' }
function Code([string] $text) { if ($text) { '`' + (Cell $text) + '`' } else { '' } }
function Files($files) {
    $list = @($files)
    $shown = ($list | Select-Object -First 3 | ForEach-Object { Code $_ }) -join ', '
    if ($list.Count -gt 3) { $shown += " (+$($list.Count - 3) more)" }
    $shown
}
$md = [System.Collections.Generic.List[string]]::new()
$md.Add("## What runs and why")
$md.Add("")
if ($FastRing) {
    $md.Add("> **A draft: this run is partial.** Only the fast ring runs: ring 0's suites on Linux, in-process tests only (``$($selection.testFilter)``), the catalog check, and actionlint when a workflow changes. Nothing on Windows, no browsers, no CodeQL or dependency review. Marking the pull request ready for review runs ring 1: everything it selects, on every system.")
    $md.Add("")
}
$md.Add("Ring $Ring over $($selection.files.Count) changed file(s) from $(Text $selection.source). Mode: **$($selection.mode)**.")
$wholeRing = $suites.reasons | Where-Object { Test-WholeRing $_.rule } | Group-Object rule
foreach ($g in $wholeRing) {
    $md.Add("The whole ring runs, by $(Code $g.Name): $(Files ($g.Group.files | Sort-Object -Unique)).")
}
if ($selection.mode -eq 'docs-only') {
    $md.Add("Every file is in ``no_test_paths``, so only suites whose triggers name them run, and no sentinels.")
}
$md.Add("")
$md.Add("| ci.yml job | Runs | Suites |")
$md.Add("|---|---|---|")
foreach ($job in $Jobs) {
    $runs = if ($outputs[$job] -eq 'true') { 'yes' } else { 'skipped' }
    $md.Add("| $job | $runs | $((@($ci[$job] ?? @()) | ForEach-Object { Code $_ }) -join ', ') |")
}
$md.Add("")
if ($productIds.Count -gt 0) {
    $projects = if ($whole) { 'all of AgentBridge.slnx' } else { (@($testProjects | ForEach-Object { Code $_ }) -join ', ') }
    $md.Add("product tests $(if ($projects) { $projects } else { 'no .NET test project' }), on $((@($productOs) -join ', ')).")
    $md.Add("")
}
$md.Add("| Suite | Status | Runs in | Why |")
$md.Add("|---|---|---|---|")
foreach ($s in $suites) {
    $whys = @($s.reasons | ForEach-Object {
        if (Test-WholeRing $_.rule) { 'the whole ring (above)' }
        elseif (@($_.files).Count -gt 0) { "$(Text $_.rule): $(Files $_.files)" }
        else { Text $_.rule }
    } | Select-Object -Unique)
    $where = if ($s.where) { Text $s.where } elseif ($s.status -eq 'planned') { 'not built yet' } else { '' }
    $md.Add("| $(Code $s.id) | $($s.status) | $where | $($whys -join '; ') |")
}
if ($selection.files.Count -gt 0) {
    $md.Add("")
    $md.Add("<details><summary>$($selection.files.Count) changed file(s)</summary>")
    $md.Add("")
    foreach ($f in $selection.files) { $md.Add("- $(Code $f)") }
    $md.Add("")
    $md.Add("</details>")
}
if ($CompareRing1) {
    $pr = $ring1
    $prJobs = @(($pr.jobs['ci.yml'] ?? @{}).Keys | Where-Object { $_ -in $Jobs } | Sort-Object)
    $md.Add("")
    $md.Add("Ring 1 over the same change (what its pull request ran, mode $($pr.mode)): $(if ($prJobs) { ($prJobs | ForEach-Object { Code $_ }) -join ', ' } else { 'none of the gated jobs' }).")
}
if ($env:GITHUB_STEP_SUMMARY) { $md | Add-Content -Path $env:GITHUB_STEP_SUMMARY -Encoding utf8 } else { $md | Write-Output }
