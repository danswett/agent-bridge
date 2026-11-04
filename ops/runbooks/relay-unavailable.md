# Relay unavailable

**Alerts:** A-AVAIL, A-AVAIL-ORIGIN, A-READY, A-REPLICAS, A-5XX, A-CANARY.
**Users see:** the PWA and apps say they can't reach the relay, machines are listed as
offline, and pushes stop. Hosts keep working locally and reconnect by themselves when the
relay is back (backoff of at most 30 s).

## Confirm (1 min)

```powershell
pwsh ops/tools/relay-probe.ps1 -Target all
```

| Probe says | Means | Go to |
|---|---|---|
| Production `healthz` FAIL with a DNS or TLS error, while the app's own name (`-Url https://agent-bridge-relay.icyflower-4f71155d.westus2.azurecontainerapps.io`) passes | DNS or certificate | [certificate-dns](certificate-dns.md) |
| Both names time out or return 5xx | The app is down | Below |
| `readyz` 503 that persists | Stuck draining: the old revision didn't stop, or the new one never became ready | Below, step 3 |
| All PASS | Probably the alert's own fault, or a regional network path. Check the availability test's per-location results | Watch for 15 min |

Check for a provider outage: [Azure status](https://azure.status.microsoft/status) (Container
Apps, West US 2) and [GitHub status](https://www.githubstatus.com) (sign-in only).

## Mitigate

1. **Was there a relay deploy in the last hour?** (`gh run list -R danswett/agent-bridge -w relay.yml -L 5`)
   If yes, roll back first: [relay-bad-deploy](relay-bad-deploy.md).
2. **See what the platform thinks:**
   ```powershell
   az containerapp show --subscription $sub -g $rg -n $app --query "{state: properties.runningStatus, rev: properties.latestRevisionName, ready: properties.latestReadyRevisionName}" -o table
   az containerapp revision list --subscription $sub -g $rg -n $app --query "[?properties.active].{name: name, health: properties.healthState, running: properties.runningState, replicas: properties.replicas}" -o table
   az containerapp logs show --subscription $sub -g $rg -n $app --type system --tail 50
   ```
   Look for `ProbeFailed`, `OOMKilled`, `ImagePullBackOff`, `CrashLoopBackOff`, or Key Vault
   reference errors (a secret the identity can't read stops a revision starting).
3. **Restart the active revision (Owner).** Every device reconnects once.
   ```powershell
   az containerapp revision restart --subscription $sub -g $rg -n $app --revision <active revision>
   ```
4. **Image pull or secret errors:** the registry or vault role assignment changed. Re-apply
   infra **without rolling the image** (Owner; docs/relay-deploy.md):
   `./infra/deploy.ps1 -Production -ProductionImage <running digest> -ProductionVersion <sha>`, after `-WhatIf`.
5. **Platform outage with no fix in sight:** nothing to fail over to at `lean` (one region,
   by design). Record it in the incident and wait. A second region is a separate relay URL
   (docs/relay-deploy.md "Adding a region"), not a quick failover.

## Confirm recovery

The probe passes; `/healthz` `online` climbs back to its usual count within a few minutes; a
phone sees its machines.

## Find the cause

Queries `revisions`, `warnings` and `deploys` in [queries/relay.kql](../queries/relay.kql),
around the start time. Write the review if it was Sev 1 or 2.
