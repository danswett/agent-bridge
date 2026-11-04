# Incidents

*Part of the [SRE plan](plan.md) §10.*

An incident is anything that stops people using Agent Bridge, or is likely to soon: the relay
unreachable, a bad release on machines, a key exposed. Declaring one is cheap. Declare when
in doubt, and close it if it turns out to be nothing.

## Severities

| Sev | Means | Examples |
|---|---|---|
| 1 | Users can't use the product now | Relay down; a release stops hosts starting on several machines; a signing key exposed |
| 2 | Degraded, or Sev 1 is likely soon | Relay restarting; blobs or push failing; certificate renewal failing; a reconnect storm |
| 3 | A defect with a workaround, limited reach | One dependency's errors; one machine stuck on an old version |

## Roles

There's one operator, the owner, and agent sessions that help. In an incident:
- **The owner decides** anything that touches production: approving a deploy or rollback,
  rotating a key, changing DNS, and anything AGENTS.md reserves for the owner (guard #67:
  host management happens at the machine).
- **An agent session investigates, drafts and prepares.** It runs the read-only probe and
  queries, writes the timeline, and prepares the exact rollback command or PR. It doesn't
  approve the `production` environment and doesn't touch the live agent-ha-bridge.

## The first 15 minutes

1. **Confirm:** `pwsh ops/tools/relay-probe.ps1 -Target all`. Is it the relay, DNS or
   certificate, or a client? Check [githubstatus.com](https://www.githubstatus.com) and
   [Azure status](https://azure.status.microsoft/status) for a provider outage.
2. **Open an issue** with [templates/incident.md](templates/incident.md): label `area:ops`,
   title `Incident: <symptom>`. Times are Pacific. Write what you see, not causes yet.
3. **Look for a change in the last 24 hours:**
   - a relay deploy (relay.yml runs);
   - a Windows release (Releases, and status.md's machine table);
   - a Pages deploy;
   - an `infra/deploy.ps1` run;
   - a DNS edit.

   Most incidents follow one.
4. **Mitigate before you diagnose. Undo the change:**
   - relay: [runbooks/relay-bad-deploy.md](runbooks/relay-bad-deploy.md);
   - Windows release: [runbooks/windows-release.md](runbooks/windows-release.md);
   - PWA: [runbooks/pwa.md](runbooks/pwa.md).
5. **Then follow the alert's runbook** ([alerts.md](alerts.md) names it).
6. Update the issue at each step, with what was done, when and what changed.

## Communication

Users are the owner's machines and devices today. When there are others, a status page is
planned (#587). Until then, the incident issue is the record. Never put an account, device id,
machine address, token or key in it: the repository is public (AGENTS.md "Secrets").

## After

- **Every Sev 1 and Sev 2, and any incident that spends more than 20% of an error budget
  ([slo.md](slo.md)), gets a review** within a week, using
  [templates/review.md](templates/review.md). Write it as a comment on the incident issue, or
  in `ops/reviews/<yyyy-mm-dd>-<slug>.md` if it's long.
- Reviews are blameless. They ask what let it happen and what would have caught it sooner,
  not who.
- Each action item becomes an issue labelled `area:ops`, linked from the review.
- If an alert should have fired and didn't, or fired and wasn't useful, fix the alert in the
  same week ([alerts.md](alerts.md) "Rules for adding an alert").

## Past incidents, before this process

| When (PT) | What | Where it's recorded |
|---|---|---|
| 2026-10-29 | The token key was exposed in a session's output, and rotated at the cutover; every device signed in again | docs/relay-deploy.md "History" |
| 2026-11-02 | A host's HA connector was banned by IP after refused tokens, on DSWETT-ARM8 | docs/status.md "Machines" |
