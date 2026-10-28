# Bridge Protocol v1: usage and machine status

How much of each coding agent's allowance is gone, and what a client says about a machine
beside its name. An extension of [v1-sessions.md](v1-sessions.md): the same channel,
framing and message rules. C# records are in `src/AgentBridge.Protocol/Usage.cs`, and the
PWA mirrors them in `pwa/js/usage.js`.

It carries what agent-ha-bridge's usage sensors and status card show (its
`hooks/daemon-usage.ps1` and the card's `AgentBridgeUsageCard`), read from the same places
at the same pace.

## Machine status

A machine is **online** while it is in the relay's presence list, as before. `hello`'s
`machine` gains two fields:

```json
{ "id": "53d1997f…", "name": "DSWETT-DEV-VM1", "os": "windows", "hostVersion": "0.1.0", "dev": true, "sessions": 3 }
```

| Field | Type | Notes |
|---|---|---|
| `dev` | bool? | `true` for a host built from a working copy rather than released. The version only moves when a release is cut, so two hosts days apart in features would otherwise read the same. Absent means released, or a host from before this field |
| `sessions` | number? | Live sessions when `hello` was answered. A client subscribed to the machine's sessions counts them itself instead |

A client shows a machine as `3 sessions · 0.1.0 (dev)`, and an offline one as `offline`.
It can learn this about a machine it is not viewing with a short channel: `hello`, then
`usage.get` if offered, then close. The PWA does that once a minute for each other online
machine while it is in front, and never more often.

## Capability

`hello`'s `capabilities` includes `usage` when the host offers `usage.get` and
`usage.subscribe`. A client hides usage without it.

## Ops

### `usage.get`

