<#
.SYNOPSIS
  Writes CI's test report as Markdown: line and branch coverage per product project, each test
  project's count and time, and the slowest tests (quality/README.md §5).

.DESCRIPTION
  Reads ReportGenerator's JsonSummary and the TRX files dotnet test wrote. Either may be missing,
  since a failed or hung run is when the timings matter most; the report says so instead of
  failing. It only reports: there are no thresholds yet.

.EXAMPLE
  pwsh build/test-report.ps1 -CoverageSummary TestResults/coverage-report/Summary.json -TestResults TestResults -Title 'windows-latest' >> $env:GITHUB_STEP_SUMMARY
#>
param(
    [string] $CoverageSummary,
    [Parameter(Mandatory)] [string] $TestResults,
    [string] $Title = '.NET tests',
    [int] $Slowest = 10
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# Coverage is reported per assembly, and several products rename theirs (agent-bridge-host is
# src/AgentBridge.Host), so name each row by the project a reader would look for.
$projectOf = @{}
foreach ($csproj in Get-ChildItem (Join-Path $root 'src'), (Join-Path $root 'tools') -Filter *.csproj -Recurse -ErrorAction SilentlyContinue) {
    $assembly = ([xml](Get-Content -LiteralPath $csproj.FullName -Raw)).Project.PropertyGroup.AssemblyName | Where-Object { $_ } | Select-Object -First 1
    $projectOf[$(if ($assembly) { $assembly } else { $csproj.BaseName })] = $csproj.BaseName
}

# TRX files name a test project only by its assembly path, which VSTest lowercases.
$testProjects = @{}
foreach ($csproj in Get-ChildItem (Join-Path $root 'tests') -Filter *.csproj -Recurse -ErrorAction SilentlyContinue) {
    $testProjects[$csproj.BaseName] = $csproj.BaseName
}

function Format-Seconds([double] $seconds) { $seconds.ToString('F1', [cultureinfo]::InvariantCulture) + ' s' }
function Escape-Cell([string] $text) { $text.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ') }

"## $Title"
''
'### Coverage'
''
$summary = $null
if ($CoverageSummary -and (Test-Path -LiteralPath $CoverageSummary)) {
    try { $summary = Get-Content -LiteralPath $CoverageSummary -Raw | ConvertFrom-Json }
    catch { "_The coverage summary could not be read: $($_.Exception.Message)_" }
}
if ($summary) {
    '| Project | Assembly | Line % | Branch % | Lines covered |'
    '|---|---|---:|---:|---:|'
    foreach ($a in $summary.coverage.assemblies | Sort-Object { $projectOf[$_.name] ?? $_.name }) {
        $project = $projectOf[$a.name] ?? $a.name
        $branch = if ($null -ne $a.branchcoverage) { $a.branchcoverage } else { 'n/a' }
        "| $project | $($a.name) | $($a.coverage) | $branch | $($a.coveredlines) / $($a.coverablelines) |"
    }
    $s = $summary.summary
    $branch = if ($null -ne $s.branchcoverage) { $s.branchcoverage } else { 'n/a' }
    "| **All** | | **$($s.linecoverage)** | **$branch** | $($s.coveredlines) / $($s.coverablelines) |"
} elseif (-not ($CoverageSummary -and (Test-Path -LiteralPath $CoverageSummary))) {
    '_No coverage summary: the tests did not get far enough to write one._'
}

$suites = @()
$tests = @()
$unreadable = @()
foreach ($trx in Get-ChildItem $TestResults -Filter *.trx -Recurse -ErrorAction SilentlyContinue) {
    # A test host killed mid-write can leave a TRX that does not parse; the others still report.
    try {
        $run = ([xml](Get-Content -LiteralPath $trx.FullName -Raw)).TestRun
        $storage = @($run.TestDefinitions.UnitTest)[0].storage
        $suite = if ($storage) { [IO.Path]::GetFileNameWithoutExtension($storage) } else { $trx.BaseName }
        $suite = $testProjects[$suite] ?? $suite
        $counters = $run.ResultSummary.Counters
        # VSTest writes a skipped test as a result with outcome NotExecuted but leaves the
        # notExecuted counter at 0, so skips are counted from the results.
        $results = @($run.Results.UnitTestResult)
        $suiteRow = [pscustomobject]@{
            Suite   = $suite
            Total   = [int]$counters.total
            Passed  = [int]$counters.passed
            Failed  = [int]$counters.failed
            Skipped = @($results | Where-Object outcome -eq 'NotExecuted').Count
            Start   = [datetimeoffset]$run.Times.start
            Finish  = [datetimeoffset]$run.Times.finish
            Seconds = ([datetimeoffset]$run.Times.finish - [datetimeoffset]$run.Times.start).TotalSeconds
        }
        $testRows = foreach ($r in $results) {
            if ($r.duration) {
                [pscustomobject]@{
                    Suite   = $suite
                    Name    = $r.testName
                    Outcome = $r.outcome
                    Seconds = [timespan]::Parse($r.duration, [cultureinfo]::InvariantCulture).TotalSeconds
                }
            }
        }
        $suites += $suiteRow
        $tests += @($testRows)
    } catch {
        $unreadable += "$($trx.Name): $($_.Exception.Message)"
    }
}

''
'### Test projects'
''
if ($suites) {
    '| Test project | Tests | Passed | Failed | Skipped | Time |'
    '|---|---:|---:|---:|---:|---:|'
    foreach ($s in $suites | Sort-Object Seconds -Descending) {
        "| $($s.Suite) | $($s.Total) | $($s.Passed) | $($s.Failed) | $($s.Skipped) | $(Format-Seconds $s.Seconds) |"
    }
    # The projects run in parallel, so the All row's time is the span from the first start
    # to the last finish, not the sum.
    $sum = { param($p) ($suites | Measure-Object $p -Sum).Sum }
    $span = (($suites.Finish | Measure-Object -Maximum).Maximum - ($suites.Start | Measure-Object -Minimum).Minimum).TotalSeconds
    "| **All** | **$(& $sum Total)** | **$(& $sum Passed)** | **$(& $sum Failed)** | **$(& $sum Skipped)** | **$(Format-Seconds $span)** wall |"
} else {
    '_No TRX files: the tests did not get far enough to write them._'
}
foreach ($u in $unreadable) { "_Not read: $(Escape-Cell $u)_" }

''
"### The $Slowest slowest tests"
''
if ($tests) {
    '| Time | Outcome | Test project | Test |'
    '|---:|---|---|---|'
    foreach ($t in $tests | Sort-Object Seconds -Descending | Select-Object -First $Slowest) {
        "| $(Format-Seconds $t.Seconds) | $($t.Outcome) | $($t.Suite) | $(Escape-Cell $t.Name) |"
    }
} else {
    '_None recorded._'
}
''
