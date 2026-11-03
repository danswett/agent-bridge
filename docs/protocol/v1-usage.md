# Bridge Protocol v1: usage and machine status

How much of each coding agent's allowance is gone, and what a client says about a machine
beside its name. An extension of [v1-sessions.md](v1-sessions.md): the same channel,
framing and message rules. C# records are in `src/AgentBridge.Protocol/Usage.cs`, and the
PWA mirrors them in `pwa/js/usage.js`. Test vectors for the records are in
[`vectors/usage.json`](vectors/README.md); [v1-sessions.md](v1-sessions.md#test-vectors) says
how to regenerate them.

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

`usage.shared` (#172), offered with `usage`, says the host also keeps the readings of the
account's other machines ([below](#shared-readings)) and serves them to a client that asks with
`{ "shared": true }`. A client that does not ask, or a host without it, gets exactly the host's
own report, as before.

## Ops

### `usage.get`

Request `data`: `{}`, or `{ "shared": true }` where the host offers `usage.shared`. Result: a
[Usage](#usage) report, as the host last read it, and with `shared` the other machines'
readings after its own.

### `usage.subscribe`

Request `data`: as `usage.get`. Result: the current Usage. Then, until the channel closes, the host
sends `usage.update` with the whole Usage whenever what a client shows changes:

* a reading changed in anything a client shows but when it was taken (a window's percent,
  amounts or ends, the source, the error, the account or plan), or
* an unchanged reading was last sent 30 minutes ago, so its `measuredAt` stays inside the hour
  after which a client calls it stale, or
* with `shared`, the other machines' readings it keeps changed: at most one such update per 15 s.

Updates follow the host's reads, which are minutes apart (below), and are at most one per
15 s. A Usage is small (a few hundred bytes per agent). Calling `usage.subscribe` again on
a channel replaces the subscription: the result is the current Usage, and later changes
arrive once. `seq` is the channel's, shared with session events.

## Types

### Usage

`{ "agents": [AgentUsage…] }`, at most 8 entries and 24 KiB as sent: one per agent, and one more
per further Copilot account the machine was asked to report (`slot`), after the agents. An agent
that has never run on the machine is absent: "no data" and "none used" look the same as a bar
and mean opposite things. A further account is never absent: one that cannot be read is a
failure under its login, saying why.

With `shared`: the host's own entries as above, first, then other machines' readings, each with
`readBy`, at most 16 entries and 48 KiB in all. A peer reading is left out where the host's own
reading of that allowance is as good (a reading over a failure, then the later one). A peer
failure is left out unless it is newer than every entry the host has for that allowance, so a
client still learns of a newer failure beside an older reading. Failures go first, then the
oldest peer readings, when the limits are reached.

### AgentUsage

| Field | Type | Notes |
|---|---|---|
| `agent` | `"copilot"` \| `"claude"` \| `"codex"` | Read as `unknown` otherwise, and shown by `label` |
| `label` | string | `GitHub Copilot`, `Claude Code`, `Codex`. At most 64 bytes |
| `account` | string? | The account as the vendor names it: the Copilot login. At most 100 bytes |
| `accountKey` | string? | For a vendor that names no account: an opaque key, the first 12 hex digits of SHA-256. Claude: of the account and organisation ids. Codex (#200): of `codex:`, the lower-cased email, `:` and the ChatGPT workspace id, only on a reading Codex's app-server gave while the login held steady (below). It tells two allowances apart and says nothing else |
| `plan` | string? | e.g. `enterprise`, `max`, `plus` |
| `source` | `"api"` \| `"cache"` \| `"transcript"` \| `"none"` | Where the figure came from: `api` the vendor or Codex's app-server, `cache` an agent's own saved reading (Copilot's fallback, and Claude Code's), `transcript` Codex's transcripts. Read as `none` otherwise |
| `windows` | Window[] | At most 6. Empty for a failure |
| `error` | string? | Why there is no fresh reading. Beside windows, the reading is an older fallback (Copilot's cache). At most 512 bytes |
| `measuredAt` | time | When the figure was taken. For a failure, when the attempt was made |
| `slot` | number? | A further account the machine was asked to report beside the one the agent is signed in as (the host's `usage.copilotAccounts`, [host-config.md](../host-config.md)): its place, `2` for the first listed, to `4`. Absent for the signed-in account. Added after the first release of this contract; a client that does not know it still shows the row by `account`. It does not change grouping: one machine's further account is another's signed-in one, and they are one row |
| `readBy` | `{ id, name }`? | On a reading a host passes on from another machine (`shared`): the device id (32 lowercase hex) of the machine that took it and the name it gave the relay, at most 64 bytes, control characters removed. Absent on the host's own readings. Never carries `slot`: a further account's place is its own machine's |

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
* **A reading that names no account stays on its own** (#200). With neither `accountKey` nor
  `account` (Codex's transcripts, or a Codex probe that could not be sure whose login it read),
  nothing says two such readings are one allowance: grouped by agent alone, the newest would stand
  in for another user's. So it is grouped by `agent` and the machine that read it (`readBy`, or the
  machine that sent it), and two machines give two rows until a keyed reading arrives. One machine's
  keyed and keyless readings of an agent are never shown side by side: whichever it took last stands
  for that machine, the other is left out, so Codex alternating between a probe's keyed reading and
  its transcripts' keyless ones is still one row. A failure is held to the same rule: one older than
  that machine's last reading of the other kind is left out, and one newer still shows; of two
  failures of the two kinds with no reading newer than either, only the later shows. Two
  readings of the two kinds measured at the same moment go to the one reported first.
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
* **Kept readings and where they came from** (#172). A client marks each host's own readings
  with that host (`readBy`), and keeps the last reading of each allowance, and any failure newer
  than it, apart and each with its own time, for the signed-in account between runs: the Windows
  app in its local state (`<profile>-usage.json`), the PWA in IndexedDB. Kept readings merge with
  live ones by the rules above, live first, and go after 14 days (none is shown past that, whether
  or not anything has been heard since, and none that old takes part in the merge, so it never
  hides a current failure; at most 32 allowances are kept). Only another machine the client lists
  for the signed-in account adds to what is kept, and once the Windows app's own host has changed
  account, what it passed on before waits for its next report. A forgotten machine's go with it,
  even where another host still passes them on; where the relay keeps a roster, so do those of any
  machine in neither the roster nor presence (one forgotten while the client was closed). Signing
  out deletes them. Each row says
  `read on <machine>` beside its age, in the stale colour when the reading is stale; the Windows
  app leaves it out for the machine it runs on. A device that has never seen a reading of an
  allowance shows nothing for it.

## Shared readings

Usage belongs to an account, not a machine; a machine matters only because it holds the login
the vendor is read with. So hosts share what they read (#172), and an account's allowance is
still shown once the machine that read it is offline, as agent-ha-bridge's retained MQTT
sensors kept it in Home Assistant. The relay cannot read the vendors itself: it would need the
owner's logins, and it is untrusted (design §5.1).

* **Asking.** While connected to the relay, a host asks every other device in its account's
  presence that is a `host` (and offers `usage`) for its readings: a channel of its own through
  the relay, as a session transfer opens one ([v1-transfer.md](v1-transfer.md)), then `hello`,
  `usage.get` (with `shared` where offered, so readings spread on through hosts that are up),
  close. When a host appears, then every 5 minutes while both are online, never more than once
  a minute, one host at a time.
* **Believing.** Only a device the relay lists in the account's presence as a `host`, whose
  channel's handshake proved its key and introduced it as a host, whose `hello` names that same
  device, and which is still in presence as a host when it answers. Readings travel only inside
  Noise channels between the account's devices. A reading it passes on that was taken by a
  machine the relay's roster no longer lists (forgotten) is not taken, and an answer that arrives
  after the host signed in to another account is not taken for the new one.
* **Keeping.** One reading and one failure per allowance (`agent`, then `accountKey`, else
  `account` ignoring case), as clients merge them: each replaced only by one strictly newer, so
  a failure never replaces a reading, and a copy passed back changes nothing. A reading or failure
  that names no account is never taken from a peer (#200): it could be anyone's allowance, and is
  shown only beside the machine that read it. A host serves them
  as separate entries (the reading, then its failure where that is newer), so a host that already
  has the reading still learns of a newer failure. A copy keeps the
  original `readBy`, and a host never takes one of its own readings back: its own readings always
  stand. A reading stamped more than 10 minutes ahead of the host's clock is refused, and one
  older than 14 days goes (announced to subscribers like any change). At most 32 allowances, the one read longest ago going first. Fields
  are bounded as any reading's; a peer's `error` is passed on as it is, never expanded.
* **Where.** `usage/peers.json` in the profile's own folder, made private to the user as the
  attachment folder is (a protected DACL on Windows, mode 0700 and files 0600 elsewhere), written
  whole and moved into place, for one account: a host signed in to another account serves none
  of it and replaces it. A machine forgotten from the roster takes the readings it took with it.
* **Read-only.** The channel carries `hello` and `usage.get` and nothing else: no command and no
  authority. A view-only or read-only host (`delivery.readOnly`, design G5) takes part, since
  reading usage is not delivery. A host with `usage.publish` off neither asks nor shares: it
  offers no `usage`, so the others only learn that from its `hello`.
* **Home Assistant.** No change: a migrated machine's connector publishes only that machine's
  own readings, as agent-ha-bridge does.

Not yet: an end-to-end-encrypted account store on the relay, readable with every machine off,
needs an account-wide key sealed to each device, so comes after device approval (#55) at the
earliest.

## Where a host gets each figure

| Agent | Source | Credential |
|---|---|---|
| Copilot | `GET https://api.github.com/copilot_internal/user`, `quota_snapshots.premium_interactions`: `entitlement` is the limit, `entitlement − remaining` is used, `quota_reset_date_utc` the reset, a month before it the start. Failing that, the CLI's own cache, `copilot-user-cache.json` (the newest entry for the login), with the failure as `error` | The account is the login in `~/.copilot/config.json` `loggedInUsers` (`lastLoggedInUser` when there are several). Its token, in the CLI's own order: `COPILOT_GITHUB_TOKEN`, `GH_TOKEN`, `GITHUB_TOKEN`, the CLI's stored login (Windows Credential Manager, `https://github.com:<login>.copilot-cli`), then `gh auth token --hostname github.com --user <login>` |
| Copilot, further accounts | The same request, with no cache fallback: only the CLI's own sessions refresh its cache, and they spend the signed-in account. An answer whose `login` is not the account asked for (ignoring case) is refused: `The token gh holds for <login> reads the allowance of <other> instead.` | Only `gh auth token --hostname github.com --user <login>`: the environment's tokens and the CLI's stored login are the signed-in account's. None from `gh` is `No Copilot credential was available for <login> (<why>).` |
| Claude | Claude Code's own last reading, `cachedUsageUtilization` in the profile's `.claude.json` (`source: cache`), taken only when its `accountUuid` is `oauthAccount.accountUuid`: `utilization.limits[]`: `kind`, `percent`, `resets_at`; `seven_day_breakdown.window_started_at` starts the weekly windows; `measuredAt` is its `fetchedAtMs`. When that is stale, Claude Code is asked to read it again (below), and the file is read once more | None. The account key from `.claude.json` `oauthAccount`; the plan from `claude auth status` |
| Codex | The newer of two. Codex's app-server, when the reading is stale (below): `account/rateLimits/read` with `excludeResetCreditDetails`, its `rateLimitsByLimitId.codex`, else `rateLimits`: `primary` and `secondary`, `usedPercent`, `windowDurationMins`, `resetsAt`, `planType` (`source: api`). And the newest `rate_limits` (by its event's time) in the 5 most recently written transcripts under `~/.codex/sessions`, searching the last 8 MiB of each, with the same fields in snake case (`source: transcript`, no key) | None held by the host: the app-server uses Codex's own sign-in |

**Asking the agents themselves** (#200, from spike #197). Neither agent's credentials are opened,
copied, refreshed or logged by the host: it starts the agent, which reads with its own sign-in,
refresh and locking, and runs no model turn.

* **Claude.** `claude -p "/usage" --safe-mode --no-session-persistence --output-format json`, the
  executable found as a launch finds it, in a folder the host owns and keeps empty
  (`<profile>/usage-probe`), with no standard input, and `CLAUDE_CONFIG_DIR` set only for a profile
  that is not the default (Claude Code names its Windows credential by it, so a relative home, which
  the probe would read from its own folder, is not probed at all; nor is a home whose account this
  host reads anywhere but where the probe would save it, such as a `--claude-home` laid out as
  another home's `~/.claude` with its account beside it: given `CLAUDE_CONFIG_DIR`, the probe would
  be another profile). `/usage` is a local
  command: no model is asked. A probe that ends with the file unchanged (rate limited) is a failure.
  `claude auth status`, at most hourly, gates it: only a `claude.ai` login is probed, and one that
  says `loggedIn: false` is the failure `Claude Code is signed out on this machine`, under the
  login's key and timed when auth status first answered so (so newer than the last reading, which a
  client keeps and shows it beside), with no probe until it says otherwise. What auth status said
  holds only for the account it was asked about: another signed in since is shown by its own
  reading until the next hourly check. A probe is decided again once auth status has answered,
  from the saved reading and account as they are then. The probe's exit code cannot say this: it exits 0 with the
  last bars after a sign-out.
* **Codex.** The route follows what Codex runs here:
  * no Codex process: a standalone `codex app-server` over stdio (JSON lines, `CODEX_HOME` set,
    made absolute without resolving its `..` off Windows, which the system takes after any link
    before it, for a home that is not the default);
  * the managed daemon running: a WebSocket on its control socket
    (`<CODEX_HOME>/app-server-control/app-server-control.sock`, path `/`), one message per text
    frame, ended with a close frame that is not waited for, since the daemon never answers one;
  * a Codex session without the daemon, or a runtime (node, bun) whose command line could not be
    read: nothing, and its transcripts are fresh anyway.

  It sends `initialize`, `initialized`, `account/read {refreshToken: false}`,
  `account/rateLimits/read {excludeResetCreditDetails: true}` and `account/read` again, each after
  the last has answered, and never `thread/*` or `turn/*`. The reading's `accountKey` is set only
  when the login held steady across it: the same email in both `account/read` answers, a workspace
  `accountId` and a non-null `ordinaryUsageAllowed` on the snapshot, the standalone route, and no
  other Codex process, or unreadable runtime, seen from just before the first `account/read` to
  just after the second (a look still under way then counts).
  A daemon-route reading never has one: another client can switch the daemon's login unseen.
  An API-key login, another provider or no `codex` falls back to the transcripts with no error
  (one the second `account/read` finds drops that probe's usage read, which may be the old login's),
  and clears an earlier sign-out failure and the last probe reading, which was another login's;
  with no transcript reading to stand instead, the host serves no Codex entry at all (what peers
  and clients kept of the old one stays theirs).
  Codex reported as needing sign-in (its `-32603` "...Please log out and sign in again", including
  a reused refresh token, an authentication failure from the usage read after Codex's own recovery,
  or no account where OpenAI sign-in is required, whether the first or the second `account/read`
  finds it, and whatever the usage read answered between them) is the failure
  `Codex needs signing in again on this machine`, timed when it was found and under the newest
  reading's key (none for a transcript reading); found only by the second `account/read`, the
  usage the probe read just before goes out as the reading, carrying that error (a later probe's
  too, should it find the same). An answer that comes after the 60 s the host waits to report
  (below) still counts: a sign-out found then is a sign-out, and a reading found then is kept. No probe follows for 2 hours or
  until a Codex session next runs here. A session running here lets a probe go ahead at once, and
  the sign-out stands until that probe says otherwise: a reading replaces it, another login (an
  API key) withdraws it, and a sign-out found again, or a probe that fails, leaves it standing. A
  transcript reading does not end it, however recent: a session whose refresh token is lost still
  reads its limits on the access token it has. Nor does it put that probe off. Where no probe can
  run (no Codex sessions folder), the session's arrival alone ends it, with nothing older put
  back in its place.
  `codex login status` cannot tell: Codex's access token lives 240 hours,
  and a lost refresh token only shows at the next refresh. A slow sign-in service
  ("...cloud config: request timed out", "workspace routing discovery timed out", "...application
  network policy is unavailable") is a failure that backs off, not a sign-out, even when only the
  second `account/read` meets it: the reading stands, without a key.

**Pace.** A host asks about an agent every 2 minutes while a session of it is open on the
machine, and every 15 minutes while none is: the allowance belongs to the account, and only a
machine spending it sees it move (`usage.intervalSeconds` and `usage.idleIntervalSeconds` change
both). A further Copilot account is asked at the second pace whatever is open, since the sessions
here never spend it, and one at a time: at most one a pass, every 15 s, the one that has waited
longest. A session opening or closing brings the next ask forward,
but never within 30 s of the last. While agent-ha-bridge's daemon runs for the same user it
already asks every 2 minutes, so a host then asks only every 15. Each request is tried up to 3
times, 400 ms and 800 ms apart, for network failures, timeouts (15 s), 408, 429 and 5xx; any
other refusal is an answer and is not retried.

An agent is asked to read its own allowance only once the newest reading is 15 minutes old (the
idle pace, or `usage.idleIntervalSeconds` when that is longer: a shorter one only paces reading
what the agents saved), at most every 15 minutes while no session of it is open here and every 5
while one is, and only at the idle pace while agent-ha-bridge's daemon runs for the user. One runs at a time per
agent home on the machine: a lease file per agent home under the product's own folder
(`usage-probes/<agent>-<digest>.lock`, the digest of the home's folder with every link on its
path followed, and `..` taken after them as the system does, so a home and a link to it share one), shared by hosts serving different bridge profiles, is
taken before the host looks at what runs and kept until the probe's process ends, even after the
host stopped waiting for it. None starts beside a Claude probe already running on the machine (one
left by a host that has since stopped; any Claude process ending with the probe's options counts,
whatever precedes them, since macOS's `ps` splits a path with a space), nor while the machine's Claude processes, any one's
command line, or a node or bun runtime's, cannot be read to tell. A Claude probe is recognised only by exactly its own
options, `-p /usage --safe-mode --no-session-persistence --output-format json` after the
executable (or after a node or bun runtime and its script), so a real prompt that only quotes
them, even one word long, still holds discovery. Claude Code 2.1.316 writes a probe a
`sessions/<pid>.json` like any interactive CLI's (`kind: interactive`, only `entrypoint: sdk-cli`
differing, which nothing promises), so that file is not taken for a session either when its process
is the probe by those options, or, where `ps` has split the executable's path, ends with them and
the file's `cwd` is the host's own probe folder: `usage-probe` directly inside a profile folder
under the product's data root (or the serving host's profile parent). A project merely named
`usage-probe` is not one, so a flattened prompt that quotes the options there is still a session.
The `claude auth status` that gates a probe is
neither a session nor a hold on discovery or worktree reclaim when it is exactly `auth status` after
the executable (or a runtime and its script) and this host started it (its parent, or the `cmd.exe`
an npm `claude.cmd` runs through, is the host, which began first). Anyone else's `claude auth
status` holds as before; so does the host's own on macOS, which cannot yet say a process's parent. After a
failure or a rate-limited answer the next waits 15 minutes, then 30, 60 and at most 2 hours, until
one works; the last reading stands meanwhile. A finished probe's reading is read at once, not at
the next turn.

**Never ending a probe.** An agent ended between its token endpoint's answer and its own write of
the new tokens loses the rotated refresh token, and the next refresh signs the machine out (spike
E4). So the host never kills a probe:
* Claude's is waited for 120 s; one still running then is a failure, and is left to finish, with
  nothing else started until it has. Host shutdown leaves a running probe alone.
* Codex's standalone app-server has its standard input closed, which it exits on, only after every
  answer and then 30 s more: Codex refreshes under limits of 15 and 20 s, and end of input tears
  down whatever it is still doing (E13). With no answer in 60 s the host reports nothing but leaves
  the process running with its input open; a late answer is still followed by the grace period.
  Only at 10 minutes is it given up on as hung, with its input closed. A Codex process left running
  counts as another Codex process, so no standalone probe starts beside it. Its input goes with the
  host, so the host is never stopped under it (#238). The app asks the host to hold its probes
  (`host.probes.hold`, [local-pipe.md](local-pipe.md)) before every stop, restart and update: no
  standalone probe starts for the next 15 minutes, and the app waits until any running one has
  had its answers and its grace, at most 11 minutes, before it stops the host; a host that cannot
  say, or is still busy then, is not stopped (nor ended) at all. An update waits
  for it however long it has waited already, a forced one at six hours too. A host restarting
  itself (`host.restart`) waits the same way, at most 90 s, before it starts its replacement. A
  host stopped by anything else holds its probes and waits for the answers and the grace, at most
  90 s from the stop. Before an update the app also holds and waits for every other profile's host
  running from the package, which Velopack ends too: each is held again every round, so no hold
  lapses while another is waited for, and again just before the hand-over, the package looked at
  again once they are done so that a host started meanwhile is held too; one of the package's
  hosts that no pipe reaches stops the update. Quitting the app gives up those waits (never a
  probe) and starts a host already stopped for the update again. Home Assistant's Install, run by the host itself,
  holds and waits for this host's probes and refuses while another profile's host runs from the
  package, whose probes it cannot hold, asked both before and after the wait. Giving up that wait
  (the host stopping) leaves the probe running. Velopack's own update and uninstall hooks wait
  only within their 25 s, and leave a busy host to Velopack. So only a host that is killed
  outright, or a busy one Velopack ends for an update or uninstall the app did not start, can
  still have a probe cut short.

**Leaving the agents' credentials alone.** A host never reads, refreshes, copies or logs Claude's
or Codex's credentials: Anthropic retires a refresh token once used and Codex's are single-use too,
so a refresh from here would race the agent's own and could sign it out, and Anthropic's terms do
not allow a third party to collect or intermediate Claude.ai tokens. The direct Claude read this
host used to make (`api.anthropic.com/api/oauth/usage` with Claude Code's stored access token) is
retired (#200). A Copilot token is sent only to `api.github.com`,
and only for a `github.com` login. A `gh` that does not answer within 5 s (a keyring holding
it at a prompt) is not asked again for 6 hours, unless a Copilot session opens on the machine
at least 15 minutes after it stopped answering. A host never reads macOS's keychain item for
Copilot: reading it raises a prompt that the CLI's next token refresh brings back.

## Privacy

Tokens never leave the machine except to the vendor that issued them, and are never logged
or written anywhere. A host's log names agents, sources and reasons, never an account: a
further Copilot account is "further Copilot account 2", and its reading's error, which names
its login, is logged without it. The email Codex's app-server names its login by is held only
while a probe runs, to compare its two answers and make the `accountKey`; it is never logged,
kept or sent. The
account a reading belongs to travels only inside the end-to-end channel, to the account's
own devices: its people's devices and, with shared readings, its other hosts, which keep them
in the profile's private folder.

## Not carried over yet

* **Standing down for another machine** reading the same account (agent-ha-bridge #142).
  Hosts now see each other's readings (#172), but each still asks the vendors for itself, at
  the pace above.
* **macOS keychain opt-in** (agent-ha-bridge's `usage.keychain`, #52). A host on a Mac reads
  Copilot's allowance from the environment's tokens and `gh`, never the keychain.
