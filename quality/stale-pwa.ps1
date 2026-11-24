#Requires -Version 7
# The stale-PWA alarm's decision (#4369 Fix 3, Part of #4104): is the PWA that app.slipgates.com serves behind main?
#
# On Nov 12 to 24 the site served a build 12 days old while every Pages run showed green (a fail-closed rollback fence held the deploys, #4369), and nothing noticed:
# the PWA showed no update button and no Notifications filters. This is the check that notices. A scheduled workflow (stale-pwa.yml) makes the two read-only GETs
# and gives this script the results as files, so the decision runs offline and is tested on fixed inputs (test-stale-pwa.ps1); it holds no token and makes no request.
#   -ConfigPath  the served https://app.slipgates.com/config.js: its APP_VERSION is the deployed commit's 40-hex sha and its BUILT_AT that commit's time (pages.yml)
#   -NewestPath  JSON of main's newest commit that changed pwa/: { "sha": "<40 hex>", "date": "<ISO 8601>" }
# FAILS (::error, exit 1) when the served build's time is more than -MaxAgeHours (24) before that commit's, and FAILS CLOSED when either file is missing, empty,
# not what it should be, or names no PWA-changing commit at all: an unreadable site or history is never read as fresh. A served build that IS main's newest PWA
# change, or newer (a later non-PWA main commit was deployed), is fine. A deliberate rollback looks the same as a stale site: it alarms, which is the point of #4369.
# Values are checked against their exact shape before they are printed, so nothing a served file says reaches a workflow command.
param(
    [string]$ConfigPath = '',
    [string]$NewestPath = '',
    [double]$MaxAgeHours = 24
)
$ErrorActionPreference = 'Stop'
$invariant = [Globalization.CultureInfo]::InvariantCulture

function Stop-Unread([string]$what) {
    Write-Output "::error title=stale-PWA alarm::$what; failing closed, because an unreadable site or history must never read as fresh (#4369)"
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "stale-PWA alarm: **unreadable**, $what" }
    exit 1
}
function Read-Text([string]$path, [string]$what) {
    if ([string]::IsNullOrWhiteSpace($path) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) { Stop-Unread "$what was not read (no file)" }
    $text = Get-Content -LiteralPath $path -Raw
    if ([string]::IsNullOrWhiteSpace($text)) { Stop-Unread "$what was empty" }
    $text
}
function ConvertTo-Instant([string]$text) {
    $at = [DateTimeOffset]::MinValue
    if ($text -match '^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?(Z|[+-]\d{2}:\d{2})$' -and
        [DateTimeOffset]::TryParse($text, $invariant, [Globalization.DateTimeStyles]::AssumeUniversal, [ref]$at)) { return $at }
    $null
}

# What is served.
$config = Read-Text $ConfigPath 'config.js'
$servedSha = if ($config -match '(?m)^export const APP_VERSION = "([^"\r\n]*)";\s*$') { $Matches[1] } else { '' }
if ($servedSha -cnotmatch '^[0-9a-f]{40}$') { Stop-Unread "config.js has no APP_VERSION that is a 40-hex commit (an unstamped or foreign build)" }
$builtText = if ($config -match '(?m)^export const BUILT_AT = "([^"\r\n]*)";\s*$') { $Matches[1] } else { '' }
$builtAt = ConvertTo-Instant $builtText
if ($null -eq $builtAt) { Stop-Unread "config.js has no BUILT_AT that is an ISO 8601 time" }

# What main says is newest.
$json = Read-Text $NewestPath "main's newest PWA-changing commit"
try { $newest = $json | ConvertFrom-Json -ErrorAction Stop } catch { Stop-Unread "main's newest PWA-changing commit was not JSON" }
if ($newest -is [array] -or $null -eq $newest -or $newest -isnot [pscustomobject]) { Stop-Unread "main has no newest PWA-changing commit to compare with" }
$newestSha = [string]$newest.sha
if ($newestSha -cnotmatch '^[0-9a-f]{40}$') { Stop-Unread "main's newest PWA-changing commit has no 40-hex sha" }
# ConvertFrom-Json turns an ISO 8601 string into a DateTime itself; any other type, and any string that is not ISO 8601, is refused.
$newestAt = if ($newest.date -is [datetime]) { [DateTimeOffset]$newest.date } elseif ($newest.date -is [string]) { ConvertTo-Instant $newest.date } else { $null }
if ($null -eq $newestAt) { Stop-Unread "main's newest PWA-changing commit has no ISO 8601 date" }

$lag = ($newestAt - $builtAt).TotalHours
$lagText = $lag.ToString('0.0', $invariant)
$served = $servedSha.Substring(0, 8); $main = $newestSha.Substring(0, 8)
if ($servedSha -ceq $newestSha -or $lag -le $MaxAgeHours) {
    $line = if ($servedSha -ceq $newestSha) { "app.slipgates.com serves $served, main's newest PWA change" }
            else { "app.slipgates.com serves $served (built $($builtAt.ToString('u'))), $lagText h behind main's newest PWA change ${main}: within the $MaxAgeHours h limit" }
    Write-Output "::notice title=stale-PWA alarm::$line"
    if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "stale-PWA alarm: ok, $line" }
    exit 0
}
$line = "app.slipgates.com serves $served (built $($builtAt.ToString('u'))), $lagText h older than main's newest PWA change $main (committed $($newestAt.ToString('u'))); the limit is $MaxAgeHours h. Pages may be held or failing: see the Pages workflow's gate and #4369"
Write-Output "::error title=stale-PWA alarm::$line"
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value "stale-PWA alarm: **STALE**, $line" }
exit 1
