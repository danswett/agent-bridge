# Protocol v1: managing a machine from a person's device (`manage.*`), proposed

> **Status: proposed (#801, phase 1: design and host contract only).** Nothing here is
> built. Connection (`eb29d6d8`, owner of #800's auth state) reviews the host contract, and
> Engineering runs the independent review (rule 5), before any code is written. Phase 2 comes
> after #800 and the P0s. Stop, config and Home Assistant token changes also wait for #55.
> **Revision 2 (2026-11-05):** the owner's decisions on #801 folded in (§11); restart now leaves
> only through #805's shared quiet lease (§2.6), replacing this page's own idle test.

Today the owner must be **at the machine** to restart or stop a host, repair its hooks, change
`config.json` or give the Home Assistant connector its token: the #67 guard (correctly) refuses
all of that from agent sessions, and the only other path is the app's Settings or a one-liner at
the machine. That breaks the no-RDP tenet (#696). This page specifies how a **person's** approved
phone, browser or Windows app does those things for another machine, through the relay, without
creating any new way for an agent to do them.

It follows the remote-update precedent ([v1-update.md](v1-update.md), #51, built in #160, with
#161's follow-ups) rule for rule, and says where it is stricter and why.

## Contents

1. [What a person can do](#1-what-a-person-can-do)
2. [Who may ask](#2-who-may-ask)
3. [The wire contract](#3-the-wire-contract)
4. [Each command](#4-each-command)
5. [The Home Assistant token, end to end](#5-the-home-assistant-token-end-to-end)
6. [Old and new values](#6-old-and-new-values)
7. [Audit](#7-audit)
8. [Restart and stop: what happens and what clients show](#8-restart-and-stop-what-happens-and-what-clients-show)
9. [Threats and mitigations](#9-threats-and-mitigations)
10. [Tests to write in phase 2](#10-tests-to-write-in-phase-2)
11. [Open questions](#11-open-questions)
12. [What blocks phase 2](#12-what-blocks-phase-2)
13. [Out of scope](#out-of-scope)

## 1. What a person can do

| Command | Op | Ships | Waits for |
|---|---|---|---|
| Restart the host | `manage.restart` | Phase 2 | #800 and the P0s (rank), Connection's review |
| Stop the host | `manage.stop` | Phase 2 code, **disabled** | **#55** ([§2.3](#23-why-this-cannot-route-around-67)); the owner decided Stop is allowed only after #55 ([§11](#decided-by-the-owner-2026-11-04)) |
| Repair hooks | `manage.hooks.repair` | Phase 2 | The same |
| Set an allowlisted config key | `manage.config.get`, `manage.config.set` | Phase 2 code, **disabled** | **#55**: never offered until the signed roster is enforced |
| Enter the Home Assistant token | `manage.haToken.set` | Phase 2 code, **disabled** | **#55**, as above |

Everything else stays at the machine, in the app's Settings (which stays the only local path).

## 2. Who may ask

### 2.1 The rules, and the precedent they follow

Remote update decides who may press in `UpdateService` (`src/AgentBridge.Host/UpdateService.cs`):

| Update rule (#160) | Where | `manage.*` |
|---|---|---|
| Through the relay only: never the pipe, where agents, the MCP server and the HA connector are | `UpdateService.cs:14-15`, `:154-156` (`channel is not SecureChannel` throws `ThroughTheRelay`); offered only on a `SecureChannel`, `:80`; added to hello at `SessionService.cs:484` | **Same.** `manage.*` is served only on a `SecureChannel`. On the pipe every `manage.*` op, reads included, throws "managing a machine from another device goes through the relay; on this machine, use the app's Settings" |
| Only a device the relay lists as `pwa` or `app` | `UpdateService.cs:16-19`, `:27` (`PersonKinds`), `:176-178` | **Same set**, `pwa` and `app`. `phone` (native apps, not yet built) is left out until the owner decides ([Q-O4](#still-open-for-the-owner)); transfers already accept it (`SessionTransfers.cs:318`) and update does not, so the three lists differ today ([Q-C1](#for-connection-eb29d6d8)) |
| The device must be connected to the relay now | `UpdateService.cs:176-177` | **Same** |
| Nothing on a read-only machine (`delivery.readOnly`, or agent-ha-bridge running, G5); reads included; presses already taken are let go | `UpdateService.cs:20-21`, `:82-101`, `:157`; `HostStatus.ReadOnly` | **Same.** hello offers no `manage*` capability, and every `manage.*` is refused with `read-only`. A machine that becomes read-only inside the lease refuses the press at `CommitDeparture` (`changed`), and the coordinator cancels its planned state ([§2.6](#26-quiet-the-shared-lease-from-805)) |
| Bounded presses, deduplicated by request, lapsing after ten minutes; three presses of each kind per device per ten minutes; refusals logged at most once a minute per device | `UpdateService.cs:28-37`, `:189-206`, `:218-254` | **Same**, except nothing waits or lapses: a press acts within #805's wait or is refused; limits in [§9.6](#96-rate-limits) |
| Idle rules: the install waits until no session has a turn running or a question waiting for two minutes | `AutoUpdater.cs:27-38`, `:258-267` (`IdleFor`, `Idle`) | **The same intent, through #805's shared quiet lease** (`TryAcquireQuiet`/`QuietLease`, [§2.6](#26-quiet-the-shared-lease-from-805)), not `AutoUpdater`'s test. **Stricter on the upper bound**: update installs anyway after six hours (`WaitAtMost`); a remote restart or stop that is not quiet within the lease's bound is **refused** instead, because v1-update.md itself says an action pressed long before "must not go ahead on sessions never watched for it" |

`manage.*` is **stricter than update** in four ways, because restart and stop interrupt
delivery (the 2026-10-15 incident was a restart) and config and the token change what the machine
does:

1. **Every command is a signed command** (design §5.5), verified by `SignedCommands.Verify`
   (`src/AgentBridge.Host/SignedCommands.cs:45-76`) with `principal.kind == "user"`, `target.machine`
   this host, a lifetime of at most **two minutes** (transfers allow ten, `Transfer.cs:31`), and taken
   once through `CommandMemory` (`SignedCommands.cs:81-100`). Update takes a bare request UUID.
2. **The signer must be the channel's own peer.** Unlike a transfer, where a machine carries a
   person's command (`SessionTransfers.cs:323-331`), no machine ever carries a `manage.*` command:
   `principal.device` must equal `SecureChannel.PeerId`. So no host, and no agent on one, can be
   asked to forward one.
3. **This machine's own app may not use the remote path.** The host refuses a command signed by
   the device key of the Windows app beside it (`local-device`): the app has Settings for that, and
   refusing its key closes the easiest impersonation, an agent on this machine using the app's
   DPAPI-protected key, which it can unprotect as the same user. The app tells the host its device id
   over the pipe (an app-only report, like `host.update.report`).
4. **Stop, config and the token need #55** ([§2.3](#23-why-this-cannot-route-around-67), [§2.4](#24-config-and-token-changes-wait-for-55)).

### 2.2 Never an agent, a connector or Home Assistant

| Who | Why it cannot |
|---|---|
| An agent session on this machine, through the pipe | The #67 guard is untouched (`LocalApi.cs:30-43`, `CallerGuard.cs:28-45`). `manage.*` is not a `host.*` op, and is refused on every `LocalChannel` whoever the caller is, the app included ([§2.3](#23-why-this-cannot-route-around-67)) |
| An agent on another machine | It acts through its host, whose relay kind is `host`: refused (`not-person`). Under `permissions.trustedPeers`, the `host` role's allowed ops (`RemoteAuthorization.cs:11`) never include `manage.*`, and `manage.*` goes only into `PersonOperations` (`:12-20`) |
| The MCP server | It is on the pipe: refused |
| The Home Assistant connector, and Home Assistant | The connector talks MQTT to Home Assistant and is on the pipe for nothing here. No entity, button or service the connector serves maps to a `manage.*` op; Home Assistant's only remote action stays Install (#112), withdrawn on read-only machines. A test pins that no HA command reaches `manage.*` |
| A connector or service peer (`service` role) | `service` may read sessions only (`RemoteAuthorization.cs:83`, `:99`) |
| `agent-bridge-host` on the command line | It gets **no** verb that sends `manage.*`, and the `HostCli` exception (`LocalApi.cs:33`, restart for `migrate`) is not widened |

### 2.3 Why this cannot route around #67

The #67 guard stops "an agent following an instruction, or making a mistake, through the obvious
door" (`CallerGuard.cs:41-45`), and says plainly it is a safety rail, not a boundary against a
same-user process set on harm. The remote path adds no new obvious door:

* **Nothing new is reachable on the pipe.** The pipe handler is unchanged. `manage.*` on the pipe
  is refused before it is parsed, for every caller (so even the app's Settings keeps using
  `host.*`, under #67). A test sends every `manage.*` op from an `Agent`, `Other`, `HostCli` and
  `App` caller and expects a refusal and no state change.
* **No local proxy exists.** No host op, CLI verb, MCP tool or HA entity sends `manage.*` for a
  local caller. The only senders are the PWA and the Windows app's view of *another* machine.
* **No machine forwards it.** The signer must be the channel peer ([§2.1](#21-the-rules-and-the-precedent-they-follow)), and it must be a
  person's kind, so an agent cannot get its own host, or another, to carry one.
* **The local app's key is refused** ([§2.1](#21-the-rules-and-the-precedent-they-follow) item 3).

What remains, before #55: a process that deliberately reads the account's relay token from a
host's profile folder, makes a key of its own and connects claiming `pwa`. It can do that on
**any** machine of the account, so it can reach **every other** machine, not only its own; on
those it has no Task Manager. So the test for what may ship before #55 is not "nothing new on this
machine" but **"no more than remote update already allows such a process on every machine"**
(`UpdateService.cs:16-19`): install this machine's own release once its sessions are idle, which
restarts the host.

* **Restart nearly passes it, and the owner accepted the excess (Q-O9, [decided](#decided-by-the-owner-2026-11-04)).**
  An update install stops the host and starts it again, but only when this machine has a release
  pending, so it cannot repeat at will. Before #55, restart is therefore capped at **2 per machine
  in 6 hours**, from all devices together, and leaves only through #805's quiet lease
  ([§2.6](#26-quiet-the-shared-lease-from-805)); its worst case is a host restarted twice in six
  hours, each time with no delivery in flight. That is somewhat more than update allows (update
  needs a release), and the owner accepted it. Once #55 enforces, the cap rises to 6 an hour.
* **Repair passes it, for agents already connected.** It writes only this product's own hook
  registrations for an agent **this installation has already set up**, as Settings' Repair does
  (#670); it never removes anything, never connects a new agent, and interrupts nothing.
* **Stop fails it.** Nothing brings a stopped host back without a sign-in or a Start at that
  machine ([§4.2](#42-managestop)), so such a process could take the whole account's fleet
  offline. **Stop therefore waits for #55**, like config and the token.
* **Config and the token fail it**: they change what the machine does after the attacker is gone,
  and the token is a credential.

Folder trust closed the same gap differently, with a list of devices a person approved at the
machine (`launch.remoteFolderTrustDevices`, `FolderTrustAnswers.cs:15-17`). That needs a visit to
each machine per device, so it is not used here; the owner chose to have Stop wait for #55 instead
(Q-O1, [decided](#decided-by-the-owner-2026-11-04)). #55 closes the door completely: a fresh key is not in the signed
roster unless a person approves its code on an already-trusted device (design §5.4).

### 2.4 Config and token changes wait for #55

From #55 (and design §5.4 to §5.6), stop, config and the token need exactly these:

| # | Needed from #55 | Why |
|---|---|---|
| R1 | Roster entries that carry the device's **kind**, added only by a person's approval of a matching code on an already-trusted device, signed so the relay cannot forge them | "A person's device" stops being what the device says about itself |
| R2 | The host **verifies the chain and pins its head**, refusing an unknown or rolled-back head, and the command envelope's `rosterHead` (§5.5) | A command is bound to the roster it was authorized under; a relay cannot replay an older roster in which a removed device was still present |
| R3 | **Remove device**, effective at once on every connected host | A stolen phone loses the power to change config the moment it is removed |
| R4 | **Grants** (`set grants`, §5.4), with three new names: `manage.lifecycle`, `manage.config` and `manage.secrets` | The owner decides which devices may stop machines, change config, and give credentials. Defaults are [Q-O11](#still-open-for-the-owner); the fresh unlock for config and the token is decided (Q-O5) |

Until all four are on the host and **enforcing**:

* hello never offers `manage.stop`, `manage.config` or `manage.haToken`;
* `manage.stop`, `manage.config.get`, `manage.config.set` and `manage.haToken.set` are refused
  `needs-roster` before the handler looks at their data (no secret is extracted into a value of its
  own; what the transport has already decrypted is covered in [§5](#5-the-home-assistant-token-end-to-end));
* **there is no switch** to turn them on: no config key, environment variable or build flag. The
  gate is the host's roster verifier reporting "enforcing", and `permissions.trustedPeers` (#402)
  does **not** count, because it is a disposable-host evaluation floor (host-config.md). A test pins
  that.

Restart and repair **do not wait**. Once #55 enforces, they too require the signer to be a
rostered person device, with the `manage.lifecycle` grant (defaults proposed in [Q-O11](#still-open-for-the-owner)).

### 2.5 How #800's auth state is consumed

#800 (Connection's) adds `Session.attention` (`signInNeeded`, `error`, `notResponding`), the roll-up
`hello.machine.signIn`, and the `sessions.signin` capability with `signin.start`, `.code`, `.done`
and `.cancel`, which runs an agent's own device-code sign-in at the machine for a person's device.
`manage.*` consumes it as follows; each line is an assumption Connection confirms in
[§11](#for-connection-eb29d6d8).

1. **One person-device predicate.** `signin.start` and `manage.*` ask the same question ("a
   person's device, through the relay, connected now, not read-only"). Phase 2 moves it into one
   shared policy (`PersonDevice.WhyNot`), replacing the copies in `UpdateService`,
   `FolderTrustAnswers` and `SessionTransfers`, so the two features cannot drift. Connection owns
   the predicate; #801 calls it.
2. **A running sign-in flow never dies with a restart.** A pending `signin.*` device flow is not,
   by itself, a quiet blocker in #805's lease ([§2.6](#26-quiet-the-shared-lease-from-805)); but a
   login process the host owns and cannot hand over makes the restart refuse (`changed`) rather
   than kill it. How #800 runs that process is Connection's ([Q-C8](#for-connection-eb29d6d8)).
3. **Restart is never offered as the remedy for `signInNeeded`.** Restarting the host does not
   sign an agent in. Clients do not put Restart on the sign-in banner.
4. **Attention survives a restart.** The new host re-derives `attention` from the transcripts, so a
   restart never clears "Needs sign-in" falsely or makes a dead session look idle.
5. **The host's own relay sign-in is out of scope.** If the host's relay token is refused
   (`RelayState.Refused`), no device can reach it, so no `manage.*` can help. `host.signIn` stays
   app-only on the pipe. That gap is **#822** (`needs-rdp`), named under [Out of scope](#out-of-scope).

### 2.6 Quiet: the shared lease from #805

A remote restart (and, after #55, a stop) leaves only through the **one** primitive #805 defines
for every planned host exit: person restarts, updates (including #819's host-only update) and the
watchdog. It is proposed in #815 (branch `sre/805-host-liveness`, `docs/design/host-liveness.md`, "Shared bounded quiet primitive") as
`src/AgentBridge.Host/LifecycleCoordinator.cs` (new in phase 2); #805 owns its definition, names and
numbers, and this section only says how #801 calls it. #801 has **no idle test of its own** and does
not use `AutoUpdater.IdleFor` or `WaitAtMost`: two definitions of "quiet" would drift, and the app's
six-hour "go anyway" must never apply to a remote press.

| Step | #805's API (as proposed in #815) | What #801 does |
|---|---|---|
| 1 | `TryAcquireQuiet(maxWait, purpose, ct)` returns a `QuietLease` or `Blocked(reason)`. `maxWait` is 0 to 10 s (default 5). The lease atomically excludes new accepted deliveries and terminal mutations, and is provisional for at most 30 s from grant, not renewable | Calls it with `purpose: RemoteRestart` (or `RemoteStop`) and the default wait. Any `Blocked` refuses the press at once; it is never retried or carried out later |
| 2 | `AnnouncePlanned(lease, plan, ct)` returns a `PlannedStateReceipt` or `Blocked`, within the lease (acknowledgement within 5 s) | **Only when the relay negotiated `host-planned-state-v1`**: publishes the planned state ([§8.3](#83-one-planned-state-announcement-shared-with-805)). Otherwise skips to step 3 with `Legacy` |
| 3 | `CommitDeparture(lease, receiptOrLegacy, ct)` returns a `DepartureGuard` or `Blocked`; it consumes the lease and carries the same exclusion across `HostHandover`, with no unlock and reacquire in between | Calls it **before anything is visible to the device**: only once it returns a `DepartureGuard` does the host write `manage-pending.json`, record the request, answer `accepted` and run the existing handover ([§4.1](#41-managerestart)). Passes `Legacy` instead of a receipt **only** for `RemoteRestart` or `RemoteStop` on a relay that never negotiated planned states (the agreed fallback for a person's press; no other purpose may) |
| Abort | Disposing a provisional lease aborts it; after any publish, including an uncertain acknowledgement, the coordinator sends `cancelled` for the same transition. A late acknowledgement cannot revive it | Disposes the lease on every refusal or failure after step 1 |

**What "quiet" means is #805's**, and it is narrower than update's idle test: a turn running, a
question waiting, a discovery scan or a sign-in flow does **not** block it. What blocks it is
work the host itself has in flight and that would be cut off: an answer or reply being typed
(`Typing`), any other delivery, launch, stop or transfer under way (`DeliveryInFlight`), or a
journal or fence the host cannot be sure of (`JournalUncertain`, fail closed). The reasoning: the
agents are separate processes, so a running turn and a waiting question survive a host restart and
are found again when the new host starts (#43); what a restart can break is the host's own
deliveries. That replaces update's "idle for two minutes" for remote restarts, and the bounded
5-second wait replaces the 30-minute lapse earlier drafts of this page proposed. Two things follow
for others:

* **The owner**: "obeying the idle rules" (the #801 issue text) now means "no delivery in flight",
  not "no session busy" ([Q-O7](#still-open-for-the-owner)).
* **#800**: a pending `signin.*` device flow is not, by itself, a blocker. But if its login
  process belongs to the host and cannot be handed over, `CommitDeparture` is refused
  `EligibilityChanged` (here `changed`), so a restart never kills a sign-in a person started.
  Connection decides whether #800's login process is detached, supervised or transferable
  ([Q-C8](#for-connection-eb29d6d8)); #801 assumes none of them.

How a `Blocked` reason reaches the device ([§3.3](#33-results)): `Typing`, `DeliveryInFlight` and
`JournalUncertain` are `not-quiet`, with the blocker in words; `LifecycleInProgress` is `busy`;
`AnnounceUnacked` and `AnnounceUnavailable` are `announce-failed` (reached only when planned states were negotiated, including a relay that refuses the plan); `LeaseExpired` and
`EligibilityChanged` are `changed`; `Cancelled` is `cancelled`.

Authorization, read-only and the rest of [§3.2](#32-the-signed-command) are checked **before**
acquiring; read-only and the rest of eligibility are checked again atomically by `CommitDeparture`
itself (`EligibilityChanged`), not by a separate #801 step.

Hook repair does **not** take the lease: it interrupts nothing ([§4.3](#43-managehooksrepair)).

## 3. The wire contract

### 3.1 Capabilities

hello's `capabilities` include, on a `SecureChannel` to a peer the host would accept, on a machine
that is not read-only:

| Capability | Ops | Offered when |
|---|---|---|
| `manage` | `manage.get`, `manage.subscribe`, `manage.restart` | Always, on those terms |
| `manage.stop` | `manage.stop` | **Only with #55 enforcing** and the peer holding `manage.lifecycle` ([§2.3](#23-why-this-cannot-route-around-67)) |
| `manage.hooks` | `manage.hooks.repair` | The host can set up hooks (`HostControl.HasAgentSetup`, #670) and the machine is not beside agent-ha-bridge (G4) |
| `manage.config` | `manage.config.get`, `manage.config.set` | **Only with #55 enforcing** and the peer holding `manage.config` |
| `manage.haToken` | `manage.haToken.set` | **Only with #55 enforcing** and the peer holding `manage.secrets` |

A client draws a control only when its capability is there (design §4.1). With `hello.changed`
(#651), a host that becomes read-only withdraws them on the open channel, and clients hide the
controls at once. Not offering `manage` is how an older host says "not supported": a client never
sends a `manage.*` op it was not offered, and an older host answers one as an unknown op.

### 3.2 The signed command

Every acting op (`restart`, `stop`, `hooks.repair`, `config.set`, `haToken.set`) takes
`{ "command": SignedCommand }` ([v1-transfer.md, "The signed command"](v1-transfer.md#the-signed-command)),
signed with the same MAC (`CommandMac`), by the PWA (`pwa/js/transfer.js`'s signer) and the
Windows app alike. The body is the existing `CommandBody` (`Transfer.cs:85`), plus two optional
fields, additive:

```jsonc
{
  "v": 1,
  "action": "manage.config.set",          // the op's name, exactly
  "request": "<uuid>",                     // one press
  "principal": { "kind": "user", "device": "<signer device id>" },
  "target": { "machine": "<this host's device id>" },
  "to": "<this host's device id>",
  "issuedAt": 1793840000, "expiresAt": 1793840120,   // at most 120 s apart
  "args": { … },                           // per op, below; at most 1 KiB; absent for restart and stop
  "contentSha256": "<base64url>"           // manage.haToken.set only: SHA-256 of the token's UTF-8 bytes
}
```

The host checks, in order, and refuses at the first failure with the code in brackets:
verifies the MAC (`bad-signature`); `action` is the op (`bad-signature`); `principal.device` is the
channel's peer (`not-peer`); the peer is connected now (`not-connected`) and of a person's kind
(`not-person`); it is not this machine's app (`local-device`); the machine is not read-only
(`read-only`); for stop, config and the token, #55 enforcing and the grant (`needs-roster`,
`not-granted`). **Then whether the request was already taken** (`CommandMemory`, plus every
lifecycle request kept on disk from the moment it is `accepted`, the last 32, across restarts): a
request already taken returns that press's current result and never acts again
([§3.4](#34-idempotency)), even if the command has since expired. Only a new request goes on to
lifetime and clocks (`expired`, `future`) and the rate limits (`rate-limited`).

### 3.3 Results

Every acting op answers `ManageResult`:

| Field | Type | |
|---|---|---|
| `request` | string | The press it answers |
| `outcome` | `accepted` `done` `unchanged` `refused` `failed` | `accepted`: `CommitDeparture` succeeded and the host is leaving under #805's departure guard (a restart or stop); the state follows. `done`: finished here (a repair, a config write, the token stored). `unchanged`: nothing to do (the value was already that). `refused`: not attempted, and `reason` says why. `failed`: attempted and did not work. An unknown outcome reads as `failed`, never `done` |
| `reason` | string? | One of the codes below |
| `detail` | string? | Words for a person, at most 512 bytes, never containing a secret or a value of one |
| `restartRequired` | bool? | Config and the token: the change takes effect after a restart |
| `current` | `ConfigValue`? | `manage.config.set` refused with `conflict`: the value now ([§6](#6-old-and-new-values)) |
| `transition` | string? | Restart and stop: the #805 planned-state transition id ([§8.3](#83-one-planned-state-announcement-shared-with-805)) |
| `state` | `ManageState`? | The state as the press found it |

`reason` codes: `not-person`, `not-peer`, `not-connected`, `local-device`, `read-only`,
`needs-roster`, `not-granted`, `bad-signature`, `expired`, `future`, `rate-limited`, `busy` (another
lifecycle action, from any source, or a repair is under way), `not-quiet` (#805's lease was
blocked by work in flight, named in `detail`: [§2.6](#26-quiet-the-shared-lease-from-805)),
`changed` (eligibility changed inside the lease), `cancelled`, `conflict`, `not-allowlisted`,
`invalid-value`, `announce-failed`, `unavailable` (no
launcher or hook setup here, or an agent not set up by this installation). An unknown code reads as `refused` with its `detail`.

`ManageState` (from `manage.get`, `manage.subscribe` and the `manage.state` event):

| Field | Type | |
|---|---|---|
| `phase` | `idle` `restarting` `stopping` `repairing` | An unknown phase reads as `idle` |
| `pending` | `{ request, kind, by, at }`? | The lifecycle press under way. `by`: the device's kind and short id (`pwa 1a2b3c4d`) |
| `restarts` | `{ left, nextAt? }` | How many remote restarts the cap ([§9.6](#96-rate-limits)) still allows on this machine now, and when the next one frees up, so a client can say so before the person runs out |
| `last` | `{ request, kind, outcome, reason?, by, at, took? }`? | How the last press ended, across a restart ([§8.2](#82-sequence)) |
| `homeAssistant` | `{ enabled, token: "stored" \| "none", state }`? | With `manage.haToken`: whether a token is stored and whether the connector is connected or refused (from `HaAuthGuard`), never any part of the token |

### 3.4 Idempotency

* A command sent again with the same `request` is the same press: before, during and after it is
  carried out, and across the host's restart (the last 32 lifecycle requests are kept beside the
  device key). It returns that press's current result and never acts twice.
* One lifecycle action at a time per machine, enforced by #805's lease (one holder). A restart
  pressed after another restart has committed (its `manage-pending.json` exists) **joins** it:
  checked before `TryAcquireQuiet`, answered `accepted` with the existing `pending.request` and
  `transition`, and the joining request is recorded on disk with that result. A restart pressed
  while another is still acquiring or announcing, and anything else, is refused `busy`
  (`LifecycleInProgress`), naming what is under way. There is
  no cancel op: a press either gets the lease within #805's few-second wait or is refused, so
  there is nothing waiting to withdraw.
* `manage.hooks.repair` is serialized with Settings' Repair (#670 already serializes
  `host.agents.setup`); a second is `busy`. Repeating a repair that succeeded is harmless and says
  so.
* `manage.config.set` is a compare-and-set: it changes the key only if the file still holds `was`
  ([§6](#6-old-and-new-values)). Setting the value already there is `unchanged` and writes nothing.
* `manage.haToken.set` with a token identical to the stored one is `done` (the same as
  `ha token set` given again: one fresh attempt, `HaCommands.cs:35-46`).

## 4. Each command

### 4.1 `manage.restart`

* **Args:** none.
* **Host:** checks [§3.2](#32-the-signed-command) and the restart cap; then follows #805's three
  steps ([§2.6](#26-quiet-the-shared-lease-from-805)): `TryAcquireQuiet` (refused at once on any
  blocker), `AnnouncePlanned` when planned states were negotiated
  ([§8.3](#83-one-planned-state-announcement-shared-with-805)), and `CommitDeparture`; only after
  that succeeds does it write `manage-pending.json` and answer `accepted` ([§8.2](#82-sequence)).
  The `DepartureGuard` carries the lease's exclusion into the existing `host.restart` handover
  (`HostHandover`, `LocalApi.cs:91-130`): the
  replacement is started only once this host has fully stopped (#239), by the logon task's launcher
  when supervised (`HostSupervisor.cs:51-56`), or by this host otherwise (`Program.cs:780`).
* **Result:** `accepted`, then the outcome in `last` reported by the new host: `done` ("restarted
  in 48 s"), or `failed` if the new host finds the press but started from a crash rather than the
  handover. Refusals as in [§3.3](#33-results), `not-quiet` among them.

### 4.2 `manage.stop`

* **Args:** none. **Disabled until #55** ([§2.3](#23-why-this-cannot-route-around-67)).
* **Host:** as restart, but ends with the app's stop path: the launcher's stop marker
  (`HostLauncherContract.StopMarker`, `HostHandover.StoppedFromTheApp`) and exit 0, which the
  launcher does not restart (`HostSupervisor.cs:11-18`, `:50`).
* **It does not come back by itself.** It starts again at the user's next sign-in (logon task), or
  from Start in the app at the machine. Nothing can start it from another device, since the host is
  what devices talk to. Clients say so in the confirmation ([§8.4](#84-what-clients-show)). The
  owner decided Stop is allowed, but only after #55 (Q-O1, [decided](#decided-by-the-owner-2026-11-04)).
* **Result:** `accepted`; the outcome is `done` when the channel closes after the announced stop,
  and the next host to start reports `last` with `kind: "stop"` and when it started again.

### 4.3 `manage.hooks.repair`

* **Args:** `{ "agent": "copilot" | "claude" | "codex" | "agency" }`.
* **Host:** the same operation as Settings' **Repair** (#670, `AgentSetupService`, action
  `install`, `AgentSetupService.cs:18`), serialized with it and cancelled by host shutdown. **Never
  `remove`**: disconnecting an agent stays at the machine. **Only for an agent this installation
  has already set up**: the agent's home must hold an `agent-bridge-owner.json` that parses as
  version 1 and names this profile, the same ownership check `AgentReadinessService.Hooks` makes
  (`AgentReadinessService.cs:166-198`; its readiness state alone does not distinguish "never set
  up" from "owned", both being `Unknown`). No record, an unrecognized one, or one naming another
  profile is refused `unavailable`, "connecting an agent stays at the machine", so a remote press
  can never connect an agent the owner did not, or disconnected. Refused on a machine beside
  agent-ha-bridge (G4) and on a read-only one.
* **Quiet:** repair writes only the agents' hook registrations, which a running agent reads at
  its next start; it interrupts nothing, so it takes no quiet lease. If the result says a restart is
  needed (`AgentSetupResult.RestartRequired`), the host does **not** restart by itself: the client
  offers Restart, which then goes through the lease and the cap like any other.
* **Result:** `done` with #670's detail (which hooks, any partial work left repairable), or
  `failed` with it. It never claims the agent is signed in or a session is fresh (#670).

### 4.4 `manage.config.get` and `manage.config.set` (disabled until #55)

* **Get:** no args. Returns `{ revision, keys: [ConfigValue] }`, where `revision` is the SHA-256 of
  `config.json`'s bytes as read **now**, and each `ConfigValue` is
  `{ key, value, effective?, allowed: [values], applies: "now" | "restart" }`: `value` from the
  file, `effective` only when the running host differs (a change waiting for a restart). Only
  the allowlisted keys this host knows are listed, so clients never hard-code the list.
* **Set:** `args: { key, value, was, revision }`. The host takes the config write lock (new in
  phase 2, shared by `host.settings`, `agent-bridge-host config set` and this), re-reads the file,
  and changes the key only if it still holds `was`; otherwise `conflict` with `current`. It writes
  through the same writer as `config set` (`ConfigCommands.cs`), so the file's other keys, comments
  and layout are kept as that writer keeps them.
* **The allowlist**, and the only values accepted remotely:

| Key | Remote values | Applies | Why it is safe from another device |
|---|---|---|---|
| `delivery.readOnly` | `true` only | Restart (the running host reads it only at start, `Program.cs:88`; the client offers Restart with it) | It only takes power away: the machine stops acting on any device. **Never `false` remotely** (owner, Q-O2, [decided](#decided-by-the-owner-2026-11-04)): turning it off stays at the machine, and a read-only host refuses all of `manage.*` anyway |
| `push.enabled` | `true`, `false` | Restart | It only decides whether this machine sends the account's own phones notifications |
| `homeAssistant.enabled` | `true`, `false` | Restart | The connector still runs only with a stored token, only while agent-ha-bridge is not running, and a development build only against a disposable HA (G6, `HaSettings.WhyNot`). `baseUrl` is **not** settable, so turning it on cannot send the token anywhere new |
| `sessions.restoreOnStart` (#780) | `true`, `false` | Restart | Restore uses the existing resume path with all its rules (approved folders, folder trust, G5, one launch at a time). Listed only once #780 has landed and the host knows the key |
| `usage.publish` | `true`, `false` | Restart | Reading allowances with credentials already on the machine; on is the default |
| `devBox.keepAwake` | `true`, `false` | Now | Keeps a Dev Box from hibernating on disconnect, which is itself a no-RDP need; it never runs on a machine that is not a Dev Box |
| `permissions.remoteApproval` | `false` only | Restart | Turning it off takes power away; turning it on grants devices permission approvals, so that stays at the machine |
| `launch.enabled` | `false` only | Restart | The same: off takes power away, on grants launching |

  **Never remotely**, and refused `not-allowlisted`: `permissions.trustedPeers` (the authority
  model); `launch.remoteFolderTrust` and `launch.remoteFolderTrustDevices` (host-config.md already
  says "never from another device": trusting a folder lets an agent run what is in it);
  `launch.workspaces`, `launch.permissions`, `launch.agents.*.path` and `.extraArgs` (code execution
  and where it runs); `launch.worktrees.*`; `homeAssistant.baseUrl` (would redirect the token),
  `homeAssistant.disposable` (would bypass G6), `homeAssistant.agentUserIds` (attribution), every
  `homeAssistant.notifications.*` and `answerButtons.*`; `usage.copilotAccounts` (decides which
  `gh` tokens are read); `devBox.intervalMinutes`; and any key this page does not list. Adding a
  key needs a change to this page and an independent review.

* **Result:** `done` with `restartRequired`, `unchanged`, or `refused` (`conflict`,
  `not-allowlisted`, `invalid-value`, …). A client then offers Restart when `restartRequired`.

### 4.5 `manage.haToken.set` (disabled until #55)

* **Data:** `{ "command": SignedCommand, "secret": "<the token>" }`; the body's `contentSha256`
  binds the secret to the signed press.
* **Host:** see [§5](#5-the-home-assistant-token-end-to-end). Stores it with `HaTokenStore`
  (DPAPI for this Windows user, `HaSettings.cs:158-186`) and records it as given again
  (`HaAuthGuard.Forget`, as `HaCommands.cs:35-46` does), so a token refused before gets one fresh
  attempt.
* **Result:** `done` with `restartRequired: true` (the connector reads the token at start, as
  `ha token set` says) and `state.homeAssistant` afterwards; or `refused`/`failed` with a reason
  that never echoes the token. No op ever reads a token back; there is no `manage.haToken.get`, and
  clearing the token stays at the machine.

## 5. The Home Assistant token, end to end

**The channel.** The token travels only inside the Noise channel between the phone and the host
(design §5.3): the relay forwards ciphertext and sees the op's frame size, nothing else. It is
never in a relay control message, a push, the #805 planned state, an HA entity or a blob.

**On the phone (PWA) and in the app.**

* A password field (`type="password"`, `autocomplete="off"`, `spellcheck="false"`, no
  autocapitalize), on its own sheet reached only from the machine's settings. Paste is allowed
  (the token is copied from Home Assistant's profile page).
* Never shown back: no reveal toggle, no "last four", no placeholder made from it. After a send,
  successful or not, the field is cleared and the value dropped.
* Never stored: not in IndexedDB, `localStorage`, the service worker, the PWA's outbox for retries,
  or the app's preferences. A send that fails is retried only by the person typing it again.
* Never logged: the PWA's and the app's logging never receive it; error paths format the op name,
  never the request. The Windows app passes it as a `char[]` it clears after use where WinUI allows,
  and says plainly where a managed string cannot be cleared.
* Checked before sending: 16 to 4096 bytes, printable ASCII without spaces (what Home Assistant
  issues); otherwise "That doesn't look like a Home Assistant token", without quoting it.

**On the host.**

* **The transport must change first.** Today `ChannelHub` decrypts and inflates every request into
  plain `byte[]`s and parses it into a `JsonDocument` that is never disposed (`ChannelHub.cs:331`),
  so the handler gets a `JsonElement` and cannot clear any of it. Phase 2 changes `ChannelHub`
  (Connection's area, [Q-C5](#for-connection-eb29d6d8)) to decrypt and inflate into pooled buffers
  it zeroes, and to dispose the document, once the handler returns. Until that lands,
  `manage.haToken.set` is not enabled even with #55.
* With that, the handler reads `secret` with `Utf8JsonReader` straight into a `byte[]`, checks its
  SHA-256 against `contentSha256`, and passes the bytes to a new
  `HaTokenStore.Save(folder, ReadOnlySpan<byte>)` overload (today's takes a `string`). The token
  bytes are zeroed (`CryptographicOperations.ZeroMemory`) in `finally`, and the frame buffers by
  the transport after it. It never becomes a `string` on the host except inside `HaAuthGuard`'s
  existing fingerprinting, which phase 2 also moves to bytes.
* The protocol layer never logs a request's `data` for any op, at any level, and an exception from
  this handler carries a fixed message. A log-capture test sends good, empty, overlong and malformed
  tokens and searches every captured line, result and exception for them.
* **Crash dumps.** The host writes none itself, and the product configures no Windows Error
  Reporting `LocalDumps`. With the transport change, the token's bytes are in memory only while the
  request is handled, in buffers zeroed afterwards, so a dump taken later does not hold them. A dump
  taken during handling could, and so could any copy the runtime made that is not ours to clear;
  DPAPI at rest cannot prevent that, and this page does not claim more. #802 (remote diagnostics)
  must never collect process dumps or the profile's `ha-token*` files; that is flagged to Dogfood.

**The audit line** records only the fact: "Home Assistant token replaced" or "stored (none
before)", by which device, when and the outcome ([§7](#7-audit)). Never its length, hash or any part of it.

## 6. Old and new values

The confirmation for each config change shows **both values** and is built from a fresh read:

1. Opening the change sends `manage.config.get`, which re-reads `config.json` from disk **at that
   moment** (not the host's start-time snapshot) and returns `value` and `revision`. When the
   running host differs, the sheet says "Running with *X* until the next restart; the file says *Y*".
2. The sheet reads "Change *push notifications* on *DEV-VM1* from **On** to **Off**? Takes effect
   after a restart." Cancel is the default and the Escape action. Confirm is a press the person
   makes; nothing is pre-filled from a URL, a push or session content ([§9.4](#94-confused-deputy-an-agent-driving-the-pwa)).
3. A sheet open longer than 60 seconds fetches again before it can be confirmed.
4. The set carries `was` and `revision`. If either no longer matches (another device, the app's
   Settings or someone at the machine changed it meanwhile) the host changes nothing and answers
   `conflict` with the current value, and the client shows the sheet again with that as the old
   value. This is the precedent of `SettingsRequest.RemoteFolderTrustDevicesWas` (`HostOps.cs`),
   which refuses a list made before another change.

## 7. Audit

Each `manage.*` acting op writes **one line per outcome** (accepted, then done/failed; or
refused) to the **host's log**, which is authoritative because the host does the work and the app
may not be running. The host also carries the last 32 lines in its status to the Windows app beside
it, which writes each once into **`app.log`** (`AppLog`, `src/AgentBridge.App/AppOptions.cs:66-77`),
so the line is in "the app's log" as #801 asks whenever the app runs ([Q-O6](#still-open-for-the-owner)).

```
2026-11-04 17:42:10 PT  manage restart  by pwa 1a2b3c4d "Dan's iPhone"  request 5e6f7a8b  accepted: restarting (departure committed)
2026-11-04 17:44:31 PT  manage restart  by pwa 1a2b3c4d "Dan's iPhone"  request 5e6f7a8b  done: restarted in 48 s
2026-11-04 17:50:02 PT  manage config.set push.enabled true -> false  by app 9c0d1e2f "DESKTOP"  request 0a1b2c3d  done: restart required
2026-11-04 17:51:40 PT  manage haToken.set  by pwa 1a2b3c4d "Dan's iPhone"  request 4d5e6f70  done: Home Assistant token replaced
2026-11-04 17:52:05 PT  manage restart  by host 7788aabb "DEV-VM2"  refused: not-person
```

* **Who:** the device's relay kind, short id (8 hex, as update's log) and name as the relay lists
  it, with control characters dropped and cut to 64 characters (the name is self-declared; the id is
  what identifies). After #55, the roster's name.
* **What:** the op, and for config the key with its old and new values (all allowlisted values are
  booleans, never secrets). For the token, only "replaced" or "stored".
* **When:** Pacific time (America/Los_Angeles, `PT` with the offset implied by the date), whatever
  the machine's own time zone, per AGENTS.md.
* **Outcome:** as in [§3.3](#33-results). Refusals from one device are logged at most once a minute,
  as update does (`UpdateService.cs:218-237`), so a flood cannot fill the log.
* **No secrets:** never the token, the signed command, a MAC, a key or the account token.

## 8. Restart and stop: what happens and what clients show

### 8.1 How the host comes back

* **Supervised by the logon task's launcher** (the normal case, #106): the host exits with
  `HostLauncherContract.Restart` after fully stopping; the launcher starts the replacement in the
  same task instance, still watched (`HostSupervisor.cs:51-56`). So #280's race (Task Scheduler's
  `IgnoreNew` ignoring a `Run` while the old instance lingers) does not arise: nothing calls
  `tasks.Run`.
* **Not supervised** (started by hand, or an older launcher): the host starts its own replacement
  once fully stopped (#239, `LocalApi.cs:91-98`, `Program.cs:780`), so the restart ends with exactly
  one host.
* **Stop** leaves the stop marker so the launcher does not restart it; it returns at the next
  sign-in or from the app's Start.
* If the replacement does not come back, #805's watchdog and "didn't come back" push take over;
  #801 adds no retry of its own.

### 8.2 Sequence

1. Press (signed), the checks in [§3.2](#32-the-signed-command), and the restart cap.
2. `TryAcquireQuiet` with #805's default wait (a few seconds). Any blocker refuses the press
   (`not-quiet`, `busy`, …) and nothing else happens; there is no waiting phase.
3. If the relay negotiated planned states, the planned state is published and acknowledged within
   the lease ([§8.3](#83-one-planned-state-announcement-shared-with-805)).
4. `CommitDeparture` (with the receipt, or `Legacy`), which rechecks eligibility, read-only
   included. A `Blocked` here refuses the press (`changed`, …), the coordinator cancels any
   published planned state, and nothing has been shown to the device but that refusal.
5. Only now: `manage-pending.json` written (`{ request, kind, by, transition, announced, at }`,
   where `announced` says whether the planned state was published and acknowledged, `false` only
   on a relay without `host-planned-state-v1`), the request
   recorded on disk, `accepted` answered, and `phase: restarting` (or `stopping`) sent on every
   subscribed channel, with `transition`.
6. The `DepartureGuard` carries the lease's exclusion into the handover; the host stops; channels
   close with a normal close.
7. The new host reads `manage-pending.json`, sets `last` (`done`, "restarted in 48 s"; or, after a
   stop, "stopped at 17:44, started again at 09:02 by sign-in"), deletes the file, and serves it in
   `manage.get`. **For a restart only**, a file written more than 30 minutes before this host
   started is reported `failed`, "the host stopped some other way", never `done`. A stop's file has
   no age limit, since the next start is expected to be hours later. `announced` only decides whether
   clients expect #805's planned-state offline reason.

### 8.3 One planned-state announcement, shared with #805

**Agreed with #805's writer (`1f59f119`). The message is #805's, specified in
`docs/protocol/host-liveness.md` in its draft PR #815; #801 only uses it, and #815 is where it is
reconciled.** For coordination and Connection's review, what #801 relies on:

* One relay-visible control message, `host-planned-state` v1, negotiated in hello/ready as
  `host-planned-state-v1`: `reason` (`sleep`, `shutdown`, `reboot`, `update`, `sign-out`,
  `restart`, `stop`), a random `transition` id, a per-connection monotonic `seq`, `phase`
  (`planned` or `cancelled`, the same type), and `expectedReturnSeconds` (30 to 900) for `reboot`,
  `restart` and `update` only. The relay's receipt and expiry are authoritative; it is metadata, not
  authority, and causes no action.
* **No actor and no request id in it.** Who pressed stays inside the Noise channel and the audit
  line. #801's result carries the same `transition` so the pressing device correlates the two.
* **Restart:** `reason: restart`, `expectedReturnSeconds: 60` (#805's grace 120, so "hasn't come
  back" after 3 minutes). **Stop:** `reason: stop`, no expected return, and **no** "didn't come
  back" push. **Repair:** not a lifecycle state; nothing is announced.
* **Order:** publish and receive the relay's durable acknowledgement before the deliberate exit,
  then close normally (1000, never 1012).
* **A relay that did not negotiate the capability:** restart and stop still go ahead, and clients
  fall back to the E2E result they cached plus a bare offline. **A relay that negotiated it but does
  not acknowledge within 5 seconds:** the host refuses with `announce-failed` and does nothing.
* **Aborted after publishing** (any `Blocked` from #805's coordinator, a read-only change, a host
  failure before exit): the coordinator publishes `phase: cancelled` with the same `transition`,
  best effort, and the lease is disposed before its 30 s run out; a late acknowledgement cannot
  revive it.
* **#805 also uses this for #819's host-only update**, with the same lease; an unattended update
  needs the capability and a receipt, so the legacy fallback above is only for a person's press.

### 8.4 What clients show

| Moment | The pressing device | Other devices of the account |
|---|---|---|
| Before | Restart opens a confirmation sheet with **Cancel as the default, focused first**, saying how many remote restarts the cap still allows and when the next frees up (`ManageState.restarts`), before the person runs out. After #55, Stop: the same sheet, with "It stays stopped until someone signs in to DEV-VM1 or presses Start in the app there; it can't be started from here." | — |
| Refused, not quiet | "DEV-VM1 is delivering an answer right now. Try again in a moment." (the blocker in words) | Nothing |
| Going | "Restarting…" | "Restart asked from Dan's iPhone" (from `manage.state`), then the planned state |
| Offline | "Restarting since 17:44" (planned state), then #805's "hasn't come back" after expected + grace | "Offline since 17:44: restarting" (#805); without the capability, plain offline |
| Back | "Restarted at 17:45 (took 48 s)" for 60 s, from `last` | The same, without who |
| Stopped | "Stopped from this phone at 17:44. Returns at next sign-in or Start." | "Offline since 17:44: stopped" (#805; the actor shown only where the E2E result is cached) |
| Refused or failed | The reason in words, and what to do | Nothing |

Neither client offers any of it on a read-only machine, one that does not offer `manage`, or one
it is not connected to. Controls whose capability is absent, including everything held until
#55, are **hidden, not disabled**.

**The client controls are the Creative director's** (`c187cf6c`, on #821): before any phase 2
client code, they are specified in `docs/design/ui.md` §6 under the accepted rules for risky
actions (one verb per state, hidden not disabled, Cancel-default sheets, the cap shown before it
runs out), and hook repair's state is tied to the #783 Settings agents rows so one place says
whether an agent is set up. The table above states what each moment must convey, not its layout;
material visual choices go to the owner.

## 9. Threats and mitigations

### 9.1 A stolen or compromised device

* **Before #55:** a stolen, unlocked phone can restart or repair, as it can install an update
  today. Mitigations: the confirmation sheet; the rate limits, including the cap of 2 restarts per
  machine in 6 hours; and the audit line naming the device. Stop, config and the token are
  impossible before #55.
* **After #55:** remove the device (R3) and it is refused at once everywhere. Stop, config and the
  token additionally need the device's grant. **Config changes and the HA token also need a fresh
  device unlock (biometric or PIN) per command** (owner, Q-O5,
  [decided](#decided-by-the-owner-2026-11-04)): WebAuthn user verification in the PWA, the
  platform's unlock prompt in the Windows app, proven to the host as part of the signed command
  ([Q-C9](#for-connection-eb29d6d8)). Restart and hook repair need none; whether Stop does is
  [Q-O10](#still-open-for-the-owner). A push to the account's **other** devices for every accepted
  stop, config or token change is still [Q-O3](#still-open-for-the-owner).
* **Malware on the device** holding its key can sign any command the person could. The fresh
  unlock bounds config and the token to presses the person makes; restart and repair remain within
  the rate limits and audited.

### 9.2 A replayed command

Noise already prevents replay inside a channel. Across channels: the MAC binds the command to this
machine and the signer; a new command lives at most 120 s; `CommandMemory` takes each request once;
and every lifecycle request is kept on disk from the moment it is accepted (the last 32), and
checked **before** expiry, so a restart pressed once and sent again, before or after the restart,
returns that press's result and never restarts twice.

### 9.3 A malicious relay

It cannot read or forge `manage.*` (Noise; the Noise prologue binds both device ids, design §5.4),
so it cannot see the token or change a value. It can:

* **Drop or delay** a command: the 120 s lifetime bounds when it can still act, and once it
  arrives, #805's lease either acts within seconds or refuses; clients show "no answer" after 15 s.
* **Lie about presence kinds** (before #55): relabel another machine as `pwa`. That machine would
  still have to sign a `manage.*` command with its own key, which no host software does; the residual
  is an agent-controlled host, the [§2.3](#23-why-this-cannot-route-around-67) case, closed by #55's signed kinds (R1).
* **Forge or suppress the planned state:** it is a hint for display, never authority. A forged
  "restarting" shows a wrong offline reason until the host reconnects; a suppressed acknowledgement
  makes the host refuse (`announce-failed`), failing safe.
* **Replay an old roster** (after #55): refused by the pinned head (R2).

### 9.4 Confused deputy: an agent driving the PWA

An agent with browser automation on the person's laptop could click the PWA, or an agent's
session output could try to steer the person. Mitigations:

* Management lives only in the machine's settings sheet. **No URL parameter, deep link, push
  `navigate`, notification action or session content** can open a confirmation, pre-fill a value
  or send a `manage.*` op; the push `navigate` already carries only `host` (see #800's amendment).
* Values are chosen with fixed controls (switches, a list of agents); the only free text is the
  token, typed by the person.
* A confirmation sheet with Cancel as the default for every acting op.
* The PWA sends no `manage.*` while `navigator.webdriver` is true. That is a speed bump, not a
  boundary (it can be hidden), so the boundary for config and the token is the fresh device unlock
  after #55, which automation cannot satisfy without the person.
* Session content is rendered as text by the existing Markdown renderer, which has no way to emit a
  control.

### 9.5 CSRF-like paths in the PWA

The PWA has no cookies or ambient credentials: a command needs the device key, which is a
non-extractable WebCrypto key usable only by the PWA's own origin, and the Noise channel, which only
the PWA's code opens. There are no form posts, no `postMessage` listener that accepts commands, and
the service worker relays no `manage.*`. The site's CSP (`pwa-site-csp`) stays as it is: no inline
script and no third-party origins. A page on another origin therefore cannot cause a command.

### 9.6 Rate limits

| Limit | Value | Like |
|---|---|---|
| Restart, stop per device | 3 of each in 10 minutes | Update's presses |
| Restarts per machine, all devices | 2 in 6 hours before #55 (owner, Q-O9); 6 an hour (restart and stop together) after. **Only a restart that passes `CommitDeparture` counts**; refusals (`not-quiet`, `busy`, `announce-failed`, `changed`) and a press that joins one under way do not. `ManageState.restarts` reflects that | Near update's scale before #55; a restart loop from several devices after |
| Hook repair per device | 3 in 10 minutes | — |
| Config sets per device | 10 in 10 minutes | — |
| Token sets per device; per machine | 3 in an hour; 5 in a day | `HaAuthGuard`'s own attempt rules still apply on top |
| Refusal log lines per device | 1 a minute | `UpdateService.cs:218-237` |
| Devices remembered for the limits | 64, oldest let go | `UpdateService.DevicesKept` |

A refused press says when to try again, as update's does.

## 10. Tests to write in phase 2

Each fails without its change. Suites are from [quality/catalog.yml](../../quality/catalog.yml).

**`host` (tests/AgentBridge.Host.Tests: new `ManageServiceTests.cs` and `ManageFlowTests.cs`;
additions to `LocalApiTests.cs`, `RemoteAuthorizationTests.cs`, `SignedCommandTests.cs`)**

* Every manage op, reads included, is refused on the local pipe for the app, an agent, the host CLI and any other caller.
* An agent session on the pipe is still refused host.restart and host.stop with the #67 reason.
* hello offers no manage capability on the pipe, to a host or service peer, or on a read-only machine.
* A restart from a device the relay lists as a host is refused as not a person's.
* A restart from a device not connected to the relay now is refused.
* A restart signed by a device other than the channel's peer is refused.
* A restart signed by this machine's own app's key is refused.
* A restart goes through the shared quiet lease and never through AutoUpdater's idle test.
* A restart refused by the quiet lease because an answer is being typed changes nothing and says not-quiet.
* A restart is refused, and the login survives, when a host-owned sign-in process cannot be handed over.
* A restart goes ahead while a session has a turn running, and the session is found again by the new host.
* A machine that becomes read-only inside the lease refuses the restart and cancels its planned state.
* Another lifecycle action holding the lease makes a restart refuse as busy, naming it.
* A restart refused at commit is never reported accepted, and leaves no pending record.
* A refused restart does not use up the per-machine cap.
* A restart sent again with the same request is the same press, before and after the host restarts.
* A restart sent again with the same request after its command expired returns that press's result and does not act.
* A restart arriving after its two-minute lifetime with a new request is refused as expired.
* A command lasting more than two minutes is refused.
* A fourth restart from one device in ten minutes is refused, saying when to try again.
* Before the roster is enforced, a third restart on one machine in six hours is refused whichever devices pressed.
* A restart publishes the planned state and waits for the relay's acknowledgement before stopping.
* A restart is refused and nothing stops when the relay negotiated planned states but does not acknowledge within five seconds.
* A restart still goes ahead on a relay without planned states.
* A remote restart ends with exactly one host running, under the launcher and without it.
* The new host reports the restart's outcome with its request and how long it took.
* A stop leaves the launcher's stop marker, so the host is not started again until sign-in or Start.
* A hook repair runs Settings' repair, never a removal, and a second repair while one runs is refused as busy.
* A repair for an agent with no ownership record naming this profile is refused and writes nothing.
* Stop, config set and token set are refused as needing the roster while none is enforced, even with trusted peers on.
* hello offers none of manage.stop, manage.config and manage.haToken without an enforced roster.
* A config key not on the allowlist is refused, homeAssistant.baseUrl and launch.agents.copilot.extraArgs included.
* delivery.readOnly can be set to true and not to false; remoteApproval and launch.enabled only to false.
* A config set whose old value no longer matches the file changes nothing and returns the current value.
* A config set to the value already there writes nothing and says unchanged.
* The token never appears in the log, an audit line, a result or an exception, for a good, empty, overlong or malformed token.
* A token whose hash does not match the signed command is refused and not stored.
* Each accepted, failed and refused manage command writes one audit line with the device, the action, the Pacific time and the outcome, refusals at most one a minute per device.
* Refusals from one device are logged at most once a minute.

**`homeassistant` (tests/AgentBridge.HomeAssistant.Tests)**

* No Home Assistant entity, button or service call reaches a manage op.
* A token stored from bytes is read back identically by the connector, and the bytes are zeroed.

**`protocol` (tests/AgentBridge.Protocol.Tests) and the vectors**

* An unknown manage phase reads as idle and an unknown outcome as failed, never as done.
* The signed manage command vectors (`docs/protocol/vectors/manage.json`) verify in C# and in the PWA (`protocol-vectors`).

**`local-api` (tests/AgentBridge.Local.Tests)**

* Only the app may report its device id to its host.

**`app-core` (tests/AgentBridge.App.Tests)**

* The app writes each of its host's manage audit lines into its log once.
* Another machine's Restart is shown only when its hello offers manage.
* The token field's characters are cleared after sending.

**`pwa-unit` (pwa/test/unit, new `manage.test.mjs`)**

* The token is cleared after sending and never reaches storage, the console or an error.
* The confirmation shows the old value from a fresh read and asks again after a conflict.
* No URL parameter, push navigation or session content opens a management confirmation.
* No manage command is sent while navigator.webdriver is true.

**`pwa-ui` (pwa/test, new `manage.spec.mjs`)**

* Restart opens a confirmation sheet with Cancel focused first, showing the restarts the cap still allows.
* A restarting machine shows Restarting, then Restarted with the time once it is back.
* Stop warns that it can only be started again at the machine or at the next sign-in.
* Management controls are absent on a read-only machine and on one that does not offer manage.

**`transport` (tests/AgentBridge.Transport.Tests)**

* The relay logs nothing of a channel frame's contents, whatever its op.

Live checks only on disposable VMs ([parity-checklist.md](../parity-checklist.md)), never on the
owner's machines.

## 11. Open questions

### For Connection (`eb29d6d8`)

* **Q-C1.** Do you agree to one shared person-device predicate for `signin.start` and `manage.*`
  (§2.5 item 1), owned by you? And which kinds: `pwa` and `app` (update, folder trust) or also
  `phone` (transfers)?
* **Q-C2.** Confirm the #800 assumptions in §2.5: `attention` is re-derived after a restart, and
  Restart is never offered for `signInNeeded`.
* **Q-C3.** Is the account token bound to the device key yet (design §5.4, "Account tokens bind to
  the device key")? If not, an agent that reads the host's relay token can join as a new device;
  that is the pre-#55 residual in §2.3, and binding narrows it.
* **Q-C4.** The additive `args` and `contentSha256` fields on `CommandBody` (`Transfer.cs:85`) and the
  120 s lifetime for `manage.*`: acceptable in the shared signed-command format, or should
  `manage.*` have its own body type?
* **Q-C5.** Will you take the `ChannelHub` change in §5 (decrypt and inflate into pooled buffers
  zeroed after the handler, and dispose the `JsonDocument`; today `ChannelHub.cs:331` leaves both),
  and can the protocol layer guarantee it never logs a request's `data` (including malformed frames
  and debug traces)? The token op stays off until both hold.
* **Q-C6.** The relay side of `host-planned-state` (§8.3, owned by #805): who changes the relay,
  and does the 5 s acknowledgement fit its durability path?
* **Q-C7.** Should a peer's kind be read from the relay's presence at the moment of the command (as
  update does) or pinned at the channel's handshake?
* **Q-C8.** #800's login process (with #805): is it detached, supervised by the launcher, or
  transferable across a host restart? If it is host-owned and cannot be handed over, #805's
  coordinator refuses `CommitDeparture` with `EligibilityChanged` while a flow is pending
  ([§2.6](#26-quiet-the-shared-lease-from-805)).
* **Q-C9.** How the fresh device unlock for config and the token (owner decision) reaches the host:
  a WebAuthn assertion (PWA) or platform unlock proof (app) bound to the signed command's
  `request`, verified against a credential registered in the #55 roster entry? Or is a client-side
  gate enough? Proposed: verified at the host, since a client-side gate can be skipped by malware.

### Decided by the owner (2026-11-04)

Recorded on #801 ([comment](https://github.com/danswett/agent-bridge/issues/801#issuecomment-6647032599)):

* **Q-O1, remote Stop:** allowed, but only after #55; it ships disabled until then.
* **Q-O2, read-only:** a device may only turn `delivery.readOnly` **on**; turning it off stays at
  the machine, even after #55.
* **Q-O5, user verification after #55:** a fresh device unlock (biometric or PIN) for config
  changes and the HA token; restart and hook repair need none.
* **Q-O9, the restart cap before #55:** yes, at most 2 remote restarts per machine every 6 hours.

### Still open for the owner

None of these blocks phase 2's restart and repair.

* **Q-O3.** A notice to the account's other devices on every accepted remote stop (and config or
  token change)?
* **Q-O4.** Native phone apps (`phone`) included once they exist?
* **Q-O6.** "The app's log": is the host's log plus a mirror into `app.log` when the app runs what
  you meant, or should the audit be a separate file?
* **Q-O7.** "Obeying the idle rules" through #805's shared lease means **no delivery in flight**,
  not "no session busy": a turn running or a question waiting does not hold a restart back, since
  agents survive a host restart (§2.6). Is that what you meant? (This replaces the earlier "Restart
  now" question: with the lease there is no long wait to skip.)
* **Q-O8.** Are the allowlisted keys in §4.4 the right set (for example `homeAssistant.detailedActivity`
  or `launch.resumeCount` added)?
* **Q-O10.** Does Stop (after #55) also need the fresh device unlock? Proposed: yes, since a
  stopped host has no remote way back.
* **Q-O11.** After #55, which devices get the `manage.lifecycle`, `manage.config` and
  `manage.secrets` grants by default? Proposed: `app` and `pwa` all three (with the fresh unlock
  where decided); `phone` per Q-O4.

## 12. What blocks phase 2

* **Rank:** #800 and the P0s land first.
* **Connection's review** of this contract, with answers to Q-C1 to Q-C9.
* **#805's lifecycle coordinator** (`TryAcquireQuiet`, `AnnouncePlanned`, `CommitDeparture`) and
  its `host-planned-state` v1 (#815; #805 owns both). Remote restart cannot be built without the
  coordinator; the planned-state relay side is not a hard block (§8.3 fallback).
* **#55** for stop, config and the token (R1 to R4). The code ships disabled before it.
* **The `ChannelHub` buffer change** (§5, Q-C5) before the token op can be enabled.
* **Docs that change with phase 2's code:** `docs/host-config.md` ("Nothing another device sends can
  change it" becomes "except the allowlisted keys, from a rostered person's device"), the hello
  capability table in [v1-sessions.md](v1-sessions.md), and `local-pipe.md` (the app's device id
  report).
* **#780** for the `sessions.restoreOnStart` key.
* **Client controls:** the Creative director's `docs/design/ui.md` §6 spec before any client code
  (§8.4).

## Out of scope

* **A host whose own relay sign-in is refused (#822).** If the relay turns down the host's account
  token (`RelayState.Refused`), no device can reach that host, so no `manage.*` op can help, and
  `host.signIn` stays app-only on the local pipe. That no-RDP gap is tracked as #822 (§2.5 item 5).
* **Starting a stopped host, or a machine that is off,** from another device: nothing is listening.
  Dev Box Start/Restart through `az devbox` is a separate design (#805).
