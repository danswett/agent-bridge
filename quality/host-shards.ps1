# The dotnet test --filter for one shard of tests/AgentBridge.Host.Tests (#2454, #2225): a test class
# is the unit, so a shard never splits a class. The split is deterministic: the classes in
# quality/baselines/host-test-class-seconds.json are placed, heaviest first, in the least loaded
# shard (the last starts with the weight of every class the file does not list).
#
# Nothing can be lost. Shards 1..N-1 run the classes placed in them (`FullyQualifiedName~Class.`);
# the last shard runs every test those patterns do not match (`FullyQualifiedName!~Class.`), so a new,
# renamed or unlisted class lands there, and a stale weights file only unbalances. A pattern is a
# substring, so a class whose name ends with another listed class's name (AgentAnswerFlowTests,
# AnswerFlowTests) is placed with it, never apart from it.
param(
    [Parameter(Mandatory)][int]$Shard,
    [int]$Of = 3,
    [string]$Weights = (Join-Path $PSScriptRoot 'baselines/host-test-class-seconds.json')
)
$ErrorActionPreference = 'Stop'
if ($Of -lt 1 -or $Shard -lt 1 -or $Shard -gt $Of) { throw "Shard must be 1..$Of." }
if ($Of -eq 1) { return '' }
$data = Get-Content -Raw $Weights | ConvertFrom-Json
$weight = [ordered]@{}
foreach ($p in $data.classes.PSObject.Properties) { $weight[$p.Name] = [double]$p.Value }
$names = @($weight.Keys | Sort-Object -CaseSensitive)

# Classes whose pattern contains another's go in one group.
$group = @{}; foreach ($n in $names) { $group[$n] = $n }
function Root([string]$n) { while ($group[$n] -cne $n) { $n = $group[$n] }; $n }
foreach ($a in $names) { foreach ($b in $names) {
    if ($a -cne $b -and "$b.".Contains("$a.", [StringComparison]::Ordinal)) { $group[(Root $b)] = Root $a }
} }
$groups = $names | Group-Object { Root $_ } | ForEach-Object {
    [pscustomobject]@{ Key = $_.Name; Classes = @($_.Group); Seconds = ($_.Group | ForEach-Object { $weight[$_] } | Measure-Object -Sum).Sum }
} | Sort-Object @{ Expression = 'Seconds'; Descending = $true }, @{ Expression = 'Key' }

$load = @(0.0) * $Of; $load[$Of - 1] = [double]$data.rest
$bins = 0..($Of - 1) | ForEach-Object { , [Collections.Generic.List[string]]::new() }
foreach ($g in $groups) {
    $to = 0; for ($i = 1; $i -lt $Of; $i++) { if ($load[$i] -lt $load[$to]) { $to = $i } }
    $load[$to] += $g.Seconds; $bins[$to].AddRange([string[]]$g.Classes)
}
$filter = if ($Shard -lt $Of) {
    ($bins[$Shard - 1] | Sort-Object -CaseSensitive | ForEach-Object { "FullyQualifiedName~$_." }) -join '|'
} else {
    (0..($Of - 2) | ForEach-Object { $bins[$_] } | Sort-Object -CaseSensitive | ForEach-Object { "FullyQualifiedName!~$_." }) -join '&'
}
# An empty filter would run every test in this shard: refuse it.
if (-not $filter) { throw "Shard $Shard of $Of would have no filter; the weights file lists too few classes." }
$filter
