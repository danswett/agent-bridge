# Which main commit the release train publishes (docs/app-windows.md, "The release train"):
# the newest whose FAST lane (ci.yml: the merge gate) AND FULL lane (full.yml: the x64 self-test, pack and installer,
# WebKit, spikes, Arm64) both passed, that follows the latest release's commit, whose run number is
# above every published version and whose FULL run's package artifacts for both architectures are still
# there. Nothing at all when that commit is already out, or has no FULL run yet, so an hourly run with nothing new does
# nothing. Pure over its inputs; build/test-select-release-commit.ps1 checks it.
#
# A run's Full property is the FULL run for the same commit (its latest attempt): Conclusion, AllJobs (job name -> conclusion)
# and ArtifactsUnexpired (both architectures' packages, which FULL builds); absent or null when it has none yet.
#
# Runs are main's push CI runs, newest first, each as its latest attempt:
#   Sha, Number (the run number, which is the version's last part), Conclusion,
#   Jobs (job name -> conclusion: quality-gate, app-x64, app-arm64: the ones a pick needs),
#   AllJobs (every job's name -> conclusion, latest attempt), ArtifactsUnexpired (both architectures' packages),
#   and optionally ReliedParent: the sha of the in-flight parent run it relied on to test only its own change (its plan job's
#   plan-base record; absent or null when it relied on none). Such a run is publishable only if that parent, and the parent it
#   relied on, passed the same checks and did not end cancelled: the commit holds the parent's change, which the run never tested.
# quality-gate and the x64 app job must have succeeded. Arm64 jobs (app win-arm64, app-installer
# win-arm64, product windows-11-arm) may be red; no other job may be: failed, timed out, and for a run
# Latest is the newest published release: Tag (v0.1.<n>) and Commit; $null when none is out.
# whose conclusion is failure, cancelled too. A cancelled (superseded) run otherwise counts.
# IsDescendant takes a sha and the latest release's commit.
# Why a run is not green enough to publish, or $null when it is: the checks on its jobs and conclusion, not those on the artifacts,
# the version or the release it follows. The same checks judge a run and the parent it relied on.
function Get-RunRedReason($run) {
    $failed = @('quality-gate', 'app-x64' | Where-Object { $run.Jobs[$_] -cne 'success' })
    if ($failed) { return "$($failed -join ', ') did not succeed" }
    # Arm64 jobs run and report but do not gate (owner decision, #2454): quality-gate and the x64 app job
    # must have succeeded, and a run that is red only for Arm64 (conclusion failure, with both of
    # those green) is publishable, its Arm64 package included (both architectures' artifacts must
    # exist, below). A timed-out or unfinished run is never eligible.
    if ($run.Conclusion -cnotin 'success', 'cancelled', 'failure') { return "the run's conclusion is '$($run.Conclusion)'" }
    # Only Arm64's own jobs may be red (ci.yml's quality-gate leaves relay-image-sarif, sign and the
    # publish jobs out, so a failure there could leave quality-gate and the x64 app job green): any
    # other job that failed or timed out (and, in a failed run, was cancelled), or an unknown set, is
    # not publishable, whatever the run's conclusion.
    $allJobs = $run.PSObject.Properties['AllJobs']?.Value
    if ($null -eq $allJobs) { return "the jobs' results are unknown" }
    $red = @('failure', 'timed_out', 'startup_failure', 'action_required') + @(if ($run.Conclusion -ceq 'failure') { 'cancelled' })
    $others = @($allJobs.GetEnumerator() | Where-Object { $_.Value -cin $red -and $_.Key -cnotin 'app (win-arm64)', 'app-installer (win-arm64)', 'product (windows-11-arm)' } | ForEach-Object { "$($_.Key) ($($_.Value))" })
    if ($others) { return $others -join ', ' }
    $null
}

