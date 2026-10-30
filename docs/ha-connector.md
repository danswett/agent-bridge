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

It is built in stages. **Stage 1 is read-only status; stage 2 adds answering questions from the
card**, and a read-only shadow that compares the connector with a daemon still running; **stage
3 adds replies, with images and files, from the card**. The plan for the later stages is below.

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
| `sensor.agent_bridge_<slug>_sessions` | `homeassistant/sensor/<mnode>/sessions/config` | `…/global/state` = count; `…/global/attr` = `sessions[{name,machine,node,kind}]`, `machine`, `machine_slug`, `capabilities`, `resumable: []`, `discovery_held` (what holds the host's discovery, [v1-sessions.md](protocol/v1-sessions.md#discovery-held), else `""`), `updated` | on a change, and every 300 s |
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

### What is withdrawn

Everything agent-ha-bridge publishes that the connector would not act on is withdrawn (an empty
retained payload), so the dashboard never offers a control that silently does nothing:

* per machine, at start: the install button, the launch card's prompt, prompt payload,
  workspace, profile, agent, model, effort, context, resume and permissions selectors, Launch,
  the launch result and transfer request, and their state topics (17 of the 25 machine topics);
* per session, when the connector first publishes it: the stop button and the two legacy state
  topics `decision/state` and `reply/state`; and, only when the host has no way to answer (no
  `ISessionAnswerer` on this platform), also the decision and field selectors (12), the submit
  button, the reply box and the reply payload sensor, as stage 1 did.

The generated dashboard draws "Entity not found" for those controls until the stage that serves
them lands. **The connector is therefore for the disposable rehearsal only; no real machine
enables it until the stages that serve what it withdraws (4–6) have landed** (see the plan).

### Stage 2: answering from the card

With an answerer, a session's first publication is agent-ha-bridge's full set in its order
(`Publish-CopilotMqttSession`): availability, the decision selector (`Idle`), the reply box,
the reply payload sensor, status, activity, the twelve field slots (`Idle`, `Field <i>`) and
Send; everything but stop. 900 ms later (`FieldPinDelay`, the daemon's wait for discovery) the
controls are pinned to their ids. `HaDecision` is the port of `Set-CopilotMqttDecision`,
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
a press is spent. A question that ends is cleared (selector `Idle`, attributes `{}`, the slots
it used reset); a question the card still shows when the connector takes over, but which the
session no longer asks, is cleared too.

**Driver.** A press whose `context.user_id` is in `homeAssistant.agentUserIds`, or a reply
payload saying `driver: agent`, makes the next turn agent-driven (the purple edge), if that
turn starts within 600 s (`DriverArmSeconds`); otherwise it reads `human`.

Where stage 2 knowingly differs from the daemon:

| Difference | Why |
|---|---|
| A question the host cannot answer from a card (`AnswerAtMachine`, or a question the host had to truncate) is armed with `**This one has to be answered in the terminal** - <why>` appended and is never read | The host already knows these cannot be delivered; the daemon learns it later |
| A question longer than the card's 6000 characters (with any hint) is answered in the terminal, never on the card; the note is kept within the limit | The daemon cuts it and still takes an answer, so an approval could authorise text nobody could read |
| An empty multi-select answer is allowed only for Copilot | The host's `Field` carries no `minItems`; Copilot's own forms allow it |
| `takes_typed_answer` only for a lone choice that allows another answer | The daemon also offers it for yes/no, which the host's question model cannot take |
| Claude forms use field slots, never the selector's choices | Claude's questions always arrive as forms here |
| An approval offers `Approve` / `Deny` on the selector with `answer_on_tap` | The host's approval is a question kind of its own |
| `Cancel request` says `Answer NOT sent` and resets the selector | The host has no way to cancel an agent's question |
| A reply while a question is open answers the question; at a question only the terminal can answer, it says `Reply NOT sent` (`the session is waiting on a question`) | The host refuses a reply while its session asks a question |
| A free-text answer from the reply box must differ from the box at arming | The box keeps the previous reply; the daemon reads it the same way through its baseline |
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

### The shadow: comparing with a running daemon

`agent-bridge-host ha shadow [--json] [--window <seconds>]` runs one connector pass against a
Home Assistant that **forwards reads and records writes without sending any** (`HaShadow`), and
reads or answers no press, then
reads the broker's retained topics for this machine and for every session either side names
(the daemon's from its `global/attr`), and compares them as JSON, ignoring `updated` and
`asked_at`. It takes one reading of every agent's usage first, as `agent-bridge-host usage`
does, so the usage sensors are compared too. Each topic is reported `same`, `differs`,
`agent-ha-bridge only` or `host only`,
with a note where the difference is by design (stop, launch and transfer controls; the answer
controls when the host cannot answer; usage when no reading was given). It needs `baseUrl` and the stored token but not
`enabled`, and it ignores G5: it is the one thing that is meant to run while the daemon does,
for the week of shadow running before a machine's cutover (§10 stage 4). It complements
`agent-bridge-host shadow`, which compares what the host *knows* with the daemon's local state
files; `ha shadow` compares what each would *publish* to Home Assistant.

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
against its own `homeAssistant.agentUserIds` to set `driver` (stage 2). Agents driving a
migrated machine go on using their own token, unchanged.

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
| 2 Answers (done) | The full per-session set but stop; arm, read, answer, tell the card, clear; `driver` from `context.user_id`; the read-only shadow; the container CI job (above and below) | `Set-CopilotMqttDecision`, `Publish-CopilotMqttDecisionFields`, `Clear-CopilotMqttDecision`, `daemon-decisions.ps1`, card `answer_consumed_at` |
| 3 Replies and attachments (done) | The reply card's publish (text, image ids, inline files) and the reply box with Send; images from Home Assistant's image store, deleted after; the files through `AttachmentStore` and `ReplyService`; driver pending until the turn starts (above) | `daemon-replies.ps1`, card reply path |
| 4 Stop | `button.<node>_stop`, two presses when the session is not idle, the confirm and lapsed notes pinned on the activity line; `StopService`; `ending` | `Invoke-PendingStops`, `Get-DaemonCardSummary`, `CopilotEndSessionConfirmNote` |
| 5 Launch and notifications | The launch card's controls (`Publish-CopilotMqttNewSession`) from the host's approved workspaces and agents; the 255-character limit lifted through the retained `newsession/promptpayload` (`{at, text, request, settings}`); `new_session_result` with `recent` per request; `capabilities.newSession`/`launchRequest`; transfer requests. Phone notifications with answer buttons and their actions | `daemon-launch.ps1`, `daemon-decision-notify.ps1` |
| 6 Update buttons | `button.agent_bridge_<slug>_install_update` starts the product's own update (the app's Velopack update on Windows), with `stage`/`stage_detail` and `update_percentage` from it, and `latest_version` from this product's release feed — never agent-ha-bridge's (G2) | `Publish-CopilotMqttUpdate`, `daemon-maintenance.ps1` |
| 7 Upkeep | The Detailed activity helper created when absent (`input_boolean/create`, never recreated); the liveness repair for a sensor Home Assistant has stopped applying, with its attempt limit (#177, #202); forget-machine (the card's X clears this machine's topics from the browser; the connector must not resurrect a machine that was forgotten while it was off) | `Initialize-CopilotVerboseToggle`, `Confirm-DaemonOnlineHeartbeat`, `Get-BridgeMachineForgetTopic` |

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

* the card's entities exist at the ids the dashboard uses, a question is armed onto them, the
  stop button is not offered, and the machine reads online with its session count;
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
   counts; Detailed activity toggles reasoning; usage rows stay; no `_2` ids; the launch card,
   install and stop buttons for the machine go and nothing else does; a question is answered
   from the card, on a phone-sized browser too.
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
  "agentUserIds": []                      // Home Assistant users whose presses are agents'
}
```

```
agent-bridge-host config set homeAssistant.baseUrl http://127.0.0.1:8123
agent-bridge-host config set homeAssistant.disposable true
agent-bridge-host config set homeAssistant.enabled true
agent-bridge-host ha token set            # paste the token; it is not shown
agent-bridge-host ha status               # on, or why not; never the token
agent-bridge-host ha shadow               # compare with agent-ha-bridge's retained topics; writes nothing
```

`serve` logs `the Home Assistant connector is on: <url> as agent_bridge_<slug>` or why it is
off, and `Home Assistant connector paused: …` while agent-ha-bridge runs.
