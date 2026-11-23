# Which main commit the release train publishes (docs/app-windows.md, "The release train"):
# the newest whose CI passed whole, that follows the latest release's commit, whose run number is
# above every published version and whose package artifacts for both architectures are still
# there. Nothing at all when that commit is already out, so an hourly run with nothing new does
# nothing. Pure over its inputs; build/test-select-release-commit.ps1 checks it.
#
# Runs are main's push CI runs, newest first, each as its latest attempt:
#   Sha, Number (the run number, which is the version's last part), Attempt, Conclusion,
#   Jobs (job name -> conclusion: quality-gate, app-x64, app-arm64), ArtifactsUnexpired (both architectures' packages).
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
        $failed = @('quality-gate', 'app-x64' | Where-Object { $run.Jobs[$_] -cne 'success' })
        if ($failed) { $why.Add("$($run.Sha) run $($run.Number): $($failed -join ', ') did not succeed"); continue }
        # Arm64 jobs run and report but do not gate (owner decision, #2454): quality-gate and the x64 app job
        # must have succeeded, and a run that is red only for Arm64 (conclusion failure, with both of
        # those green) is publishable, its Arm64 package included (both architectures' artifacts must
        # exist, below). A timed-out or unfinished run is never eligible.
        if ($run.Conclusion -cnotin 'success', 'cancelled', 'failure') { $why.Add("$($run.Sha) run $($run.Number): the run's conclusion is '$($run.Conclusion)'"); continue }
        if (-not $run.ArtifactsUnexpired) { $why.Add("$($run.Sha) run $($run.Number): package artifacts expired"); continue }
        if ($run.Number -le $floor) { $why.Add("$($run.Sha) run $($run.Number): not above published version $floor"); continue }
        if ($Latest -and (& $IsDescendant $run.Sha $Latest.Commit) -cne $true) { $why.Add("$($run.Sha) does not follow the latest release's commit"); continue }
        if ($Explain) { return "selected $($run.Sha)" }
        return $run
    }
    if ($Explain) { return ($why -join "`n") }
    $null
}

# The train refuses while the app is signed: it has no sign step yet, and an unsigned release
# where signing is on would reach every installed copy (docs/app-windows.md, "The release train").
function Assert-TrainMayPublish([string]$AppSigning) {
    $value = if ($null -eq $AppSigning) { '' } else { $AppSigning.Trim() }
    # Publish unsigned only when signing is plainly off: unset or false. Anything else (true in any
    # case, or a value this does not know) fails closed, as ci.yml reads true case-insensitively.
    if ($value -ne '' -and $value -ine 'false') { throw "APP_SIGNING is '$value' and the release train has no sign step; refusing to publish unsigned" }
}

function Invoke-ReleaseGh {
    # gh reports a failure on stderr and a nonzero exit; unchecked, it would read as no runs, and a
    # broken token would look like nothing new.
    $output = & gh @args
    if ($LASTEXITCODE) { throw "gh $($args[0]) $($args[1]) failed ($LASTEXITCODE)" }
    $output
}

# Main's push CI runs for the selector, newest first (gh, GH_TOKEN): one list of runs, and per
# run its jobs (the latest attempt, so a successful re-run counts) and its artifacts. The version
# is read off the artifact names, so a minor bump (0.2.n) needs no change here. Not covered by the
# fixture test; a dispatch run checks it.
function Get-ReleaseRuns([string]$Repository, [int]$Count = 20) {
    $list = Invoke-ReleaseGh api "repos/$Repository/actions/workflows/ci.yml/runs?branch=main&event=push&per_page=$Count" --jq '.workflow_runs[] | [.id, .head_sha, .run_number, .conclusion] | @tsv'
    foreach ($row in $list) {
        $id, $sha, $number, $conclusion = $row -split "`t"
        $jobs = @{}
        foreach ($job in (Invoke-ReleaseGh api "repos/$Repository/actions/runs/$id/jobs?filter=latest&per_page=100" --jq '.jobs[] | [.name, .conclusion] | @tsv')) {
            $name, $result = $job -split "`t"
            $key = switch ($name) { 'quality-gate' { 'quality-gate' } 'app (win-x64)' { 'app-x64' } 'app (win-arm64)' { 'app-arm64' } default { $null } }
            if ($key) { $jobs[$key] = $result }
        }
        $names = @(Invoke-ReleaseGh api "repos/$Repository/actions/runs/$id/artifacts?per_page=100" --jq '.artifacts[] | select(.expired == false) | .name')
        $prefix = ($names | ForEach-Object { if ($_ -cmatch "^agent-bridge-windows-(\d+\.\d+)\.$number$") { $Matches[1] } } | Select-Object -First 1)
        $both = $prefix -and ($names -contains "agent-bridge-windows-$prefix.$number") -and ($names -contains "agent-bridge-windows-arm64-$prefix.$number")
        [pscustomobject]@{ Id = $id; Sha = $sha; Number = [int]$number; Conclusion = $conclusion; Jobs = $jobs
            Version = $(if ($both) { "$prefix.$number" } else { '' }); ArtifactsUnexpired = [bool]$both }
    }
}
