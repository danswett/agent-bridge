#Requires -Version 7.2
<#
.SYNOPSIS
  Opens, updates or closes the one issue that says production's daily probe found a problem.

.DESCRIPTION
  ops-probe.yml runs ops/tools/relay-probe.ps1 against production once a day and passes its JSON
  here. Only actionable results open an issue: any FAIL, and a WARN on the TLS certificate (renewal
  overdue). A slow /healthz from a GitHub runner is a WARN that's only noted in the run summary,
  since one slow request from one runner says little.

  One issue, labelled area:ops and found by its title, is updated in place, so a problem that lasts
  a week is one issue with a comment a day, not seven. It's closed, with a comment, on the first
    clean run; a run whose DNS lookups couldn't complete leaves it open, since that can't confirm a
    DNS fix. The probe masks addresses in every detail it reports, so its output is safe for a
  public issue.

.EXAMPLE
  ./ops/tools/Publish-ProbeResult.ps1 -ResultFile probe.json -Repository danswett/agent-bridge -RunUrl <url>
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ResultFile,
    [Parameter(Mandatory)] [string] $Repository,
    [Parameter(Mandatory)] [string] $RunUrl,
    # Say what would be done, without touching any issue.
    [switch] $DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$title = 'Ops probe: production relay checks failing'
$probe = Get-Content $ResultFile -Raw | ConvertFrom-Json
$results = @($probe.results)
$actionable = @($results | Where-Object { $_.Status -eq 'FAIL' -or ($_.Status -eq 'WARN' -and $_.Check -eq 'tls certificate') })
$inconclusive = @($results | Where-Object { $_.Status -eq 'WARN' -and $_.Check -eq 'dns' })

$table = @('| Check | Status | Detail |', '|---|---|---|') + @($results | ForEach-Object { "| $($_.Check) | $($_.Status) | $($_.Detail -replace '\|', '\|') |" })
$body = @(
    "Production probe at $($probe.at) ([run]($RunUrl)):"
    ''
    $table
    ''
    'What to do: [ops/runbooks](https://github.com/' + $Repository + '/blob/main/ops/runbooks/README.md). Confirm by hand with `pwsh ops/tools/relay-probe.ps1`. This issue closes itself on the first clean daily run.'
) -join "`n"

$open = gh issue list -R $Repository --state open --label area:ops --search "in:title `"$title`"" --json number,title |
    ConvertFrom-Json | Where-Object title -EQ $title | Select-Object -First 1

if ($actionable.Count -gt 0) {
    $what = ($actionable | ForEach-Object { "$($_.Check) $($_.Status)" }) -join ', '
    if ($DryRun) { "would $(if ($open) { "comment on #$($open.number)" } else { 'open an issue' }): $what"; return }
    $file = New-TemporaryFile
    try {
        Set-Content $file $body -Encoding utf8
        if ($open) { gh issue comment $open.number -R $Repository --body-file $file | Out-Null; "commented on #$($open.number): $what" }
        else { gh issue create -R $Repository --title $title --label area:ops --body-file $file; "opened: $what" }
    }
    finally { Remove-Item $file -ErrorAction SilentlyContinue }
}
elseif ($open -and $inconclusive.Count -gt 0) {
    # A DNS lookup that couldn't complete says nothing about whether a missing or stale record
    # was fixed, so the issue stays open until DNS is actually verified.
    if ($DryRun) { "would leave #$($open.number) open: dns not verified"; return }
    gh issue comment $open.number -R $Repository --body "Not closing: the DNS checks couldn't complete at $($probe.at) ([run]($RunUrl)), so a DNS problem can't be confirmed fixed." | Out-Null
    "left #$($open.number) open: dns not verified"
}
elseif ($open) {
    if ($DryRun) { "would close #$($open.number)"; return }
    gh issue close $open.number -R $Repository --comment "Clean again at $($probe.at) ([run]($RunUrl))." | Out-Null
    "closed #$($open.number)"
}
else { 'clean; no open issue' }