# The FULL lane (full.yml) gates the release, never a merge (S2, #4104). Required, all succeeded: the aggregate full-gate (which fails when a lane
# it selected was skipped, WebKit's included), the x64 self-test, pack and installer, and the Arm64 PACK (with its package and SBOM, below). Waived (owner
# decision, #2454 and "ship now"; R8b on 4220): everything Arm64 that RUNS the app: the self-test, the tests, and the Arm64 INSTALLER, which installs and starts
# the app, i.e. is the Arm64 self-test (#4159: a red Arm64 installer must not block a release). So the Arm64 package is never optional, but its checks are.
# No other job there may be red. A run with no FULL run yet, or one still running, cancelled or timed out, is not publishable: the train picks it up when FULL is done.
$script:FullRequiredJobs = @('full-gate', 'app-selftest (win-x64)', 'app-pack (win-x64)', 'app-installer (win-x64)', 'app-pack (win-arm64)')
$script:ArmJobs = @('app-selftest (win-arm64)', 'app-installer (win-arm64)', 'product (windows-11-arm)')
function Get-FullRedReason($run) {
    $full = $run.PSObject.Properties['Full']?.Value
    if ($null -eq $full) { return 'no FULL run for this commit yet' }
    if ($full.Conclusion -cnotin 'success', 'failure') { return "the FULL run's conclusion is '$($full.Conclusion)'" }
    $jobs = $full.PSObject.Properties['AllJobs']?.Value
    if ($null -eq $jobs) { return "the FULL run's jobs' results are unknown" }
    $missing = @($script:FullRequiredJobs | Where-Object { $jobs[$_] -cne 'success' })
    if ($missing) { return "FULL: $($missing -join ', ') did not succeed" }
    $red = @('failure', 'timed_out', 'startup_failure', 'action_required', 'cancelled')
    $others = @($jobs.GetEnumerator() | Where-Object { $_.Value -cin $red -and $_.Key -cnotin $script:ArmJobs } | ForEach-Object { "FULL: $($_.Key) $($_.Value)" })
    if ($others) { return $others -join ', ' }
    $null
}

# What was waived, said, never silent (Quality's condition on 4220): the Arm64 self-test, tests and installer may be red, skipped or absent and the release
# still goes out, so the train's publish summary carries this notice, and full-red's alert names a red Arm64 job too. $null when nothing Arm64 was waived.
function Format-WaivedNotice($run) {
    $full = $run.PSObject.Properties['Full']?.Value
    $jobs = if ($full) { $full.PSObject.Properties['AllJobs']?.Value } else { $null }
    if ($null -eq $jobs) { return $null }
    $lines = foreach ($name in $script:ArmJobs) {
        $result = $jobs[$name]
        if ($null -eq $result -and $name -cne 'product (windows-11-arm)') { "${name}: not run" }
        elseif ($null -ne $result -and $result -cne 'success') { "${name}: $result" }
    }
    if (-not $lines) { return $null }
    "Arm64 checks waived for this release, which was published: $($lines -join '; ')"
}

# The FULL run of a commit is found by the commit's head_sha, never by branch: a branch lookup could name another commit's run.
function Get-FullRunsPath([string]$Repository, [string]$Sha) { "repos/$Repository/actions/workflows/full.yml/runs?head_sha=$Sha&per_page=1" }

# What FULL built for a commit, from its run's unexpired artifact names, under the FAST run's number (FULL packs as 0.1.<that number>; its own
# run number is never the version): both architectures' packages AND both SBOMs, which the attestation needs. Incomplete is named, not a version.
function Get-FullPackages([string[]]$ArtifactNames, [int]$Number) {
    $prefix = @($ArtifactNames | ForEach-Object { if ($_ -cmatch "^agent-bridge-windows-(\d+\.\d+)\.$Number$") { $Matches[1] } }) | Select-Object -First 1
    if (-not $prefix) { return [pscustomobject]@{ Version = ''; Complete = $false; Missing = @("agent-bridge-windows-<version>.$Number") } }
    $missing = @("agent-bridge-windows-$prefix.$Number", "agent-bridge-windows-arm64-$prefix.$Number", "sbom-win-x64-$prefix.$Number", "sbom-win-arm64-$prefix.$Number" |
        Where-Object { $ArtifactNames -cnotcontains $_ })
    [pscustomobject]@{ Version = "$prefix.$Number"; Complete = ($missing.Count -eq 0); Missing = $missing }
}

# Why the in-flight parents a run relied on do not cover it, or $null when the whole chain is green. A parent that ended cancelled never
# tested its change, so the run's pass does not stand for it; one still running, or not among the runs, is no pass either.
function Get-ReliedRedReason($run, [object[]]$Runs) {
    $parent = $run.PSObject.Properties['ReliedParent']?.Value
    for ($depth = 0; $parent; $depth++) {
        if ($depth -ge 20) { return "its chain of reliance is deeper than 20" }
        $found = @($Runs | Where-Object { $_.Sha -ceq $parent } | Select-Object -First 1)
        if ($found.Count -eq 0) { return "relied on $parent, which has no run among these" }
        $why = if ($found[0].Conclusion -ceq 'cancelled') { 'it was cancelled and never tested its change' } else { Get-RunRedReason $found[0] }
        if ($why) { return "relied on $parent, which is not green: $why" }
        $parent = $found[0].PSObject.Properties['ReliedParent']?.Value
        if ($parent -ceq $run.Sha) { return "relied on a parent that relied back on it" }
    }
    $null
}

