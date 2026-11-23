# Which main commit the release train publishes (docs/app-windows.md, "The release train"):
# the newest whose CI passed whole, that follows the latest release's commit, whose run number is
# above every published version and whose package artifacts for both architectures are still
# there. Nothing at all when that commit is already out, so an hourly run with nothing new does
# nothing. Pure over its inputs; build/test-select-release-commit.ps1 checks it.
#
# Runs are main's push CI runs, newest first, each as its latest attempt:
#   Sha, Number (the run number, which is the version's last part), Attempt, Conclusion,
#   Jobs (job name -> conclusion: quality-gate, app-x64, app-arm64), ArtifactsUnexpired.
# A cancelled or superseded run counts only if quality-gate and both app jobs succeeded.
# Latest is the newest published release: Tag (v0.1.<n>) and Commit; $null when none is out.
# IsDescendant takes a sha and the latest release's commit.
function Select-ReleaseCommit {
    param(
        [Parameter(Mandatory)][object[]]$Runs,
        $Latest,
        [Parameter(Mandatory)][scriptblock]$IsDescendant,
        [switch]$Explain
    )
    $why = [Collections.Generic.List[string]]::new()
    $floor = if ($Latest) { [int](($Latest.Tag -replace '^v\d+\.\d+\.', '')) } else { 0 }
    foreach ($run in $Runs) {
        if ($Latest -and $run.Sha -ceq $Latest.Commit) { $why.Add("$($run.Sha) is already released"); break }
        $failed = @('quality-gate', 'app-x64', 'app-arm64' | Where-Object { $run.Jobs[$_] -cne 'success' })
        if ($failed) { $why.Add("$($run.Sha) run $($run.Number): $($failed -join ', ') did not succeed"); continue }
        if (-not $run.ArtifactsUnexpired) { $why.Add("$($run.Sha) run $($run.Number): package artifacts expired"); continue }
        if ($run.Number -le $floor) { $why.Add("$($run.Sha) run $($run.Number): not above published version $floor"); continue }
        if ($Latest -and -not (& $IsDescendant $run.Sha $Latest.Commit)) { $why.Add("$($run.Sha) does not follow the latest release's commit"); continue }
        if ($Explain) { return "selected $($run.Sha)" }
        return $run
    }
    if ($Explain) { return ($why -join "`n") }
    $null
}

# The train refuses while the app is signed: it has no sign step yet, and an unsigned release
# where signing is on would reach every installed copy (docs/app-windows.md, "The release train").
function Assert-TrainMayPublish([string]$AppSigning) {
    if ($AppSigning -ceq 'true') { throw 'APP_SIGNING is true and the release train has no sign step; refusing to publish unsigned' }
}

# Main's push CI runs for the selector, newest first (gh, GH_TOKEN): one list of runs, and per
# run its jobs and its artifacts. Not covered by the fixture test; a dispatch run checks it.
function Get-ReleaseRuns([string]$Repository, [int]$Count = 20) {
    $list = gh api "repos/$Repository/actions/workflows/ci.yml/runs?branch=main&event=push&per_page=$Count" --jq '.workflow_runs[] | [.id, .head_sha, .run_number, .run_attempt, .conclusion] | @tsv'
    foreach ($row in $list) {
        $id, $sha, $number, $attempt, $conclusion = $row -split "`t"
        $jobs = @{}
        foreach ($job in (gh api "repos/$Repository/actions/runs/$id/jobs?per_page=100" --jq '.jobs[] | [.name, .conclusion] | @tsv')) {
            $name, $result = $job -split "`t"
            $key = switch ($name) { 'quality-gate' { 'quality-gate' } 'app (win-x64)' { 'app-x64' } 'app (win-arm64)' { 'app-arm64' } default { $null } }
            if ($key) { $jobs[$key] = $result }
        }
        $version = "0.1.$number"
        $names = gh api "repos/$Repository/actions/runs/$id/artifacts?per_page=100" --jq '.artifacts[] | select(.expired == false) | .name'
        [pscustomobject]@{ Id = $id; Sha = $sha; Number = [int]$number; Attempt = [int]$attempt; Conclusion = $conclusion; Jobs = $jobs
            ArtifactsUnexpired = (@($names) -contains "agent-bridge-windows-$version") -and (@($names) -contains "agent-bridge-windows-arm64-$version") }
    }
}
