# Log Analytics cap

**Alert:** A-LOGCAP, two rules in `infra/modules/monitoring.bicep`, both severity 2 by email:
- **80% of the daily cap:** today's billable ingestion (since midnight UTC) passed 0.8 x
  `logDailyCapGb`. It's a warning: there's still headroom, so act now. Checked every 15 minutes;
  the `Usage` table lags ingestion by up to an hour.
- **Collection stopped:** the workspace logged `OverQuota` in the last hour, so the cap was hit.
  Both rules use the text of `logcap-80` and `logcap-overquota` in
  [queries/relay.kql](../queries/relay.kql); paste them into the Logs blade to see what they see.
  They clear on their own once the condition passes (the stopped rule can take up to an hour).

The workspace `log-agent-bridge-relay-wus2` caps ingestion at **0.1 GB a day**
(`logDailyCapGb` in infra/main.bicep). Once the cap is hit, **nothing more is ingested until
the next reset**: relay logs, and every log-based alert, go blind for the rest of the day.
The cap resets at a fixed hour UTC, shown in the workspace's Usage and estimated costs blade.
The usual cause is a reconnect storm or a scan, which is exactly when logs are needed.

## Confirm

Queries `ingestion` (which table is loud) and `cap-hit` in [queries/relay.kql](../queries/relay.kql), then
`connections` to see what's logging.

## Mitigate

1. **Find the source.** Is it relay console logs (`ContainerAppConsoleLogs_CL`) or platform
   logs (`ContainerAppSystemLogs_CL`)? Most likely connects and disconnects in a storm: see
   [reconnect-storm](reconnect-storm.md) and fix the cause.
2. **If you need logs today (Owner):** raise the cap for the day. It's a cost decision: a few
   dollars per GB at pay-as-you-go.
   ```powershell
   az monitor log-analytics workspace update --subscription $sub -g $rg -n log-agent-bridge-relay-wus2 --quota 0.5
   ```
   Put it back to 0.1 afterwards, or re-apply infra. `deploy.ps1` re-states the parameter.
3. While logs are capped, metrics and availability tests still work (OBS-1, ALR-2), and so
   does the probe.

## Afterwards

OBS-3 (#571) samples connect and disconnect lines during a storm, so one storm can't cap the
workspace: past `Relay__LogLinesPerMinute` lines in a minute (default 120, 0 logs every line) the
relay drops the rest and writes one `N connect/disconnect lines suppressed in the last minute`
line when the next window's first line arrives (#1310). Warnings, errors and the `relay.connects`
and `relay.disconnects` counters are never sampled, so use the counters for a storm's true size.
If the cap is hit in normal use, raise `logDailyCapGb` in a PR, with the measured
daily volume.
