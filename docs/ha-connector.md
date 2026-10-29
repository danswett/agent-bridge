# The Home Assistant connector

A machine moved off agent-ha-bridge's PowerShell daemon onto `agent-bridge-host` stays on the
existing Home Assistant dashboard: its cards, answering, replies, attachments, launch, stop,
usage, fleet status, update buttons and phone notifications, as today (decided 2026-10-29;
design doc §2, §9, §10 "Path to migration", stage 1b, and §0.1 G5–G9). The connector is the
host module that does it, by speaking agent-ha-bridge's **existing per-machine contract
exactly**: the same MQTT discovery configs, state and attribute topics, retained messages,
command topics and pinned entity ids. DSWETT-HOME, the publication writer, still on
agent-ha-bridge, goes on generating the dashboard from every machine's retained session list,
and a migrated machine's cards render unchanged.

It is built in stages. **This is stage 1: read-only status.** The command-handling plan for the
later stages is below.

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
* an **unretained** `online` on `copilot/cli/machine/<slug>/online/state` every 60 s. Three
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

### Names

| What | Rule (agent-ha-bridge function) | Example |
|---|---|---|
| Machine slug | lowercase, every run of non-`[a-z0-9]` one `_`, trimmed, at most 24, `machine` if empty (`Get-BridgeMachineSlug`) | `DSWETT-DEV-VM1` → `dswett_dev_vm1` |
| Machine node | `agent_bridge_<slug>` | `agent_bridge_dswett_dev_vm1` |
| Machine topic root | `copilot/cli/machine/<slug>` | |
| Session node | `agent_bridge_` + the id's letters and digits, at most 16, lowercased; an id with none gets 12 hex digits of its SHA-256 (`Get-CopilotMqttNodeId`) | `6fcbab0c-1d2e-…` → `agent_bridge_6fcbab0c1d2e4f50` |
| Session topic root | `copilot/cli/<node>` | |
| Session name (its device, its card title) | `Copilot: <workspace name or id[:8]>`, `Claude: <folder>`, `Codex: <folder>`; at most 120; `{{`, `{%`, `{#` broken with a zero-width space (`Get-*SessionDisplay`, `Remove-CopilotTemplateMarkup`) | `Copilot: Fix the build` |

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
| `binary_sensor.agent_bridge_<slug>_online` | `homeassistant/binary_sensor/<mnode>/online/config` | `…/online/state`, unretained `online` | config at start; beat every 60 s |
| `sensor.agent_bridge_<slug>_sessions` | `homeassistant/sensor/<mnode>/sessions/config` | `…/global/state` = count; `…/global/attr` = `sessions[{name,machine,node,kind}]`, `machine`, `machine_slug`, `capabilities`, `resumable: []`, `discovery_held: ""`, `updated` | on a change, and every 300 s |
| `update.agent_bridge_<slug>_update` | `homeassistant/update/<mnode>/update/config` | `…/update/state` = `installed_version`, `latest_version` (both the host's version), `title`, `release_summary: ""`, `in_progress: false`, `update_percentage: null`; `…/update/attributes` = `stage`, `stage_detail` | at start |
| `sensor.agent_bridge_<slug>_usage_<agent>` (copilot, claude, codex) | `homeassistant/sensor/<mnode>/usage_<agent>/config` | state = the window closest to running out, or `unknown`; attributes `client`, `label`, `account`, `account_id`, `plan`, `source`, `windows[{key,label,percent,used?,limit?,unit?,resets_at?,elapsed?}]`, `error`, `measured_at`, `machine`, `machine_slug`, `updated` | when a reading changes |

`capabilities` is what lets the writer draw the right rows for this machine. Stage 1 reports
`newSession`, `profile`, `resume`, `agent`, `tuning`, `permissions`, `resumeShare` false and
`fleet` empty, so no launch card is drawn for it; `detailed` true only while the machine's
`input_boolean.agent_bridge_<slug>_detailed_activity` exists (agent-ha-bridge created it), so
the Detail switch is drawn only when it has something to switch; `dev` true for a development
build, so the machine row says `(dev)`.

Per session:

| Entity (pinned id) | Discovery topic | State / attributes |
|---|---|---|
| `sensor.<node>_status` | `homeassistant/sensor/<node>/status/config` | `working`, `waiting`, `idle`, `agents` (idle, background agents running), `shell` (idle, background commands running) or `ending`; attributes `session`, `machine`, `process_id`, `updated`, and only when non-empty or non-zero `model`, `background_agents`, `background_shells`, `compactions` (`Add-DaemonTuningAttributes`) |
| `sensor.<node>_activity` | `homeassistant/sensor/<node>/activity/config` | a line, cut to 255 with `...`: `Running: <tool>`, the reply's first line, `Thinking`, or `Idle`; attributes `session`, `machine`, `verbose`, `updated`, `driver`, `history` (12 deep, 24 with Detailed activity), and the card text as `Add-DaemonCardText` lays it out: `response` (to 6000 characters, then `_(truncated - see terminal)_`) with `response_kind` `text` or `reasoning`, and for an agent that does not interleave its thinking, `reasoning` (to 4000) |

Both carry the device block (`identifiers: [<node>]`, `name`, `manufacturer: AI CLI bridge`,
`model: <machine>`) and `availability` on `copilot/cli/<node>/available`. A renamed session
republishes only these two configs (`Update-CopilotMqttSessionName`). Status and activity are
published when they change, compared without their `updated` stamp.

How the host's `Session` maps to what the daemon kept, where the two differ:

* The daemon remembers the newest message and whether it was a thought; the host keeps the
  newest reply and the newest reasoning. A thought is shown as the newest line (Detailed
  activity on, `response_kind: reasoning`) while the turn has produced no reply yet.
* The trail is rebuilt from the host's tool history in the daemon's words: `Reading your
  message`, `Running: <tool>` per call, `Thinking: <first line>` with Detailed activity on
  (`Get-BridgeThoughtLine`, ported and fixture-checked), the reply's first line.
* `driver` is `human` until replies and launches come through this host (stage 3).

### What stage 1 withdraws

Everything agent-ha-bridge publishes that stage 1 would not act on is withdrawn (an empty
retained payload), so the dashboard never offers a control that silently does nothing:

* per machine, at start: the install button, the launch card's prompt, prompt payload,
  workspace, profile, agent, model, effort, context, resume and permissions selectors, Launch,
  the launch result and transfer request, and their state topics (17 of the 25 machine topics);
* per session, when the connector first publishes it: the decision and field selectors (12),
  the submit and stop buttons, the reply box and reply payload sensor, and their state topics.

The generated dashboard draws a card with "Entity not found" for those controls until the
stage that serves them lands. **Stage 1 is therefore for the disposable rehearsal only; no real
machine enables the connector until the stages that serve what it withdraws (2–6) have
landed** (see the plan).

## Lifecycle

**Start**, and again after a pause:

1. Declare the online sensor and pin it; the first beat follows two seconds later.
2. Read the Detailed activity switch (`on`, `off`, or absent: the setting `detailedActivity`).
3. Withdraw this machine's unserved controls; publish the update entity and pin it.
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
300 s; the beat every 60 s; usage on a new reading; pending pins; and every 15 s (the daemon's
reconcile) the Detailed activity switch and the retirement queue. A failing call is retried on
the next pass and logged once per kind of failure until it clears.

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

**A host restart** re-runs the start: live sessions are republished onto their existing,
pinned entities (no new ones), and the remembered queue is carried over. **A Home Assistant
restart** loses nothing retained; the 300 s re-assert of the session list covers a broker that
lost retained state. The daemon's repair of a liveness sensor Home Assistant has stopped
listening to (#177, #202) is stage 7.

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
   their cards have gone. The `Detailed activity` helper is left as it is (it is Home
   Assistant's, not MQTT's) and read.
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
process list; not echoed at a console), and kept the way the device key is: DPAPI to the Windows
user in `ha-token.dpapi` beside it; elsewhere an owner-only file until the Keychain arrives
(P5). It is never in `config.json`, never logged; failures name the HTTP status and never echo a
body. `ha status` says whether it is stored, never what it is.

**Agent attribution.** Two tokens matter in agent-ha-bridge: the user's, which the daemon
publishes with, and the agent's (`homeAssistant.agentTokenEnvVar`, default
`AGENT_HA_AGENT_TOKEN`), which agents use when *they* press, reply or launch. The daemon marks a
card agent-driven (the purple edge) when the `context.user_id` on the state change that carried
the input is in `homeAssistant.agentUserIds`. The connector keeps that split: it publishes with
the user's token only, and from stage 2 reads `context.user_id` off each command's state change
and matches it against its own `homeAssistant.agentUserIds` to set `driver`. Agents driving a
migrated machine go on using their own token, unchanged. Stage 1 publishes `driver: human`.

