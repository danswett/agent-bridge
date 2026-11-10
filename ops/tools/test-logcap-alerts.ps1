# Checks that A-LOGCAP (#1309) is built. The Bicep rules load their queries from
# ops/queries/relay.kql, so the text a person pastes into the Logs blade is the text that
# alerts; this guards the pieces that would let the two drift or leave the alert half-wired.
$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '../..')
$kql = Get-Content (Join-Path $root 'ops/queries/relay.kql') -Raw
$mon = Get-Content (Join-Path $root 'infra/modules/monitoring.bicep') -Raw
$main = Get-Content (Join-Path $root 'infra/main.bicep') -Raw
$alerts = Get-Content (Join-Path $root 'ops/alerts.md')
$failures = @()
function Check($ok, $what) { if (-not $ok) { $script:failures += $what } }

foreach ($id in 'logcap-80', 'logcap-overquota') {
  Check ($kql -match "(?m)^// ==== ${id}:") "relay.kql has no '// ==== $id' query"
  Check ($mon.Contains("'${id}:'")) "monitoring.bicep does not load the '$id' query from relay.kql"
}
Check ($mon -match 'loadTextContent\(.*ops/queries/relay\.kql') 'monitoring.bicep does not read ops/queries/relay.kql'
Check (([regex]::Matches($mon, 'Microsoft\.Insights/scheduledQueryRules')).Count -ge 3) 'monitoring.bicep needs two new scheduledQueryRules'
Check ($mon -match '(?s)A-LOGCAP 80 percent.*?severity: 2.*?autoMitigate: true') 'the 80% rule must be severity 2 with autoMitigate'
Check ($mon -match '(?s)A-LOGCAP collection stopped.*?severity: 2.*?autoMitigate: true') 'the stopped rule must be severity 2 with autoMitigate'
Check (([regex]::Matches($mon, 'ops/runbooks/log-cap\.md')).Count -ge 2) 'both rule descriptions must name ops/runbooks/log-cap.md'
Check ($main -match 'logDailyCapGb:\s*logDailyCapGb') 'main.bicep does not pass logDailyCapGb to the monitoring module'
Check (@($alerts | Where-Object { $_ -match '^\| A-LOGCAP ' -and $_ -match '\| Built \|' }).Count -eq 1) 'ops/alerts.md does not list A-LOGCAP as Built'

if ($failures) { $failures | ForEach-Object { Write-Host "FAIL: $_" }; exit 1 }
Write-Host 'A-LOGCAP checks passed'
