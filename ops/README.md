# ops: running Agent Bridge in production

The SRE plan, and the tools to carry it out, for the relay and everything else we deploy
(the PWA, Windows releases, the fleet that updates from them). Tracking issue:
[#568](https://github.com/danswett/agent-bridge/issues/568).

| | |
|---|---|
| [plan.md](plan.md) | **Start here.** What we operate, the principles, and the design for observability, alerting, safe deploys and rollouts, auto-mitigation, capacity, fleet and cost, with the roadmap and an issue per slice |
| [slo.md](slo.md) | SLIs, SLOs and the error-budget policy |
| [alerts.md](alerts.md) | The alert catalog: id, severity, condition, runbook, issue and status |
| [incidents.md](incidents.md) | Severities, roles, the first 15 minutes, reviews |
| [runbooks/](runbooks/README.md) | One page per kind of alert |
| [templates/](templates/) | Incident and review templates |
| [queries/relay.kql](queries/relay.kql) | The relay's Log Analytics queries: the one source, deployed as saved searches under the category `Slipgates relay` (`infra/modules/saved-searches.bicep`). Edit the file, never a copy in the portal; adding or removing a `// ==== <id>: <description>` block needs no other edit |
| [tools/relay-probe.ps1](tools/relay-probe.ps1) | Read-only outside-in check of a relay |

## The probe

```powershell
pwsh ops/tools/relay-probe.ps1                   # production (relay.bad-duck.com)
pwsh ops/tools/relay-probe.ps1 -Target production-names   # both production names; the daily ops-probe runs this
pwsh ops/tools/relay-probe.ps1 -Target all       # production, staging and the old stamp
pwsh ops/tools/relay-probe.ps1 -ExpectVersion <sha>   # after a deploy
```

It checks health, readiness, the serving version, the push key, CORS for the PWA's two origins (danswett.github.io and app.slipgates.com), the
TLS certificate's expiry, and, for each production name, the CNAME and `asuid` TXT records that
the certificate's renewal depends on. It sends only anonymous requests, prints no addresses, and
exits 0 (pass), 1 (fail) or 2 (warnings). Every row names its Target (the host). The daily run shows it in the run summary and the `area:ops` issue table. Any session can run it against production.

## Rules here

Everything in AGENTS.md applies. In particular:
- **No identities.** No account, device, address, token or key in any metric, log, query,
  alert, issue or runbook output (design §5.10, AGENTS.md "Secrets").
- **Production changes are the owner's.** Runbooks mark those steps **Owner**. Agent sessions
  run read-only commands and prepare the rest.
- Infra for monitoring goes in `infra/` (Bicep) and is applied by `infra/deploy.ps1`, like the
  rest of the stamp. This folder holds the plan, procedures and operator tools, not deployed
  resources.
