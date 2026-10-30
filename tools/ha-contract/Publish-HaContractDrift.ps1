<#
    Reports what Test-HaContractDrift.ps1 found on one GitHub issue labelled `parity`, rather
    than only in a failed run nobody reads.

    -Code is the drift check's exit code. 1 or 2 opens the issue, or brings the open one up to
    date (its body always describes the latest run; a comment is added only when agent-ha-bridge
    has moved to a new commit since, so a daily run does not add a comment a day). 0 closes an
    open one with a note that the contract matches again. There is only ever one: it is found by
    its exact title among open `parity` issues.

    What it reads from -OutDir was written by a job that ran agent-ha-bridge's code, so it is
    treated as data only: the commit must be a commit id, the texts are length-capped, and
    nothing of it is ever run or passed through a shell.

    -DryRun prints what would be done instead of doing it.
#>
#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet(0, 1, 2)][int]$Code,
    [Parameter(Mandatory)][string]$OutDir,
    [Parameter(Mandatory)][string]$Repository,
    [string]$RunUrl = '',
    [switch]$DryRun
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$title = 'HA contract drift: agent-ha-bridge main no longer matches the connector''s fixtures'
$label = 'parity'
# GitHub refuses an issue body over 65,536 characters; the full diff is in the run's artifact.
$maxDiff = 50000

$upstream = (Get-Content -LiteralPath (Join-Path $OutDir 'upstream.txt') -Raw).Trim()
if ($upstream -cnotmatch '^[0-9a-f]{40}$') { throw "upstream.txt does not hold a commit id" }
$summary = Get-Content -LiteralPath (Join-Path $OutDir 'summary.md') -Raw
if ($summary.Length -gt 8000) { $summary = $summary.Substring(0, 8000) + "`n`n(cut; the whole of it is in the run's artifact)" }
$marker = "<!-- ha-contract-drift upstream: $upstream -->"

function Invoke-Gh {
    param([string[]]$Arguments)
    if ($DryRun) { Write-Host "would run: gh $($Arguments -join ' ')"; return '' }
    $out = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "gh $($Arguments[0]) $($Arguments[1]) failed ($LASTEXITCODE)" }
    $out
}

$open = @(& gh issue list --repo $Repository --label $label --state open --limit 100 --json 'number,title,body' | ConvertFrom-Json |
    Where-Object { $_.title -ceq $title })
if ($LASTEXITCODE -ne 0) { throw 'gh issue list failed' }
$existing = $open | Select-Object -First 1

if ($Code -eq 0) {
    if ($null -eq $existing) { Write-Host 'Contract unchanged; no drift issue open.'; exit 0 }
    $note = "agent-ha-bridge ``$upstream`` matches the fixtures again, so this is resolved.$(if ($RunUrl) { " ($RunUrl)" })"
    Invoke-Gh @('issue', 'close', "$($existing.number)", '--repo', $Repository, '--comment', $note) | Out-Null
    Write-Host "Closed #$($existing.number)."
    exit 0
}

$body = [Collections.Generic.List[string]]::new()
$body.Add($marker)
$body.Add('The scheduled drift guard (`.github/workflows/ha-contract-drift.yml`) re-recorded agent-ha-bridge''s per-machine Home Assistant contract from its `main` with `tools/ha-contract/Export-HaContract.ps1`, and it no longer matches the fixtures the HA connector is tested against. Until the connector is re-pinned, a machine moved to `agent-bridge-host` may not render or drive exactly as it did.')
$body.Add('')
$body.Add($summary.TrimEnd())
$diffPath = Join-Path $OutDir 'contract.diff'
if (Test-Path -LiteralPath $diffPath) {
    $diff = Get-Content -LiteralPath $diffPath -Raw
    $cut = $diff.Length -gt $maxDiff
    if ($cut) { $diff = $diff.Substring(0, $maxDiff) }
    $body.Add('')
    $body.Add('<details open><summary>Diff, pinned &rarr; upstream</summary>')
    $body.Add('')
    $body.Add('```diff')
    $body.Add($diff.TrimEnd())
    $body.Add('```')
    $body.Add('</details>')
    if ($cut) { $body.Add(''); $body.Add("The diff is cut at $maxDiff characters; the whole of it is in the run's ``ha-contract-drift`` artifact.") }
}
if ($RunUrl) { $body.Add(''); $body.Add("Run: $RunUrl") }
$bodyFile = Join-Path $OutDir 'issue.md'
Set-Content -LiteralPath $bodyFile -Value ($body -join "`n")

if ($null -eq $existing) {
    $url = Invoke-Gh @('issue', 'create', '--repo', $Repository, '--title', $title, '--label', $label, '--body-file', $bodyFile)
    Write-Host "Opened $url"
    exit 0
}

$moved = -not ([string]$existing.body).Contains($marker)
Invoke-Gh @('issue', 'edit', "$($existing.number)", '--repo', $Repository, '--body-file', $bodyFile) | Out-Null
if ($moved) {
    $note = "Still drifting, now at agent-ha-bridge ``$upstream``; the description is updated.$(if ($RunUrl) { " ($RunUrl)" })"
    Invoke-Gh @('issue', 'comment', "$($existing.number)", '--repo', $Repository, '--body', $note) | Out-Null
}
Write-Host "Updated #$($existing.number)$(if ($moved) { ' (agent-ha-bridge moved)' })."
