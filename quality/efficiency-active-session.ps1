#Requires -Version 7.4
<#
.SYNOPSIS
  Drives design/efficiency.md section 2.1 through caller-owned session/viewer adapters.
  Dot-source this file, then call Invoke-EfficiencyActiveSession; it measures nothing.
#>
function Invoke-EfficiencyActiveSession {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][scriptblock] $WriteTranscript,
        [Parameter(Mandatory)][scriptblock] $SendHook,
        [Parameter(Mandatory)][scriptblock] $AskAndAnswer,
        [Parameter(Mandatory)][scriptblock] $AttachViewer,
        [Parameter(Mandatory)][Threading.CancellationToken] $CancellationToken,
        [scriptblock] $Elapsed = { $watch.Elapsed.TotalSeconds },
        [scriptblock] $Wait = {
            param($seconds, $token)
            # Round up: sub-millisecond deadlines must not become zero-time spin waits.
            if ($token.WaitHandle.WaitOne([int][math]::Ceiling($seconds * 1000))) {
                $token.ThrowIfCancellationRequested()
            }
        }
    )
    $ErrorActionPreference = 'Stop'
    Set-StrictMode -Version Latest
    $CancellationToken.ThrowIfCancellationRequested()
    $viewer = & $AttachViewer
    if ($viewer -isnot [IDisposable]) { throw 'AttachViewer must return an IDisposable viewer attachment.' }
    $watch = [Diagnostics.Stopwatch]::StartNew()
    try {
        $start = [double](& $Elapsed)
        $lineDue = 2.0; $hookDue = 6.0; $questionDue = 60.0
        while ($true) {
            $CancellationToken.ThrowIfCancellationRequested()
            $now = [double](& $Elapsed) - $start
            $due = [math]::Min($lineDue, [math]::Min($hookDue, $questionDue))
            if ($now -lt $due) {
                $null = & $Wait ($due - $now) $CancellationToken
                continue
            }
            # Keep absolute deadlines so callback time does not accumulate rate drift.
            if ($lineDue -eq $due) {
                $CancellationToken.ThrowIfCancellationRequested()
                $null = & $WriteTranscript
                $lineDue += 2
            }
            if ($hookDue -eq $due) {
                $CancellationToken.ThrowIfCancellationRequested()
                $null = & $SendHook
                $hookDue += 6
            }
            if ($questionDue -eq $due) {
                $CancellationToken.ThrowIfCancellationRequested()
                $null = & $AskAndAnswer
                $questionDue += 60
            }
        }
    } finally {
        $watch.Stop()
        $viewer.Dispose()
    }
}
