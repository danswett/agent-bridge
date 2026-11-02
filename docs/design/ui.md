# UI design: the session workbench

The design for every Agent Bridge client: the Windows app, the PWA, and the later macOS,
iOS and Android apps.

- What to build, in order: [ui-plan.md](ui-plan.md).
- How a session and its sub-agents are modelled and sent: [subagents.md](subagents.md).
- Machine-readable values (colours, type, spacing, glyphs, icons): [`/design`](../../design/README.md).

*Status: proposed by the design session on 2026-11-01 and signed off by the owner the
same day (status.md §5). Two light-theme tokens are corrected for contrast in #183.* The
owner's mock-ups live in a private design canvas, "Agent Bridge — Session Workbench".
Every board is also rendered in [mockups/](mockups/README.md), and its source is in
[`/design/canvas`](../../design/canvas/README.md). This page is the spec, and it is complete
without them: where they disagree, this page wins and the mock-ups get fixed.

## 1. Principles

1. **One language, native everywhere.** What things *mean* is ours and identical on every
   client: status glyphs, status colours, icons, the mark, and the session → main agent →
   delegate hierarchy. How things are *built* belongs to each platform: window chrome,
   navigation pattern, controls, materials, type family, motion and gestures.
2. **Order is yours.** Rows never move on their own. Only a question floats, into Needs
   you, and its row stays where it is (#164, #165).
3. **One voice per line.** The conversation shows the main agent only. Every delegate's
   words live on that delegate's own row or panel, and name who wrote them (#84).
4. **Panes, not expanding cards.** A list of one- or two-line rows, and detail in a pane or
   a pushed screen. Nothing expands in place and shoves the list around.
5. **The interface doesn't explain itself.** No helper sentences inside the UI ("Replies go
   to the main agent…"). If a control needs a sentence, the control is wrong. Tooltips and
   accessibility labels carry the detail.

## 2. Status language

### 2.1 Glyphs

One shape per state, drawn on a 16 grid ([`/design/glyphs`](../../design/glyphs)). The shape
alone must identify the state: in greyscale, in high contrast, and for colour-blind
people.

| Glyph | Shape | Colour role | Meaning |
|---|---|---|---|
| `needs` | filled disc with "!" (the only solid glyph) | needs | A question or approval is waiting |
| `working` | ring with a turning quarter arc | active | The main agent's turn is running (foreground task agents included) |
| `delegating` | small ring, a dotted orbit, two satellites | active | Main agent idle; background agents or commands still running |
| `idle` | thin ring | neutral | Nothing running |
| `done` | ring with a check | neutral | A delegate finished. Never used for sessions, since "done" isn't news |
| `failed` | ring with × | danger | A delegate aborted, a launch failed, or a host is unreachable |
| `ended` | dotted ring | neutral | The session has gone; shown only in the Ended group |
| `driven-badge` | small diamond, bottom right | agent | Added to any glyph while an agent drives the session; the active colour becomes violet |

Motion: `working` turns once per 1.2 s, and `delegating`'s satellites drift once per 8 s.
Both are still under reduced motion. Nothing else animates on its own.

The glyph **replaces** today's coloured card edges and glows (`.frame.working`,
`.frame.delegating`, `.frame.agent` in `pwa/app.css`, and the same in the app). Selection
is a quiet fill plus the Bridge accent, never a status colour.

### 2.2 What a session row says

Applies to the row, the session header, notifications and the Home Assistant card alike.

Rules 1–6 are first match wins. Rule 8 refines rule 4. Rule 7 is not a row of its own: it is
an overlay applied after rules 1–6 (or 8) have picked the glyph and the words.