Request `data`: `{}`. Result: a [Usage](#usage) report, as the host last read it.

### `usage.subscribe`

Request `data`: `{}`. Result: the current Usage. Then, until the channel closes, the host
sends `usage.update` with the whole Usage whenever what a client shows changes:

* a reading changed in anything a client shows but when it was taken (a window's percent,
  amounts or ends, the source, the error, the account or plan), or
* an unchanged reading was last sent 30 minutes ago, so its `measuredAt` stays inside the hour
  after which a client calls it stale.

Updates follow the host's reads, which are minutes apart (below), and are at most one per
15 s. A Usage is small (a few hundred bytes per agent). Calling `usage.subscribe` again on
a channel replaces the subscription: the result is the current Usage, and later changes
arrive once. `seq` is the channel's, shared with session events.

## Types

### Usage

`{ "agents": [AgentUsage…] }`, at most 8 agents and 24 KiB as sent. An agent that has never
run on the machine is absent: "no data" and "none used" look the same as a bar and mean
opposite things.

### AgentUsage

| Field | Type | Notes |
|---|---|---|
| `agent` | `"copilot"` \| `"claude"` \| `"codex"` | Read as `unknown` otherwise, and shown by `label` |
| `label` | string | `GitHub Copilot`, `Claude Code`, `Codex`. At most 64 bytes |
| `account` | string? | The account as the vendor names it: the Copilot login. At most 100 bytes |
| `accountKey` | string? | For a vendor that names no account (Claude): an opaque key, the first 12 hex digits of SHA-256 of the account and organisation ids. It tells two allowances apart and says nothing else |
| `plan` | string? | e.g. `enterprise`, `max`, `plus` |
| `source` | `"api"` \| `"cache"` \| `"transcript"` \| `"none"` | Where the figure came from. Read as `none` otherwise |
| `windows` | Window[] | At most 6. Empty for a failure |
| `error` | string? | Why there is no fresh reading. Beside windows, the reading is an older fallback (Copilot's cache). At most 512 bytes |
| `measuredAt` | time | When the figure was taken. For a failure, when the attempt was made |

### Window

| Field | Type | Notes |
|---|---|---|
| `key` | string | Stable per agent: `plan` (Copilot), `session`, `weekly_all`, `weekly_opus` (Claude), `primary`, `secondary` (Codex) |
| `label` | string | `Plan`, `Session (5h)`, `Weekly`, `5h limit`… |
| `percent` | number | How much is **gone**, 0 and up, to a tenth. Vendors disagree (Copilot reports what remains), so hosts normalise |
| `used`, `limit` | number? | When the vendor gives amounts (Copilot) |
| `unit` | string? | `AIC` (AI credits) |
| `startsAt` | time? | When the window began, when known: Copilot's month, Claude's week. Never guessed from a length: Codex reports none, because its windows roll |
| `resetsAt` | time? | When it resets |

## What a client shows

Agent-ha-bridge's usage card, kept exactly:

* **One row per allowance, not per machine.** Readings from every machine the client knows
  are grouped by `agent` and account (`accountKey`, else `account` ignoring case). In a
  group, a reading (windows present) outranks a failure however new, and then the latest
  `measuredAt` wins. A failure newer than the reading lends it its `error`.
* **Rows in order of the fullest window**, highest first. The folded summary is
  `GitHub Copilot 79% · Claude Code 12%`, each figure in its own colour.
* **Colour by percent:** 90 and up critical (red), 75 and up warning (amber), else normal.
* **Pace:** with both `startsAt` and `resetsAt`, elapsed is `(now − startsAt) / (resetsAt −
  startsAt)`, clamped to 0–100%, worked out when drawn. A mark on the bar at elapsed, a
  line `│ even pace 45%`, and a verdict: more than 10 points ahead is `N% ahead of pace`
  (warning colour), more than 10 under is `N% under pace`, else `on pace`.
* **Resets:** `resets in 25d`, `in 4h`, `in 12m` (rounded), or `due` once past.
* **Amounts:** `1,183,313 / 1,500,000 AIC` when `used` and `limit` are given.
* **Age:** `just now` under 90 s, then minutes, hours, days ago. Older than an hour is
  stale, shown in the warning colour.
* **Errors:** shown unless the row has a reading that is not stale: a failed read beside a
  reading taken minutes ago is noise, but a failure is all there is to say when there is
  no reading or it is old.
* Times move on without updates: a client redraws relative times at least every 30 s while
  visible.

## Where a host gets each figure

| Agent | Source | Credential |
|---|---|---|
| Copilot | `GET https://api.github.com/copilot_internal/user`, `quota_snapshots.premium_interactions`: `entitlement` is the limit, `entitlement − remaining` is used, `quota_reset_date_utc` the reset, a month before it the start. Failing that, the CLI's own cache, `copilot-user-cache.json` (the newest entry for the login), with the failure as `error` | The account is the login in `~/.copilot/config.json` `loggedInUsers` (`lastLoggedInUser` when there are several). Its token, in the CLI's own order: `COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, `GITHUB_TOKEN`, the CLI's stored login (Windows Credential Manager, `https://github.com:<login>.copilot-cli`), then `gh auth token --hostname github.com --user <login>` |
| Claude | `GET https://api.anthropic.com/api/oauth/usage`, `limits[]`: `kind`, `percent`, `resets_at`; `seven_day_breakdown.window_started_at` starts the weekly windows | `claudeAiOauth.accessToken` in `~/.claude/.credentials.json`. The account key from `~/.claude.json` `oauthAccount` |
| Codex | The newest `rate_limits` (by its event's time) in the 5 most recently written transcripts under `~/.codex/sessions`, searching the last 8 MiB of each: `primary` and `secondary`, `used_percent`, `window_minutes`, `resets_at`, `plan_type` | None: no request leaves the machine |

**Pace.** A host asks about an agent every 2 minutes while a session of it is open on the
machine, and every 15 minutes while none is: the allowance belongs to the account, and only a
machine spending it sees it move. A session opening or closing brings the next ask forward,
but never within 30 s of the last. While agent-ha-bridge's daemon runs for the same user it
already asks every 2 minutes, so a host then asks only every 15. Each request is tried up to 3
times, 400 ms and 800 ms apart, for network failures, timeouts (15 s), 408, 429 and 5xx; any
other refusal is an answer and is not retried.

**Leaving the agents' credentials alone.** A host only reads. It never refreshes a token:
Claude's expires about hourly and Anthropic retires a refresh token once used, so a refresh
from here would sign Claude Code out. An expired Claude token is not spent, a 401 is not an
error, and the last reading simply ages. A Copilot token is sent only to `api.github.com`,
and only for a `github.com` login. A `gh` that does not answer within 5 s (a keyring holding
it at a prompt) is not asked again for 6 hours, unless a Copilot session opens on the machine
at least 15 minutes after it stopped answering. A host never reads macOS's keychain item for
Copilot: reading it raises a prompt that the CLI's next token refresh brings back.

## Privacy

Tokens never leave the machine except to the vendor that issued them, and are never logged
or written anywhere. A host's log names agents, sources and reasons, never an account. The
account a reading belongs to travels only inside the end-to-end channel, to the account's
own devices.

## Not carried over yet

* **Further Copilot logins** (agent-ha-bridge's `usage.copilotAccounts`). A host has no
  configuration yet; the report already holds one entry per account, so they need no
  contract change.
* **Standing down for another machine** reading the same account (agent-ha-bridge #142).
  That bridge sees every machine's readings in Home Assistant; hosts here do not see each
  other's. Each host asks for itself, at the pace above.
* **macOS keychain opt-in** (agent-ha-bridge's `usage.keychain`), for the same lack of
  configuration.
