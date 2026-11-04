# Proposal #519: cross-account collaborators in the UI

> **Decided 2026-11-03.** The owner approved the recommended option (a) for all 12
> decisions in §12 (recorded on
> [#566](https://github.com/danswett/agent-bridge/pull/566)). The decisions are promoted into
> [ui.md](../ui.md) (§2, §3, §6.1, §6.4, §6.5, the new §6.7 Collaborators, §7, §8, §9 and
> §10), and the External role into design/tokens.json. The External tint (§4.2) is decided
> but pending a design-gen change. This page stays as the record of why; ui.md wins wherever
> the two disagree.

*Written for the owner, from the design track.* Issues and specs:
[#519](https://github.com/danswett/agent-bridge/issues/519) (cross-account collaboration),
the draft design in [#522](https://github.com/danswett/agent-bridge/pull/522)
(`docs/design/agent-principals.md`, especially §5.7 per-message approval, §6.2 and §6.3
where people and machines meet, §7.3 links and revokers, §7.7 revocation, §7.11 discovery,
and §7.12 collaborators in your own app).

The mock-ups are in [519-collaborators/](519-collaborators/), with every board on one
[contact sheet](519-collaborators/contact-sheet.png). They are drawn from boards in
[`/design/canvas`](../../../design/canvas) (`Proposal-519-*.dc.html`) with
[tools/design-mockups](../../../tools/design-mockups/README.md), from the same tokens, glyphs
and icons as every other board, and in the approved Slipgates language of
[#536](https://github.com/danswett/agent-bridge/pull/558) (brand.md, PR #558): the S glyph family for session states, the ink plate,
rows at 20 px, delegates keep their rings, and plain words. The people are example logins
(@danswett is you; @welshofer, @mira-k, @octo-dev and @j-rivera are collaborators); every
machine name, time, path and code is made up.

## 1. The problem

Agent principals (#522) lets two people's Slipgates link per repository, so their agents can
read, reply to and launch sessions on each other's machines within a scope both accepted.
That changes three things the UI has never had to say:

1. **Whose session is this?** Today every row is yours. With a link, some rows belong to
   @welshofer, run on @welshofer's machines, and can't be answered, approved or stopped by
   you.
2. **Who sent this message?** Today a message is from you, from your own agent (the filled
   violet diamond, ui.md §6.4), or from the agent itself. Now a message in *your* session
   can come from *their* agent.
3. **What did I agree to?** Links, scopes, pinned workspaces, revokers, suspension and
   revocation need a home, and a few messages need a person to look at them before they're
   delivered.

None of this may weaken the rules people rely on: Needs you means *you* are needed, colour is
never the only signal, and nothing that isn't verified is shown as identity.

## 2. Principles

1. **Colour says whose; shape says state.** A collaborator's session uses the same S glyph
   for the same state as yours, drawn in a new External colour. The shape still tells you
   it's working, waiting or idle; the colour, the person header and the word "External" tell
   you it isn't yours.
2. **Never colour alone.** Every External mark sits next to the outlined diamond ◇, the word
   "External", or the collaborator's verified @login. In forced colours the colour goes and
   all three remain.
3. **Their waiting is not your Needs you.** A collaborator's session that waits on
   @welshofer is never amber, never counted, never in Needs you, never notified, never on a
   badge or tile. The only collaborator item that enters Needs you is a message that is
   waiting on *you* (§3.6).
4. **Read-only means absent, not disabled.** Read-only external sessions have no reply box,
   no action bar, no swipe actions and no context-menu verbs. One line says why, where the
   reply box would be.
5. **Identity comes only from verified fields.** Only the @login is identity: it comes from
   the GitHub user id the link verified, resolved when shown. A collaborator's machine name
   is their own label for it (agent-principals §7.3), so it's shown in italics with *their
   name* and never in bold. A sender's session title is something they typed: it's
   secondary, quoted and never styled as identity.
6. **Quiet discovery.** Collaboration suggestions are a grey dot in Settings. Never a push,
   a toast, a badge count or a Needs you item.
7. **Gone means gone.** When a link is revoked or suspended, that person's sessions leave
   every list at once, anything cached included, and a quiet notice with the reason takes
   their place.

## 3. The seven surfaces

### 3.1 Person, then machine (boards 2, 3, 4)

The list gains a person level above machines: **Mine** first, then each collaborator by
verified @login in alphabetical order, with their machines nested beneath. Machine
sub-headers under a collaborator read `WS-LAPTOP · their name`, because the name is theirs,
not one you chose (agent-principals §6.2). Person headers carry the person glyph; a
collaborator's header adds an **◇ External** tag.

The filter is "Show sessions from": Everyone (default), Mine, and one entry per collaborator
with their machines listed under the login. It never shows per-person counts, because counts
are only for Needs you.

| Shell | Control | Board |
|---|---|---|
| Windows app (≥ 1008 px) | Chips under the list header: **Everyone · Mine · @welshofer · …**; more than three collaborators fold into "More ▾" | 2 |
| PWA desktop | A "Show sessions from" menu button in the list header, ending with "Manage collaborators…" | 3 |
| PWA phone | One chip showing the current choice; tapping it opens a bottom sheet with the same list | 4 |
| HA card | No person level (§6) | 9 |

Needs you stays available in every filter, and its count only ever counts your own items.

### 3.2 External sessions are read-only (boards 1–4)

An external row is the normal 20 px row, with:

- the state's S glyph drawn in **External** (working, idle and ended keep their shapes);
- line 2 in plain words with "read-only": *Working · read-only*, *Idle · read-only*;
- a collaborator's waiting session drawn as an **outlined** S tile in External, never the
  filled amber tile, with *Waiting for @welshofer*;

Opening one shows exactly what the link's read projection carries (agent-principals §7.4,
§7.12): the title, status, the **latest response** (`activity.response`) and whether it's
waiting. Never their prompts, questions, reasoning, tool history or delegates' content. The
header reads `@welshofer · WS-LAPTOP (their name) · Working` with an **◇ External** tag;
above the response, *Latest response · 2 min ago*; below it, *The link shares the latest
response only. @welshofer's prompts, questions and tool history stay on their side.* The reply box, the action
bar, Stop, Transfer, Launch here, and swipe and context-menu verbs are **absent**. Where the
reply box would be, one line:

> 🔒 Read-only · @welshofer's session. Only @welshofer can reply, approve or stop it.

If their agent is waiting (`waiting: true`), an outlined External card says *@welshofer's
agent is waiting for @welshofer.* and *Only @welshofer can see what it asked and answer
it.* It never shows the question, and it has no buttons.

Your own session that an external agent is driving keeps your violet driven treatment
(ui.md §2.2 rule 7) and adds an **outlined** diamond in External in the badge seat, with
line 2 *◇ Driven by an external agent · @welshofer* (phone: *◇ External agent ·
@welshofer*).

### 3.3 The external-agent provenance mark (board 5)

On your own sessions' messages:

| Sender | Mark | Label |
|---|---|---|
| You | none | none |
| Your own agent (today) | filled violet ◆, violet left edge | Sent by copilot on *DESK* |
| An external agent | **outlined ◇** in External, a dashed External outline, no filled edge | **external** · Sent by an agent on *WS-LAPTOP* · **@welshofer** |
| Any agent carrying external content (§5.8 retained origins) | its own mark, plus a small outlined ◇ line | Carries **external** content from **@welshofer** |

Under the external label, secondary and quoted: *Their session: "Port the pairing tests"*.
The filled-versus-outlined contrast and the word "external" keep the two apart in forced
colours and in greyscale; the dashed outline keeps them apart for people who can't tell the
hues apart (§4.2).

Only `principal.login` is bold identity. The machine name stays in italics as their label,
as on board 5's own-agent mark. The session title is
regular weight, in quotes, after "Their session:" and is truncated with an ellipsis at one
line; its full text is in the tooltip and the accessible description.

### 3.4 "Involving external agents" (board 5)

A filter chip in the list header (in "More filters" on the phone): **◇ Involving external
agents**, with a segmented window of **1 h · 24 h · 7 days**, default 24 h. It shows your
own sessions whose `Session.external.lastAt` is within the window, and line 2 becomes
*◇ External agents: sent, read · 2 min*, from `Session.external.kinds`. There's no login on
that line because the field doesn't carry one (decision 12).

Empty state, centred in the list:

> **No external agents in the last hour**
> None sent to, read or were refused by your sessions.
> [Show 24 hours] [Clear filter]

The wider-window button is absent at 7 days.

### 3.5 Settings › Collaboration (boards 6, 7)

A new page in Settings, after Devices.

- **Discovery** is opt-in: *Find collaborators who use Slipgates on
  danswett/agent-bridge*, with the 90-day activity window shown. A suggestion appears as a
  **grey dot** on the Collaboration nav item and a card on the page, with agent-principals
  §7.11.5's copy (identity proof first, then what connecting would allow), and
  **[Review and connect] [Not now] [Never for this repo]**. No push, toast or count.
- **Links** list each collaborator and repository with what each side shares, and a status
  in words and shape:
  - **Active**: filled teal dot, *Active*.
  - **Suspended**: filled amber dot, amber *Suspended*, the reason, and the one fix
    (*Re-confirm workspaces*).
  - **Revoked**: hollow grey dot, *Revoked*, the date and who ended it.
- **History** (expanded per link) lists accepted, verified, renewed, suspended and revoked
  events, each with its cause and device. **[Message audit]** opens the per-message audit;
  **[Revoke…]** is one press plus presence (§7.7) and needs nothing from the other side.

**The link dialog** (board 7), from "Review and connect" or "Link by invitation…":

- *What @mira-k can do*: **Read status**, **Read content**, **Reply**, **Launch** (the separate
  grants of agent-principals §7.4), each with one plain line. Nothing is pre-ticked. Read
  content carries the §7.4 warning that @mira-k and their AI provider may retain what their
  agents read, and that revocation doesn't retract copies.
- *Allow-all sessions*: two separate opt-ins (read my allow-all sessions; ask me before each
  message to an allow-all session), each with its §7.11.5 warning inline in Needs you text
  colour.
- *Workspaces (pinned now)*: each workspace as it resolves at acceptance, with its path and
  remote. New workspaces are never added on their own; a changed remote suspends the link.
- *Who can end this link*: if no revoker device exists off the custodian's machine, an amber
  requirement box, *Add a device or a recovery key that can end this link*, with **[Add PHONE]**
  and **[Recovery key…]**; either one satisfies it.
- *Identity*: "Verified by @mira-k's GitHub signing key" when that proof checks. *Link code
  (optional, stronger)* with that proof, *Link code (required)* without it (§7.11.3): four word groups, `MAPLE·RIVER·OTTER·NINE`, to read
  aloud by voice or video. Matching it records "Verified" in history.
- **Accept with Windows Hello** (or the platform's presence check) stays disabled until one
  scope, one workspace and an off-machine revoker exist and the identity is verified (by the
  signing key or matching link codes). **Cancel** is always available.
- If the other side's descriptor says single machine, a side card says so, in §7.3's words.

### 3.6 Per-message approval in Needs you (board 8)

When you chose "Ask me before each message" for a link (the `gate` opt-in, off by default),
messages from that collaborator's agents to an **allow-all** session or to a session whose
mode is **unknown** are held for you (§5.7). Without the opt-in they're refused and nothing
reaches Needs you. This *is* yours to decide, so it is the one collaborator item that enters Needs
you: amber edge and counted, but never pushed on its own (agent-principals §5.7 is normative:
quiet holds are counted only, and an opted-in push is one text-free summary an hour at most).

The card is built to look unlike a tool approval:

- Title **Message from an external agent** (a tool approval's title is the action, e.g.
  *Run a command?*), the outlined ◇ and *Held · 52 min left*, counted down to the host's
  absolute expiry (60 minutes quiet, 5 to 15 pushed), never a client timer.
- A **From / To** block: *From* ◇ external · an agent on *WS-LAPTOP* (their name) ·
  **@welshofer**, with only the login bold, and their session title quoted and secondary;
  *To* your session, agent, machine and its mode.
- The **full** message text in the mono face, escaped exactly like approval commands (ui.md
  §6.4), never Markdown-rendered, in a scrollable box with a visible scrollbar, and
  *Shown exactly as sent, 294 characters. Not formatted.*
- One consequence sentence. Allow-all: *Approving makes this **your** instruction to an
  allow-all session. It can run commands on DESK without asking.* Unknown mode: *Approving
  makes this **your** instruction. Slipgates can't tell whether this session asks before it
  runs commands on DEV-VM1.*
- Actions, in this order: **Deny**, **Approve once**, **Allow @welshofer here for… ▾**
  (5, 15, 30 or 60 minutes, nothing pre-selected). No teal primary, so nothing is the easy
  default.
- After the hold: *Expired · not delivered*, removed from Needs you.
- With a window open, a quiet strip on that session: *Allowing @welshofer's agent here*,
  *11 min left · 3 messages delivered*, and **[Stop allowing @welshofer]**.

### 3.7 Revocation and suspension (board 9)

On `collaborator.removed`, the person's whole section is replaced **at once** (no fade, no
countdown) by one quiet notice: *@welshofer's sessions are hidden* and the reason line, with
**Details** opening the link's history. If one of their sessions was open, the pane becomes
*This session is no longer shared*, the reason, *What you saw of it was removed from this
device.* and **[Back to sessions]**. The notice is not a notification and never enters Needs
you.

| Cause | Reason line |
|---|---|
| They revoked the link | Revoked by @welshofer |
| You revoked the link | Revoked by you |
| GitHub write access lost | Link suspended: write access removed |
| A pinned workspace's remote changed | Link suspended: a pinned workspace's remote changed |
| The link expired (the descriptor's own expiry, 30 days unless renewed) | Link expired: @welshofer hasn't renewed it |
| Their read grant removed | @welshofer stopped sharing read |
| Federation paused on this machine | Collaboration paused on ULTRA |

## 4. The External colour

### 4.1 Why a new hue

The existing roles are taken: teal is the brand and Active, amber is Needs you, red is
Danger, violet is agent-driven, blue is your own working edge, and grey is neutral. An
orchid sits between violet and red but is far enough from both, and it reads as "someone
else" without reading as an alarm. It's never used for counts, badges, Needs you or
notifications.

### 4.2 Tokens

Decided 2026-11-03. The graphic, text and track values are in design/tokens.json as
`color.role.external` and generated into tokens.css (`--ab-external*`) and the Windows
resources (`AbExternal`, `AbExternalText`, `AbExternalTrack`). The tint is **decided but
pending**: design-gen measures every `fill*` value as a question-card surface for every role
and text colour, and light Bridge text (4.02:1) and Danger text fail on it, although no
question card ever uses this tint. It goes into tokens.json once design-gen can mark a fill
as a decorative tint.

| Token | Dark | Light | Forced colours | Status |
|---|---|---|---|---|
| `color.role.external` (graphic: glyphs, diamonds, outlines) | `#F769CF` | `#A3177F` | `CanvasText` + outline + word | In tokens.json |
| `color.role.external` `text` (@login, "External") | `#FBA8E4` | `#A3177F` | `CanvasText` | In tokens.json |
| `color.role.external` `track` (glyph track) | `#5B3A56` | `#E3C1DB` | `GrayText` | In tokens.json |
| External tint (tag and card tint) | `rgba(247,105,207,.10)` | `rgba(163,23,127,.08)` | none (`Canvas`) | Pending design-gen |

Track and row composites, for the glyph's background track: dark row 60% `#A4508F`, track
25% `#5B3A56`, fill 10% `#3C303E`; light row 60% `#C571B0`, track 25% `#E3C1DB`, fill 10%
`#F0E2ED`.

### 4.3 Measured contrast

WCAG 2.x contrast ratio, measured against the token surfaces each mark sits on. Targets:
3:1 for non-text marks, 4.5:1 for text. All pass.

| Foreground | Shell | Layer | Card | Selected on layer | Selected on card | Subtle on layer | Subtle on card | On External fill |
|---|---|---|---|---|---|---|---|---|
| Dark graphic `#F769CF` (≥ 3:1) | 5.92 | 5.41 | 4.77 | 4.36 | 3.84 | 4.65 | 4.09 | 4.67 |
| Dark text `#FBA8E4` (≥ 4.5:1) | 8.89 | 8.12 | 7.16 | 6.54 | 5.76 | 6.97 | 6.14 | 7.00 |
| Light `#A3177F`, graphic and text (≥ 4.5:1) | 6.16 | 6.68 | 7.03 | 5.98 | 6.30 | 6.26 | 6.58 | 5.65 |

Light uses one value for both graphic and text because it already passes 4.5:1 everywhere.
In forced colours the system pair (`CanvasText` on `Canvas`, `HighlightText` on `Highlight`
when selected) carries the user's own theme contrast.

### 4.4 Colour vision

CIEDE2000 ΔE between External and each existing role, normal / protan / deutan / tritan
simulation:

| vs | Dark | Light |
|---|---|---|
| Agent violet | 40 / 17 / 32 / 62 | 50 / 40 / 56 / 73 |
| Danger red | 56 / 55 / 55 / 18 | 63 / 62 / 63 / 33 |
| Needs you amber | 113 / 115 / 96 / 27 | 97 / 95 / 80 / 27 |
| Brand teal | 114 / 47 / **16** / 107 | 96 / 41 / **13** / 91 |
| Active blue | 65 / **16** / 41 / 95 | 60 / 38 / 60 / 87 |
| Neutral grey | 70 / 32 / **16** / 65 | 66 / 34 / **13** / 67 |

The bold pairs are close for some people. That's why principle 2 holds: External is always
paired with ◇, the word "External" or an @login, and an External waiting tile is
**outlined** where Needs you is filled, so no decision rests on telling these hues apart.

### 4.5 Forced colours (board 1)

External marks map to `CanvasText`. The outlined diamond, the **External** tag (a 1 px
`CanvasText` border), the dashed outline on external messages and the @login in the person
header remain; fills drop. Selected rows use `Highlight` / `HighlightText` as today.

## 5. Accessibility and copy for assistive tech

- An external row's accessible name: *External session, @welshofer, WS-LAPTOP, their name,
  Port the pairing tests, working, read-only.*
- A person header is a group with the name *@welshofer, external collaborator, 2 machines*.
  Machine sub-headers are nested groups.
- The read-only line is the pane's description, read once when the pane opens.
- An external message's name starts *External agent message from @welshofer on
  WS-LAPTOP*; the quoted title follows as a description.
- The external-message approval is announced like any Needs you item, with the title first:
  *Message from an external agent, from @welshofer, held, 52 minutes left.* (from the host's expiry, like the card) The message box is
  a focusable, scrollable region named *Message text, 294 characters*.
- The revocation notice is announced politely once (*@welshofer's sessions are hidden.
  Revoked by @welshofer.*), not as an alert.
- The filter sheet and chips are radio groups; the window segments are a radio group named
  *Window*.
- Motion: no new motion. Hiding on revocation is immediate in every motion setting.

## 6. The Home Assistant card

The card is gated so older cards degrade safely: CARD_VERSION today (1.41.0), or the
`hello.features` capability list in the new stack (design.md §4.1). The feature flag is
`collaborators`.

- **Cards without the feature (1.41 and older):** the host never publishes external sessions
  to them. An external-message approval appears only as a pointer: *A message from an
  external agent is waiting. Open Slipgates to review it.* The text is never sent to the
  card. Your own sessions driven by an external agent read *Driven by an agent* as today.
- **Cards with the feature:** your own sessions show *Driven by an external agent ·
  @welshofer*. Collaborators' sessions stay app-only in v1 (decision 9); the card shows one
  line, *Collaborators' sessions: in the Slipgates app*.
- The card never approves external messages; that needs presence on a device (§5.7).

## 7. Every board

9 boards plus the [contact sheet](519-collaborators/contact-sheet.png), each labelled
"Proposal #519 · decided 2026-11-03":

1. [Palette](519-collaborators/proposal-519-1-palette.png): External in dark, light and
   forced colours, its marks, and the colour-vision table.
2. [Windows](519-collaborators/proposal-519-2-windows.png): the dark Windows app, person then
   machine, the person chips, and a read-only external session.
3. [PWA desktop](519-collaborators/proposal-519-3-pwa-desktop.png): light, the person menu,
   and a collaborator's waiting session.
4. [PWA phone](519-collaborators/proposal-519-4-pwa-phone.png): dark, the list, the filter
   sheet, and a read-only session.
5. [Provenance](519-collaborators/proposal-519-5-provenance.png): own-agent and
   external-agent marks in dark and light, and the "Involving external agents" filter with
   its empty state.
6. [Collaboration](519-collaborators/proposal-519-6-collaboration.png): Settings ›
   Collaboration, the suggestion, links with status, and history.
7. [Link dialog](519-collaborators/proposal-519-7-link-dialog.png): scope, allow-all
   opt-ins, pinned workspaces, the revoker requirement and the link code.
8. [Approval](519-collaborators/proposal-519-8-approval.png): the external-message approval
   for allow-all (dark) and unknown mode (light), next to a tool approval, with the
   allow-window strip.
9. [Revocation](519-collaborators/proposal-519-9-revocation.png): before and after, the
   open-pane state, the reason table and both HA cards.

Re-render with:

```
cd tools/design-mockups
npm install
node render.js ../../design/canvas ../../docs/design/proposals/519-collaborators Proposal-519-
node contact-sheet.js ../../docs/design/proposals/519-collaborators proposal-519-
```

(contact-sheet.js arrives with #532.) Limits: Inter stands in for Segoe UI; forced colours
are emulated, not captured; Mica is drawn as flat surfaces.

## 8. What this doesn't change

- No change to the S glyphs, the ink plate, row height, typography, spacing or motion.
- Delegates keep their rings. A collaborator's delegates nest under their session the same
  way, in External.
- Needs you, notifications, badges and counts keep their meaning: only things waiting on
  you.
- The word "gate" appears in no status or error copy.

## 9. Wording to coordinate with Agent Bridge UI

The strings below are now in ui.md §6.7 and §8. Agent Bridge UI owns them in implementation
and changes them there, not here:

- Person and filter: **Mine**, **Everyone**, **Show sessions from**, **Manage
  collaborators…**, **External** (tag), *WS-LAPTOP · their name*.
- Rows: *Working · read-only*, *Idle · read-only*, *Waiting for @welshofer*, *◇ Driven by an
  external agent · @welshofer*, *◇ External agent · @welshofer* (narrow).
- Read-only: *Read-only · @welshofer's session. Only @welshofer can reply, approve or stop
  it.*; *Latest response · 2 min ago*; *The link shares the latest response only.
  @welshofer's prompts, questions and tool history stay on their side.*; *@welshofer's
  agent is waiting for @welshofer.*; *Only @welshofer can see what it asked and answer it.*
- Provenance: *Sent by copilot on DESK*; *external · Sent by an agent on WS-LAPTOP ·
  @welshofer*; *Their session: "…"*.
- Filter: *Involving external agents*; *External agents: sent, read, refused · 2 min*; *No
  external agents in the last hour*; *None sent to, read or were refused by your sessions.*;
  *Show 24 hours*; *Clear filter*.
- Collaboration: *Find collaborators who use Slipgates on {repo}*; the §7.11.5 suggestion
  copy; *Review and connect*; *Not now*; *Never for this repo*; *Link by invitation…*;
  *Active*, *Suspended*, *Revoked*; *Re-confirm workspaces*; *Revoke…*; *Message audit*.
- Link dialog: *What @{login} can do*; *Read status*, *Read content*, *Reply*, *Launch* and their lines; the Read content retention
  warning; the two
  allow-all opt-ins and warnings; *Workspaces (pinned now)*; *Who can end this link*; *Add a
  device or a recovery key that can end this link*; *Identity*; *Link code (optional, stronger)*; *Link code (required)*; *Accept with Windows
  Hello*.
- Approval: *Message from an external agent*; *Held · 52 min left*; *Shown exactly as sent,
  N characters. Not formatted.*; the allow-all and unknown-mode consequence sentences;
  *Deny*, *Approve once*, *Allow @{login} here for…*; *Expired · not delivered*; *Stop
  allowing @{login}*.
- Revocation: every reason line in §3.7; *@{login}'s sessions are hidden*; *This session is
  no longer shared*; *What you saw of it was removed from this device.*; *Back to sessions*.
- HA: *A message from an external agent is waiting. Open Slipgates to review it.*;
  *Collaborators' sessions: in the Slipgates app*.

## 10. Slices

1. ui.md changes (§11): done in #566.
2. External token and the person level in the list, behind the `collaborators` feature.
3. Read-only external session view.
4. Provenance marks and the "Involving external agents" filter.
5. Settings › Collaboration and the link dialog.
6. External-message approval in Needs you.
7. Revocation handling and the HA card.

## 11. Changes to ui.md

Made in #566 after the decisions. These are the changes to [ui.md](../ui.md), most of them
gathered in its new §6.7 Collaborators:

- **§2.1 Glyphs:** External sessions use the same S glyphs in External; an external waiting
  state is the outlined S tile, never the filled Needs you tile.
- **§2.2 What a session row says:** a rule for external rows (*read-only* on line 2, no
  verbs) and, after rule 7, the driven-by-external variant (outlined ◇ in External in the
  badge seat, *Driven by an external agent · @login*).
- **§3 Colour:** a new **External** role with the §4.2 tokens and the rule "never alone,
  never for counts, Needs you, badges or notifications"; the matching entries in
  tokens.json, with the tint pending (§4.2).
- **§6.1 Session list:** the person level (Mine, then collaborators by verified login, with
  machines nested); the Show sessions from filter per shell; the "Involving external
  agents" filter and its empty state; external items excluded from Needs you, except the
  external-message approval.
- **§6.2 Session header:** @login, machine and the External tag for external sessions.
- **§6.4 Conversation:** the external-agent provenance mark beside the own-agent mark; the
  read-only latest-response view and the waiting card; the external-message approval card.
- **§6.5 Reply box:** absent for external sessions, replaced by the read-only line.
- **§6.6 Machines:** a collaborator's machine names are shown with *their name*.
- **§7 Platform shells:** the filter control per shell; notifications and tiles exclude
  collaborators' items; the HA card gating in §6.
- **§8 Copy:** the strings in §9.
- **§9 Accessibility:** the accessible names and announcements in §5, and the forced-colour
  mapping in §4.5.
- **New:** a Settings › Collaboration section (discovery, links, link dialog, history).
- **§10 Mock-up index:** the nine boards.

## 12. Owner decisions

**Decided 2026-11-03: the owner chose (a), the recommended option, for all 12.** The options
are kept as the record. Decision 11's "notified" is narrowed by agent-principals §5.7 (merged
in #522), which is normative: a held message is counted, never pushed on its own, and an
opted-in push is a text-free hourly summary. Decision 12 is a request to the #522 spec, not a UI change; until
#522 adds the logins, line 2 shows kinds only.

1. **External hue.** (a) **Chosen (recommended):** orchid, `#F769CF` dark / `#A3177F` light. (b) No
   hue: neutral outlines and words only. (c) Plum, a darker purple. (a) is distinct from
   every existing role in normal vision and passes contrast everywhere; (b) is safest but
   loses at-a-glance scanning; (c) sits too close to agent violet.
2. **Forced-colour mapping.** (a) **Chosen (recommended):** `CanvasText` plus outline and the word.
   (b) `Highlight`. (b) would look like a selected row.
3. **External row glyph.** (a) **Chosen (recommended):** the state's own S shape in External, waiting
   as an outlined tile. (b) One neutral glyph for every external session. (a) keeps "is
   their agent busy?" readable without implying it's yours to answer.
4. **Person filter control.** (a) **Chosen (recommended):** chips on Windows, a menu on PWA desktop
   and a sheet on the phone. (b) A menu everywhere. (c) Tabs per person. Chips are one tap
   where there's room; tabs don't scale past three people.
5. **Driven-by-external badge.** (a) **Chosen (recommended):** outlined ◇ in External plus words.
   (b) Words only. (a) matches the provenance mark and survives truncation of line 2.
6. **"Involving external agents" default window.** (a) **Chosen (recommended):** 24 h. (b) 1 h.
   (c) 7 days. 24 h covers a working day without becoming a history view.
7. **Approval allow-window default.** (a) **Chosen (recommended):** nothing pre-selected. (b) 15
   minutes pre-selected. Agent-principals §5.7 treats a window as a deliberate grant; a
   default makes it the easy path.
8. **External sessions in search.** (a) **Chosen (recommended):** a separate "Collaborators" group
   after your own results. (b) Excluded. (c) Mixed in. (a) finds them without implying
   they're yours.
9. **Home Assistant.** (a) **Chosen (recommended):** collaborators' sessions app-only in v1;
   gated cards show external provenance on your own sessions; older cards get a pointer
   only. (b) Hide all external information on every card. (c) Show collaborators' sessions
   on gated cards. Dashboards are often shared screens; (a) keeps other people's work off
   them.
10. **Where Collaboration lives.** (a) **Chosen (recommended):** Settings › Collaboration. (b) A
    top-level page in the app rail. Links change rarely; a rail entry would invite
    attention discovery shouldn't ask for.
11. **External-message approval in Needs you.** (a) **Chosen (recommended):** counted and notified
    like any approval, with its own title and layout. (b) Counted but not notified. A held
    message expires in five minutes; without a notification most would expire unseen.
12. **Logins on `Session.external`.** (a) **Chosen (recommended):** ask #522 to add the sender's
    verified login(s), so line 2 can read *External agents: @welshofer sent · 2 min*.
    (b) Keep kinds only, as drawn. (a) answers "who?" without opening the session.
