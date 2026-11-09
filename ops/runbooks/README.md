# Runbooks

*Part of the [SRE plan](../plan.md). Start an incident with [incidents.md](../incidents.md).*

One page per kind of alert. Each says how to confirm, how to mitigate (undoing the last
change comes first), and how to find the cause. [alerts.md](../alerts.md) names the runbook
for each alert.

| Runbook | Alerts |
|---|---|
| [relay-unavailable](relay-unavailable.md) | A-AVAIL, A-REPLICAS, A-5XX, A-CANARY |
| [relay-bad-deploy](relay-bad-deploy.md) | A-BAKE; any fault right after a relay deploy |
| [relay-resources](relay-resources.md) | A-RESTART, A-CRASHLOOP, A-MEM, A-CPU, A-SOCKETS, A-FORWARD, A-CANARY-SLOW |
| [reconnect-storm](reconnect-storm.md) | A-STORM, A-RATELIMIT, A-REFUSALS |
| [relay-dependencies](relay-dependencies.md) | A-ROSTER, A-BLOB, A-PUSH, A-SIGNIN |
| [certificate-dns](certificate-dns.md) | A-CERT, A-DNS; A-AVAIL red while the app's own name is healthy |
| [security-events](security-events.md) | A-CHANGE; a key or token exposed |
| [log-cap](log-cap.md) | A-LOGCAP |
| [cost](cost.md) | A-BUDGET |
| [relay-messaging](relay-messaging.md) | None yet; the fleet's messaging cut-over from Home Assistant to the relay, and its rollback |
| [windows-release](windows-release.md) | A-RELEASE; a release breaking machines |
| [pwa](pwa.md) | A-PAGES; a PWA deploy breaking phones |

## Before any of them

Every Azure command names the subscription. The owner's Azure CLI profile for this project is
separate:

```powershell
$env:AZURE_CONFIG_DIR = "$HOME\.azure-agentbridge"
$sub = '0e38f344-908e-4ff3-88ac-4d499a1f51f8'; $rg = 'rg-agent-bridge-relay'; $app = 'agent-bridge-relay'
az account show --subscription $sub --query name -o tsv   # signed in? If not: az login
```

**Read-only** commands (`show`, `list`, `logs`, `metrics`, queries, the probe) are safe for
anyone, agent sessions included. **Anything that changes production** (a restart, a deploy or
rollback, a key, DNS) is the owner's call (AGENTS.md "Relay deploys"); a runbook marks those
steps **Owner**.

Never paste a key, token, connection string or address into an issue or a log. If a command
would print one, send its output to a file and don't read it back.
