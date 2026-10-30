# Parity rehearsal checklist

The scripted migrate-and-rollback rehearsal on the disposable Hyperloop VM: design §10 stage 3,
issue #39. A person or an agent driving Hyperloop runs it step by step. It goes in order:

1. Install agent-ha-bridge.
2. Install agent-bridge beside it, read-only.
3. Migrate.
4. Check each row of [parity.md](parity.md) against a disposable Home Assistant, the Windows
   app and the PWA.
5. Roll back.
6. Prove agent-ha-bridge is exactly as it was.
7. Restore the snapshot and run it again.

Each step has an exact expected result and the evidence to keep. Wherever a check can be
made by a program, it is made by **`agent-bridge-rehearsal`** (`tools/AgentBridge.Rehearsal`).
The tool reads three things:

* the host over its local pipe, as the Windows app does;
* the agents' own transcripts after a mark;
* the disposable Home Assistant.

It also hashes agent-ha-bridge's files. What only a person can see (a card, a glow, a
notification) is a Hyperloop step, recorded with `note` and a screenshot.

Read [parity.md](parity.md) first. Rows are cited here as **§N "start of the row"**, meaning
the row in parity.md section N whose first cell starts with those words. CI checks that:

* each citation names exactly one row;
* every row in parity.md is cited at least once, by a step or by
  [Rows not exercised](#rows-not-exercised);
* every `agent-bridge-rehearsal` command below parses;
* step ids are unique.

The checks live in `ChecklistTests`.

## What runs where

The rehearsal needs **two** disposable machines.

agent-ha-bridge names its per-machine entities after the computer, with no override:
`Get-BridgeMachineSlug` takes `[Environment]::MachineName` (`hooks/decision-bridge-common.ps1`).
So two Windows users on one VM would publish the same `online`, `sessions`, `update` and
launch entities. Their session counts and heartbeats would overwrite each other. The
publication writer therefore runs on a computer of its own. This replaces the single-VM,
two-user arrangement in [ha-connector.md](ha-connector.md) › Validation plan. The connector's
`machineName` renames only the migrated host, not agent-ha-bridge before it.

| Machine | Computer name (slug) | Runs |
|---|---|---|
| **VM-W** | `REHEARSAL-W` (`rehearsal_w`) | Docker with the disposable Home Assistant and Mosquitto ([ha-connector.md](ha-connector.md) › Validation plan, when #60 lands). agent-ha-bridge as the **publication writer** of that instance. Edge for the HA dashboard and the PWA |
| **VM-M** | `REHEARSAL-M` (`rehearsal_m`) | The machine being migrated: one Windows user, `rehearsal`, with Copilot CLI, Claude Code, Codex, agent-ha-bridge (participant), then agent-bridge. Reaches VM-W's Home Assistant through `ssh -N -L 8123:127.0.0.1:8123 rehearsal@VM-W`, so every address stays `127.0.0.1` |
| **Phone** | — | An iPhone with the PWA installed and signed in to the same GitHub account (push) |

Never the live Home Assistant, broker or relay account's other machines (design G6). The
tool refuses any Home Assistant URL that is not loopback before making a request.

**Actors** in the step tables:

* **Tool**: run the command in a PowerShell window on the machine named. Exit code 0 is the
  expected result. 1 means the product did something else, 2 means the check could not be
  made, and 3 means it is left to a person.
* **App**: the Windows app on VM-M, through Hyperloop.
* **PWA**: the PWA in Edge on VM-W, through Hyperloop. It is a separate device, so
  everything it does crosses the relay.
* **HA**: the disposable instance's `/agent-decisions` dashboard in Edge on VM-W, through
  Hyperloop.
* **Phone**: a person with the iPhone.
* **Machine**: the agent's own terminal on VM-M, through Hyperloop.

**Needs** names the work a step depends on. If that work is not on `main` yet, or not
deployed, when you reach the step, record it with
`note --result manual --detail "needs #NN"` and run it once the work lands. The table says
which have merged. For the rest, the interfaces assumed are the ones stated in each issue or
PR:

| Mark | Work | Interface assumed |
|---|---|---|
| **#37** | Hook intake | The host consumes `agent-bridge-hook`'s spool and keeps its heartbeat. It writes the Claude and Codex registrations and the Codex approval marker. `agent-bridge-host hook --fallback` prints the fixed safe reply |
| **#38** | Migrate and roll back | `agent-bridge-host migrate [--dry-run]` and `agent-bridge-host rollback`. Migrate disables (not deletes) `AgentBridgeDaemon_<id>`, moves the hook fallback to the host, imports `newSession.*` into `launch.*`, refuses on the publication writer, and never writes `~/.agent-ha-bridge`. Rollback restores registrations, instruction blocks and config byte for byte |
| **#40** | Shadow comparison (#63, merged) | `agent-bridge-host shadow run\|once\|summary` ([host-shadow.md](host-shadow.md)): read-only, from the daemon's own files, with a daily verdict `clean`, `mismatches` or `no data` |
| **#41** | Claude and Codex replies | `sessions.reply` delivers to Claude and Codex |
| **#43** | Discovery holds | An incomplete pass holds removal, resume offers and reclaim |
| **#44** | Sessions cannot manage their host | `host.restart` and `host.stop` from a process inside a session the host can see are refused |
| **HA1** | HA connector stage 1 (#60, merged) | `homeAssistant.*` config, `agent-bridge-host ha token set\|status`. Read-only status in agent-ha-bridge's contract: `binary_sensor.agent_bridge_<slug>_online`, `sensor.agent_bridge_<slug>_sessions`, per-session `sensor.agent_bridge_<node>_status` and `_activity`. Paused while agent-ha-bridge runs for the user |
| **HA2** | HA connector later stages | The old dashboard's controls (answer, reply, end, launch, install update, Detail toggle) and actionable notifications, wire-compatible |
| **Blob** | Blob store (#61, merged; production storage is deployed separately, `deploy.ps1 -Production`) | Files over 256 KiB up to 100 MiB: `sessions.attach.blob` from a device through the relay, `sessions.attach.direct` from the app over the pipe ([host-replies.md](host-replies.md) › Large files) |
| **Transfer** | Session transfer, after the blob store | Copy a session to another machine and resume it there |
| **#59** | Claude discovery without hooks | Claude sessions found from `~/.claude/sessions/<pid>.json`. A waiting `AskUserQuestion` still needs the PreToolUse hook, which is #37 |
| **#62** | Codex discovery | Codex sessions run with `--no-daemon` found from their thread lock |
| **Cl/Cx live** | Claude and Codex live tests | The Claude and Codex answer and end paths, verified live by their own slice |

## Conventions

On each machine, once per PowerShell window:

```powershell
$env:AGENT_BRIDGE_REHEARSAL_EVIDENCE = 'C:\rehearsal\evidence\run-1'   # run-2 after the restore
$ha   = 'C:\rehearsal\ha.token'        # a disposable-instance token, written once, never printed
$m    = 'rehearsal_m'                  # VM-M's slug
$repo = 'C:\rehearsal\repo'
$base = 'C:\rehearsal\evidence\baseline-S1.json'
```

* The evidence folder holds:
  * `steps\<step>.jsonl`, one record per attempt: command, verdict, detail, data, time in UTC;
  * `marks.json`;
  * `screens\<step>.png`.

  `agent-bridge-rehearsal report --checklist docs\parity-checklist.md`, run from the checkout's
  root, lists the latest attempt of each step, in Pacific time, and the steps not yet run.
  Keep the folder with the run.
* **Marks.** Run `agent-bridge-rehearsal mark <id>` *before* acting on a session.
  The `expect` checks read only what the session's transcript and the host log gained since
  the mark, so the evidence is the record the step produced, never an older one that
  happens to match.
* Session ids: `$cp`, `$cl` and `$cx` hold the Copilot, Claude and Codex sessions in use. Set
  them with `sessions … --id-only`, which prints only the id of the one matching session and
  fails otherwise. Before agent-bridge is installed, use `newest <agent>`, which reads the
  transcripts instead.
* **Screenshots.** Take them with Hyperloop and keep them with
  `agent-bridge-rehearsal note --step <id> --result pass --detail "…" --screenshot <file>`.
* The prompts in [Prompts](#prompts) are typed at the machine (Machine) or sent as a reply
  (App or PWA) exactly as written, because the checks look for their words.
* Times are Pacific. Nothing here prints a token. The tool hashes agent-ha-bridge's files
  and never reads their contents into the evidence.
* Start the agents from a window where the variables above are set. A command an agent runs
  itself (E15-01) then records into the same evidence folder.
* The agents ask before running a shell command. Outside the permission steps (E3), approve each at the terminal **once**: never "for the rest of the session". Otherwise a later step's prompt (P9) would not ask.
* Words containing curly quotes (“ ”) go in single quotes, as E2-01 and E2-04 do. PowerShell
  treats curly double quotes as delimiters inside a double-quoted string.

## Phase A: the machines (once)

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **A-01** | | Both | Install Git and the .NET 10 SDK. Clone agent-bridge at the commit under test into `C:\rehearsal\agent-bridge`. From there, `dotnet publish tools/AgentBridge.Rehearsal -c Release -r win-x64 --self-contained -o C:\rehearsal\tool`, and put `C:\rehearsal\tool` on `PATH`. Every later step runs from that checkout | `agent-bridge-rehearsal --help` lists every command | `agent-bridge-rehearsal report --step A-01` | |
| **A-02** | | VM-W | Rename the computer `REHEARSAL-W`. Set up Docker with the compose file, onboarding, MQTT integration and long-lived token from ha-connector.md › Validation plan, using the live instance's HA version. Write the token to `C:\rehearsal\ha.token` | `/api/states` answers with that token | `agent-bridge-rehearsal ha --token-file $ha --step A-02` | |
| **A-03** | | VM-M | Rename the computer `REHEARSAL-M`. Create user `rehearsal` (standard user). Install Copilot CLI, Claude Code and Codex, each signed in. Create `$repo` as a clone of a small local bare repository, so `origin` can be fetched | `copilot --version`, `claude --version`, `codex --version` and `git -C $repo fetch` succeed | `agent-bridge-rehearsal note --step A-03 --result pass --detail "<versions>"` | |
| **A-04** | | VM-M | Start the tunnel `ssh -N -L 8123:127.0.0.1:8123 rehearsal@VM-W` in its own window, and copy the token file across | Home Assistant answers on VM-M's loopback | `agent-bridge-rehearsal ha --token-file $ha --step A-04` | |
| **A-05** | | Both | Snapshot both VMs: **S0** | Snapshots listed in Hyperloop | `agent-bridge-rehearsal note --step A-05 --result pass --detail "S0 taken"` | |

## Phase B: agent-ha-bridge, as the machines run it today

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **B-01** | | VM-W | Install the agent-ha-bridge release the live machines run, pointed at `http://127.0.0.1:8123` with the disposable token. Make VM-W the publication writer with the README's explicit bootstrap | The dashboard `/agent-decisions` exists, and the writer is online without duplicates | `agent-bridge-rehearsal ha --token-file $ha --slug rehearsal_w --expect-online --expect-no-duplicates --step B-01` | |
| **B-02** | | VM-M | As `rehearsal`: install the same release as a participant (`install.ps1`, then `claude/install-claude.ps1` and `codex/install-codex.ps1`). Add workspace **Rehearsal** = `$repo`, isolated (`install.ps1 -Workspace`) | The machine is online on the disposable instance; nothing is numbered `_2` | `agent-bridge-rehearsal ha --token-file $ha --slug $m --expect-online --expect-sessions 0 --expect-no-duplicates --timeout 120 --step B-02` | |
| **B-03** | | Tool (VM-M) | Check G5 sees agent-ha-bridge | Active, naming the daemon task | `agent-bridge-rehearsal g5 --expect active --step B-03` | |
| **B-04** | | Machine, HA | In `$repo`, start `copilot` and type prompt **P1**. Then run `$cp = agent-bridge-rehearsal newest copilot` (from the transcripts: no host is installed yet) and `agent-bridge-rehearsal mark $cp`. Answer **rehearsal-old** from the HA card | The old bridge delivers the answer and Copilot records it | `agent-bridge-rehearsal expect answer $cp --text rehearsal-old --step B-04` | |
| **B-05** | | Machine, HA | Launch from the HA launch card into **Rehearsal** with prompt **P11**, then `/exit` both sessions at their terminals | An isolated worktree marked by agent-ha-bridge | `agent-bridge-rehearsal worktrees $repo --expect-old-trees 1 --step B-05` | |
| **B-06** | | Tool (VM-M) | With no session running, hash what agent-ha-bridge installed | The baseline is written; the tool prints file and task counts per category | `agent-bridge-rehearsal baseline save --out $base --step B-06` | |
| **B-07** | | VM-M | Snapshot VM-M: **S1** (agent-ha-bridge installed, baseline taken) | | `agent-bridge-rehearsal note --step B-07 --result pass --detail "S1 taken"` | |

On the first run, compare the baseline again after five idle minutes:
`agent-bridge-rehearsal baseline compare --baseline $base --step B-08`.

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **B-08** | | Tool (VM-M) | The baseline again after five idle minutes | Identical | The command above | |

Any file the daemon changes on its own is volatile. Add it to the list below and pass it
with `--ignore` from then on. `runtime/`, `logs/` and `*.log*` are already excluded.

Volatile files found so far: none recorded yet (fill in after the first run).

## Phase C: agent-bridge beside it, read-only

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **C-01** | §14 "One-step per-user install", §14 "Own names" | App | Run the build under test's `Setup.exe` as `rehearsal` (no elevation). Sign in with GitHub. In Settings, start the host at sign-in | The host runs as `rehearsal`, unelevated. Its task is `AgentBridgeHost…`, never `AgentBridgeDaemon…` | `agent-bridge-rehearsal host --expect-read-only --timeout 60 --step C-01`; screenshot of Settings | |
| **C-02** | §13 "Never writes" | Tool | G5 keeps the host read-only while the daemon runs | Reading only; no answer, reply, end or launch is offered | `agent-bridge-rehearsal host --expect-read-only --expect-cap sessions.read --expect-no-cap sessions.answer --expect-no-cap sessions.reply --expect-no-cap sessions.stop --expect-no-cap sessions.launch --step C-02` | |
| **C-03** | §13 "Never writes" | Tool | Leave it running for five minutes with a Copilot session open, then end the session | Not a byte of agent-ha-bridge's install, hooks or agent config changed | `agent-bridge-rehearsal baseline compare --baseline $base --step C-03` | |
| **C-04** | §17 "Only the user" | PWA, Phone | Sign the PWA in on VM-W and on the iPhone, with the same account | Both list REHEARSAL-M | Screenshot of the PWA's machine list: `agent-bridge-rehearsal note --step C-04 --result pass --detail "listed on both" --screenshot <file>` | |
| **S-01** | §13 "Copilot sessions from live locks" | Tool (VM-M) | **Optional, before migrating**, with agent-ha-bridge running: `agent-bridge-host shadow once`, then `agent-bridge-host shadow run` through at least an hour of use with a session of each agent, then `agent-bridge-host shadow summary --days 1` | `once` exits 0. Today's verdict is `clean`, or each mismatch is listed and explained | `agent-bridge-rehearsal note --step S-01 --result pass --detail "<verdict, minutes compared, mismatches by kind>"` | #40 |

## Phase D: migrate

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **D-01** | §14 "Migrate an existing" | VM-W | Install agent-bridge on VM-W too, and run `agent-bridge-host migrate --dry-run` there | Refused: VM-W is the publication writer (G7). Nothing changes | `agent-bridge-rehearsal note --step D-01 --result pass --detail "<refusal text>"` | #38 |
| **D-02** | §14 "Migrate an existing" | VM-M | `agent-bridge-host migrate --dry-run` | Lists the task to disable, the hook fallback to move and each `newSession.*` key with its `launch.*` target. Keys with no equivalent (`usage.*`, `homeAssistant.*`, `answerButtons.*`, …) are reported as kept in the old file. Nothing changes | `agent-bridge-rehearsal baseline compare --baseline $base --step D-02` | #38 |
| **D-03** | §14 "Migrate an existing" | VM-M | `agent-bridge-host migrate` | Exit 0. The daemon stopped; its task is **disabled, not deleted** | `agent-bridge-rehearsal g5 --expect inactive --timeout 30 --step D-03` | #38 |
| **D-04** | §14 "Migrate an existing" | Tool | What changed | Only hook registrations, agent config and the tasks (disabled). `~/.agent-ha-bridge` is untouched | `agent-bridge-rehearsal baseline compare --baseline $base --allow hooks --allow agents --allow tasks --step D-04` | #38 |
| **D-05** | §14 "configure:" | VM-M | `agent-bridge-host workspace list` and `agent-bridge-host config show` | **Rehearsal** imported with its path and isolation. The default agent, permissions and worktree settings match the old `newSession.*` | `agent-bridge-rehearsal note --step D-05 --result pass --detail "<imported keys>"` | #38 |
| **D-06** | §12 "A native hook spools", §12 "PowerShell fallback" | Tool | The host now delivers | Every capability, and delivering | `agent-bridge-rehearsal host --expect-delivering --timeout 60 --expect-cap sessions.answer --expect-cap sessions.approve --expect-cap sessions.reply --expect-cap sessions.attach --expect-cap sessions.stop --expect-cap sessions.launch --step D-06` | #38, #37 |
| **D-07** | §18 "Per-machine online" | VM-M | `agent-bridge-host config set homeAssistant.baseUrl http://127.0.0.1:8123`, `… homeAssistant.disposable true`, `… homeAssistant.enabled true`, `agent-bridge-host ha token set`, then restart the host from the app | The connector is on. The machine stays online on the disposable instance with the same ids, none numbered `_2` | `agent-bridge-rehearsal ha --token-file $ha --slug $m --expect-online --expect-no-duplicates --timeout 120 --step D-07` | HA1 |

## Phase E: parity on the migrated machine

Sessions run in `$repo` on VM-M unless a step says otherwise. `expect` and `log` read only
what came after the latest `mark`, so mark the session the step acts on (which also marks the
host log) before doing anything. Unless a step says to act while the session works, start
each step with the session it acts on (`$cp`, `$cl` or `$cx`) idle and no question or prompt
open: wait with `agent-bridge-rehearsal sessions --id $cp --status idle --timeout 120` (or
`$cl`, `$cx`). E1-01 starts the Copilot session the phase uses. Right after it, type prompt
**P0** in that session and set `$cp`:

```powershell
$cp = agent-bridge-rehearsal sessions --agent copilot --id-only --timeout 30   # the only Copilot session running
```

### E1. Session cards

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E1-01** | §1 "A session appears", §13 "Copilot sessions from live locks" | Machine, Tool | Start `copilot` in `$repo` and wait, typing nothing | Listed within 5 s, idle, before it has written a transcript | `agent-bridge-rehearsal sessions --agent copilot --status idle --expect-min 1 --timeout 5 --step E1-01` | |
| **E1-02** | §1 "Status working", §1 "Glow:", §1 "Current tool" | Machine, App, PWA | Type **P2** | Working with a blue glow and the current tool shown, then idle with the response | `agent-bridge-rehearsal sessions --id $cp --status working --timeout 30 --step E1-02`, then `agent-bridge-rehearsal sessions --id $cp --status idle --timeout 120 --step E1-02b`; screenshots of both cards while working | |
| **E1-03** | §1 "Current tool", §1 "Reasoning and the tool history", §1 "detailedActivity: false", §13 "Byte-level LF framing" | App, PWA | Open the tool history. Turn "show reasoning and tools" off, then on | The response renders as Markdown (heading, list, code). The history lists P2's tools. The app's toggle hides reasoning and tools; there is no HA helper | `agent-bridge-rehearsal note --step E1-03 --result pass --detail "…" --screenshot <file>` | |
| **E1-04** | §1 "A subagent", §16 "Windowless child" | Machine, App | `agent-bridge-rehearsal mark $cp`, then type **P8** | The subagent's words and tools reach the card. Title and model stay the session's. No console window flashes on VM-M | `agent-bridge-rehearsal note --step E1-04 --result pass --detail "…" --screenshot <file>` | |
| **E1-05** | §1 "An idle session waiting" | Machine, App, PWA | Type **P7** | Idle but delegating: a steady edge, with the shell counted | `agent-bridge-rehearsal sessions --id $cp --expect-background --timeout 60 --step E1-05`; screenshot | |
| **E1-06** | §1 "Compaction count" | Machine | Type `/compact` | One compaction, shown beside the status | `agent-bridge-rehearsal sessions --id $cp --expect-compactions 1 --timeout 120 --step E1-06` | |
| **E1-07** | §1 "Model shown" | Machine | `/model` and pick another model (written below as `<model>`) | The card's model follows | `agent-bridge-rehearsal sessions --id $cp --model <model> --timeout 30 --step E1-07` | |
| **E1-08** | §1 "Fold a card", §1 "Glow:" | PWA, App, Machine | Fold the card, reload the PWA, then type **P1** at the machine. Turn Windows' "Animation effects" off. Then answer the question at the terminal | The card stays folded across the reload, and the question unfolds it once. With animation off the glow holds still and the spinner stops | `agent-bridge-rehearsal note --step E1-08 --result pass --detail "…" --screenshot <file>` | |
| **E1-09** | §1 "Long text is bounded" | Machine, PWA | Type **P6** | The response is cut to the budget with a marker. The session stays usable | `agent-bridge-rehearsal note --step E1-09 --result pass --detail "…" --screenshot <file>` | |
| **E1-10** | §17 "Hostile names" | Machine, App, PWA | Start a second Copilot session with prompt **P10**, and set `$cp2 = agent-bridge-rehearsal newest copilot` | The title shows as text in the app, the PWA and a toast. Nothing runs and no link is live | `agent-bridge-rehearsal note --step E1-10 --result pass --detail "…" --screenshot <file>` | |
| **E1-11** | §1 "Claude and Codex sessions are listed", §1 "The same for Claude" | Machine, Tool | Start `claude` with **P0c** and `codex` with **P0x** in `$repo`. Set `$cl = agent-bridge-rehearsal sessions --agent claude --id-only --timeout 30` and `$cx = agent-bridge-rehearsal sessions --agent codex --id-only --timeout 30` | Both listed, working then idle | `agent-bridge-rehearsal sessions --agent claude --status idle --expect-count 1 --timeout 120 --step E1-11`, and the same with `--agent codex --step E1-11b` | #37 or #59, #62; Cl/Cx live |

### E2. Questions and answers

Mark before each: `agent-bridge-rehearsal mark $cp`.

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E2-01** | §2 "Free-text ask_user", §2 "Non-Latin-1", §2 "An answer counts only", §10 "A question reaches the phone" | Machine, Phone | Type **P1**. On the phone, tap the notification and answer `Grüße — “quoted” 你好 🚀` | Notified with the question; the tap opens the card. Copilot records exactly those words | `agent-bridge-rehearsal expect answer $cp --text 'Grüße — “quoted” 你好 🚀' --step E2-01` | |
| **E2-02** | §2 "Single choice", §10 "Desktop notifications" | Machine, App | Type **P3**. Answer **beta** from the toast's button | Recorded as `beta`; the toast is withdrawn | `agent-bridge-rehearsal expect answer $cp --text beta --step E2-02` | |
| **E2-03** | §2 "Forms of any number", §2 "Multi-select checkboxes", §2 "Question forms on the phone" | Machine, PWA | Type **P4**. Answer colour **green**, confirm **yes**, name `Zoë 🚀`, count **3**, extras **x** and **z** | Each field recorded as given | `agent-bridge-rehearsal expect answer $cp --field colour=green --field confirm=true --field "name=Zoë 🚀" --field count=3 --field extras=x,z --step E2-03` | |
| **E2-04** | §2 "Other (type your answer)" | Machine, PWA | Type **P5**. Answer through "Other" with `“smart” words` | The words recorded, not an option | `agent-bridge-rehearsal expect answer $cp --text '“smart” words' --step E2-04` | |
| **E2-05** | §2 "Other inside", §2 "The older question / choices format" | Machine, PWA | Type **P4o** (a form whose choice allows Other) | Other inside the form is not offered for typing; the reason is shown. The card says to answer at the machine. Answer at the machine | `agent-bridge-rehearsal note --step E2-05 --result pass --detail "…" --screenshot <file>` | |
| **E2-06** | §2 "The terminal and the remote", §10 "Answered anywhere" | Machine, PWA, Phone | Type **P3**. Answer **alpha** at the terminal, then press **gamma** on the PWA | The PWA's press is stale, and nothing is typed. The phone's notice is withdrawn | `agent-bridge-rehearsal expect answer $cp --text alpha --step E2-06`; screenshot of the stale result | |
| **E2-07** | §2 "One typing attempt", §2 "A resent request", §14 "Restarted after a crash" | Machine, PWA, Tool | Type **P1**. On the PWA send `rehearsal-once`, and while it is being typed restart the host from the app. After the restart, send it again | Typed once: exactly one answer recorded, and the words never arrive a second time as a prompt. The second send returns the first outcome. The host is back within a minute | `agent-bridge-rehearsal expect answer $cp --text rehearsal-once --expect-once --step E2-07`, then `agent-bridge-rehearsal host --expect-delivering --timeout 60 --step E2-07b` | |
| **E2-08** | §2 "Draft text survives" | PWA, Machine | Type **P1**. Type words in the PWA's answer box without sending. Restart the host from the app. Then answer the question at the terminal | The words are still in the box after the restart. Once the question is answered at the machine, they are kept and marked as not used as the answer | `agent-bridge-rehearsal note --step E2-08 --result pass --detail "…" --screenshot <file>` | |
| **E2-09** | §2 "Claude AskUserQuestion", §2 "Claude's review screen" | Machine, PWA | `agent-bridge-rehearsal mark $cl`, then type **P12** in Claude. Answer **beta** from the PWA | Recorded by Claude, after exactly one more Enter on its review screen | `agent-bridge-rehearsal expect answer $cl --field "Pick one=beta" --step E2-09` | #37, Cl/Cx live |

### E3. Permission prompts

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E3-01** | §3 "Copilot permission prompts" | Machine, PWA | `agent-bridge-rehearsal mark $cp`; type **P9**; approve once from the PWA | Recorded approved; the command runs | `agent-bridge-rehearsal expect permission $cp --decision approved --step E3-01` | |
| **E3-02** | §3 "Copilot permission prompts" | Machine, App | Mark; type **P9**; deny from the app | Recorded as denied (or cancelled and then the turn stopped) | `agent-bridge-rehearsal expect permission $cp --decision denied --step E3-02` | |
| **E3-03** | §3 "A machine can turn remote approval off", §3 "A prompt only the machine" | App, PWA | In the app's Settings turn "approve from other devices" off and restart. Type **P9** | The PWA shows the prompt with no buttons, marked as waiting at the machine. The phone is notified the same way | `agent-bridge-rehearsal sessions --id $cp --expect-answer-at-machine --timeout 30 --step E3-03`; screenshot. Then approve it at the terminal, turn the setting back on and restart | |
| **E3-04** | §3 "Codex approvals" | Machine, PWA | `agent-bridge-rehearsal mark $cx`. In Codex, type **P13**, then approve from the PWA | `y` then Enter is typed. The outcome is `unconfirmed`, since Codex records no answer. The command runs | `agent-bridge-rehearsal expect answer $cx --step E3-04` (exits 3, left to a person), then `agent-bridge-rehearsal note --step E3-04b --result pass --detail "…"` | #37, Cl/Cx live |
| **E3-05** | §3 "Claude permission prompts" | Machine, PWA | In Claude, type **P9**. After the check, approve it at Claude's terminal | Shown as waiting at the machine, with nothing to press. Once approved there, Claude goes idle | `agent-bridge-rehearsal sessions --id $cl --expect-answer-at-machine --timeout 30 --step E3-05`, then `agent-bridge-rehearsal sessions --id $cl --status idle --timeout 120 --step E3-05b` | #37, Cl/Cx live |

### E4. Replies and attachments

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E4-01** | §4 "Reply to a session", §4 "Ctrl/Cmd+Enter" | PWA | Mark; type `rehearsal reply one` then a new line with Enter, then `two`, then send with Ctrl+Enter | One prompt, the line break as a space | `agent-bridge-rehearsal expect reply $cp --text "rehearsal reply one two" --step E4-01` | |
| **E4-02** | §4 "Reply to a working session" | Machine, App | Type **P6**. While it works: mark, confirm it is working, and reply `rehearsal steer` from the app | Copilot takes it during the turn, as a steering or queued prompt. The delivery alone does not prove the turn was running, since Copilot records interactive prompts as steering even when idle; the working check just before the reply does | `agent-bridge-rehearsal sessions --id $cp --status working --step E4-02`, then `agent-bridge-rehearsal expect steering $cp --text "rehearsal steer" --step E4-02b` | |
| **E4-03** | §4 "A leading / or !" | PWA | Reply `/help`, then ` !dir` | Both refused; nothing typed | `agent-bridge-rehearsal note --step E4-03 --result pass --detail "…" --screenshot <file>` | |
| **E4-04** | §4 "A reply waits" | Machine, PWA | Type **P1**; while the question is open, reply `rehearsal waits`. Then answer the question at the terminal | The reply is refused while the question is open. Once answered, the session goes idle | `agent-bridge-rehearsal note --step E4-04 --result pass --detail "…" --screenshot <file>`, then `agent-bridge-rehearsal sessions --id $cp --status idle --timeout 60 --step E4-04b` | |
| **E4-05** | §4 "Paste an image", §4 "Attachments staged" | PWA | Mark. Reply **P14** with a 100 KiB PNG pasted and a text file attached | Each file mentioned as `@path` ahead of the words; Copilot attaches both | `agent-bridge-rehearsal expect reply $cp --text "Describe the attached files in one line each." --attachments 2 --step E4-05`, then `agent-bridge-rehearsal staging --expect-min-files 2 --step E4-05b` | |
| **E4-06** | §4 "Images of any size" | Phone, App | In this order: (1) check the app's capability; (2) mark `$cp`, reply **P14** from the phone with a full-size photo (several MB) and a 2 MiB file, and check it; (3) only then mark `$cp` again, reply **P14** from the app with a 20 MiB file, and check that | Each arrives whole, not shrunk: through the blob store from the phone, over the pipe from the app | (1) `agent-bridge-rehearsal host --expect-cap sessions.attach.direct --step E4-06`. That is the pipe; the blob store is offered only to devices through the relay, so (2) is its evidence; (2) `agent-bridge-rehearsal expect reply $cp --text "Describe the attached files in one line each." --attachments 2 --step E4-06b`; (3) `agent-bridge-rehearsal expect reply $cp --text "Describe the attached files in one line each." --attachments 1 --step E4-06c` | Blob |
| **E4-07** | §4 "Replies to Claude and Codex", §4 "Attachments to Claude and Codex" | PWA | Mark `$cl` and reply `rehearsal claude reply`; mark `$cx` and reply `rehearsal codex reply` | Each takes the prompt | `agent-bridge-rehearsal expect reply $cl --text "rehearsal claude reply" --step E4-07`, `agent-bridge-rehearsal expect reply $cx --text "rehearsal codex reply" --step E4-07b` | #41 |

### E5. Ending a session

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E5-01** | §5 "One press when idle", §5 "/exit, then terminate", §5 "Status reads ending" | PWA | `agent-bridge-rehearsal mark $cp2` (the idle session from E1-10), then press End session once on its card | `ending`, then gone. Copilot recorded its shutdown | `agent-bridge-rehearsal expect shutdown $cp2 --step E5-01`, then `agent-bridge-rehearsal sessions --id $cp2 --expect-gone --timeout 30 --step E5-01b` | |
| **E5-02** | §5 "One press when idle", §5 "A lapsed confirmation" | Machine, PWA | Type **P6** in `$cp`; press End session once; wait 12 s; press again | "not confirmed"; the session keeps working | `agent-bridge-rehearsal sessions --id $cp --status working --step E5-02`; screenshot | |
| **E5-03** | §5 "A session asking a question is terminated", §5 "Closes the console window" | Machine, App | Start a Copilot session, type **P1**, and press End session twice from the app | Terminated without `/exit` typed into the question. The window the person opened stays open; only the agent ends | `agent-bridge-rehearsal log --contains "is asking a question, so /exit is not typed" --step E5-03` | |
| **E5-04** | §5 "Ending a Claude or Codex" | PWA | End `$cl` and `$cx` (two presses if working) | Both gone | `agent-bridge-rehearsal sessions --agent claude --expect-gone --timeout 30 --step E5-04`, `agent-bridge-rehearsal sessions --agent codex --expect-gone --timeout 30 --step E5-04b` | #37, Cl/Cx live |

### E6. Launch and resume

A launch has no session to mark yet, so before each step here run `agent-bridge-rehearsal mark $cp`.
That also marks the host log, which `log` then reads from.

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E6-01** | §6 "Launch in an approved workspace", §6 "A launch's result", §7 "isolate: true" | PWA | Launch Copilot into **Rehearsal** with prompt **P11** (300 characters) | `started`, naming the session. It runs in a new worktree with the host's marker; the whole prompt arrives | `agent-bridge-rehearsal log --contains "registered as session" --timeout 60 --step E6-01`, then `agent-bridge-rehearsal worktrees $repo --expect-host-trees 1 --step E6-01b` | |
| **E6-02** | §6 "Workspaces are an explicit allowlist" | VM-M, PWA | `agent-bridge-host workspace add C:\Windows --label Bad` | Refused at the machine. The PWA offers only approved workspaces | `agent-bridge-rehearsal note --step E6-02 --result pass --detail "<refusal>"` | |
| **E6-03** | §6 "Agent: Agency", §6 "Agency profiles", §6 "Model, effort and context", §6 "Permissions: ask", §6 "Extra arguments" | PWA | Launch with each agent offered; pick a model, effort and context; choose Allow all once; set `launch.agents.copilot.extraArgs` and relaunch | Started with each setting; an option the machine does not offer is not shown. Agency profiles appear only if Agency is installed | `agent-bridge-rehearsal note --step E6-03 --result pass --detail "…"` | |
| **E6-04** | §6 "Resume: recent sessions", §6 "Live sessions are never offered" | PWA | Open Resume | Ended sessions in **Rehearsal** are offered; `$cp` (live) is not. Resume one: it reopens under its own id in its own folder | `agent-bridge-rehearsal log --contains "resumed pid" --timeout 60 --step E6-04` | #43 for the live-session guarantee |
| **E6-05** | §6 "Folder trust", §16 "Elevated targets" | PWA, Machine | `agent-bridge-host workspace add C:\rehearsal\untrusted --label Untrusted`. Launch Claude there. While it waits on trust, launch again. Then decline the trust question at Claude's window and close it. Last, start an elevated `copilot` by hand, type **P1**, answer from the PWA, then close that window | The first launch is left for the machine; the second is `busy`. The elevated session's answer is refused, saying it is elevated | `agent-bridge-rehearsal log --contains "is asking whether to trust the folder" --timeout 60 --step E6-05` | Cl/Cx live |
| **E6-06** | §6 "Codex launched with no prompt", §6 "A batch-file agent" | PWA | Launch Codex with no prompt, then Copilot with a prompt containing `" & echo pwned & "` | Codex is refused. Copilot receives the prompt as text and nothing is run | `agent-bridge-rehearsal note --step E6-06 --result pass --detail "…"` | |
| **E6-07** | §7 "Fails closed", §7 "Reclaim finished worktrees", §7 "Neither product" | PWA, VM-M | (1) From the PWA, end every session launched in E6-01 to E6-06, so none of the host's worktrees is in use. (2) `agent-bridge-host config set launch.worktrees.limit 2` and `agent-bridge-host config set launch.worktrees.idleHours 0`, then restart the host. (3) Mark `$cp`, then launch into **Rehearsal** three times, ending nothing | The first launch removes the host's finished trees before counting, and never agent-ha-bridge's tree from B-05. The first two start; the third is refused at the cap, with no fallback to `$repo` | (3) `agent-bridge-rehearsal log --contains "removed finished worktree" --timeout 60 --step E6-07`, `agent-bridge-rehearsal worktrees $repo --expect-host-trees 2 --expect-old-trees 1 --step E6-07b`, and a screenshot of the refusal. Then end the two, `agent-bridge-host config unset` both settings, and restart | #43 |
| **E6-08** | §6 "Launch from the Windows app" | App | `agent-bridge-rehearsal mark $cp`. On the app's Launch page, launch Claude into **Rehearsal** with prompt **P11**; then pick an ended session under Resume and launch it | Each press is `started` and opens the new session's card; the resumed one reopens under its own id in its own folder | `agent-bridge-rehearsal log --contains "registered as session" --timeout 60 --step E6-08` | |

### E8. Usage

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E8-01** | §8 "Copilot allowance", §8 "Bars with pace mark", §8 "Pace:" | App, PWA, HA | Open the usage view on the app and PWA; compare with the disposable HA's usage card from B | The same figures as the old card, with bars, pace and "resets in" | `agent-bridge-rehearsal note --step E8-01 --result pass --detail "…" --screenshot <file>` | |
| **E8-02** | §8 "Claude session and weekly", §8 "Codex 5-hour" | PWA | As above, for Claude and Codex | Their windows shown | `agent-bridge-rehearsal note --step E8-02 --result pass --detail "…"` | Cl/Cx live |
| **E8-04** | §8 "usage.copilotAccounts" | VM-M, PWA, HA | With a second login in `gh` on VM-M, `agent-bridge-host config set usage.copilotAccounts '["<that login>"]'` and restart serve; open the usage view | Each account is a row of its own under its login, with its own figure; the disposable HA has `sensor.agent_bridge_<slug>_usage_copilot_2` | `agent-bridge-rehearsal note --step E8-04 --result pass --detail "…" --screenshot <file>` | Two `gh` logins |
| **E8-03** | §8 "A failure is shown" | VM-M, PWA | Disconnect VM-M's network for two minutes, then reconnect | A fresh reading shows no error; the error appears only beside a reading over an hour old | `agent-bridge-rehearsal note --step E8-03 --result pass --detail "…"` | |

### E9. Machines and updates

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E9-01** | §9 "Machine row", §9 "Every machine" | PWA | Read the machine list | REHEARSAL-M with its session count, version and `(dev)` if a dev build | `agent-bridge-rehearsal host --step E9-01`; screenshot | |
| **E9-02** | §9 "The machine updates itself", §14 "Uninstall removes" | App | If a newer release than the build under test exists, update from Settings; otherwise record this step as manual. After Phase F of the last run, uninstall from Windows Settings | The update stops the host, then the app restarts on the new version. Uninstall removes the task, Run entry and toast registration, and keeps the profile | `agent-bridge-rehearsal host --timeout 120 --step E9-02` after the update; `agent-bridge-rehearsal note --step E9-02b --result pass --detail "…"` after uninstalling | |

### E10. Notifications

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E10-01** | §10 "A finished turn", §10 "One notification per session" | Phone | Type **P2** in `$cp` and in a second session; lock the phone | One notification per session, each with the start of its response | Phone screenshot: `agent-bridge-rehearsal note --step E10-01 --result pass --detail "…" --screenshot <file>` | |
| **E10-02** | §10 "Option buttons" | Phone | Type **P3**. Look at the notification, tap it, and answer **alpha** on the card it opens | The notification has no buttons; the tap opens the card (design §8). Once answered, the session goes idle | `agent-bridge-rehearsal note --step E10-02 --result pass --detail "…"`, then `agent-bridge-rehearsal sessions --id $cp --status idle --timeout 60 --step E10-02b` | |

### E11. Transfer

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E11-01** | §11 "Copy a session", §11 "Resume choices shared" | PWA | Copy `$cp` to VM-W (with agent-bridge installed there) and resume it | Forked under a new id on VM-W, with its history | `agent-bridge-rehearsal note --step E11-01 --result pass --detail "…"` | Transfer |

### E12. Hooks

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E12-01** | §12 "A native hook spools", §12 "Hook health" | VM-M | Start `claude` and `codex` again (E5-04 ended them) and set `$cl` and `$cx` as in E1-11. Run **P2** in `$cp`, `$cl` and `$cx`. Read the native hook's timings | Each hook returns in tens of ms through the spool. The host reports hook health | `agent-bridge-rehearsal note --step E12-01 --result pass --detail "<p50/p95 ms>"` | #37 |
| **E12-02** | §12 "PowerShell fallback", §12 "Copilot ask_user hook" | VM-M | Stop the host from the app; type **P1** in Copilot and answer it at the terminal; restart the host | The agents keep running with the fixed safe reply. The native prompt stays answerable. No request reaches the disposable HA from a hook | `agent-bridge-rehearsal note --step E12-02 --result pass --detail "…"` | #37 |
| **E12-03** | §12 "Claude and Codex registrations" | Tool | With the Claude and Codex sessions from E12-01 still running | The host wrote Claude's registration and Codex's approval marker, so both are listed | `agent-bridge-rehearsal sessions --agent claude --expect-count 1 --step E12-03`, `agent-bridge-rehearsal sessions --agent codex --expect-count 1 --step E12-03b` | #37 |

### E13. Discovery

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E13-01** | §13 "Huge transcripts" | Machine, PWA | Make `C:\rehearsal\big.txt` (`1..200000 \| ForEach-Object { "line $_ of the rehearsal" } \| Set-Content C:\rehearsal\big.txt`). In a new Copilot session type **P16** three times, `/exit`, and resume it from the PWA | The resumed session is listed with its title and state within seconds, however long its transcript | `agent-bridge-rehearsal sessions --agent copilot --expect-min 2 --timeout 30 --step E13-01`; then end it from the PWA | |
| **E13-02** | §13 "Incomplete discovery holds" | VM-M | With sessions running, stop and start the host three times quickly | No live session drops from the list, no resume offer includes a live session, no worktree is reclaimed | `agent-bridge-rehearsal sessions --id $cp --timeout 60 --step E13-02` after each restart | #43 |

### E14. Configuration

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E14-01** | §14 "A config that cannot be read", §14 "status, logs, restart" | VM-M, App | Corrupt `permissions.remoteApproval` in the profile's `config.json` (`"maybe"`); restart from Settings; read the log tail there | Remote approval is off, and the log says why. Settings shows the state, start/stop/restart and the log tail | `agent-bridge-rehearsal log --contains "approving Copilot permission prompts from other devices is off" --step E14-01`. Restore the file | |

### E15. Agents driving sessions

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E15-01** | §15 "A session cannot restart" | Machine | In `$cp`, type **P15**, and approve its shell command at the terminal. It runs the tool with `--try-restart` from inside the session | Refused; the host keeps running (invariant 13) | The step's own record: `agent-bridge-rehearsal host --try-restart --step E15-01` | #44 |
| **E15-02** | §15 "MCP tools", §15 "Launched sessions drive" | VM-M | `agent-bridge-rehearsal mark $cp`. From another session, use agent-ha-bridge's MCP server (still installed) to list sessions and reply `rehearsal mcp reply` to `$cp` | Works through the connector; the reply is attributed to the agent | `agent-bridge-rehearsal expect reply $cp --text "rehearsal mcp reply" --step E15-02` | HA2, #45 |

### E17. Security

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E17-01** | §17 "Secrets never logged", §17 "One target per action" | Tool | Search the host log and the evidence folder for the GitHub, relay and HA tokens. Every command names one session | No token appears anywhere | `agent-bridge-rehearsal note --step E17-01 --result pass --detail "no match"` | |
| **E17-02** | §17 "Only the user" | PWA | Sign a second PWA in with another GitHub account | REHEARSAL-M is neither listed nor reachable | `agent-bridge-rehearsal note --step E17-02 --result pass --detail "…"` | |

### E18. The old dashboard, through the HA connector

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **E18-01** | §18 "Per-machine online", §18 "Per-machine and per-session MQTT" | PWA, HA, Tool | End every session except `$cp` from the PWA, so `$cp` is the only one, then open `/agent-decisions` | The machine row is online with its count. `$cp`'s card renders status, activity, trail, compactions and background counts. No `_2` | `agent-bridge-rehearsal ha --token-file $ha --slug $m --expect-online --expect-sessions 1 --expect-no-duplicates --step E18-01`; screenshot | HA1 |
| **E18-02** | §18 "Per-machine and per-session MQTT" | Machine, HA | Start a second Copilot session with **P0**, and set `$gone = agent-bridge-rehearsal newest copilot`. Once its entities are there, `/exit` it at the machine (not `$cp`, which Phase F needs running) | It appears with its own entities. Then its card goes from the dashboard, then its entities: neither state nor registry entry is left | `agent-bridge-rehearsal ha --token-file $ha --slug $m --expect-sessions 2 --expect-session $gone --timeout 60 --step E18-02`, then `agent-bridge-rehearsal ha --token-file $ha --slug $m --expect-sessions 1 --expect-no-session $gone --timeout 180 --step E18-02b` | HA1 |
| **E18-03** | §18 "Per-machine online" | VM-W | Restart Home Assistant (`docker compose restart homeassistant`) | Online again within a minute, from retained messages | `agent-bridge-rehearsal ha --token-file $ha --slug $m --expect-online --expect-no-duplicates --timeout 120 --step E18-03` | HA1 |
| **E18-04** | §18 "Actionable phone notifications", §18 "Launch request payload", §18 "Agent attribution" | HA, Machine | Mark `$cp`, type **P1** at the machine, and answer **rehearsal-ha** from the old dashboard. Reply to `$cp` from its reply card, then type **P3** and answer it by tapping the phone notification. Launch from the launch card and end **that** session from its card. Drive one action from an agent with the agent token. `$cp` stays running | Each works as before migration, attributed to the right principal | `agent-bridge-rehearsal expect answer $cp --text rehearsal-ha --step E18-04` and screenshots | HA2 |
| **E18-05** | §18 "Per-machine online" | VM-M | G5. Mark `$cp`. Run the daemon briefly: `schtasks /change /tn AgentBridgeDaemon_<id> /enable`, then `schtasks /run /tn AgentBridgeDaemon_<id>`. Once the pause is logged, `schtasks /end /tn AgentBridgeDaemon_<id>` and `schtasks /change /tn AgentBridgeDaemon_<id> /disable`, leaving the task as migrate left it | The connector pauses and publishes nothing while the daemon runs, then resumes | `agent-bridge-rehearsal log --contains "Home Assistant connector paused" --timeout 60 --step E18-05`, then `agent-bridge-rehearsal log --contains "Home Assistant connector resuming" --timeout 120 --step E18-05b` | HA1 |

## Phase F: roll back

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **F-01** | §14 "Migrate an existing" | VM-M | Leave `$cp` running. `agent-bridge-host rollback` | Exit 0. The daemon's task is enabled and running | `agent-bridge-rehearsal g5 --expect active --timeout 60 --step F-01` | #38 |
| **F-02** | §14 "Migrate an existing" | Tool | Compare with the baseline taken before migrating | **Identical, byte for byte**: install, hooks, agent config and tasks | `agent-bridge-rehearsal baseline compare --baseline $base --step F-02` | #38 |
| **F-03** | §13 "Never writes" | Tool | The host is read-only again | Reading only | `agent-bridge-rehearsal host --expect-read-only --timeout 60 --expect-no-cap sessions.answer --expect-no-cap sessions.reply --expect-no-cap sessions.stop --expect-no-cap sessions.launch --step F-03` | |
| **F-04** | §18 "Per-machine and per-session MQTT" | HA, Tool | Open `/agent-decisions` | The daemon adopted `$cp`: its card is back with the daemon's controls and the launch card. No `_2`; nothing left unavailable | `agent-bridge-rehearsal ha --token-file $ha --slug $m --expect-online --expect-sessions 1 --expect-no-duplicates --timeout 180 --step F-04`; screenshot | HA1 |
| **F-05** | | HA | Mark `$cp`; type **P1** at the machine; answer **rehearsal-back** from the HA card | The old bridge answers as in B-04 | `agent-bridge-rehearsal expect answer $cp --text rehearsal-back --step F-05` | |
| **F-06** | | HA | Mark; reply `rehearsal old reply` from the HA reply card | Delivered by the old bridge | `agent-bridge-rehearsal expect reply $cp --text "rehearsal old reply" --step F-06` | |
| **F-07** | | HA | End `$cp` from its HA card (two presses) | Shut down | `agent-bridge-rehearsal expect shutdown $cp --step F-07` | |
| **F-08** | | VM-M | `agent-ha-bridge status` | Hooks spool to the daemon again; heartbeat fresh | `agent-bridge-rehearsal note --step F-08 --result pass --detail "<status lines>"` | #37 |
| **F-09** | | Tool | agent-ha-bridge's worktrees | The tree from B-05 is still there, untouched | `agent-bridge-rehearsal worktrees $repo --expect-old-trees 1 --step F-09` | |

## Phase G: again, from the snapshot

| Step | Rows | Actor | Do | Expect | Evidence | Needs |
|---|---|---|---|---|---|---|
| **G-01** | | VM-M, VM-W | (1) On VM-M, write run 1's report, then copy `C:\rehearsal\evidence\run-1` to VM-W (`scp -r`): restoring S1 erases everything written since it was taken. (2) Restore **S1** on VM-M. (3) Set `$env:AGENT_BRIDGE_REHEARSAL_EVIDENCE` to `C:\rehearsal\evidence\run-2` and run Phases C to F again. (4) Write run 2's report and copy it across the same way | Every step passes as in run 1, and both runs' evidence is on VM-W | (1) `agent-bridge-rehearsal report --checklist docs\parity-checklist.md --step G-01`; (4) `agent-bridge-rehearsal report --checklist docs\parity-checklist.md --step G-01b` | |
| **G-02** | | Person | Copy both runs' `report` output into the design doc's Progress section. Move the rows that passed to `Verified` in parity.md, citing "Live: rehearsal run 1/2" | parity.md updated through a PR | `agent-bridge-rehearsal note --step G-02 --result pass --detail "PR #…"` | |

## Prompts

| Id | Prompt |
|---|---|
| **P0** | `rehearsal-p0: say ok and nothing else.` |
| **P0c** | `rehearsal-p0c: say ok and nothing else.` |
| **P0x** | `rehearsal-p0x: say ok and nothing else.` |
| **P1** | `rehearsal-p1: call ask_user once with the question "Rehearsal word?" and no choices, then reply with only the word you were given.` |
| **P2** | `rehearsal-p2: run "git status" and "git log --oneline -3", then answer with a level-2 heading, a two-item list and a fenced code block.` |
| **P3** | `rehearsal-p3: call ask_user once with the question "Pick one" and the choices alpha, beta, gamma, with no free-text answer, then stop.` |
| **P4** | `rehearsal-p4: call ask_user once as a form with fields colour (one of red, green, blue), confirm (boolean), name (text), count (number) and extras (any of x, y, z), then stop.` |
| **P4o** | `rehearsal-p4o: call ask_user once as a form with fields colour (one of red, green, blue, allowing a free-text answer) and confirm (boolean), then stop.` |
| **P5** | `rehearsal-p5: call ask_user once with the question "Pick or write" and the choices one, two, allowing a free-text answer, then stop.` |
| **P6** | `rehearsal-p6: write the numbers from 1 to 2000, one per line, then stop.` |
| **P7** | `rehearsal-p7: start the shell command "Start-Sleep 180" in the background without waiting for it, say that you started it, and end your turn.` |
| **P8** | `rehearsal-p8: use a subagent to list the files in this folder, then summarise what it found in one sentence.` |
| **P9** | `rehearsal-p9: run the shell command "git status" once.` |
| **P10** | `rehearsal-p10 <img src=x onerror=alert(1)> [x](javascript:alert(1)) <script>alert(1)</script>: say ok.` |
| **P11** | A 300-character prompt starting `rehearsal-p11:` that asks for a one-line summary of the repository |
| **P12** | (Claude) `rehearsal-p12: use AskUserQuestion once with the question "Pick one" and the options alpha, beta, gamma, then stop.` |
| **P13** | (Codex, with approvals on request) `rehearsal-p13: run the shell command "git status" once.` |
| **P14** | `Describe the attached files in one line each.` |
| **P15** | `rehearsal-p15: run the shell command "agent-bridge-rehearsal host --try-restart --step E15-01" once and tell me its output.` |
| **P16** | `rehearsal-p16: run "Get-Content C:\rehearsal\big.txt" and tell me how many lines it printed.` |

## Rows not exercised

These rows have no step. Each has its reason. A row moves into a step when its issue lands.

| Rows | Why no step |
|---|---|
| §1 "Purple edge", §1 "Spinner verbs" | Missing (#46, #53). The step would only confirm absence |
| §1 "Unknown statuses" | Only seen when two versions meet: the first update after the rehearsal, E9-02 |
| §2 "Cancel request", §2 "Claude free text" | Missing (#56, #42) |
| §8 "usage.keychain" | Missing (#52) |
| §8 "Only the publication writer" | N/A: tied to the HA writer role |
| §9 "Offline machines", §9 "Update available" | Missing (#50, #51) |
| §10 "Notification styling" | N/A: HA companion app only |
| §11 "Transfers signed" | Intentionally different: signed commands replace the fleet secret |
| §12 "Register hooks" | Missing for fresh installs (#54). A migrated machine keeps agent-ha-bridge's registrations, which D-04 and F-02 check |
| §14 "Detect agents", §14 "Layout migration" | Missing (#54); N/A (migration starts from 1.33.x) |
| §16 "macOS and Linux", §16 "Dev Box keep-awake" | Missing (#48, #47); not on a Windows VM |
| §17 "A new machine joins" | Missing (#55) |
| §17 "Control requests validated" | Intentionally different: covered offline by the request tests |
| §18 "Generated Lovelace", §18 "Publication authority", §18 "Entity-registry pinning", §18 "255-character text", §18 "HA image store", §18 "HA REST and WebSocket", §18 "Pairing through HA", §18 "Detached-entity repair", §18 "The test runner's HTTP guard" | N/A: Home Assistant plumbing the design replaces. The old dashboard itself is exercised in B and F |

## The §6.3 invariants, by step

| # | Steps |
|---|---|
| 1 Transcript is the source of truth | E2-01 to E2-04, E2-09, E4-01, E4-02 |
| 2 Answers by identity | E2-06, E2-07 |
| 3 `ask_user` hook never blocks | E12-02 |
| 4 Copilot Other | E2-04 |
| 5 Control characters, elevated targets | E4-01, E6-05 |
| 6 Incomplete discovery holds | E13-02 |
| 7 Worktree isolation, cap | E6-01, E6-07 |
| 8 Reclaim only finished, marked trees | E6-07, F-09 |
| 9 Protected attachment staging | E4-05 (`staging`) |
| 10 Live sessions never resumed | E6-04 |
| 11 Folder trust never answered remotely | E6-05 |
| 12 End session one or two presses | E5-01, E5-02 |
| 13 No session restarts the host | E15-01 |
| 14 Hooks never fail the agent | E12-02 |
| 15 One target per action | E17-01, E18-01 |

## Before the DEV-VM1 cutover

This checklist is the rehearsal half of parity.md's migration readiness. Two runs passing
(G-01) satisfy "(a)". For "(b)", also run:

* the shadow week (#40);
* E13-02 (#43) and E15-01 (#44) on DEV-VM1 itself;
* Phase F there once right after the cutover.