| # | When | Glyph | Status text |
|---|---|---|---|
| 1 | The session itself has a pending question | `needs` | "Waiting on you", plus "· N delegates still running" when there are any, and "· answer at the machine" when no host can answer it from another device (`answerAtMachine`) |
| 2 | A delegate has a pending question (`waitingOn`, or the question's `agent`) | `needs` | "*Delegate name* asks", or "… · answer at the machine" when the host can't prove where the answer lands (`answerAtMachine`). A delegate the list left out is "A delegate asks" |
| 3 | The main agent is working | `working` | "Working", or "Working · task: *name*" while a foreground task agent runs (a running `kind: task` sub-agent whose starters are all task agents too; one a background delegate started is that delegate's work) |
| 4 | Main agent idle, background agents running | `delegating` | "Delegating to N agents", N from `background.agents` (a nested delegate counts once, under its parent, and a list cut short still counts them all) |
| 5 | Main agent idle, only background commands | `delegating` | "N background commands" |
| 6 | Nothing running | `idle` | "Idle" |
| 7 | **Overlay.** Any of the above while an agent drives | the same glyph plus `driven-badge`; `working` and `delegating` turn from active to agent | The same words, in agent colour, with "Driven by an agent" on rows |
| 8 | **Refines rule 4.** The agent can't list its sub-agents (Claude `Task`, Codex threads, until #84's later slices, or a host older than `subagents`): `background.agents` counts them but neither `subagents` nor `subagentsMore` lists any | `delegating` | "Delegating to N agents · details not reported". Nothing is ever wrongly attributed |

While the host ends a session (`status: ending`), none of rules 1–6 apply: the row shows the
`ended` glyph and "Ending session…" in secondary text, and rule 7's overlay still applies.

N is a number before its noun, singular for one ("1 delegate", "1 agent", "1 background
command"). A glyph's accessible name is the row read aloud (§9).

Rule 1 matches the 2026-10-30 shadow finding on #84 ("question wins") and the host's
current behaviour. Rule 7 keeps #46's meaning in a new form. The tests in both clients are
named after these rules (`pwa/test/unit/status.test.mjs`, `SessionStatusTests`).

## 3. Colour

Six roles, each with a dark value, a light value and a high-contrast mapping, defined in
[`tokens.json`](../../design/tokens.json) under `color.role`, and generated into each client
by `tools/design-gen` (#177).

| Role | Dark | Light | Used for |
|---|---|---|---|
| Bridge | `#2DD4BF` | `#0B7A6E` | Primary actions, selection, focus, the mark, the "new activity" dot |
| Needs you | `#FFB224` | `#B87700` (text `#7A4600`) | Questions and approvals |
| Active | `#4C9AFF` | `#0B63E5` | The `working` and `delegating` glyphs only |
| Agent | `#A78BFA` | `#6D4AE0` | Driven by an agent, and nothing else |
| Danger | `#FF6B6B` (text `#FF9494`) | `#C9302C` | Failed, stopped, End session |
| Neutral | `#8B949E` | `#6E7781` | The `idle`, `done` and `ended` glyphs only |

Secondary and tertiary text are the text colour at reduced opacity: `text2` and `text3`,
white at 0.72 and 0.54 in dark, black at 0.62 and 0.58 in light. `text3` is not used on
a role fill (4.30:1 on the dark Needs you fill).

Rules:
- **Status colours are never used on controls.**
- **Words are never in Active or Neutral.** They are glyph colours (Active is 4.46:1 on the
  dark card). Text in a role uses Bridge or the role's own text colour (Needs you's,
  Agent's and Danger's): dark Danger is 3.72:1 on the Needs you fill, so its words are
  `#FF9494`.
- **Surfaces and text come from the platform.** Mica and layer fills on Windows, window and
  sidebar materials on macOS, system grouped backgrounds on iOS, and a Material 3 scheme
  seeded from Bridge on Android. Dynamic colour may restyle Android surfaces but never the
  status roles.
- **Theme:** light, dark and high contrast follow the OS on every client. In high
  contrast, every role and every Windows/PWA surface maps to a system colour (`tokens.json`
  `hc`, `hcVariants` and `surface.windows.hc`).
- **Contrast:** status glyphs reach 3:1 against their surface, and text in a role colour
  reaches 4.5:1, on every surface and on every role's fill over it (`tokens.json`
  `color.contrast`, checked by `tools/design-gen`'s tests). Words on a tint (`subtle`,
  `selected`) are text, text2 or Bridge, and a tint never sits on a role fill: code in a
  question takes the card colour. That is why light Needs you is `#B87700` rather than the brighter `#E8A10C` (2.20:1 on the
  card), light `text3` is 0.58 rather than 0.52 (#183), and iOS light secondary text is
  `rgba(60,60,67,0.76)` rather than UIKit's 0.60.

This replaces the GitHub-derived palette in `pwa/app.css` and `App.xaml`: both clients take
every colour from the generated roles (#177).

## 4. Typography

### 4.1 Faces

- **Text: each platform's system face.** Segoe UI Variable on Windows, SF Pro on Apple
  platforms, Roboto Flex on Android, and `system-ui` in the PWA. This is most of what
  makes a native app feel native. A licensed text face would make all four feel slightly
  foreign, so none is used.
- **Code: one monospace everywhere,** for commands, paths, agent IDs, tool calls and code
  blocks. That's where agent apps usually look generic, and it's on screen constantly.
  - **Agent Bridge Mono,** our own build of Iosevka with a Berkeley Mono-like
    character (IoskeleyMono configuration, a tailed `l`, a 560 width), in
    [`/design/fonts/agent-bridge-mono`](../../design/fonts/agent-bridge-mono/README.md).
    It is under the SIL OFL, so it ships inside every client and lives in this public
    repository. Berkeley Mono was considered and rejected: its Commercial license limits
    embedding to apps without text entry, requires the font to be unextractable, and bars
    public sharing (#181).
  - **Stack:** Agent Bridge Mono, then Cascadia Mono, SF Mono, Roboto Mono and
    `ui-monospace` (Consolas in XAML, which has no generic name). The fallbacks cover
    loading and any character outside the font's subset.

### 4.2 Rules

- **Two weights:** regular and semibold (bold for Apple titles). A semibold row title
  means *unread activity*, and nothing else uses weight.
- **Tabular figures** wherever a number can change (times, counts, percentages,
  durations), so text doesn't jitter. Durations read `4:02`, not `4m 02s`.
- **Secondary text is opacity, not a third grey:** `text2` and `text3` in `tokens.json`.
- **Sentence case** everywhere, except macOS button titles, which use Title Case as macOS
  does.
- **Reading measure:** the conversation is capped at about 68 characters (680–712 px on
  desktop) and set a step larger than the UI around it.
- **Mono** is never smaller than 11 px.

### 4.3 Ramps

Size/line-height in px. Weights are regular unless noted.

| Role | Windows (and PWA desktop) | PWA phone | macOS | iOS | Android |
|---|---|---|---|---|---|
| Title | 20/28 semibold, Display | 24/32 semibold | 13/16 bold (toolbar) | 34/41 bold, Large Title | 22/28 Title Large |
| Row title | 14/20 | 15/20 | 13/16 | 17/22 Body | 16/24 Body Large |
| Reading | 15/24 | 16/24 | 14/21 | 17/22 Body | 16/24 Body Large |
| Meta | 12/16 | 13/18 | 11/14 | 15/20 Subheadline, 13/18 Footnote | 14/20 Body Medium, 11/16 Label Small |
| Section header | 12/16 semibold | 13/18 semibold | 11/14 bold | 13/18 Footnote | 14/20 medium, Title Small, in `primary` |
| Code | 12/18 | 13/18 | 11/16 | 13/18 | 13/20 |

Letter-spacing follows each platform's own tracking: SF's per-size values on Apple and
Material 3's on Android. Never add tracking to Segoe UI.

## 5. Space, shape and layout

### 5.1 Spacing and radius

- **Spacing:** a 4 px grid (4, 8, 12, 16, 20, 24, 32, 40, 48).
  - 24 between blocks in the conversation, and 8 between a speaker label and its text.
  - Panes pad 20–24 on desktop and 16–20 on phones.
  - A row's glyph aligns with the row's first line of text, not with its centre.
- **Radii follow the platform:**
  - Windows: 4 on controls, 8 on cards.
  - macOS: 6 and 10.
  - iOS: 10 on cells, 12 on sheets, capsule buttons.
  - Android: 8 on chips, 16 on cards, 28 on the search bar.
- **Fewer boxes.** Group with space and a single hairline. Never put a bordered box inside
  a bordered box.

### 5.2 Layout by window width

The window decides, not the device.

| Width | Layout |
|---|---|
| ≥ 1280 | Three panes: list (320) · session (fills) · delegate inspector (336, can be hidden) |
| 760–1279 | Two panes: list (264–320) · session with Conversation / Delegates / Tools tabs |
| < 760 | One column, push navigation: list → session → delegate (a sheet on phones) |

Selection survives a resize across breakpoints. #166 has the acceptance criteria.

## 6. Surfaces

### 6.1 Session list

Sections, top to bottom:

1. **Needs you · N** (#165): one entry per pending question across machines, oldest first.
   - Each entry shows: the session title, the question's first line or the approval's
     action, and its age.
   - Approvals (`kind: approval`, not `answerAtMachine`) can be answered inline: "Approve
     once" (Bridge) and "Deny" (secondary).
   - Forms open the session. `answerAtMachine` entries say so and have no controls.
   - The lane references sessions; it never moves their rows.
2. **Pinned.**
3. **Sessions,** in your order (#164).
4. **Ended · N,** collapsed.

**Order (#164).** Rules the clients share; the PWA and the Windows app keep the order in
their view models (`pwa/js/session-order.js`, `App.Core`), not in the markup.
- **Per device.** Kept on the device (the PWA's `localStorage`, a file beside the Windows
  app's folds). Never sent to a host: it isn't protocol.
- **New sessions** go to the top of Sessions once, then stay put. A machine's first list is
  placed together, newest first, once the machine has sent all of it.
- **Moving** is the person's only: Alt+↑/↓ on a row; a drag by the handle that shows on hover
  (mouse or pen); or the row menu, opened by a long press on touch, a right-click, or
  Shift+F10 / the menu key. Its items are Move up, Move down and Pin (or Unpin). Moves stay
  within the row's group; a row dragged into the other group is pinned or unpinned.
- **Pins:** a pinned row goes to the foot of Pinned, and an unpinned one to the top of
  Sessions.
- **Sort menu:** "Your order" (the default), "Most recent" and "Machine" (by machine name,
  then your order). It sorts within Pinned and within Sessions. Rows move only in Your order.
  Where the order can't be kept (storage blocked or full), the list is Most recent and Your
  order can't be picked.
- **Ended** holds the sessions a host removed while the client was running: the most
  recently ended first, at most 20, until the client restarts. A machine that goes offline
  or out of view ends nothing.
- **Unread:** a row whose session updated since the row was last open: the selected row (an
  unfolded card in the PWA until #179 lands). A session seen for the first time starts read.

**Row anatomy (two lines):**
- **Leading:** the status glyph, aligned to line 1.
- **Line 1:** the title. Semibold means unread activity, and the 6 px Bridge dot appears at
  the trailing edge.
- **Line 2:** at most two facts, for example "3 delegates · DEV-VM1", "Editing
  LaunchView.xaml.cs · ULTRA" or "Driven by an agent · ULTRA" (in agent colour).
- **Trailing:** the age (tabular), when there's no unread dot.
- **Never on a row:** the model, the repo SHA, turn and compaction counts, or the pid.
  Those live in session details.

**Machine filter:**
- Windows and the PWA: SelectorBar-style text tabs.
- macOS: a "Machines" sidebar section.
- iOS: a toolbar button.
- Android: filter chips.

### 6.2 Session header

- **Title:** the session name.
- **One meta line:** glyph · status text (rule set §2.2) · machine · repo.
- **Actions:** icon buttons with tooltips: Pin, Details (everything removed from rows),
  and toggle inspector. End session lives in Details on desktop, and in the overflow menu
  on phones.

### 6.3 Delegates (#84)

- **Wide desktop:** a strip under the header with one item per delegate. Each item shows
  the glyph, the name, and the elapsed time or "N nested", with the selected item in
  Bridge. Finished delegates fold into a single "N finished" item.
- **macOS:** the same, as a token row labelled "Delegates".
- **Narrow desktop:** the Delegates tab.
- **Phone:** one horizontal chip row. iOS opens a sheet with medium and large detents.
  Android and the PWA open the delegate in place or as a pushed screen.
- **Inspector:**
  - the delegate's name and glyph;
  - a property list: Status, Model, Kind (background or task), ID (mono);
  - Task (the prompt it was given), Latest (its latest output), and Reasoning (collapsed);
  - Tools: mono, glyph per call, tabular duration;
  - nested delegates, listed under their parent.
- **One action:** "Ask main agent to stop it". It pre-fills a reply to the main agent and
  sends nothing on its own. There is no per-delegate typing or Stop.

### 6.4 Conversation

- **Your messages:** a right-aligned bubble (card fill, platform radius).
- **Main agent:** a label line ("Main agent", then the age in meta style), then reading
  text. Reasoning is behind a "Show reasoning" disclosure, and is always the main agent's
  own.
- **Delegate events:** one line each, meta style, with an icon: "Started 3 delegates ·
  26m", "*Read witness shell logic* reported back · 22m". The name links to the delegate.
- **Questions:** in the stream, as a card in Needs you colours. The header reads "Main agent
  asks" or "*Delegate* asks", followed by the question and the platform's own radio,
  checkbox and text controls, with Answer in Bridge.

### 6.5 Reply box

- **Placeholder:** "Message the main agent". Replies only ever go to the main agent.
- **Controls:** Attach, then Send (Bridge, icon only, with a tooltip naming the shortcut:
  Ctrl+Enter, or ⌘↩ on Apple).
- **Platform styling:**
  - Windows and the PWA: a card with a 2 px Bridge bottom edge.
  - macOS: a capsule field.
  - iOS: a capsule field over the keyboard.
  - Android: a filled field on `surfaceContainer`.

## 7. Platform shells

**Windows (WinUI 3):**
- **Window:** Mica, with a custom 48 px title bar carrying the mark, the app name (12/16)
  and a centred search box. System caption buttons.
- **Search:** finds sessions on every machine by the words of their title, repository, branch,
  agent or machine, titles starting with them first, and opens the one chosen as a
  notification does.
- **Navigation:** a compact `NavigationView` rail (Sessions, Machines, Launch, Usage;
  Settings at the foot) with a selection pill.
- **Content:** a layer with an 8 px top-left radius.
- **Controls:** ListView rows with a 3×16 Bridge pill on selection, SelectorBar for
  machines, an Accent button for New, subtle icon buttons for header actions.

**macOS (SwiftUI, later):**
- **Sidebar:** a translucent source list (Needs You, Pinned, Sessions, Machines), 28 px
  rows, and a Bridge fill on the selected row.
- **Toolbar:** unified, with title and subtitle, search, and icon buttons.
- **Inspector:** a property grid with right-aligned labels.
- **Keyboard:** ⌘↩ sends.

**iOS (SwiftUI, later):**
- **Sessions:** large title with the filter and New buttons on the title row (no empty
  nav-bar row above it), a search field, and inset grouped sections.
- **Session:** a back button, a 28/34 title, the conversation, and delegates in a sheet.
- **Navigation:** a tab bar (Sessions, Machines, Usage, Settings).
- **Gestures:** swipe actions on rows (Pin, Ask main agent to stop).

**Android (Compose, later):**
- **Sessions:** a Material 3 search bar, filter chips, list items, an extended "New
  session" FAB, and a navigation bar.
- **Session:** a top app bar, assist chips for delegates, and the composer on
  `surfaceContainer`.

**PWA:**
- **Look:** follows the Windows design at every width (#166), with no imitation of other
  platforms' controls.
- **Desktop:** the Windows layout with a 56 px web app bar in place of the title bar (mark,
  name, search, machines online, account).
- **Phone:**
  - a 24/32 title row with Search and New;
  - machine tabs;
  - a bottom navigation bar with a Bridge indicator.
- **Platform layer (#168):** detection only for shortcut hints, safe areas and back
  navigation.

## 8. Copy

- **Status words** come from §2.2 only. Don't invent synonyms.
- **Names:**
  - "Main agent" (never "parent").
  - "Delegate" in the UI ("sub-agent" in code and specs).
  - "Needs you".
  - "End session" (not "Stop" for the whole session).
- **Numbers before nouns:** "3 delegates", "1 background command".
- **No sentences explaining the UI,** no exclamation marks, and no emoji.

## 9. Accessibility

- **Contrast:** every text pair meets 4.5:1 and every glyph 3:1, in light, dark and high
  contrast.
- **Touch targets:** at least 44×44 on touch platforms, 32×32 on desktop.
- **Glyphs** carry their state as an accessible name. A row reads as "Plan Migration to
  Windows App, delegating to 3 agents, DEV-VM1".
- **Reduced motion** stops all glyph animation. Nothing relies on motion alone.
- **Keyboard:** desktop lists move with arrows, Alt+↑/↓ reorders (#164), Shift+F10 or the
  menu key opens a row's menu, and Ctrl/⌘+K focuses search.

## 10. Mock-up index

The canvas has these boards, rendered as images in [mockups/](mockups/README.md). On the
canvas, each platform board has a light/dark switch.

| Board | Shows |
|---|---|
| Design language | Principles, glyphs, colour roles, icons, app icon, hierarchy, typography, layout |
| Windows · dark / light | Three panes, delegating session, inspector on |
| macOS · light / dark | Sidebar, token row, inspector |
| PWA desktop · dark / light | The Windows board with the web app bar |
| iOS Sessions / Session · dark / light | List with Needs you; session with the delegate sheet |
| Android Sessions / Session · light / dark | List with FAB; a session waiting on you while delegates run |
| PWA phone / phone session · dark / light | Windows-styled phone list and session |
| Status rules | The table in §2.2, drawn |
