# The Home Assistant connector

**Current compatibility, not the target architecture.** The owner redirected HA
delivery on 2026-11-02 in [#387](https://github.com/danswett/agent-bridge/issues/387):
the separate [integration and companion card](https://github.com/danswett/agent-bridge-home-assistant/issues/1)
own HA entities/actions/presentation. Bridge retains generic protocol/core logic
and host-enforced authorization. The stages, settings and operator procedures
below describe the existing compatibility connector unless explicitly labeled
target or historical; they are not installation instructions for its successor.

A machine moved off agent-ha-bridge's PowerShell daemon onto `agent-bridge-host` stays on the
existing Home Assistant dashboard: its cards, answering, replies, attachments, launch, stop,
usage, fleet status, update buttons and phone notifications, as today (decided 2026-10-29;
design doc §2, §9, §10 "Path to migration", stage 1b, and §0.1 G5–G9). The connector is the
host module that does it, by speaking agent-ha-bridge's **existing per-machine contract
exactly**: the same MQTT discovery configs, state and attribute topics, retained messages,
command topics and pinned entity ids. DSWETT-HOME, the publication writer, still on
agent-ha-bridge, goes on generating the dashboard from every machine's retained session list,
and a migrated machine's cards render unchanged.

It is built in stages. **Stage 1 is read-only status; stage 2 adds answering questions from the
card**, and a read-only shadow that compares the connector with a daemon still running; **stage
3 adds replies, with images and files, from the card; stage 4 adds End session; stage 5 adds
Launch, from the card and from an agent's request; stage 5b phone notifications; stage 6 the
update buttons; stage 7 the daemon's upkeep** (the Detailed activity switch, the liveness
sensor's repair, a live session's entities put back) and what forgetting a machine means here.
The plan, with every stage done, is below.

### Retaining HA without a legacy daemon (#317)

The owner requires HA functionality without a legacy daemon. The later redirect
in #387 supersedes the permanent native shared dashboard/card publisher direction
of #318, **not the functionality and not completion of #318**. #317 remains the
parent goal; #319 remains the explicit writer cutover, rollback and disposable-HA
rehearsal gate. G7 still refuses writer migration. A target diagram or fixture
pass does not authorize cutover.

**Merged compatibility evidence (#322).** The first implementation slice ports protocol 1's policy validation, ordinal canonical
policy JSON/digest/resource URL, receipt-progress checks and writer/artifact authorization
to `HaPublicationPolicy`. Exact rollback pins independently verify the served inline
card's version and bytes; an opaque file URL cannot prove a pin. It is internal
contract code only: no runtime publisher, policy mutation, migration permission or
live HA write is added by this slice.

`PublicationTests` compares those decisions with the original functions, recorded by:

```
pwsh -NoProfile -File tools/ha-contract/Export-HaPublication.ps1 -Source <legacy-checkout>
```

The separate fixture `Fixtures/ha-publication.json` records its own source commit
(`ef5454e8ea6fd1dab304658ca097b5aae7667e97` initially); it does not re-pin the per-machine
fixtures below. The exporter loads only named pure functions from the source syntax
tree, stubs local settings in memory and performs no HA or installed-config access.
Malformed resources are not absence, observers cannot acquire writer authority, and
generation/high-water regressions remain refusals. Policy validation additionally
rejects duplicate JSON members instead of letting a parser silently select one.

### Publisher work disposition (#387)

Verified against main `95983d6031dfc690b5c6a122b2e8f4ac8cac955e` and actual PR
metadata/file inventories on 2026-11-05 PT. These are preservation/extraction
boundaries, not permission to close PRs, remove trees or merge the held stack.

| Work | Retain / extract / supersede |
|---|---|
| [#322](https://github.com/danswett/agent-bridge/pull/322), merged as `d2c40f62e07467a897947a0568a37154e96df194` | **Retain** `HaPublicationPolicy`, `PublicationTests`, `Fixtures/ha-publication.json` and `Export-HaPublication.ps1` as current compatibility/rollback evidence. This internal contract is not a running shared publisher. Final lifetime/placement belongs to #387/#389 |
| [#331](https://github.com/danswett/agent-bridge/pull/331), closed unmerged 2026-11-05 at 12:45 PT; branch kept, retained head `b58fcd42f7b936ef1316ccab42ee88a29908b4ff` | **Preserve** authority observation, protected high-water receipts and recorded malformed/missing/regression cases. **Extract** relevant migration failure evidence through reviewed replacement slices; no claim its receipt runtime is on main |
| [#337](https://github.com/danswett/agent-bridge/pull/337), closed unmerged 2026-11-05 at 12:45 PT; branch kept, retained head `ac1fd159cecff4231ae09e5f5e660e7a52f79898` | Permanent host renderer/writer is **superseded**. **Preserve/extract** card behavior, templates, whole-view recordings, exact-pin/upgrade/conflict tests for HA-side delivery. Keep source commit, exporter recipe, card-byte provenance and `Publication/LICENSE` with any approved extraction; do not copy bundled third-party content without license review |
| [#345](https://github.com/danswett/agent-bridge/pull/345), closed unmerged 2026-11-05 at 00:59:49 PT; retained head `0f81009b434414ccd9209ac103108e6c7db9fa73` | Permanent opt-in host fleet publisher is **superseded**. **Preserve/extract** mixed-fleet reconciliation, selector repair, retirement and restart/rollback failure cases; do not adopt host publication configuration or drift jobs by inertia. Closure is withdrawal, not authorization for cleanup or cutover |

Preserve all commits/worktrees while ownership and extraction are resolved.
[The canonical whole-HA audit](https://github.com/danswett/agent-bridge/issues/389#issuecomment-6617977766)
routes cleanup through #389 and its existing children: #525 runtime/generic seams,
#526 credential/config lifecycle, #527 CI/tooling. No bulk HA string purge:
generic dashboard UI, `driver`/`driverPrincipal`, licenses and historical results
are not obsolete merely because they mention HA.

### Target prerequisites and existing owners

The merged [connector read profile](protocol/connector-read.md) (#534) describes
existing application reads and host-backed reference fixtures, **not** released
enrollment, encrypted transport certification or an installable integration.
[The agent-principals design](design/agent-principals.md) (#522) is design, not a
service-identity implementation. #388/#519 and #55/#391 retain identity, grants,
attribution and roster ownership; this redirect does not design a competing path.
Initial connector reads do not imply any action grant, and agents must never
answer approvals or folder trust.

[#394](https://github.com/danswett/agent-bridge/pull/394)'s original architecture and
enrollment proposal at `d9382fd31dc9fad9d34919317bf7a3265de43c18` is preserved
historical, **unadopted** evidence. That proposal is withdrawn from the corrected
documentation-only #394; preserving its source does not adopt its enrollment
design or protocol audit. Enrollment interface work remains Trust-owned under #388.

#258 (Settings/health) and #175 (sub-agents/shadow) remain existing compatibility
work with their existing owners. Closed #78 is historical token-propagation
scope, not a request to restore that path. No issue is closed or reassigned here.
The supported legacy configuration window, final fixture placement, notification/
action parity and credential revocation/rollback retention still need decisions
on #387 with those owners. Keep current connector credentials/settings and G5/G6/G7
and owner-only management guards until verified #319 replacement/rollback; only
then may #389 remove obsolete dependencies.

## What it never does

* **Publish the shared card resource, the Lovelace dashboard or the publication policy.**
  Publication authority is explicit and fenced in agent-ha-bridge (its AGENTS.md,
  "Publication authority, versions and rollback"); this host is never the writer (G7). It reads
  the served dashboard config, and only to know when an ended session's card has gone.
* **Call a Home Assistant service that names more than one entity.** Every service call goes
  through `ServiceTarget.Check`, a port of agent-ha-bridge's `Assert-HomeAssistantServiceTarget`
  (the 2026-10-02 incident that pressed 166 buttons): one scalar `entity_id` or none, and never
  `area_id`, `device_id`, `label_id` or `floor_id`. The fixture records the original's verdict
  on fifteen shapes and the port must agree on each.
* **Run while agent-ha-bridge runs for this user** (G5). Two publishers of one machine's
  entities would fight over every retained topic. The same probe that gates typing
  (`AgentHaBridgeProbe`: the daemon and supervisor mutexes and the scheduled task) is asked
  before **every write**, cached for five seconds as the app's view of it is, so the daemon
  starting part-way through a long pass stops the rest of it, and the connector pauses —
  publishing nothing at all — for as long as the answer is "running" or "could not be checked".
  Where the probe does not exist yet (off Windows), the connector does not start.
* **Write to a Home Assistant that is not disposable from a development build** (G6). A build
  without `AgentBridgeRelease` runs the connector only when `homeAssistant.disposable` says the
  target is a disposable instance.
* **Read agent-ha-bridge's files.** Its config and token in `~/.agent-ha-bridge` are never
  opened; the host is given a token of its own.

## Transport: Home Assistant's own `mqtt.publish`, not a broker connection

agent-ha-bridge's daemon never connects to the MQTT broker. Every message goes through Home
Assistant's `mqtt.publish` service over REST with the user's long-lived token, at QoS 1
(`Publish-CopilotMqttMessage`), and the entity registry and dashboard are read and changed over
Home Assistant's WebSocket API. The connector does the same:

* one credential, a Home Assistant token, and no broker account on any machine;
* the same path the daemon has proven against the live instance, so nothing about delivery,
  retention or ordering differs from what the dashboard has always seen;
* the entity-id pinning the contract needs (below) is a WebSocket call in any case.

`IHomeAssistant` is the seam. A direct broker transport (MQTTnet) would only add a last will,
which the contract does not use (next section); it is not planned.

### Liveness: a beat with an expiry, not a last will

The daemon holds no broker connection, so the contract has no LWT. Liveness is:

* `binary_sensor.agent_bridge_<slug>_online`, declared once per start with
  `expire_after: 180`, retained;
* an **unretained** `online` on `copilot/cli/machine/<slug>/online/state` every 60 s, with the
  beat's time (`{"at": ...}`, also unretained) on `…/online/attr`, the sensor's attributes
  topic: a state repeating `online` is not written, so `last_reported` would otherwise stand
  still on a machine beating perfectly well (agent-ha-bridge #202). Three
  missed beats and the machine reads offline; a Home Assistant restart cannot resurrect a stale
  "online", because nothing retained says it. The config is published before the first beat
  (two seconds before), because a beat that reaches Home Assistant before its config is
  processed is dropped and the machine would read offline for a whole interval after starting;
* each session's own `copilot/cli/<node>/available`, retained `online` while the session runs
  and `offline` first when it is retired.

On a clean stop the connector publishes nothing, as the daemon publishes nothing: liveness
lapses on its own, the cards stay, and whichever bridge starts next takes them over.

## The contract

`HaContract` holds it; each member names the agent-ha-bridge function it ports. The fixture
(`tests/AgentBridge.HomeAssistant.Tests/Fixtures/ha-contract.json`) is **recorded by running
agent-ha-bridge's own functions**: `tools/ha-contract/Export-HaContract.ps1` loads them from an
agent-ha-bridge checkout by syntax tree (no file's top-level code runs, so nothing reads
`~/.agent-ha-bridge`), replaces the one function that sends (`Invoke-HomeAssistantService`)
with a recorder that still runs the original target guard, calls them with fixed inputs, and
writes what they published. The source commit is in the fixture; regenerating after an
agent-ha-bridge change shows what moved. Payloads compare as JSON (Home Assistant parses them;
key order is not part of the contract), numbers by value, `updated` stamps as a placeholder.

### Keeping up with agent-ha-bridge

The fixtures are pinned at agent-ha-bridge **`8b9f2f4`** (`Release 1.33.33`). agent-ha-bridge
keeps shipping, so `.github/workflows/ha-contract-drift.yml` records the contract again from
its `main` every day (and on demand) with `tools/ha-contract/Test-HaContractDrift.ps1`, which
writes the recording outside the repository and compares it with the committed fixtures,
ignoring only the commit each was recorded from. When something moved it opens, or brings up to
date, **one issue labelled `parity`** with the diff (a comment only when agent-ha-bridge has
moved to a new commit since, so it does not add one a day), and the run fails; an exporter that
can no longer record (a ported function renamed or removed) is reported the same way. When the
contract matches again the issue is closed. On a pull request that touches the exporter, the
guard or the fixtures it runs against the pinned commit instead, which proves the committed
fixtures are exactly what the exporter records there.

It is read-only against agent-ha-bridge: a shallow checkout with no credentials kept, read by
the exporter's syntax-tree loader and `git rev-parse`/`status`. Recording runs
agent-ha-bridge's functions, which loading them from the syntax tree does not sandbox, so it is
a job holding no credential at all; a second job, the only one allowed to write the issue, runs
only this repository's script on what the first wrote, as data. Every run that may write the
issue shares one concurrency group, so two can never each open one.

To re-pin, as its own pull request: regenerate from the new commit
(`pwsh -NoProfile -File tools/ha-contract/Export-HaContract.ps1 -Source <checkout>`), make the
contract tests pass by implementing what moved (or withdrawing it, with the stage that will serve
it), update this document, and close the issue by merging. The re-pin to `42fc551` added the
heartbeat's attributes topic, the update-check button, the launch outcomes topic and the usage
sensors for further Copilot logins (below); the exporter now also runs the daemon's
`Get-BridgeConsoleFailureHint` when a reply fails, which adds a hint only for the Windows
console's refusal of an elevated session, a detail the host never reports. The re-pin to
`8b9f2f4` changed no recorded topic or payload: its two changes (agent-ha-bridge #266, which reads
a question's start however far back in the transcript it is, and #267, which stops the decision log
flooding on OneDrive folders) are inside the daemon, and the host's own transcript reader already
parses every append whole and skims a cold start for unanswered questions.

### Names

| What | Rule (agent-ha-bridge function) | Example |
|---|---|---|
| Machine slug | lowercase, every run of non-`[a-z0-9]` one `_`, trimmed, at most 24, `machine` if empty (`Get-BridgeMachineSlug`) | `DSWETT-DEV-VM1` → `dswett_dev_vm1` |
| Machine node | `agent_bridge_<slug>` | `agent_bridge_dswett_dev_vm1` |
| Machine topic root | `copilot/cli/machine/<slug>` | |
| Session node | `agent_bridge_` + the id's letters and digits, at most 16, lowercased; an id with none gets 12 hex digits of its SHA-256 (`Get-CopilotMqttNodeId`) | `6fcbab0c-1d2e-…` → `agent_bridge_6fcbab0c1d2e4f50` |
| Session topic root | `copilot/cli/<node>` | |
| Session name (its device, its card title) | `Copilot: <workspace name or id[:8]>`, `Claude: <folder>`, `Codex: <folder>`; at most 120; `{{`, `{%`, `{#` broken with a zero-width space (`Get-*SessionDisplay`, `Remove-CopilotTemplateMarkup`), after every `{` that a `{`, `%` or `#` follows. The daemon breaks each pair once, so a run of three braces kept a live `{{` there (`{{{` → `{`+ZWSP+`{{`); here no opener survives, and such a name differs from the daemon's | `Copilot: Fix the build` |

`copilot/cli` and `object_id` are kept although they look odd (every agent, and an id Home
Assistant ignores): the dashboard and the other daemons read exactly these.

### Entity ids are pinned

Home Assistant names an MQTT entity after its device and entity names and ignores `object_id`,
so `sensor.ai_agent_bridge_dswett_dev_vm1_sessions` is what first appears. The daemon forces
each entity onto the id the dashboard addresses, through `config/entity_registry/update`, once
discovery has registered it (`Set-CopilotMqttEntityIds`, `Set-CopilotMqttMachineEntityId` and
friends). The connector does the same 1.5 s after publishing a config, retrying while the entity
has not registered (ten attempts), and leaves an id that is already right alone. An entity that
is already pinned keeps its id across a republish, a restart and a change of publisher, which is
what makes a migration duplicate-free.

### What stage 1 publishes

Per machine:

| Entity (pinned id) | Discovery topic | State / attributes | When |
|---|---|---|---|
| `binary_sensor.agent_bridge_<slug>_online` | `homeassistant/binary_sensor/<mnode>/online/config` | `…/online/state`, unretained `online`; `…/online/attr`, unretained `{"at"}` | config at start; beat every 60 s |
| `sensor.agent_bridge_<slug>_sessions` | `homeassistant/sensor/<mnode>/sessions/config` | `…/global/state` = count; `…/global/attr` = `sessions[{name,machine,node,kind}]`, `machine`, `machine_slug`, `capabilities`, `resumable: []`, `discovery_held` (what holds the host's discovery, [v1-sessions.md](protocol/v1-sessions.md#discovery-held), else `""`), `updated` | on a change, and every 300 s |
| `update.agent_bridge_<slug>_update` | `homeassistant/update/<mnode>/update/config` | `…/update/state` = `installed_version`, `latest_version` (both the host's version), `title`, `release_summary: ""`, `in_progress: false`, `update_percentage: null`; `…/update/attributes` = `stage`, `stage_detail` | at start; from stage 6, on an installed host, what its release checks and updates find (below) |
| `sensor.agent_bridge_<slug>_usage_<agent>` (copilot, claude, codex) | `homeassistant/sensor/<mnode>/usage_<agent>/config` | state = the window closest to running out, or `unknown`; attributes `client`, `label`, `account`, `account_id`, `plan`, `source`, `windows[{key,label,percent,used?,limit?,unit?,resets_at?,elapsed?}]`, `error`, `measured_at`, `machine`, `machine_slug`, `updated` | when a reading changes |
| `sensor.agent_bridge_<slug>_usage_copilot_<n>` (2–4: the further Copilot logins of the host's `usage.copilotAccounts`, by place, `Get-CopilotMqttUsageRecordKey`) | `homeassistant/sensor/<mnode>/usage_copilot_<n>/config`, named `GitHub Copilot usage (<login>)` | as above, `client` `copilot` and `account` the login (GitHub's spelling once read); a login that cannot be read is `unknown` with its `error` | when its reading changes |

`capabilities` is what lets the writer draw the right rows for this machine. Without a launcher
that launches here the connector reports `newSession`, `profile`, `resume`, `agent`, `tuning`,
`permissions`, `resumeShare` false, `launchRequest` 0 and `fleet` empty, so no launch card is
drawn for it (stage 5, below, for a machine that launches); `detailed` true only while the machine's
`input_boolean.agent_bridge_<slug>_detailed_activity` exists (the connector creates it when it is
missing, from stage 7), so the Detail switch is drawn only when it has something to switch; `dev` true for a development
build, so the machine row says `(dev)`.

Per session:

| Entity (pinned id) | Discovery topic | State / attributes |
|---|---|---|
| `sensor.<node>_status` | `homeassistant/sensor/<node>/status/config` | `working`, `waiting`, `idle`, `agents` (idle, background agents running), `shell` (idle, background commands running) or `ending`; while the host sets the session's `attention` (#800, below), the clients' words instead: `Needs sign-in`, `Signed in · waiting for the session to confirm`, `Agent error` (`Not authorised` or `Rate limited` by class), `Not responding`. Attributes `session`, `machine`, `process_id`, `updated`, and only when non-empty or non-zero `model`, `background_agents`, `background_shells`, `compactions` (`Add-DaemonTuningAttributes`), and `attention` while it is set |
| `sensor.<node>_activity` | `homeassistant/sensor/<node>/activity/config` | a line, cut to 255 with `...`: `Running: <tool>`, the reply's first line, `Thinking`, or `Idle`; attributes `session`, `machine`, `verbose`, `updated`, `driver`, `history` (12 deep, 24 with Detailed activity), and the card text as `Add-DaemonCardText` lays it out: `response` (to 6000 characters, then `_(truncated - see terminal)_`) with `response_kind` `text` or `reasoning`, and for an agent that does not interleave its thinking, `reasoning` (to 4000). Added, only for a session with sub-agents: `subagents` and `subagents_more` (below) |

Both carry the device block (`identifiers: [<node>]`, `name`, `manufacturer: AI CLI bridge`,
`model: <machine>`) and `availability` on `copilot/cli/<node>/available`. A renamed session
republishes only these two configs (`Update-CopilotMqttSessionName`). Status and activity are
published when they change, compared without their `updated` stamp.

How the host's `Session` maps to what the daemon kept, where the two differ:

* The daemon remembers the newest message and whether it was a thought; the host keeps the
  newest reply and the newest reasoning, and keeps the previous turn's reply while a new turn
  thinks. The connector therefore remembers, per session, which of the two changed last, and
  shows a thought as the newest line (Detailed activity on, `response_kind: reasoning`) until
  a new reply arrives. A session first seen (a start or restart) is thinking only while it has
  no reply at all, the one case the snapshot alone settles.
* The trail is rebuilt from the host's tool history in the daemon's words: `Reading your
  message`, `Running: <tool>` per call, `Thinking: <first line>` with Detailed activity on
  (`Get-BridgeThoughtLine`, ported and fixture-checked), the reply's first line.
* `driver` is `human` until an answer from the card sets it (stage 2, below); replies and
  launches set it from stages 3 and 5.
* **Attention** (#800, [v1-sessions.md](protocol/v1-sessions.md#attention)). The host keeps a
  session's `status` truthful: `idle` after a turn that ended on a 401 or an agent error, with the
  turn before's reply still beside it. On its own, that read on the card as an idle session that
  had answered. While `Session.attention` is set, the status sensor's state is the clients'
  words for it, in the order of their status rules (`StatusRules`, `AuthState.ErrorWords`): the
  session's own question still reads `waiting`, then `Needs sign-in` (or `Signed in · waiting for
  the session to confirm` while recovery is `pending`), then a delegate's question `waiting`, then
  `Not responding`, then `working`, then the error's words (`Not authorised` for `auth`,
  `Rate limited` for `rate`, otherwise `Agent error`). An `ending` session still reads `ending`.
    The card shows the state as written.
  The status attributes gain `attention`:
  `{kind, error_class?, provider?, message?, since}`, with `kind` one of `sign_in_needed`, `error`
  or `not_responding`. `error_class` is only for an error. `provider` is only for a sign-in, and
  only when it is `github`, `anthropic` or `openai`. `message` is the host's own sentence
  (`AgentErrors.Template`, never the agent's words), on one line, within 300 characters, with its
  template markup broken. It never carries the account, a sign-in code or a URL. A value the
  connector doesn't know makes no claim, as on the clients: an unknown kind is an error, and an
  unknown class is `other`. A session without attention publishes exactly the daemon's attributes.
  End session still judges the session by its own status, so one idle after a failed turn ends at
  one press, as the host ends it. The shadow shows these as differences from agent-ha-bridge,
  which has no attention.
* **Sub-agents** ([design/subagents.md](design/subagents.md) §7, #175). The host's top-level
  `response` and `reasoning` are the latest writer's, a delegate's included, as on
  agent-ha-bridge's card. The connector publishes the **main agent's own** instead: `own`'s
  text for a field `activity.source` gives to a sub-agent, else the top-level text, else
  `own`'s. That holds for the card text, the activity line, the trail and the "finished"
  push (stage 5b). A main agent with no words of its own shows none, never the delegate's.
  This is a deliberate difference from agent-ha-bridge, which by the owner's decision of
  2026-11-01 keeps showing the latest writer's. The shadow labels it `main-agent-only (#175)`
  ([host-shadow.md](host-shadow.md)). The connector reads each session **before** the
  protocol's 40 KiB budget (`ISessionSource.GetForms`), within the field limits only.
  `SessionBudget.Fit` trims the fields older clients ignore first, `own` among them, so in a
  session over it the budgeted form can have lost the main agent's reply while a delegate's
  16 KiB stays (found in review). Both forms come from one read, so a card never mixes two
  moments (a waiting Claude without its question would push as blocked). A change only
  that form shows sends clients no upsert, since none would see anything new; the source
  tells the connector through `UnbudgetedChanged` instead. Home Assistant's limits are the
  connector's own: the card text and trail are capped as above, the delegates budgeted
  below, and the question is still
  the budgeted form's. That is the one the host answers, and a question cut to fit is
  answered at the machine there and here alike. The whole activity payload is held to 15 KiB
  as well (`HaPayloads.FitCard`), since agent-ha-bridge's layout can pass Home Assistant's
  16 KiB: the reply goes in `response` and, its first line whole, in the trail, and JSON
  escaping can double either. Only a card over that is touched, so any other is the daemon's
  exactly. The trail's lines are cut first, as the activity state is (255 characters), then
  its oldest lines go, then the reasoning expander, then the shown text, its start kept with
  the truncation note.
  Delegates are fitted after that, into whatever room is left. Should a reply be gone all the same
  (`own.responseTruncated` with no `own.response`), the card says the reply is too long to
  show here, never blank and never the delegate's. The delegates themselves go in two **additions** to the
  activity attributes, present only while the session has any, so a session without
  sub-agents publishes exactly the contract. `subagents` holds the first 5 entries of the
  host's list (running first, then the most recently finished), each `{name, status, line}`:
  `status` is `working`, `waiting`, `idle` or `ended`, and `line` is absent until the
  delegate has said something. `subagents_more` counts the entries left out, here or by the
  host, when there are any. A name is a delegate's own words, so it is cleaned again here
  as the host cleans it (`DelegateName`, [design/subagents.md](design/subagents.md) §6, #263):
  no bidi, invisible or control characters, at most 60 characters and 120 bytes, and
  `Delegate “<name>”` for one that reads as "Main agent"; one with nothing left is `""`.
  Lines keep their last 300 bytes, whitespace collapsed; template markup is broken in both,
  as in session names, so five entries come to a few
  KiB with any number of delegates. They are also budgeted against the rest of the
  attributes, never at the card text's cost: past 15 KiB in all (Home Assistant keeps 16 KiB
  of a state's attributes, less room for End session's hint and its own), the lines go, then
  entries from the end, each counted in `subagents_more`. A session whose card text fills
  that alone, or whose every entry the host's own budget dropped, publishes `subagents: []`
  with the count. Room for that much is kept before the card text is fitted, so the 15 KiB
  holds with it too. The card draws neither yet.

### What is withdrawn

Everything agent-ha-bridge publishes that the connector would not act on is withdrawn (an empty
retained payload), so the dashboard never offers a control that silently does nothing:

* per machine, at start: the transfer request with its state topic; the Install and Check
  buttons where the host's updater cannot act on them (a copy it did not install, or
  `homeAssistant.updates.checkForUpdates` off; stage 6); when the host cannot launch here, also
  the launch card's prompt, prompt payload, workspace, profile, agent, model, effort, context,
  resume and permissions selectors, Launch, the launch result and its outcomes (2 of the 28
  machine topics, 4 without the update buttons, and 15 more without a launcher); and the usage sensors of further Copilot logins
  (`usage_copilot_2`–`_4`, three topics each) in a place the host's `usage.copilotAccounts` does not
  fill, so a reading the daemon or an earlier configuration left is not kept with nothing to refresh
  it (as the daemon's first pass does with `Remove-CopilotMqttUsageSensors`). Taken from the
  configuration rather than the readings, so a login not yet read is not withdrawn and registered
  again under a new id moments later. With `usage.publish` off every usage sensor goes, the
  agents' own too: agent-ha-bridge leaves its sensors when told not to publish, but a reading
  left with nothing to refresh it is exactly what this withdrawal is for. Forget
  clears all 46 (`Get-CopilotMqttMachineTopic`: 28 plus three per usage sensor);
* per session, when the connector first publishes it: the two legacy state topics
  `decision/state` and `reply/state`; End session when the host cannot end sessions here (no
  `ISessionStopper`); and, only when the host has no way to answer (no `ISessionAnswerer` on this
  platform), also the decision and field selectors (12), the submit button, the reply box and the
  reply payload sensor, as stage 1 did.

The generated dashboard draws "Entity not found" for those controls until the stage that serves
them lands. With stage 6 every control a machine row draws for an installed host is served; the
transfer request stays withdrawn because sharing a session between machines is not a host
feature yet. With stage 7 the daemon's upkeep is ported too, so what is left before a real
machine moves is the rehearsal ([parity-checklist.md](parity-checklist.md)).

### Stage 2: answering from the card

With an answerer, a session's first publication is agent-ha-bridge's full set in its order
(`Publish-CopilotMqttSession`): availability, the decision selector (`Idle`), the reply box,
the reply payload sensor, status, activity, the twelve field slots (`Idle`, `Field <i>`) and
Send; everything but stop. 900 ms later (`FieldPinDelay`, the daemon's wait for discovery) the
controls are pinned to their ids, and the reply box, if it holds no value, is primed to `' '`
as `Add-DaemonSession` primes it, so the card shows an empty field rather than `unknown`. `HaDecision` is the port of `Set-CopilotMqttDecision`,
`Publish-CopilotMqttDecisionFields` and `Clear-CopilotMqttDecision`, and
`Fixtures/ha-decisions.json` records the originals on ten questions (free text, approval, a
long question, single choice, yes/no, text, a form, a twelve-slot multi-select, unsafe and
empty multi-select options), three clears and the multi-select code table.

**Arming** a question, as the daemon does:

1. the decision selector's config with its options: `Awaiting answer...` and `Cancel request`
   around the choices when there are any and no field slots, otherwise just the two;
2. `select_option` `Awaiting answer...`, retried while Home Assistant has not yet applied the
   new options (12 attempts, 150 ms apart);
3. the field slots: every slot the previous question used, or all twelve when that is unknown,
   each with `Choose...` (a choice), `Idle` (text, or unused), or the multi-select's default;
4. the decision attributes the card reads: `decision_id`, `question` (to 6000 characters, then
   a truncation note), `choices`, `mode`, `multi_field`, `field_count`, `session`, `machine`,
   `asked_at`, and per field `field_<i>_label`, `_multi`, `_options`, `_separator`, `_codes`,
   `_empty`, plus `answer_on_tap` and `takes_typed_answer` when they apply.

A sub-agent's question (Question `agent`, [design/subagents.md](design/subagents.md) §7) is
armed, read and answered exactly as the session's own: the card has one question, and it
answers whichever the host says is pending. Two things differ, both additions. Its `question`
begins with "Delegate *name* asks:" and a blank line, so the card shows whose it is without knowing
about delegates. The name is added before the length check above, so it can never be what
makes an answerable question one the card would have to cut. And the decision attributes
gain `agent` (the sub-agent's id) and `agent_name`. The pushes for it (stage 5b) are titled
"Delegate *name* asks · *their usual title*" and keep the session's tag. *name* is cleaned as in the
`subagents` attribute (`agent_name` is that alone, `""` without one), and the line is "A delegate asks" when the
list no longer has it or nothing is left. The word "Delegate" is always there (#263), as in the
clients (ui.md §6.3): no look-alike check is complete ("Ma1n agent" passes it), and the main
agent's own lines never have it. A name already starting "Delegate " is not given it twice.

**Reading** an answer. Presses come from one held WebSocket `subscribe_trigger` on every armed
session's Send button and reply payload sensor (the daemon's `Wait-CopilotHaStateChange`), and
every card with an open question is also read every 5 s (`AnswerSweep`), so a press made while
the watch was reconnecting is late, never lost. A press counts
only when it changed from the baseline taken at arming, was not already spent, and its
`context.user_id` is recorded for `driver`. The fields are read from the slots, choice labels
mapped back to their values (`#n,m` codes for a multi-select), numbers parsed, `Other` taken
from the reply card or from the reply box with Send; a form's text field takes reply-card words,
or reply-box words typed since the question was shown (never what the box held before it). A
free-text question is answered from the reply box or a reply-card payload whose `decision_id`
matches. Reply-card words sent before a form is complete are said (`answer every field`) and
held: they fill the form later, but only a press of Send submits it, and box words typed after
them replace them. An approval tap counts only on a selector seen waiting after the question was
put on screen, and naming it: a tap left over from the last question, made while this one was
being shown, or made on a question something else put on the card never answers it. The answer goes through the host's `ISessionAnswerer`, the same
path as the app's, so G5, the delivery gate and the attempt ledger apply unchanged.

**Telling the card**, on the activity line, as `Set-DaemonTransientActivity` does:
`Sending answer...`, then `Answer sent` (with `answer`, `at` and `answer_consumed_at`, which the
reply card uses to clear its draft, and the reply box reset to `' '`), `Answer unconfirmed -
check the terminal`, or `Answer NOT sent` with a hint. After `NOT sent` the attempt that failed
is never retried on its own: a new Send press, a new reply-card publish, a new tap (the
selector goes back to `Awaiting answer...`, and a tap counts again only once it has been seen
waiting), or for words already given (in the reply box, or from the reply card into a form) a
press of Send made after the failure, answers again, each as a request of its own so the host
does not answer it from the earlier refusal.
`Not sent - answer every field` (with `waiting_on`), `Not sent - this question takes options`
and `Not sent - no Send button on this session` say what was wrong with the press itself; such
a press is spent. `Not sent - type your answer again` says reply-box words were typed before the
question was verifiably on screen; they are not sent, the question stays open, and words typed
after are sent as usual. A re-arm or host restart takes a fresh window check. A question that ends is cleared (selector `Idle`, attributes `{}`, the slots
it used reset); a question the card still shows when the connector takes over, but which the
session no longer asks, is cleared too.

**Driver.** An answer whose press's `context.user_id` is in `homeAssistant.agentUserIds`, or
whose reply payload says `driver: agent`, makes the turn it answers agent-driven (the purple
edge) at once. An answer continues that turn, so it arms nothing for the next prompt, which is
whoever's it is (the daemon does not attribute answers at all; below). Replies and launches arm
the next turn: it is agent-driven if it starts within 600 s (`DriverArmSeconds`), otherwise it
reads `human`. The host keeps it in
one record (`SessionDrivers`) for the cards and its own clients alike, so the phone and the app
show the same edge (`Session.driver`, with `driverPrincipal` `homeassistant:<user id>` for the
account that pressed), and a person's answer or reply from either takes it back as one on the
card does.

Where stage 2 knowingly differs from the daemon:

| Difference | Why |
|---|---|
| A question the host cannot answer from a card (`AnswerAtMachine`, or a question the host had to truncate) is armed with `**This one has to be answered in the terminal** - <why>` appended and is never read | The host already knows these cannot be delivered; the daemon learns it later |
| A question longer than the card's 6000 characters (with any hint) is answered in the terminal, never on the card; the note is kept within the limit | The daemon cuts it and still takes an answer, so an approval could authorise text nobody could read |
| An approval holding a character not drawn where it runs (a bidi control, a zero-width character, a control but newline and tab), right-to-left letters, or a character reference that an HTML renderer would decode to one (`&#x202E;`, `&#8238` with no semicolon or with leading zeros, HTML5 names such as `&ZeroWidthSpace;`, `&NoBreak;` and `&nbsp;`, `&nbsp` with no semicolon), is answered in the terminal, on the card as its source in a code block (those characters shown as visible escapes, its fence longer than any run that starts one of its lines), so nothing in it is decoded or drawn as Markdown. Its push carries none of the prompt, only that an approval is waiting at the machine (#220). A host sends hidden characters and right-to-left letters already escaped, offering Approve once for them (#253, decision 3), so from a host it is a reference that holds an approval here, which Markdown would decode; the card shows every approval as its source all the same. A delegate's name is judged as part of its approval as the session reported it, before it is cleaned (#263) or its template braces are neutralised; a cleaned name that could still be drawn other than as written (right-to-left letters, or a reference) is "A delegate" on the card and in pushes, and "Delegate *name*" leads the card as literal text, what Markdown acts on in it backslash-escaped, so it cannot open a fence, link or emphasis around the prompt. A question whose card would now read or be answered otherwise under the same id (such a name arriving, a host updated to refuse it, a form's options or their values changed) is armed again: nothing on the card or a phone is taken for it from then until it is, however long a channel Home Assistant cannot read holds that up; it keeps what it has been through (attempts, presses and taps used); one answered, or maybe answered, is never armed again; and its phone buttons are withdrawn and sent again under the name of what it says now, so a tap on the old ones answers nothing, even after a restart. An approval the host holds at the machine or had to cut is cut, if the card must, inside its block, before the note saying why | The card and the phone draw text through the bidi algorithm, so what a person reads could run in another order. The host sends hidden characters and right-to-left letters escaped, and still offers Approve once (#253, decision 3), but a reference stays as written and this card's Markdown decodes it; the card holds to its rule whatever it is given. A code block spares the card from knowing what Markdown would make of the text around a reference (a code span, an HTML block, a heading). A phone may draw a push as HTML (decoding a reference) or as plain text (showing `&amp;` as written), and nothing here knows which. Neutralising a name's template braces adds a zero-width space of the card's own |
| An approval says what its escapes stand for above it, "_Contains hidden characters_" or "_Contains right-to-left text_" (#220). Every approval is on the card as its source, in one code block whose fence nothing in it can close, as the PWA and the app draw it: none of it is read as Markdown. One Markdown would not draw as written (the agent's own words closing the command's fence, a link, an image or HTML, text after a block, or a line in a block shaped as a fence, `ApprovalShape`) is still answered in the terminal. One that does not fit whole on the card as its source is answered in the terminal, never cut | The host sends hidden characters escaped and Approve once is offered (#253, decision 3), yet PowerShell ends a `#` comment at a bare CR, so `# list files\u000DRemove-Item ...` reads as a comment and runs. The agent's own words reach the card unescaped until #212. Drawn as Markdown, an intention ending in `~~~` opened a block that swallowed the host's fence, so the command's backtick lines were markers, never drawn, and bash ran them as command substitutions |
| An empty multi-select answer is allowed only for Copilot | The host's `Field` carries no `minItems`; Copilot's own forms allow it |
| `takes_typed_answer` only for a lone choice that allows another answer | The daemon also offers it for yes/no, which the host's question model cannot take |
| Claude forms use field slots, never the selector's choices | Claude's questions always arrive as forms here |
| An approval offers `Approve` / `Deny` on the selector with `answer_on_tap`. One the host escaped anything in (`escaped`, #803) offers `Deny` only, under "_Approve from the Agent Bridge app or at the machine._" (or "_Approve at the machine._" when its `approve` is `machine` or `tooMany`), and its phone buttons are Deny only. One that is also `answerAtMachine`, as one listed while other Copilot permissions wait usually is (#690), offers nothing, Deny included | The host's approval is a question kind of its own. Approve on an escaped approval needs a person's signed confirmation from an approved device, which Home Assistant cannot give: it has no device key, and its users include agents (`agentUserIds`) |
| `Cancel request` says `Answer NOT sent` and resets the selector | The host has no way to cancel an agent's question |
| A reply while a question is open answers the question; at a question only the terminal can answer, it says `Reply NOT sent` (`the session is waiting on a question`) | The host refuses a reply while its session asks a question |
| A free-text answer from the reply box must differ from the box at arming | The box keeps the previous reply; the daemon reads it the same way through its baseline |
| Box words that differ from the baseline but were typed before the question is verifiably on screen (selector `Awaiting answer...` under its decision id) are not sent: `Not sent - type your answer again` | Those words were typed looking at something else; the daemon has no such window |
| An answer from the card makes the turn it answers its sender's (the purple edge for an agent), arming nothing for the next prompt | The daemon attributes only replies and launches, so an agent answering from the card never showed purple. An arm left for the next prompt, as a reply's is, would mark the person's own next prompt at the machine as the agent's |
| Baselines, `answer_consumed_at` and a pending driver are kept in memory | A host restart re-arms every open question with a fresh baseline, so a press before it is not replayed |

### Stage 3: replies from the card

With a replier as well as an answerer (`ISessionReplier` on this platform), a card with no
question on it takes replies, read as the daemon reads them, and `Fixtures/ha-replies.json`
records the daemon's own `Send-DaemonCardPayload`, `Send-DaemonReplyBoxText` and
`Invoke-DaemonReply` on sixteen cases (card words, attachments, an agent's publish, a refusal,
an image that cannot be fetched, incomplete metadata, a publish for a question, blank and
repeated publishes; box words, a refusal, a follow-up typed during delivery, a blank box from a
person, from an agent and given up on, a repeated press) with the daemon's constants. The
contract tests assert the connector tells the card exactly what the daemon told it, and makes
the same service calls.

**Request id and acknowledgement.** A sender may add `rid` (1 to 64 letters, digits, `-`, `_`, `.`;
anything else is ignored) and `sender` (a name, at most 64 characters). The host then publishes
`reply_ack` `{rid, outcome, at, sender?, duplicate?}` in the session's activity attributes in the same
update as `Reply sent` / `Reply NOT sent`. `outcome` is `delivered`, `unconfirmed` or `refused`. The ack
stays until a newer one replaces it, so a sender that finds another rid there knows its message was
swapped or lost and sends again. A rid seen before (the last 50 per session) is not delivered again; the
host re-acks it with `duplicate: true`. A `refused` rid is forgotten, so the same rid may be retried.
The history is kept per session in the connector, not on the card, so a pause or restart that rebuilds
the cards neither empties it nor loses the rid of a delivery still running; a repeat after the rebuild
is still dropped. When a session ends for good its history is dropped with its card (after any
delivery still running for it finishes); a session that comes back under the same id starts empty.
`sender` loses control, format and unassigned characters, whitespace is collapsed,
and the cut at 64 never splits a character. A reply discarded because a question opened gets no ack.
Publishes without a `rid` behave as before.

**The reply card** publishes `{at, text, images: [{id, name, content_type}], files: [{name,
content_type, b64}], driver?}` (not retained) to `copilot/cli/<node>/replypayload/set`, read
through the reply payload sensor: its state is `at`, the stamp that makes a publish new. A new
stamp is one reply. One tagged with a `decision_id` is words for a question, used by stage 2 or
not at all. One whose `images` or `files` hold an entry the connector cannot read, or are not
lists, says `Reply NOT sent` (`The attachment metadata is incomplete; no reply was sent.`) and sends nothing. The
images are fetched from Home Assistant's image store (`GET /api/image/serve/<id>/original`,
ids of letters and digits only, at most 25 MiB, above Home Assistant's own 10 MiB upload
limit, within the client's timeout for the whole body); the files are decoded inline, at most
256 KiB each (`Save-BridgeReplyFile`). Any that fails, a timeout included, says the daemon's
sentence and sends nothing. More than ten attachments (the host's limit for one reply) are refused
before any is fetched. A reply that went in deletes its images
from the store (`image/delete`, beside the passes, so a slow delete holds up nothing); one
that reached nothing keeps them. The publish takes
precedence over the reply box in the same pass.

**The reply box and Send.** A new Send press sends the box's words. The tap on Send is what
commits the box, so a press usually arrives first: the box is read again up to six times,
500 ms apart, then, for a person, the card says `Waiting for your text` and the press stays
armed, sending the words the moment they arrive, for ten minutes (`SubmitArmSeconds`), after
which it says `Nothing to send` (`Type a reply first, then press Send.`). An agent's press at an
empty box says `Nothing to send` at once, since an agent's text commits immediately; unless its
reply-card publish went in within the driver window, when the press is that message's Send and
its arm is kept. After a reply, sent or not, the box is reset to `' '` once it is read still
holding what was sent, never if something was typed meanwhile or it cannot be read.

**Telling the card**: `Sending...`, then `Reply sent` (`sent`, the first 60 characters, and
`at`), `Reply unconfirmed - check the terminal`, or `Reply NOT sent` (`error`, and `unsent`
when delivery refused it).

**Driver**: a press's `context.user_id`, or the publish's `driver`, arms the next turn as for
an answer. A reply that reached nothing disarms it; one that went in restarts its window
(`Update-DaemonDriverPendingStamp`).

**Delivery.** The reply goes to the host's `ISessionReplier` (`session.reply`, as the app's),
each press or publish its own request id. Its files go into the `AttachmentStore` as the
desktop app's do over the local pipe (direct, in 11 KiB chunks, checked against their SHA-256),
under a peer of their own (`home-assistant`), and the reply names them; whatever the reply did
not use is discarded afterwards. Delivery may take a minute to confirm, so it runs beside the
connector's passes, and the card is read again as soon as it is done; status, answers and the
other cards are not held up meanwhile. A pause (agent-ha-bridge starting) does not lose track of
a delivery under way: the card waits for it, and what it comes to touches only the driver arm
it made. Publishes the watch reports meanwhile wait their turn and each is sent, in the order they
came (the sensor itself shows only the latest); any still waiting when the session asks a question
are said to be not sent.

**Which presses are replies.** What the reply controls hold when the connector first reads a
card is never a reply: a card taken over from agent-ha-bridge, or from this host before a
restart, may hold one already sent. Nor is anything given while a question was open: as the
question goes, its controls are read again, before the card is cleared, and nothing the watch
reported before that is taken for new. A press or publish
while the connector was not running is not sent when it starts.

Where stage 3 knowingly differs from the daemon:

| Difference | Why |
|---|---|
| The host stages the files and types `@path` itself (`ReplyService`), not the connector | The same staging, naming and private folder as an app's attachments |
| `Reply unconfirmed - check the terminal` | The host's delivery can be unconfirmed; the daemon has only sent or not |
| A reply of attachments alone shows `(n attachments)` as `sent`, not the typed `@path` line | The connector never learns where the host staged them |
| A press or publish made while the connector was not running is not sent at start | The daemon keeps what it handled in its state file; the connector cannot know what agent-ha-bridge already sent, so it never replays |
| Words given while a question is open never become a reply once it goes | The host refuses replies while a question is open, and they belonged to the question |
| Publishes made while a reply is being delivered are each sent, in order | The daemon reads only the sensor's latest, but types a reply in well under a second; the host's delivery waits up to a minute for confirmation |
| More than ten attachments in one publish are refused before any is fetched | The host takes at most ten per reply; the daemon has no limit |
| After a box reply, a box that cannot be read is left as it is | The daemon clears it then; here the press is already spent, so nothing is sent again, and clearing blind could delete a follow-up typed meanwhile |

### Stage 4: End session

With the host's stop delivery (`ISessionStopper` on this platform), each card carries End session
(`button.<node>_stop`, last of its controls, as `Publish-CopilotMqttSession` publishes it), and a
press is taken as `Invoke-PendingStops` takes it; `Fixtures/ha-stops.json` records the daemon's
own functions on nine cases (an idle session's one press; the first press of a working, waiting,
background-agent and background-shell session; the confirming press; an end that fails; a
repeated press; a confirmation that lapses), the hints for every status, and the summary the
card keeps while it asks (`Get-DaemonCardSummary`).

* **Two presses for a session that is not idle.** The rule is the host's own (`session.stop`,
  the same for the app and the PWA): a first press arms it and hands back a token, which the
  connector sends with a second press within the window (10 s); a press after the window is a
  first press again. An idle session ends on one press.
* **The card while it asks**: `Press End session again to end it`, with the daemon's hint
  (`this session is still working - the confirmation lapses in 10s`, and so on by status),
  kept over the session's own activity line until the window closes, while its reply and
  reasoning stay live. A first press the card could not be told of is forgotten, so the press
  made because it looked dead is never taken for the confirmation.
* **A lapse** says `End session NOT confirmed` (`no second press within 10s`), and the
  confirmation is forgotten only once that is shown, retried every 2 s
  (`StopLapseRetrySeconds`) otherwise.
* **Ending**: the status `ending` and `Ending session...` first; then `ended` once the host
  has ended it (the card then goes as any ended session's does), or `Could not end session`
  with the host's reason and the status `error`. A press the host refuses outright (the
  session's process unknown, say) says `Could not end session` and the card shows the session
  as it is again.
* Ending can take a while (the session is given time to exit before it is terminated), so it
  runs beside the passes; a press while it does is not a second end, then or afterwards, and
  a confirmed end is never said to have lapsed however long the host takes. A press made
  before the card has said what came of the last one (even one reported only afterwards) never
  confirms the question it then asks, and one made the moment it asks is taken: the confirming
  press must be no earlier, on Home Assistant's own clock, than the card's change to the
  question (its `last_changed`), so a double press never ends a working session; while it asks,
  the button is read every pass, so a second press is seen within the window even with the
  watch down. The window is counted from when the press went to the host, so it never outlasts
  the host's own, and a question the host puts too late to answer is not put.
* What the button holds when the connector first reads it is not a press; a session asking a
  question can be ended like any other.

Where stage 4 knowingly differs from the daemon:

| Difference | Why |
|---|---|
| The second press is checked by the host (token, same process), not by the connector | The host's `session.stop` is the one rule for every client; the daemon's process-identity check is the same check |
| A press with no process behind the session is refused (`Could not end session`) | The host never ends what it cannot identify; the daemon lets it through to a stop that then does nothing |
| Closing the window the bridge opened is the host's launcher's concern, not the connector's | The connector only presses; the host owns its windows |
| Another notice replacing the question on the card (`Answer sent`, say) withdraws it: the next press asks again | The daemon pins the question back at its next publish, but until then a press would end a session whose card no longer asks |

### Stage 5: Launch

With the host's launcher available here (`ISessionLauncher`, the one `session.launch` and the
app use), the machine's launch card is served as `Publish-DaemonNewSessionControls` publishes
it: the prompt box (`text.agent_bridge_<slug>_new_prompt`, 255 characters), the whole-prompt
sensor on the retained `newsession/promptpayload`, the workspace, profile, agent, resume, model,
effort, context and permissions selectors, Launch (`newsession/start`) and its result, whose
attributes are the retained `newsession/outcome`. `Fixtures/ha-launch.json` records the daemon's
own functions: the configs for a full card and an empty one, the note's 255-character cut, the
resume labels (which the host's launcher gives its resumable sessions, so the drift guard watches
them for it), the request kinds and their refusal texts, eleven requests resolved against the
recorded machine (`Resolve-DaemonRequestedLaunch`), the outcome record and the capability flags.

* **The card's choices are the host's**: its approved workspaces, its installed agents, Agency's
  profiles, the selected agent's models, efforts and context sizes (with `Agent default` first),
  and the host's resumable sessions as its launcher lists them (newest first, cut to
  `newSession.resumeCount`, each titled as the daemon labels it), offered verbatim. They are
  read from the launcher every 30 s and when the agent selector changes; the configs are
  republished only when what they offer changes. A selector holding nothing usable (`unknown`
  after a Home Assistant restart, or a value no longer offered) is driven to the machine's
  default; a choice someone made is never overwritten. `capabilities` then reports
  `newSession`, `tuning`, `permissions` and `launchRequest: 1`, and `profile`, `resume` and
  `agent` when there is such a row to draw.
* **A person's press** launches what the card shows. The prompt is the payload's whole text when
  it has one, else the box's; a tuning value the selected agent does not offer (left from
  another agent) is the machine's default, `Agent default` is the agent's own; only the exact
  `Allow all` allows all. The note says `Starting <agent> in <workspace> (<profile>)...` or
  `Resuming <label>...`, then goes once the host says the session started, or becomes the
  host's reason when it did not. A prompt that started a session is cleared from each box that
  still holds it once the host says so; a prompt or request written meanwhile is left alone.
* **An agent's request** (agent-ha-bridge #227): `{at, text, request, settings}` published to the
  prompt payload and then a press. Its settings are the only ones used: one left out is the
  machine's default, never what the card shows; one this machine does not offer is refused in
  the daemon's words, naming what it does offer. Names are matched in any case, as the daemon's
  PowerShell compares them, and the host is given the offered spelling; permissions alone must
  be exact. Its outcome (`starting`, then `started`,
  `refused`, `busy`, `failed` or `unconfirmed`, with the session id and what was launched) is
  kept with the four before it, newest first, on `new_session_result`'s `recent`, which is what
  the bridge's MCP `launch_agent_session` reads. A request published after the press waits for
  its own; one more than 60 s older than the press is skipped, words and all, and the press
  launches from the card; an id not matching `^[A-Za-z0-9-]{8,64}$` is refused; one already
  served is never served again; and one that reached the machine more than five minutes after
  its press is refused rather than started late. An agent's second press within 10 s of the one
  that took a request launches nothing (the daemon's guard against two presses for one request).
  A press whose prompt payload cannot be read waits for the next pass rather than launching from
  the card, since it may carry a request with settings of its own.
- **Launch options (beyond agent-ha-bridge, #1142).** `sensor.agent_bridge_<machine>_launch_options`
  is read-only. Its state is a 12-character `version` (a hash of `default_agent` and the lists, so it
  changes exactly when an answer does); its attributes (`json_attributes_topic` `newsession/options`,
  retained) are `{version, default_agent, agents: {<id>: {label, models, efforts, contexts,
  defaults: {model, effort, context}}}}` for every agent the host's launcher offers, whichever
  agent the card has selected, so choosing another agent on the card never changes it. The lists
  are the launcher's own (the card's selectors show only the selected agent's, "Agent default"
  excluded: omit an axis to take it). It is republished on the card's refresh (30 s) when a
  list changes, and again after a pause. When the host does not serve launch it is withdrawn (its
  config and state cleared) rather than left with lists nothing refreshes. It is not in
  agent-ha-bridge's per-machine topic list, so that daemon's Forget does not clear it. `launch-remote.ps1 -Options` prints it and a launch is checked
  against it before the button is pressed.
* **The purple edge**: a session an agent's press launched is `agent`-driven from the moment it
  appears, for the turn it was launched with (already under way or still to come); the turn
  after that is whoever's it is.
* **One at a time.** The launch runs beside the passes (the host follows it to registration); a
  press meanwhile launches nothing, and a request hears `busy`, even when the launch finished
  before the press was read: the card still said `Starting...` when it was made.
* **A restart**, or taking the machine back after agent-ha-bridge had it, reads the outcomes back
  before taking any press: what Home Assistant holds is the newest account, the requests it
  names are not served again, the history agents read carries on, and one left at `starting`
  (other than the connector's own launch still under way) is reported `unconfirmed` rather than
  launched a second time.
* What the button holds when the connector first reads it is not a press, and the note is
  cleared after ten minutes, as the daemon clears it. A note that cannot be shown does not stop
  the launch it describes.

Where stage 5 knowingly differs from the daemon:

| Difference | Why |
|---|---|
| A launched session's folder-trust prompt is never answered from Home Assistant | The host leaves trust to a person at the machine ([host-launch.md](host-launch.md)); the daemon's `trustFolders` is not carried over |
| The transfer request (`sensor.agent_bridge_<slug>_transfer_request`) stays withdrawn | Sharing a session between machines is not a host feature yet |
| Refusals the host makes itself (an unapproved workspace, an agent that is not installed) carry the host's reason, not the daemon's, on the card's path | The host's launcher decides what may start; the connector does not second-guess it |
| Whether the card is served is decided when the connector starts | The host's launcher does not come and go while it runs |
| The note is not cleared when the launched session registers, only when the host says it started (or after ten minutes) | The host reports registration as `Started`; there is no separate signal to wait for |
| Outcomes survive a restart (above); the daemon's start over | Beyond parity: otherwise a payload whose clear was lost could be served twice across a restart |

### Stage 5b: phone notifications

What agent-ha-bridge sends phones through Home Assistant, in its own settings' names under
`homeAssistant` (below). `Fixtures/ha-notify.json` records the daemon's own functions
(`daemon-decision-notify.ps1`, `Send-BridgeNotification`, `Send-BridgeResponseNotification`,
`Format-BridgeNotificationTitle`): the buttons for eight kinds of question, a payload without
styling, the button titles, what each phone is sent and how it is withdrawn, what six taps mean,
the pushes (plain, ticker, short and long responses, an empty one, notifications off) and the
settings' defaults, read from `decision-bridge-common.ps1`. The drift guard re-records it.

* **Answer buttons** (`answerButtons.*`, on by default but sent nowhere until `services` names a
  phone). Once a question is armed on the card and still waiting, it goes to every service in
  `answerButtons.services`, once (`Sync-DaemonAnswerButtons`): the session's name, the question,
  the machine as the subtitle, the dashboard's decision view to open, and the daemon's payload
  (`tag: bridge_decision_<node>`, `group`, `action_data` naming session and question, icon,
  colour and interruption level). Unlike the daemon's, the question is named `<id>~<16 hex>`, the
  hex a digest of what its card shows and takes (its plan), so a tap on a notification of what a
  question said before under the same id, one kept on a phone through a restart among them,
  matches nothing it offers now, and the fixed-length suffix keeps any other question's id from
  spelling it (#220). Its options are buttons (`BRIDGE_<last 12 of that name>_<i>`, up to
  `maxButtons`, titles cut to 30 as the daemon cuts them, `Other (type your answer)` left out),
  with a "Type an answer" text action. A phone that fails is logged and
  the others are still sent; a question no phone took is tried again 15 s later. A question
  answered only at the machine is sent as a notice with no actions; an approval (Codex's, or a
  permission prompt, however the card shows it) is not sent, as the daemon sends none (it has no
  marker). Sent is kept per session and question, so two sessions' questions never share one.
* **A tap** fires `mobile_app_notification_action`, which the connector holds a subscription
  for while it runs and is not paused (agent-ha-bridge's own listener takes them otherwise). No
  notification is sent until Home Assistant has confirmed that subscription (on a start, a
  resume, or after a lost connection), so a tap on one is never fired to nobody. It
  is answered on the next pass through the same delivery as the card: an option's own value
  (`true`/`false` for Yes/No, a one-element list for a "choose any" question, as the daemon's
  `Selections @(answer)`), or typed words as the free-text answer, a lone text or number field's
  value, or through the prompt's own "Other". The card says `Answer sent`, and the next turn is
  `agent`-driven when the tapping account is in `agentUserIds`. A tap for a session that is not
  live, for an earlier question, or after the question was answered, answers nothing, and so does
  an action the notification does not offer (another action ending in a number, or a button past
  `maxButtons`). Each tap is known by its Home Assistant event (`context.id`), which is part of
  the request id the host remembers outcomes by: the same event read twice is one answer, and the
  same button tapped again after a refusal, even after a resume re-arms the question, is tried
  again rather than answered from the remembered refusal.
* **Withdrawn** (`clear_notification` by tag, at every phone that has it) once a tap is
  delivered, when the question is answered anywhere else or replaced, and when the session ends.
  A phone that could not be reached, or that missed the question replacing the one it shows, is
  withdrawn on its own every 15 s (a timeout at one phone is that phone's failure, not the
  pass's); which question each phone shows is kept in the connector's state file, so one answered or ended while
  the host was stopped is withdrawn when it starts. A restart, or taking the machine back from
  agent-ha-bridge, sends a waiting question again, as the daemon does after its own restart; the
  tag replaces what the phone shows.
* **The hooks' pushes** (`notifications.*`, off by default, as in agent-ha-bridge). The daemon's
  hooks sent them; the host owns the hooks on a migrated machine, so the connector sends them
  from what the host sees, worded as each hook words them: Copilot's question (`Copilot:
  <name>`, the session, machine, question, numbered choices and where to answer, capped at 950)
  and permission prompt (`Copilot permission needed`), Claude's question and its blocked
  notification (`Waiting: <name>`), Codex's approval (`Approval needed: <name>`), and a
  finished turn's reply (`Copilot response: <name>` or `Response: <name>`, cut at 880 with
  where to read the rest). A `ticker.*` service gets its own fields. Each question is pushed
  once. A turn is finished when a session that was working or waiting (or has a new turn) is at
  rest with a reply that turn has not been pushed for, and stays at rest for 3 s, as the host's
  own notifier waits (a question answered at the machine passes through idle); a background
  agent's message while the session rests is not one, a reply that changes during the wait
  starts it again, and a later turn ending in the same words is a finished turn of its own. What sessions already show when the connector starts is not pushed again. A push that
  fails is logged and holds nothing up. The reply is the main agent's own: while a delegate
  wrote last, its words neither finish a turn nor appear in the push (design/subagents.md
  §7). A delegate's question is pushed as the session's are, titled "Delegate *name* asks · " before
  the usual title, its text led by "Delegate *name* asks:". Its answer buttons are titled the same
  way, under the session's tag. The hooks' pushes carry no tag at all, the session's own
  questions included, exactly as the daemon's hooks send them (`Fixtures/ha-notify.json`),
  so on that path a delegate's question is treated as the session's are and neither
  replaces the other; the answer buttons are where the newer replaces the older.
  **A turn that ended on an error or a failed sign-in is not a finished turn** (#800; the
  host's Web Push rule, #845). Its session is idle beside the turn before's reply, and pushing
  that reply was the owner's "replies with the last turn as if alive". No finished push goes
  while `attention` is `signInNeeded` or `error` (or a kind not known here). That includes one
  already waiting its 3 s when the attention arrives. The reply is taken as told, so it isn't
  pushed later either. The next turn that does finish is pushed as ever.
* **Needs sign-in** (#800), through the same two channels as a question's Needs you, mirroring
  the host's `signIn` push (§3 of #800's
  [6646501884](https://github.com/danswett/agent-bridge/issues/800#issuecomment-6646501884)):
  * There is **one per machine, agent and account**, never one per session. It goes on the first
    pass after the first of its sessions needs a person to sign in: kind `signInNeeded`, and not
    while recovery is `pending`. The pass runs each second, well inside the owner's 60 s. It goes
    again **at most hourly** while any of its sessions still needs it.
  * It goes to every phone in `answerButtons.services`, with no actions. The tag is
    `bridge_signin_<slug>_<agent>_<first 8 hex of SHA-256(account)>`, with the question's
    icon, colour and interruption level, the subtitle `<machine> - needs sign-in`, and the
    dashboard to open. When `notifications.enabled` is on, it also goes on `notifications.service`.
  * Title: `<Agent> on <machine> needs sign-in` (ui.md §6.13). Message: `2 sessions: <name>, <name>`
    (`1 session: <name>` for one), naming three at most.
  * **No account, code or URL** is in either one.
  * It is withdrawn from the phones (`clear_notification` by its tag) once none of its sessions
    needs a person: signed in, recovered or ended. A later need is news again. Nothing is withdrawn
    while the host's session discovery is held. Retirement waits the same way: after a restart
    under a hold, a session not found yet is missing, not signed in.
  * When it was sent, which destinations have it and which phones hold it are kept in the
    connector's state file. A restart within the hour therefore doesn't send it again, and still
    withdraws it. A destination that missed it (a phone or the push service failing, or a pass
    stopped part-way) is tried again every 15 s. Each destination is told again an hour after its
    own delivery, never sooner, and a failing service is logged once until it works. Once the need
    is over, what was delivered is forgotten at once, even while a withdrawal is still being
    retried, so a need that comes back is told everywhere again.
  * An error and Not responding send nothing. They show on the status only, as on the clients.

Where stage 5b knowingly differs from the daemon:

| Difference | Why |
|---|---|
| "Type an answer" is left off where the host could not deliver the words: a lone choice whose prompt has no "Other", a Yes/No, a list with no options, a form | A button that silently does nothing is worse than none; the daemon offers it for anything but a form |
| The hooks' pushes are made from the host's view of the session, not from inside each hook | The host's hooks report to the host; Copilot's permission push carries the host's prompt text, where the hook had its own title |
| Until migrate re-points the hooks, a machine whose agent-ha-bridge hooks still run may push a question twice | Both would be sending; `migrate` moves the hooks to the host, and nothing runs agent-ha-bridge's hooks after it |
| The host's own Web Push (`push.*`) notifies the app's phones as well | A different channel, with its own settings; turn one off if both reach the same phone |
| A tap must name an action its notification offered; a withdrawal that failed is retried, across a restart too | Beyond parity: the daemon reads the index off any action's suffix, and forgets a withdrawal whether or not it arrived |
| A session that needs sign-in, or whose turn ended on an error, gets no finished push. Sign-in gets a notice of its own (#800) | The daemon has no attention, and the agent's stop hook pushed the turn before's reply as if the session were alive |
### Stage 6: update buttons

The machine row's update entity, and its **Install Bridge Update** and **Check for Bridge
Update** buttons, as `Sync-DaemonUpdateStatus` serves them for agent-ha-bridge's own install.
`Fixtures/ha-update.json` records `Publish-CopilotMqttUpdate` at six moments (a release found,
with notes past the 2000-character cap; checking; downloading at 42%; a release that could not
be established; a check that found nothing newer; completed) and the busy notice
(`Send-DaemonUpdateBusyNotice`); the drift guard re-records it.

What is checked and installed is **this host's own product** (design G2): the app's package,
which carries the host (`IHostUpdates`, Velopack on Windows, [app-windows.md](app-windows.md)),
with the app's feed (GitHub releases of `danswett/agent-bridge`, or the app's `feed.txt`) and
its "Include pre-releases" setting. `latest_version` on a migrated machine is therefore a
Slipgates version, never agent-ha-bridge's. A copy its updater did not install (a build
folder, or a platform it does not package yet) serves no buttons and reports its own version as
current, as stage 1 did; so does a host with `homeAssistant.updates.checkForUpdates` off, which
looks nothing up, and a read-only host (`delivery.readOnly`), which acts on nothing from another
device.

* **Checks.** At start, every `homeAssistant.updates.checkHours` (0.5, as the daemon's
  `updates.checkHours`; at least 0.1), and on a press of Check, beside the passes so a slow feed
  holds up no card. Until the first answer the entity reports the version installed as current
  (an entity with no `latest_version` reads unavailable, and the machine row with it); after it,
  only a check changes what it says, a resume included. A newer
  release puts its version, notes (capped at 2000) and the releases page on the entity, and
  Home Assistant shows an update. A check that cannot reach the feed leaves `latest_version`
  out, with `The latest release could not be established: <why>` in the notes, and is tried
  again after five minutes: not `failed`, because a check that could not be made is not a
  failed update (agent-ha-bridge #92). So does a check that has not answered within two
  minutes or whose request timed out, and a configured `feed.txt` that cannot be read:
  it is never taken for GitHub, which would be another channel. A press of Check that finds
  nothing newer says `current`, `Checked just now; <version> is the latest release.` for ten
  minutes, until a check finds a newer one. A press while a check is already under way gets a
  check of its own once that one is done, so "just now" is never an earlier check's answer.
* **Install.** A press shows `checking` at once (`in_progress`, the spinner), then looks for the
  newest release again, downloads it (`downloading` with `update_percentage`), records the
  attempt in the connector's state file (an attempt that cannot be recorded is not handed
  over: the host that came back could not say how it ended), shows `restarting` and hands the
  release to Velopack. Before that, still showing `downloading`, it makes sure nothing the update
  ends is mid-refresh (#238): this host's own usage probes are held and a standalone Codex
  app-server it started is waited for, at most 11 minutes; another profile's host running from the
  package refuses the install, since its probes cannot be held from this host (its pipe takes
  management only from the app; install from the app there, which holds them all), asked again
  once this host's wait is over. When it cannot confirm, the
  install fails to start and `stage_detail` and the notification say why. The release goes to
  Velopack,
  which applies it once the host has exited; the host then stops as on Ctrl+C. Velopack
  force-stops everything else running from the package folder (the app, and every other
  profile's host, with their sessions' delivery), starts the app again in the background for the
  host's profile, and the app starts the host. A press made with an agent's account
  (`agentUserIds`) is refused with **Bridge update not started on `<machine>`**: a person must
  press it (below). While an
  update runs a press of Check looks nothing up: the install found its release itself. A press with nothing newer says
  `current` and notifies **No newer bridge release on `<machine>`**; one whose download or
  hand-over fails says `failed` with the reason, notifies **Bridge update failed on
  `<machine>`**, and leaves Install usable. A press while an update runs, or while the host waits
  to exit into one, is not acted on and gets the daemon's **Bridge update already running on
  `<machine>`** notice. A notice Home Assistant does not take is tried again every 15 s until it
  does or a newer one replaces it; notices go one at a time, newest first, so an older one never
  lands after a newer one. What is downloaded and applied is exactly the release the
  install's own check found, from the feed that found it. A host stopped part-way through leaves the last stage retained, as the
  daemon does, and the next start republishes the entity without it.
* **The outcome.** The host that comes back reads the attempt and says how it ended, once: on
  the new version, `completed` (`Updated to <version>`) for an hour and **Bridge installer
  completed on `<machine>`**; on the old one, `failed` and **Bridge update failed on
  `<machine>`**. An attempt older than six hours is dropped unannounced, as the daemon drops an
  old outcome file. The host that recorded it never announces it: it is still on the old version
  for the moment before it exits. The outcome answers the press, not a check, so it is told
  even when checks were turned off meanwhile, and a notification Home Assistant refuses is tried
  again every 15 s without holding up a new press. It is older news than any press this host has
  answered: its verdict is not laid over a press's answer, and its notice is not sent once a
  press's notice has been (they share one notification, which would then say the older). Every
  notice is a `persistent_notification` with
  `notification_id: agent_bridge_update_<slug>`, so each replaces the last.
* **Presses** are read from the buttons' states (press timestamps), on a watch hit or every
  five seconds; a press from before the connector started is history, not an instruction
  (`Test-DaemonUpdateCheckPress`), so one made moments after a restart still counts.

What the tests cannot show is the hand-over itself: the unit tests and the `ha-integration` job
use a stand-in updater, so Update.exe stopping the host and the rest of the package, replacing it
and restarting the app for the pressing profile, and that host then telling the outcome, are
checked on an installed machine by the rehearsal's step E18-06
([parity-checklist.md](parity-checklist.md)), from a local feed one version up.

Where stage 6 knowingly differs from the daemon:

| Difference | Why |
|---|---|
| No check is forced because another machine's entity reports a newer `latest_version` (`Test-DaemonPeerUpdateNotice`) | Machines on agent-ha-bridge report agent-ha-bridge versions on the same entity; comparing across products would be wrong both ways. A daemon reading a host's lower version does nothing with it |
| The outcome is the host seeing its own version after the restart, not an updater's outcome file | Velopack applies the update out of process and leaves no outcome; the version the host comes back on is the evidence. Its notice says the host restarted on it, which the daemon could not observe |
| Install checks the feed itself rather than an `agent-ha-bridge update` child, and refuses a second press from the host's own state | One host per machine and user (its mutex), so the host knows whether it is updating; there is no lock file to share |
| An agent may not press Install: a press made with an account in `agentUserIds` starts nothing and gets **Bridge update not started on `<machine>`**, saying a person must press it. Check is still an agent's to press | Design §6.3 invariant 13: no agent stops or restarts its host. An update does both, and Velopack stops every profile's host running from the package folder while it applies it. The daemon (`Sync-DaemonUpdateStatus`) does not ask who pressed |
| A read-only host (`delivery.readOnly`) serves neither button, as a copy its updater did not install | It acts on nothing from another device, and an update restarts it |

### Stage 7: upkeep, and forgetting a machine

The daemon does more than publish: it keeps a machine's entities working when Home Assistant
loses them. Stage 7 ports that upkeep, recorded from the daemon's own functions in
`Fixtures/ha-upkeep.json`. That covers the Detailed activity switch provisioning, the liveness
judgement replayed beat by beat over scripted reads, the rebuild's messages, and the timings.

* **The Detailed activity switch** (`Initialize-CopilotVerboseToggle`). At each start the
  connector lists Home Assistant's `input_boolean` helpers, and creates
  `input_boolean.agent_bridge_<slug>_detailed_activity` only when there is none. It uses the
  daemon's name (`Agent Bridge <slug with spaces> Detailed Activity`, from which Home Assistant
  makes the id) and icon (`mdi:brain`), then names it `Detailed activity (<machine>)` through
  the registry.
  * It never deletes and creates again a switch Home Assistant holds: Home Assistant keeps the
    value across a restart, and a new switch starts off.
  * If a list came back empty while Home Assistant was starting, Home Assistant numbers the new
    switch beside the old one (`…_2`). That numbered copy, which was just made, is deleted, and
    the original and its value are kept.
  * A create that comes back under any other id is left alone and logged.
  * A list that fails means nothing is created: the machine keeps the
    `homeAssistant.detailedActivity` fallback and draws no Detail switch.
  * From now on `capabilities.detailed` is true for a machine on the host, as it is on the
    daemon.
* **The liveness sensor's repair** (`Confirm-DaemonOnlineHeartbeat`, agent-ha-bridge #177 and
  #202). `mqtt.publish` answers 200 even for a topic Home Assistant has stopped applying, and
  such an entity runs no expiry timer. So after each beat the connector reads
  `binary_sensor.agent_bridge_<slug>_online` and compares its `last_reported`, which the beat's
  changing time moves (`HaLiveness`).
  * **When it is rebuilt.** Only when two things both hold: the sensor has taken no beat for
    180 s, and it has missed 3 beats.
  * **How.** The connector publishes an empty retained config, waits 1.5 s, publishes the config
    again, pins the id, waits up to 12 × 250 ms for the entity, and only then beats. A rebuild
    whose declaration failed after the retraction is declared again on the next beat, whatever
    the count, so a publish that failed once cannot leave the machine without its sensor.
  * **When it stops.** After 3 rebuilds in a row it stops and says once which topic nothing
    reaches. Only two beats landing in a row reset that count; the one beat a rebuild sends
    itself does not.
  * **Reads that decide nothing.** A read that fails starts and stops no clock. A sensor that
    does not report `last_reported` is never rebuilt.
  * **When agent-ha-bridge has had the machine.** The count is kept across a pause. The daemon
    having had the machine is no sign the sensor was mended, and starting again on every
    hand-back would tear a sensor that never takes a beat down three more times each time. Two
    beats landing in a row still reset it, whoever sent them.
* **A live session's entities** (`Repair-CopilotSessionEntities`,
  `Restore-DaemonSessionEntities`). On every reconcile the connector checks each session it has
  published for at least 60 s (sooner, its entities may not have registered yet). It reads the
  session's reply box, or its status sensor when this host serves no answers.
  * **Not there** (a 404) for at least 60 s while the session runs here: the whole set is
    published again, its status and activity from what the session holds. A shorter absence is
    left alone: a Home Assistant that is still starting answers 404 until MQTT has loaded the
    entities again, and republishing then would reset a question somebody may be answering. A question on the card goes
    on screen again. This is said the first time and every tenth attempt.
  * **Read failed:** nothing happens. A republish parks the selector on Idle, which would blank a
    question somebody is answering.
  * **Reply box or selector reads `unknown`:** these have no state topic, so a Home Assistant
    restart can bring them back with no value. `unavailable` is not `unknown`: it is Home
    Assistant not answering for the entity yet, and the entity comes back holding its value, so
    nothing is repaired on it.
    * Only the box lost its value: it gets `' '` again, and nothing else is touched. The
      selector and the choices kept their values and their options, so the question is still on
      screen, and arming it again would only reset choices somebody may be making.
    * The selector lost its value: words in the box are somebody's and are left (they keep the
      baseline they had, so a form's text field still takes them at Send), and the question goes
      on screen again. When there is no question, the selector is parked on Idle; under a
      question already answered, it is put back on `Awaiting answer...` and nothing is armed.
    * Before the question is armed again, the card is read once with the baselines it was armed
      with, because the choices may have come through and an answer given on them since is still
      an answer. It is delivered rather than taken for the new baseline, and a question answered
      that way is not put on screen again. What the card's channels held (Send, the reply
      payload, the box, the selector) is noted just before that read, and the new baselines are
      that. Anything given on the card after it, a Send, a tap, words or a publish, holds the
      re-arm off until the next read takes it.
    * Only a read that could see every control counts: one that could not read Send, say, or
      found the selector or a choice `unavailable`, cannot tell an answer from none, so the card
      is read again on the next pass before anything is re-armed. A Send made while a choice
      reads `unavailable` is not refused as incomplete either; it waits for a read that can see
      the choice.
    * Arming again keeps what came through on the card: a choice slot holding a choice of this
      question (its options are this question's, as they were), and a selector holding a tap
      under it not yet used. This is decided as each would be reset, after the arm's wait for
      the slots to register, so a choice made during that wait is kept too. A slot still showing
      the last question's choice is reset as usual. Anything given while the arm runs (a Send,
      words, a publish, a tap) stops it before new baselines are taken, the event boundary moves
      or the selector is set waiting, and the next pass reads it with the old baselines.
  * In both cases the question keeps what it has been through: answered, its attempts, what was
    used. An answer that may have reached the terminal is never typed again because the card was
    put back (`An_answer_that_may_have_landed_is_not_given_again_once_the_card_is_put_back`). An
    approval's tap that reached nothing may be tapped again once the card waits again, as after
    any failure.
  * A selector removed on its own, with the reply box still there, is declared again alone and
    pinned, after the same 60 s of absence. The rest of the card is left as it is, choices
    included (published again, its choice slots would get the first publication's options), and
    the question is armed again as above, read first.

**Forgetting a machine.** The machine row's X is the generated status card's own: two taps on
the row of a machine that is offline and not part-way through an update. Those taps publish an
empty retained message from the person's browser to each topic of
`Get-BridgeMachineForgetTopic`, which the writer lists for that row: the machine's own topics,
and every session its retained session list names. The connector therefore deletes nothing when
a machine is forgotten, and has no forget command an agent, a press or a payload could reach:

* `mqtt.publish` is administrator-only. The agent's own token (`agentTokenEnvVar`) belongs to
  an account that is not an administrator, which is why agent-ha-bridge sends the one
  administrator-only publish agents need (a long launch prompt) through the user's token. An
  agent that has been handed an administrator's token is beyond what any bridge can stop.
* The X is never offered for a machine whose host is running: the beat keeps it online, and
  stage 6 keeps an update's stage on the entity through its restart.
* What the X removes is only this machine's own:
  `Everything_this_connector_keeps_in_Home_Assistant_is_on_the_list_the_dashboards_forget_clears`
  runs a connector with answers, End session, Launch, updates, two Copilot logins' usage, a
  question and a retired session. It checks that every retained topic it leaves is on that list,
  and that pressing the X then leaves nothing of the machine in the broker or the registry.
  The one exception is the Detailed activity switch, which is Home Assistant's helper and not
  MQTT's; the daemon's X leaves it too.
* An ended session still waiting for retirement is not on the machine's retained list (listed,
  the writer would draw it as a live card, and its retirement, which waits until no dashboard
  draws it, would never come). So a host stopped with one waiting and then forgotten leaves that
  session's entities behind, as the daemon does. The next bridge to start anywhere, host or
  daemon, finds them listed by no machine and retires them through the orphan sweep
  (`A_session_waiting_for_retirement_when_its_forgotten_host_stopped_is_retired_by_the_next_bridge_to_start`).
  Clearing them with the X itself needs the writer to read such sessions from an attribute of
  their own, which is agent-ha-bridge's to add.

A machine is forgotten because it is gone: renamed, reimaged or thrown away. Two cases are not:

* **A host that was only off.** Its row comes back when the host starts, which is what the card
  says of the daemon ("the cost of a mistake is a row that returns").
* **A host that was running when its row was forgotten.** That can happen only after its liveness
  had lapsed for three minutes. The connector finds its online sensor gone for 180 s and three
  beats, and publishes the whole machine again, as a start does. It is not a restart, though: its
  sessions keep what they know (who drives each, a thought on the card, a question's history),
  and only the cards that went with it are published again. A card still in Home Assistant (only
  the liveness sensor's config went) is left as it is, choices and words included: published
  again, its selectors would get the first publication's options. What Launch was last read as
  is kept, so a press made just then still launches. The daemon here rebuilds only the sensor, and the rest comes back piecemeal.

Where stage 7 knowingly differs from the daemon:

| Difference | Why |
|---|---|
| The daemon's one-time move off the pre-1.2x shared switch `input_boolean.agent_bridge_detailed_activity`, which deletes that switch, is not ported | Every machine of the fleet shared it, and a machine comes to this host from agent-ha-bridge 1.33, which made the move long since. The connector deletes no helper but a numbered copy of its own switch that it has just made |
| A create that comes back as anything but the switch's id or a numbered copy of it is left alone; the daemon deletes whatever came back | Only what the connector can show is its own is ever deleted |
| An online sensor that is absent, rather than present and silent, is answered by publishing the whole machine again, not only the sensor | It is absent because its config went, most likely to the X pressed while the machine's liveness had lapsed, which removed every entity of the machine; a running machine was not gone. Bounded by the same three attempts. A session whose card is still there is left as it is |
| The rebuild count is kept when the connector resumes after agent-ha-bridge had the machine; a daemon starting has a count of its own | A hand-back is no sign the sensor was mended, and a fresh count on each would undo the limit (#202). Two beats landing in a row still reset it |
| The session check waits until 60 s after a session's entities were published, and republishes only an absence that has lasted 60 s | Before then a missing entity may only be one Home Assistant has not registered yet, or has not loaded again while it starts; the daemon publishes and pins in one step and republishes on the first 404 |
| The reply box is primed, and put back to `' '` after a restart, only when it holds no value; the daemon blanks it at every session add and every repair | Words in the box are somebody's reply, not yet sent. A machine published again around a card that stayed, or a selector that lost its value on its own, leaves them |
| A reply box or selector that reads `unavailable` is not repaired; the daemon repairs `unavailable` as it repairs `unknown` | `unavailable` is Home Assistant still starting, or an availability lapse, not a lost value. Repairing it would re-arm a question over a choice being made and blank words not yet sent, for an entity that comes back holding both |
| After a restart, a question is armed again only when its selector lost its value, never for the reply box alone, and an answered question's selector is put back on `Awaiting answer...` without arming; a choice slot still holding this question's choice keeps it. The daemon re-arms whenever either lost its value (`Restore-DaemonSessionQuestion`) | Arming resets the choices: re-arming a question still on screen would only reset choices somebody may be making, and lose a Send made while it ran |
| A selector removed on its own is declared again alone; the daemon publishes the session's whole set again | The rest of the card is still there: published again, its choice slots would get the first publication's options, and a choice made on them would read as none |
| A switch present but with no state yet is not waited for; it is named and left | The daemon only warns. There is nothing to do until Home Assistant finishes starting, and waiting would hold every start for four seconds |
### The shadow: comparing with a running daemon

`agent-bridge-host ha shadow [--json] [--window <seconds>]` runs one connector pass against a
Home Assistant that **forwards reads and records writes without sending any** (`HaShadow`), and
reads or answers no press, then
reads the broker's retained topics for this machine and for every session either side names
(the daemon's from its `global/attr`), and compares them as JSON, ignoring `updated` and
`asked_at`. It takes one reading of every agent's usage first, as `agent-bridge-host usage`
does, so the usage sensors are compared too. Each topic is reported `same`, `differs`,
`agent-ha-bridge only` or `host only`,
with a note where the difference is by design (the launch card, whose choices are the launcher's, which a shadow does not ask for; the update buttons, as a shadow looks for no release; the transfer controls; the answer and
stop controls when the host cannot use them; usage when no reading was given). It needs `baseUrl` and the stored token but not
`enabled`, and it ignores G5: it is the one thing that is meant to run while the daemon does,
for the week of shadow running before a machine's cutover (§10 stage 4). It complements
`agent-bridge-host shadow`, which compares what the host *knows* with the daemon's local state
files; `ha shadow` compares what each would *publish* to Home Assistant.

## Lifecycle

**Start**, and again after a pause:

1. Declare the online sensor and pin it; the first beat follows two seconds later.
2. Create the Detailed activity switch if Home Assistant has none (stage 7), then read it
   (`on`, `off`, or absent: the setting `detailedActivity`).
3. Withdraw this machine's unserved controls; publish the update entity (and, on a host its
   updater installed, the Install and Check buttons) and pin them; announce how an update the
   host restarted for ended, if it did (stage 6).
4. Work out what this machine published before and is not running now: the nodes in its
   retained session list (whichever bridge wrote it), the nodes the connector remembered as
   waiting for retirement or as part-way through their first publication (`ha-connector.json`
   in the profile's folder, written before that first write), and the orphan sweep
   (`Clear-CopilotMqttOrphans`): session entities no machine lists and that are not running
   here. The sweep is skipped, not guessed, when no machine's session list is visible (Home
   Assistant still starting). All of these join the retirement queue; none is cleared yet.
5. Publish every live session (availability, the two sensor configs, withdrawals, status,
   activity), then the session list.

**Steady state**, one pass a second: changed sessions; the session list on a change or every
300 s; the beat every 60 s, each followed by the liveness check (stage 7); usage on a new
reading; pending pins; and every 15 s (the daemon's reconcile) the Detailed activity switch, the
check of each live session's entities (stage 7) and the retirement queue. A failing call is
retried on the next pass and logged once per kind of failure until it clears; a refused token
or a banned address is not ([Troubleshooting](#troubleshooting)).

**A session ending** leaves the session list at once, so the writer regenerates the dashboard
without its card. Its entities are retired only when **the dashboard served to browsers no
longer renders it**: the connector reads `lovelace/config` for `homeAssistant.dashboardUrlPath`
every 15 s, treats any occurrence of the node in that JSON as "still rendered" (stricter than
rendered, so it errs the safe way, and needing no trust in who wrote it), and retires one pass
after the node is first seen absent, because a browser keeps the config it has until Home
Assistant pushes the new one (`Update-DaemonRetireQueue`). No dashboard at that path is an
answer (nothing renders it); a dashboard that cannot be read holds every retirement. Retiring
publishes exactly the daemon's 29 messages (`Remove-CopilotMqttSession`): `offline` on the
availability topic, then an empty retained payload on all 19 discovery topics and all 9 state
topics, so no entity and no retained value is left behind. A session that comes back before it
is retired leaves the queue.

**Nothing is retired while the host's session discovery is held** ([v1-sessions.md](protocol/v1-sessions.md#discovery-held)),
whichever agent the hold is for: a session missing from a held pass may be one discovery could
not see. A host restarted under a hold once retired the entities of a Codex session that ran on
(#147). The queue waits, logged once ("retires nothing while session discovery is held"), and
carries on once a complete pass has removed what really ended. A session the host lists is never
retired, even before this connector has published it again. Both are checked again just before
each session's entities go, since a hold can begin, or the session be found again, while the
dashboard is read.

**A host restart** re-runs the start: live sessions are republished onto their existing,
pinned entities (no new ones), and the remembered queue is carried over. A session the host's
discovery was keeping only because it was held is still listed after the restart, so it is
republished rather than queued (#147). **A Home Assistant
restart** loses nothing retained; the 300 s re-assert of the session list covers a broker that
lost retained state. The reply boxes and selectors come back with no value, and the session
check puts them back, a waiting question armed again. A liveness sensor Home Assistant has
stopped listening to is rebuilt (#177, #202). Both are stage 7.

**A pause** (agent-ha-bridge started for this user) stops every write: the write that finds it
is not made, nor anything after it in that pass. Within the probe's five-second cache, one
request already on the wire is the most that can still land. The next pass after it stops
re-runs the start.

One connector per machine and user: `Local\AgentBridgeHaConnector_<slug>` is claimed at start,
so a second profile cannot publish the same machine's entities.

## Migration and rollback of a machine

Both bridges publish the same unique ids on the same topics, so moving a machine in either
direction replaces entities rather than adding any.

**agent-ha-bridge → host** (G8's *Migrate*, the app's job in stage 2; by hand in a rehearsal):

1. Stop agent-ha-bridge for the user and disable, not delete, its scheduled task. G5 holds the
   connector paused until the daemon, its supervisor and the task are all gone.
2. The connector starts: the online, sessions and update entities keep their pinned ids (same
   unique ids, already registered); every running session's sensors are republished in place;
   the daemon's controls the stage does not serve are withdrawn; the sessions the daemon's list
   still names but that are not running, and orphans the daemon never swept, are retired once
   their cards have gone. The `Detailed activity` helper is kept as it is (it is Home
   Assistant's, not MQTT's, and its value is the person's) and read; a machine without one gets
   one, as the daemon would have made it.
3. Nothing in `~/.agent-ha-bridge` changes, and agent hook registrations are not touched (G4).
   Until *Migrate* re-points them, agent-ha-bridge's hooks may still publish a session's
   question entities on demand; stage 2 owns those entities and replaces them, and the rehearsal
   checklist looks for this.

**host → agent-ha-bridge** (*Roll back*):

1. Stop the host (it publishes nothing on the way out) or turn the connector off.
2. Re-enable agent-ha-bridge's task. Its daemon adopts each running session (republishing the
   full entity set when the question selector is missing, which is exactly what stage 1
   withdrew), republishes its launch, install and update entities, re-pins ids, and its own
   orphan sweep retires whatever ended in between, since the host's last session list claims no
   node for another machine.

In both directions the checks are the same, and are part of the rehearsal: no entity id with a
`_2` suffix in the registry; no `agent_bridge_<slug>_*` or session entity left `unavailable`
after its card has gone; retained topics under `copilot/cli/machine/<slug>/` and `copilot/cli/
<node>/` only for what is running.

## Credentials, agent attribution and notifications

**The token.** The connector uses a Home Assistant long-lived access token of an
**administrator** (as the daemon's `homeAssistant.token` is: `mqtt.publish` and the entity
registry are administrator-only). It is given at the machine with `agent-bridge-host ha token
set`, read from standard input (never an argument, which would sit in shell history and the
process list; not echoed at a console; refused inside an agent session, as `ha token clear` is,
[host-config.md](host-config.md#approving-a-workspace-at-the-machine)), and kept the way the device key is: DPAPI to the Windows
user in `ha-token.dpapi` beside it; elsewhere an owner-only file until the Keychain arrives
(P5). It is never in `config.json`, never logged; failures name the HTTP status and never echo a
body. `ha status` says whether it is stored, never what it is.

**Agent attribution.** Two tokens matter in agent-ha-bridge: the user's, which the daemon
publishes with, and the agent's (`homeAssistant.agentTokenEnvVar`, default
`AGENT_HA_AGENT_TOKEN`), which agents use when *they* press, reply or launch. The daemon marks a
card agent-driven (the purple edge) when the `context.user_id` on the state change that carried
the input is in `homeAssistant.agentUserIds`. The connector keeps that split: it publishes with
the user's token only, reads `context.user_id` off each command's state change and matches it
against its own `homeAssistant.agentUserIds` to set `driver` (stage 2).
`migrate` imports `agentTokenEnvVar` as metadata only; the native launcher does not
resolve that name or inject an HA token. A child may inherit its host's environment,
but that is not managed token propagation or the proposed native agent credential.
See [host-launch.md](host-launch.md) and [host-config.md](host-config.md).
The target uses separately reviewed Bridge identities/attribution, not permanent
per-host HA agent tokens; this does not change current command authorization.

**Notifications.** The phone flow (`notify.mobile_app_*` with answer buttons, and
`mobile_app_notification_action` events back) is stage 5b (above). A tap is attributed as a
press is: the account Home Assistant records on the event, matched against `agentUserIds`. The
services are named in `homeAssistant.answerButtons.services` and `homeAssistant.notifications.service`;
they call nothing but notify-style services, and name no entity. The host's own Web Push is a
separate channel with its own settings.

## Command handling: the plan for later stages

Each stage is one PR, contract-tested against fixtures recorded from agent-ha-bridge the same
way, and validated on the disposable Home Assistant. Everything that types into a session goes
through the services the relay path already uses (`AnswerService`, `ReplyService`,
`StopService`, `LaunchService`), so G5, the delivery gate and the attempt ledger apply
unchanged. Commands arrive as state changes the connector observes over one held WebSocket
(`subscribe_trigger` on the session's optimistic entities, as `Wait-CopilotHaStateChange`
does), never by subscribing to the broker.

| Stage | Commands | agent-ha-bridge spec |
|---|---|---|
| 2 Answers (done) | The full per-session set but stop; arm, read, answer, tell the card, clear; `driver` from `context.user_id`; the read-only shadow; the container CI job (above and below) | `Set-CopilotMqttDecision`, `Publish-CopilotMqttDecisionFields`, `Clear-CopilotMqttDecision`, `daemon-decisions.ps1`, card `answer_consumed_at` |
| 3 Replies and attachments (done) | The reply card's publish (text, image ids, inline files) and the reply box with Send; images from Home Assistant's image store, deleted after; the files through `AttachmentStore` and `ReplyService`; driver pending until the turn starts (above) | `daemon-replies.ps1`, card reply path |
| 4 Stop (done) | `button.<node>_stop`, two presses when the session is not idle, the confirm and lapsed notes pinned on the activity line; `StopService`; `ending` (above) | `Invoke-PendingStops`, `Get-DaemonCardSummary`, `CopilotEndSessionConfirmNote` |
| 5 Launch (done) | The launch card's controls (`Publish-CopilotMqttNewSession`) from the host's approved workspaces and agents; the 255-character limit lifted through the retained `newsession/promptpayload` (`{at, text, request, settings}`); `new_session_result` with `recent` per request (its attributes, on the retained `newsession/outcome`); `capabilities.newSession`/`launchRequest`; the purple edge for a session an agent launched (above). The transfer request stays withdrawn | `daemon-launch.ps1`, `Publish-DaemonNewSessionControls`, `Resolve-DaemonRequestedLaunch` |
| 5b Phone notifications (done) | `notify.mobile_app_*` with the question's answer buttons, and the `mobile_app_notification_action` events back through `AnswerService`; the response notification when a turn ends | `daemon-decision-notify.ps1`, `Send-BridgeResponseNotification` |
| 6 Update buttons (done) | `button.agent_bridge_<slug>_check_updates` forces a fresh release check, and `button.agent_bridge_<slug>_install_update` starts the product's own update (the app's Velopack update on Windows), with `stage`/`stage_detail` and `update_percentage` from it, and `latest_version` from this product's release feed — never agent-ha-bridge's (G2) | `Publish-CopilotMqttUpdate`, `daemon-maintenance.ps1` |
| 7 Upkeep (done) | The Detailed activity helper created when absent (`input_boolean/create`, never recreated); the liveness repair for a sensor Home Assistant has stopped applying, with its attempt limit (#177, #202); a live session's entities put back, and its reply box and question after a Home Assistant restart; forget-machine: the card's X clears this machine's topics from the browser, the connector's own topics are all on its list, and the connector itself deletes nothing (a machine forgotten while its host runs is published again, one forgotten while it was off comes back when it starts, as with the daemon) (above) | `Initialize-CopilotVerboseToggle`, `Confirm-DaemonOnlineHeartbeat`, `Repair-CopilotSessionEntities`, `Restore-DaemonSessionEntities`, `Get-BridgeMachineForgetTopic` |

## Validation: a disposable Home Assistant and Mosquitto

Never the live Home Assistant or broker.

### In CI, on every change

The `ha-integration` job in `.github/workflows/ci.yml` is the disposable Home Assistant every
connector change is checked against. On an Ubuntu runner it starts Mosquitto
(`eclipse-mosquitto:2.0`, no authentication, reachable only on a private Docker network) and
Home Assistant (a pinned release, published only on `127.0.0.1:8123`), both empty;
`tools/ha-ci/onboard.py` onboards Home Assistant (owner, core config, analytics, integrations),
adds the MQTT integration through its config flow, waits for it to load and hands the test a
token through the job's environment, masked. The script refuses any address that is not
loopback. Both containers go with the runner.

The tests (`LiveHomeAssistantTests`, trait `Category=HomeAssistantIntegration`) are skipped
everywhere else: they run only when `AGENT_BRIDGE_HA_TEST_URL` is loopback and
`AGENT_BRIDGE_HA_TEST_DISPOSABLE=1`. Each poses as a machine of its own and checks, through Home
Assistant itself rather than a fake:

* the card's entities exist at the ids the dashboard uses, End session among them, a question is
  armed onto them, and the machine reads online with its session count;
* End session pressed through Home Assistant on a working session asks for a second press, and
  the second, within the window, reaches the host's stop with the first press's token and the
  card's status reads `ended`;
* a choice and a Send press made through Home Assistant's services reach the host's answerer
  with the chosen value, the card says `Answer sent`, and the next turn is `agent`-driven
  because the pressing account is in `agentUserIds`;
* a reply card payload published through `mqtt.publish` answers a free-text question and
  `answer_consumed_at` comes back;
* words typed into the reply box and a Send press reach the host's reply delivery, the card says
  `Reply sent` and the box is reset to `' '`;
* an image uploaded to Home Assistant's image store (`/api/image/upload`, as the card does) and
  a file, sent in one reply-card publish that is not retained, reach the host's reply delivery
  byte for byte, and the image is then deleted from the store;
* the launch card's selectors reach the machine's defaults; a workspace chosen and words typed
  through Home Assistant's services and a Launch press reach the host's launcher, and the box is
  reset to `' '`; an agent's request published to the prompt payload and a press reach it with
  the request's own settings, and `new_session_result`'s `recent` says it `started`;
* a question goes to `notify.persistent_notification` (standing in for a phone) with its buttons,
  and the hook's push beside it; a tap fired as `mobile_app_notification_action` through Home
  Assistant's events API answers it with the tapped option, the notification is withdrawn, and
  the reply of the turn it started is pushed, `agent`-driven because the account that fired the
  tap is in `agentUserIds`;
* the update entity Home Assistant builds from the state shows the release found and the
  installed version; a press of Check asks the feed again; a press of Install shows
  `downloading` at 42% with the spinner, a second press gets the busy notice, and the release
  reaches the updater with the entity at `restarting`;
* the machine's Detailed activity switch is created off and named for its machine, and a second
  start, after it was switched on through Home Assistant, keeps that switch and its value;
* a machine forgotten as the row's X forgets it (an empty retained message on every topic of
  `Get-BridgeMachineForgetTopic`) while its host runs, with the beat shortened, is published
  again whole: online, sessions, update and its session's card, showing what the session does;
* only the machine's liveness sensor's config removed, with a choice made on a waiting question:
  the machine is published again around the card, the choice is still there, and a Send press
  reaches the host's answerer with it;
* Home Assistant restarted through `homeassistant.restart` while a question waits on a card: once
  it is back, the reply box holds a value again, the question is armed on the card again, a
  choice and a Send press reach the host's answerer, and the session's entity ids are exactly
  those it had before (nothing made again under a new id);
* with no dashboard at all, an ended session is retired, and nothing it published is left
  retained;
* the shadow, against the daemon's own recorded publications seeded into the broker, reports
  what it should and leaves the broker exactly as it found it.

To bump the Home Assistant release, change `HA_IMAGE` in the job.

### By hand, with a copy of the dashboard

What CI cannot show is the generated dashboard and card drawing a migrated machine, because they
are agent-ha-bridge's output. That needs the rehearsal below, on the Hyperloop VM (or a
developer machine with Docker), with its own instance, broker and dashboard.

### 1. The instance

```yaml
# docker-compose.yml, on the VM
services:
  mosquitto:
    image: eclipse-mosquitto:2.0
    ports: ["127.0.0.1:1883:1883"]
    volumes: ["./mosquitto:/mosquitto/config"]   # mosquitto.conf: listener 1883, password_file
  homeassistant:
    image: ghcr.io/home-assistant/home-assistant:<the live instance's version>
    ports: ["127.0.0.1:8123:8123"]
    volumes: ["./ha:/config"]
    depends_on: [mosquitto]
```

Then, scripted with `curl` against `http://127.0.0.1:8123`: onboarding
(`POST /api/onboarding/users` for an owner, `/auth/token` for an access token, the remaining
onboarding steps); a long-lived token over the WebSocket (`auth/long_lived_access_token`); a
second, non-administrator user for agent attribution later; and the MQTT integration through
its config flow (`POST /api/config/config_entries/flow` with `{"handler":"mqtt"}`, then the
broker's host, port and credentials). The tokens are written to files on the VM and never
printed.

### 2. A copy of the generated dashboard and card

The dashboard and card are agent-ha-bridge's output, so agent-ha-bridge generates the copy,
into the disposable instance:

* Install agent-ha-bridge (the release the live machines run) on the VM as a first Windows user,
  pointed at the disposable instance with its token, and make it that instance's **publication
  writer** with an explicit bootstrap there (its README's operator procedure). It serves the
  card resource and generates `/agent-decisions` exactly as DSWETT-HOME does for the live one.
  This touches nothing outside the VM: different instance, broker, policy and fences.
* A second Windows user on the same VM is the machine being migrated. It first runs
  agent-ha-bridge as a non-writer participant, then migrates to `agent-bridge-host` with the
  connector (`homeAssistant.enabled`, `baseUrl`, `disposable: true`; `machineName` set to a
  distinct name such as `REHEARSAL-M`, since both users share the computer's name). G5 is per
  user, so the writer keeps running throughout, as DSWETT-HOME will.

### 3. The checklist (both directions)

Run with sessions of each agent (Copilot, Claude, Codex) on the migrating user, reading the
dashboard in a browser, `mosquitto_sub -v -t 'homeassistant/#' -t 'copilot/cli/#' --retained-only`
for retained state, and `config/entity_registry/list` for ids:

1. Daemon → host: the machine row stays (online, session count, version, `(dev)`); each card
   keeps rendering status, activity, the reply text, the trail, compactions and background
   counts; Detailed activity toggles reasoning; usage rows stay; no `_2` ids; the update row
   shows the Slipgates version and its Install and Check buttons work; a question is answered
   from the card, on a phone-sized browser too.
2. A session ending: it leaves the list, the writer drops its card, and only then do its
   entities go; all 29 topics are empty.
3. Host restart (kill the process, start it): no new entities; a session that ended while the
   host was down is retired after its card has gone.
4. Home Assistant restart: everything comes back from retained messages; the machine is online
   again within a minute; the reply boxes are blank rather than `unknown` within a reconcile,
   and a question that was waiting is armed again and answerable.
5. G5: start agent-ha-bridge for the migrating user while the host runs: the connector logs that
   it paused and the broker shows no message from it; stop it and the connector resumes.
6. Host → daemon: the daemon adopts the running sessions, republishes its full set and launch
   card, sweeps what ended; no duplicates, nothing unavailable left.
7. Then snapshot-restore the VM and run it again (§10 stage 3).

The rest of the connector's tests stay in-process (`tests/AgentBridge.HomeAssistant.Tests`:
contract tests against the fixtures, lifecycle and answer tests against an in-process Home
Assistant and broker).

## Configuration and commands

`config.json`'s `homeAssistant` section ([host-config.md](host-config.md)):

```jsonc
"homeAssistant": {
  "enabled": false,                       // off by default
  "baseUrl": "http://127.0.0.1:8123",
  "dashboardUrlPath": "agent-decisions",  // read, never written
  "detailedActivity": true,               // when the machine has no Detailed activity switch
  "machineName": null,                    // the computer's name; set only for a rehearsal
  "disposable": false,                    // a development build needs true
  "agentUserIds": [],                     // Home Assistant users whose presses are agents'
  "notifications": {                      // the hooks' pushes (stage 5b); off by default
    "enabled": false, "service": "notify.notify", "tickerCategory": ""
  },
  "answerButtons": {                      // a question's buttons on the phones named here
    "enabled": true, "services": [], "maxButtons": 6,
    "icon": "mdi:chat-question", "iconColor": "#FFFFFF", "color": "#FF9F0A", "interruptionLevel": "time-sensitive"
  },
  "updates": {                            // the host's own releases on the update entity (stage 6)
    "checkForUpdates": true, "checkHours": 0.5
  }
}
```

```
agent-bridge-host config set homeAssistant.baseUrl http://127.0.0.1:8123
agent-bridge-host config set homeAssistant.disposable true
agent-bridge-host config set homeAssistant.enabled true
agent-bridge-host ha token set            # paste the token; it is not shown
agent-bridge-host ha status               # on, or why not, and any refusal; never the token
agent-bridge-host ha shadow               # compare with agent-ha-bridge's retained topics; writes nothing
```

`serve` logs `the Home Assistant connector is on: <url> as agent_bridge_<slug>` or why it is
off, and `Home Assistant connector paused: …` while agent-ha-bridge runs.

## Troubleshooting

### Refused tokens are never repeated into a ban

Home Assistant's `http.ip_ban_enabled` bans a client address after `login_attempts_threshold`
failed logins (10 by default). Every request it answers 401 counts, and so does every WebSocket
`auth_invalid`. Only a WebSocket `auth_ok` resets the count; a successful REST request does not.
A banned address gets 403 on everything, whatever the token, until the ban is cleared by hand.
On 2026-11-02 a machine was banned this way just after `migrate` ([#227](https://github.com/danswett/agent-bridge/issues/227)):
its connector tried `mqtt.publish` again every 30 s with a token Home Assistant had rejected.

Every request the host makes with a Home Assistant token therefore goes through one guard
(`HaAuthGuard`): the connector's REST calls and WebSocket logins (publishing, the registry, the
watch, notifications, the launch card, the reply card's images), `ha shadow`, and `shadow run`'s
reads with agent-ha-bridge's token. They all share one record, `ha-auth.json` beside the profiles
(`%LOCALAPPDATA%\AgentBridge\ha-auth.json` on Windows). It holds counts and digests, never a token
or an address, and a restart of the host starts nothing over. The rules, which are stricter than
agent-ha-bridge's (`test-auth-backoff.ps1`):

- **A rejected token** (401 or `auth_invalid`) is tried at most 3 times in all, at least 5 and
  then 15 minutes apart. After that it is never sent again: not on a timer, and not after a
  restart. Only a change of token or address gives an attempt.
- **Refusals are counted for the machine**, whatever the token and however the address is
  written (`homeassistant.local`, its IP, `http` or `https`), since Home Assistant counts by the
  client's address. Once it has refused 3 times without a WebSocket login since, a change
  (`ha token set`, even with the same token, or a new `homeAssistant.baseUrl`) earns exactly one
  attempt.
- **A refusal is recorded when Home Assistant answers**, from the status alone, however the body
  then goes; and every process reads what the others have recorded before each request.
- **A token not yet accepted** goes one request at a time, across processes too, so a burst of
  callers spends one attempt, not one each. That covers a fresh start, a fresh token, and a look
  during a ban. An accepted token goes at most 2 requests at a time in each process, so if it is
  revoked while a burst is on its way, only those already out are refused. A request Home
  Assistant answers without reading the token (an image from `/api/image/serve`) never counts as
  accepting it.
- **A 403 is taken for a ban**, whatever the token. Home Assistant answers a refused or
  under-privileged token with 401; it answers 403 only to an address it has banned (or a proxy in
  front of it does, to a machine it refuses). Requests from a banned address are refused before
  any token is read, so they count for nothing but achieve nothing either. The host only looks
  again after 15 minutes, then an hour, then every 4 hours.
- **Anything else** is not a refusal: a 5xx, a timeout or a refused connection (Home Assistant
  restarting, say). Those are retried as before, the connector's start every 30 s.
- **Without the record, nothing new is tried.** Every process reads and writes `ha-auth.json`
  under its lock (`ha-auth.json.lock`), and writes it to a temporary file flushed to disk before
  moving it into place. If the record can't be locked, read or written for a moment (another
  program holding it open, say), the process keeps what it counted, holds every token not
  already accepted, and writes what it counted as soon as it can. Only a write that succeeds
  ends that; a read alone doesn't. A token already accepted carries on.
- **A corrupt record is set aside, not trusted.** A record that isn't one (cut short, zero-filled
  by a power cut, JSON with a null table or a negative count, one missing a field of its version, or
  one of a version newer than the host knows) is copied to `ha-auth.json.corrupt`, logged, and replaced by a record of its
  loss; until that is written, the corrupt file stays where it is and goes on holding. Its counts
  are lost, so every token not accepted since is held, in every process and after a restart, even
  one a process still knew as accepted, until a person acts. `ha token set` allows that token
  one attempt; deleting `ha-auth.json` starts over. If `ha token set` can't record that, it says
  so and exits with 1, and the token stays held.
- **The record's format is versioned.** A record written by an earlier build is brought up to
  date when it is read, not set aside: a token it shows as accepted carries on, and one it shows
  refused at all is stopped. A change to the format bumps the version, gives the new field a
  default, and migrates older records.

The host log, `ha status` (a `refused:` line) and the app's Settings page all say the same thing:
what was refused, what to do, and when the next attempt is, or that there will be none.

### "Home Assistant rejected the token"

The token was revoked, belongs to a user that was removed or is not an administrator, or was
mistyped. Make a new long-lived access token for an administrator (your profile in Home
Assistant, *Security*). Give it at the machine, then restart the host:

```
agent-bridge-host ha token set
```

The host tries it once. If it is refused too, `ha status` says so again.

### "Home Assistant has banned this machine's address"

Home Assistant has written this machine's address to `/config/ip_bans.yaml` and refuses
everything from it. That includes agent-ha-bridge after a rollback, and a browser on the same
machine. Home Assistant reads the file at start and keeps the ban in memory, so neither
restarting Home Assistant nor editing the file is enough on its own. To clear it:

1. Fix whatever caused it first, usually the token (above), or the machine is banned again.
2. In `/config/ip_bans.yaml` (the File editor, Studio Code Server or the SSH add-on), remove
   this machine's entry: its address followed by an indented `banned_at:` line. Leave any others.
3. Restart Home Assistant (*Settings*, *System*, restart). Every machine's entities read
   `unavailable` for a minute or two while it starts.
4. The host's next look finds Home Assistant answering again and carries on by itself. To look at
   once, give the token again with `ha token set` and restart the host.
