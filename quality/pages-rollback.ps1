#Requires -Version 7
param([object[]]$Runs = @(), [int]$TotalCount = 0, [switch]$Unreadable)
$ErrorActionPreference = 'Stop'
$state = @{ held = $true; fence = ''; reason = 'rollback-boundary-unavailable' }
if ($Unreadable) { $state.reason = 'rollback-history-unavailable'; return $state }
# Equal timestamps favor rollback, never an uncertain resume.
foreach ($run in ($Runs | Sort-Object { [DateTimeOffset]$_.created_at }, { $_.display_title -ceq 'Pages rollback' } -Descending)) {
    if ($run.event -cne 'workflow_dispatch' -or $run.head_branch -cne 'main') { continue }
    if ($run.display_title -ceq 'Pages rollback') {
        if ($run.head_sha -match '^[0-9a-f]{40}$') { $state.fence = $run.head_sha; $state.reason = 'rollback-active' }
        return $state
    }
    if ($run.display_title -ceq 'Pages resume' -and $run.status -ceq 'completed' -and $run.conclusion -ceq 'success') {
        $state.held = $false; $state.reason = 'rollback-resumed'
        return $state
    }
}
if ($TotalCount -gt $Runs.Count) { $state.reason = 'rollback-history-truncated' }
$state