# The parent a plan-base record names (ci.yml's plan job: 'base=<sha> relied-parent=<sha|none>'): its sha, or $null for none or no record
# at all (a run from before the record). A record that is not in the shape is an error, not a guess.
function ConvertFrom-PlanBase([string]$Message) {
    if ([string]::IsNullOrWhiteSpace($Message)) { return $null }
    if ($Message -cnotmatch '^base=[0-9a-f]{40} relied-parent=(none|[0-9a-f]{40})$') { throw "unreadable plan-base record '$Message'" }
    if ($Matches[1] -ceq 'none') { $null } else { $Matches[1] }
}

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
        $red = Get-RunRedReason $run
        if ($red) { $why.Add("$($run.Sha) run $($run.Number): $red"); continue }
        $reliedRed = Get-ReliedRedReason $run $Runs
        if ($reliedRed) { $why.Add("$($run.Sha) run $($run.Number): $reliedRed"); continue }
        $fullRed = Get-FullRedReason $run
        if ($fullRed) { $why.Add("$($run.Sha) run $($run.Number): $fullRed"); continue }
        if (-not $run.Full.ArtifactsUnexpired) { $why.Add("$($run.Sha) run $($run.Number): FULL's packages or SBOMs expired or missing"); continue }
        if ($run.Number -le $floor) { $why.Add("$($run.Sha) run $($run.Number): not above published version $floor"); continue }
        if ($Latest -and (& $IsDescendant $run.Sha $Latest.Commit) -cne $true) { $why.Add("$($run.Sha) does not follow the latest release's commit"); continue }
        if ($Explain) { $waived = Format-WaivedNotice $run; return "selected $($run.Sha)$(if ($waived) { "; $waived" })" }
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
        $allJobs = @{}
        $planJob = $null
        foreach ($job in (Invoke-ReleaseGh api "repos/$Repository/actions/runs/$id/jobs?filter=latest&per_page=100" --paginate --jq '.jobs[] | [.name, .conclusion, .id] | @tsv')) {
            $name, $result, $jobId = $job -split "`t"
            if ($name -ceq 'plan') { $planJob = $jobId }
            $key = switch ($name) { 'quality-gate' { 'quality-gate' } 'app (win-x64)' { 'app-x64' } 'app (win-arm64)' { 'app-arm64' } default { $null } }
            if ($key) { $jobs[$key] = $result }
            $allJobs[$name] = $result
        }
        # The FULL run for this commit (full.yml; its newest run, latest attempt), and the packages it built: FULL builds under this run's
        # number (0.1.<number>), so its artifacts carry the version the selector floors against. None yet: Full is $null.
        $fullRow = @(Invoke-ReleaseGh api (Get-FullRunsPath $Repository $sha) --jq '.workflow_runs[] | [.id, .conclusion] | @tsv') | Select-Object -First 1
        $full = $null
        $both = $false
        $prefix = $null
        if ($fullRow) {
            $fullId, $fullConclusion = $fullRow -split "`t"
            $fullJobs = @{}
            foreach ($job in (Invoke-ReleaseGh api "repos/$Repository/actions/runs/$fullId/jobs?filter=latest&per_page=100" --paginate --jq '.jobs[] | [.name, .conclusion] | @tsv')) {
                $name, $result = $job -split "`t"
                $fullJobs[$name] = $result
            }
            $names = @(Invoke-ReleaseGh api "repos/$Repository/actions/runs/$fullId/artifacts?per_page=100" --jq '.artifacts[] | select(.expired == false) | .name')
            $packages = Get-FullPackages $names ([int]$number)
            $prefix = $packages.Version -replace '\.\d+$', ''
            $both = $packages.Complete
            $full = [pscustomobject]@{ Id = $fullId; Conclusion = $fullConclusion; AllJobs = $fullJobs; ArtifactsUnexpired = [bool]$both }
        }
        # The in-flight parent this run relied on, from its plan job's plan-base notice (none when there is no record).
        $record = if ($planJob) { @(Invoke-ReleaseGh api "repos/$Repository/check-runs/$planJob/annotations?per_page=100" --jq '.[] | select(.title == "plan-base") | .message') | Select-Object -First 1 } else { $null }
        [pscustomobject]@{ Id = $id; Sha = $sha; Number = [int]$number; Conclusion = $conclusion; Jobs = $jobs; AllJobs = $allJobs
            Version = $(if ($both) { "$prefix.$number" } else { '' }); ArtifactsUnexpired = [bool]$both; Full = $full; ReliedParent = (ConvertFrom-PlanBase $record) }
    }
}
