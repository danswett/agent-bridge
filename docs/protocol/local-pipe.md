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
with code 3.

## Framing

Each message is a 4-byte little-endian length, then that many bytes of UTF-8 JSON. A
message is at most 65,519 bytes, the plaintext limit of one Noise transport message, so
every op fits either transport unchanged. A larger length, or JSON that is not one of the
shapes below, closes the connection.

There is no Noise handshake: both ends are on one machine and checked by the OS, and
anything that could read the pipe as this user could read the device key and the
transcripts directly.

## Messages

The same three shapes as a relay channel (v1-sessions.md, Messages):

```json
{ "id": 7, "op": "session.answer", "data": { ... } }
{ "re": 7, "ok": true, "result": { ... } }
{ "re": 7, "ok": false, "error": "why" }
{ "ev": "session.upsert", "seq": 12, "data": { ... } }
```

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
| **Inside an agent session** | The connecting process, or any process it descends from, is a live session's own process (by id and start time, so a reused id does not count), one a launch started, any agent CLI found running (Copilot, Claude, Codex, including under Node or Bun), or an executable named `copilot`, `claude`, `codex` or `agency` | Read: `hello` (offering only `sessions.read` and `usage`), `sessions.list`, `session.get`, `sessions.subscribe`, `usage.*`, `launch.options`, `host.status`, `host.subscribe`. Everything else is refused: `refused: this connection is from pid 4120, inside the copilot session 1a2b3c4d (pid 3112); …` |
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
| `host.signIn` | `{ "relay"?: "https://…" }` | `SignInStatus`: GitHub's device flow started through that relay (or the profile's), with `userCode` and `verificationUri` once GitHub gives them. The host finishes on its own and starts using the relay; progress arrives as `host.status` events |
| `host.signIn.cancel` | none | `SignInStatus` as it is; the flow then ends, reported by a `host.status` event |
| `host.settings` | `{ "remoteApproval"?: bool, "remoteFolderTrust"?: bool, "name"?: string }` | `{ "settings": HostSettingsView, "restartRequired": bool }`. Written to the profile's `config.json`; read when the host starts. `name: ""` removes the name |
| `host.restart` | none | `{ "restarting": true }`, then the host starts a replacement and exits |
| `host.stop` | none | `{ "stopping": true }`, then the host exits |
| `host.relay.account` | none | `{ "relay", "token", "accountId", "login" }`: the account this host signed in to, and the relay that issued it, so the app can reach the account's other machines through the relay as a device of its own (kind `app`, with its own key) without a second sign-in. `token` is a bearer credential for that relay only. Fails with `this host is not signed in to the relay` before a sign-in, and on a `--local-only` host |

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
| `settings` | `{ remoteApproval, name?, remoteFolderTrust }` as `config.json` says now (`launch.remoteFolderTrust`) |
| `restartRequired` | `config.json` differs from what the running host read |
| `log` | Path of the host's log file |

Unknown fields are ignored, and unknown enum values read as the first value listed, as in
the rest of the protocol.

## The host without the relay

`serve` always opens the pipe. It goes to the relay only when the profile is signed in,
and keeps running when it is not, or when the relay refuses it, so an app can sign it in
over the pipe. A sign-in made with `agent-bridge-host sign-in` while it runs is picked up
within a few seconds. The relay of a sign-in made during the run is the one the host then
uses, over `--relay` and `AGENT_BRIDGE_RELAY`, so a token only ever goes to the relay that
issued it. `serve --local-only` never uses the relay.
