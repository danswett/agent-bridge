# Proposal #384: first-run setup and Settings

**Status: Decided by the owner on 2026-11-03, decisions 1–13** (option (a) in each, as
recommended; §12). The rules are in [ui.md §6.8–§6.10](../ui.md#68-readiness-384), now the spec,
with the copy in §8 and accessibility in §9; where this page and ui.md differ, ui.md wins. The
boards below stay as the reference drawings. Most of this isn't built yet: ui.md says what ships
today and marks the rest **Not built yet (#384 slice *N*)** (§11). The sectioned Settings
(§4), the remote read-only view (§4.1) and the guided setup are slice 5; the npm install card
and Install Node.js (§3.2, decision 12) are slice 3, since winget installs today; and the
Workspaces and trust editors (§3.5) are **Parked (#699)**, but for the two answer toggles
that ship.

*Written for the owner by the design track on 2026-11-03.* Issues:
[#384](https://github.com/danswett/agent-bridge/issues/384) (the scope),
[#231](https://github.com/danswett/agent-bridge/issues/231) (Settings for agents),
[#395](https://github.com/danswett/agent-bridge/issues/395) (readiness and the Settings
foundation), [#378](https://github.com/danswett/agent-bridge/issues/378) (launch and hook
repair) and [#54](https://github.com/danswett/agent-bridge/issues/54) (owned hooks).

The mock-ups are in [384-first-run/](384-first-run/), with every board on one
[contact sheet](384-first-run/contact-sheet.png). They are drawn from boards in
[`/design/canvas`](../../../design/canvas) (`Proposal-384-*.dc.html`) with
[tools/design-mockups](../../../tools/design-mockups/README.md), from the same tokens, glyphs
and icons as every other board. The demo machine is ULTRA, the Dev Box is DEV-VM1, and every
time, version and path is made up.

## 1. The problem

Today a machine becomes useful only after a person has, by hand and in the right order:
installed each CLI, signed in to each, registered Agent Bridge's hooks (and, for Codex,
trusted them in Codex), approved launch folders in `host.json`, chosen a permission mode,
signed in to Agent Bridge, and then started a session to see whether any of it worked. The
app's Settings page shows some of this, mixed together, and says "working" when a file was
written or a process is up, not when a hook actually arrived.

#384 asks for one guided first run and one permanent Settings that are the *same* editors
over the *same* host state, where readiness means evidence, and where each trust decision is
its own decision. The two concept renders (§8) found a good shape. This page fits that shape
to ui.md, and draws every state a person can meet.

## 2. Principles

1. **First run is Settings with a guide.** The six steps are the Settings editors, in order,
   with Continue. Nothing is set up in first run that can't be changed, checked or repaired in
   Settings, and Settings can reopen the guide (Resume setup, Set up again).
2. **Read before write.** Discover changes nothing. Every step that writes says what it will
   write, where, and who owns it, before it does.
3. **Working means evidence.** A check is Working only with a fresh observation: a heartbeat,
   a sign-in the CLI reports, a hook received from the test session that was started for it.
   Installed is not signed in; signed in is not entitled; registered is not trusted; trusted
   is not received.
4. **One decision per control.** Launch folders, subfolders, each CLI's folder trust, hook
   trust, the permission mode, and which devices may answer which prompts are separate rows,
   each with its own default. No control combines them, and nothing defaults to Allow all.
5. **Nothing finished is green.** There is no success colour (ui.md §3), and the owner decided
   on 2026-11-03 not to add one. Working is the neutral ring with a check; colour is spent only
   on Needs you (amber) and on Blocked and Failed (danger).
6. **Never silently interrupt.** A change applies to new sessions. Running sessions that need
   restarting say "New session needed"; nothing is stopped for them.
7. **Home Assistant is not in first run.** No step, gate or editor mentions it. Legacy
   coexistence and migration guards stay where they are; Discover only reports what they find.

## 3. The flow

Six steps, in a stepper that replaces the Settings pane while setup runs. The app rail stays,
with Settings selected, so Sessions and Machines are one click away and setup never traps the
person. Each step's row in the stepper carries its state glyph and one line of state.

| Step | State line examples | Board |
|---|---|---|
| Discover | This machine · Fresh install · Earlier setup found | [dark](384-first-run/proposal-384-discover.png), [light](384-first-run/proposal-384-discover-light.png) |
| Agents | 3 agents · Installing Codex CLI · Codex CLI install failed | [dark](384-first-run/proposal-384-agents.png), [light](384-first-run/proposal-384-agents-light.png) |
| Accounts | Signed in · 1 needs you | [dark](384-first-run/proposal-384-accounts.png), [light](384-first-run/proposal-384-accounts-light.png) |
| Hooks | Registered · Codex hook trust | [dark](384-first-run/proposal-384-hooks.png), [light](384-first-run/proposal-384-hooks-light.png) |
| Workspaces | 2 folders | [dark](384-first-run/proposal-384-workspaces.png), [light](384-first-run/proposal-384-workspaces-light.png) |
| Verify | Checking 6 of 11 · 3 checks left · Ready, local only | [dark](384-first-run/proposal-384-verify.png), [light](384-first-run/proposal-384-verify-light.png) |

Step states use the readiness glyphs (§5): the idle ring for Not started, the neutral ring
with a check for a finished step, the amber disc for a step that needs the person, the failed
ring for a step with a failure, and the progress ring while it works. The current step is the
selection pill (ui.md §2.1: selection is a quiet fill plus the Bridge accent, never a status
colour). The foot says "N of 6 steps done". The footer always has **Save and exit**, then
**Back** and **Continue** (or **Finish**), with a one-line reason beside them when Continue
means something less than "all done".

### 3.1 Discover (read only)

Title "Discover", subtitle "Read only: nothing on this machine changes until you choose".
Three groups:
- **This machine:** Bridge Host (version, Running) and Earlier setup ("Fresh install", or
  "Earlier setup found").
- **Agents:** Copilot CLI, Agency, Claude Code, Codex CLI, each Found with version and path, or
  Not found ("Not on PATH or in its usual folders"). **Scan again** re-runs detection.
- **Accounts:** the Agent Bridge account, Not signed in or Signed in.

**Migrated or legacy-managed machines** ([dark](384-first-run/proposal-384-discover-migrated.png),
[light](384-first-run/proposal-384-discover-migrated-light.png)). An info bar says "Earlier
setup found · agent-ha-bridge 1.3.22 set up this machine. Its working settings are kept",
with **Review migration**, which opens the existing reversible migration path (G4). Below it,
"Managed by agent-ha-bridge" lists what will be **Kept** (hooks that delivered recently,
approved folders). "Owned by another tool" lists config Agent Bridge will not touch: Codex
CLI hooks written by another hook manager, Blocked, **Not changed**, with **Details**. Fresh
setup never takes over residual legacy state; the person migrates or leaves it.

### 3.2 Agents, install consent, progress and failure

**What ships differs from these boards** (ui.md §6.9, §6.10): the app installs a CLI with the
Windows Package Manager (winget), through one combined consent dialog, "Install *Agent* and
its required runtimes?", with **Install CLI and runtimes** and **Cancel**; winget brings the
runtimes, so there is no separate Install Node.js step, and Agency is refused ("installed
through your organization"). An agent's row uses the creative director's #784 words:
"Installing…" with Install CLI held and **Cancel** ("Cancel CLI installation"), and "Install
failed · *reason*" from a categorised set, with no package-manager lines and no Copy details.
The npm consent card, progress lines and failure drawn below, and **Install Node.js**
(decision 12), are **Not built yet (#384 slice 3)**.

"Found agents are selected. Choose any others to install." Each agent is a checkbox row:
Found agents are checked; missing ones are unchecked and read **Not selected**. Checking a
missing agent never installs it: its row turns to **Install needed** (Needs you) and a
consent card opens under the list ([agents](384-first-run/proposal-384-agents.png)):

| Key | Value |
|---|---|
| Source | npm registry · @openai/codex, published by OpenAI |
| Runs | `npm install --global @openai/codex` |
| Needs | Node.js 18 or later · 22.11.0 found |
| Installs to | `%APPDATA%\npm` |
| Administrator | Not needed |
| Sign-in | Separate, on Accounts |

with **Install**, **Not now** and **Open the official page**, and the card's head says
"Nothing runs until you choose Install". The source is always the CLI's official package or
installer; the card names it, and the command shown is the command run.

**Installing** ([dark](384-first-run/proposal-384-install.png),
[light](384-first-run/proposal-384-install-light.png)): the row reads "Installing · 46% ·
2 min left" with a determinate bar when the package manager reports progress (indeterminate
otherwise), the progress ring, **Cancel**, and the last three lines of the package manager's
output in the code face. The footer says "Installing continues if you go on": the person may
continue to Accounts while it runs.

**Failed** ([dark](384-first-run/proposal-384-install-failed.png),
[light](384-first-run/proposal-384-install-failed-light.png)): "Install failed", "npm exited
with code 1 · 14:32", the npm error lines, **Try again** and **Copy details**. The same board
shows a **package manager unavailable** case: Claude Code checked but "npm not found · needs
Node.js 18 or later", Blocked, with **Install Node.js** (decision 12). The footer reason
is "Codex CLI won't be verified until it's installed", and Continue stays available: a failed
optional install doesn't trap the person, it only keeps Finish from counting that agent.

### 3.3 Accounts and sign-in

"Agent Bridge first, then each agent through its own sign-in." Agent Bridge's own account
comes first because the relay and devices depend on it.
- **Device code** ([dark](384-first-run/proposal-384-accounts.png),
  [light](384-first-run/proposal-384-accounts-light.png)): GitHub account, "Device code ·
  expires in 14 min", Waiting for you, with the code drawn large in the mono face,
  `github.com/login/device`, **Copy code and open the page** and **Cancel**. The app never
  asks for a password and never shows a token.
- Each CLI signs in through its own supported flow: Copilot CLI by its device flow, Codex CLI
  by "Opens codex login in a terminal", and Agency by Copilot CLI's sign-in ("Uses Copilot
  CLI's sign-in"). Agent Bridge shows what the CLI reports, never the credential.
- **Expired and entitlement** ([dark](384-first-run/proposal-384-accounts-expired.png),
  [light](384-first-run/proposal-384-accounts-expired-light.png)): Copilot CLI "Sign-in
  expired · last worked Oct 31", Needs you, **Sign in again**; Agency "Blocked by Copilot
  CLI"; Codex CLI split into **account** (Signed in) and **access** ("This account's plan
  doesn't include Codex", Blocked, **Check plan**). Installation, sign-in and entitlement are
  always three rows.

### 3.4 Hooks

"Agent Bridge adds only its own hooks and keeps everything else."
([dark](384-first-run/proposal-384-hooks.png), [light](384-first-run/proposal-384-hooks-light.png))
- **Copilot CLI and Agency:** Registration ("Owned by Agent Bridge · …\hooks · backup
  kept", Registered), Instructions ("Owned block in copilot-instructions.md", Added), Agency
  ("Reports through Copilot CLI's hooks", Shared).
- **Codex CLI:** Registration (plugin agent-bridge, Registered), **Hook trust** ("Codex asks
  you to trust a new hook. Agent Bridge never answers this.", Not trusted, Needs you, **Open
  Codex**), and **Hook command** ("C:\Program Files\Agent Bridge\hook.exe · quoted for
  Codex", Runs): a command path with spaces is quoted and executed once before it's written;
  a command known not to run is never installed (#384 Safety).
- An info bar, **New session needed**: "2 Copilot CLI sessions started earlier keep running
  without hooks", with **Show sessions**. They are not stopped.

Registration is serialized, re-reads the file before writing, keeps unrelated hooks, settings,
instruction text, line endings and a backup, and refuses foreign ownership (#54, #378).

### 3.5 Workspaces and policy

"Separate decisions, each with its own default. None of them is Allow all."
([dark](384-first-run/proposal-384-workspaces.png), [light](384-first-run/proposal-384-workspaces-light.png))
Four groups, each a different decision with a different owner:

| Group | Row | Default | Who decides, and where it's enforced |
|---|---|---|---|
| Folders Agent Bridge may start sessions in | each approved folder, **Subfolders** Off, **Remove**; **Add folder** | none until added; exact folder only | Bridge launch authorization (`launch.workspaces`, host) |
| Each agent's own folder trust | Copilot CLI and Agency; Codex CLI: "Asks in the CLI" | the CLI's own | The CLI. Agent Bridge reports it and never pre-trusts |
| Permission prompts | New sessions: **Standard prompts** | Standard prompts (`launch.permissions: ask`) | Host, per launch |
| Answers from my devices | "Let this app and my devices approve Copilot permission prompts"; "Answer folder-trust questions from my devices"; paired devices | On (today's `remoteApproval` default, decision 6); **Off**; "No devices paired", **Pair a phone** | Host, through the existing approval path |

`C:\Projects\My Tools` shows a path with a space, which every launch quotes. System folders
are refused as today. Subfolders is new: the host approves exact folders only, so the toggle
needs host support before it can ship (decision 7).

### 3.6 Verify and finish

**Running** ([dark](384-first-run/proposal-384-verify.png),
[light](384-first-run/proposal-384-verify-light.png)). Verify starts a real test session for
each selected agent, and only after the person allows it. The **Test sessions** card says
where (Folder `C:\Projects\sample`), which (Copilot CLI, Agency, Codex CLI), the first message
("Reply with the word ready. Don't change any files."), **Close the test sessions
afterwards** (checked, decision 8), and who allowed it ("By you, in this app, at
14:35"). Checks then run in order, each a row: Bridge Host (heartbeat), Relay, each agent
("Launched 14:35:12 · seen 14:35:15", Session seen), then its hook ("Waiting for a hook from
this test session", Checking). Agents queue one at a time ("Waits for Agency"). The footer
says "Checking 6 of 11" with Finish disabled.

**Blocked finish** ([dark](384-first-run/proposal-384-blocked.png),
[light](384-first-run/proposal-384-blocked-light.png),
[high contrast](384-first-run/proposal-384-blocked-contrast.png)). "Finish waits for 3 checks ·
Fix them, or deselect what you don't need." The failing checks are grouped first under
**Needs you**, each with its one fix:
- Codex CLI: hook trust, Not trusted, **Open Codex**;
- Codex CLI: hook (indented under it), "No hook from test session 7f3c · blocked by hook
  trust", Blocked;
- Relay, "wss://relay.example.net timed out after 10 s · 14:38", Failed, **Try again** and
  **Use local only**.

**Working** checks follow, with **Check again**. The footer reason is "1 needs you, 1
blocked, 1 failed", and Finish stays disabled. Deselecting Codex on Agents, or choosing local
only, are the two honest ways past it; neither is shown as success.

**Ready** ([dark](384-first-run/proposal-384-ready.png),
[light](384-first-run/proposal-384-ready-light.png)). "Ready on this machine · Checked 14:41 ·
3 test sessions closed", with a **Local only** chip in the header (decision 9). Agents
each Working with their test session and hook times; Claude Code **Not selected** ("Not part
of this setup"); This machine (Bridge Host "starts when you sign in", Launch folders 2
approved); Other devices: Relay **Off** ("You chose local only · sessions stay on this
machine") and Phone **Not paired** with **Pair a phone** ("Pair later in Settings"). Finish is
enabled because every *required* check passed; nothing skipped reads as Working.

### 3.7 Save and exit, and resume

**Save and exit** is on every step but Ready. It saves the choices and the unfinished work,
never a "done" flag: on return, every check runs again. The app opens on Settings ›
Overview, which says "Setup isn't finished · Verify hasn't run for Codex CLI" with **Resume
setup**, and the pane's foot keeps **Resume setup · 5 of 6**
([overview](384-first-run/proposal-384-settings-overview.png)). Resume opens the first step
that isn't finished.

An install interrupted by a crash, sign-out or reboot resumes as a fresh check, not a replay:
the step reads "Install interrupted" (Needs you) with **Check again**, and an install that
completed meanwhile is simply Found. Retries never duplicate a registration (#384 Safety).

## 4. Settings

The same Settings page the app has, reorganised into five sections in a `NavigationView`
pane (ui.md §7):

| Section | What it holds | First-run step it edits | Board |
|---|---|---|---|
| **Overview** | Readiness of everything, grouped This machine / Agents / Other devices; Check again; Diagnostics; Resume setup when unfinished | Verify (the same checks, without test sessions unless asked) | [dark](384-first-run/proposal-384-settings-overview.png), [light](384-first-run/proposal-384-settings-overview-light.png) |
| **Agents** | One tab per agent: Installation (Executable, Home), Account (Sign-in, Access), Hooks (Registration, Hook trust, Last hook, Running sessions); Run test session, Repair hooks, Remove bridge integration | Discover, Agents, Hooks | [dark](384-first-run/proposal-384-settings-agents.png), [light](384-first-run/proposal-384-settings-agents-light.png) |
| **Accounts** | Agent Bridge (GitHub account, Relay, this machine's name), each agent's sign-in, Devices | Accounts | [dark](384-first-run/proposal-384-settings-accounts.png), [light](384-first-run/proposal-384-settings-accounts-light.png) |
| **Workspaces and trust** | The four groups of §3.5, unchanged | Workspaces | [dark](384-first-run/proposal-384-settings-workspaces.png), [light](384-first-run/proposal-384-settings-workspaces-light.png) |
| **General** | Bridge Host (Restart, Stop, start at sign-in, Dev Box keep-awake), This app (start at sign-in, notifications, updates, pre-releases, version), Troubleshooting (Host log, Set up again) | none (these have defaults) | [dark](384-first-run/proposal-384-settings-general.png), [light](384-first-run/proposal-384-settings-general-light.png) |

- **Overview is Health.** The concept had both; one readiness list is enough, and Overview is
  where the nav badge leads (decision 5). An agent row opens its Agents tab; a detected
  but unselected agent says **Not selected** with **Set up** (#384: later-installed agents are
  offered, never silently opted in).
- **Agents tabs** carry an amber dot when that agent needs the person, and the nav item the
  same badge. **Remove bridge integration** removes only Agent Bridge's registrations and
  blocks; uninstalling a CLI or its credentials is never on this page.
- **Dev Box keep-awake** appears only on a Dev Box ("DEV-VM1 · Dev Box"), with its
  consequences on the row: "Signs in with az login · an awake Dev Box keeps billing", Off by
  default.
- **Changes revalidate:** editing a folder, the permission mode or a hook re-runs the checks
  it affects, and Running sessions reads "New session needed · 1 session started before this
  change" ([Agents](384-first-run/proposal-384-settings-agents.png)).

### 4.1 Remote clients

The PWA (and later Apple) shows a machine's Settings and health **read only**
([phone, dark](384-first-run/proposal-384-phone.png),
[light](384-first-run/proposal-384-phone-light.png)): "ULTRA · Online · checked 14:52 · read
only", a note "Change these on ULTRA · Settings in the Windows app", then Agents, This machine
and Trust (Permission prompts: Standard prompts; Folder-trust answers: Off). A row that needs
the person says "Needs you on ULTRA". No control is drawn that the device can't use. An action
appears remotely only where an existing explicit approval path already permits it (#67).

## 5. Readiness: one model, six states

Every check is one row: glyph, name, reason with its last observation, the state's words, and
at most one or two fixes. The states, drawn on the
[states board](384-first-run/proposal-384-states.png)
([light](384-first-run/proposal-384-states-light.png)):

| State | Glyph (existing) | Colour role | Words colour | Counts toward Finish |
|---|---|---|---|---|
| Not selected | `ended` (dotted ring) | neutral | text3 | Never counted, never success |
| Missing | `idle` (thin ring) | neutral | text2 | Not passed |
| Needs action | `needs` (filled disc) | needs | amber text | Not passed |
| Working | `done` (ring with a check) | neutral | text2 | Passed, with its evidence |
| Blocked | `failed` (ring with ×) | danger | danger text | Not passed; names what blocks it |
| Failed | `failed` (ring with ×) | danger | danger text | Not passed |
| *Checking* (transient) | `working` (progress ring) | active | text2 | Not yet |

Blocked and Failed share a shape on purpose: both are "not working and not yours to answer in
this row"; the words say which, and Blocked names its blocker ("Blocked by hook trust"). That
reuse is decision 2.

### 5.1 The state matrix

Words are examples from the boards; each row also carries its last observation time.

| Dimension | Not selected | Missing | Needs action | Working | Blocked | Failed |
|---|---|---|---|---|---|---|
| Executable, version, home | Not selected | Not found · *Install…* | Update needed: 0.98 is too old · *Update* | Found 1.0.3 | Blocked: npm not found · *Install Node.js* (not built yet, #384 slice 3) | Install failed · *reason* · *Install CLI* (#784) |
| Sign-in | Not selected | Not signed in · *Sign in* | Sign-in expired · *Sign in again* | Signed in as danswett | Blocked by Copilot CLI | Sign-in failed · *Try again* |
| Access (entitlement) | Not selected | Not checked | Choose a plan · *Check plan* | Plan includes Codex | No access · *Check plan* | Couldn't check: 503 · *Check again* |
| Ownership | Not selected | Nothing registered | Earlier setup to migrate · *Review migration* | Owned by Agent Bridge | Owned by another tool · *Details* | Couldn't read hooks.json · *Check again* |
| Registration | Not selected | Not registered · *Register* | Out of date · *Repair hooks* | Registered | Command can't run here | Couldn't register · *Try again* |
| Activation and trust | Not selected | Not active | Not trusted in Codex · *Open Codex* | Trusted | Turned off by policy | Couldn't check |
| Host heartbeat | Always required | Host not running · *Start* | Restart needed · *Restart* | Heartbeat 14:52:10 | Another host is running | Host stopped at 14:20 · *Start* |
| Last hook from a test session | Not selected | No hook yet · *Run test session* | New session needed | Hook 14:35 from 7f3c | Blocked by hook trust | No hook within 2 min · *Run test session* |
| Launch authorization | Not selected | No folders approved · *Add folder* | Folder trust question waiting | 2 folders approved | System folder: not allowed | Launch failed: exit code 1 |
| Connectivity | Local only | Not signed in · *Sign in* | Reconnect | Connected 14:52 | Offline | Relay timed out after 10 s · *Try again*, *Use local only* |

Rules:
- **Working needs evidence.** A heartbeat older than its interval, a written file, a running
  process, or a hook from another session are not Working. "Last hook" is Working only for a
  hook correlated to the session started for it, and only after its last change.
- **Not selected is never success** and is drawn in text3 with the dotted ring, so a list of
  Not selected rows can't be mistaken for a list of Working ones, in colour or greyscale.
- **Required** checks for Finish: host heartbeat, launch authorization, and for each
  *selected* agent: executable, sign-in, access, ownership, registration, activation and
  trust, and last hook. Connectivity is required unless the person chose local only.

## 6. Errors and recovery

| Case | Where | What the row says | The fix |
|---|---|---|---|
| Install failed | Agents | Install failed · *reason*, from a categorised set (#784). *Not built yet (#384 slice 3):* npm exited with code 1 · 14:32, last output lines | Install CLI again. Copy details is withdrawn (#784) |
| Package manager unavailable | Agents | *Not built yet (#384 slice 3); winget brings runtimes today:* Blocked · npm not found · needs Node.js 18 or later | Install Node.js (consent card, decision 12), or Open the official page |
| Interrupted install or reboot | Agents, on resume | Install interrupted (Needs you) | Check again: finds it installed, or offers Try again |
| Auth expired | Accounts, Agents › Account | Sign-in expired · last worked Oct 31 | Sign in again (the CLI's own flow) |
| Entitlement missing | Accounts | No access · This account's plan doesn't include Codex | Check plan (opens the provider's page) |
| Offline or relay unreachable | Verify, Overview | Relay timed out after 10 s · 14:38 (Failed) | Try again, or Use local only |
| Foreign ownership | Discover, Agents › Hooks | Owned by another tool · Not changed (Blocked) | Details: whose, which file, and that Agent Bridge won't edit it |
| Untrusted Codex hooks | Hooks, Verify | Not trusted in Codex (Needs you); its hook Blocked by hook trust | Open Codex; Agent Bridge never answers the trust prompt |
| Folder-trust prompt pending | Verify, Overview | Folder trust question waiting (Needs you) | Answer in the CLI, or on a device if allowed (Off by default) |
| Path with spaces | Workspaces, Hooks | C:\Projects\My Tools · path has a space; Hook command · quoted for Codex | Quoted automatically; a command that fails its dry run is Blocked: Command can't run here |
| Running sessions after a change | Hooks, Agents › Hooks | New session needed · 1 session started before this change | None forced; Show sessions |
| Migrated machine | Discover | Earlier setup found · its working settings are kept | Review migration (G4) |

## 7. Copy

Copy follows ui.md §8: no sentences explaining the UI, numbers before nouns, no exclamation
marks or emoji, and status words from a fixed list. Setup adds its own fixed words:

- **States:** Not selected, Not found, Not signed in, Not checked, Not registered, Not
  trusted, Needs you, Working, Blocked, Blocked by *X*, Failed, Checking, Waiting for you,
  Queued, Found, Signed in, Registered, Trusted, Approved, Kept, Not changed, Not paired,
  Local only, New session needed, Install needed, Installing, Install failed.
- **Titles:** Set up; Discover; Agents; Accounts; Hooks; Workspaces; Verify; Ready on this
  machine. Settings: Overview, Agents, Accounts, Workspaces and trust, General.
- **Actions:** Continue, Back, Finish, Save and exit, Resume setup, Set up again, Scan again,
  Check again, Install, Not now, Cancel, Try again, Sign in, Sign in again,
  Open Codex, Repair hooks, Run test session, Remove bridge integration, Add folder, Remove,
  Pair a phone, Use local only.
- **Lines that are not status** are an exception to ui.md §1's principle 5, now written there:
  consent ("Nothing runs until you choose Install"), security ("Codex asks you to trust a new
  hook. Agent Bridge never answers this."), cost ("an awake Dev Box keeps billing"), and the
  step subtitles. Each is one line, factual, and states a consequence rather than explaining
  the control (decision 3).

## 8. From the concepts, and what changed

| Idea | Concept | Here | Why |
|---|---|---|---|
| Left stepper: Discover, Agents, Accounts, Hooks, Workspaces, Verify | 1 | Kept, as a pane beside the app rail | Same order as the dependencies; the rail keeps the app reachable |
| Detected agents preselected, missing unchecked, install on consent | 1 | Kept; consent is a card naming source, command, prerequisites, location, admin and sign-in | #384 needs an official source and explicit consent |
| Green checks and success badges | 1, 2 | **Changed** to the neutral ring with a check | No success colour; owner decision 2026-11-03 |
| "Ready on this machine" with Local-only badge and Phone not paired | 1 | Kept; Local only is a neutral chip, Relay reads Off | Local only is a choice, not a warning |
| Settings nav with Overview and Health | 2 | **Merged** into Overview | One readiness list; fewer places to look |
| Per-agent tabs with separate rows for executable, account, registration, trust, fresh hook | 2 | Kept, grouped Installation / Account / Hooks, with Access split from Sign-in and Running sessions added | Entitlement and staleness are their own facts |
| Run test session, Repair hooks, Remove bridge integration | 2 | Kept; Remove is a subtle button | It removes only Agent Bridge's own registrations |
| Workspaces & trust: folders, include subfolders off, CLI permissions, remote folder-trust off, approved devices | 2 | Kept, in four labelled groups; added each CLI's own folder trust | Each decision separate, with its owner named |
| Concept icons and illustration | 1, 2 | **Dropped** for the existing glyphs and icons | Not approved assets |
| Hooks inside Agents | 2 | First run has its own Hooks step | Hook trust is its own decision and needs its own moment |

## 9. Accessibility, motion and narrow windows

**Stepper.** A list (`role="list"`) of links; the current step has `aria-current="step"`. Each
step's accessible name joins its label, state and line with commas: "Hooks, needs you, Codex
hook trust". The heading level 1 is the step title; groups are level 2.

**Focus order.** Stepper (only when focused by Shift+Tab or F6), then the step title, the
step's controls in reading order, then the footer: Save and exit, Back, Continue or Finish.
When a step opens, focus lands on its title. A disabled Finish stays focusable, with its
reason as its accessible description ("Finish, unavailable, 1 needs you, 1 blocked, 1
failed").

**Live regions.** Install progress and Verify each have one polite region that announces
state changes, not percentages: "Installing Codex CLI", "Codex CLI installed", "Codex CLI
install failed", "Check 6 of 11: Copilot CLI hook, working". At most one announcement per 5 s
while a step runs, and failures are announced at once. The device code is a heading with its
text spelled for screen readers.

**Keyboard.** Tab and Shift+Tab move; arrows move within the stepper, the tabs and toggle
groups; Space toggles checkboxes and switches; Enter activates; Alt+← is Back; Escape closes
the consent card without installing. Checkboxes, switches and the combo report their state
(`aria-checked`, `aria-expanded`); every switch is named for its row ("Subfolders,
C:\Projects\sample, off").

**Contrast.** Every pair keeps ui.md §9's 4.5:1 for text and 3:1 for glyphs. In high contrast
([board](384-first-run/proposal-384-blocked-contrast.png), emulated) the neutral check ring
uses WindowText rather than GrayText (which reads as disabled), Needs you and Danger fall back
to system colours with their shapes carrying the state, and the selected step is Highlight
with HighlightText ink (decision 11).

**Reduced motion.** Only the progress ring loops, and it is still under reduced motion (ui.md
§2.1). Install bars jump to their value; the consent card and step changes cross-fade or
change instantly (ui.md §5.3). Nothing relies on motion.

**Narrow windows** (ui.md §5.2, compact). The stepper pane collapses into a line, "Step 2 of 6
· Agents", a six-segment progress bar and a **Steps** button that opens the list as a flyout
([agents](384-first-run/proposal-384-narrow-agents.png),
[light](384-first-run/proposal-384-narrow-agents-light.png)). In Settings the nav becomes a
list page and each section a pushed page with a back row "‹ Settings"
([Codex CLI](384-first-run/proposal-384-narrow-settings-agents.png),
[light](384-first-run/proposal-384-narrow-settings-agents-light.png)); an agent's tabs become
the list of agents one level up. Rows keep their actions on one line where they fit; below
about 560 px they wrap under the reason.

## 10. Every board

43 boards: 17 Windows screens in dark and light (1440×900); the Agents step and Settings ›
Agents at 720×900 in dark and light; one high-contrast board; the phone in dark and light
(390×844 at 2×); and the states board in dark and light (1440×1000). All are on the
[contact sheet](384-first-run/contact-sheet.png). Re-render with:

```
cd tools/design-mockups
npm install
node render.js ../../design/canvas ../../docs/design/proposals/384-first-run Proposal-384-
node contact-sheet.js ../../docs/design/proposals/384-first-run proposal-384- "--note=decided by the owner on 2026-11-03 · ui.md is the spec"
```

Limits: Inter stands in for Segoe UI (tools/design-mockups/README.md); high contrast is an
emulation of the Aquatic theme, not a capture; Mica is drawn as flat surfaces.

## 11. Slices

Following #384's own slices, design adds nothing new to the order: (1) the readiness model
behind Overview and Agents (#231, #395); (2) owned setup and repair (#54, #378); (3) install
and sign-in adapters; (4) the Workspaces and trust editors, reusing the existing ones; (5) the
guided shell over them; (6) Verify's correlated test sessions. Each UI slice changes ui.md
first, in its own PR, for only the parts the owner approved; ui.md §6.8–§6.10 is that change
for the decisions below.

## 12. Decisions

Each lists the recommended option first. The owner chose (a) in all 13.

1. **Working's look.** (a) **Recommended:** the neutral `done` ring with a check, text2 words;
   colour only for Needs you and Danger. (b) A new success token and colour. (a) follows the
   2026-11-03 decision; (b) would need a new role in tokens.json and ui.md §3.
   **Decided by the owner on 2026-11-03: (a).**
2. **Glyph reuse.** (a) **Recommended:** reuse `done` for Working, `idle` for Missing, `ended`
   for Not selected, and `failed` for both Blocked and Failed (words tell them apart). (b) A
   new `blocked` glyph (e.g. a ring with a bar). ui.md §2.1 today says `done` is "never used
   for sessions"; readiness rows aren't sessions, but the table would need a line.
   **Decided by the owner on 2026-11-03: (a).**
3. **Helper lines.** (a) **Recommended:** allow one-line consent, security and cost lines in
   Settings and setup only, as an explicit exception to ui.md §1.5. (b) Move them all into
   tooltips. Consent and cost must be visible before the action, so (b) weakens consent.
   **Decided by the owner on 2026-11-03: (a).**
4. **Where the stepper lives.** (a) **Recommended:** setup replaces the Settings pane, with the
   app rail kept and Settings selected. (b) A full-window wizard without the rail. (a) keeps
   "first run is Settings" literal and never traps the person.
   **Decided by the owner on 2026-11-03: (a).**
5. **Overview and Health.** (a) **Recommended:** one Overview that is the health list. (b)
   Separate Overview (summary) and Health (detail) pages, as in concept 2.
   **Decided by the owner on 2026-11-03: (a).**
6. **Remote permission approval default.** (a) **Recommended:** keep today's default On for
   "Let this app and my devices approve Copilot permission prompts", shown as its own row and
   changeable in setup. (b) Off by default on fresh installs. #384 defaults *folder-trust*
   answers Off, which this keeps; it doesn't say the existing permission-prompt default
   changes, so (b) is a behaviour change for existing users.
   **Decided by the owner on 2026-11-03: (a).**
7. **Subfolders.** (a) **Recommended:** show "Subfolders" per folder, Off, and ship it only
   once the host supports subfolder scope. (b) Leave it out until then. Today the host approves
   exact folders only.
   **Decided by the owner on 2026-11-03: (a).**
8. **Test session cleanup.** (a) **Recommended:** "Close the test sessions afterwards" checked
   by default. (b) Unchecked, so the person can look at them.
   **Decided by the owner on 2026-11-03: (a).**
9. **Local only.** (a) **Recommended:** a neutral "Local only" chip on Ready and Relay "Off";
   no warning colour. (b) An info bar explaining what local only means.
   **Decided by the owner on 2026-11-03: (a).**
10. **Remote Settings scope.** (a) **Recommended:** PWA shows readiness and trust settings read
    only, with "Needs you on *machine*", no controls. (b) Also allow "Check again" remotely
    (it starts nothing, but is still a remote action and needs its own approval path).
    **Decided by the owner on 2026-11-03: (a).**
11. **High contrast.** (a) **Recommended:** check ring in WindowText, needs and danger as
    system colours with shapes carrying the state, the selected step as Highlight. (b) Keep
    GrayText for the check ring, as ui.md uses for neutral.
    **Decided by the owner on 2026-11-03: (a).**
12. **Installing a runtime.** (a) **Recommended:** offer "Install Node.js" through the same
    consent card (official source, winget or nodejs.org), never silently. (b) Only link to the
    official page and let the person install it.
    **Decided by the owner on 2026-11-03: (a).** **Not built yet (#384 slice 3):** today winget
    installs a CLI with its required runtimes through one consent dialog, so no runtime is
    offered on its own (§3.2).
13. **Agency.** (a) **Recommended:** show Agency as its own agent that shares Copilot CLI's
    hooks and sign-in, saying so on each row. (b) Fold it into Copilot CLI.
    **Decided by the owner on 2026-11-03: (a).**
