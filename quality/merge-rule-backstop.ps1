#Requires -Version 7.4
function Test-MergeRuleBackstop {
    [CmdletBinding()]
    param([string]$Repo, [long]$RunId, [DateTimeOffset]$Now = [DateTimeOffset]::UtcNow)
    $ErrorActionPreference = 'Stop'
    function Read-Pages([string]$path, [string]$key) {
        $text = @(gh api --paginate --slurp $path)
        if ($LASTEXITCODE) { throw "Queue API failed: $path ($LASTEXITCODE)" }
        $pages = @(($text -join "`n") | ConvertFrom-Json)
        if (-not $pages.Count) { throw "Empty queue API response: $path" }
        $items = @(foreach ($page in $pages) {
            if ($null -eq $page.$key -or $null -eq $page.total_count) { throw "Incomplete queue API response: $path" }
            $page.$key
        })
        if ($items.Count -ne $pages[0].total_count) { throw "Truncated queue API response: $path" }
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
        Write-Host 'Fresh scoped evaluations are queued; this cron sweep yields before the writer queue.'
        return $false
    } catch {
        Write-Warning "Could not establish a fresh scoped queue: $($_.Exception.Message) Cron will proceed."
        return $true
    }
}
