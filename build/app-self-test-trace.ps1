# The installed self-test's bottom-bounce trace, summarised for the job's log and app-exit-cause.json.
#
# SelfTest.HistoryBottomBounce records every frame it samples (app.history-bottom-bounce.json, beside the result) and
# latches invalidSurface once any frame's surface is not a valid one to judge a bounce on: four checks read that
# latch. A failure said only that it was set, never which frame or which term tripped it (main's win-arm64 run of
# 40c35191). This names both, bounded (the first frames, the first invalid one, the last), and changes no assertion.
# Mirrors SelfTest.HistoryBottomBounce's Surface().Valid, term by term; a term the trace does not carry is not blamed.
Set-StrictMode -Version Latest

function Get-BounceTraceSummary {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Path, [int]$Head = 12, [int]$Tail = 6)
    try {
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $null }
        $all = @(Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
    }
    catch { return $null }
    # Only the sampled frames: a request record has no surface.
    $frames = @($all | Where-Object { $_.PSObject.Properties['surfaceValid'] })
    if ($frames.Count -eq 0) { return $null }
    function Get-Field($frame, $name) { $p = $frame.PSObject.Properties[$name]; if ($p) { $p.Value } else { $null } }
    function Brief($frame) {
        [ordered]@{
            ms = Get-Field $frame 'ms'; phase = Get-Field $frame 'phase'; kind = Get-Field $frame 'kind'
            offset = Get-Field $frame 'offset'; extent = Get-Field $frame 'extent'; viewport = Get-Field $frame 'viewport'
            rows = Get-Field $frame 'rows'; requiredRows = Get-Field $frame 'requiredRows'; floor = Get-Field $frame 'extentFloor'
            drawnHistory = Get-Field $frame 'drawnHistory'; visibleHistory = Get-Field $frame 'visibleHistory'; visibleResponse = Get-Field $frame 'visibleResponse'
            valid = Get-Field $frame 'surfaceValid'; arrived = Get-Field $frame 'arrived'
        }
    }
    $first = $frames | Where-Object { (Get-Field $_ 'invalidSurface') -eq $true } | Select-Object -First 1
    $why = @()
    if ($null -ne $first) {
        $viewport = Get-Field $first 'viewport'; $width = Get-Field $first 'width'; $extent = Get-Field $first 'extent'
        $offset = Get-Field $first 'offset'; $floor = Get-Field $first 'extentFloor'; $growth = Get-Field $first 'growthFloor'
        $rows = Get-Field $first 'rows'; $stored = Get-Field $first 'storedRows'; $required = Get-Field $first 'requiredRows'
        $drawn = Get-Field $first 'drawnHistory'; $vh = Get-Field $first 'visibleHistory'; $vr = Get-Field $first 'visibleResponse'
        if ($null -ne $viewport -and -not ($viewport -gt 0)) { $why += "viewport height $viewport is not positive" }
        if ($null -ne $width -and -not ($width -gt 0)) { $why += "viewport width $width is not positive" }
        if ($null -ne $extent -and $null -ne $viewport -and -not ($extent -gt 2 * $viewport)) { $why += "extent $extent is not over twice the viewport $viewport" }
        if ($null -ne $extent -and $null -ne $floor -and $extent -lt $floor) { $why += "extent $extent is below its floor $floor" }
        if ($null -ne $extent -and $null -ne $growth -and -not ($extent -gt $growth)) { $why += "extent $extent is not above the growth floor $growth" }
        if ($null -ne $offset -and $offset -lt 0) { $why += "offset $offset is negative" }
        if ($null -ne $offset -and $null -ne $extent -and ($extent - $offset) -lt -1.5) { $why += "offset $offset is past the extent $extent" }
        if ($null -ne $required -and ($stored -ne $required -or $rows -ne $required)) { $why += "rows $rows / stored $stored are not the $required required" }
        if ($null -ne $drawn -and -not ($drawn -gt 0)) { $why += "drawn history $drawn (no history row drawn)" }
        if ($null -ne $vh -and $null -ne $vr -and -not (($vh + $vr) -gt 0)) { $why += "nothing in view (history $vh, response $vr)" }
        if ($why.Count -eq 0) { $why += 'no single term named: the frame read as valid when the latch was set' }
    }
    [ordered]@{
        frames = $frames.Count
        firstInvalid = if ($null -ne $first) { Brief $first } else { $null }
        why = $why
        head = @($frames | Select-Object -First $Head | ForEach-Object { Brief $_ })
        tail = @($frames | Select-Object -Last $Tail | ForEach-Object { Brief $_ })
    }
}