**Notifications.** The phone flow (`notify.mobile_app_*` with answer buttons, and
`mobile_app_notification_action` events back) is stage 5. Until then a migrated machine's
questions reach phones through the host's own Web Push, not through Home Assistant.

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
| 2 Answers | Publish the full per-session set (`Publish-CopilotMqttSession`: decision, 12 field slots, submit, reply, reply payload, stop) and prime the reply box with `' '`. Arm a question onto the decision selector and field slots with its `question`, options, `decision_id` and the `#n,m` position encoding; read a Submit press or a tapped choice; answer through `AnswerService`; stamp `answer_consumed_at`; Cancel request; clear on resolve. `driver` from `context.user_id` | `Set-CopilotMqttDecision`, `Publish-CopilotMqttDecisionFields`, `Clear-CopilotMqttDecision`, `daemon-decisions.ps1`, card `answer_consumed_at` |
| 3 Replies and attachments | The reply card's JSON on `copilot/cli/<node>/replypayload/set` (text, uploaded image ids, `at`), read through the reply payload sensor's attributes; images fetched from Home Assistant's image store (`/api/image/serve/<id>/original`), staged in `AttachmentStore`, deleted after (`image/delete`); `ReplyService`; driver pending until the turn starts | `daemon-replies.ps1`, card reply path |
| 4 Stop | `button.<node>_stop`, two presses when the session is not idle, the confirm and lapsed notes pinned on the activity line; `StopService`; `ending` | `Invoke-PendingStops`, `Get-DaemonCardSummary`, `CopilotEndSessionConfirmNote` |
| 5 Launch and notifications | The launch card's controls (`Publish-CopilotMqttNewSession`) from the host's approved workspaces and agents; the 255-character limit lifted through the retained `newsession/promptpayload` (`{at, text, request, settings}`); `new_session_result` with `recent` per request; `capabilities.newSession`/`launchRequest`; transfer requests. Phone notifications with answer buttons and their actions | `daemon-launch.ps1`, `daemon-decision-notify.ps1` |
| 6 Update buttons | `button.agent_bridge_<slug>_install_update` starts the product's own update (the app's Velopack update on Windows), with `stage`/`stage_detail` and `update_percentage` from it, and `latest_version` from this product's release feed — never agent-ha-bridge's (G2) | `Publish-CopilotMqttUpdate`, `daemon-maintenance.ps1` |
| 7 Upkeep | The Detailed activity helper created when absent (`input_boolean/create`, never recreated); the liveness repair for a sensor Home Assistant has stopped applying, with its attempt limit (#177, #202); forget-machine (the card's X clears this machine's topics from the browser; the connector must not resurrect a machine that was forgotten while it was off) | `Initialize-CopilotVerboseToggle`, `Confirm-DaemonOnlineHeartbeat`, `Get-BridgeMachineForgetTopic` |

A shadow mode belongs beside these (stage 2): while agent-ha-bridge still runs, the connector
computes what it would publish and compares it, read-only, with the daemon's retained topics,
for the week of shadow running on DEV-VM1 before its cutover (§10 stage 4).

## Validation plan: a disposable Home Assistant and Mosquitto

Never the live Home Assistant or broker. Everything below runs on the Hyperloop VM (or a
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
   counts; Detailed activity toggles reasoning; usage rows stay; no `_2` ids; the launch card
   and install button for the machine go (stage 1) and nothing else does.
2. A session ending: it leaves the list, the writer drops its card, and only then do its
   entities go; all 29 topics are empty.
3. Host restart (kill the process, start it): no new entities; a session that ended while the
   host was down is retired after its card has gone.
4. Home Assistant restart: everything comes back from retained messages; the machine is online
   again within a minute.
5. G5: start agent-ha-bridge for the migrating user while the host runs: the connector logs that
   it paused and the broker shows no message from it; stop it and the connector resumes.
6. Host → daemon: the daemon adopts the running sessions, republishes its full set and launch
   card, sweeps what ended; no duplicates, nothing unavailable left.
7. Then snapshot-restore the VM and run it again (§10 stage 3).

The parent session arranges the VM; the connector's own tests stay in-process
(`tests/AgentBridge.HomeAssistant.Tests`: contract tests against the fixture, lifecycle tests
against an in-process Home Assistant and broker). A CI job with the same two containers, driving
the connector on a Linux runner with an injected probe, is planned with stage 2 (G6's "container
in CI").

## Configuration and commands

`config.json`'s `homeAssistant` section ([host-config.md](host-config.md)):

```jsonc
"homeAssistant": {
  "enabled": false,                       // off by default
  "baseUrl": "http://127.0.0.1:8123",
  "dashboardUrlPath": "agent-decisions",  // read, never written
  "detailedActivity": true,               // when the machine has no Detailed activity switch
  "machineName": null,                    // the computer's name; set only for a rehearsal
  "disposable": false                     // a development build needs true
}
```

```
agent-bridge-host config set homeAssistant.baseUrl http://127.0.0.1:8123
agent-bridge-host config set homeAssistant.disposable true
agent-bridge-host config set homeAssistant.enabled true
agent-bridge-host ha token set            # paste the token; it is not shown
agent-bridge-host ha status               # on, or why not; never the token
```

`serve` logs `the Home Assistant connector is on: <url> as agent_bridge_<slug>` or why it is
off, and `Home Assistant connector paused: …` while agent-ha-bridge runs.
