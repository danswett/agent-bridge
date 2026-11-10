# Agent usage read: where fleet usage figures come from without Home Assistant (#1405)

**Status: design note. No code changes with it.** Slice S6 of the Home Assistant retirement
epic (#1396). The fleet's budget governor reads Home Assistant (HA) usage sensors today: the
Copilot plan percentage and the Codex weekly figure. This note says what replaces them. Every
claim was checked against the code at the commit this note was written from; file names are
given so a reviewer can check them.

## Owner summary

* **Slipgates already has the figures.** The host reads each agent's allowance and serves it
  on the `usage.get` and `usage.subscribe` ops (`src/AgentBridge.Protocol/Usage.cs`,
  `src/AgentBridge.Host/UsageService.cs`, contract in `docs/protocol/v1-usage.md`). The HA
  sensors are fed from the same readings, so the governor can read the source instead of HA.
* **A process inside an agent session may read them today**, on the machine's local pipe.
  `LocalApi.AgentCapabilities` is `sessions.read` and `usage`, and nothing else
  (`src/AgentBridge.Host/LocalApi.cs:26`).
* **There is no dollar spend figure anywhere.** Slipgates reports how much of an allowance is
  used, as a percent, and for Copilot also credits used and the limit (`AIC`). If the governor
  needs money, nothing today provides it (see "Spend").
* **One small code slice is needed** (about 250 lines, under the 400 limit): a way for a
  script that is not a protocol client to read the figures, plus the comparison check. A relay
  read like #1400's is not needed, for the reason in "Fleet view".

## Where each figure comes from

Both come from the host's own reading of the vendor, then through `usage.get`. Intervals are
the defaults in `UsageOptions` (`src/AgentBridge.Host.Core/Usage/UsageOptions.cs`), changed by
`usage.intervalSeconds` and `usage.idleIntervalSeconds` (`docs/host-config.md`).

| Figure | Window in the report | Read from | How often the host reads |
|---|---|---|---|
| Copilot plan percentage | agent `copilot`, window key `plan` (label `Plan`); `percent` = `100 * used / limit`, with `used`, `limit`, unit `AIC`, `startsAt`, `resetsAt` (`CopilotUsage.cs:165`) | `GET https://api.github.com/copilot_internal/user`, `premium_interactions`. If that fails, Copilot CLI's own cache file, marked `source: cache` with the failure in `error` | Every 120 s while a Copilot session is open on the machine, every 900 s otherwise; never closer than 30 s apart |
| Codex weekly figure | agent `codex`, window key `secondary` (label `Weekly`); `percent` = Codex's `usedPercent` (`CodexUsage.cs:370`) | Codex's own app-server (`account/rateLimits/read`, `source: api`), or the newest `rate_limits` in Codex's recent transcripts (`source: transcript`); the newer wins | Same 120 s and 900 s. The app-server is asked only when the reading is stale: a probe at most every 5 minutes with a Codex session open, every 15 minutes or more without |

A subscriber gets `usage.update` when anything shown changed (at most one per 15 s), and at
least every 30 minutes for an unchanged reading. So the freshest a governor can be is the
host's read interval above plus a few seconds. `measuredAt` says when the vendor was asked;
the governor should use it, not its own receive time, and treat a reading over an hour old as
stale, as every client does.

Look windows up by agent and key, never by position. Read `secondary` as weekly only while its
label is `Weekly` (the host labels by duration, `CodexUsage.cs:356`). Codex has no `startsAt`
because its windows roll. `percent` is always "how much is gone"; the host converts Copilot's
"remaining".

## What an agent credential may read, and may not

As the code stands (`LocalApi.cs`, `RemoteAuthorization.cs`, `AgentCredentials.cs`):

**May read, on the local pipe only:**
* `usage.get` and `usage.subscribe`, including `{ "shared": true }`, which adds the readings
  the host keeps from the account's other machines (below). A launched agent's credential has
  scope `read` (`AgentCredentials.Prepare`), and `agent-launch-scope.md` lists `usage.*` in it.
* Alongside: `sessions.list`, `session.get`, `sessions.subscribe`, `launch.options`.

**May not:**
* Act or manage: `host.*` management ops, `host.peers.*`, `update.*`, and answering, replying,
  stopping or launching sessions (`LocalApi.Acts` and the `host.` guard). Those are for the
  person.
* Read usage over the relay. A person's device may; an agent has no principal on the relay. The
  only relay usage path is host to host (`HostOperations` in `RemoteAuthorization.cs`),
  carrying `hello` and `usage.get` and nothing else.
* Change what is read. `usage.publish`, the paces and `usage.copilotAccounts` are host config,
  set by the person; config changes are refused to agents (guard #67).
* Obtain any vendor credential. Claude's and Codex's are never opened by the host, and Copilot's
  token is not handed out. A report holds percentages, amounts, times, the account name or an
  opaque key, and the plan.

A script that is neither the app nor an agent (another process of the user's) may also call the
usage ops on the pipe, as it may the sessions ops, and no acting ops. A governor run as a plain
scheduled script gets exactly the same read.

**Decision for the owner:** keep usage as a read every launched agent credential holds, as
today. `agent-principals.md` once pencilled usage as a later, separate grant, but the code
already grants it. Narrowing it to the governor alone would change `AgentCapabilities` and the
scope, would be a sensitive slice, and is not needed for this note. Recommended default: leave
it.

## Fleet view without a relay read

The governor wants the account's figures, not one machine's. `usage.get` with `shared: true`
already returns that from the local host: its own readings, then the freshest readings it keeps
from the account's other machines, each marked `readBy` (`UsageShare.Serve`; at most 16 entries
and 48 KiB). Hosts ask each other every 5 minutes (`UsagePeers.cs`, `v1-usage.md` "Shared
readings") over channels between the account's own devices. So no new relay read is needed.

What the governor must know:
* A peer reading can be about 5 minutes older than that machine's own. Use `readBy` and
  `measuredAt`.
* A reading with **no account** is never taken from a peer (#200). Codex's transcript reading has
  no account key; only an app-server reading made while its login held steady has one. So the
  Codex weekly figure is fleet-wide only when a keyed reading exists. Otherwise the governor sees
  the local machine's reading only. "No figure from machine B" is not "nothing used on B".
* A host with `usage.publish` false offers no `usage` at all.
* A failure is a reading with no windows and an `error`; it is not "zero used". An agent that
  has never run on the machine is absent, not zero.

## How the governor's figures are compared with the app's

The app and PWA show one merged report (`pwa/js/usage.js`, `UsageViewModel`): group by agent and
account, a reading beats a failure, then the latest `measuredAt` wins (`UsageShare.Outranks`).
The governor must choose its figure by the same rule, or the two can differ because they picked
different machines' readings, not because anything is wrong.

The check passes only if all of this holds:

1. Take **ten readings**, at least a minute apart.
2. For each, record the governor's figure (agent, window key, `percent`, `measuredAt`, `readBy`)
   and, within 60 seconds, the figure the app shows for the same allowance (read off its merged
   row; for an automated run, `usage.get` with `shared: true` on the app's own host).
3. A reading passes when the two `percent` values differ by **1.0 point or less** and the two
   `measuredAt` values are within one read interval. A pair where the vendor figure changed
   between the two reads is retried once and not counted.
4. All ten pass, for Copilot `plan` and Codex `Weekly` each.
5. The result goes on the issue that retires the HA sensor, as evidence for the usage row of
   the parity checklist and the retirement gate.

The host reports to a tenth and the app shows what it is sent, so the one-point margin covers
rounding; it is there for different machines and read times, not different arithmetic. During
the HA-off rehearsal the governor's figure is also compared once with the HA sensor's, to the
same margin, while both run. After HA is off, the app is the reference.

## Spend

Slipgates has no dollar or token spend figure. Windows carry `percent`, and `used`, `limit` and
`unit` (`AIC`) only where the vendor gives amounts, which today is Copilot. Nothing in
`src/AgentBridge.Protocol/Usage.cs` carries money or tokens. If the governor's "spend" is these
percentages, nothing more is needed. If it needs dollars or tokens per session, that is a new
reading from the vendors, not a repackaging, and it is not sized here. Product should say
which the governor uses before S6 is cut.

## Code slice needed

The figures are exposed, but a search of `src` and `tools` found no command or helper for a
script to read them; the PWA, the app and the HA connector are the only readers. One slice,
estimated **about 250 changed lines (size M), under 400**:

1. A read-only helper any script can call: open the local pipe, `hello`, `usage.get` with
   `shared: true`, print JSON, choosing one figure per allowance by the apps' rule. Reuses
   `UsageShare` and the protocol records; no new op. About 150 lines with tests.
2. The ten-pair comparison above as a script or test tool that writes the pairs and a verdict.
   About 100 lines.

Not in the slice: changing what an agent credential may do, relay reads, new readings, spend,
and the HA connector. It does not touch `sensitive_paths` (no protocol, relay, auth or token
change) unless the owner narrows the scope above, which would make it sensitive.

## Not verified

* That the governor's host has peers connected. Without them it sees only its own machine's
  readings, which is what the HA sensors gave from that machine.
* Live read intervals on the owner's machines. These are code defaults; config may differ, and
  the comparison above measures the real result.
* Whether the host classifies a plain scheduled script as an agent or as another caller of the
  user's. Both may call usage, so the result is the same.
