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
  a different elevation.
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
here). Hello's capabilities on the pipe add `host.manage`.

A refused client receives one event, then an error response to its first request, then
the connection closes:

```json
{ "ev": "local.refused", "seq": 1, "data": { "reason": "the app runs elevated and the host does not" } }
```

## Management ops

Local only: a relay channel answers each of these with `unknown op`.

| Op | Data | Result |
|---|---|---|
| `host.status` | none | `HostStatus` |
| `host.subscribe` | none | `HostStatus`, then a `host.status` event with the whole status whenever it changes |
| `host.signIn` | `{ "relay"?: "https://…" }` | `SignInStatus`: GitHub's device flow started through that relay (or the profile's), with `userCode` and `verificationUri` once GitHub gives them. The host finishes on its own and starts using the relay; progress arrives as `host.status` events |
| `host.signIn.cancel` | none | `SignInStatus` as it is; the flow then ends, reported by a `host.status` event |
| `host.settings` | `{ "remoteApproval"?: bool, "name"?: string }` | `{ "settings": HostSettingsView, "restartRequired": bool }`. Written to the profile's `config.json`; read when the host starts. `name: ""` removes the name |
| `host.restart` | none | `{ "restarting": true }`, then the host starts a replacement and exits |
| `host.stop` | none | `{ "stopping": true }`, then the host exits |

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
| `settings` | `{ remoteApproval, name? }` as `config.json` says now |
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
