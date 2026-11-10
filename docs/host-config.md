# The host's config.json

What a host may do on its machine is set by the person who runs it, in `config.json` in the
profile's folder, beside the device key: `%LOCALAPPDATA%\AgentBridge\<profile>\config.json`
on Windows, `~/.agent-bridge/<profile>/config.json` elsewhere (`default` unless
`--profile` says otherwise). `agent-bridge-host serve` reads it when it starts; a change
takes a restart. Nothing another device sends can change it.

Until the app has a settings screen, it is edited with the host's own commands, at the
machine (below), or by hand. The app will edit the same file; this page is the schema both
follow.

## Schema, version 1

```jsonc
{
  "version": 1,
  "permissions": {
    "remoteApproval": true,
    "trustedPeers": false,
    "escapedApproval": "confirm",
    "escapedApprovalDevices": null,
    "phoneSignIn": false
  },
  "push": { "enabled": true },
  "delivery": { "readOnly": false },
  "sessions": { "restoreOnStart": true },
  "launch": {
    "enabled": true,
    "workspaces": [
      { "path": "C:\\Users\\me\\repos\\bridge", "label": "Bridge", "isolate": true },
      "C:\\Users\\me\\notes"
    ],
    "defaultWorkspace": "Bridge",
    "defaultAgent": "auto",
    "permissions": "ask",
    "worktrees": { "root": "~/repos/wt", "limit": 10, "idleHours": 12 },
    "profiles": [],
    "defaultProfile": null,
    "resumeCount": 12,
    "agents": {
      "copilot": {
        "path": null,
        "model": "gpt-6.1-sol", "effort": null, "context": null,
        "models": [], "efforts": [], "contexts": [],
        "extraArgs": []
      },
      "agency": { "path": null, "extraArgs": [] },
      "claude": { "model": "opus" },
      "codex": { }
    },
    "transfer": { "serve": true, "receive": true, "maxBytes": 26214400, "maxSourceBytes": null },
    "remoteFolderTrust": false,
    "remoteFolderTrustDevices": []
  },
  "homeAssistant": {
    "enabled": false,
    "baseUrl": "http://homeassistant.local:8123",
    "dashboardUrlPath": "agent-decisions",
    "detailedActivity": true,
    "machineName": null,
    "disposable": false,
    "agentUserIds": [],
    "updates": { "checkForUpdates": true, "checkHours": 0.5 }
  },
  "usage": {
    "publish": true,
    "intervalSeconds": 120,
    "idleIntervalSeconds": 900,
    "copilotAccounts": ["dswett_microsoft"]
  },
  "devBox": { "keepAwake": false, "intervalMinutes": 15 },
  "agentMessaging": { "remote": "off", "allowAllTargets": false }
}
```

Comments and trailing commas are allowed. Absent and `null` mean the default.

