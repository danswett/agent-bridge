# Agent Bridge: from Home Assistant add-on to a standalone product

*Plan and design. Drafted 2026-10-27 (PT) against `danswett/agent-ha-bridge` at
`5e59309` (1.33.30+).*

> **This is the master design.** It was kept outside the repository until 2026-11-01
> and is now maintained here. Current status, what is running where and what comes
> next live in [status.md](status.md). The "Progress (2026-10-28)" part of §10 is a
> historical snapshot; status.md supersedes it. Decisions made after 2026-10-29 are
> recorded in status.md's decision log until they are folded into the sections here.

## 0. Summary

Today the bridge is a PowerShell daemon plus a Go hook that uses **Home Assistant as
six things at once**: the UI, the message bus, the multi-machine hub, the identity
provider, the trust anchor for pairing, and the phone client. This plan replaces all
six with parts we own, keeping every user-facing feature (cards, hooks, decisions,
replies, launches, usage, updates, transfers) and turning the project into something
**anyone can install with one download and a GitHub sign-in**. Nothing depends on a
home lab.

| Decision | Choice |
|---|---|
| Desktop UI | **Native on each platform, equal standing:** **WinUI 3** on Windows (Windows App SDK 2.x, .NET 10, unpackaged) and **SwiftUI** on macOS (decided 2026-10-27). Both are thin shells over the same host and protocol, and carry no product logic of their own. Linux runs the host only (§3.3) |
| Cross-platform rule | **Nothing depends on Windows.** One shared host binary (.NET), Go hook, relay, protocol and all product logic on Windows, macOS and Linux. Only the UI shell and the OS adapters (console injection vs tmux, TPM vs Secure Enclave, toasts vs UserNotifications, scheduled task vs LaunchAgent) differ per platform, behind one interface each |
| Install/update | **Velopack** (Windows and macOS) from GitHub Releases of a **new** `danswett/agent-bridge` repository (never `agent-ha-bridge`'s release feed, see G2). The app installs and manages the bridge on both. macOS builds are Developer ID signed and notarized (§5.7) |
| Daemon | **Rewrite in C#** (.NET 10) as **Bridge Host**, one per machine (Windows and macOS), headless |
| Hooks | Keep the Go `agent-bridge-hook` and its spool contract unchanged. Only its fallback target changes |
| Machine-to-machine and phone connectivity | **Agent Bridge Relay**: an **untrusted**, end-to-end-encrypted (E2E) rendezvous and relay, **Azure-native and built for massive scale and low latency** (§5.8: account-homed routing across AKS nodes, anycast global load balancer, regions added as stamps, direct WebRTC/LAN paths that upgrade in place). It is also self-hostable as one container, and there is a relay-free LAN-only mode |
| Identity | **Sign in with GitHub** (an OAuth app owned by `danswett`) scopes the account. **Per-device keys**, approved on an existing device with a matching code, carry the actual trust |
| Phone | **PWA** first, served from GitHub Pages (not the relay; see §8), with Web Push. Native iOS/Android later on the same protocol and push gateway |
| Home Assistant | Optional **connector** plug-in: status entities and actionable notifications. The dashboard and its publication machinery are retired |
| Macs | **First-class**: the same host, plus a native SwiftUI app with the same pages, menu-bar item, notifications with typed answers, installer and manager as the Windows app. Its code is the base of the later native iPhone app (SwiftUI on both) |
| Accounts and billing | GitHub `danswett`: repository, Actions (free, the repository is public), Releases, artifact attestations, the OAuth app, and Copilot credits for the implementation work. The relay's hosting is the only recurring cost (see §11) |

The work ships as eight phases (§10). The first phase that can replace HA on one machine
is **P2**: local app, local host, phone continuity through the HA connector.

---

## 0.1 Hard rule: the existing HA bridge keeps working throughout

The current bridge (PowerShell daemon 1.33.x, HA dashboard, phone notifications) is
in daily use on DSWETT-HOME, DSWETT-DEV-VM1, the ARM laptop, the Dev Box and the Mac.
**Nothing in this program may degrade it.** The guardrails below are requirements,
not advice. A PR that violates one is not merged.

| # | Guardrail | Why it matters (what would break) |
|---|---|---|
| G1 | **New code lives in a separate repository** (working name `danswett/agent-bridge`) until P7. `danswett/agent-ha-bridge` gets only small, deliberate PRs under its own AGENTS.md process | Its CI (`ci.yml` on every push to `main`), the test runner's whole-checkout `test-*.ps1` inventory, the PSScriptAnalyzer sweep and `release.yml` (any `v*` tag) all apply to the entire repository |
| G2 | **No release of the new product is ever published in `agent-ha-bridge`**, and no `v*` tag is pushed for it there. New-product releases live only in the new repository | `bridge-update.ps1:369` follows `github.com/<repo>/releases/latest` (newest published, non-prerelease). An app release there would be offered to every machine, including through `button.agent_bridge_<slug>_install_update`, and installed over the bridge |
| G3 | **Separate identity on disk and in the OS**: install root `%LOCALAPPDATA%\AgentBridge` / `~/.agent-bridge` (never `~/.agent-ha-bridge`), and its own pipe names, mutexes, scheduled task names, LaunchAgent label, uninstall registry key and PATH shim | A shared name would let one stack's install, update or uninstall stop, overwrite or delete the other (cf. `installation.json` and the existing installation-isolation rules) |
| G4 | **Hands off agent hook registrations** (`~/.copilot/hooks/*`, Claude settings, Codex config, instruction files and owned blocks) on any machine that has not explicitly run *Migrate*. Development and test installs use `--no-hooks` or an isolated `COPILOT_HOME` | The Go hook and the PS fallback are how every session reaches the bridge today. A rewritten registration silently detaches all sessions on that machine |
| G5 | **The new host never injects, launches, stops or writes HA while a PS daemon is active for that user.** It checks the `CopilotBridgeSupervisor_<id>` / daemon mutexes and the `AgentBridgeDaemon_<id>` task at start and on every action, and refuses with a diagnostic. Shadow mode is strictly read-only: it reads transcripts and copies of spool events, never consumes the spool | Two writers typing into one console or both answering one question corrupts sessions. Consuming spool files would starve the PS daemon |
| G6 | **No writes to the production HA** from development builds. The HA connector is developed against a disposable HA (container in CI, as the existing `Integration` group requires) with a distinct topic root and entity prefix. Pointing the connector at the real HA is part of an explicit per-machine *Migrate* | Writes into `agent_bridge_*`, the `/agent-decisions` dashboard, Lovelace resources or the publication-policy resource collide with the live bridge and its publication fences |
| G7 | **No new dashboard writer.** The new stack never publishes the reply card, the dashboard or the publication policy. The configured PS writer (per the shared policy, currently DSWETT-HOME) keeps that role until P7 and is **the last machine migrated** | Publication authority is explicit and fenced. A second writer is exactly the overwrite the fences exist to prevent |
| G8 | **Per-machine migration is explicit, reversible and verified**: *Migrate* keeps `~/.agent-ha-bridge` intact, disables (not deletes) the PS task, and *Roll back* restores the task and hook registrations byte for byte. The rollback is exercised in CI and on a VM before it is offered | A failed cutover must cost minutes, not a rebuild |
| G9 | **Wire compatibility on the HA side for migrated machines** (§3.1, HA connector): a migrated machine's host keeps publishing the same per-machine and per-session MQTT entities (topics, ids, attributes, command inputs) that the existing dashboard renders, verified by contract tests recorded from the PS daemon | That way the existing HA dashboard and phone flow keep showing and answering sessions from migrated machines during the transition |
| G10 | **Changes to `agent-ha-bridge` are limited** to (a) the opt-in trace recorder, default **off**, and (b) at P3, a migration hand-off hook. Each lands through the normal PR, full suite, analyzer and release process, one release at a time | Keeps the live bridge's release cadence and risk exactly as today |
| G11 | **Testing on disposable machines first**: Hyperloop VMs or a separate Windows user profile until P2's exit criteria are met. The first real machine to migrate is DSWETT-DEV-VM1, never the publication writer | A bug in a spike should never reach a machine you depend on |

---

## 1. Goals, non-goals, parity contract

**Goals**

1. A person with no Home Assistant installs one app, signs in with GitHub, and has the
   full experience on that machine in under five minutes.
2. Adding a second machine (Windows or Mac) or a phone takes one sign-in and one
   matching-code approval.
3. **Feature parity** with today (§9 lists everything). Nothing a user does today
   becomes impossible.
4. **The relay is never trusted with content or authority.** A compromised relay or
   GitHub account cannot read transcripts or drive sessions without a paired device.
5. It works on corporate machines and Dev Boxes using outbound HTTPS/WSS (port 443) only. No
   inbound ports and no VPN client.

**Non-goals (for this plan)**

* Multi-user sharing, teams or organizations. One person, many devices.
* Linux hosts. The architecture allows them, but they are not planned.
* Replacing the agents' own terminals. The terminal remains a first-class input, and
  "first answer wins" is unchanged.

**Parity contract.** Every behavior in README "What you get", the card inventory (§9),
and every invariant in §6.3 must hold in the new host. A behavior is changed only where
a Home Assistant limit forced it (255-character text, optimistic selects, entity-id
pinning, `#1,3` position encoding, 10-option multi-select cap, card-version gating).
Those limits disappear and the cleaner behavior replaces them.

---

## 2. What Home Assistant does today, and what replaces each role

| Role | Today (HA mechanism) | Replacement |
|---|---|---|
| **UI** | Generated Lovelace dashboard, 7 custom elements in `agent-bridge-reply-card.js` (CARD_VERSION 1.41.0), button-card, layout-card, card-mod | WinUI 3 and SwiftUI apps, PWA, later native phone apps. Each renders locally from protocol state |
| **Bus** | `mqtt.publish` over REST for every entity, MQTT discovery, retained topics, `subscribe_trigger`/`mqtt/subscribe` over WS, `/api/states` polling | **Bridge Protocol v1** (§4): snapshot + ordered event stream + commands with acknowledgements, over a secure channel |
| **Multi-machine hub** | Retained per-machine sensors. Every daemon reads all of them (`Get-DaemonPeerMachines`). One configured writer publishes the shared dashboard under publication fences | Each client aggregates by subscribing to every host directly. **No shared dashboard, so no publication authority, render versions or fences.** Discovery comes from the account's signed device roster (§5.4) |
| **Identity and attribution** | User HA token vs agent HA token. `context.user_id` matched against `agentUserIds` gives the purple edge | Principals are `user@device` and `agent@session`, carried in signed command envelopes (§5.5) |
| **Trust anchor** | `input_text.agent_bridge_pairing` (HA-native state the broker cannot forge) confirms the pairing SAS code | The matching code is compared on **an existing paired device's screen**. The relay cannot forge either side |
| **Phone** | HA companion app, `notify.mobile_app_*` with actionable buttons, `mobile_app_notification_action` events | PWA + Web Push now, native apps + APNs/FCM later, both through the relay's **push gateway** with E2E-encrypted payloads. The HA connector remains optional |
| **Image store** | `/api/image/upload`, `/api/image/serve`, `image/delete` | Chunked, content-addressed blobs on the secure channel (§4.6) |

Two side effects are worth stating plainly:

* About a third of the hardest code in the repository exists only because HA is a
  shared, multi-writer, version-skewed rendering target. That includes publication
  fencing, rollback pins, render fingerprints, card-version gating,
  entity-registry pinning, `config-refresh` recovery and the 255-character workarounds.
  In the new model **it is simply deleted**, not ported.
* The `control-authorization` component (`docs/control-authorization.md`) was designed
  for a "future verified adapter" that never existed. In this design it becomes the
  real authorization layer (§5.6).

---

## 3. Target architecture

```
 ┌────────────── Windows PC ───────────────┐        ┌──────── Mac / Dev Box ────────┐
 │  Copilot / Claude / Codex CLI           │        │  CLI sessions (tmux on Mac)   │
 │        │ hooks (Go, unchanged)          │        │        │ hooks                │
 │        ▼ spool (files)                  │        │        ▼                      │
 │  ┌─────────────────────┐  named pipe    │        │  ┌───────────────┐            │
 │  │  Bridge Host (C#)   │◄──────────────┐│        │  │ Bridge Host   │            │
 │  │  daemon rewrite     │               ││        │  │ (C#, headless)│            │
 │  │  + connectors (HA)  │   ┌──────────┐││        │  └──────┬────────┘            │
 │  └──────┬──────────────┘   │ WinUI 3  │││        └─────────┼─────────────────────┘
 │         │ secure channel   │   app    ├┘│                  │
 │         │ (Noise)          │ tray,    │ │                  │
 │         │                  │ toasts,  │ │                  │
 │         │                  │ installer│ │                  │
 │         │                  └────┬─────┘ │                  │
 └─────────┼───────────────────────┼───────┘                  │
           │  LAN direct (mDNS)    │                          │
           ├───────────────────────┼──────────────────────────┤
           │  WSS :443 (outbound only, E2E ciphertext)        │
           ▼                       ▼                          ▼
     ┌───────────────────────────────────────────────────────────────┐
     │  Agent Bridge Relay (untrusted)                               │
     │  GitHub sign-in · device directory (signed roster) · frame    │
     │  relay · push gateway (Web Push now; APNs/FCM later). Serves  │
     │  no app code: the PWA comes from GitHub Pages (§8)            │
     └───────────────────────────────┬───────────────────────────────┘
                                     │
                              ┌──────┴──────┐
                              │ Phone: PWA  │ (later: native iOS/Android)
                              └─────────────┘
```

### 3.1 Processes and responsibilities

| Component | Runs where | Responsibility |
|---|---|---|
| **agent-bridge-hook** (Go) | Inside each agent's hook call | Unchanged: stdin → spool file → immediate reply, about 21 ms. Fallback when the host is down becomes `agent-bridge-host hook --fallback` (NativeAOT, fast start) instead of the PowerShell script |
| **Bridge Host** (C#, .NET 10) | One per machine, per user, at logon. Windows: `WinExe`, no console, scheduled task. macOS: background-only signed `.app`, LaunchAgent. Linux: systemd user service | Everything the daemon does today: spool intake, discovery, transcript tailing, activity, decisions, replies and injection, launch/stop/resume, usage, worktree isolation and reclaim, transfers, self-maintenance. Also the protocol server (pipe or Unix socket, LAN, relay) and the connectors. **All product logic lives here**, so every client behaves the same |
| **Agent Bridge app**: WinUI 3 (Windows) and SwiftUI (macOS) | Per user, one per desktop OS | UI, tray or menu-bar icon, notifications with typed answers, installer/manager for the host and agent hooks, pairing UX. A thin client of Bridge Protocol v1 over the local socket: it renders protocol state and sends commands, and holds no product rules, so the two apps cannot drift. Both are held to one feature list (§7.1) and one set of protocol contract tests. **Optional at run time**: closing it never stops the bridge |
| **Relay** (ASP.NET Core) | Cloud, run by the project; self-host container available | Account sign-in, signed device directory, opaque frame forwarding, push gateway. Serves no app code. Holds no keys capable of reading content |
| **PWA** | Phone/browser, served from GitHub Pages, never from the relay (§8) | Session list/cards, answer, reply, launch, Web Push |
| **MCP server** (Node, existing) | Launched by MCP clients | Re-targeted from HA REST to the **local host pipe** with a scoped agent token. Tool names stay, with an alias for `ask_via_home_assistant` |
| **HA connector** (in host, optional) | Host plug-in | **During the transition: wire-compatible** with today's per-machine and per-session MQTT entities and inputs (G9), so the existing dashboard keeps working for migrated machines. Also sends actionable phone notifications and receives taps. **Never** publishes the dashboard, Lovelace resources or the publication policy (G7). After P7 it can shrink to a slim status + notifications set |

### 3.2 Why a separate host process and not "the app is the daemon"

* Sessions must keep working with the app closed, before sign-in, after an app crash,
  and on Linux machines that have no app.
* Console injection, spool handling and transcript tailing are latency- and
  lifetime-sensitive. They should not share a UI thread or restart with UI updates.
* The same host binary runs on Windows, macOS and Linux. Keeping the logic in it is
  what lets each UI be fully native without making one platform second class.

The app supervises the host: it installs it, starts it, restarts it and updates it.
A scheduled task at logon (Windows), a LaunchAgent (macOS) or a systemd user service
(Linux) starts it independently of the app. That replaces `agent-bridge-launch.vbs` +
`agent-bridge-supervisor.ps1` (see §6.2 on the console requirement).

### 3.3 Platforms

| | Windows | macOS | Linux |
|---|---|---|---|
| Host | ✅ | ✅ | ✅ (headless) |
| Desktop app | WinUI 3 | SwiftUI | None for now; driven from the other apps and the phone |
| Delivery into sessions | Console injection (S1) | tmux (S7) | tmux |
| Device key | TPM, DPAPI fallback (S6) | Secure Enclave, Keychain fallback (S7) | TPM 2.0 via tpm2 where present, else a file key in the Secret Service or a 0600 file |
| Notifications | Toasts with a text box (S3) | UserNotifications with text input (`UNTextInputNotificationAction`) | Via the phone or another desktop |
| Install/update | Velopack | Velopack, signed and notarized | Tarball + self-update with attestation check |
| CI | Windows x64/arm64 | macOS arm64/x64 | Linux x64/arm64 |

---

## 4. Bridge Protocol v1

A single protocol is used by app↔host, phone↔host and host↔host traffic, on every
transport. It replaces MQTT discovery, retained topics, `/api/states` polling,
`subscribe_trigger` watches, the `promptpayload` topic, the transfer topics and the
HA image store.

### 4.1 Shape

* **Framing.** JSON messages (UTF-8, at most 1 MiB each), compressed and then
  encrypted inside the secure channel (§5.3). Large payloads use blobs (§4.6).
* **Resources** (state, replicated):
  `machine`, `session`, `decision`, `activity`, `launchOptions`, `resumable`,
  `usage`, `update`, `worktree` (diagnostics), `connector`.
* **Subscribe → snapshot + events.** A client subscribes to a host and receives
  `snapshot{seq}`, then `event{seq, resource, op: upsert|patch|delete}` in order. On
  reconnect it sends `resume{fromSeq}`. If the host's ring buffer no longer covers
  that seq, it sends a fresh snapshot. **This replaces retained MQTT:** an offline
  client loses nothing it needs, and a reconnect after a laptop sleep is one message.
* **Deltas.** `activity` is append-only (reasoning chunks, tool calls) and is sent as
  appended items, not full state. This matters for relay egress cost and phone battery.
* **Commands** (client → host) carry the signed envelope in §5.5:
  `session.reply`, `decision.answer`, `session.launch`, `session.stop`
  (with the existing daemon-side two-press confirmation), `session.transfer.request`,
  `machine.update.install`, `machine.update.check`, `machine.forget`,
  `machine.setDetail`, `pairing.*`.
* **Results.** Every command gets `accepted` → `progress*` → `completed|refused|failed`
  with a reason. This closes the "no acknowledgement" gap that
  `cross-machine-resume.md` and `control-authorization.md` both call out, and replaces
  the "Last launch" sensor and the snapshot-and-poll answer-reading in AGENTS.md.
* **Capabilities.** `hello{protocolVersion, hostVersion, features[]}` replaces
  CARD_VERSION gating: a client draws a control only if the host advertises the
  feature, and an old host never receives a command it cannot execute.

### 4.2 Decisions (forms)

`decision` carries the parsed `ask_user` (or AskUserQuestion, or a Codex approval)
schema itself, not slot entities. As a result:

* There is no 255-character limit, no `#1,3` position encoding and no 10-option cap on
  multi-select. Field count is still capped where the **terminal walk** needs it (the
  Copilot "Other" walk from #240 remains host logic).
* The answer is a typed object (`{fieldId: value|[values]|{other: text}}`) plus an
  optional typed reply. The host validates it against the schema and the live question
  identity (**answer by identity**, unchanged) before walking it into the terminal.
* "Answer differs - check the terminal" and the transcript-as-source-of-truth
  completion check are unchanged. The result is reported in the command `completed`.

### 4.3 Launch

`launchOptions` per machine lists agents, workspaces (approved only), profiles,
models, efforts, context sizes and permission modes, which is today's
`get_launch_options`. `session.launch` carries the full settings and the full opening
prompt (no `promptpayload` workaround). The result returns the session id or the
refusal reason (`busy` while a trust prompt is waiting is unchanged: trust is still
answered only by a person at that machine).

### 4.4 Usage, updates, machines

Same data as today's usage sensors and update entities, as resources. The fleet
"check updates" action becomes a parallel command to each host.

### 4.5 Ordering and the "first answer wins" race

The host remains the single writer for its sessions. Two clients answering at once
are serialized by the host. The loser gets `refused: alreadyAnswered` with what was
delivered, the same guarantee today's double-answer guard gives the notification tap.

### 4.6 Blobs (attachments, transfers)

**Parity baseline** (agent-ha-bridge today):
- Paste or attach images (png, jpg, gif, webp, heic/heif, pdf) through HA's image store.
- Any other file inline, up to 256 KB.
- Several attachments per reply.
- Private per-installation staging, with cleanup.
- Session transfers: bundles capped at 25 MB (`newSession.transferMaxBytes`).

The new design keeps every one of these and raises the limits. It also works in both
directions and when the receiver is offline.

**One encrypted format, three paths.** The sender picks a random 256-bit key per blob
and encrypts the blob as a stream of 64 KiB AES-256-GCM records. The nonce is the
record counter, and the associated data binds the blob id, the record index and a
final-record flag, so truncation, reordering and splicing are detected. The key,
SHA-256, size, MIME type and name travel **only inside the Noise channel**, in the
command that uses the blob (for example `reply.send {text, attachments:[…]}`). The
receiver decrypts as the stream arrives, verifies the hash, and then stages the file
under the existing privacy rules. Nothing outside the two ends ever sees plaintext,
and the same ciphertext works on any of the three paths, picked automatically:

| Path | When | Relay cost |
|---|---|---|
| **Direct** (LAN or WebRTC data channel, §5.2) | Whenever a direct path is up | None |
| **In-channel through the relay** | Up to 256 KiB (typical pasted screenshots and small files), when no direct path is up | Frames on the relay's **bulk lane** (§5.8), in records of at most 16 KiB, so they never delay an interactive frame by more than one small record |
| **Blob store** (Azure Blob Storage) | Larger blobs, session transfers, or a receiver that is offline | Two tiny requests: the relay issues short-lived, single-blob SAS URLs (write-once for the sender, read for the receiver). The bytes go **straight between the clients and Storage**, never through relay nodes |

**Limits (parity and beyond):** images and files up to 100 MB each, session transfers
up to 200 MB (default 25 MB, as today), and quotas per account per day. These are server
settings, not protocol constants.

**Blob store details.**
- One storage account per region stamp, with uploads going to the receiver's home
  region.
- A lifecycle policy deletes blobs after 24 hours. A receiver deletes a blob as soon as
  it has verified it.
- CORS is limited to the product origins.
- Blob names are random, with no content addressing across users (convergent
  encryption would reveal identical files).

**Guarantee: blobs are end-to-end encrypted in transit and at rest (decided
2026-10-27).** The service holds ciphertext it cannot read, so it has no user content
to protect, disclose or process. Concretely:

1. **Encrypted before it leaves the device.** The sender encrypts on its own device, and
   only ciphertext is ever uploaded, held at rest or downloaded. Azure Storage's own
   encryption at rest is a second layer, not the one relied on.
2. **No key ever reaches the service.** The per-blob key exists only on the two
   endpoints and inside the Noise channel. It is never in a URL, a SAS token, a header,
   a log, Cosmos DB or Key Vault. Operators (and anyone who compels them) get only
   ciphertext.
3. **No content-describing metadata stored.** The file name, MIME type, hash and true
   size travel only inside the Noise channel. Blob names are random, and blob metadata
   and tags are left empty. Ciphertext is padded up to the next size bucket (powers of
   two up to 1 MiB, then whole MiB), so even the size reveals little.
4. **Short-lived by construction.** Each SAS URL covers one blob, one direction, and
   minutes. Lifecycle deletion after 24 hours is a hard backstop, and a receiver deletes
   a blob as soon as it has verified it. There are no backups, no versioning and no soft
   delete on the container.
5. **Tamper-evident.** Per-record AEAD with the index and final flag in the associated
   data, plus a SHA-256 check after decryption against the hash sent over the
   authenticated channel. A modified, truncated or substituted blob is refused.

What remains is the same metadata the relay already sees: an account id, times and
padded sizes. It is covered by the privacy policy like relay metadata. **CI enforces
the guarantee** in the style of S4's leak check: tests upload marked files through the
real path against a storage emulator, then assert that no stored byte, blob name,
metadata field, URL or log line contains the marker, the file name, the MIME type, the
hash or the key.

### 4.7 Bandwidth budget

Relay egress is the main running cost (§11). Targets:
- Interactive traffic: steady state below 2 KB/s per working session, near zero when
  idle (keepalives every 25–30 s). §5.9 replaces these guesses with measured numbers.
- Blobs: none through relay nodes above 256 KiB. Large blobs go direct or through
  Storage, which scales and bills separately (about $0.02/GB-month stored, for at most a
  day, plus egress).
- The app shows per-day relay and blob usage.

---

## 5. Connectivity and security

### 5.1 Threat model

| Party | Trusted? | Consequence |
|---|---|---|
| **Paired device keys** (host, app, phone) | Yes | The only source of authority |
| **The person, at a paired device's screen** | Yes | Confirms pairing codes, high-risk actions, folder trust |
| **The relay operator, its storage and logs** | **No** | Sees ciphertext, routing metadata (account, device ids, sizes, timing) and push endpoints. Cannot read, forge, replay or reorder commands. Can deny service |
| **GitHub account** | Partially | Scopes the rendezvous. Compromise alone lets an attacker *register* a device, but the device does nothing until an existing device approves it with a matching code |
| **Network, TLS-terminating proxies, corporate middleboxes** | No | Covered by the inner secure channel |
| **Other processes of the same OS user** | Same as today | Can reach the local pipe, as they can read `config.json` today. Optional Windows Hello / Touch ID prompts for high-risk actions (§5.6) |
| **Agents** (Copilot/Claude/Codex sessions) | Constrained | Act as `agent@session` principals with scoped grants, never as the user |

This keeps the property today's design has against an untrusted MQTT broker and
generalizes it to the whole transport.

### 5.2 Transport ladder

Each connection tries, in order:

1. **Local**: a named pipe `\\.\pipe\agent-bridge-<installationId>` whose ACL allows
   only the current user's SID and denies network logons (macOS: a Unix socket at
   `0600` in `~/.agent-bridge/run`).
2. **LAN direct**: the host advertises `_agentbridge._tcp` over mDNS on private
   networks only (opt-in per network profile) and accepts WSS with the same inner
   channel. This saves relay bandwidth and keeps working when the internet is down.
3. **Relay**: outbound WSS to the relay. The host keeps one persistent connection;
   clients connect on demand. Relay frames are `{to: deviceId, from: deviceId,
   ciphertext}`.

The WebRTC data channel (step 4 of §5.8) is a **core latency path, not a later
optimization**. It uses the relay for signaling and STUN, connects directly through NAT,
and the channel upgrades in place from relayed to direct. The relay remains the fallback,
so no TURN service is needed.

**Bring-your-own network**: a "direct address" setting (Tailscale/WireGuard IP, VPN)
uses path 2 without mDNS. **LAN-only mode**: no relay account at all. Pairing then
needs a QR code or a typed code on the LAN path. This mode is for people who refuse
any cloud component.

### 5.3 The secure channel

**Noise `XX` handshake with P-256 ECDH, AES-256-GCM and SHA-256**, with each side's
static key pinned from pairing (`IK` once the peer is known), run inside
WebSocket/pipe frames.

Why this specific suite:

* It is the same primitive set the shipped pairing protocol already uses (ECDH P-256,
  HKDF-SHA256, AES-GCM in `bridge-pairing.ps1`), so the review baseline carries over.
* **WebCrypto supports P-256 and AES-GCM everywhere**, including iOS Safari, so the PWA
  can implement it with non-extractable keys and no WASM.
* **TPM-backed keys**: the Windows Platform Crypto Provider and Apple's Secure Enclave
  both support P-256 ECDH, so a host's static key can be non-exportable hardware.
* TLS terminated by a relay, a proxy or a tunnel cannot see inside. **Dev Tunnels
  web-forwarding terminates TLS at Microsoft's edge**, which is one reason it was
  rejected (§12).

Per-message: compress-then-encrypt (deflate per message, so there is no
cross-message dictionary that mixes attacker-chosen and secret text). Each frame carries
a sequence number, so replay and reorder inside a session are rejected.

**Spike S2 result (2026-10-27, `danswett/agent-bridge#2`): GO.** Independent .NET (BCL
only) and WebCrypto (no WASM) implementations of `Noise_XX_P256_AESGCM_SHA256` agree
byte for byte in Node, Chromium and WebKit, live over WebSocket through Kestrel.
**Requirement it added:** validate every peer public key on the curve explicitly before
ECDH. WebKitGTK's WebCrypto accepted an off-curve point that the others refused, and
that is the invalid-curve attack on long-lived device keys. The self-test is published
at `danswett.github.io/agent-bridge/s2/` for real-device runs.

### 5.4 Accounts, devices and the signed roster

* **Account** = GitHub user id, through the "Agent Bridge" GitHub OAuth app registered
  under `danswett`. Hosts and the desktop app use the **device flow**, the PWA uses the
  web flow through the relay. The relay issues its own short-lived session tokens.
  GitHub tokens are not kept on the relay beyond sign-in.
* **Device** = a long-term P-256 key pair created on first run: TPM/Secure Enclave
  where available, otherwise DPAPI (CurrentUser) or the Keychain. A device also has
  a human name, a kind (`host`, `app`, `pwa`, `phone`) and a platform.
* **Roster** = an append-only, hash-chained list of signed entries
  (`add device`, `remove device`, `set grants`), each signed by an existing device.
  The relay stores and serves it but cannot forge an entry. Every device verifies the
  whole chain and pins the head it last saw. **A rollback or fork is a hard error**, the
  same posture as the publication high-water receipts today.
* **First device** creates the roster, the way `NEW` creates a fleet today. It is an
  explicit choice, never inferred from finding nobody online.
* **Adding a device** is today's `fleet-pairing.md` protocol (commit, offer, reveal,
  transcript-bound 6-digit code), but the payload is the **new device's public key and a
  roster entry**, not a shared secret. The code is shown on the new device and **typed
  or compared on any existing device**, and that device signs the entry. A phone pairs
  by scanning a QR code from the desktop app, which carries the commitment and makes the
  code check one tap.
* **Removing a device** is a signed `remove` entry, effective immediately on every
  connected peer. Nothing like a shared fleet secret ever needs rotating.
* **Recovery**: if every device is lost, "reset" creates a new roster generation, and
  every surviving device must re-pair. Recovery codes could be offered later.

The fleet secret and transfer HMAC are retired. Transfers are authorized by signed
commands between device identities, which fixes the documented gap that "any holder
can request anything any machine offers".

**Spike S4 result (2026-10-27, `danswett/agent-bridge#4`): GO, live.** The relay
proxies GitHub's device flow through the "Agent Bridge (dev)" **GitHub App** and keeps
no GitHub credential; it issues its own signed account token. A device joins by
proving possession of its static key with an HMAC keyed by ECDH against a relay
ephemeral, which works for ECDH-only WebCrypto and TPM keys. **Device id = SHA-256 of
the static key (16 bytes)**, and the Noise prologue binds both ids and the channel, so
a relay that redirects a channel breaks the handshake. Nineteen hostile-relay and
hostile-client checks run in CI. Live on Azure Container Apps, a corp-network host was
reached from a second device and from the Pages-hosted PWA in Chromium and WebKit
(handshake about 0.3 s, round trip about 280 ms from the corp network to West US 2).
**Design changes it adds:**

* **The relay needs a cross-replica backplane** (shared presence and forwarding keyed
  by account) before it can deploy without splitting. **Resolved by §5.8**: every node
  routes through account homes in the cluster, so there are no per-replica islands.
  Observed live: a rolling deploy
  leaves the old replica serving reconnected clients for about 60 s while new clients
  reach the new one. The relay sends 1012 on shutdown, and both ends use a
  ping-with-required-pong keepalive, but that only shortens the window.
* **Account tokens bind to the device key** (bearer tokens in the spike).
* Until the roster exists, any device that signs in to the account is reachable; hosts
  must refuse channels from keys not in the roster.

### 5.5 Command envelope

Every command is signed by the issuing device and binds:

```
{ v:1, requestId, action, principal:{kind:user|agent, device, session?},
  target:{machineId, installationId, sessionId?, processId?, processStartedAtUtc?},
  issuedAtUtc, notBeforeUtc, expiresAtUtc, contentSha256, rosterHead }
```

This is the `control-authorization.md` command schema, with the principal now real (a
device signature) instead of assumed. The host keeps a bounded **request-id replay
store** for the command lifetime and rejects unknown roster heads. Process id plus start
time binding for replies stays exactly as specified.

### 5.6 Authorization

Wire `Get-BridgeControlEligibility`'s rules into the C# host as its policy engine
(port the component and its test matrix as written). Defaults:

| Principal | Grants (default) |
|---|---|
| `user@` desktop app or host device | read, reply, answer, launch, stop, update, forget, transfer |
| `user@` phone/PWA | read, reply, answer, stop. Launch allowed, but **Allow-all launches and updates need a confirmation on the phone** (WebAuthn user verification, or Face ID in the native app) |
| `agent@session` (local) | read, reply, answer, launch with `ask` permissions on approved workspaces. **No** update, forget, pairing or allow-all. Replaces `AGENT_HA_AGENT_TOKEN` with a per-session, short-lived token in `AGENT_BRIDGE_TOKEN` |
| `agent@session` (remote, through its host) | The local agent grants, carried as `agent` principals signed by the originating host |

Unchanged local refusals: elevated sessions are not injected into, control characters
are collapsed, only approved workspaces can be launched, folder trust is answered
only at the machine, and nothing restarts the host from a host-launched session (the
`discovery_held` lesson).

**The purple edge** is computed from `principal.kind == agent` on the last
reply/launch, which is exact. It replaces the `context.user_id` comparison, and the
silent failure mode documented in AGENTS.md (wrong token, no error, wrong color)
disappears, because an agent can only ever hold an agent token.

### 5.7 Secrets at rest

| Secret | Windows | macOS |
|---|---|---|
| Device private key | TPM (Platform Crypto Provider), DPAPI fallback. **S6 GO** (`danswett/agent-bridge#8`): non-exportable, raw ECDH through NCrypt directly (.NET's CNG wrappers fail with the TPM provider), about 2 ms per ECDH on a vTPM; chosen only when the TPM supports `ECDH_P256` | Secure Enclave, Keychain fallback |
| Relay session token, GitHub device-flow token (sign-in only) | DPAPI (CurrentUser) + the existing owner-only ACL rules | Keychain |
| HA connector token (optional) | DPAPI | Keychain |
| Per-session agent tokens | Memory only, injected into the launched session's environment | Same |

This is an improvement on today's ACL-only `bridge-secrets.ps1`. The owner and
reparse-point checks it enforces are kept for every file the host writes.

**macOS signing (decided 2026-10-27):** the Mac host ships **Developer ID signed and
notarized** under the user's Apple Developer account. The same pipeline is already proven
in `danswett/streamdeck-teams-control`: a throwaway CI keychain with the certificate
`.p12`, `notarytool` with an App Store Connect API key, the ticket stapled, and a
`notary-status` workflow for when Apple's queue outlasts the job. Those secrets have to
be added to `danswett/agent-bridge`.

Because it is signed, the host can be a background-only `.app` bundle (no Dock icon,
started by the LaunchAgent). A bundle can carry entitlements and a provisioning profile,
which a bare command-line binary can't, so keys can live in the Secure Enclave. Two
routes, and the Mac spike picks one:
- CryptoKit's `SecureEnclave.P256.KeyAgreement` key, whose `dataRepresentation` is an
  enclave-wrapped blob that can be kept in a file, like S6's TPM blob. This needs a small
  Swift shim called from .NET.
- `SecKeyCreateRandomKey` with `kSecAttrTokenIDSecureEnclave`, kept in the data
  protection keychain, which needs the `keychain-access-groups` entitlement.

Either way, `SecKeyCopyKeyExchangeResult` or CryptoKit gives the raw X coordinate, and
S6's `P256KeyPair.FromAgreement` takes it unchanged. GitHub's macOS runners are VMs
with no Secure Enclave, so that part is verified on the real Mac, as S6 was on a TPM.

### 5.8 Relay at scale (Azure-native, decided 2026-10-27)

**Requirements:** very many users, each with many agent sessions and several clients;
extremely low latency; native to Azure; adding capacity or a region is routine, not a
project. Measured per-user load is in §5.9. In short, **connections are the scaling
constraint, not frames per second.**

**Principles**

1. **The data plane is dumb and in memory.** A relay node forwards opaque frames by
   device id. There is no JSON parsing past the header, no database, and no disk on the
   hot path. Presence and routing are soft state that devices rebuild by reconnecting.
2. **Connections land anywhere; each account has one home.** A device connects to any
   node in its nearest region. Each account is *homed* on one node in its home region,
   chosen by rendezvous (highest-random-weight) hashing over the live membership. The
   home holds the account's routing table (device → node), its presence and its rate
   limits. Nodes cache the routing table and send data frames **straight to the node
   holding the target device**: in-process when both are on one node, otherwise one hop
   over the cluster network (about 0.1–0.3 ms). No L7 router or sticky load balancing
   is needed.
3. **Scaling never moves a connection.** Adding a node moves only the homes the hash
   reassigns (about 1/N): the old home hands the routing table over and forwards during
   a short overlap. Devices notice nothing. A node that dies drops only its own
   connections; those devices reconnect with jitter, and the homes it held rebuild from
   their re-registration. This also fixes S4's deploy split: there are no per-replica
   islands, because every node routes through the cluster.
4. **Direct beats relayed.** The relay is also the rendezvous for direct paths (§5.2): LAN
   direct, then WebRTC data channels with STUN, which work in Safari, Chrome and .NET.
   A channel starts on the relay at once and **upgrades in place** when a direct path
   succeeds. The Noise channel is unchanged; only the carrier swaps. This makes the
   steady state as fast as the network allows and takes most bytes off the relay.
5. **Deltas, not snapshots.** At scale the protocol sends what changed. `session.patch`
   carries changed fields, and a streaming response sends only appended text.
   `sessions.focus` lets a client say which session it is looking at: that one gets
   full-rate updates (coalesced at 100 ms or less), and the rest get status-only
   summaries at 1 s. This is added to Protocol v1 as an optional capability (§4), sized
   by the measurements in §5.9.
6. **Two lanes per connection.** Each socket has an **interactive** lane (answers,
   replies, session events, handshakes) and a **bulk** lane (blob records, §4.6). Nodes
   and endpoints always send interactive frames first, and keep a small fairness floor
   so bulk traffic still progresses. Bulk records are at most 16 KiB, which bounds how
   long one can hold up an answer on a slow link. Anything over 256 KiB goes direct or
   through the blob store, never through relay nodes.

**Azure building blocks**

**Linux only, no Windows VMs anywhere in the service (decided 2026-10-27).**
- Every server-side component is a Linux container: the relay, the push gateway and
  the synthetic probes. The image is .NET on a minimal, distroless or chiseled Linux
  base, non-root (as the S4 image already is).
- Container Apps runs Linux containers, and AKS uses Azure Linux node pools. Nothing
  provisions or manages a VM image.
- Images are multi-arch (`linux-amd64` and `linux-arm64`), so `standard` and `massive`
  can run on Arm-based Azure Cobalt nodes, which cost less for the same throughput.
  S9 benchmarks both. Windows appears only as a client OS (host and app), never in the
  cloud.

| Concern | Service | Why |
|---|---|---|
| Global entry for the WebSocket data plane | **Azure cross-region (global) Load Balancer**: one anycast IP in front of the regional Standard Load Balancers | Layer 4, so no proxy hop or WebSocket timeouts; the client is sent to the nearest healthy region automatically. TLS ends on the relay nodes |
| HTTP APIs (sign-in, push key, PWA and site on the product domain) | **Azure Front Door Premium** + WAF | Edge TLS, WAF and rate limiting for the parts that are request/response |
| Compute | **AKS** per region: a relay node pool over availability zones, and a separate pool for the push gateway | Stable pod identity and direct pod-to-pod addressing (which Container Apps lacks), graceful drain control, and headless-service membership |
| Adding regions and rolling out | **Azure Kubernetes Fleet Manager** + one Bicep "stamp" module per region | A new region is one parameter file: the stamp brings up AKS, the load balancer, Key Vault access and monitoring, Fleet propagates the workloads, and the global load balancer gains a backend. Staged, region-by-region rollouts |
| Autoscaling | **KEDA** on connections per node and CPU, plus the cluster autoscaler | Scales on the relay's real limit (sockets) rather than CPU alone |
| Account directory, devices, roster, push subscriptions, home region | **Azure Cosmos DB** (NoSQL, multi-region writes) | Touched only at sign-in, connect and roster changes, and cached. Never on the per-frame path |
| Signing keys (account tokens, VAPID) | **Key Vault**, with managed identity everywhere; key ids in tokens for rotation | Tokens are verified locally on every node with no lookup |
| Push delivery | Push-gateway deployment, fed through **Azure Service Bus** | A push spike (APNs, FCM, Web Push) never competes with frame forwarding |
| Blobs over 256 KiB (attachments, session transfers, offline delivery) | **Azure Blob Storage**, one account per stamp, single-blob SAS URLs issued by the relay, 24-hour lifecycle deletion | Ciphertext only. Bytes bypass relay nodes entirely, and Storage scales and bills on its own |
| Images | **ACR**, geo-replicated | Each region pulls locally |
| Observability | **Azure Monitor managed Prometheus + Managed Grafana**, Application Insights | Connections, frames/s, forward latency histogram, queue drops and home moves, per node and region. Never content |
| Protection | **Azure DDoS Protection** on the public IPs; connection caps per IP and per account; connect-rate limits | Abuse and reconnect storms are the main availability risks |

**Regions.** An account's home region is set at first sign-in to the region of that
connection, stored in Cosmos DB, and moved when most of its devices are elsewhere. A
device always connects to its nearest region. If that isn't the account's home region,
its node keeps one multiplexed link to the home region over the Azure backbone. Cost:
one backbone round trip, only for accounts whose devices really are continents apart.

**Targets, to be validated by S9:**
- Per node (8 vCPU): 150k–200k idle sockets, 100k frames/s, p99 forward ≤ 1 ms.
- In-region added latency: p99 ≤ 5 ms.
- Deploys and scale-out complete with no lost frames and no client-visible reconnect
  except on the node being drained.
- Reconnect storms: 100k devices reconnecting to a region within 60 s are absorbed
  through jittered backoff and connect-rate limits.

**Self-hosting** stays one container. With one node, every account is homed locally and
nothing else changes; the cluster pieces are optional.

**Lean until demand requires more (decided 2026-10-27).** The design above is the shape
at scale, but it costs only what the load needs, and **growing is a deployment change,
never a redesign**. The invariants that make that true:

1. **One relay binary at every size.** Home/forward routing runs with a membership of
   one exactly as with a hundred: a single node homes every account and forwards
   in-process. The mesh only carries traffic once a second node joins.
2. **Membership doesn't depend on the platform.** Nodes register their private address
   with a heartbeat TTL in a Cosmos DB container, so the same code finds its peers on
   Container Apps or AKS. Kubernetes isn't required for it.
3. **Stable names from day one.** Clients only ever see `relay.<domain>` and per-region
   names. Behind them, DNS (Traffic Manager, costing pennies) can later be swapped for
   the anycast global load balancer without a client change.
4. **Cosmos DB from day one**, on the free tier, single region. Growing means adding RU/s
   or regions, not a migration.
5. **Every optional service sits behind the interface it already has.** Push delivery
   works through a queue interface (in-process first, Service Bus later). The WAF and
   edge go in front of names that already exist.
6. **One Bicep stamp with a `size` parameter**, `lean` | `standard` | `massive`, plus one
   parameter file per region. Moving up a size or adding a region is a parameter
   change and a deploy.

| Size | Fits | Shape | Rough cost per region |
|---|---|---|---|
| `lean` (start here) | Up to a few thousand connected devices | Container Apps consumption, 1 replica (0.5 vCPU); Cosmos free tier; a Storage account for blobs (pay per GB, 24-hour lifecycle); Key Vault; Traffic Manager name; images on GHCR (free); App Insights with a daily cap; Azure's default DDoS infrastructure protection plus in-app limits | **about $15–30/month** |
| `standard` | Tens to hundreds of thousands | AKS free control plane with 2–3 zonal small nodes, KEDA autoscale, Cosmos autoscale, Service Bus for push | about $150–400/month |
| `massive` | Millions, with strict latency SLOs | §5.8 in full: AKS node pools at scale, Fleet Manager, anycast global load balancer, Front Door Premium + WAF, Cosmos multi-region writes, DDoS Network Protection, managed Prometheus/Grafana | $1.5k+/month, growing with load |

S9 verifies the invariants, not just the big shape. It runs the same image at `lean`
and `standard`, shows that a second node joining (and leaving) loses no frames, and
proves a region can be added from a parameter file. It also checks whether Container
Apps replicas can reach each other directly in a VNet environment. If they can,
`standard` can stay on Container Apps longer; if not, `standard` is AKS, as in the
table.

### 5.9 Load model (measured 2026-10-27)

Three independent measurements of one heavy user (Dan: up to 8 concurrent sessions
across several machines), all read-only:

1. **Transcript replay.** All 449 sessions on DSWETT-DEV-VM1 (448 Copilot, 1 Claude;
   March–October), replayed through Protocol v1's rules: full upserts, 250 ms
   coalescing, field limits.
2. **HA history.** 7 days of the live sessions' activity and status sensors (6,828
   activity updates).
3. **Live MQTT capture.** 20 minutes of everything the current bridge published, across
   all machines, during heavy multi-session work.

| Measure | Result |
|---|---|
| Visible changes per active session | 6.4/min mean. Busy 1 s p99 2, max 4 (replay). HA: 35/h typical, 135/h busy, at most 33/min |
| Upsert size | 2.7 KB typical, p99 7.3 KB, max 24 KB (inside the 40 KiB budget) |
| Concurrent active sessions (activity in the last 2 min) | p50 1, p90 2, max 8 |
| Current bridge, whole fleet, heavy 20 min | 0.58 msg/s, 556 B/s; busiest second 9 msgs, 15.6 KB |

**Per user, Protocol v1 as written (full upserts at 250 ms, keepalives every 30 s):**

| | 24 h mean | Busy hour | Burst p99 / max (1 s) |
|---|---|---|---|
| 1 client | 0.14 frames/s, 33 B/s | 0.29 frames/s, 469 B/s | 3 / 8 frames, 9.5 / 84 KB |
| 3 clients | 0.29 frames/s, 91 B/s | 0.74 frames/s, 1.4 KB/s | 9 / 24 frames, 28 / 251 KB |

Keepalives are about half the frames while a user is active.

**Levers** (bytes while active, compared with the table):
- Deltas: −83%.
- Focus (full updates only for the viewed session): −39%.
- Both together: −86%.
- Coalescing at 1 s instead of 250 ms: −24%; at 100 ms: +20%.
- Counting invisible events in `updatedAt` would have added 22%, so the contract now
  forbids it.

**What this means for the relay:**

* **Sockets, not throughput, are the binding constraint.** For 1M simultaneously
  active users with 3 devices each:
  - Busy-hour load is about 0.74M frames/s, which is 8 nodes at 100k frames/s.
  - The sockets are 3M, which is 15–20 nodes at 150–200k sockets each.
  So the relay is optimized for **memory per idle connection** (small TLS and socket
  buffers, no per-connection tasks while idle) and for keepalive cost, before raw
  frames per second. Phones that sleep disconnect and are reached by push, which cuts
  standing sockets further. Hosts stay connected.
* **Bandwidth and egress are modest once deltas ship.** Busy-hour traffic for 1M
  active users is about 1.4 GB/s with full upserts, or about 0.2 GB/s with deltas and
  focus. Over a day, at 24 h means, it is about 34 TB a month with deltas, before
  direct paths take their share. **Deltas and focus move into Protocol v1 before the
  relay goes to `standard`.**
* **Bursts are small and short.** The worst 1-second burst for a heavy user with 3
  clients is 24 frames. A per-account limit of about 100 frames/s with bursts of 200
  leaves 4–8× headroom. The S4 relay's flat 200 frames/s per connection is replaced by
  per-account token buckets.
* **Blobs dominate bytes whenever present**, which is why anything over 256 KiB bypasses
  relay nodes (§4.6).

**Limitations:**
- One heavy user. Population mixes come from S9 load profiles: idle-heavy, typical and
  power user.
- Transcripts don't record streamed partial responses. A host that streams text could
  briefly reach 4 upserts/s per streaming session, which is exactly where deltas
  matter most.
- Reconnect snapshots are bounded by the session count (one upsert each) and are paced
  by the client's send limiter.

The data and scripts are kept outside the repository, in the session's files
(`traffic-model/report.md`, `traffic-ha/`).

### 5.10 Observability (required, decided 2026-10-27)

**Goal:** full, live visibility into the relay's and the transport's health and
performance, as a dashboard the operator can simply watch, **with no PII or UII**
anywhere in it.

**Rules (enforced in CI, like S4's leak check):**

* **Aggregates only, with low-cardinality labels.** The allowed dimensions are region,
  node, stamp size, version, client kind (`host`/`app`/`pwa`), OS, path
  (`direct`/`relay`/`blob`), lane, close code and outcome. Never an account or device
  id (hashed or not), IP address, user name, session id or title, file name, or any
  content. A test fails the build if a metric uses a label outside the allowlist.
* **Logs carry no identities.** Within a node, connections are correlated by an
  ephemeral random id that is meaningless outside it. Client IPs are never written.
  Retention is 7 days. The existing log-hygiene tests extend to every new path (blobs,
  push, membership).
* **Client-side health is anonymous and aggregated before it leaves the device.** Hosts
  and apps send histogram buckets per period, with no ids and no timestamps finer than
  the period. Users can turn it off in settings; relay-side metrics and synthetic probes
  still give full coverage without it.

**What is measured:**

| Area | Signals |
|---|---|
| Relay data plane | Connections (open, connect/s, by kind and OS); reconnects by close code; frames/s and bytes/s by lane; **forward latency histograms** (in-node and cross-node); per-connection queue depth and drops; slow-consumer disconnects |
| Cluster | Membership size and changes; account-home moves and handoff duration; drain progress; cross-region link RTT and traffic |
| Edge and auth | Sign-ins and failures; proof failures (4003); rate-limit hits; origin refusals; connect-rate throttling |
| Transport, from clients | Noise handshake time; round trip by path; **direct-path upgrade success rate** and time to upgrade; time to reconnect after sleep; **event lag** (host emit → client render, measured by the host clock in the event); blob throughput by path |
| Push gateway | Sends by provider (APNs, FCM, Web Push), latency, 4xx/5xx and gone-subscription rates, queue depth |
| Blob store | SAS URLs issued; bytes up and down (from Storage metrics); lifecycle deletions; quota refusals |
| Dependencies | Cosmos RU and throttling; Key Vault latency; Service Bus depth; node CPU, memory and GC; sockets per node versus the target |
| **Synthetic probes** | A canary host and canary clients per region, run by the project, measure end-to-end round trip, handshake, event lag and blob round trip through the production path every minute. This gives true end-to-end SLOs with **zero user data** |

**SLOs and alerts:**
- Relay availability.
- p99 in-region forward latency ≤ 5 ms.
- p99 synthetic end-to-end round trip per region.
- Reconnect storms.
- Push failure rate.
- Direct-upgrade success rate.

Alerts reach the operator as notifications in the Agent Bridge apps (dogfooding the push
path), with email as the fallback.

**Where to watch it: built into the native apps.** The WinUI 3 and SwiftUI apps get an
**Operator** page. It appears only for accounts on the relay's operator allowlist
(signed in, on a paired device). It reads a relay `ops` API that serves the aggregated
metrics above, so there's no second pipeline and no extra access to anything sensitive.
It shows live charts:
- connections and reconnects
- frames/s by lane
- forward-latency percentiles
- per-region and per-node health
- synthetic-probe round trips
- push and blob health
- active alerts

Every user also gets a **Connection** panel with their own health: current path (direct
or relay), round trip, region, reconnects and today's relay and blob usage. It is
computed on their device and sent nowhere.

**Pipeline at each stamp size (§5.8)** uses the same instrumentation (.NET `Meter` and
OpenTelemetry) throughout:
- `lean`: the relay's in-process aggregates (the `ops` API) plus Azure Monitor metrics
  and Application Insights with a daily cap.
- `standard` and `massive`: add Azure Monitor managed Prometheus and Managed Grafana for
  long retention and deep dives, behind the same `ops` API the apps use.

**Roadmap:** the S9 load test exercises the metrics, and its results are read from the
Operator page. P4's exit criteria include the Operator page, the synthetic probes and
the label-allowlist and log-hygiene tests.

---

## 6. Bridge Host: the daemon rewrite

### 6.1 Module map

The C# host mirrors the daemon's existing seams (`docs/daemon-split.md` step 3
already gives us the per-agent table design):

| C# project / namespace | Ported from | Notes |
|---|---|---|
| `Host.Agents` (`IAgentAdapter`, `ILauncher`, tuning tables) | `daemon-agents.ps1`, `$script:BridgeLaunchers`, `BridgeLauncherTuning`, `claude/hooks/*`, `codex/hooks/*` | One adapter per agent, the same slots (`FindSessions`, `ReadAppend`, `Activity`, `IsWorking`, `AskUserState`, `ApprovalMarker`, flags such as `ExactOtherWalk`) |
| `Host.Intake` | `daemon-hookspool.ps1`, `copilot-hooks.ps1`, `bridge-native-hook.ps1` | Same spool directory, file format and ordering. FileSystemWatcher plus a 2 s sweep |
| `Host.Discovery` | `daemon-discovery.ps1` | Including incomplete-discovery **holds** (absence-based cleanup waits) and unaccounted-process detection |
| `Host.Activity` | `daemon-activity.ps1` | Byte-level LF framing before UTF-8 decoding, bounded windows, cursor rules: port exactly, with the transcript-framing tests |
| `Host.Decisions` | `daemon-decisions.ps1`, `decision-bridge-common.ps1` (parsing/repair) | `ask_user` repair of leaked markup, schema derivation, answer-by-identity baselines |
| `Host.Delivery.Windows` | `decision-inject.ps1` | `AttachConsole` / `WriteConsoleInput` by P/Invoke. Copilot "Other" walk (#240). Elevation refusal |
| `Host.Delivery.Mac` | tmux paths in `session-launch.ps1` | `tmux send-keys` with the same sanitizing |
| `Host.Replies` | `daemon-replies.ps1` | Attachments become blobs |
| `Host.Launch` | `session-launch.ps1`, `daemon-launch.ps1` | Workspace allowlist, argument quoting (diff all 768 combinations against the PowerShell output), Agency profiles, resume, worktree isolation (`isolate: true`) |
| `Host.Worktrees` | worktree isolation and `Remove-BridgeFinishedWorktree` | **Port the safety rules verbatim**: marker, lock, ownership, no checked-out branch, nothing uncommitted/untracked, the idle-hours rule, and the single named build-output exception. Never sweep the root (the 2026-10-08 incident) |
| `Host.Usage` | `daemon-usage.ps1` | Three vendors normalized into one shape |
| `Host.Transfer` | transfer code in `session-launch.ps1`, `cross-machine-resume.md` | Over blobs and signed commands instead of MQTT |
| `Host.Maintenance` | `daemon-maintenance.ps1`, `bridge-update.ps1` | Host self-update on macOS. On Windows the app owns updates (§7.3) |
| `Host.DevBox` | `bridge-devbox.ps1`, keep-awake | Unchanged behavior, scheduled inside the host |
| `Host.Protocol` (shared library) | new | Resources, events, commands, envelope, Noise, blobs. Shared by host, app and relay (and compiled to TS for the PWA through generated schemas) |
| `Host.Connectors.HomeAssistant` | `decision-mqtt.ps1` (subset), `daemon-decision-notify.ps1` | Optional: status sensors, actionable notifications, tap listener. No dashboard |
| **Deleted, not ported** | `bridge-frontend-cards.ps1`, dashboard generation in `decision-ha-websocket.ps1`, publication policy/fencing, entity-registry pinning, Lovelace resource management, `config-refresh` | No shared renderer exists any more |

### 6.2 Process model on Windows (the console question)

Today the daemon must keep a **real** console (VBS hidden launch, never
`conhost --headless`), because pwsh is a console app that frees its own console and
attaches to the target's.

**Spike S1 result (2026-10-27, `danswett/agent-bridge#1`): GO.** A .NET `WinExe` (no
console) delivered into conhost and Windows Terminal consoles and into a live Copilot
CLI 1.0.104 session, which recorded the prompt exactly. But **a target window closing
while the injector is attached terminates the injector**, whatever its handler does.
So delivery goes through a small supervised **injector worker** process (NativeAOT
`WinExe`, one at a time, `SetConsoleCtrlHandler(NULL, TRUE)`). If the worker dies
mid-delivery, the host reports the outcome as *wrote, unknown* and never retries
silently. The host itself needs no console and no VBS launcher.

S1 also found that **the existing bridge corrupts non-Latin-1 text** in every reply
and typed answer. Its `KEY_EVENT_RECORD` has no `CharSet`, so dashes, smart quotes,
CJK, symbols and emoji arrive mangled (reproduced live). The fix is one attribute, in
agent-ha-bridge, through its own PR process.

Every child process the host starts (probes, `git`, `agency hub list-local-sessions`)
goes through one `StartWindowless` helper, which ports the
`Start-BridgeWindowlessProcess` lesson (no flashing windows that steal focus).

### 6.3 Hard-won invariants to carry over (non-exhaustive, each with a test)

1. The transcript is the source of truth for "answered"
   (`tool.execution_start` paired with `..._complete`).
2. Answers are read **by identity, never by clock**.
3. The `ask_user` hook never blocks; the native prompt stays live.
4. Copilot "Other": Up×(N-1), Down×N, words, a single Enter (#240). Verify typed words
   against the recorded answer.
5. Control characters are collapsed before injection. Elevated targets are refused with
   an explicit message.
6. Incomplete discovery **holds** retirement, cleanup and launch decisions. It never
   reads as absence.
7. Launched sessions get worktree isolation and fail closed. The cap is enforced with
   no fallback to the primary clone.
8. Reclaim only marked, unlocked, clean, detached, idle trees, and only in the given
   repository.
9. Attachments stay in protected per-installation staging, never shared TEMP.
10. Live sessions are never offered for resume. Remote resume only lands in workspaces
    the target has approved.
11. Folder trust is never answered remotely. A launch that meets one returns `busy`.
12. End session: a single press when idle, two presses within 10 s otherwise, enforced
    by the host so every client gets it.
13. No session launched by the bridge may restart the bridge (the unaccounted-process
    hold).
14. Hooks must never fail the agent: fixed safe reply on any error.
15. Single target per action. The selection-collapse incident (166 buttons) has no
    analogue in the new protocol, because commands name exactly one target in a typed
    field, but keep the "count exactly one" rule in the HA connector.

### 6.4 Rewrite method: trace replay plus shadow mode

1. **Recorder.** Add a small, opt-in trace writer to the PowerShell daemon (1.34.x)
   that records spool events, transcript appends, HA inputs and the daemon's resulting
   outputs (including the exact `mqtt.publish` payloads, which become the G9 contract
   fixtures), normalized into protocol-shaped JSON. It is **off by default** and
   lands through the normal `agent-ha-bridge` PR and release process (G10). It writes
   only under the installation's protected `runtime` directory, with content redaction
   rules matching the attachment-privacy suite.
2. **Replay harness.** The C# host consumes recorded traces and must emit the
   same normalized outputs. The PowerShell suites' scenario names become xUnit test
   names (the "test names read as sentences" style is kept).
3. **Shadow mode.** On real machines the C# host runs **read-only** next to the
   PowerShell daemon (no injection, no launches, no HA writes) and diffs its state
   against the daemon's. A mutex shared by both enforces that only one of them may
   ever inject or launch.
4. **Cutover per machine** once a module's diff has been clean for a week of real use.

### 6.5 Testing and CI

* xUnit for host and protocol, at least the same offline isolation as
  `tests/run-tests.ps1`: fresh HOME/TEMP per test, no network unless a transport is
  mocked, a guard that throws on real HTTP.
* Protocol conformance: the same vectors run against C# and TS (PWA), including Noise
  interop and envelope verification.
* UI: WinUI with Hyperloop on a VM (already available here) for end-to-end card
  parity and screenshots. PWA with Playwright (WebKit for iOS behavior).
* CI on GitHub Actions under `danswett`: Windows (x64/arm64) and macOS (arm64/x64)
  matrix, CodeQL for C# and TS, PSScriptAnalyzer for as long as PowerShell remains.

---

## 7. Agent Bridge apps (WinUI 3 on Windows, SwiftUI on macOS)

Both apps have every page and feature below; neither is the "main" one. Platform
idiom differs (Windows: tray icon, toasts, Fluent; macOS: menu-bar item, UserNotifications,
standard Mac window and settings conventions), and nothing else does. A
feature is not done until it ships on both, and both run the same protocol contract
tests plus UI tests (Hyperloop on Windows, XCUITest on macOS). The Windows-specific
details in §7.2 and §7.3 have macOS counterparts, listed there.

### 7.1 Surfaces

| Page | Parity source | Notes |
|---|---|---|
| **Sessions** (default) | session card, status card | Cards grouped or filterable by machine. Glow states: blue working, amber waiting, steady blue for background agents/commands, purple edge for agent-driven, honoring reduced motion. Fold per session (persisted), auto-unfold on a question, compaction count, spinner verbs |
| Session card body | activity, choices and reply cards | Markdown (CommunityToolkit `MarkdownTextBlock`), expandable reasoning and tool history separate from the response, decision form (single, multi with checkboxes, boolean, "Other" row, Cancel request, Send answer), reply box (Ctrl+Enter, paste image, attach file, keep text until consumed), End session with two-press confirm |
| **Launch** | launch card | Machine, Resume, Agent, Workspace, Profile, Model, Effort, Context, Permissions (Allow-all clearly flagged), opening prompt (multi-line, no length limit), last-launch result from the command result |
| **Usage** | usage card | Grouped by account, multiple windows, pace marker, amber at 75 %, red at 90 %, live "resets in" |
| **Machines** | status card | Online/offline, version, dev marker, Detail toggle, update with stage/progress, Forget (two-press), relay/LAN path indicator, last seen |
| **Connection** (every user) | new | This device's own connection health: path (direct or relay), round trip, region, reconnects, today's relay and blob usage. Computed locally and never sent (§5.10) |
| **Operator** (operator allowlist only) | new | The live relay and transport health dashboard from §5.10: connections, frames by lane, forward-latency percentiles, per-region and per-node health, synthetic probes, push and blob health, alerts. No PII or UII |
| **Devices** | new | Roster: hosts, apps, phones. Pair a device (QR or code), remove a device, grants |
| **Settings / Install** | `install.ps1`, `agent-ha-bridge configure` | §7.3 |

Tray icon (H.NotifyIcon): a count of waiting questions. Clicking it opens Sessions. The
app starts minimized at logon (opt-in).

### 7.2 Notifications (Windows toasts)

Windows toasts support buttons, a **quick-reply text box** and combo boxes, up to five
inputs, and work for unpackaged apps through a COM activator registered in HKCU.

**S3 result (2026-10-27): GO, but not through Windows App SDK.** In an unpackaged,
self-contained app on WinAppSDK 2.5.1, `AppNotificationManager.Register()` throws
(microsoft/WindowsAppSDK#6774), so toasts show but clicks never arrive. The OS API
(`Windows.UI.Notifications`) with the classic desktop COM activator worked
end to end on a clean VM: an option button and a typed Unicode answer while running, a
cold start from the toast, toasts still answerable after a Velopack delta update, and
full removal on uninstall. The product **owns a small activator** (HKCU
`LocalServer32` + `INotificationActivationCallback`, about 150 lines) rather than
depending on the maintenance-mode Community Toolkit, and removes it from Velopack's
uninstall hook. Publishing also needs `EnableMsixTooling`, or the app's `.pri` is
left out and XAML fails at startup. Both failures surface only as `0xC000027B`, so
unhandled-exception logging is in from the first build. Details:
`spikes/s3-winui-velopack/RESULTS.md`.

Mapping:

* Single-choice question: one button per option (overflow goes to a combo box) plus
  a text box for "Other".
* Freeform question: a text box and Send.
* Multi-field form or multi-select: "Open" (deep-links to the card), the same rule as
  today's "three questions get no buttons".
* Tags per session, so a question replaces only its own toast. An answer from anywhere
  withdraws the toast. A stale tap is refused, as today.

A cold activation (app closed) starts the app headless and sends the command over
the pipe without showing a window. The activation can arrive before or after the UI
exists (S3), so the activator is registered before XAML starts and hands the answer
straight to the host.

### 7.3 Install, manage and update

**Distribution.** One Velopack `Setup.exe` per architecture on GitHub Releases. It
installs per user under `%LOCALAPPDATA%\AgentBridge`, with no admin rights. The package
contains the app, the host (`agent-bridge-host.exe`), the Go hook, the MCP server and
the per-agent hook adapters. **An app update is a host update**, so the version skew
between the two is always zero on Windows. S3 measured the self-contained WinUI shell
alone at a 101 MB `Setup.exe` (94 MB package), a 3-5 s silent install, and 0.17 MB
deltas for code-only updates applied and restarted in under 13 s.

**First-run wizard** (replaces `bootstrap.ps1` + `install.ps1` interactive flow):

1. Detect agents (Copilot CLI, Claude Code, Codex, MCP clients) and offer to install
   missing ones (winget), as `install.ps1` does today.
2. Register hooks: write `~/.copilot/hooks/...`, Claude settings, Codex config,
   instruction files and owned marked blocks. **Port the ownership markers and
   "leave others' text alone" rules exactly.**
3. Workspaces: approved roots, and the `isolate` setting with its worktree limit.
4. Sign in with GitHub (device flow), create or join an account, pair (§5.4).
   Skippable for **local-only** use.
5. Optional connectors: Home Assistant (URL + token, then status entities and phone
   notifications).
6. Start the host. Show a live health check (the `agent-ha-bridge status` content in
   the UI).

**Migration from an existing PowerShell install** (one click, reversible until
"Finish"):

* Read `installation.json`, `config.json` and the protected secrets, and preserve the
  installation id, workspaces, launcher settings, usage accounts and Dev Box settings.
* Stop and disable `AgentBridgeDaemon_<id>`, keeping it until migration is confirmed.
  Swap the hook fallback target. Leave `~/.agent-ha-bridge` intact for rollback.
* If HA was configured: point the HA connector at it in wire-compatible mode (G9), so
  this machine's sessions stay on the existing dashboard and phone flow. Migration
  **never** touches the dashboard, Lovelace resources or publication policy (G7).
  Retiring the dashboard is a separate, explicit P7 operator action on the
  configured writer.
* Verify before finishing: a test question is answered from the HA card and from the
  app, and a reply is delivered. If either fails, roll back automatically (G8).

**Uninstall** removes hooks (owned markers only), tasks, the host and app data, and
signs a roster `remove` for this device.

**Release integrity**

* GitHub **artifact attestations** (Sigstore, free for a public repository) on every
  asset, plus `SHA256SUMS`. The host's macOS self-updater verifies the attestation
  bundle, not only the hash.
* **Authenticode signing**, so SmartScreen and Defender do not flag a product people
  install. Options: SignPath Foundation (free for OSS), Azure Trusted Signing
  (about $10 per month), or an OV certificate. See §11.
* Keep the release discipline: one release at a time, draft first, all assets
  attached (now including Velopack packages, mac host builds and hook builds), then
  publish.

---

## 8. Phone: PWA now, native later

* **Served from GitHub Pages, not the relay** (changed by S4). A relay that serves the
  PWA's code could serve code that reads what it decrypts, which would make the relay
  trusted. The page is built from this repository, carries a CSP allowing scripts only
  from its own origin and connections only to the relay, and the relay accepts browser
  sockets and sign-in calls only from that origin. It installs to the home screen,
  which iOS 16.4+ requires for Web Push. Later: version the PWA and pin its hash in the
  desktop app's "open on phone" QR code.
* **Pairing:** the desktop app shows a QR code containing the account and a pairing
  commitment. The PWA creates a non-extractable P-256 key (WebCrypto, IndexedDB) and
  registers a **passkey** for user verification. The desktop signs the roster entry.
* **Push:** each host encrypts the notification payload to the PWA's push
  subscription keys (RFC 8291), so the relay's push gateway only adds the VAPID
  signature and forwards it. On iOS, action buttons are limited and there are **no text
  inputs** in web notifications, so a tap opens the card for anything beyond simple
  options. This is a known gap that native apps (and the optional HA connector) fill.
  **Spike S5 result (2026-10-27, `danswett/agent-bridge#6` and `#7`): GO, on a real
  iPhone.** A host's question reached the locked phone's lock screen through the live
  relay and Apple. A tap opened the PWA, which connected to the
  asking host by itself, and the answer was back at the host 10 s after the push.
  Requirements S5 found, which the product must keep:
  - **The phone is never on the notification path.** iOS kills the socket on every lock,
    so a question is always asked and pushed by the host. The PWA reconnects whenever
    it returns to the front, reopens its last host, and fails sends on a dead socket
    visibly (a WebSocket otherwise drops them silently).
  - **The host keeps push subscriptions per account, not per relay connection.** It
    resends a notice that a dropped connection lost, and drops a profile's
    subscriptions if it signs in as another account. Push goes around the relay's
    account routing, so the host is the boundary.
  - **Apple is stricter than RFC 8030.** It decodes the `Topic` header and refuses
    lengths base64 can't produce (`BadWebPushTopic`), so topics are encoded bytes. Test
    every push detail against Apple, not only a fake push service. The gateway should
    return the push service's reason to the host; the spike's did not, which hid this.
  - **Push endpoints are capabilities**, so they never go in logs. HttpClientFactory
    request logging is off on the relay.
  - **Lock-screen privacy:** a setting for generic notices only ("A session needs
    you"), so the question text appears only after unlock, in the app.
  - **An origin of its own:** `danswett.github.io` shares notification permission and
    storage with every other page there, so the product needs its own domain (open
    item, §13).
* **Native apps later**, on the same protocol and envelope: Swift/Kotlin (or .NET MAUI
  to share `Host.Protocol`). APNs/FCM go through the same push gateway, with payloads
  decrypted in a notification service extension. The relay's push gateway exists from
  day one, so native apps are additive.

---

## 9. Feature parity checklist

The full card inventory (from `frontend/agent-bridge-reply-card.js` and the dashboard
generator), mapped to surfaces. ✅ = carried over unchanged, ➕ = improved by removing
an HA constraint, 🔁 = reimplemented differently.

| Feature | App | PWA | Notes |
|---|---|---|---|
| Live status, activity, last response, markdown | ✅ | ✅ | Deltas instead of full-state republish |
| Detailed activity (reasoning, tool calls), per-machine Detail toggle | ✅ | ✅ | Toggle is a host setting, not an `input_boolean` helper |
| Glow (working, waiting, delegating), purple agent edge, reduced motion | ✅ | ✅ | ➕ exact principal-based purple |
| Fold/unfold, persisted per session, auto-open on question | ✅ | ✅ | |
| Compactions count, background agents/commands states | ✅ | ✅ | |
| Decision forms: single, multi (checkbox), boolean, Other row, Cancel | ➕ | ➕ | No 255-character, position-encoding or 10-option limits. Multi-field over 4 fields still falls back to an outline |
| Reply after the turn, Ctrl+Enter, keep text until consumed | ✅ | ✅ | |
| Paste image, attach file | ➕ | ✅ | Blobs (§4.6): no HA image store; images and files up to 100 MB in either direction, delivered even if the receiver is offline |
| End session (two-press when not idle) | ✅ | ✅ | Host-enforced |
| Launch: machine, resume, agent, workspace, profile, model, effort, context, permissions, prompt | ➕ | ✅ | No 255-character prompt limit. Typed result |
| Agent usage (pace, thresholds, resets-in) | ✅ | ✅ | |
| Fleet status: online, sessions, version, dev marker | ✅ | ✅ | |
| Update install, progress, fleet check | ✅ | 🔁 | Windows through the app (Velopack). Mac through host self-update. PWA can trigger |
| Forget a machine | 🔁 | 🔁 | Signed roster `remove` |
| Notifications with answer buttons | 🔁 Toasts with text box | 🔁 Web Push | ➕ Windows toasts can take typed answers |
| Session transfer, cross-machine resume | 🔁 | trigger | Signed commands and blobs, through the blob store or a direct path. Default cap 25 MB as today, configurable to 200 MB. Fleet secret retired |
| Agent driving other sessions (MCP tools) | 🔁 | — | MCP → local pipe with agent token |
| Pairing | 🔁 | 🔁 | Code/QR on an existing device. No HA helper |
| Dev Box keep-awake | ✅ | — | |
| HA automations on bridge state | — | — | Optional HA connector keeps a slim entity set |

---

## 10. Roadmap

Each phase ends shippable, with the PowerShell bridge still able to run until P7.

| Phase | Content | Exit criteria |
|---|---|---|
| **P0 Spikes** | S1 console injection from a .NET `WinExe` (Copilot, Claude, Codex; Windows Terminal and conhost). S2 Noise-P256 interop between .NET and Safari/Chrome WebCrypto. S3 WinUI 3 unpackaged + Velopack + toast text box with cold activation. S4 relay prototype (ASP.NET Core) with GitHub device flow, two hosts and one PWA through a corporate network and a Dev Box. S5 iOS PWA Web Push with an RFC 8291 payload from a host. S6 TPM P-256 ECDH for the Noise static key. S7 macOS: .NET tmux delivery, a signed and notarized background `.app` host with its device key in the Secure Enclave (§5.7), and a SwiftUI shell talking to the host over its Unix socket with Velopack install/update and a notification answered by typing. **S8 corporate policy check** (§11, parked). **S9 relay at scale** (§5.8): the home/forward cluster on AKS with rendezvous hashing, a load generator (100k+ sockets per node, frames/s, p99 forward latency), scale-out and drain with no lost frames, a second region via the Bicep stamp and global load balancer, and a WebRTC upgrade-in-place between the PWA and a .NET host | A written result per spike. S1, S2 and S8 are go/no-go gates |
| **P1 Protocol + host core (read-only)** | `Host.Protocol`, intake, discovery, activity, decisions parsing. Local pipe. App: Sessions page read-only. Trace recorder in PS 1.34.x, replay harness, shadow mode | Shadow diff clean for a week on DSWETT-HOME and DEV-VM1 |
| **P2 Local interactive parity** | Delivery (injection), replies and blobs, launch/stop/resume, worktrees, usage, toasts, the HA connector (status + phone notifications) | **One machine runs without the PS daemon**, and the phone still answers through the HA connector |
| **P3 Install and manage** | Velopack packaging, first-run wizard, hook registration, migration from the PS install, uninstall, signing, attestations | A clean VM goes from download to a working session in under 5 minutes (Hyperloop-verified). An existing install migrates and rolls back |
| **P4 Relay + fleet** | Relay service (accounts, roster, frames, rate limits, metrics), pairing, the transport ladder, multi-machine app view, transfers, signed commands, authorization engine, agent principals, MCP retarget, **observability (§5.10): `ops` API, Operator and Connection pages, synthetic probes** | Home PC, VM and Dev Box all visible and drivable from each other, with the relay demonstrably blind (inspected). The Operator page shows live relay health, and the label-allowlist and log-hygiene tests pass |
| **P5 macOS** | Host on macOS (tmux delivery, LaunchAgent, Secure Enclave/Keychain) and the **SwiftUI app** at full parity with the Windows app (all §7.1 pages, menu bar, notifications with typed answers, install/manage/update, migration). Linux host (systemd, tmux) | A Mac goes from download to a working session in under 5 minutes, and runs without any Windows machine. Mac sessions at parity from either app |
| **P6 PWA + push** | PWA, passkey pairing, Web Push gateway | Answer and reply from an iPhone and an Android phone with the desktop off |
| **P7 Retire HA dependence** | Migrate the publication writer last, then retire the HA dashboard as an explicit operator action. Retire the fleet secret. Archive `agent-ha-bridge` with a pointer, public docs, privacy policy, relay status page | No feature requires HA. Every machine has been migrated, and the old bridge was never broken along the way |
| **P8 Native phone apps** (later) | iOS from the shared SwiftUI package; Android (Kotlin) on `Host.Protocol`; APNs/FCM | Store-published |

Rough size: P0 2–3 weeks; P1 4–6; P2 6–8; P3 3–4; P4 6–8; P5 2–3; P6 3–4; P7 2. The
rewrite (P1, P2, P5) dominates. For scale, it replaces about **41,000 lines of
PowerShell** in 50 runtime files, specified by **87 test suites (about 49,000
lines)**. On top of that come `install.ps1` (3,600 lines), the card (3,760 lines of
JS, mostly replaced rather than ported) and the MCP server (1,700 lines,
re-targeted).

**Machine migration order** (G7, G11): Hyperloop VM → DSWETT-DEV-VM1 → ARM laptop →
Dev Box → Mac (after P5) → DSWETT-HOME (the publication writer) last, at P7. Each
machine stays on the new host for a week, with rollback available, before the next
one moves.

### Path to migration (decided 2026-10-29)

The first goal is parity with agent-ha-bridge, then migrating machines. New features
(observability beyond health and logs, scale work) wait until then. The stages:

| Stage | Content | Validated by |
|---|---|---|
| **0 Parity audit** | `docs/parity.md`: every agent-ha-bridge feature, test suite and §6.3 invariant mapped to the new product with evidence (a test or a recorded live test), plus a migration readiness checklist. Each gap becomes a `parity` issue | Every row has evidence or an issue |
| **1 Close host gaps** | Worktree reclaimer; blob store (large attachments are parity, because phone photos exceed the 256 KiB in-channel limit), then session transfer; Claude and Codex live tests; the gaps found in Stage 0 | One PR each, CI and Codex review, a live test on the VM and the iPhone |
| **1b Full HA connector** | **Decided: full.** A migrated machine stays visible and drivable on the existing HA dashboard. The host speaks agent-ha-bridge's per-machine entity contract exactly and is never the publication writer. It is built in stages: status first, then commands | Contract fixtures from agent-ha-bridge's source; a disposable HA and Mosquitto, never the live one |
| **2 WinUI slice 2** | Remote machines through the relay, Launch page, hook registration, migration from agent-ha-bridge and rollback, then signing | Hyperloop on the VM |
| **3 VM rehearsal** | Install agent-ha-bridge on the VM, migrate to the app, run the scripted parity checklist, roll back and confirm the HA bridge is unchanged, restore a snapshot and repeat | A clean pass of the whole checklist in both directions |
| **4 Real machines** | DEV-VM1 first: read-only shadow comparison with the PS daemon until a **coverage gate** passes (decided 2026-10-29, replacing a fixed week), then a cutover and a soak. Then the laptop, the Dev Box and the Mac. DSWETT-HOME (the writer) last | Coverage gate: at least 100 questions with the 95% upper bound on the mismatch rate ≤ 3% (rule of three), 30 sessions, 500 status transitions, 50 background starts and finishes, 5 compactions, 100 resume snapshots, 50 usage readings, one restart seen, at least 24 h of wall clock, no unexplained mismatch in the last 24 h. No missed answers, rollback available throughout |

**Stage 0 result (#57, 2026-10-29).** `docs/parity.md` has 146 rows: 45 Verified, 28 implemented but untested live, 3 Partial, 24 Missing, 17 Intentionally different, 12 N/A, 17 In flight. Of the 15 §6.3 invariants, 7 are Verified; 6 (discovery holds), 13 (sessions can't restart the bridge) and 14 (hooks never fail the agent) are Missing. It opened 20 `parity` issues (#37–#56).
- **Blocks the VM rehearsal:** #37 (the host takes over hook handling), #38 (migrate and roll back), #39 (scripted parity checklist), the HA connector's status PR, and the Claude/Codex live tests.
- **Blocks the DEV-VM1 cutover:** #43 (discovery holds), #44 (local-pipe stop/restart restricted), #40 (trace recorder and shadow diff), #41 (Claude/Codex replies), plus the blob store and the connector's commands.

### Progress (2026-10-28)

The work has been built phone-first: the relay and PWA from P4 and P6 were pulled ahead of
the desktop app, so a person can drive real sessions from a phone early.

**Merged in `danswett/agent-bridge`:**
- **Spikes and relay:** spikes S1–S6, the contract (#9) and the transport (#10).
- **Viewing sessions:**
  - PWA read-only cards (#11);
  - host core (#12);
  - `agent-bridge-host serve` (#14);
  - background agents and shells (#15);
  - subagent reasoning and responses on the parent's card (#19).
- **Interacting with sessions:**
  - **Answering** (#16–#18, #21): the `session.answer` contract, the PWA UI, and host delivery. Delivery uses a NativeAOT injector and Copilot key walks, keeps the G5 guard, and confirms each answer in the transcript.
  - **Replies** (#23): `session.reply` to Copilot. Steering prompts are confirmed when Copilot takes them, and a leading `/` or `!` is refused.
  - **Web Push** (#22): question, finished and resolved notifications; per-session tags; rate limit; dropping dead subscriptions.

**Verified live from an iPhone** against a Copilot session on a disposable Hyperloop VM:
- **Answers:** single choice, a five-field form with é and 🚀 kept exact, and Other with smart quotes.
- **Replies:** a two-line reply delivered as one line; a `/help` reply refused.
- **Steering:** a reply sent while Copilot was writing was recorded as a queued prompt and confirmed delivered within the host's 60 s wait.
- **Attachments (#27):** an image was sent and Copilot described it; a PDF was delivered. As in agent-ha-bridge, staged files sit outside the workspace, so a Manual Approval session asks for path access before a tool reads them.
- **End session (#25):**
  - an idle session ended on one press (`/exit`, clean exit);
  - a working session asked for a second press, and the confirmation lapsed after 10 s;
  - two presses inside the window ended it;
  - a session blocked on a permission prompt was terminated without `/exit` being typed.
- **Usage (#26):** the machine row reads "N sessions · version (dev)", and the Copilot usage panel matches the HA card. On the VM, Copilot keeps its login in plaintext and `gh` is absent, so usage came from Copilot's own cache, as agent-ha-bridge's would.
- **Copilot permission prompts from the phone (#24, #29):**
  - Approve once typed only the "Yes" digit through Windows Terminal; Copilot ran the command, and the next `node` command prompted again, so no standing grant was left.
  - Deny is Escape, which Copilot records as `cancelled` followed by an abort of the turn. #29 counts that as a confirmed deny, and a phone Deny stops the turn by design.
- **PWA caching (#28):** GitHub Pages' 10-minute `max-age` meant an iPhone kept a stale build after a deploy. The service worker now revalidates every app file.
- **Launch and resume (#31):**
  - a 300-character prompt launched Copilot in an approved workspace, registered in 2.8 s;
  - an isolated workspace got a fresh worktree at `origin/<default>`;
  - an ended session resumed under its own id.
  - Workspaces are approved at the machine with `agent-bridge-host workspace add`. This host never deletes a worktree; it has no reclaimer yet.
- **Windows app (#32), first slice:**
  - `Setup.exe` installed per user with no admin rights: the app and host in `%LOCALAPPDATA%\AgentBridgeApp`, a per-user scheduled task for the host, and an HKCU Run entry for the app.
  - The Sessions page renders the local cards.
  - Settings handles device-code sign-in to the relay and naming the machine (a rename applies on the host restart the app prompts for). The phone then listed the machine with its sessions.
  - Answers were delivered from a card (a single choice, and free text with emoji and accents kept exact) and from a toast button.
  - Toasts carry the choices and a text box, and an answered question's toast is withdrawn.
  - Deferred: remote machines through the relay, Launch, hook registration and migration, signing, Arm64.
- **Production relay (#30, #35, #36, 2026-10-29):**
  - Product relay deployed from Bicep (`infra/`) through an OIDC pipeline: build, staging deploy, smoke test, then production with a manual approval.
  - Staging caught a crash on start and non-JSON logs before production.
  - The token-signing key was rotated after it was accidentally printed in a session log; old tokens are refused with `4001`.
  - Moved to the Visual Studio Enterprise subscription (`0e38f344…`), resource group `rg-agent-bridge-relay`, and served at **https://relay.bad-duck.com** (Cloudflare DNS-only CNAME plus an `asuid` TXT record, with an Azure-managed certificate).
  - About $19 a month: one always-on production replica plus the container registry; staging scales to zero.
  - The old stamp in the trial subscription is idle, awaiting decommission.
- **Push:**
  - the question notification reached the lock screen;
  - tapping it opened the session with the question;
  - "finished" arrived after the turn;
  - the question notification was cleared rather than left stale.

On DSWETT-DEV-VM1 the host runs read-only: G5 sees agent-ha-bridge there and refuses to type. That is the guard working as intended.

**Subagent rule (from live testing).** A card's `reasoning` and `response` can come from a
subagent, and the last writer wins, which is what the HA card does. `status`, `userMessage`
and `model` are always the session's own. Since #173 (#20), `activity.source` names the
subagent that wrote each, and `activity.own` keeps the main agent's own beside them
([design/subagents.md](design/subagents.md) §5).

**VM test method.**
- Use a Hyperloop Hyper-V VM with Node, Git and Copilot CLI installed, and sign Copilot in with a device-flow token.
- Start the host and Copilot through `explorer.exe`, so they run unelevated. Processes Hyperloop launches run elevated, and the host refuses elevated targets.
- The host runs `serve --profile vm` with a DPAPI-protected device key.
- Drive it from the PWA on the iPhone, and check the transcript and the host log.

**P2 parity slices merged and verified live (2026-10-28), one PR each:**
- reply attachments, through the in-channel path up to 256 KiB;
- Claude and Codex answering, at agent-ha-bridge parity. Replies to Claude and Codex are still refused (`danswett/agent-bridge#41`, found by the parity audit);
- **Copilot permission prompts answerable from the phone (beyond parity, decided 2026-10-28).** The phone offers only Approve once and Deny, and persistent grants stay at the machine. The client sees the exact command, path or URL in full, or the host refuses. Each approval is bound to that permission request and confirmed in the transcript. A per-machine host setting turns this on or off, and it is on by default;
- `session.stop`, with the host enforcing the two-press rule;
- agent usage and machine status.

**Next:**
- launch and resume, which needs the host configuration model: workspaces, profiles, worktrees;
- the blob store for large attachments and transfers;
- the purple driver edge;
- observability (§5.10);
- S9 relay at scale;
- the WinUI 3 app;
- S7 macOS.

### Repository layout (new repository until P7, per G1)

```
danswett/agent-bridge (new)                     danswett/agent-ha-bridge (unchanged)
  src/AgentBridge.Protocol/   shared              hooks/, frontend/, install.ps1 ...
  src/AgentBridge.Host/       cross-platform      hook/ (Go; the new package vendors a
  src/AgentBridge.Host.Windows/                          pinned, attested build of it)
  src/AgentBridge.Host.Mac/                       tests/ (PS suites: the behavioral
  src/AgentBridge.Host.Linux/                            spec and trace source)
  src/AgentBridge.App/        WinUI 3
  apple/AgentBridge/          SwiftUI: macOS app now, iOS app at P8 (shared package)
  src/AgentBridge.Relay/      + Dockerfile
  pwa/                        TypeScript
  mcp/                        re-targeted copy
  tests/                      xUnit, contract fixtures, Playwright
```

At P7 the old repository is archived with a README pointer, and the native hook's
source moves to the new repository.

---

## 11. Accounts, costs, and production readiness

**Under GitHub `danswett`:** the repository, Actions (free for a public repository,
including Windows/macOS runners and arm64), Releases, artifact attestations, the
"Agent Bridge" OAuth app, Dependabot and CodeQL. Implementation sessions push and open
PRs as `danswett` (the active `gh` account here). Copilot credits on `danswett` pay for
agent-driven implementation.

**The relay is the one service that must run continuously.** Decided 2026-10-27:
**Azure-native**, built to scale out and add regions (§5.8). The dev relay from S4 stays
on Container Apps until the S9 stamp replaces it.

| Stage | Hosting | Rough cost |
|---|---|---|
| Dev (now) | Container Apps, one replica | about $14/month plus registry |
| Launch | The §5.8 stamp at size `lean`: Container Apps, Cosmos free tier, Traffic Manager name, GHCR | about $15–30/month |
| Growth | The same stamp at `standard`, then `massive`, and more regions as parameter files | see the §5.8 size table; egress about $0.05–0.08/GB on top, which the direct paths reduce |

Options considered and rejected for the hosted relay: App Service (no pod-to-pod
mesh or zonal control), a design that needs a different relay at each size (which
would mean a redesign to grow), and Cloudflare Durable Objects (not Azure).

**Decision needed:** which subscription or billing pays for it. Visual Studio
subscription credits are licensed for dev/test, not for running a service other people
depend on.

**S4 deployment (dev, 2026-10-27):** `agent-bridge-relay` in resource group
`rg-agent-bridge-relay`, West US 2, on a **personal Azure free-trial subscription**
(not corporate), with 0.25 vCPU, 0.5 GiB and one replica, pulling from a Basic ACR
through a managed identity. It costs about $14/month plus $5 for the registry; GHCR
would remove the registry cost. The trial must be converted to pay-as-you-go before it
ends, or the relay stops. Measured cost confirms the low end of the estimate above.

**Production checklist (before public launch):** Authenticode signing; privacy policy
and terms (the relay sees metadata); account deletion; rate limits and abuse
controls on sign-in, pairing and frames; relay monitoring (§5.10) and a public status page fed by the synthetic probes;
crash reporting, opt-in only and never including content; protocol compatibility
policy (N-1 host/client); accessibility pass (keyboard, Narrator, contrast);
localization-ready strings.

---

## 12. Alternatives considered

| Option | Why not (as the backbone) |
|---|---|
| **Microsoft Dev Tunnels** | A development service: 5 GB per user per month, 10 tunnels per user, and a 16 MB body limit. TLS is terminated at Microsoft's edge, so we would need our own E2E layer anyway. Every host must sign in to manage its tunnel. The product terms are not aimed at a consumer product |
| **Tailscale/WireGuard as a requirement** | Production-grade, but it makes every user install and run a VPN, and it is often blocked on corporate machines and Dev Boxes. Supported as "bring your own network" (§5.2) |
| **Keep MQTT (any broker) as the bus** | Users would need a broker. The retained-topic model is what produced the multi-writer and fencing complexity. No push gateway |
| **Azure Web PubSub / Azure Relay** | Managed and reasonable, but per-unit or per-listener pricing is high for an always-connected-host model, and we still need our own E2E, roster and push gateway |
| **Keep the PowerShell daemon** (behind an abstraction) | Lower risk. Rejected by decision, in favor of one cross-platform C# codebase shared with the app, relay and protocol |
| **MSIX** | Signing is mandatory and there are filesystem virtualization quirks when writing `~/.copilot` and other agents' configuration. Velopack plus Authenticode is simpler for a GitHub-released tool |

---

## 13. Risks and open questions

1. ~~**S1 console injection from a console-less .NET process.**~~ Resolved: GO, through an
   injector worker (§6.2).
2. **Corporate policy (S8), parked 2026-10-27 at the user's direction.** Not a gate
   for building P1–P3. Transcripts from corporate machines (Dev Box, DEV-VM1)
   would travel, encrypted, through a personally operated relay to personal devices.
   Today they already reach a home HA. Confirm this is acceptable under SFI and
   corporate device policy before P4. LAN-only or self-hosted relay modes are the
   fallback.
3. **Relay cost and abuse** once others use it: per-account quotas and the
   LAN-direct/WebRTC paths keep egress down.
4. **iOS PWA limits:** no text input in notifications, and the PWA must be installed to
   the home screen. Native apps close this gap. S5 confirmed delivery to a locked
   iPhone works; the phone itself is never on the notification path (§8).
5. **Rewrite scope.** Mitigated by trace replay, shadow mode, per-module cutover, and
   the PS daemon staying runnable until P7.
6. **Version skew between Mac hosts and Windows apps.** Mitigated by `hello`
   capabilities and an N-1 compatibility policy.
7. **Product name and domain** for the relay origin (needed for the PWA and OAuth
   callback URLs). S5 adds a reason: Web Push permission and storage belong to the
   origin, and `github.io` is shared.
8. **Agent tokens on remote machines.** An agent on machine A acting on machine B is
   signed by A's host as an `agent` principal. Decide whether remote agent actions
   need a per-target grant, defaulting to off for launch with allow-all.
