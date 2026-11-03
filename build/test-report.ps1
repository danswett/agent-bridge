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

function Format-Seconds([double] $seconds) { '{0:N1} s' -f $seconds }
function Escape-Cell([string] $text) { $text.Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ') }

"## $Title"
''
'### Coverage'
''
if ($CoverageSummary -and (Test-Path -LiteralPath $CoverageSummary)) {
    $summary = Get-Content -LiteralPath $CoverageSummary -Raw | ConvertFrom-Json
    '| Project | Assembly | Line % | Branch % | Lines covered |'
    '|---|---|---:|---:|---:|'
    foreach ($a in $summary.coverage.assemblies | Sort-Object { $projectOf[$_.name] ?? $_.name }) {
        $project = $projectOf[$a.name] ?? $a.name
        $branch = if ($null -ne $a.branchcoverage) { $a.branchcoverage } else { 'n/a' }
        "| $project | $($a.name) | $($a.coverage) | $branch | $($a.coveredlines) / $($a.coverablelines) |"
    }
    $s = $summary.summary
    "| **All** | | **$($s.linecoverage)** | **$($s.branchcoverage)** | $($s.coveredlines) / $($s.coverablelines) |"
} else {
    '_No coverage summary: the tests did not get far enough to write one._'
}

$suites = @()
$tests = @()
foreach ($trx in Get-ChildItem $TestResults -Filter *.trx -Recurse -ErrorAction SilentlyContinue) {
    $run = ([xml](Get-Content -LiteralPath $trx.FullName -Raw)).TestRun
    $storage = @($run.TestDefinitions.UnitTest)[0].storage
    $suite = if ($storage) { [IO.Path]::GetFileNameWithoutExtension($storage) } else { $trx.BaseName }
    $suite = $testProjects[$suite] ?? $suite
    $counters = $run.ResultSummary.Counters
    $suites += [pscustomobject]@{
        Suite   = $suite
        Total   = [int]$counters.total
        Passed  = [int]$counters.passed
        Failed  = [int]$counters.failed
        Skipped = [int]$counters.total - [int]$counters.executed
        Seconds = ([datetimeoffset]$run.Times.finish - [datetimeoffset]$run.Times.start).TotalSeconds
    }
    foreach ($r in @($run.Results.UnitTestResult)) {
        if ($r.duration) {
            $tests += [pscustomobject]@{
                Suite   = $suite
                Name    = $r.testName
                Outcome = $r.outcome
                Seconds = [timespan]::Parse($r.duration, [cultureinfo]::InvariantCulture).TotalSeconds
            }
        }
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
    "| **All** | **$(($suites | Measure-Object Total -Sum).Sum)** | | | | |"
} else {
    '_No TRX files: the tests did not get far enough to write them._'
}

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
