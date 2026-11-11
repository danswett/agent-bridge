#Requires -Version 7.4
function Test-MergeRuleBackstop {
    [CmdletBinding()]
    param([string]$Repo, [long]$RunId, [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow)
    $ErrorActionPreference = 'Stop'
    function Read-Pages([string]$path, [string]$key, [switch]$FirstPage) {
        $text = if ($FirstPage) { @(gh api $path) } else { @(gh api --paginate --slurp $path) }
        if ($LASTEXITCODE) { throw "Queue API failed: $path ($LASTEXITCODE)" }
        $pages = @(($text -join "`n") | ConvertFrom-Json)
        if (-not $pages.Count) { throw "Empty queue API response: $path" }
        $items = @(foreach ($page in $pages) {
            if ($null -eq $page.$key -or $null -eq $page.total_count) { throw "Incomplete queue API response: $path" }
            $page.$key
        })
        $expected = if ($FirstPage) { [Math]::Min(10, $pages[0].total_count) } else { $pages[0].total_count }
        if ($items.Count -ne $expected) { throw "Truncated queue API response: $path" }
        $items
    }
    try {
        $runs = @(foreach ($status in 'queued', 'pending', 'waiting', 'in_progress') {
            Read-Pages "repos/$Repo/actions/workflows/merge-rule.yml/runs?status=$status&per_page=100" 'workflow_runs'
        }) | Sort-Object id -Unique
        $oldest = $null
        foreach ($run in $runs) {
            if (-not $run.id -or -not $run.event -or -not $run.status) { throw 'Incomplete queued run.' }
            if ($run.id -eq $RunId -or $run.event -in 'schedule', 'workflow_dispatch' -or
                $run.status -notin 'queued', 'pending', 'waiting', 'in_progress') { continue }
            if ($run.event -notin 'status', 'pull_request_target', 'check_suite', 'workflow_run') {
                throw 'Unrecognised queued event; evaluation may still be needed.'
            }
            foreach ($job in @(Read-Pages "repos/$Repo/actions/runs/$($run.id)/jobs?filter=latest&per_page=100" 'jobs')) {
                if (-not $job.status) { throw 'Incomplete queued job.' }
                if ($job.status -notin 'queued', 'pending', 'waiting') { continue }
                # Versioned job names are the API's classifier; unknown names never suppress cron.
                if ($job.name -eq 'merge-rule-sweep-v1') { continue }
                if ($job.name -ne 'merge-rule-scoped-v1') { throw 'Unrecognised queued job; evaluation may still be needed.' }
                $created = if ($job.created_at) { $job.created_at } else { $run.created_at }
                if (-not $created) { throw 'Queued evaluation has no creation time.' }
                $at = [DateTimeOffset]::Parse($created, [Globalization.CultureInfo]::InvariantCulture)
                if ($at -gt $Now) { throw 'Queued evaluation has a future creation time.' }
                if ($null -eq $oldest -or $at -lt $oldest) { $oldest = $at }
            }
        }
        if ($null -eq $oldest) { return $true }
        if (($Now - $oldest).TotalMinutes -ge 30) {
            Write-Warning 'Queued evaluation is at least 30 minutes old; the cron backstop will proceed.'
            return $true
        }
        # A missing recent writer must cause a sweep, not an unbounded history scan.
        $history = @(Read-Pages "repos/$Repo/actions/workflows/merge-rule.yml/runs?event=schedule&status=completed&per_page=10" 'workflow_runs' -FirstPage)
        foreach ($run in $history) {
            if (-not $run.id -or $run.event -ne 'schedule' -or $run.status -ne 'completed') {
                throw 'Incomplete cron history.'
            }
            if ($run.id -eq $RunId) { continue }
            foreach ($job in @(Read-Pages "repos/$Repo/actions/runs/$($run.id)/jobs?filter=latest&per_page=100" 'jobs')) {
                if ($job.name -ne 'merge-rule-sweep-v1' -or $job.status -ne 'completed' -or
                    $job.conclusion -notin 'success', 'failure') { continue }
                if (-not $job.completed_at) { throw 'Completed cron writer has no completion time.' }
                $finished = [DateTimeOffset]::Parse($job.completed_at, [Globalization.CultureInfo]::InvariantCulture)
                if ($finished -gt $Now) { throw 'Completed cron writer has a future completion time.' }
                # Any proven writer within the bound proves the latest writer is also recent.
                if (($Now - $finished).TotalMinutes -lt 30) {
                    Write-Host 'Fresh scoped evaluations are queued and a cron writer completed within 30 minutes; this cron sweep yields.'
                    return $false
                }
            }
        }
        Write-Warning 'No completed cron writer within 30 minutes could be established; the cron backstop will proceed.'
        return $true
    } catch {
        Write-Warning "Could not establish a fresh scoped queue: $($_.Exception.Message) Cron will proceed."
        return $true
    }
}
