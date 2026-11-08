# Local API: the host's pipe

How a desktop app on the same machine talks to its Bridge Host: the
[v1 sessions](v1-sessions.md) and [v1 usage](v1-usage.md) protocol, unchanged, over a
local connection instead of a relay channel, plus a few management ops only this
connection reaches. Implemented by `AgentBridge.Local` (server and client) and
`AgentBridge.Host` (`LocalApi`, `HostControl`). Why it looks like this:
[app-windows.md](../app-windows.md#local-api-a-named-pipe-owned-by-the-host).

## Endpoint

One per host profile.

| OS | Endpoint |
|---|---|
| Windows | Named pipe `\\.\pipe\AgentBridge.Host.<h>`, where `<h>` is the first 16 hex digits of SHA-256 of `<user SID>|<profile folder, lower case>` |
| macOS, Linux | Unix socket `<profile folder>/host.sock` (the folder is `0700`). If that path is too long for a socket address (over 103 bytes), `agent-bridge-<h>.sock` in `$XDG_RUNTIME_DIR`, or `$TMPDIR` on macOS (both the user's alone), where `<h>` is 16 hex digits of SHA-256 of the profile folder. If neither fits, the host runs without its local API and says so in its log |

The profile folder is `%LOCALAPPDATA%\AgentBridge\<profile>` on Windows and
`~/.agent-bridge/<profile>` elsewhere. An app finds its host from the same profile name;
nothing is advertised.

The name is not a secret. What keeps others out:

* The pipe's DACL allows the current user only and denies `NETWORK` (S-1-5-2). Its owner
  is the host's token owner: the user, or Administrators when an administrator runs it
  elevated.
* The host creates the first instance with `FILE_FLAG_FIRST_PIPE_INSTANCE`. If something
  already holds the name, the host exits rather than share it.
* The client connects with `PipeOptions.CurrentUserOnly`, which checks that the pipe's
  owner is the client's own token owner: another user's pipe fails, and so does a host at
  a different elevation. While no host is listening it looks for the pipe again every
  25 ms until its timeout, holding no thread in between (`ConnectAsync` would spin on a
  thread-pool thread for the whole wait).
* For each connection the host reads the client's process (`GetNamedPipeClientProcessId`)
  and refuses it unless the process runs as the same user, at the same elevation as the
  host. A process it cannot identify is refused.

One host runs per profile: `serve` holds `<profile>/host.lock`, and a second one exits
with code 3. On macOS and Linux a stopping host removes its socket file before it stops
listening, never after, so it cannot remove the file of a host started just after it.

## Framing

Each message is a 4-byte little-endian length, then that many bytes of UTF-8 JSON. A
message is at most 65,519 bytes, the plaintext limit of one Noise transport message, so
every op fits either transport unchanged. A larger length, or JSON that is not one of the
shapes below, closes the connection.

There is no Noise handshake: both ends are on one machine and checked by the OS, and
anything that could read the pipe as this user could read the device key and the
transcripts directly.

## Messages

The same shapes as a relay channel (v1-sessions.md, Channel framing):

```json
{ "id": 7, "op": "session.answer", "data": { ... } }
{ "re": 7, "progress": { "stage": "received" } }
{ "re": 7, "ok": true, "result": { ... } }
{ "re": 7, "ok": false, "error": "why" }
{ "ev": "session.upsert", "seq": 12, "data": { ... } }
```

A progress frame (`re` and `progress`, no `ok`) comes only for a `session.reply` or
`session.answer` that asked for it with `"progress": true`, always before its response, and
never completes the request ([v1-sessions.md, Progress](v1-sessions.md#progress)). In C#,
`CallAsync` and `RequestAsync` take a `progress` listener for them. The listener is let go as its response arrives, so a progress frame that comes after the response (from a host that breaks the rule above) reaches nobody, on the pipe and on a relay channel alike.

The first request must be `hello`. Every v1 op and event works as on a relay channel,
served by the same `SessionService` and `UsageService`, except `push.*`: Web Push is for
devices that reach the host through the relay (`push` is not in hello's capabilities
here). Hello's capabilities on the pipe add `host.manage` for the app, and are cut to
reading for a process inside an agent session ([below](#who-may-manage-the-host)).

Attachments differ in size only. The pipe is a direct path (design §4.6): its bytes cost
the relay nothing, so hello offers `sessions.attach.direct` with `sessions.attach`, and
`attachment.chunk` takes files up to 100 MiB, not 256 KiB. The host writes one over
256 KiB to its private `incoming/` folder as it arrives rather than holding it in memory.
`sessions.attach.blob` is never offered here: the relay's blob store is for devices that
reach the host through it.

A refused client receives one event, then an error response to its first request, then
the connection closes:

```json
{ "ev": "local.refused", "seq": 1, "data": { "reason": "the app runs elevated and the host does not" } }
```

## Who may manage the host

Design §6.3 invariant 13: no session the bridge launched, nor any agent process, may stop or
restart its host. On 2026-10-15 an agent ran `agent-ha-bridge restart`. The new daemon took
that agent for an unaccounted process, held discovery, and every resumable session vanished
from the phone. The pipe admits any process of the user's at the host's elevation, so the
host also asks *which* process each request comes from, op by op:

| The caller | How the host tells | May |
|---|---|---|
| **Inside an agent session** | The connecting process, or any process it descends from, is a live session's own process (by id and start time, so a reused id does not count), one a launch started, any agent CLI found running (Copilot, Claude, Codex, including under Node or Bun), or an executable named `copilot`, `claude`, `codex` or `agency` | Read: `hello` (offering only `sessions.read` and `usage`), `sessions.list`, `session.get`, `sessions.subscribe`, `usage.*`, `launch.options`, `host.status`, `host.subscribe`. Everything else is refused: `refused: this connection is from pid 4120, inside the copilot session 1a2b3c4d (pid 3112); …`. That includes `session.history` (`…; reading a session's history is for the person, from the app or a phone`, [v1-history.md](v1-history.md#security-and-authorization)) and `sessions.focus` (`…; telling the host the person is using the app is for the person, from the app or a phone`): a device in use holds back the person's pushes ([v1-sessions.md](v1-sessions.md#while-the-person-uses-a-device-1190)) |
| **The app** | Not inside an agent session, its whole ancestry read, and its executable is `AgentBridge.exe` (the app) in the host's own folder, which is how the app ships | Everything, `host.manage` included |
| **The host's command line** | Not inside an agent session, its whole ancestry read, and its executable is this host's own (`agent-bridge-host`, as `migrate` and `rollback` run it) | What anything else may, and `host.restart`, which a migration and a rollback need to hand the hook spool over and back ([migration.md](../migration.md)). Nothing else the app may |
| **Anything else of the user's** | Neither, a process that could not be traced, or one whose ancestry could be read only in part (an agent may be above what was read) | The sessions ops as before, `host.status` and `host.subscribe`; not the other management ops (`only the Agent Bridge app beside this host may stop, restart, sign in, change settings or read the relay account`) |

The ancestry is read when the connection is accepted (`GetNamedPipeClientProcessId`, then one
process snapshot for every parent and its executable's name, with start times checked so a
parent id given to a later process ends the chain). A parent that has gone ends it as well, at
its natural end; one that is there but cannot be read leaves it incomplete, and what was read
still counts. It is matched against the agents the host knows: afresh for every management op,
so a session found after the app connected counts, and at most every 5 seconds for the sessions
ops, which an attachment upload sends thousands of in a row. Each refusal is logged.

`agent-bridge-host`'s own commands that change its config or its Home Assistant token, sign it
in, or migrate the machine or roll it back (not a dry run), refuse in the same case, from their own ancestry: on Windows the snapshot, on Linux
`/proc`, on macOS one `ps` listing. They refuse as well when that ancestry cannot be read in
full (fail closed). Every session a launch starts has `AGENT_BRIDGE_SESSION` in its environment
too, which survives a process started detached from its parent
([host-config.md](../host-config.md#approving-a-workspace-at-the-machine)).

The app does not go round this. Asked to stop or restart a host that answers no, it reports the
refusal and ends nothing (it ends a host only when the host does not answer at all). Its install,
update and uninstall hooks, which Velopack runs for whoever starts `AgentBridge.exe` with their
arguments, leave the host, its logon task and the app's registrations alone inside an agent
session or an ancestry that cannot be read in full, and say so in the app's log.

**What this is not.** Every process here runs as the same user, so this is not a security
boundary, only a safety rail, like agent-ha-bridge's (an agent token and an instruction not to
restart the bridge). Anything running as the user can read the device key and `config.json`,
write the file directly, end the host from Task Manager, copy a program to the app's name, or
start one through Explorer or a scheduled task so that it descends from nothing an agent
runs. What it stops is the obvious door: an agent following an instruction to restart its
host, or doing so by mistake. An agent that is determined to get past it, or malware, is not
stopped. Sessions acting as an agent rather than as the person (design §5.5) are #45 and #46.
On macOS and Linux the host cannot trace a socket client yet, so nothing there manages the
host, and there is no app there to do it.

**The one exception: the host's own command line may restart it.** `agent-bridge-host migrate`
and `rollback` must restart the host to hand agent-ha-bridge's hook spool over and back
([migration.md](../migration.md)), and they are run by a person, from a terminal of their own.
So a caller that is this host's own executable, beside it, with its whole ancestry read and no
agent in it, may call `host.restart` and nothing else the app may. It is the same rail as the
app's: the same ancestry check refuses it inside an agent session, and the commands themselves
refuse first, with exit 4, before reading anything. A copy of the program elsewhere, or one whose
ancestry is read only in part, is anything else of the user's. An agent that starts the program
detached from itself gets past this as it could past the app's check; restarting is the only
thing it gains.

## Management ops

Local only: a relay channel answers each of these with `unknown op`. All but `host.status`
and `host.subscribe` are the app's alone ([above](#who-may-manage-the-host)).

| Op | Data | Result |
|---|---|---|
| `host.status` | none | `HostStatus` |
| `host.subscribe` | none | `HostStatus`, then a `host.status` event with the whole status whenever it changes |
| `host.agents.refresh` | none | `AgentReadinessReport` (below). Advertised as `host.agents` in the app's local hello only. Requests a fresh, read-only scan; overlapping requests share a scan. App-only despite not saving settings: version checks can start CLI processes |
| `host.agents.setup` | `{ "agent": "copilot" \| "agency" \| "claude" \| "codex", "action": "install" \| "remove" }` | `{ "completed": bool, "detail": string, "restartRequired": bool }`. Advertised as `host.agents.setup` only to the local App on Windows when available. `install` also repairs; Agency shares Copilot hooks. Uses the owned standalone backend, refuses read-only/legacy/migrated/foreign configuration, and serializes operations with a three-minute deadline and host-shutdown cancellation. Unknown agents/actions are rejected without writes. Expected setup failures return `completed: false` with explicit incomplete-work details, not readiness. Cancellation and unexpected failures are protocol errors. Never restarts, accepts trust, installs a CLI, or signs in |
| `host.agents.install` | `{ "agent": "copilot" \| "claude" \| "codex", "acceptAgreements": true }` | `AgentInstallResult`: `{ "completed": bool, "detail": string, "executable"?: string }`. Windows local App only, advertised as `host.agents.install`; fresh caller classification is required. Uses fixed WinGet package IDs/sources, explicit source/package/runtime consent, a ten-minute deadline and host-shutdown cancellation. Reuses existing executables without upgrades; missing configured paths and read-only/ownership/legacy conflicts are refused. Checks fresh executable discovery before success, never implies sign-in or readiness, never returns installer output. Settings allows eleven minutes for the operation plus a separate one-minute readiness refresh |
| `host.agents.install.cancel` | none | `{ "cancellationRequested": bool }`. Same App-only authorization; requests cancellation without ending serialization until the installer has stopped. Cancellation/deadline returns `completed: false`; external installers may already have changed the machine, so no rollback or automatic uninstall is promised. Shutdown cancellation is a protocol error |
| `host.agents.verify` | `{ "request": UUID, "agent": "copilot" \| "agency" \| "claude" \| "codex", "workspace": approved label, "confirmed": true, "profile"?: Agency profile }` | Starts a new explicitly consented test and returns `AgentVerificationReport` immediately; subsequent progress is in `host.status`. Advertised as `host.agents.verify` to the Windows local App only. Requires current launch configuration, active intake and complete discovery. Repeating the same UUID and selections returns the same run (retained for 25 minutes); a different selection needs a new UUID. One test runs at a time |
| `host.agents.verify.cancel` | `{ "request": UUID }` | Stops observation and returns its report. Does not undo a committed launch, close any session, or answer trust/sign-in questions |
| `host.agents.signIn` | `{ "agent": "copilot" \| "claude" \| "codex", "confirmed": true }` | `{ "opened": bool, "detail": string }`. Windows local App only, advertised as `host.agents.signIn`, with fresh caller classification and explicit person confirmation. Refuses read-only/legacy/ownership conflicts, missing configured executables and unavailable interactive desktops. A bounded, contained `--help` probe must list the standalone command before opening `copilot login`, `claude auth login` or `codex login` in a visible terminal. Serializes window creation and reuses a still-running window for that CLI. Uses the same recorded homes, Claude configuration mode and refreshed child PATH as session launch, but no session marker, model, bypass or extra arguments. The person completes/cancels in the terminal/browser; the window is not killed when the host stops. `opened` is not authentication, online validity or entitlement evidence. No credentials or raw CLI output are returned, no trust accepted and no hook/relay account changed. The app allows thirty seconds for the bounded opening request |
| `host.signIn` | `{ "relay"?: "https://…" }` | `SignInStatus`: GitHub's device flow started through that relay (or the profile's), with `userCode` and `verificationUri` once GitHub gives them. The host finishes on its own and starts using the relay; progress arrives as `host.status` events |
| `host.signIn.cancel` | none | `SignInStatus` as it is; the flow then ends, reported by a `host.status` event |
| `host.settings` | `{ "remoteApproval"?: bool, "remoteFolderTrust"?: bool, "remoteFolderTrustDevices"?: [device id], "remoteFolderTrustDevicesWas"?: [device id], "devBoxKeepAwake"?: bool, "phoneSignIn"?: bool, "name"?: string }` (`remoteFolderTrust`, `remoteFolderTrustDevices`, which replaces the whole list, and `devBoxKeepAwake` (`devBox.keepAwake`, on a Dev Box) apply at once; the rest at the next start). A `devBoxKeepAwake` change is refused, leaving the file as it was, when the saved `config.json` would not read back with it (another setting the host rejects reads as keep-awake off). `remoteFolderTrustDevicesWas` is the list the new one was made from. If the saved list differs (as a set), because another app or a person's terminal changed it in the meantime, nothing in the request is changed and `conflict` is true. A device revoked elsewhere is therefore never approved again from a list read before. On a conflict, read the list again and make the change from that | `{ "settings": HostSettingsView, "restartRequired": bool, "conflict"?: bool }`. Written to the profile's `config.json`; read when the host starts. `name: ""` removes the name |
| `host.restart` | none | `{ "restarting": true }`, then the host stops, and once it has finished (every disposal done, the profile's lock let go, however long that takes) is replaced and exits. Started any earlier, the replacement could give up waiting for the lock and leave no host at all (#239). A host run by the logon task's launcher (`agent-bridge-hostw`) exits with code 75 and the launcher starts the replacement, which it keeps watching for crashes (#106); any other host starts its own replacement |
| `host.stop` | none | `{ "stopping": true }`, then the host exits |
| `host.probes.hold` | none | `{ "busy": bool, "why"?: string }`. The app is about to stop the host, or replace it with an update (#238): for the next 15 minutes, extended by each call, the host starts no usage probe whose process goes with it (Codex's standalone app-server), and `busy` says whether one is still answering or within its grace after the last answer. The app stops the host only once it is not, so the probe's standard input is never cut mid-refresh; a failed or refused call, or a host still busy after 11 minutes, is not stopped at all. Only `unknown op` (an older host, which runs no such probe) lets the stop go on. Refused to anything but the app |
| `host.relay.account` | `{ "device"?: device id }`: the app's own relay device id, so the host never accepts a management command from the app beside it (#801); an older app sends nothing | `{ "relay", "token", "accountId", "login" }`: the account this host signed in to, and the relay that issued it, so the app can reach the account's other machines through the relay as a device of its own (kind `app`, with its own key) without a second sign-in. `token` is a bearer credential for that relay only. Fails with `this host is not signed in to the relay` before a sign-in, and on a `--local-only` host |
| `manage.*` | any | Refused to every caller, the app included: `managing a machine from another device goes through the relay; on this machine, use the app's Settings`. Managing a machine from another device ([v1-sessions.md](v1-sessions.md#managing-a-machine)) is through the relay only, so the #67 guard on `host.restart` and `host.stop` gains no way around it |
| `host.update.report` | `{ "state": UpdateState, "taken"?: [request], "carriesUpdateNow"?: bool }`: what the app's updater is doing, the presses in `updateAsks` it has taken on, and whether it can carry out an Update now (#1159; absent from an older app, whose machine then offers none, `updateNowWhy` `old-app`) ([v1-update.md](v1-update.md)) | `{ "ok": true }`. The host serves the state to the account's other devices (`update.*`) until the app's connection closes, then says the app is not running, and drops the presses taken from `updateAsks`. `canUpdateNow`, `updateNowWhy`, `updateNowAt` and `busy` are the host's own, and dropped from what the app reports. Refused to anything but the app: `only the Agent Bridge app beside this host reports its updates, and takes other devices' presses of Install` |
| `host.update.depart` | `{ "request": UUID }`: an Update now's press (#1159) the app is carrying, its download checked and its probe hold over | `{ "outcome": "committed" \| "waiting" \| "refused", "code"?, "transition"? }` ([v1-update.md](v1-update.md#going-the-lease-and-the-departure), step 3). The host takes #805's quiet lease as purpose `Update`, compares what is busy with what the person confirmed, announces and commits: `committed` (the host refuses new deliveries until it exits; the app then stops it with `host.stop` and installs), `waiting` (`code` is `turn-running`, `question-waiting` or `unknown`; the app keeps the download for the next quiet moment) or `refused` (`code` says why; the host serves on). An unknown outcome reads as `refused`. A request that isn't a UUID is an error. This is #870's departure step, wired minimally until that lane lands it. Refused to anything but the app, as `host.update.report` is; an older host answers `unknown op`, and the press ends refused |

`HostStatus`:

| Field | |
|---|---|
| `version`, `dev` | The host's version, and whether it is a development build |
| `profile`, `folder` | Its profile and folder |
| `processId`, `startedAt`, `elevated` | Its process |
| `machine` | As in hello: id, name, OS |
| `account` | `{ login, accountId }` once signed in, else absent |
| `relay` | `{ state, url?, detail?, since? }`; `state` is `off` (`--local-only`), `signedOut`, `connecting`, `connected` or `refused` (sign in again) |
| `signIn` | `{ state, userCode?, verificationUri?, expiresAt?, error? }`; `state` is `idle`, `pending` or `failed` |
| `agentHaBridge` | Why answering, replying and stopping are off: agent-ha-bridge runs for this user (design G5). Absent when not |
| `homeAssistant` | Why the Home Assistant connector is not reaching Home Assistant: it rejected the token, or has banned this machine's address, with what to do and when it is next tried ([ha-connector.md](../ha-connector.md#troubleshooting)). Absent when nothing is refused |
| `settings` | `{ remoteApproval, name?, remoteFolderTrust, remoteFolderTrustDevices?, devBoxKeepAwake, escapedApproval?, escapedApprovalDevices?, phoneSignIn }` as `config.json` says now (`launch.remoteFolderTrust`, the approved device ids, absent when none, `devBox.keepAwake`, `permissions.phoneSignIn` (#972, applied at the next start), and `permissions.escapedApproval` and `permissions.escapedApprovalDevices`, the latter absent while it inherits the folder-trust devices, which confirm only while folder trust is on; #803). The app's Settings shows which devices may confirm an escaped approval |
| `restartRequired` | `config.json` differs from what the running host read, or agent setup has been attempted in this host run and may have changed registrations. A partial failure also conservatively requests restart; this is not evidence that setup completed |
| `log` | Path of the host's log file |
| `readOnly` | Why the host acts on nothing from other devices now (`delivery.readOnly`, or agent-ha-bridge running, G5); absent when it does. The app carries out no install a device asked for while it is set, and checks it again just before stopping the host |
| `updateAsks` | Presses of Check and Install from the account's other devices waiting for the app (#51): `[{ request, kind: check \| install, by, at, now? }]`, `by` naming the device (`"pwa 1a2b3c4d"`). `now`, on an Install that is an Update now (#1159): `{ release, accept?, until }`, the release the person was shown, what they confirmed it may go over (`UpdateBusy`), and when the press's 15 minutes end. Absent when none. At most 8, each dropped once the app reports it taken or after ten minutes (an Update now's at `until`, if sooner). The pipe itself asks for no update: `update.*` is refused here, for the app included, which updates its own machine from Settings |
| `devBox` | The Dev Box keep-awake ([host-config.md](../host-config.md#dev-box-keep-awake)), present only when this machine is a Microsoft Dev Box (the Dev Box agent is installed, its settings usable or not): `{ keepAwake, intervalMinutes, last?: { at, outcome, detail, alarming }, nextAt?, problem? }`. `keepAwake` is whether passes run now; `outcome` is `skipped`, `delayed`, `safe`, `no-action`, `blocked`, `failed`, `stood-back`, `busy` or `not-a-devbox`; `alarming` (`failed`, `blocked`, or `stood-back` with the scheduler unreadable) says the Dev Box may hibernate. `nextAt` while on. `problem`: why the agent's settings could not be used, so it cannot be kept awake |
| `agents` | Optional `AgentReadinessReport`: separate evidence for each launcher's installation, version, hooks, CLI sign-in and activation. Absent on older hosts and until the first scan finishes |
| `agentInstallation` | Optional `{ agent, inProgress, detail, blocked }`: host-authoritative current or last installation state, recovered on reconnect or by another Settings window. No installer output. A lost request reply does not cancel the operation. WinGet's job cleanup is awaited before admission of another installer and during shutdown/replacement; unconfirmed cleanup sets `blocked` and refuses further installs in this host. Windows deployment services outside the job may already have made partial changes; check installation activity before restarting and retrying |
| `signInDevices` | **New (#972 slice 2).** Who can sign an agent in on this machine from their device, read-only, for Settings: `{ state: none \| ready \| unreadable, devices: [{ name, kind, platform, thisDevice? }] }`, from the machine's copy of the account's device roster (#55), the same as `signin.devices` ([v1-sessions.md](v1-sessions.md#phone-sign-in-800-slice-2-enabled-per-machine-by-972)). `thisDevice` marks the app's own entry, by the device id it gave `host.relay.account`. Never a device id, key, generation or presence. Absent while this host doesn't run sign-in from devices (the switch off, turned on but not applied until the next start, or unable to run here), and from an older host |
| `agentVerifications` | Optional current-host-run reports, latest per selected launcher (at most four). Never loaded from disk; absent on older/unsupported hosts |

Unknown fields are ignored, and unknown enum values read as the first value listed, as in
the rest of the protocol.

### Current-run verification

The shared Settings/first-run backend opens the normal visible launcher with the explicitly
selected agent and approved workspace, standard `ask` permissions, no resume/transfer, and a
fixed harmless prompt asking for a unique reply without tools, file access or delegation.
The App must explain and obtain consent before sending `confirmed: true`: this opens a real
provider session, may consume paid usage, and may first ask the person to sign in or trust
the folder. Verification never answers those questions or changes permissions.
The verification-specific launcher forces `ask` even if machine/request defaults allow all,
and refuses any configured `extraArgs` for that launcher before starting anything.
Provider-defined arguments can override permissions through aliases or config switches;
they are neither silently removed nor checked against an incomplete bypass-flag denylist.

Each report contains `request`, `agent`, `workspace`, optional `profile`, `state`, `detail`,
`startedAt`, `checkedAt`, and separate `{ state, detail }` checks named `launch`, `discovery`,
`provider` and `hooks`. Optional `session`, `discoveredAt`, `providerObservedAt` and
`hookReceivedAt` identify the correlated evidence. States are `unknown`, `running`,
`verified`, `needsAction`, `blocked`, `cancelled`, `failed`; unknown values read as
`unknown`, never verified.

Only the launcher's exact request/session binding counts, including registration after its
initial `unconfirmed` answer. Arbitrary new-looking sessions cannot satisfy the test.
Discovery becoming held after launch keeps the same test observing within its existing
timeout, so sign-in/trust followed by late registration does not require another launch.
Evidence is evaluated only after discovery is complete again.
Discovery must identify a process started in this run; sessions present before the request
cannot count. The main agent must answer this run's unique prompt, not echo it or borrow a
delegate's response. A hook must name the same session and agent, have fresh receipt/handling
times, and identify that process in its owner or ancestry. Old spool backlog, heartbeat,
installation markers, login exit codes and unrelated traffic cannot satisfy these checks.
The hook spool is a same-user boundary: the host does not authenticate who wrote a record, so this
proof assumes trusted local writers. A same-user process that forges a record naming this run's session,
fresh times and process ancestry could satisfy the hook check once the provider answer also arrives.
It shows the hook path reached this host, not that the record came from the agent's own hook.

The test observes for at most eight minutes. Missing/unknown evidence, held discovery,
read-only/legacy state, changed launch configuration or hook setup, cancellation and shutdown
never become success. Stopping observation does not stop or undo the normal launch;
its window may still appear and the person closes it if unwanted. Errors and timeouts explain
this explicitly. Hook subscriptions are released when observation ends.

A verified result applies only to that selected agent/profile/workspace on this host run,
not relay connectivity, every configured agent, or a separately proven account entitlement.
Before using it as a finish gate, read fresh `host.status`. Changed setup/configuration,
incomplete discovery, a changed/missing session process or ten-minute-old evidence projects
`needsAction`. The same freshness projection applies when retrying any retained request UUID
or asking to stop a run that already ended, even after a newer run replaced its status row.
Restart discards every result.

Windows Settings loads `launch.options` before offering explicitly selected agent,
approved workspace and optional Agency profile choices. Opening its confirmation alone
sends no verification request. Confirmation explains the exact folder/isolation, profile,
paid usage and manual sign-in/trust, with Cancel as the default. Before starting, the app
revalidates the folder path, isolation and offered profile and pins the request to that
host process. A host replacement or changed choice requires a new confirmation.
It never automatically resends a start after a lost response. Status subscription restores
reports after reconnecting to the same host; disconnected rows are hidden and a new host
cannot inherit a cached success. Rows label the timestamped last reported result, with
an explicit status recheck, and cancellation says only that observation stopped.
The guided first-run shell and disposable clean-machine acceptance remain incomplete.

### Agent readiness evidence

The shared first-run/Settings contract is `{ checkedAt, agents: [...], readOnlyReason? }`.
Each agent has `id`, `label`, `home`, optional `executable` and `version`, checks named
`installation`, `versionCheck`, `hooks`, `authentication` and `activation`, optional
`lastHookHandledAt`, and `hookSetup`. Each check is `{ state, detail }`; states are `unknown`, `missing`,
`needsAction`, `working`, `blocked` and `failed`. Unknown future states read as `unknown`.
There is deliberately no aggregate "ready" bit.

`hookSetup` is this profile's own setup record for the agent home: `notSetUp` (no ownership
record), `incomplete` (an owned record without completed setup: an interrupted or failed
setup, repair or removal, or a record from before completion was recorded; offer Repair),
`completed` (the last setup wrote every registration, Codex's plugin and intake) or
`unknown` (not determined: older hosts omit the field, a read-only host, unreadable intake
or ownership records, or another profile's record). Unknown future values read as `unknown`.
With intake enabled, `incomplete` has `hooks.state` `failed`, and `completed` keeps
`hooks.state` `unknown`: setup completion is not evidence that hooks run in a new session.
With intake off, both report `needsAction`. Clients use `hookSetup`, not `detail` text.

The running host scans at startup, every ten minutes and when the app asks. It uses
Launch's executable locator and running launch configuration, not an app-side PATH probe.
On Windows, installed executables get `--version` with a three-second timeout each in a
non-breakaway process job assigned atomically at creation. Success, timeout and cancellation
all terminate remaining descendants and allow one second for process and pipe cleanup.
All Windows host subprocess starts share the native launch's inheritable-handle gate so unrelated
usage, git or injector children cannot retain a probe's pipe writers outside that job.
The scan waits for cleanup before releasing its gate. Other platforms still detect
executables and hook ownership, but explicitly report version probing unavailable until
equivalent process isolation is implemented. A failed version probe does not make an
installed CLI missing. A read-only host (including one beside the
legacy bridge) runs no version commands and reports hook setup blocked.
Explicit app checks require a scan that started after the request; queued requests may
share that scan, but cannot reuse an older in-flight scan after setup changes. Settings
allows one minute for an active scan plus this fresh scan, including probe cleanup.
The guard is checked again before each process: if the legacy bridge starts during a
scan, subsequent commands are blocked. Earlier observations remain evidence from before
that transition; `readOnlyReason` reports the reason observed at scan completion.

This first slice reports all four launchers (Agency, Copilot, Claude and Codex).
Ownership records and enabled intake are evidence, not proof that registrations are
correct or a new session will activate hooks. Missing, foreign or broken records are
distinguished; no record or registration is written. Authentication, entitlement, folder
trust and new-session activation remain `unknown` until verified probes are added.
`lastHookHandledAt` comes from the existing intake's accepted events since this detector
started in the current host run, not from consuming its spool or scanning old logs.
Agency shares Copilot's hook evidence; it does not prove that an Agency test session ran.
Neither old events nor a matching ownership marker imply end-to-end readiness.

Scan completions and newly handled hook events publish whole `host.status` events.
After a manual scan, Settings also reads status explicitly so a delayed or absent event
does not leave the old report displayed; a newer status event or connection wins the race.
Settings drops displayed agent rows on disconnect and explains an older host's missing
capability instead of presenting its agents as not installed.

## The host without the relay

`serve` always opens the pipe. It goes to the relay only when the profile is signed in,
and keeps running when it is not, or when the relay refuses it, so an app can sign it in
over the pipe. A sign-in made with `agent-bridge-host sign-in` while it runs is picked up
within a few seconds. The relay of a sign-in made during the run is the one the host then
uses, over `--relay` and `AGENT_BRIDGE_RELAY`, so a token only ever goes to the relay that
issued it. `serve --local-only` never uses the relay.
