# Cost

**Alert:** A-BUDGET.

The stamp runs on a Visual Studio subscription with a monthly credit. **If the credit runs out,
Azure disables the subscription and the relay stops.** Cost is an availability risk here, not
just money. Expected at `lean`: about $15–30 a month (design §11).

## Confirm

Cost Management in the portal, scoped to `rg-agent-bridge-relay`, grouped by resource and then
by meter. Or:

```powershell
az consumption usage list --subscription $sub --start-date (Get-Date).AddDays(-30).ToString('yyyy-MM-dd') --end-date (Get-Date).ToString('yyyy-MM-dd') --query "[?contains(instanceId, 'rg-agent-bridge-relay')].{resource: instanceName, meter: meterDetails.meterName, cost: pretaxCost}" -o table
```

## Usual suspects

| Meter | Why it grows | Do |
|---|---|---|
| Log Analytics ingestion | A storm or a chatty build | [log-cap](log-cap.md) |
| Storage (blobs) transactions or egress | Large transfers, or abuse of signed URLs | Check `relay.blobs` and the store's metrics. The lifecycle rule deletes everything after a day |
| Container Apps vCPU or memory | Someone raised the size, or staging stopped scaling to zero | Compare with `regions/westus2.bicepparam`. Check staging's replica count |
| Registry | Basic tier, fixed | Nothing |
| Anything in the **old free-trial stamp** | It's in its own subscription and isn't counted here, but it costs about $19 a month of that trial's credit | Decommission it once it reads 0 online for a day: `pwsh ops/tools/relay-probe.ps1 -Target old`. The steps are in docs/relay-deploy.md "Decommissioning the old stamp" (**Owner**) |

## If the credit is nearly gone

**Owner:** move the subscription to pay-as-you-go, or reduce cost (staging to zero, log cap
down), before it's disabled. Design §11 notes that Visual Studio credits are for dev and test,
not for a service others depend on; that decision is still open.
