# Joins the result files of the published-app self-test's shards (#1755): every shard 1..Count there exactly once,
# all of one count, their stages adding up to stagesAll with none run twice, no failed check and nothing unfinished.
# Ends 0 when the set is whole and green, 1 otherwise, saying why.
#
#   pwsh build/app-self-test-join.ps1 -Results shard1/result.json, shard2/result.json, shard3/result.json -Count 3
#Requires -Version 7
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$Results,
    [Parameter(Mandatory)][ValidateRange(1, 3)][int]$Count
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# pwsh -File passes -Results a,b,c as one string: split it, so a list works however it arrives.
$Results = @($Results | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
$problems = [System.Collections.Generic.List[string]]::new()
$shards = @{}
$stages = @{}
$all = $null
$passed = 0
foreach ($path in $Results) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { $problems.Add("no result at $path (a shard did not run or wrote nothing)"); continue }
    try { $r = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json }
    catch { $problems.Add("$path is not JSON: $($_.Exception.Message)"); continue }
    $label = if ($r.PSObject.Properties['shard']) { [string]$r.shard } else { $null }
    if ($label -notmatch '^([1-3])/([1-3])$') { $problems.Add("$path names no shard (shard='$label')"); continue }
    $index = [int]$Matches[1]; $of = [int]$Matches[2]
    if ($of -ne $Count) { $problems.Add("shard $label is of $of, not $Count"); continue }
    if ($shards.ContainsKey($index)) { $problems.Add("shard $label is there twice"); continue }
    $shards[$index] = $path
    $mine = @($r.stages)
    $theirAll = @($r.stagesAll)
    if ($theirAll.Count -eq 0) { $problems.Add("shard $label lists no stagesAll: it proves nothing"); continue }
    if ($mine.Count -eq 0) { $problems.Add("shard $label ran no stage"); continue }
    if ($null -eq $all) { $all = $theirAll }
    elseif (($all -join ',') -ne ($theirAll -join ',')) { $problems.Add("shard $label lists other stages than the rest: $($theirAll -join ', ')") }
    foreach ($stage in $mine) {
        if ($stages.ContainsKey($stage)) { $problems.Add("stage '$stage' ran in shard $label and in shard $($stages[$stage])") }
        else { $stages[$stage] = $label }
    }
    foreach ($failed in @($r.failed | Where-Object { $_ })) { $problems.Add("shard ${label}: failed: $failed") }
    if ($r.PSObject.Properties['unfinished'] -and $r.unfinished) { $problems.Add("shard $label did not finish: $($r.unfinished)") }
    $passed += @($r.passed).Count
}
foreach ($index in 1..$Count) { if (-not $shards.ContainsKey($index)) { $problems.Add("shard $index/$Count is missing") } }
if ($null -ne $all) {
    foreach ($stage in $all) { if (-not $stages.ContainsKey($stage)) { $problems.Add("stage '$stage' ran in no shard") } }
    foreach ($stage in $stages.Keys) { if ($all -notcontains $stage) { $problems.Add("stage '$stage' is not in stagesAll") } }
}
if ($problems.Count -gt 0) {
    foreach ($problem in $problems) { Write-Output "::error::self-test shards: $problem" }
    exit 1
}
Write-Output "self-test shards: $Count of $Count joined, $($stages.Count) stages, $passed checks passed"
exit 0