| Key | Default | Meaning |
|---|---|---|
| `version` | `1` | The schema. A file with a higher version, written by a newer Slipgates host, is not guessed at: remote approval and launching are both off until the host is updated |
| `permissions.remoteApproval` | `true` | Copilot permission prompts may be approved or denied from another device ([host-answers.md](host-answers.md)) |
| `permissions.escapedApproval` | `confirm` | How Approve is given for an approval holding characters the host shows as escapes (bidi controls, hidden characters, right-to-left letters; #803). `confirm`: a two-step confirmation from a person's phone, browser or Windows app this machine approved ([v1-sessions.md](protocol/v1-sessions.md#escaped-approvals)). `machine`: only at the machine. Deny goes from anywhere either way. A value that is neither is read as `machine` and said in the log. Applies when the host restarts |
| `permissions.escapedApprovalDevices` | absent | The devices, by device id, that may confirm one. Absent or `null`: the devices approved for folder trust (`launch.remoteFolderTrustDevices`, as the Windows app's Settings changes them), and only while `launch.remoteFolderTrust` is on, so a device approved there needs no second visit and turning folder trust off (for a lost phone, say) takes Approve from them too. A list, even an empty one, replaces that. Invalid ids are ignored with a problem; a value that is not a list approves none. At most 64. Applies when the host restarts |
| `permissions.remoteManage` | `true` | A person's other device (a phone or browser, or the app on another machine; never this machine's app, an agent or Home Assistant) may restart this host and repair the hooks of an agent this installation set up, through the relay, each press signed by that device (#801). Restarts are capped at 2 in 6 hours and wait for the host to be quiet; a read-only host never offers it. Read when the host starts |
| `permissions.trustedPeers` | `false` | Transitional full-key remote authorization floor (#402), for disposable-host evaluation only. Restart-only; requires explicitly seeded pins. See [trusted peers](#trusted-peers-disposable-host-foundation) |
| `permissions.phoneSignIn` | `false` | Signing Copilot in again from a person's device (`sessions.signin`, #972): the host runs Copilot's own `copilot login --device-code` and shows the one-time code on that device only. While it runs, **the machine opens a browser and copies the code**. Only a device #55's device roster pins as a person's is offered it (none until that roster ships), and only on Windows. A value that is not `true` or `false` turns everything off, as `remoteApproval`'s does. Applies when the host restarts. See [phone sign-in](#phone-sign-in) |
| `delivery.readOnly` | `false` | A host that only shows sessions: it takes no answer, reply, stop, launch, permission approval or transfer from any device, whatever agent-ha-bridge is doing, and says so in `hello` (`machine.readOnly`, "this is a read-only comparison host"), refusing each with that reason. The shadow host beside agent-ha-bridge sets it ([host-shadow.md](host-shadow.md)), so a daemon restart never leaves it acting. Without it, a host is held only while agent-ha-bridge runs (design G5) |
| `sessions.restoreOnStart` | `true` | After a reboot or a crash of the host, the sessions that died with it are restored, idle, through the resume path, one at a time, and what cannot be is offered to resume ([host-launch.md](host-launch.md#restoring-after-a-reboot-or-a-crash)). Nothing is ever typed into them. `false` restores nothing, only says what went, in `hello` and one notification, and keeps nothing waiting. A value that is not `true` or `false`, or a file that cannot be read, turns it off, but keeps what died offered (the person may have meant it on). Per machine
| `push.enabled` | `true` | Notifications (Web Push) to the account's phones. `false` sends none and takes no subscription: `hello` leaves `push` out, and `push.subscribe` fails saying the machine doesn't send notifications. For a machine that should be seen but not heard from, as the shadow host beside agent-ha-bridge ([host-shadow.md](host-shadow.md#on-the-phone--relay)) |
| `launch.enabled` | `true` | Launching from other devices at all. `false` leaves `sessions.launch` out of `hello` |
| `launch.workspaces` | `[]` | **The approved folders.** A new session starts only in one of these, and a session is resumed only if it ran in one of these folders itself (or in a worktree this host or agent-ha-bridge made for an isolated one), as agent-ha-bridge has it: not in a folder below one. Each is a path, or `{ "path", "label", "isolate" }`. `path` is absolute (`~` is the home folder); `label` defaults to the folder's name, and is what a client names; `isolate: true` starts each new session in a git worktree of its own ([host-launch.md](host-launch.md#isolation)). A folder that does not exist, or a system folder (Windows, Program Files, ProgramData, temp, a drive root), is not offered. At most 64 |
| `launch.defaultWorkspace` | first | The label a launch gets when it names none |
| `launch.defaultAgent` | `auto` | `auto` (the first installed of Agency, Copilot, Claude, Codex) or one of `agency`, `copilot`, `claude`, `codex` |
| `launch.permissions` | `ask` | What a launch that does not say gets: `ask` keeps permission prompts; `allowAll` launches without them. A client may choose either per launch |
| `launch.worktrees.root` | `~/repos/wt` | Where isolated sessions' worktrees go. Shared with agent-ha-bridge and every other session: this host only ever adds its own there |
| `launch.worktrees.limit` | `10` | Worktrees this host made, per repository, before isolated launches there are refused, counted after finished ones are removed. `0` is no cap |
| `launch.worktrees.idleHours` | `12` | How long after this host made a worktree it may be removed once finished: clean (no uncommitted, untracked or ignored files but two named build outputs), detached, nothing ahead of its base, unlocked, and no live session in it ([host-launch.md](host-launch.md#reclaiming-finished-worktrees)). `0` removes a finished one at the next isolated launch |
| `launch.profiles` | `[]` | Agency profiles to offer: empty offers every one Agency lists; otherwise those of them named here (all, if none of them is listed) |
| `launch.defaultProfile` | first | The profile a launch gets when it names none |
| `launch.resumeCount` | `12` | How many resumable sessions to offer, up to 50; `0` offers none |
| `launch.agents.<id>.path` | found | The agent's executable, when it is not where it is usually found (PATH, then its installer's folder). A path that does not exist means the agent is not installed |
| `launch.agents.<id>.model`, `effort`, `context` | none | What a launch that does not say is started with. None means the agent's own default. `agency` uses `copilot`'s |
| `launch.agents.<id>.models`, `efforts`, `contexts` | built in | The values to offer, replacing the built-in lists (and, for models, those the machine's sessions are using). At most 64 each |
| `launch.agents.<id>.extraArgs` | `[]` | Arguments added to every launch of that agent, as written |
| `launch.transfer.serve` | `true` | Hand this machine's ended sessions to the account's other machines, each on a command a person's device signed for it ([host-transfer.md](host-transfer.md)). `false` leaves `sessions.transfer.serve` out |
| `launch.transfer.receive` | `true` | Bring sessions here from the account's other machines (`sessions.transfer`) |
| `launch.transfer.maxBytes` | `26214400` (25 MiB) | The largest session bundle, compressed, this machine sends or takes: 1 MiB to 200 MiB. A transfer needs it on both machines |
| `launch.transfer.maxSourceBytes` | 10 × `maxBytes` | The most read from disk to pack one, and unpacked to install one: from `maxBytes` to 20 GiB |
| `launch.remoteFolderTrust` | `false` | A person may answer a launched agent's folder-trust question (Trust or Quit) from their phone, browser or Windows app, with a command that device signs ([v1-launch.md](protocol/v1-launch.md#folder-trust)). Off: the question is shown on those devices read-only, to answer at the machine. Set it here, with `agent-bridge-host config set launch.remoteFolderTrust true` (applies when the host restarts), or in the Windows app's Settings (applies at once, so a question already waiting can then be answered); never from another device. Trusting a folder lets the agent run what is in it |
| `launch.workerMcp` | `{}` | For each `role` a coordinator may pass on a launch, the MCP servers (by name, as in the owner's `<Copilot home>/mcp-config.json`) a worker started for that role keeps, e.g. `{"review":[],"github":["github-mcp-server"],"workiq":["workiq"]}`. A role not listed keeps none. Applies only to a direct `copilot` launch that carries a role, never to a launch without one (the owner's own) and not to Agency, which `--profile-only` already narrows. The host reads the server names from the owner's file at every launch (never cached) and passes `--disable-mcp-server <name>` for each one not listed, and `--disable-builtin-mcps` unless `github-mcp-server` or `githubiq` is listed. The log says counts only, never a name. A file that cannot be read refuses the launch. A server added between the host's read and Copilot's own read of the file (milliseconds) would load. Role names are letters, digits and hyphens, matched exactly |
| `launch.copilotAccount` | absent | The GitHub login a Copilot or Agency launch must run as. Every such launch (new, resume, restore) gets `COPILOT_GITHUB_TOKEN`, read from the user-level environment when the launch starts, and the host checks it first with its own two calls (`/user`, then `/copilot_internal/user`) before any worktree or process exists. Absent: any valid token with premium quota passes; set: the token's login must match (case-insensitive). Refused with a plain reason when the token is unset or invalid, the login differs, premium interactions are zero, or the check cannot complete ("could not check the account"). Quota not reported is accepted and logged. A stored login (`danswett`) is never used instead. `GH_TOKEN` and `GITHUB_TOKEN` in the session are left alone |
| `launch.remoteFolderTrustDevices` | `[]` | The devices, by device id (32 hex), a person approved here to answer folder-trust questions from. The relay's kind for a device is self-declared, so this list is what lets one answer: with `launch.remoteFolderTrust` on and none approved, nobody can answer from a device. Approve a device in the Windows app's Settings (applies at once), or with `agent-bridge-host config set launch.remoteFolderTrustDevices '["<id>"]'`; a device that may not answer is told its short id. At most 64; invalid ids are ignored with a problem |
| `homeAssistant.enabled` | `false` | The Home Assistant connector ([ha-connector.md](ha-connector.md)): this machine's sessions on the existing agent-ha-bridge dashboard. Runs only while agent-ha-bridge does not, and only with a token (`agent-bridge-host ha token set`, kept protected beside the device key, never in this file) |
| `homeAssistant.baseUrl` | none | The Home Assistant, `http` or `https`, with no credentials in it. Required when enabled |
| `homeAssistant.dashboardUrlPath` | `agent-decisions` | The dashboard agent-ha-bridge's writer generates. Only read, to know when an ended session's card has gone |
| `homeAssistant.detailedActivity` | `true` | What cards carry when this machine has no Detailed activity switch in Home Assistant: the connector creates one when it is missing, so only while it cannot (docs/ha-connector.md, stage 7) |
| `homeAssistant.machineName` | the computer's | The name the entities are keyed on (`agent_bridge_<slug>`). Only for a rehearsal posing as another machine on a disposable Home Assistant |
| `homeAssistant.disposable` | `false` | Says `baseUrl` is a disposable Home Assistant. A development build writes to no other |
| `homeAssistant.agentUserIds` | `[]` | Home Assistant user ids whose presses are an agent's, as agent-ha-bridge's own setting: an answer or reply they send makes the next turn agent-driven (the card's purple edge). A list of strings; `migrate` carries agent-ha-bridge's ([migration.md](migration.md)) |
| `homeAssistant.agentTokenEnvVar` | none | Legacy variable-name metadata carried by `migrate`. The native launcher does not resolve it or inject an HA token; ordinary inherited environment is not managed token propagation. Not the proposed native agent credential (#519); see [ha-connector.md](ha-connector.md#credentials-agent-attribution-and-notifications) |
| `homeAssistant.notifications.enabled` | `false` | The pushes agent-ha-bridge's hooks sent (a question, a permission prompt, a blocked Claude, a finished turn), sent by the connector (stage 5b) |
| `homeAssistant.notifications.service` | `notify.notify` | The notify-style service they go to (`notify.mobile_app_<phone>`, `ticker.notify`) |
| `homeAssistant.notifications.tickerCategory` | `""` | The category a `ticker.*` service is given |
| `homeAssistant.answerButtons.enabled` | `true` | A waiting question's options as buttons on the phones below, answered by a tap |
| `homeAssistant.answerButtons.services` | `[]` | The `notify.mobile_app_*` services to send them to. None: nothing is sent |
| `homeAssistant.answerButtons.maxButtons` | `6` | Buttons per notification, besides "Type an answer" |
| `homeAssistant.answerButtons.icon`, `iconColor`, `color`, `interruptionLevel` | `mdi:chat-question`, `#FFFFFF`, `#FF9F0A`, `time-sensitive` | The notification's styling; an empty `icon` or `interruptionLevel` leaves it out |
| `homeAssistant.updates.checkForUpdates` | `true` | Whether the machine's update entity looks for this host's own releases and serves its Install and Check buttons (stage 6), as agent-ha-bridge's `updates.checkForUpdates`. Only on a copy its updater installed |
| `homeAssistant.updates.checkHours` | `0.5` | How often, in hours (0.1 to 168), as agent-ha-bridge's `updates.checkHours`; a press of Check looks at once |
| `usage.publish` | `true` | Reading the agents' allowances at all ([v1-usage.md](protocol/v1-usage.md)). `false` asks no vendor anything: `hello` leaves `usage` out, the host neither asks the account's other hosts for their readings nor keeps them (#172), and the Home Assistant connector withdraws this machine's usage sensors rather than leave their last readings with nothing to refresh them (agent-ha-bridge leaves them) |
| `usage.intervalSeconds` | `120` | How often an agent's allowance is read while a session of that agent is open here, from 30 to 86400. While agent-ha-bridge runs for this user the host reads only at `idleIntervalSeconds`, since that bridge is already asking |
| `usage.idleIntervalSeconds` | `900` | How often it is read while none is, from 30 to 86400, and never more often than `intervalSeconds` |
| `usage.copilotAccounts` | `[]` | Further GitHub logins whose Copilot allowance this machine reports beside the account Copilot is signed in as, as agent-ha-bridge's own setting: up to three, each its own row on the phone and the app, under its login, and on the Home Assistant connector's `usage_copilot_2`, `_3` or `_4` by its place in the list. Each is read with the token `gh` holds for exactly that login (`gh auth token --user <login>`) and nothing else, at the idle pace, one at a time; a reading that answers for another login is refused. So `gh auth login` as that account is all the setup there is. Logins only, never tokens |
| `devBox.keepAwake` | `false` | On a Microsoft Dev Box, keep the machine from hibernating on disconnect while the host runs ([below](#dev-box-keep-awake)), as agent-ha-bridge's own setting. Set it in the Windows app's Settings (shown on a Dev Box only; applies at once) or here (applies when the host restarts). `migrate` carries agent-ha-bridge's. Nothing happens on a machine that is not a Dev Box |
| `devBox.intervalMinutes` | `15` | How often a pass runs, from 1 to 30. More than 30 is read as 30 and said, and `config set` refuses it: a pass less often than the pool's grace period (never under 60 minutes) can miss the pending stop entirely |
| `agentMessaging.remote` | `"off"` | The temporary AP5-lite mode (#728): `"off"` or `"own-account"`, for the owner's own agents on the owner's own account, ahead of the signed roster (#55), which still gates everything beyond it. The runbook is [protocol/agent-lite.md](protocol/agent-lite.md). Off means nothing of it is active, and an install or upgrade never writes it. Read afresh for each message, so changing it needs no restart and switching it to `"off"` holds from the next message; a message not sent this way goes over Home Assistant. This slice holds only the setting: nothing sends or receives yet |
| `agentMessaging.allowAllTargets` | `false` | Counts only while `agentMessaging.remote` is `"own-account"`. `false`: own agents may not type into allow-all sessions, and those targets fall back to Home Assistant. Left `false` until the owner decides otherwise |
| `agents.enabled` | `false` | The agents policy (#720, [design](design/agent-principals.md), "The per-machine policy"): master switch for agent-to-agent commands on this machine, both directions. **Changed only at the machine**, in this file or with `agent-bridge-host config` (refused inside agent sessions); no request from another device or an agent reads or changes it. Absent section: agents are off, every grant below is off, and the limits are the defaults. This slice only parses and validates it; nothing consumes it yet |
| `agents.credentialDays` | `7` | How long an agent's credential lives, from 1 to 7 |
| `agents.issue.launched`, `agents.issue.adopted` | `none` | The scope a credential gets at issue: `none`, `read`, `read+reply` or `read+reply+launch` |
| `agents.outbound.remote` | `false` | Whether this machine's agents may reach other machines |
| `agents.outbound.allowAgentLaunchedToLaunch` | `false` | Whether a session an agent launched may itself launch |
| `agents.inbound.local.*`, `agents.inbound.remote.*` | all off | Agents on this machine, and on the account's other machines. `read`: `any`, `agent-launched` or `none` (`none`). `reply`: `forced-ask`, `agent-launched` or `none` (`none`). `gate`: `off`, `quiet` or `push` (`off`). `allowAllTargets`: `false` or `{ "workspaces": "same" or "any" }` (`false`). `coordinationGroups`: `false` or `{ "maxHours": 4 to 24, "perMemberPerDay", "perGroupPerMinute" }` (`false`). `launch`: `false` or `{ "workspaces": "approved" or [labels], "permissions": "ask", "agents": [ids], "profiles": [names] }`, with `permissions` stated (`false`). `inbound.remote.fromMachines`: `"any"` or a list; none by default |
| `agents.inbound.exclude` | `{ "sessions": [], "workspaces": [] }` | Session ids and workspace labels no agent may reach |
| `agents.limits.*` | `maxDepth` 3, `conversationMessages` 20, `conversationMinutes` 60, `perPairPerMinute` 6, `perTargetPerMinute` 12, `launchesPerOriginPerHour` 3, `launchesPerMachinePerHour` 10 | Each can only be lowered: the default is also the ceiling, and zero, a negative, or a larger number is read as the default and said |
| `agents.label` | `prefix` | `prefix` or `none` |
| `agents.links` | `[]` | Must be empty in this version |

Reading `agents` fails closed. A section that is not an object, or a nested object (`issue`, `outbound`,
`inbound`, `inbound.local`, `inbound.remote`, `inbound.exclude`, `limits`) that is not one, denies everything
(the policy is off) and is said. A value of the wrong type or out of range takes that key's narrowest
default, never a wider grant, and is said; an unknown key is ignored and said. A `config.json` that cannot
be read or parsed leaves agents off. Each problem is listed with the other section problems in the log, `config show` and
`config set`'s check. Other sections still load.

How a file that is wrong is read: one that cannot be read or parsed turns remote approval,
launching, notifications and agent usage off and makes the host read-only ("this host's config.json has a
problem"); a `permissions.remoteApproval`, `permissions.remoteManage`, `push.enabled` or `delivery.readOnly` that is not
`true` or `false` does too. An unreadable, invalid or newer-schema file also enables
trusted-peer enforcement rather than silently downgrading it; a malformed
`permissions.trustedPeers` does the same. In `usage`, a `publish` that is not `true` or `false`, or a `usage` that is
not an object, turns usage off; a pace that is not a number in range keeps its default; and an entry of
`copilotAccounts` that is not a login, repeats one, or is past the third is left out and keeps its place,
so the logins after it keep their rows and sensors. An empty string (`""`) is a place kept unused on
purpose, and is not a problem: `["", "second-login"]` reports `second-login` second. Each is said in the log by its place, never repeated:
one shaped like a token is refused. In `launch`, a workspace entry that is wrong (no path, a relative path, an `isolate`
that is not `true` or `false`) is left out and said in the log, and any other value that is
wrong turns launching off. In `homeAssistant`, any value that is wrong turns the connector off
and is said in the log. In `devBox`, a `keepAwake` that is not `true` or `false`, or a `devBox` that
is not an object, leaves keep-awake off; an interval that is not a whole number from 1 is 15. In
`agentMessaging`, a `remote` that is not exactly `"off"` or `"own-account"`, or an `agentMessaging`
that is not an object, is off, and an `allowAllTargets` that is not `true` or `false` is `false`;
each is said, and `config set` refuses to write it. A setting
someone may have meant as off is never read as on.

## Trusted peers: disposable-host foundation

`permissions.trustedPeers` is **off by default** and read only at startup. This
transitional host-only floor (#402) is for disposable-host evaluation, not production
enrollment or authorization to migrate a live machine. Disabled mode retains the
legacy relay-admission authority model and is not suitable for scoped services.

In enforcing mode, relay presence, names, intro kinds and short routing IDs grant
no authority. Every relay channel, in either handshake direction, needs a locally
pinned full Noise static public key. Every request is checked again:

| Local role | Allowed remote operations |
|---|---|
| `service` | `hello`, `sessions.list`, `session.get`, `sessions.subscribe` |
| `host` | `hello`, `usage.get` (including shared readings), `session.transfer` |
| `person` | Existing explicitly registered person operations, except `session.transfer` |
| Unpinned | None |

Unknown operations are denied. `hello` advertises only the intersection of role and
host capabilities. All existing read-only, folder approval, MAC and replay checks
still apply. Transfer carriers and outbound usage/transfer peers need host pins;
transfer and folder-trust signers independently need person pins at the machine
whose authority they request. A person's approval at another host is insufficient.
Historical usage from devices without current host pins is discarded.
This boundary covers relay channels, not the existing HA MQTT connector or local
pipe: those retain their own caller and command authorization checks.

Pins are kept in version-1 `trusted-peers.json`, bound to this host's full key and
signed-in account. Each has a full canonical SEC1 base64url key, role, monotonically
increasing generation, explicit future expiry and revocation state. There are at most
256 pins. Missing, invalid, truncated or newer-schema state denies all authority
until an explicit reset. Before each mutation, including reset, a separate
`trusted-peers.blocked` marker is durably written. Only a successful commit clears
it (a blocked store keeps it). A failed or interrupted mutation therefore denies
all authority across restart, even when the previous grant file remains intact.
Recovery requires the guarded owner reset, which creates an empty new trust epoch;
do not delete the marker by hand. If even the marker cannot be written, the API and
log explicitly report **"revocation NOT durable; do not restart"**: the process
denies access, but restart safety cannot be promised until a successful owner reset
after repairing storage. The mode switch is in `config.json`, not the trust store:
deleting the trust store cannot turn enforcement off.

Pin management is currently an API-only developer foundation through the fresh,
guarded **app-only local pipe**, not a CLI bypass, remote operation, enrollment UI
or import from relay presence:

| Operation | Data |
|---|---|
| `host.peers.status` | None; returns mode, any store problem and pins |
| `host.peers.reset` | None; explicit empty trust epoch for the signed-in account |
| `host.peers.put` | `{publicKey, role, expiresAt}`; full key and explicit UTC expiry |
| `host.peers.revoke` | `{publicKey}`; full key already pinned here |

Missing or wrongly typed key, role or expiry fields return a field-specific error
before the requested mutation. Store validation still checks the full curve key,
known role and future expiry. Expiry timestamps must include `Z` or a numeric UTC
offset; offset-less timestamps and bare dates are rejected, not interpreted in the
host's local time zone.

The owner must transfer each full key over an independently authenticated,
owner-controlled path, including host keys. A key learned from the relay or a
short fingerprint is not approval. Reset invalidates every old lease. Reapproval
creates a new generation and requires a new channel; it cannot upgrade an old one.

Open channels and feeds are revalidated on pin-management changes and every 250 ms;
incoming frames, dispatch and outgoing encrypted sends also check the lease. Active
expiry has a monotonic deadline. This deliberately strict pilot latches deny-all on
any observed backward wall-clock movement, including clock corrections, until an
owner reset. Trust-file edits are not live-reloaded: use the guarded API, not file
deletion/editing, for revocation. A restarted host reloads and validates the store.

After restart, rollback is detected only relative to the last saved trust mutation
or recorded expiry, not the most recent access. Ordinary access does not persist
the clock watermark, even on active hosts. A clock rollback plus restart can
therefore extend wall-clock expiry without restoring any files; use revocation to
end access durably. No periodic durable clock checkpoint is implemented.

Saved push subscriptions bind the full person key, trust epoch and generation.
Enforcing startup drops legacy/unbound subscriptions; an authorized person must
register again. Enqueue, delivery and retry paths recheck the grant; revocation,
expiry or reset removes unauthorized subscriptions and queued notices. Reapproval
cannot revive an old subscription or queue. A push already handed to the sender
cannot be recalled, nor can a completed action be undone; revalidation does not
promise transactional cancellation of work already admitted.

This is not a boundary against arbitrary processes running as the same OS user,
which can read/change that user's files. Full-store or whole-machine rollback
cannot be detected from those rolled-back files alone. Restoring a backup requires
owner-controlled reset and reapproval, not automatic old-grant activation. Signed
account rosters, relay credentials/revocation anchors, HA key storage, recovery,
enrollment and production enablement remain separately gated.

## Phone sign-in

`permissions.phoneSignIn` (#972) lets a person sign Copilot in again from their own device when
its sessions on this machine need it, without going to the machine
([v1-sessions.md](protocol/v1-sessions.md#phone-sign-in-800-slice-2-enabled-per-machine-by-972)).
It is **off by default** and set on each machine separately. What it runs is Copilot's own
`copilot login --device-code`, unpatched: the one-time code goes only to the device that asked,
over its encrypted channel, and **this machine opens a browser and copies the code** while it
runs (accepted by Engineering, #800); a browser the login started is closed when the flow ends. Nothing is typed into a session, and the code never reaches
a log, a notification, Home Assistant or a transcript.

It is offered to a device only when all of these hold:

- `permissions.phoneSignIn` is `true`, and the host has restarted since;
- the machine runs Windows, and when the host started it found Copilot as an `.exe` the way
  launching does (`launch.agents.copilot.path`, else `PATH`; a `.cmd` shim is never run);
- the device is **pinned as a person's** by this machine's copy of the account's device roster
  (#55, [device-approval.md](design/device-approval.md)): an approved device of kind `app`, `pwa`
  or `phone`, with a presence-proven key (`key` or `webauthn`), past probation and confirmed,
  found by its full key and relay account. The kind a device says it is counts for nothing. Being
  signed in to the account is not enough;
- the machine is not read-only (`delivery.readOnly`, or agent-ha-bridge running for this user).

**Until #55's roster ships, nobody is pinned, so no device is offered sign-in on any machine**,
with the switch on or off (#972 slice 2): the host's roster is `NoPersonRoster`, which pins no one.
**A #402 trusted-peer `person` pin (`host.peers.put`) no longer opens sign-in.** Where
`trustedPeers` enforcement is on, a channel still needs that pin to act at all, but it is never
enough. While nobody is pinned no session's `signInNeeded` attention says `signIn: "phone"`
either, so no device shows a Sign in that would be refused. A roster that can't be read, or is
stale, pins nobody; a device removed from it loses a sign-in under way at once.

The app's Settings lists, under the switch and only while the running host has it on (a change
applies at the next start, and until then the devices listed can still sign in), **Devices that can sign
Copilot in**: each by name, with its kind and platform, read-only. It reads "No devices can sign
Copilot in here yet." until the roster lists someone, and "Can't read the device list on this
machine." when the roster can't be read. A device's Machines page shows the same in that
machine's row. Neither adds nor removes a device: adding is #55's enrollment, and removing one
device waits for #55's single Remove; to stop them all, turn the switch off.
**To turn it on for one machine** (the owner, at that machine or over RDP; an agent session is
refused, #67):

1. Open the Slipgates app → **Settings**, and turn on **Let my devices sign Copilot in on this
   machine**. Or, in a terminal that is not inside an agent session:
   `agent-bridge-host config set permissions.phoneSignIn true`
2. Restart the host: **Restart** in Settings (shown once a change needs it).
3. Check the host log (Settings → the log, or `logs\host.log` in the profile folder) for
   `sign-in from devices is on for Copilot (permissions.phoneSignIn)`. "is set ... but off" means
   the host is not on Windows or did not find Copilot's `.exe`; "is off" means the setting did not take.
4. On a pinned device (once #55's roster lists one), a session needing sign-in on that machine shows **Sign in** in its banner. Press it,
   open `https://github.com/login/device` and enter the code shown. The session then reads "Signed in ·
   waiting for the session to confirm" until its next reply clears it.

**To turn it off:** the same switch off (or `config set permissions.phoneSignIn false`), then
Restart. A value that is not `true` or `false` turns every permission off and is said in the log.
Devices cannot change it yet; that waits for #801's settings path.

## Machine and session load in the log

So a saturated machine is visible (#1150), the host takes one shared snapshot of the machine's
processes every 5 seconds and writes a line to `logs\host.log` at most once a minute:

```
load: machine cpu 92% of 8 cores, 310 MiB memory available; sessions: 3f2a9c1d cpu 41% working set 880 MiB 6 procs; 9be0aa12 cpu 7% working set 420 MiB 3 procs
```

- Each session's figure covers its process and everything below it, found through the process
  the host already records for the session (id plus start time, so a reused id is not counted).
- `cpu` is the processor time used between two snapshots, as a share of the whole machine, so
  it is a rate over the last 5 seconds, not a point reading. The first snapshot has no earlier
  one, so it reads `unknown`.
- `working set` is the memory resident in RAM. Pages shared between processes count once per
  process, so the sum can overstate.
- `unknown` means the host could not read it (for example a process it may not open); it is never
  shown as 0. A session marked `partial` or `unavailable` is missing some or all of its processes.
- No paths or command lines are read or written. Session ids are cut to 8 characters in the log.
- The per-session figures can also ride the sessions sensor under a `load` attribute, keyed by
  node. That stays off: it needs both `HaConnectorOptions.PublishLoad` and
  `HaConnectorOptions.PublishSessionLoad`, and the host sets only the first.

### Fleet load feed (machine totals only)

The owner cleared `HaConnectorOptions.PublishLoad` for machine totals only (17:39 PT, Nov 9; recorded
on #1150). With it on, the connector publishes one extra per-machine sensor,
`sensor.agent_bridge_<machine>_fleet_load`, every 10 seconds (#1587). With it off, nothing is
published. Its state is `observedAt` (the sample time); its attributes are this record, version 1:

| Field | Meaning |
|-------|---------|
| `version` | always `1` |
| `machine` | the machine's name |
| `observedAt` | when the sample was taken |
| `cpuPercent` | mean of the last 10 s (two 5 s samples) |
| `cpuPeakPercent` | highest single sample in that window |
| `cores` | processor count |
| `availableMemoryMiB` | memory available |
| `sessions` | the host's live-session count |
| `runQueue` | Processor Queue Length, latest sample |
| `runQueueAvg`, `runQueueMax` | mean and highest over the window |

Every number is `null` when unknown, never 0. There are no process names, paths, session ids,
titles or per-session rows. A record is stale when `observedAt` is more than 30 s old; nothing else
is used to judge it.

`docs/fleet-load-limits.json` holds the limits the fleet judges the record against:
`maxCpuPercent` (1-100), `minAvailableMemoryMiB` (above 0), `maxRunQueuePerCore` (0 or more),
`maxAgeSeconds` (above 0) and `nullFails` (true or false; true means an unknown figure fails). A
test rejects a file with any value out of range or missing.

## Notifications in the log

Each push the host makes leaves one line in `logs\host.log` for each try, so the time a
notification takes can be split between the host and the push service (#1190):

```
push question session=3f2a9c1d device=a1b2c3d4 queued=2026-11-08T00:47:55.165Z decided=+2003ms sent=+2004ms response=+2391ms status=201 attempt=1 outcome=delivered
```

- `queued`: when the host first saw what the notice says (the question asked, the turn finished,
  the answer given), in UTC. The other times are milliseconds after it.
- `decided`: when the host made the notice. It waits on purpose: 2 s for a question and its
  answer (`QuestionDelay`), 3 s for a finished turn (`FinishedDelay`).
- `sent`: when it was handed to the relay. Later than `decided` while the relay was down, the
  limit of 20 a minute was reached, or a retry waited.
- `response`: when the relay said how the push service took it; `status` is the push service's
  HTTP status (0: the relay's own, such as unreachable). `outcome` is `delivered`, `retrying`,
  `gone` (the subscription ended), `refused`, `not-allowed` (the device lost its grant meanwhile),
  or `relay-down` (kept for the next connection).

A notice that waits or never goes says so on a line of its own: `held=collapse until=+…ms` for a
finished turn held back because the device had one for that session in the last 10 minutes
(`FinishedCollapse`), and `dropped=…` with `at=+…ms` for one that will not go: `on-screen` (the
device has the app open and sees it), `collapse-moved-on`, `turn-failed`, `answered`,
`signed-in`, `expired`, `unsubscribed`, `not-allowed` or `unencryptable`. While the person uses a
device (#1190; the rule is in [v1-sessions.md](protocol/v1-sessions.md#when-a-host-notifies)), a
finished turn is `dropped=in-use`, and a question whose card a device in use shows
`dropped=card-on-screen`; on the other devices the same reason ends in `-elsewhere by=` and the
device in use.

Sessions and devices are named by the first 8 characters of their ids; the grouped, sign-in,
restore and machine notices by their own keys. No line has the notice's text, an endpoint or a
whole id.

## The agent audit log

What agents did on this machine is written one JSON line per event to
`<profile>\audit\agents-YYYY-MM-DD.jsonl` (#1296, design/agent-principals.md §5.6). The folder
and files are readable by this user alone, like `agent-credentials.json`. Each line has `at`,
`direction` (`out`, `in` or `local`), `request`, `principal` (`agent:<machine>/<session>`, never
the owner), `chain`, `action`, `target`, `outcome`, `reason`, and, when the event carried text,
`dataSha256` and `textLength`. The text itself is never written; a reason is cut at 200
characters, every other field at 256, and the chain at 16 entries.

The host keeps at most 30 days and 64 MiB, deleting the oldest day's file first and never the
file being written. That file is bounded too: once a line would take it past 64 MiB the write
returns false and the audited action is refused until the next day. A future-dated file (a clock
stepped back) does not count toward the size. Age pruning never trusts a clock that has jumped ahead of the newest earlier
file, and size pruning stops at a file it cannot delete. A line torn by a crash is not joined to
the next event. An unwritable log (a read-only folder, a full disk) refuses the action being
audited, as the #1401 row in design/agent-launch-scope.md requires: the log says so once in the
host log and again when writing resumes, and the caller must refuse and release any slot when the
write returns false. Nothing calls the log yet: the agent operations that write to it arrive with
#720 slice 4 and #1170.

## Approving a workspace at the machine

```
agent-bridge-host workspace list
agent-bridge-host workspace add C:\Users\me\repos\bridge --label Bridge --isolate
agent-bridge-host workspace remove Bridge
```

`add` refuses a folder that does not exist, a system folder, one already approved, a label in
use, and `--isolate` outside a git repository. Every command takes `--profile <name>`.

`workspace add` and `remove`, `config set` and `unset`, `ha token set` and `clear`, and
`sign-in` are for a person. They refuse to run (exit code 4) inside an agent session: when
anything they run inside is an agent CLI (`copilot`, `claude`, `codex`, `agency`, or Node or Bun
running one), or when `AGENT_BRIDGE_SESSION` is set, as it is in every session the host
launches. They refuse too when the process tree they run in cannot be read in full (an
ancestor `/proc` will not show, or `ps` failing on macOS), since an agent could be above what
was read. Run them in a terminal of your own. `list`, `show` and `ha status` work anywhere. Why, and what this does not stop:
[local-pipe.md](protocol/local-pipe.md#who-may-manage-the-host).

The rest of the file:

```
agent-bridge-host config show
agent-bridge-host config set launch.defaultAgent copilot
agent-bridge-host config set launch.permissions allowAll
agent-bridge-host config set launch.agents.copilot.models '["gpt-6.1-sol","claude-opus-5.5"]'
agent-bridge-host config unset launch.permissions
```

A value is read as JSON when it is JSON (`true`, `12`, `["a","b"]`), and as text otherwise.
Only the keys above can be set. Each change is read back as `serve` would read it first, and
one that would leave a problem is not written; the file is replaced in one step. The
commands rewrite the file without its comments.

Then restart `serve`. It logs `launching sessions is on: 1 workspace(s) approved (Bridge
[worktrees])`, or why launching is off.

## Dev Box keep-awake

A Microsoft Dev Box pool commonly has **stop-on-disconnect** on: the Dev Box agent hibernates
the machine a grace period after the last RDP or tunnel session goes, however busy it is, since
idleness there is counted in sessions, not load. The host stops mid-turn and its machine reads as
offline with nothing saying why. With `devBox.keepAwake` on, the host does what agent-ha-bridge's
`AgentBridgeDevBoxKeepAwake_<id>` task did (design §6.1, `Host.DevBox`, #47), inside the host
instead of as a task of its own:

* **Which Dev Box.** Read from the Dev Box agent's own settings
  (`C:\Program Files\Microsoft Dev Box Agent\…\appsettings.Production.json`): its Dev Center, project
  and name, afresh for every pass, so a reprovision is followed. A machine without the Dev Box
  agent is not a Dev Box (`not-a-devbox`, said once), and nothing is done. A Dev Center address
  that is not `https://<name>.devcenter.azure.com` is not followed, since the user's token would go
  there. Settings that are there but cannot be used (not JSON, a shape not known, such an address,
  or the agent installed with no settings file found or searchable under it) are a Dev Box that
  cannot be kept awake: with keep-awake on, every pass is `failed` and says so, and the app keeps
  showing the section with the alarm.
* **As whom.** The Azure CLI's sign-in: `az account get-access-token --resource
  https://devcenter.azure.com`, run with no window (`az.cmd` through `cmd.exe`). Dev Center's
  developer API under `users/me` is the only scope that lets a user skip or delay their own Dev
  Box's actions; nothing on the Dev Box agent is reconfigured. The token is never logged.
* **What a pass does.** It lists this Dev Box's actions (`api-version=2024-02-01`) and, for each
  `Stop` action with an occurrence scheduled, asks to **skip** it; if that is refused, to
  **delay** it by 8, 6, 4, 2 or 1 hours, largest first (a delay may not land more than 8 hours past
  where the occurrence first was, and the service never says where that was). A stop more than
  **24 hours** away cannot be moved and need not be: that is `safe`, and only the list is read.
  Nothing else is ever read or moved: no other action, no other Dev Box, nothing of the pool's.
* **When.** A first pass 30 seconds after the host starts (a Dev Box nobody is connected to may
  have a stop already pending), then every `devBox.intervalMinutes`. The occurrence exists only from
  the last session leaving until one grace period later, and the grace is never under 60 minutes,
  so a pass every 15 minutes sees it four times; 30 is the most that still sees it twice. Turned on
  from the app, a pass runs at once.
* **One at a time.** A pass takes `%LOCALAPPDATA%\AgentBridge\devbox-keepawake.lock` without
  waiting, so two profiles' hosts never move the schedule together, and holds the mutex each of
  agent-ha-bridge's keep-awake passes takes (`Local\<its task name>`) for every such task registered
  here, so a pass of that task started meanwhile (a rollback enables and starts it) exits without
  a cloud call. A pass that finds either taken is `busy` and calls nothing; one that cannot read
  Task Scheduler to tell is `failed`, not a quiet `busy`.
* **Never beside agent-ha-bridge's own.** Before every pass, the host looks for agent-ha-bridge's
  `AgentBridgeDevBoxKeepAwake[_<id>]` tasks and the mutex each of its passes holds. While any is
  enabled or running, or the scheduler cannot be read, the host stands back (`stood-back`): two
  writers to one schedule is the problem G5 exists for. Standing back only because the scheduler
  could not be read is alarming, since nothing is then known to keep the Dev Box awake. This
  holds for every host, read-only (`delivery.readOnly`) or not. `migrate` disables that task and
  carries `devBox.*`, so the host takes over; `rollback` enables it again, and the host stands
  back from its next pass.
* **What it says.** Every pass is a line in the host's log, beginning `keep-awake:` and its
  outcome: `skipped`, `delayed`, `safe`, `no-action` (nothing scheduled: the normal state while
  anyone is connected), `blocked` (a stop the service would not move, or levers that never reached
  it), `failed` (no `az login`, Dev Center unreachable or refusing, the lock file unusable, the
  Dev Box agent's settings unusable), `stood-back` or `busy`. `failed`, `blocked` and a
  `stood-back` with the scheduler unreadable add `THIS DEV BOX MAY HIBERNATE WHEN DISCONNECTED`,
  on every pass. The app's Settings shows the switch on a Dev Box only, with the last pass and the
  next, and those in red (`devBox` in the host's status, [local-pipe.md](protocol/local-pipe.md)).

```
agent-bridge-host devbox status
agent-bridge-host devbox dry-run
```

`status` reads only this machine: whether it is a Dev Box, the profile's `devBox` settings, and
whether agent-ha-bridge's keep-awake is on here. `dry-run` is agent-ha-bridge's `-DryRun`: one
pass that signs in with `az`, lists this Dev Box's actions and says what it would clear,
changing nothing (exit 1 when it failed). There is no pass by hand that moves the schedule:
`serve` runs those.

This is a reprieve per occurrence, not a policy change, and a Dev Box kept awake keeps costing
money, so it is off unless turned on. The durable fix is for whoever runs the pool to raise its
`stopOnDisconnect.gracePeriodMinutes` or turn stop-on-disconnect off.
