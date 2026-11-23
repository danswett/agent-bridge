# Asserts app-self-test-join.ps1 accepts a complete set of self-test shards and refuses each way a set can be wrong:
# a missing or doubled shard, another count, stages that do not add up to stagesAll, a failed check, an unfinished run.
#   pwsh build/test-app-self-test-join.ps1
#Requires -Version 7
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$join = Join-Path $PSScriptRoot 'app-self-test-join.ps1'
$dir = Join-Path ([IO.Path]::GetTempPath()) ('self-test-join-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $dir | Out-Null
$all = @('layout', 'launch', 'approve', 'themes', 'signin', 'progress', 'contrast', 'resize')
$plan = @(@('layout', 'launch', 'approve'), @('themes', 'signin', 'progress'), @('contrast', 'resize'))
$seq = 0

function New-Result([string]$Shard, [string[]]$Stages, [string[]]$Failed = @(), $Unfinished = $null, [string[]]$StagesAll = $all) {
    $path = Join-Path $dir ('r' + (++$script:seq) + '.json')
    [ordered]@{ shard = $Shard; stages = $Stages; stagesAll = $StagesAll; passed = @('a check'); failed = $Failed; unfinished = $Unfinished } |
        ConvertTo-Json -Depth 4 | Set-Content $path
    $path
}
function Complete-Set { 0..2 | ForEach-Object { New-Result "$($_ + 1)/3" $plan[$_] } }
function Join-Exit([string[]]$Paths, [int]$Count = 3) {
    & pwsh -NoProfile -File $join -Results ($Paths -join ',') -Count $Count *> $null
    $LASTEXITCODE
}
$failures = @()
function Expect([string]$What, [int]$Actual, [bool]$Ok) {
    if ($Ok) { Write-Output "ok: $What" } else { $script:failures += "$What (exit $Actual)"; Write-Output "FAILED: $What (exit $Actual)" }
}

try {
    $good = @(Complete-Set)
    $code = Join-Exit $good
    Expect 'three complete shards join' $code ($code -eq 0)

    $code = Join-Exit @($good[0], $good[1])
    Expect 'a missing shard is refused' $code ($code -ne 0)

    $code = Join-Exit @($good[0], $good[1], $good[1])
    Expect 'a doubled shard is refused' $code ($code -ne 0)

    $code = Join-Exit @($good[0], $good[1], (New-Result '3/2' $plan[2]))
    Expect 'a shard of another count is refused' $code ($code -ne 0)

    $code = Join-Exit @($good[0], $good[1], (New-Result '3/3' @('contrast')))
    Expect 'stages that do not add up to stagesAll are refused' $code ($code -ne 0)

    $code = Join-Exit @($good[0], $good[1], (New-Result '3/3' @('contrast', 'resize', 'launch')))
    Expect 'a stage run by two shards is refused' $code ($code -ne 0)

    $code = Join-Exit @($good[0], $good[1], (New-Result '3/3' $plan[2] -Failed @('draws all rows')))
    Expect 'a failed check is refused' $code ($code -ne 0)

    $code = Join-Exit @($good[0], $good[1], (New-Result '3/3' $plan[2] -Unfinished 'live resize'))
    Expect 'an unfinished shard is refused' $code ($code -ne 0)

    $code = Join-Exit @($good[0], $good[1], (Join-Path $dir 'none.json'))
    Expect 'a result file that is not there is refused' $code ($code -ne 0)

    $code = Join-Exit @((New-Result '1/1' $all)) 1
    Expect 'the whole run (1/1) joins' $code ($code -eq 0)
}
finally { Remove-Item $dir -Recurse -Force -ErrorAction SilentlyContinue }

if ($failures) { throw "ASSERTION FAILED: $($failures -join '; ')" }
Write-Output 'app-self-test-join: all cases passed'
