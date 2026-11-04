# Relay resources: restarts, memory, CPU, sockets, latency

**Alerts:** A-RESTART, A-MEM, A-CPU, A-SOCKETS, A-FORWARD, A-CANARY-SLOW.

The replica is 0.25 vCPU and 0.5 GiB, and holds at most 10,000 sockets
(`Relay:MaxConnections`). Memory per idle socket is the binding limit (design §5.9). There's
deliberately **no scale-out**: a second replica would split presence
(docs/relay-deploy.md "Why one replica").

## Confirm

```powershell
$id = az containerapp show --subscription $sub -g $rg -n $app --query id -o tsv
az monitor metrics list --resource $id --metric WorkingSetBytes UsageNanoCores RestartCount Replicas --interval PT5M --offset 6h -o table
curl.exe -s https://relay.bad-duck.com/healthz    # "online": sockets now
az containerapp logs show --subscription $sub -g $rg -n $app --type system --tail 50   # OOMKilled, ProbeFailed
```

## Mitigate

| What you see | Do |
|---|---|
| Restarts right after a deploy | [relay-bad-deploy](relay-bad-deploy.md) |
| `OOMKilled`, or memory climbing steadily with a flat `online` count | A leak. Roll back if it began with a deploy. Otherwise restart the revision (**Owner**) to buy time, and open a P1 bug with the memory curve |
| Memory high and `online` high | Real load. Short term: **Owner** raises the size to 0.5 vCPU / 1 GiB with `infra/deploy.ps1` (a parameter in `regions/westus2.bicepparam`; a new revision, every device reconnects once). Long term: CAP-1 (#585) |
| CPU high with normal `online` | A hot loop or a frame flood. Check `reconnect-storm` and refusals ([reconnect-storm](reconnect-storm.md)); per-socket frame limits should be closing abusers (4008) |
| Forward latency or canary slow, resources normal | Probably the network or Azure. Check the availability test's per-location latency; nothing to do at `lean` |
| Sockets > 60% of the cap | Not urgent. Start the capacity review (CAP-1) and the `standard` plan (design §5.8, S9) |

## Find the cause

A heap dump needs a debug build and a container with tools; the production image is minimal.
Reproduce on staging under load instead (CAP-1's load generator), and compare the `deploys`
and `session-lengths` queries before and after the change.
