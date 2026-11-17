param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][long]$RunId)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
function Api { gh api @args }
$run = Api "repos/$Repository/actions/runs/$RunId" | ConvertFrom-Json
if ($run.event -ne 'schedule' -or $run.head_branch -ne 'main' -or $run.conclusion -eq 'success') { return }
$marker = "<!-- ring-drift:$RunId -->"
$comments = Api "repos/$Repository/issues/2567/comments?since=$($run.created_at)&per_page=100" --paginate --slurp | ConvertFrom-Json
if ($comments.body -match [regex]::Escape($marker)) { return }
$pages = Api "repos/$Repository/actions/runs/$RunId/jobs?filter=latest&per_page=100" --paginate --slurp | ConvertFrom-Json
$failed = @($pages.jobs | Where-Object conclusion -In failure,timed_out,cancelled,action_required,startup_failure)
if (-not $failed.Count) { throw 'Scheduled run was not successful but no failing jobs were returned' }
$lines = @($marker, "Daily full ring failed: $($run.html_url)")
foreach ($job in $failed) {
    $log = Api "repos/$Repository/actions/jobs/$($job.id)/logs"
    $version = [regex]::Match(($log -join "`n"), '(?:Runner image: [A-Za-z0-9._-]+ |Version: )(\d{8}\.[0-9.]+)').Groups[1].Value
    if (-not $version) { $version = 'unavailable (job failed before image information was recorded)' }
    $lines += "- $($job.name) ($($job.conclusion)), runner image $version`: $($job.html_url)"
}
$labels = Api "repos/$Repository/labels?per_page=100" --paginate --slurp | ConvertFrom-Json
foreach ($label in 'ring-drift','priority:P1') {
    if ($label -notin $labels.name) { gh label create $label --repo $Repository --color B60205 | Out-Null }
}
Api "repos/$Repository/issues/2567/labels" --method POST -f 'labels[]=ring-drift' -f 'labels[]=priority:P1' | Out-Null
Api "repos/$Repository/issues/2567/comments" --method POST -f "body=$($lines -join "`n")" | Out-Null
