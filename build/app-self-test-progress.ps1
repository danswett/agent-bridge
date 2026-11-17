function Get-SelfTestStageEvidence {
    param($Result, [DateTimeOffset]$ObservedAt, [DateTimeOffset]$Deadline, [bool]$WaitFinished, [bool]$ProcessExited)
    $stages = @()
    if ($null -ne $Result -and $Result.PSObject.Properties['stages']) { $stages = @($Result.stages) }
    $active = $stages | Where-Object { -not $_.endedAt } | Select-Object -Last 1
    $completed = if ($null -ne $Result -and $Result.PSObject.Properties['completedAt']) { $Result.completedAt } else { $null }
    $classification = 'timing unavailable; cause unproven'
    if ($completed) {
        $classification = if ([DateTimeOffset]$completed -gt $Deadline) { 'late completion' }
            elseif (-not $WaitFinished -and $ProcessExited) { 'missed process exit' }
            elseif (-not $ProcessExited) { 'completed; awaiting process exit' } else { 'completed within budget' }
    }
    elseif ($active) {
        $silence = [Math]::Max(0, ($ObservedAt - [DateTimeOffset]$active.lastProgressAt).TotalSeconds)
        $where = if ($active.step) { $active.step } else { $active.name }
        $classification = if ($ProcessExited) { "process exited at $where" }
            elseif ($silence -le 30) { "still progressing at $where" }
            else { "stalled at $where; no progress for $([Math]::Round($silence, 1)) s; hang unproven" }
    }
    [pscustomobject]@{
        classification = $classification
        observedAt = $ObservedAt; deadline = $Deadline; completedAt = $completed
        waitFinished = $WaitFinished; processExited = $ProcessExited
        stages = @($stages | ForEach-Object {
            [pscustomobject]@{
                name = $_.name; step = $_.step; startedAt = $_.startedAt; endedAt = $_.endedAt
                lastProgressAt = $_.lastProgressAt; elapsedSeconds = $_.elapsedSeconds
                elapsedAtObservationSeconds = if ($_.endedAt) { $_.elapsedSeconds } else { [Math]::Max(0, ($ObservedAt - [DateTimeOffset]$_.startedAt).TotalSeconds) }
            }
        })
    }
}
