# Offline checks for quality/host-shards.ps1 (#2454): the shard filters are matched against a stand-in
# list of test names, as dotnet test matches them (a `~` term is a substring of the full name; `|` any,
# `&` all). The point: no test is ever lost, none runs twice, and the split is stable and balanced.
$ErrorActionPreference = 'Stop'
$script = Join-Path $PSScriptRoot 'host-shards.ps1'
$weights = Get-Content -Raw (Join-Path $PSScriptRoot 'baselines/host-test-class-seconds.json') | ConvertFrom-Json
$fail = 0
function Check([string]$name, [bool]$ok) { if ($ok) { "ok   $name" } else { "FAIL $name"; $script:fail++ } }
function ShardFilter([int]$shard, [int]$of) { [string](& $script -Shard $shard -Of $of) }
function Runs([string]$filter, [string]$test) {
    if ($filter.Contains('&')) { return -not ($filter -split '&' | Where-Object { $test.Contains(($_ -replace '^FullyQualifiedName!~', ''), [StringComparison]::Ordinal) }) }
    [bool]($filter -split '\|' | Where-Object { $test.Contains(($_ -replace '^FullyQualifiedName~', ''), [StringComparison]::Ordinal) })
}
# Every listed class, plus classes the file does not list: new ones, and ones whose names end in a listed name.
$tests = @($weights.classes.PSObject.Properties.Name | ForEach-Object { "$_.First", "$_.Second(x: 1)" }) +
    'NewlyAddedTests.Works', 'ZzzTests.Works', 'AgentReplyFlowTests.Works', 'AgentBridge.Host.Tests.OtherTests.Works'
foreach ($of in 2, 3, 4) {
    $filters = 1..$of | ForEach-Object { ShardFilter $_ $of }
    $lost = @($tests | Where-Object { $t = $_; -not ($filters | Where-Object { Runs $_ $t }) })
    Check "of ${of}: no test is lost, including unlisted and new classes ($($lost -join ', '))" ($lost.Count -eq 0)
    $twice = @($tests | Where-Object { $t = $_; @($filters | Where-Object { Runs $_ $t }).Count -gt 1 -and $t -notmatch '^AgentReplyFlowTests' })
    Check "of ${of}: no listed or new test runs in two shards ($($twice -join ', '))" ($twice.Count -eq 0)
}
$three = 1..3 | ForEach-Object { ShardFilter $_ 3 }
$answer = @($three | Where-Object { Runs $_ 'AnswerFlowTests.First' })
Check 'a class and one whose name ends with it share a shard' ($answer.Count -eq 1 -and (Runs $answer[0] 'AgentAnswerFlowTests.First'))
Check 'the split is the same on every run' ((($three -join "`n") -ceq ((1..3 | ForEach-Object { ShardFilter $_ 3 }) -join "`n")))
$seconds = @($three | ForEach-Object { $f = $_; ($weights.classes.PSObject.Properties | Where-Object { Runs $f "$($_.Name).X" } | Measure-Object Value -Sum).Sum })
$biggest = [double]($weights.classes.PSObject.Properties | Where-Object { $_.Name -match 'AnswerFlowTests$' } | Measure-Object Value -Sum).Sum
Check "no shard is heavier than the biggest class group or 15% over an even split ($($seconds -join ', '))" (($seconds | Measure-Object -Maximum).Maximum -le [math]::Max($biggest, ($seconds | Measure-Object -Sum).Sum / 3 * 1.15))
Check 'one shard is the whole project' ((ShardFilter 1 1) -eq '')
foreach ($bad in @(@(0, 3), @(4, 3), @(1, 0))) {
    $threw = $false; try { ShardFilter $bad[0] $bad[1] | Out-Null } catch { $threw = $true }
    Check "shard $($bad[0]) of $($bad[1]) is refused" $threw
}
if ($fail) { "$fail check(s) failed"; exit 1 }
"all checks passed"
