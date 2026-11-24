#Requires -Version 7
# Whether automatic Pages deploys are held by a manual rollback (#2166; pages.yml, gate job).
#   held    the fence: no deploy until a later successful 'Pages resume' (or a newer green commit past a rollback's fence)
#   fence   the rollback's commit, while one is active
#   reason  rollback-active | rollback-resumed | no-rollback | rollback-history-unavailable | rollback-history-truncated
#   loud    a hold nobody chose: the history could not be read in full, so the gate must FAIL visibly, never skip green (#4369)
# Known edge: runs are matched by exact run title. A renamed title, or a 'Pages rollback' run that was deleted or aged out by retention,
# leaves the fence open; and a newer green commit past the fence clears it by design.
# TotalCount is required: without it a short page cannot be told from a complete history, so the fence holds loudly.
param([object[]]$Runs = @(), [int]$TotalCount = 0, [switch]$Unreadable)
$ErrorActionPreference = 'Stop'
$state = @{ held = $true; fence = ''; reason = 'rollback-history-unavailable'; loud = $true }
if ($Unreadable) { return $state }
if (-not $PSBoundParameters.ContainsKey('TotalCount')) { Write-Warning 'pages-rollback: TotalCount is required; holding the fence'; return $state }
# Equal timestamps favor rollback, never an uncertain resume.
foreach ($run in ($Runs | Sort-Object { [DateTimeOffset]$_.created_at }, { $_.display_title -ceq 'Pages rollback' } -Descending)) {
    if ($run.event -cne 'workflow_dispatch' -or $run.head_branch -cne 'main') { continue }
    if ($run.display_title -ceq 'Pages rollback') {
        $state.loud = $false
        if ($run.head_sha -match '^[0-9a-f]{40}$') { $state.fence = $run.head_sha; $state.reason = 'rollback-active' } else { $state.reason = 'rollback-active' }
        return $state
    }
    if ($run.display_title -ceq 'Pages resume' -and $run.status -ceq 'completed' -and $run.conclusion -ceq 'success') {
        $state.held = $false; $state.loud = $false; $state.reason = 'rollback-resumed'
        return $state
    }
}
# No rollback in what was read. If that is all of the history, none was ever made: nothing is held. A history cut short may hide one.
if ($TotalCount -gt $Runs.Count) { $state.reason = 'rollback-history-truncated'; return $state }
Write-Warning "pages-rollback: no 'Pages rollback' run in the history, so the fence is open. Matching is by exact run title; a renamed or expired rollback run would also leave it open."
$state.held = $false; $state.loud = $false; $state.reason = 'no-rollback'
$state
