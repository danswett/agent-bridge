#Requires -Version 7.4
param([ValidateSet('rates', 'cancel', 'viewer')][string] $Scenario)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
. (Join-Path $PSScriptRoot 'efficiency-active-session.ps1')
function Assert($Condition, [string] $Reason) { if (-not $Condition) { throw $Reason } }
class EfficiencyViewerLease : System.IDisposable {
    [hashtable] $State
    EfficiencyViewerLease([hashtable] $state) { $this.State = $state; $state.viewers++ }
    [void] Dispose() { $this.State.viewers--; $this.State.detached++ }
}
$state = @{
    seconds = 0.0; viewers = 0; detached = 0; waits = 0; activeWaits = 0
    lines = [Collections.Generic.List[double]]::new()
    hooks = [Collections.Generic.List[double]]::new()
    questions = [Collections.Generic.List[double]]::new(); answers = 0
}
$cts = [Threading.CancellationTokenSource]::new()
$options = @{
    CancellationToken = $cts.Token
    AttachViewer = { [EfficiencyViewerLease]::new($state) }
    WriteTranscript = {
        Assert ($state.viewers -eq 1) 'Transcript emitted without an attached viewer.'
        $state.lines.Add($state.seconds)
    }
    SendHook = { $state.hooks.Add($state.seconds) }
    AskAndAnswer = { $state.questions.Add($state.seconds); $state.answers++ }
    # Unlike a manually advanced clock, every read moves forward as well.
    Elapsed = { $state.seconds += 0.001; $state.seconds }
    Wait = {
        param($seconds, $token)
        Assert ($seconds -gt 0 -and $seconds -le 2) 'Wait was spinning or missed the next due event.'
        $state.waits++; $state.activeWaits++
        try {
            if ($state.seconds + $seconds -gt 120.01) { $cts.Cancel(); $token.ThrowIfCancellationRequested() }
            $state.seconds += $seconds
        } finally { $state.activeWaits-- }
    }
}
function Invoke-CancelledDriver {
    $cancelled = $false
    try { Invoke-EfficiencyActiveSession @options }
    catch [OperationCanceledException] { $cancelled = $true }
    Assert $cancelled 'The driver did not propagate cancellation.'
}
try {
    Write-Output 'READY: efficiency-active-session'
    switch ($Scenario) {
        'rates' {
            Invoke-CancelledDriver
            foreach ($pair in @(@($state.lines, 2, 60), @($state.hooks, 6, 20), @($state.questions, 60, 2))) {
                Assert ($pair[0].Count -eq $pair[2]) 'Wrong event count over two minutes.'
                for ($i = 0; $i -lt $pair[0].Count; $i++) {
                    Assert ([math]::Abs($pair[0][$i] - ($i + 1) * $pair[1]) -lt 0.01) 'Event drifted from its scripted deadline.'
                }
            }
            Assert ($state.answers -eq 2) 'Each question must be answered.'
            Assert ($state.waits -ge 60 -and $state.activeWaits -eq 0) 'Waits leaked or events were emitted without waiting.'
        }
        'cancel' {
            # Use the production wait: cancellation must interrupt a pending two-second wait.
            $options.Remove('Elapsed'); $options.Remove('Wait')
            $options.AttachViewer = { $cts.CancelAfter(100); [EfficiencyViewerLease]::new($state) }
            $watch = [Diagnostics.Stopwatch]::StartNew()
            Invoke-CancelledDriver
            Assert ($watch.Elapsed.TotalSeconds -lt 1.5) 'Cancellation waited until the next event.'
            Assert ($state.lines.Count -eq 0 -and $state.hooks.Count -eq 0 -and $state.questions.Count -eq 0) 'Work escaped cancellation.'
            Assert ($state.viewers -eq 0 -and $state.detached -eq 1) 'Cancellation left the viewer attached.'
            Assert (@(Get-Job).Count -eq 0 -and @(Get-EventSubscriber).Count -eq 0) 'Background work survived cancellation.'
            $options.AttachViewer = { throw 'A pre-cancelled run must not attach.' }
            Invoke-CancelledDriver
        }
        'viewer' {
            foreach ($cycle in 1..3) {
                $cts.Dispose(); $cts = [Threading.CancellationTokenSource]::new()
                $state.seconds = 0; $options.CancellationToken = $cts.Token
                $options.WriteTranscript = { throw [IO.IOException]::new('scripted transcript failure') }
                $failed = $false
                try { Invoke-EfficiencyActiveSession @options }
                catch [IO.IOException] { $failed = $_.Exception.Message -eq 'scripted transcript failure' }
                Assert $failed 'Callback failure was hidden.'
                Assert ($state.viewers -eq 0 -and $state.detached -eq $cycle -and $state.activeWaits -eq 0) 'An activity cycle left residue.'
            }
        }
    }
    Write-Output "PASS: $Scenario"
} finally { $cts.Dispose() }
