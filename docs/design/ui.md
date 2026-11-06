# UI design: the session workbench

The design for every Agent Bridge client: the Windows app, the PWA, and the later macOS,
iOS and Android apps.

- What to build, in order: [ui-plan.md](ui-plan.md).
- How a session and its sub-agents are modelled and sent: [subagents.md](subagents.md).
- Machine-readable values (colours, type, spacing, glyphs, icons): [`/design`](../../design/README.md).

*Status: proposed by the design session on 2026-11-01 and signed off by the owner the
same day (status.md §5). Two light-theme tokens are corrected for contrast in #183. Motion
(§5.3) was added in #232 and signed off by the owner on 2026-11-02. Attention (one row and one
question, #253) replaced the Needs you lane with a filter, signed off by the owner on
2026-11-02; the reasoning is in [proposals/253-attention.md](proposals/253-attention.md).
Collaborators (#519: the External colour, its marks, the person filter, the external filter and
the held message) were decided by the owner on 2026-11-03 and added here (§6.7); the reasoning
and the boards are in [proposals/519-collaborators.md](proposals/519-collaborators.md). The
accessibility contract (§9.1) gathers the accessibility rules already decided here and the WCAG
requirements they rest on, each with the check that enforces it, and the rules the owner
approved for it on 2026-11-04 (#707).* The
owner's mock-ups live in a private design canvas, "Agent Bridge — Session Workbench", and
proposals wait for the owner's sign-off in the private design studio
([design/README.md](../../design/README.md#owner-sign-off)).
Every board is also rendered in [mockups/](mockups/README.md), and its source is in
[`/design/canvas`](../../design/canvas/README.md). This page is the spec, and it is complete
without them: where they disagree, this page wins and the mock-ups get fixed.

## 1. Principles

1. **One language, native everywhere.** What things *mean* is ours and identical on every
   client: status glyphs, status colours, icons, the mark, and the session → main agent →
   delegate hierarchy. How things are *built* belongs to each platform: window chrome,
   navigation pattern, controls, materials, type family, motion and gestures.
2. **Order is yours.** Rows never move on their own, not even for a question: its row shows
   it in place, and Needs you · N narrows the list to the rows waiting on you (#164, #253).
   A question's words are drawn once, in the conversation.
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
| `external-badge` | small **outlined** diamond, bottom right | external | Takes `driven-badge`'s seat while an external agent (a collaborator's, #519) drives your session; `working` and `delegating` still turn agent violet |

A collaborator's session (§6.7) draws its state's glyph in External. When it waits on its owner,
it draws the `needs` shape **outlined** in External, never the filled `needs` glyph: the
question is theirs, not yours.

Motion: `working` turns once per 1.2 s, and `delegating`'s satellites drift once per 8 s.
Both are still under reduced motion. Nothing else animates on its own: every other motion
answers the person or a change, and is in the catalogue in §5.3.

The glyph **replaces** today's coloured card edges and glows (`.frame.working`,
`.frame.delegating`, `.frame.agent` in `pwa/app.css`, and the same in the app). Selection
is a quiet fill plus the Bridge accent, never a status colour.

### 2.2 What a session row says

Applies to the row, the session header, notifications and the Home Assistant card alike.

Rules 1–6 are first match wins. Rule 8 refines rule 4. Rules 7 and 9 are not rows of their own:
they are overlays applied after rules 1–6 (or 8) have picked the glyph and the words.

| # | When | Glyph | Status text |
|---|---|---|---|
| 1 | The session itself has a pending question | `needs` | "Waiting on you", plus "· N delegates still running" when there are any, and "· answer at the machine" when no host can answer it from another device (`answerAtMachine`) |
| 2 | A delegate has a pending question (`waitingOn`, or the question's `agent`) | `needs` | "Delegate *name* asks", or "… · answer at the machine" when the host can't prove where the answer lands (`answerAtMachine`). The word "Delegate" is always there, so no name reads as "Main agent asks" (§6.3). A delegate the list left out is "A delegate asks" |
| 3 | The main agent is working | `working` | "Working", or "Working · task: Delegate *name*" while a foreground task agent runs (a running `kind: task` sub-agent whose starters are all task agents too; one a background delegate started is that delegate's work) |
| 4 | Main agent idle, background agents running | `delegating` | "Delegating to N agents", N from `background.agents` (a nested delegate counts once, under its parent, and a list cut short still counts them all) |
| 5 | Main agent idle, only background commands | `delegating` | "N background commands" |
| 6 | Nothing running | `idle` | "Idle" |
| 7 | **Overlay.** Any of the above while an agent drives | the same glyph plus `driven-badge`; `working` and `delegating` turn from active to agent | The same words, in agent colour, with "Driven by an agent" on rows. When the driver is an external agent (#519): `external-badge` in place of `driven-badge`, and "Driven by an external agent · @*login*" on rows (phone: "External agent · @*login*") |
| 8 | **Refines rule 4.** The agent can't list its sub-agents (a host older than `subagents`, or an agent whose transcript counts its delegates without saying which they are): `background.agents` counts them but neither `subagents` nor `subagentsMore` lists any | `delegating` | "Delegating to N agents · details not reported". Nothing is ever wrongly attributed |
| 9 | **Overlay, #519.** A collaborator's session (§6.7) | the same shape in External; rules 1–2 become the outlined `needs` shape | The state's words plus "· read-only" ("Working · read-only"); rules 1–2 read "Waiting for @*login*". Never counted, never in Needs you. Rule 7 doesn't apply: whoever drives it, it stays in External |

While the host ends a session (`status: ending`), none of rules 1–6 apply: the row shows the
`ended` glyph and "Ending session…" in secondary text, and rule 7's overlay still applies.

While a session's machine is away (#269), none of the rules apply: what the row last said may no
longer be so. It shows the `failed` glyph (a host unreachable, §2.1) and, in secondary text,
"Offline · last seen 2m ago" once the machine has left the relay's presence, or "Reconnecting…"
while it is listed but its link is down; rule 7's overlay does not apply. A row's line 2 is
"Offline · *machine*". Nothing on it can be acted on (§6.1, "A machine away").

A message from an external agent held for you (§6.7) is a pending question on its target
session: rule 1 applies, Needs you counts it, and the status reads "Waiting on you · message
from an external agent".

N is a number before its noun, singular for one ("1 delegate", "1 agent", "1 background
command"). A glyph's accessible name is the row read aloud (§9.2).

Rule 1 matches the 2026-10-30 shadow finding on #84 ("question wins") and the host's
current behaviour. Rule 7 keeps #46's meaning in a new form. The tests in both clients are
named after these rules (`pwa/test/unit/status.test.mjs`, `SessionStatusTests`).

## 3. Colour

Seven roles, each with a dark value, a light value and a high-contrast mapping, defined in
[`tokens.json`](../../design/tokens.json) under `color.role`, and generated into each client
by `tools/design-gen` (#177).

| Role | Dark | Light | Used for |
|---|---|---|---|
| Bridge | `#2DD4BF` | `#0B7A6E` | Primary actions, selection, focus, the mark, the "new activity" dot, an active collaborator link's dot (§6.7) |
| Needs you | `#FFB224` | `#B87700` (text `#7A4600`) | Questions and approvals; a suspended collaborator link's dot and word (§6.7), which it can fix |
| Active | `#4C9AFF` | `#0B63E5` | The `working` and `delegating` glyphs only |
| Agent | `#A78BFA` | `#6D4AE0` | Driven by an agent, and a prompt an agent or a schedule sent (§6.4), and nothing else |
| Danger | `#FF6B6B` (text `#FF9494`) | `#C9302C` | Failed, stopped, End session |
| Neutral | `#8B949E` | `#6E7781` | The `idle`, `done` and `ended` glyphs, and a revoked collaborator link's hollow dot (§6.7) |
| External | `#F769CF` (text `#FBA8E4`) | `#A3177F` | A collaborator's session or an external agent (#519), and nothing else |

Secondary and tertiary text are the text colour at reduced opacity: `text2` and `text3`,
white at 0.72 and 0.54 in dark, black at 0.62 and 0.58 in light. `text3` is not used on
a role fill (4.30:1 on the dark Needs you fill).

Rules:
- **Status colours are never used on controls.**
- **Words are never in Active or Neutral.** They are glyph colours (Active is 4.46:1 on the
  dark card). Text in a role uses Bridge or the role's own text colour (Needs you's,
  Agent's and Danger's): dark Danger is 3.72:1 on the Needs you fill, so its words are
  `#FF9494`.
- **External is never alone** (#519): it always sits beside the outlined diamond, the word
  "External" or a verified @login, because under deuteranopia it nears Bridge and Neutral. It
  is never used for counts, Needs you, count badges, notifications or controls. In high contrast it
  maps to CanvasText (its track to GrayText), and the outline and the word carry it. Its tint
  (10% dark, 8% light, for the External tag and the provenance card) is decided but **pending**:
  `tools/design-gen` measures every role `fill` as a question-card surface, and light Bridge
  text (4.02:1) and Danger text would fail on it, though no question ever sits on it. Until the
  generator can mark a tint as decorative, clients draw the tag and the card with an External
  outline and no tint.
- **Surfaces and text come from the platform.** Mica and layer fills on Windows, window and
  sidebar materials on macOS, system grouped backgrounds on iOS, and a Material 3 scheme
  seeded from Bridge on Android. Dynamic colour may restyle Android surfaces but never the
  status roles.
- **Theme:** light, dark and high contrast follow the OS on every client. In high
  contrast, every role and every Windows/PWA surface maps to a system colour (`tokens.json`
  `hc`, `hcVariants` and `surface.windows.hc`; §9.1 A9).
- **Contrast:** status glyphs reach 3:1 against their surface, and text in a role colour
  reaches 4.5:1, on every surface and on every role's fill over it (`tokens.json`
  `color.contrast`, checked by `tools/design-gen`'s tests; §9.1 A8). Words on a tint (`subtle`,
  `selected`) are text, text2 or Bridge (on light's `selected`, `text` where Bridge would be; §9.1 A8), and a tint never sits on a role fill: code in a
  question takes the card colour. That is why light Needs you is `#B87700` rather than the brighter `#E8A10C` (2.20:1 on the
  card), light `text3` is 0.58 rather than 0.52 (#183), and iOS light secondary text is
  `rgba(60,60,67,0.76)` rather than UIKit's 0.60.

This replaces the GitHub-derived palette in `pwa/app.css` and `App.xaml`: both clients take
every colour from the generated roles (#177).

## 4. Typography

### 4.1 Faces

- **Text: Slipgates Sans everywhere** (#544), on Windows, macOS, iOS and iPadOS, Android and
  the PWA: our own tuned build of Inter, under the SIL OFL, in
  [`/design/fonts/slipgates-sans`](../../design/fonts/slipgates-sans/README.md). Five of Inter's
  own alternates are made the defaults to echo the mono (a tailed `l`, open `4`, `6` and `9`,
  a flat-top `3` and square dots), so text and code read as one family. Inter was chosen over Iosevka Aile
  (a quasi-proportional cut of the mono's own skeleton, which reads as typewritten in long
  prose) and over keeping the system face for body text with a brand face only for headings,
  which left two unrelated faces on every screen (#544).
  - **Files:** hinted static faces in one collection on Windows, where ClearType wants hinting;
    the variable pair (weight 400–700, upright and a true italic) everywhere else.
  - **The text cut at every size** (#544 §11). Inter's optical-size axis narrows its large
    sizes, and CoreText and Android would pick that display cut for titles on their own, which
    read as condensed. So every file is the text cut (opsz 14): the Windows statics are, and
    the variable pair's optical size is pinned at build time, leaving weight as its only axis.
  - **Stack:** Slipgates Sans, then each platform's system face (Segoe UI Variable, SF via
    `system-ui`, Roboto), which cover loading and any character outside the font.
  - **Scaling:** it scales exactly as the system face would: Dynamic Type through
    `UIFontMetrics` on iOS and iPadOS, `sp` with Android's nonlinear font scale up to 200%,
    Windows text scaling, and the browser's font size in the PWA.
- **Code: one monospace everywhere,** for commands, paths, agent IDs, tool calls and code
  blocks. That's where agent apps usually look generic, and it's on screen constantly.
  - **Slipgates Mono,** our own build of Iosevka with a Berkeley Mono-like
    character (IoskeleyMono configuration, a tailed `l`, a normal 600 width), in
    [`/design/fonts/slipgates-mono`](../../design/fonts/slipgates-mono/README.md).
    It was first built at 560 to fit more per line, but beside the text face that read
    as condensed, so it was rebuilt at 600, close to Cascadia Mono's and Consolas'
    proportions (#295).
    It is under the SIL OFL, so it ships inside every client and lives in this public
    repository. Berkeley Mono was considered and rejected: its Commercial license limits
    embedding to apps without text entry, requires the font to be unextractable, and bars
    public sharing (#181).
  - **Stack:** Slipgates Mono, then Cascadia Mono, SF Mono, Roboto Mono and
    `ui-monospace` (Consolas in XAML, which has no generic name). The fallbacks cover
    loading and any character outside the font's subset.

### 4.2 Rules

- **Weights:** regular and semibold, with bold for Apple titles and medium for Android's
  section headers (Material 3's Title Small), and italic for emphasis in agent text. A semibold
  row title means *unread activity*, and nothing else uses weight.
- **No synthesized styles.** Every weight and style the ramps use (`type.weights` ×
  `type.styles` in `tokens.json`: 400, 500, 600 and 700, upright and italic) is a real face
  in both Slipgates Sans and Slipgates Mono, and no client lets the platform fake a bold, an
  italic or an oblique. `tools/design-gen` refuses to generate if a family lacks one, and each
  platform is held to it:
  - **PWA:** `font-synthesis: none` on the root, an `@font-face` for every face (the sans as
    a variable `font-weight: 400 700` range, upright and italic). A Playwright test checks the
    faces load and nothing is synthesized.
  - **Windows:** each family is one font collection (`.ttc`) holding every face, so WinUI's
    family has them all and DirectWrite has nothing to simulate. The self-test asks DirectWrite
    for every weight and style and fails on a simulation, and checks rendered bold and italic
    widths against the real faces' advances.
  - **Apple:** every face is registered (`UIAppFonts`, `ATSApplicationFontsPath` on macOS,
    from the generated `SlipgatesFonts.plist`) and named by PostScript name through the
    generated `SlipgatesFonts.swift`. Never ask a face for `.bold()` or `.italic()` that
    it lacks; take the named face, scaled with `UIFontMetrics`.
  - **Android:** a font family XML with a `<font>` for every weight and style
    (`design/android/font`), and `FontSynthesis.None` in Compose.
- **Tabular figures** wherever a number can change (times, counts, percentages,
  durations), so text doesn't jitter. Durations read `4:02`, not `4m 02s`.
- **Secondary text is opacity, not a third grey:** `text2` and `text3` in `tokens.json`.
- **Sentence case** everywhere, except macOS button titles, which use Title Case as macOS
  does.
- **Reading measure:** the conversation is capped at about 68 characters (735–770 px on
  desktop: Slipgates Sans sets about 8% wider than Segoe UI Variable, #544) and set a step
  larger than the UI around it.
- **Mono** is never smaller than 11 px.
- **Inline mono** (mono on a line of text, #215) sits as if it were the same face:
  1. **Matched x-height.** It is scaled so its x-height matches the text face's: the scale is
     the text face's x-height over Slipgates Mono's, per platform, from `tokens.json`
     `type.inlineMono` (generated as `--ab-inline-mono-x` and `--ab-inline-mono-scale` for the
     PWA, which uses `font-size-adjust` where the browser has it, and as `AbInlineMono*` sizes
     per ramp role for WinUI, which hasn't). Never under 11 px. Only the x-height is matched:
     the width stays the font's own, so a narrow mono reads as condensed beside the text
     (#295). The 600 width (§4.1) didn't change the x-height, so the scale held.
  2. **Same baseline and line height** as the text around it. No padding, background or
     letter-spacing on an inline run, except a code span in prose (Markdown's `code`).
  3. **Same weight** as the text around it: a tool name is not bold beside regular text.
  4. **Separators are always in the text face**, with the same space on both sides, whatever
     face the run beside them is in.
  5. **What is mono:** commands, paths, agent and session ids, branch@SHA, and a tool's name with
     its arguments. **Text face:** repository, machine and model names, which people read as
     names.
  A code block is not inline: it stands on its own lines in the Code role (§4.3), and these
  rules don't size it.

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

Every platform sets these sizes in Slipgates Sans, with each role's real weight.

**Tracking** (`type.tracking`, #544 §11): **+2% (0.02 em) on the Title role and on the
wordmark**, and none anywhere else. The Title role takes it on every platform, at whatever weight
the platform sets it (SemiBold on Windows and the PWA, Bold on Apple, regular Title Large on
Android), because a large Slipgates Sans title otherwise reads a little condensed; body text, rows, meta, section headers and code keep the face's own spacing,
so the reading measure holds, and `tools/design-gen` refuses tracking on the roles that set
text. It is generated for each client: `--ab-tracking-title` and `--ab-tracking-wordmark` (CSS
`letter-spacing`), `CharacterSpacing="20"` on WinUI's `AbTypeTitle`, `SlipgatesFont.Tracking`
for Apple (em times the drawn size, after Dynamic Type, for `.tracking(_:)` or `.kern`), and
`@dimen/slipgates_tracking_*` for Android's `letterSpacing`. Material 3's and SF's own tracking
values were made for Roboto and SF, and aren't used. Tracking is spacing, not a synthesized
style (§4.2).

**The wordmark** is Slipgates Sans SemiBold, text cut, tracked +2%: the PWA's app bar name, and
and the brand assets, which move from the interim Inter Tight in a follow-up to #558. Windows' title bar keeps the system caption style, as Fluent
does.

## 5. Space, shape, layout and motion

### 5.1 Spacing and radius

- **Spacing:** a 4 px grid (4, 8, 12, 16, 20, 24, 32, 40, 48).
  - 24 between blocks in the conversation, and 8 between a speaker label and its text.
  - Panes pad 24 on desktop (the keylines below) and 16–20 on phones.
  - A row's glyph aligns with the row's first line of text, not with its centre.
- **Keylines (desktop, #771).** A base unit of 4 and a rhythm of 8. Every pane (the list,
  the session, the inspector, and full-page surfaces such as Settings) shares one set:
  - **K0, the pane inset: 24** from the pane's left and right edges (16–20 in the one column
    below 760, as phones pad). Headings, body text,
    tabs and the edges of cards sit on it.
  - **K1, text after a glyph: K0 + 24.** The glyph sits on K0 and the text 24 after it,
    whatever the glyph's size: a 16 px glyph leaves an 8 px gap, and the 20 px session mark
    (brand.md) leaves 4. It applies to session rows, Tools rows, delegate events, the delegate
    strip's items, nested delegates and Needs you items. A glyph inside a card sits on the
    card's own inner inset, and its text 24 after that.
  - **Cards** (Needs you, the delegate strip's items, the banner, Show reasoning, the reply
    box) put their edge on K0 and their text 12–16 inside it. A card's edge never hangs
    outside K0 to line its text up with K0.
  - **Gutters between panes** are K0 | hairline | K0: nothing else separates two panes.
  - **One header band** across the panes, 20 from the pane's top: a 32 px title row in which
    Sessions, the session's title and the inspector's heading sit on one shared baseline.
    Details keeps its section-header style but sits on that baseline. A two-line heading (a
    chosen delegate's "Delegate" overline over its name, §6.3) puts the name on the baseline
    and the overline in the band's top 20. Nothing else sits above the band.
  - **Meta lists** (property rows such as Details, Tools rows and delegate events) have a row
    pitch of 24. A wrapped row grows by whole lines; its next row still starts on the pitch.
  - **Scrolling under a header:** a header that stays put (a pane's header band, the delegate
    strip) shows a hairline at its foot only while content is scrolled beneath it, and none
    at rest.
  - **Overflow in a strip:** a row of items too wide for its pane (the delegate strip, like the
    machine tabs) ends in an "N more" item that opens the rest. It never clips an item
    mid-word.
  - Every margin, padding and spacing value is a step of the scale above; no other values.
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

- **Which session:** the one chosen, or with none chosen yet on a desktop, the first row.
  A phone shows the list until one is chosen.
- **The inspector's toggle** is remembered on the device.
- **Keylines at each width** (§5.1): each pane keeps K0 inside its own edges, whether there
  are three panes, two or one. K0 is 24 from 760 up, and in the one column below 760 it is
  §5.1's phone padding, 16–20 (`design/tokens.json`), so a phone has one inset, not two.
  When the session pane is wider than the conversation's
  centred reading column, the session's header, its tabs and the delegate strip follow that
  column's edges, as the conversation's cards and the reply box (§6.5) already do, so the whole
  pane shares one left edge. K0 is then the column's edge rather than the pane's.
- **Scrolling:** from 760 the window itself never scrolls. The app bar, the rail and the
  reply box stay put; a page scrolls on its own, and on Sessions the list, the conversation
  and the inspector each do (#288). A phone's list scrolls the page; a pushed session's
  panes scroll on their own.
- **A pushed session** takes the whole screen on a phone, with the reply box at its foot: the
  bottom navigation gives way to it, and Back (the header's, or the platform's own) returns
  to the list.

**Resizing (the Windows app, #297).** A divider between the list and the session, and between
the session and the inspector, wherever both show. The widths above are the defaults; at two
panes the app's list is 320 too, as long as the session keeps its narrowest.
- **Limits** (`tokens.json` `layout.windows`): the list 264–480, so a row's two lines keep a
  title and its facts (§6.1); the inspector 280–560, so Details keeps its label column and a
  value beside it; and the session never under 440, about 50 characters of the Reading role
  (§4.3) inside its padding, with the header's actions on its title's row.
- **The window still decides how many panes show.** A pane's width never hides it: short of
  room, the inspector gives way first, down to its narrowest, then the list, so the session
  keeps its narrowest and nothing goes past the window's edge. At every breakpoint the narrowest
  of each fit. The width kept is unchanged by this: the window widened again gets it back. The
  list's divider may likewise take the inspector down to its narrowest, keeping its width for
  when there is room.
- **Per device:** the widths are kept on the device, beside Your order (§6.1), across restarts,
  and never sent to a host. One list width serves two and three panes.
- **A divider** is a grip as tall as the page, with the resize cursor. It is clear at rest (the
  panes' hairline is its line), a line in `stroke2` while pointed at, and in Bridge while dragged
  or focused from the keyboard; each is a system colour in high contrast. Left and Right move a
  focused divider 16 px, a screen reader sees a splitter whose value is its pane's width, and a
  double-click puts its pane back to its default.
- **The PWA** keeps its fixed widths for now.
### 5.3 Motion

*Signed off by the owner on 2026-11-02 (#232). That includes three calls: platform
transitions keep their own timing, the in-flight progress ring is the only loop besides the
glyphs, and scrolling to a question never moves a conversation the person has scrolled back
through.*

Motion shows where something came from and where it went. It answers the person or a
change, and never runs for its own sake. The values are in `tokens.json` under `motion`,
generated as `--ab-motion-*` for the PWA and `AbMotion*` resources for the app
([`/design`](../../design/README.md)). This section is the whole list: a motion that isn't
in the catalogue below isn't in the design.

A recording of most of the catalogue on a phone-sized list and session, timed by the
generated tokens: [motion-preview.gif](motion-preview.gif), and the same under reduced
motion: [motion-preview-reduced.gif](motion-preview-reduced.gif). Each frame names the
motion it shows. The page is [motion-preview.html](motion-preview.html), and
[tools/design-mockups](../../tools/design-mockups/README.md) records it. It is a preview,
not the PWA.

**Principles:**
1. **Motion answers something:** a tap, a key, a drag, or a change worth noticing, such as
   a question arriving or a session ending. Nothing moves as decoration. Nothing loops but
   the two glyphs (§2.1) and a progress ring while a request the person made is in flight.
2. **It orients.** Whatever enters comes from where it lives (the inspector from the
   trailing edge, a pushed screen from the trailing side, a toast from its edge), and
   whatever leaves goes back the way it came.
3. **It never waits for itself.** A change takes effect at once, and motion only shows
   it. A control works during its own transition. A second tap interrupts the first
   motion instead of queueing behind it. Nothing is disabled or made untappable while it
   moves.
4. **Rows never move on their own** (§1, principle 2). Only the thing that changed
   animates. A row arriving fades in where it lands; its neighbours take their new places
   at once rather than sliding, so the list never looks as if it reordered itself.
5. **Short and calm.** About 100–300 ms (83–250 on Windows and in the PWA), and nothing
   but the glyphs longer than 300 ms. Entering decelerates. Leaving accelerates and is
   quicker. A move on screen eases at both ends. Nothing overshoots or bounces.
6. **Native where it exists.** Each platform's own vocabulary:
   - Windows: Fluent's theme transitions, connected animation, and the WinUI controls'
     own motion.
   - Apple platforms: SwiftUI springs, with no bounce.
   - Android: Material motion.
   - The PWA: CSS transitions and the Web Animations API, timed by the generated tokens. It
     doesn't use view transitions: no motion here fades a surface, so none needs a picture of
     the old screen, and a view transition would show its change a frame late under an overlay.
     It follows the Windows feel and timings, as everything else in the PWA does (§7).

   A platform's own transition keeps the platform's timing. An app that moves at a
   different pace from every other app on the device feels foreign (§1, principle 1).
   The tokens time what a client draws itself.

**Tokens** (`motion.platform`; the PWA inherits Windows):

| Token | Windows and PWA | Apple | Android |
|---|---|---|---|
| `fast` | 83 ms | 150 ms; spring 0.15 s, damping 1 | 100 ms |
| `standard` | 167 ms | 250 ms; spring 0.25 s, damping 1 | 200 ms |
| `emphasis` | 250 ms | 300 ms; spring 0.3 s, damping 1 | 300 ms |
| `crossFade`: reduced motion's fade | 83 ms | 150 ms | 100 ms |
| `standard` easing: moving on screen | `(0.33, 0, 0.67, 1)`, Fluent 2's easy ease | `(0.42, 0, 0.58, 1)`, easeInOut | `(0.2, 0, 0, 1)`, standard |
| `decelerate`: entering | `(0, 0, 0, 1)`, Fast Out Slow In | `(0, 0, 0.58, 1)`, easeOut | `(0, 0, 0, 1)`, standard decelerate |
| `accelerate`: leaving | `(1, 0, 1, 1)`, Slow Out Fast In | `(0.42, 0, 1, 1)`, easeIn | `(0.3, 0, 1, 1)`, standard accelerate |
| `nudge`, `small`, `medium` | 4, 12, 24 px | the same in pt | the same in dp |

Windows' durations are WinUI's own `ControlFasterAnimationDuration`,
`ControlFastAnimationDuration` and `ControlNormalAnimationDuration`, so what the app draws
moves at the pace of the controls around it. Android's are Material 3's `short2`, `short4`
and `medium2`.

**Catalogue.** The Token column names the `motion.catalogue` entry, then its duration and
easing in, its duration out, and how far it travels.

| Motion | Surface | Trigger | What moves | Token | Platforms | Reduced motion |
|---|---|---|---|---|---|---|
| Push and pop | Narrow widths (§5.2): list → session → delegate | Opening a row or a delegate; Back, Esc or the system back gesture | The incoming screen's surface covers the old screen at once, and its content slides 24 in from the trailing side and fades in. Pop is the mirror: the screen beneath returns at once, and its content slides 24 in from the leading side. An approval's command never slides: a session asking a question only fades in, and the list's Needs you lane is there at once. Only content fades, never a surface, so two screens never show through each other | `push`: emphasis, decelerate; medium | Windows: `Frame` navigation with `SlideNavigationTransitionInfo`, or a connected animation from the row's title to the session's, configured Direct (never Gravity's lift and shadow). PWA: the tokens on the incoming content. Apple: `NavigationStack`. Android: Material's shared axis X, with predictive back | Cross-fade |
| Pane and inspector | Wide widths: the delegate inspector, Details | The header's inspector toggle; Details; choosing a delegate | The pane's content slides 12 in from the trailing edge and fades in. The columns take their new widths at once, so text reflows once and not on every frame | `pane`: standard, decelerate; fast out; small | Windows: the tokens on the pane's content. PWA: a transition on transform and opacity. macOS: `.inspector` | Cross-fade |
| Tab switch | Conversation / Delegates / Tools; machine tabs; the phone's bottom bar | Choosing a tab | The indicator moves to the new tab, as Selection does. The new content fades in without sliding (Fluent's top-level fade) | `tab`: standard, decelerate | Windows: `SelectorBar`'s and `NavigationView`'s own indicators. Apple: `TabView`. Android: `NavigationBar` | Instant |
| Disclosure | Show reasoning, Show prompt, Tool history, the finished delegates, the Ended group, the machine list | Opening or closing it | The chevron turns, and the content fades in and settles 4 down. Its space opens at once: nothing below it slides. Closing turns the chevron back and closes the space at once | `disclosure`: standard, decelerate; fast out; nudge | Windows: `Expander`'s own motion where it is one; Show prompt's compact disclosure, the tokens on the chevron and the content (`Motion.Turn`, `Motion.Enter`). PWA: the tokens on the chevron and the content | Instant |
| Arriving and leaving | A session row (also as Needs you shows or hides it), a delegate in the strip | A host adds or removes it; a question is asked or answered; Needs you is turned on or off | The item fades in where it lands. Leaving, its space closes at once and it fades out where it was. Neighbours never slide | `listItem`: standard, decelerate; fast out | Windows: the list's `ItemContainerTransitions` replaced by this fade, because `AddDeleteThemeTransition` and `RepositionThemeTransition` slide the neighbours. PWA: the tokens on the row; a copy of a leaving row, which nothing can find or tap, fades where it was. Until #253 reaches the PWA, its Needs you entries and an approval's command appear at once and never fade or move: only an entry's mark fades in. Apple: `.transition(.opacity)`. Android: `animateItem` with no placement spec | Cross-fade |
| Status glyph | A row, the session header, the delegate strip | The state changes, such as working → idle | The old glyph cross-fades into the new one, with its colour. The words change at once. The arc and the orbit turning are §2.1's, not this | `glyph`: fast, standard | The same on every platform: opacity on the two glyphs | Instant |
| Activity dot | A row's trailing edge (§6.1) | The session updates while its row isn't open; the row is opened | The 6 px dot fades in, and later fades out. It never pulses, grows or repeats. The title's weight changes at once | `activityDot`: standard, decelerate; fast out | The same on every platform | Instant |
| Selection | The list's 3×16 Bridge pill; the rail's pill | Choosing a row or a destination | The pill slides from the old row to the new one when both are on screen, and fades in when the old one isn't. The row's fill changes at once | `selection`: standard, standard | Windows: `NavigationView`'s own pill on the rail, and the same motion on the list. Apple: the system's selection | Instant |
| Press | Every button, and every row | Pointer or touch down, and release | The pressed fill shows at once on press, and eases back on release. Nothing scales | `press`: fast, standard | Windows: the controls' own visual states. PWA: a transition on the background. Apple: the system button styles. Android: the ripple | Instant |
| Send and Answer | The reply box; a question card | Send, Send answer, Approve once, Deny | Your message shows at once as your bubble, rising 12 as it fades in. An answer's outcome fades in on the card. While the request is in flight, the button that sent it (never an option, #773) shows the platform's progress ring, which stops with the request. Nothing waits for the motion to finish | `acknowledge`: standard, decelerate; small | Windows: `ProgressRing`. PWA: the answer's ring. Apple: `ProgressView`. Android: `CircularProgressIndicator` | Cross-fade; the ring is still |
| Toast and banner | In-app toasts; banners such as a host going offline | The event it reports | It slides 12 in from its edge and fades in, then leaves the same way, quicker | `toast`: standard, decelerate; fast out; small | Windows: `InfoBar` with the tokens. PWA: the tokens on the banner coming in; a leaving one's space closes at once, and a copy of it, which nothing can find or tap, goes back the way it came. System notifications are each OS's own | Cross-fade |
| Sheet | The delegate sheet on iOS (§6.3), and any phone sheet | Opening a delegate; dragging between detents; dismissing | The sheet rises from the bottom edge and the scrim fades in. While dragged it follows the finger with no easing, and on release it settles at the nearest detent | `sheet`: emphasis, decelerate; standard out | iOS: `.sheet` with `.presentationDetents([.medium, .large])` and the system's spring. Android: `ModalBottomSheet`. PWA: the tokens on transform | Cross-fade at the detent. A drag still follows the finger |
| Menu and flyout | The row menu, Sort, the overflow menu, flyouts | A click, a long press, Shift+F10 or the menu key | It fades in and settles 4 from its anchor. Choosing an item acts at once, and the menu fades out | `menu`: fast, decelerate; fast out; nudge | Windows: `MenuFlyout`'s own. PWA: the tokens, on a popover where supported. Apple and Android: the system menus | Instant |
| Scroll to a question | The open session's conversation, and the list when a notification opens a session | A question arrives while the person is at the end of the conversation; a toast, the tray, the menu bar, a waiting row or (in the PWA, until #253 reaches it) a Needs you entry opens a session | The view scrolls smoothly to the question or the row. It never scrolls a conversation the person has scrolled back through (a response growing below on its own doesn't count: the reader is still at its end), is touching, or has the pointer over, nor one in a page or app in the background. For 500 ms after a question appears in a conversation on screen (arriving, shown as its session opens, shown again as its tab, the Sessions page or the app comes back (the window taking the focus back included, visible all along or not), moved as the inspector is shown or hidden or the window crosses a layout width, moved where it is by anything above it changing height (the title wrapping, a tool line, the response, the reasoning or your message, a kept draft dismissed or an answer's receipt going, the delegate strip or a banner appearing or going), by the conversation being pulled back as it gets shorter, or sideways as the window or a pane is narrowed or widened, or becoming answerable as its machine's hold lifts or its machine starts taking answers), or is scrolled into view (a notification's answer focused included), a click or tap on its choices, Approve once, Deny or Send is ignored, so a click already on its way to what was there can't answer it. Needs you's Approve once, Deny and Resend wait the same way after the lane changes (an entry arriving, going or moving, and its answers moved by an entry above growing as its answer goes, by anything above the lane changing height, a banner or the app bar appearing included, or by the list being pulled back as rows below get shorter or go while it is scrolled to its end; an answer's status appearing below the last answers, or the list scrolling, moves nothing and doesn't restart it) or comes back on screen, and while a session opened from it scrolls the list to its row. A smooth scroll to it counts from where it comes to rest; a scroll the person makes after that doesn't restart the wait, unless something above the answers changes height in the same moment, which counts as a move (the scroll and the move can't be told apart). A scroll pulled back to its end (what it scrolls got shorter, or it got taller) is never set aside as the person's: it counts as a move. A click or tap on an answer first reads where the answers are: moved since they were last read (an update drawn in the moment before the click), or unreadable, the click is ignored and the wait starts. A key acts at once | `scroll`: emphasis, standard | Windows: `StartBringIntoView` with `AnimationDesired`; the answers' places read after each layout pass and scroll (`AnswerWatch`) and as a pointer presses one, setting aside only a scroll the person makes (a wheel, a finger, a key or the scroll bar). PWA: `scrollIntoView` with smooth behaviour, at the browser's pace; the places read on `ResizeObserver` and scroll events (`watchMoves`) and as a click comes. Apple: `ScrollViewReader`. Android: `animateScrollToItem` | Instant: a jump |
| Reorder | The session list (#164) | Alt+↑/↓; Move up or Move down; a drag by the handle | By key or menu, the moved row slides to its new place, and the row it passed takes its own new place at once. In a drag, the row follows the pointer, a line shows where it will land, and on drop it settles into place. No other row moves | `reorder`: standard, standard | Windows: no `ReorderThemeTransition`, which slides the neighbours. Apple and Android, later: the system list's drag, whose gap follows the finger | Instant |

**Not in the design:**
- **Loops** other than the two glyphs and an in-flight progress ring: no pulsing dot, badge
  or Needs you toggle, no shimmer, no animated placeholder.
- **Parallax, bounce and overshoot,** on content or on controls. Every curve stays within
  0–1, and every spring has a damping fraction of 1.
- **Motion on data updates:**
  - A response streaming in shows as it arrives, with no typing effect and no fade per
    chunk.
  - Ages, counts and percentages change in place, with no rolling numbers.
  - Usage bars take each reading at once. A single fill-width ease when a reading changes
    was considered and left out. A reading isn't something the person did, and several
    bars easing every few minutes is motion on its own, which tells the person nothing
    new. Phase 2 removes the PWA's current 0.4 s ease on them.
- **Choreography for its own sake:** no staggered list on load, and no motion when the
  app starts, the window opens or the theme changes.

**Reduced motion.** Each platform's own setting:
- the PWA: `prefers-reduced-motion: reduce`;
- the Windows app: Windows' "Animation effects" (`UISettings.AnimationsEnabled`);
- Apple platforms: Reduce Motion (`accessibilityReduceMotion`);
- Android: "Remove animations" (an animator duration scale of 0).

Under it:
- Nothing travels: every distance is 0.
- An entry marked cross-fade fades over `crossFade`, so a screen or a toast doesn't
  just blink in. Everything else changes at once.
- The glyphs and the progress ring are still (§2.1, §9).

The PWA's generated tokens do this by themselves: their reduced-motion block sets every
duration to 0 or the cross-fade, every distance to 0, and `--ab-motion-travel` (the scale
for travel no distance token measures, such as the sheet's own height) to 0. The platform
transitions follow the setting already. The app's own animations check it and follow its changes, as the
status glyph does, and each entry's `AbMotion*Reduced` resource says which form it takes.

**In the Windows app,** the Sessions page is one page laid out by width, not `Frame` pages, so
push and pop are the `push` tokens on the incoming content, as in the PWA, rather than a
`SlideNavigationTransitionInfo`. Everything the app draws itself runs as a composition
animation on the element's visual, which changes neither layout nor hit testing: a control
works while it moves, and a second change replaces the first motion. The session list's
`ItemContainerTransitions` is empty. The decisions (what is a push, an arrival, a question to
scroll to) are App.Core's, tested on every platform; the durations, curves and distances are
read from the generated `AbMotion*` resources.

The clients implement this after U5 and U6 (#232, phase 2). Each tests that reduced motion
makes every change instant or a cross-fade, that no motion runs longer than its token, and
that a control can be used while it moves.

## 6. Surfaces

### 6.1 Session list

Sections, top to bottom:

1. **Needs you · N** (#165, #253): a toggle, not a lane. It is the list's first item, under the
   machine tabs, shown while any question waits or while it is pressed.
   - **N** counts pending questions on every machine connected, whatever the machine tabs show.
     It is the app's badge (§7): the Windows app's tray and Sessions badge, the PWA's app badge
     where the browser has one, and on Apple the Dock badge, the menu bar extra and the icon
     badge.
   - A question on a machine that is away, or back but not yet confirmed by its next full list,
     is neither counted in N nor shown by the filter, nor raised as a notification (#269).
   - **Look:** the `needs` glyph (`idle` at none), then "Needs you · N". It is a control, so it
     takes the platform's button styling and never a status colour. Pressed, it has the
     selection's look (the quiet fill, the 3×16 Bridge pill, semibold words) and, at its
     trailing edge, "Showing N of M", or "N on *machine*" with a machine chosen. It is 36 px tall
     on a desktop and 44 on a phone.
   - **Pressed:** the list shows only the rows waiting on you, in Pinned and Sessions, in their
     own order and under the Sort menu's choice. Groups with none, and Ended, are hidden.
     - Nothing moves. A row answered while the filter is on stays where it is, with its new
       glyph and words, until the filter is turned off. A question arriving adds its row where
       that row lives.
     - With a machine chosen, it narrows that machine's rows. When others wait elsewhere, the
       list ends with one secondary button, "N more on other machines", which chooses All.
     - With nothing left waiting it reads "Needs you · 0", keeps the rows already shown, and
       says "Nothing else needs you" under them.
   - **Not kept:** the filter starts off each time the client starts.
   - **It draws no question's words and offers no answer.** Choosing a waiting row, filtered or
     not, opens its session at its question (§6.4). A click, a tap or Enter puts keyboard focus
     on the question's heading. Arrow keys only move the selection.
   - **Several questions in a session** (if a host ever sends them; protocol v1 has one): one
     row, whose status reads "N questions waiting", and N counts each question.
2. **Pinned.**
3. **Sessions,** in your order (#164).
4. **Ended · N,** collapsed.
5. **Collaborators** (#519), once you have a link: one group per collaborator, by verified
   @login, with their machines nested, after your own sessions (§6.7). Needs you's N never
   counts their items, and its filter never shows them.

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

**A machine away (#269).** A host's relay link drops now and then (a restart, an update, a
network blip), often for under a minute. While its machine has left the relay's presence, and the
client's own relay link is up on the same account:
- Its rows stay where they are, read as stale (§2.2), and so do the session open, its machine tab,
  your order and pins.
- Nothing on them can be done: answering, Approve once and Deny, replying, attaching, End session,
  and launching or resuming on that machine are disabled. Their questions leave Needs you, the
  badges and notifications.
- Back again, they can be acted on only once the machine's next full list (its snapshot) has
  confirmed that the session and its question still exist, never on the copy kept meanwhile. A
  session that ended meanwhile goes to Ended then, and a question that ended never becomes
  answerable. A question notification withdrawn meanwhile comes back silently if still asked.
- The same hold applies, for a moment, to a machine still connected whose events had a gap (some
  were lost, so it is asked for its full list again; v1-sessions.md): its rows can be acted on
  again once that list is whole (#427).
- After **15 minutes** away its rows go, and the machine is listed as offline (§6.6). It goes at
  once when it is forgotten, when the client is signed out or refused, or when the account changes.

**Row anatomy (two lines):**
- **Leading:** the status glyph, aligned to line 1.
- **Line 1:** the title. Semibold means unread activity, and the 6 px Bridge dot appears at
  the trailing edge.
- **Line 2:** at most two facts, for example "3 delegates · DEV-VM1", "Editing
  LaunchView.xaml.cs · ULTRA" or "Driven by an agent · ULTRA" (in agent colour).
- **Trailing:** the age (tabular), when there's no unread dot.
- **Never on a row:** the model, the repo SHA, turn and compaction counts, or the pid.
  Those live in session details. Nor a question's words: a waiting row says only its status
  ("Waiting on you", "Delegate *name* asks"), and the question is in the conversation (#253).
- **Line 2 is one line,** cut short at its end; the title is too. Every fact is still in the
  session's header or Details, and the row reads in full to a screen reader (§9.2).

**Facts on a line (#214).** Wherever facts are joined by "·" (a row's line 2, the header's
meta line, the status words themselves): the separator is in the text face whatever face its
neighbours are in (§4.2), with the same space on both sides, and it **stays with the fact before
it**. A line may end with a separator, and **no line ever starts with one**. In the PWA that is a
no-break space before the dot and an ordinary space after it; in WinUI, a run that keeps the
dot with the preceding fact.

**Facts are kept apart until they are drawn.** A status (§2.2) is a list of facts, joined with
" · " only to be shown; a row's line 2 takes its first fact from the list, never by cutting the
joined words at a separator, since a fact can carry a delegate's name (#286).

**Machine filter:**
- Windows and the PWA: SelectorBar-style text tabs: All, then each machine by name.
  - **They never scroll sideways** (#290): a thin scrollbar under them hid the machines past the
    pane's edge. As many tabs as fit show, in their order, and the chosen one always shows, in its
    own place. The rest are under **"N more"** at the tabs' end, a button whose menu lists each
    machine with its state, as the tooltip says it. One chosen there is selected and takes its
    place among the tabs shown, the last of the others giving way to it.
  - **A long name** is cut short with "…" at `layout.windows.machineTab` (128 px). A tab's tooltip and
    accessible name are the whole name and the machine's state: "Build server · 3 sessions", with
    "· View only" for a read-only machine (§6.6), "connecting…" while it connects, and "offline"
    while it is away ("A machine away").
  - **Keyboard:** the arrows move through the tabs shown, Tab goes on to More, and Enter or Space
    opens its menu. Touch and the mouse reach More as any button.
  - It holds at the narrowest list (264, §5.2) and in one column. The PWA's tabs still scroll
    sideways (#351).
- macOS: a "Machines" sidebar section.
- iOS: a toolbar button.
- Android: filter chips.

### 6.2 Session header

- **Title:** the session name.
- **One meta line:** glyph · status text (rule set §2.2) · machine · repo.
- **Actions:** icon buttons with tooltips: Pin, Details (everything removed from rows),
  and toggle inspector. End session lives in Details on desktop, and in the overflow menu
  on phones.
- **Phone:** Back (to the list) leads the header, and Pin, Details and End session are in its
  overflow menu.
- **Details** is a property list: Agent, Machine, Repository, Branch (mono), Folder (mono, and
  a long path gives up its middle, never a word), Model, Started, how long in this state,
  background agents and commands, turns, compactions and the process id; then End session.
  At three panes it heads the inspector, over Tools (each call: glyph, name and arguments in
  mono, age). Below three panes Tools is a tab beside Conversation, and Details takes the
  conversation's place until pressed again. Delegates (#176) join the inspector and the tabs: a
  delegate chosen takes the inspector in place of Details and Tools (§6.3).
- **An `ask_user` call in Tools** (#253) is one line without its summary. While its question
  (Question `id` is the call's id) waits, it reads "Waiting on you", a link to the question.
  After, it reads the call's reported status ("Done", "Failed"), since a call can end without
  an answer. "Answered" is said only for an answer this device sent and the host confirmed. The
  words link to the question while the conversation keeps it (§6.4). "Show summary" in the row's
  menu discloses the `summary` the host sent (at most 200 bytes; the protocol sends no
  arguments), for diagnosis, closed by default. The call an approval holds keeps its summary: it
  is the action, not the question.

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
  - a property list: Status, Model, Kind (background or task), ID (mono). A delegate that asks
    has Status "Asks you", a link to its question (with "· answer at the machine" when so),
    and never the question itself (#253);
  - Task (the prompt it was given), Latest (its latest output), and Reasoning (collapsed);
  - Tools: mono, glyph per call, tabular duration;
  - nested delegates, listed under their parent.
- **Choosing:** at three panes a strip item shows its delegate in the inspector, in place of
  Details and Tools, bringing a hidden inspector back; pressed again, it lets go. Below three
  panes the Delegates tab lists every delegate under its parent, finished ones folded, and the
  one opened is pushed in the list's place with "All delegates" to go back. A phone's chip
  pressed again lets its delegate go too, unless another tab hides it: then it shows it. A
  delegate stays open across a resize, and as it finishes (its fold opens).
- **A host that lists none** (Claude, Codex, a host before sub-agents) gets no strip; its
  Delegates tab, while it counts background agents, says only how many.
- **One action:** "Ask main agent to stop it". It pre-fills a reply to the main agent, after
  anything already typed, and sends nothing on its own. There is no per-delegate typing or
  Stop, and a finished delegate has no action.
- **Delegate names** are the delegate's own words, so one could pose as the main agent.
  Every surface shows the same safe label (the app's `DelegateLabel`): bidi controls,
  zero-width, format, control and other default-ignorable characters are removed, any
  whitespace (line and paragraph separators too) becomes one space, and the result is cut to
  60 characters with "…". The blank-looking characters a code block escapes (§6.4, #546) need no
  escape here, since a label never runs: the spaces among them become one space, as above, and
  the check below reads the braille blank U+2800 as a space too. A middle dot, and the dots drawn like it (U+00B7, U+0387, U+1427,
  U+16EB, U+2022, U+2027, U+2219, U+22C5, U+2E31, U+2E33, U+30FB, U+318D, U+A78F, U+FF65,
  U+10101), becomes "-": "·" separates
  a status's facts, and a name with one would add a fact of its own ("Delegate Idle · Read logs
  asks", #286). With nothing left, it is "Delegate" and the first 8 characters of
  its id. A name that reads as the main agent's shows as `Delegate “<name>”`, any quote of
  its own made a plain apostrophe so it can't close them: every character Unicode files as an
  opening or closing quote (Pi, Pf) or marks Quotation_Mark (" ' „ ‚ ⹂ 「 」 『 』 〝 〞 〟 ＂ ＇
  and their kind), and those drawn like one: primes (′ ″ ‴ ‵ ‶ ‷ ⁗), the Hebrew geresh and
  gershayim (׳ ״), ʹ ʺ ʻ ʼ ˝ ˮ, and the ornament quotes ❛ ❜ ❝ ❞ 🙶 🙷 🙸. To decide
  whether it reads so, the name is
  NFKD-folded, with marks and case dropped by code point (so a combining mark outside the BMP
  goes too), and Cyrillic, Greek, Armenian and Latin IPA letters drawn like those of "main
  agent" are read as them (а, і, М, Μ, α, ι, ո, ɑ, ı and the like), then checked for "main"
  followed by "agent…", or "mainagent…". The host sends such a name already in that form
  (design/subagents.md §6, #263), which is shown as it is rather than quoted twice. No such
  list is complete, so it is not the guard:
  every line that says who asks or works puts the word "Delegate" before the label ("Delegate
  *name* asks" in the status line, the row and the question's heading; "Working · task:
  Delegate *name*"), as the conversation's events do, and the main agent's own lines never
  have it. The inspector's heading has a
  fixed "Delegate" overline above the name, and the pre-filled reply names the delegate by its
  id alone ("Please stop your delegate *id*."), never by its name; an id with anything but
  ASCII letters, digits, `-`, `_`, `.` or `:` gets no action.

### 6.4 Conversation

- **The column:** the pane's width up to the reading measure (§4.2), whatever it holds, and
  centred in the pane once the pane is wider than the measure, as the PWA's is; it follows the
  pane's width as it changes, a divider being dragged included (§5.2). The scroll bar is at the
  pane's edge, not the column's.
- **A list of blocks** (#765): the response's top-level blocks (a paragraph, a heading, a list,
  a table, a code block, a quote, a rule) are the items of a virtualised list, so only those near
  the viewport are laid out and a live resize re-wraps those alone. Everything else in the column
  (the question, the receipt, your message and the response's label above; the reasoning, the
  delegates' events and the current tool below) is the list's header and footer. It draws exactly
  as one column did: the same reading measure, keylines and spacing, no selection, no item chrome,
  and no focus visual on an item; focus goes to the links and buttons inside a block, as before.
  - **The place is kept by the block, not the offset:** the block across the viewport's top edge
    stays where it was through a change of width (a live resize, a divider dragged), and as blocks
    are laid out for the first time and their estimated heights are corrected: a layout that moves
    it while the view is still puts it back in the same pass, before it is drawn; a scroll or a pan
    takes it again where it is. With the header at the top, nothing is held.
  - **Accessibility (#707):** a screen reader gets the response as a list, each block a list item
    numbered in the set ("12 of 214"), the header and footer outside it, so its scan and search
    reach a block that is off screen and bring it into view. An item is named by its text, hidden
    characters as their escapes as a code block draws them, cut at 500 characters at a whole
    grapheme with "…"; the whole text is read from its content. Its
    control type is said as the system says a list item.
  - **The keyboard reads it as a page, not a list:** the arrow keys scroll a line, Page Up and Page
    Down a viewport, Home and End (Ctrl too) to the top and the end, under reduced motion without
    animation; none moves between blocks, nor does any of them with Ctrl or Shift, and Left and
    Right do nothing to the page (a wide table still scrolls sideways with them, and selected text
    keeps Shift and the arrows, and with caret browsing every arrow). A key with Alt is left to the
    window, and one pressed in a text input or a control that uses it (a combo box, a slider, a
    list of its own) to that control, Ctrl+Home and Ctrl+End included. Tab and Shift+Tab stop at
    every link and Copy button in order, a block not
    fully in view included, laid out or not: it is brought into view with its start at the leading
    edge (at once, without animation, so under reduced motion as well), and focus goes to its first
    stop (its last, going back), whether that is a link or a Copy button; a block fully in view
    doesn't move; a block with none is passed. That holds when Tab or
    Shift+Tab wraps round the window too: when nothing after the response is a stop, Shift+Tab from
    the window's first control goes to the response's last block with a stop, laid out or not. A
    block taller than the view lands with its start at the leading edge, unless that would leave
    the focused element out of view. Then the view scrolls only as far as it takes to bring the
    focused element wholly into view at the nearest edge, and the block's start stays as close to
    the leading edge as that allows. Focus is never scrolled out of sight (A4, WCAG 2.4.11).
    Known gap (#881): if focus is on a stop scrolled out of view and Tab lands on a stop already in
    view, the view may return toward the stop focus left. The landing stop stays wholly in view.
    The intended behaviour is that the view stays put. No block's container is
    a stop, and in high contrast it has no fill: the block draws in the system's colours.
  - The self-test checks that a block stays in place through a live resize and the conversation is
    drawn alike before and after it (a renderer's noise aside), that Page Down and Down scroll the text with focus and
    selection left alone, that Tab reaches a block not yet laid out, from a Copy button or from a link
    to the link in the next block, brought in at the leading edge, that Tab onto a Copy button in a
    block across the view's bottom edge brings the block's start to the leading edge and onto one in
    a block wholly in view moves nothing, even with its top near the view's (Shift+Tab), that
    Tab onto the first link of a block taller than the
    view brings its start to the leading edge and Shift+Tab onto its last brings that link wholly
    into view at the nearest edge, that no container is a stop or
    draws chrome, that the last block is found by its name, which no other block has, and that every
    block, found and realised as a screen reader does it (FindItemByProperty, then Realize), is a
    list item numbered 1 to N in a set of N, none -1 ([app-windows.md](../app-windows.md)).
- **Your messages:** a bubble at the right edge of the conversation's column (card fill,
  platform radius), at most 85% of the column's width, so a short message beside a short reply
  is still at the right (#255).
- **A prompt you didn't type** (#489): a schedule's (`activity.userMessageFrom` `schedule`), another
  agent's (`agent`), or one the host knows only as not yours (`other`, and any other text a
  client doesn't know). It is **never a bubble**, and never at the right, which is yours only.
  A prompt from you, and every prompt from a host that doesn't send the field (or sends it
  null), keeps the bubble. A sender that isn't text is refused with the whole `activity`
  ([v1-sessions.md](../protocol/v1-sessions.md), strict fields), so no prompt shows at all.
  - **At the column's start, across it,** like an event line; on a phone, a full-width row.
  - **A label line in agent colour, with an icon:** a clock (`icons/clock.svg`) and "Scheduled
    prompt · #4" for a schedule (its `userMessageLabel`: "#" before a number, anything else in
    quotes); the
    `driven-badge` diamond and "Sent by an agent" for an agent, with `driverPrincipal` after it in
    secondary text when the host knows; the diamond and "Automatic prompt" for `other`. Then the
    age in meta style: when its turn began (`statusSince` when it started running, seen here). The
    protocol doesn't date a prompt, so one first seen after its turn has no age.
  - **Collapsed by default:** two lines of it in secondary text, then **Show prompt**, with the
    Disclosure motion (§5.3): the chevron turns and the text settles in, nothing below slides, and
    it is instant under reduced motion. Open, it reads "Hide prompt" and shows all of it in reading
    style inside a thin (2 px) agent-colour rule on the leading edge, not a filled card. A new
    prompt starts collapsed; an update to the same one leaves it as the person left it.
  - **Repeats fold.** When a schedule's prompt has the same words as the last one shown under the
    same label as drawn (two labels an agent wrote apart but drawn alike are one),
    the line says "Scheduled prompt · #4 · same as last time" and has no preview; Show prompt still
    opens it. A session carries only its latest prompt, so the previous one is the one this view
    saw: a new prompt is one whose words or sender changed, or, with the same words, a finished
    turn more (`turnCount` while idle) than this view saw once that prompt had settled (been seen
    idle, its own turn counted); turns that end before then are never put down to it. So a
    schedule's repeat folds once its turn is done: while it runs it looks like a turn
    no prompt started (Claude's for a delegate reporting back). A prompt seen for the first time
    never folds, since a wrong "same as last time" would hide words that changed.
  - **Its words are an agent's.** The label is cleaned as a delegate's name is (§6.3: nothing hidden
    or steering, one line, no middle dot of its own, so it can't add a fact such as "same as last
    time"), cut to 64 bytes at a whole character, and, unless it is a number, in quotes, so no label
    reads as words of the line's own ("Scheduled prompt · “Main agent”").
    The prompt's hidden and bidi characters are drawn as escapes (`\u202E`). All of it is text.
  - **Accessibility:** the label line is read before the prompt. Show prompt is a real button,
    toggled with Enter or Space, named for whose prompt it is, then its words: "Scheduled prompt 4,
    Show prompt" or "Prompt sent by an agent, Show prompt", with `aria-expanded` saying collapsed or
    expanded. The words shown stay in the name (WCAG 2.5.3), and the state is said once.
  - **High contrast:** the agent role maps to a system colour, and the icon and the words carry
    the meaning, never the colour alone.
  - **Lists:** no row shows a prompt (§6.1), so a row is unchanged. Where a card shows the latest
    prompt, one you didn't type is "Scheduled prompt · #4" or "Sent by an agent" in agent colour
    with a one-line preview, never a quote of yours.
- **Main agent:** a label line ("Main agent", then the age in meta style), then reading
  text. Reasoning is behind a "Show reasoning" disclosure, and is always the main agent's
  own. "Main agent" only where the host says whose the words are (`activity.source`, `own`
  or a delegate list); otherwise the label is the agent's name ("Claude"), as such a host may
  send a delegate's words as the response.
- **Delegate events:** one line each, meta style, with an icon: "Started 3 delegates ·
  26m", "Delegate *Read witness shell logic* reported back · 22m". The name links to the
  delegate, and the word "Delegate" stays outside the link.
- **Questions** (#253): in the stream, as a card in Needs you colours. It is the one place a
  question's words are drawn, and the one place in the app it is answered. The header reads
  "Main agent asks" or "Delegate *name* asks". Then come the question and the platform's own
  radio, checkbox and text controls, with Send answer in Bridge, or Approve once (Bridge) and
  Deny, as "Answering" says below.
  - **Answering** (the owner's decision in #773): every question is answered the same way on
    every client. The person chooses or types first, then presses one **Send answer**. Nothing
    sends on a tap: not an option, not Yes or No, and not a lone single choice.
    - **One control per field type, the platform's own:**

      | Field | Control | Selected |
      |---|---|---|
      | Single choice | A radio list, one option per row | The platform's checked radio |
      | Multiple choice | Checkboxes | The platform's checked box |
      | Yes or no | A two-option radio group, Yes then No, on one line | The platform's checked radio |
      | Text | The platform's multi-line text field | — |
      | Number | The platform's text field with a numeric keyboard | — |
      | Other… | The last radio or checkbox in its list; choosing it, or typing in it, reveals and fills a text field under it | As its list |

      The option the agent recommends keeps its place at the top of the list and carries a
      plain "Recommended" tag after its label (meta style, `text2`). It is never filled with
      Bridge, bordered with it or set in bold. A default the host sends is pre-selected, and
      has no tag of its own. A field's head is its title, then "Required" when it is required.
      It has no type hint ("Choose one", "Choose any", "Yes or no", "Text answer", "Number"),
      since the control already says that.
    - **An optional single choice or Yes or no can go back to unanswered:** once one of its
      options is selected, a host default included, a plain "Clear" link follows the group on
      its last line, and pressing it unchecks the group. It is absent while nothing is
      selected and on a required field, and is named "Clear *title*". Pressing it moves focus
      to the group's first option, without selecting it, before Clear goes (A4).
    - **One primary per card:** Send answer is the only button with Bridge's fill. A selected
      option is the platform's checked control and never takes the accent fill, so it never
      reads as a button that would act.
    - **The footer row** is the card's last row. Send answer is right-aligned, at the same
      right edge as the reply box's Send (§6.5), with its label as written: "Send answer" on
      every client, Apple included. The answer's progress and outcome line (§6.5's receipt)
      sits left of it on the same row, and wraps to the line below when the card is narrower
      than both, with Send answer staying right. Try again, when an answer failed, takes Send
      answer's place on the row.
    - **Send answer is shown whenever the card can be answered here, and never disabled.**
      Pressing it with a required field unanswered, or a value the host would refuse, sends
      nothing: focus moves to the first such field, the field shows its inline error ("Choose
      an option", "Enter a number"), and the error is said once (§9.2). It is absent where
      the card can't be answered here (`answerAtMachine`, `truncated`, resolved, a
      collaborator's read-only view, §6.7) and while a sent answer's outcome forbids sending
      another (§6.5). While its machine is away it stays, disabled, as §6.1's controls do. Why
      visible and not hidden until complete: the rule that a button which doesn't apply is
      absent, not disabled (A7), is about actions that do nothing in the current state, and
      an open question's Send answer always applies. Hidden until complete, the card's only
      action would be missing on arrival, nobody could find out what was missing, and the
      card would grow a row under the person's finger as they finished. Pressing it to learn
      what's missing is WCAG 3.3.1's error identification.
    - **While an answer goes,** §6.5's receipt runs on Send answer, as on the reply box's Send:
      its ring, then its check, then the exception-only line. What was chosen or typed stays
      selected and read-only (the fields keep their values and focus, A4) until the outcome.
      Busy and refused make the fields editable again, with Send answer back. Not confirmed
      and stale keep them read-only, with no Send answer. Failed keeps them read-only, with
      Try again, which resends the same request, so the same answer, never a new one.
      Focus is never dropped as these change (A4). Send answer keeps focus through its ring
      and check. If focus is on Send answer when it changes: when Try again takes its place,
      focus moves to Try again, and when it goes with nothing in its place (not confirmed,
      stale), focus moves to the card's heading, whose description is the outcome line. If
      focus is anywhere else, it stays there, so an outcome arriving later never takes it.
    - **Keyboard:** Tab walks the fields in order, then Send answer (A3). Arrows move and
      select within a radio group, Yes and No included, and Space checks a checkbox. Enter in
            an answer field (a radio, a checkbox or a one-line field) sends, as pressing Send
            answer does, so an incomplete answer shows what's missing rather than sending. In a
            multi-line text field, Enter is a new line and Ctrl+Enter (⌘↩ on Apple) sends. On
            anything else in the card, a link in the question or Clear, Enter keeps its own action
            and sends nothing.
    - **Spacing** (§5.1): 16 inside the card, 16 between fields, 8 between a field's head and
      its control, and 16 above the footer row. Each option row is at least the target height
            (A11: 44 on touch, 32 with a pointer) and grows with a label that wraps or is scaled
            up (§4.1), never clipping it, with 8 between a control and its label. The
      Windows grid audit (#771) may refine these keylines; this section follows it when it
      does.
    - **Approvals are unchanged:** one press of Approve once (Bridge) or Deny answers, with
      nothing to choose first. They sit right-aligned in the same footer row, Deny then
      Approve once at the right edge, with the receipt line to their left.
    - **Each client:**
      - The Windows app and the PWA draw it as above.
      - Apple: `Picker` with `.radioGroup` on macOS and an inline (checkmark) list on iOS for
        single choice and yes or no, `Toggle` with `.checkbox` on macOS and a checkmark row on
        iOS for multiple choice, and `TextField` for text and numbers. Send answer is the
        section's trailing, `.borderedProminent` button.
      - Android, when it comes: `RadioButton` rows, `Checkbox` rows, an outlined `TextField`,
        and a filled `Button` for Send answer at the end of the row.
      - Home Assistant: a `select` per field and the Send answer button, which is already
        choose-then-send, so its card stays as it is. An approval there keeps Approve and
        Deny, each its own press.
  - **One card per question id:** an update to the same question changes its card in place,
    and never adds a second.
  - **An approval is drawn as it runs,** since Approve once allows all of it: exactly as
    written, in the code face, never read as Markdown, with its hidden characters,
    right-to-left letters and bare carriage returns escaped and the code notes beside it, and
    no Copy ("An approval's card", below, #220). Approve once stays offered whatever it holds,
    the owner's "escape and allow" (#253). It wraps and is never cut, so a long command or a
    diff keeps Approve once.
  - **A host sends an approval's hidden characters and right-to-left letters as escapes**
    (`\u202E`, `\u05D0`, #220). The card's notes, and Needs you, which never offers such a
    prompt inline (§6.1), count a host's escape as the character it stands for.
  - **An approval Markdown would not draw as written** is held at the machine by the Windows
    app's card and by Home Assistant's, though both draw every approval as its source (the app's
    since #323; Home Assistant's in one code block nothing in it can close, #220): a character
    reference that would decode to such a character (`&#x202E;`), the agent's own words closing
    the command's fence, a link, an image or HTML, text after a block, or a line in a block shaped
    as a fence (`ApprovalShape`). Both say the same notes above an approval.
  - **The agent's own words in an approval** (its intention, a tool's title or description) are
    text, never Markdown: a host escapes them (#212), so nothing that reads an approval as Markdown
    can take them for a fence around the command or a link's words in place of where it goes.
    Every card draws an approval as its source, so the PWA's, the Windows app's and Home
    Assistant's show their backslashes. A toast shows the words without their escapes, the command
    as written, and anything parsing decodes (a reference to a right-to-left letter) escaped again.
  - **A Codex approval** is approved from here only as a host sends it from #209, the whole
    command in one block (`ApprovalShape.IsFencedCommand`). An older host sent the hook's
    300-character preview in prose, cut short of what runs with nothing marking it `truncated`,
    so every card answers that at the machine (#209). One a host had to cut is marked `truncated`,
    and every card says part of it is missing before it judges the shape. The Mac, iPhone and iPad
    app does not check the shape yet, so it answers every Codex approval at the machine until it
    does.
  - **`answerAtMachine`:** the whole question, no controls, and one line in Needs you's text
    colour: "Answer at the machine · *machine*". A prompt that reached the device cut short
    (`truncated`) can't be answered from here either, as before. Leaving the session is never
    an answer: the row keeps its glyph until the host clears the question.
  - **Resolved:** the card quietens, with no Needs you fill, and reads "Main agent asked"
    (or "Delegate *name* asked") over the question as it was shown. Below that:
    - with this device's answer record for the question, the record's progress and outcome as
      the card shows them: "You answered *value*" once confirmed; "Sent, but not confirmed.
      Check at the machine" for `unconfirmed`; refused, failed and stale as they are;
    - with no record (answered elsewhere, or the call ended without an answer), "No longer
      waiting", since the protocol doesn't say what was answered elsewhere.
  - **History:** a resolved question stays as a read-only item where it was asked, across later
    turns: the latest 20 per session, for as long as the client runs, none across a restart.
  - **Several at once** (if a host ever sends them): one card each, in the order asked, with
    "1 of N" in the heading.
- **Now:** the tool the main agent is running, as a meta line under its reply (glyph, then the
  tool's name and arguments in mono). Not while the session waits on a question: the card says
  what is happening.
- **Code blocks** (#296): in the Code role (§4.3), wrapped to the block's width rather than
  scrolled sideways, with Copy in the top-right corner above the code (with the copy icon in the app):
  "Copy", then "Copied" (or "Copy failed") for 1.5 s, named "Copy code" whatever it shows, the
  outcome said to a screen reader. Every code block reads strictly left to right, and a
  character that is not on screen as it runs (bidi controls, zero-width and other format or
  invisible characters, and a bare carriage return, which a shell does not take as a line break)
  is drawn as a visible escape such as `\u2067` (#220, the owner's
  "escape and allow" rule from #253): an agent's isolates must never draw a download as a comment.
  So is a character that draws blank but that a shell takes as part of a word (#546): every
  Unicode space but the space itself (U+00A0, U+1680, U+2000–U+200A, U+202F, U+205F, U+3000) and
  the braille blank U+2800, besides the Hangul fillers and U+17B4/U+17B5, which are
  default-ignorable. Drawn blank, `true\u00A0#; curl … | sh` read as a comment, and bash ran the
  `curl`. A tab is drawn as a tab. A look-alike that draws visibly, such as the fullwidth `＃`
  (U+FF03), is drawn as itself: no list of them is complete, and escaping every one would make
  Chinese, Japanese and Korean text in code unreadable, so reading them is the person's, as
  telling `0` from `O` is.
  Right-to-left letters are escaped too (`\u05D0`), since even in a left-to-right line the
  punctuation between two of them joins their run and is drawn reversed: `echo א#;ב;rm -rf ~` would
  read as commented out. This is the owner's trade-off (#253): Hebrew or Arabic in a code block is
  harder to read, so that what is read is what runs. **A block says what its escapes stand for,**
  beside Copy: "Contains hidden characters" when it holds any, and "Contains right-to-left text"
  when it holds right-to-left letters, both if both. An escape can be read past while its
  character still acts: PowerShell ends a `#` comment at a bare CR, so
  `# just a note\u000D Write-Output RAN` drawn on one line still reads as a comment, and runs.
  **Copy takes exactly what is drawn,** escapes included, every space, tab and line break, never
  the lines it wraps into, and not the whitespace-only lines a block may end with (pasted, they
  would run the last line; #429). Needs you offers no answer (§6.1): an approval is approved on
  its card, below. The Windows app does this since #296, and the PWA since #429 (#415).
- **Who sent a message** (#519): your own agent's message has the filled Agent diamond and
  "Sent by copilot on *machine*"; an external agent's has the **outlined** External diamond, a
  dashed External outline and "external · Sent by an agent on *machine* · **@login**" (§6.7).
  A message from an external agent held for you has its own card (§6.7), unlike an approval's.
- **An approval's card** (#220): **an approval's message is drawn exactly as written, fence
  markers included, in the code face; nothing in it is read as Markdown.** It is one block, read
  left to right with its line breaks kept, and escaped as every code block is (hidden characters,
  right-to-left letters and a bare carriage return), with the same notes, and Approve once still
  offered. **Its notes are read from the message as it came** (#423): a CRLF is drawn as the line
  ending it is, but its CR is still in what runs, so the card says "Contains hidden characters"
  for it, as the Needs you lane refuses it (`cd /tmp/scratch\r\nrm -rf ./*` in bash fails the
  `cd` and runs the `rm` where it is). The app follows in #422.
  **It has no Copy:** the block is the whole message, prose and fence lines too, and pasted
  into bash a line of backticks runs as a command substitution. Ordinary code blocks keep Copy. Whatever a parser decides can hide what runs: a link draws
  `echo "[safe](https://a/";curl…|sh;")"` as `echo "safe"`, `*tmp*` loses its stars, and a line
  of three backticks, which Markdown takes as a fence, is a command substitution to bash, so a
  fenced `echo rm -rf ~` inside `bash -c '…'` runs `rm -rf ~`. A host sends a Codex approval's
  whole command in a block (#209); an older host sent it in prose (`Needs approval: …`), the
  hook's 300-character preview cut short with nothing marking it truncated, so a card draws that
  exactly as written and answers it at the machine. Other questions and the conversation keep
  their Markdown.
- **Markdown the parser can't take** (#440): an agent's text that the Markdown parser fails on,
  or could read only slowly, is drawn as written: one paragraph of plain prose, its line breaks
  kept, never dropped and never read as Markdown. Since an agent can send text here on purpose,
  it is revealed as code is: hidden characters, right-to-left letters and a bare carriage return
  are drawn as escapes. It is such text when it is longer than 64 Ki
  characters, nests quotes, lists or inlines more than 32 deep (50,000 `[` once crashed the app,
  and 5,000 overflowed its stack), or holds more inline markup than the parser handles quickly in
  one paragraph (20,000 `[](` took seconds; every entity, backslash escape and line break counts,
  as each is a node of its own). The limits sit far above what an agent writes: the
  protocol sends at most 16 KiB of response, and a 16 KiB table of 250 rows, each with code, a
  link and bold, is read as a table. The Windows app does this since #440; the PWA's is #439.
- **A word too long to lay out** (#493): WinUI's text layout fails on about 32,000 characters
  with nowhere to break after other text on their line ("Done: " and 32,000 `[`), and the app
  ended. The Windows app guards it twice. **What a machine sends is held to the protocol's limits
  as it arrives** (v1-sessions.md): a response or a delegate's response longer than 16 KiB keeps
  its end and shows "Earlier text not shown: the response was longer than the machine sends.",
  reasoning keeps its last 4 KiB, and a question's message longer than 8 KiB keeps its start and
  is answered at the machine, as one the machine had to cut. **And prose is never drawn with more
  than 8,192 characters between places a line may end** (a quarter of the measured limit): past
  that, a break of no width goes in, just after a space where there is one, never inside a
  letter and its accents. Nothing is added or lost: every character is drawn as written, and
  selecting and copying the text gives exactly what was sent. Code blocks, drawn another way,
  lay out 40,000 such characters and are unchanged.

### 6.5 Reply box

- **Width:** the conversation's column, the pane's width up to the reading measure (§4.2), whatever
  has been typed (#294), and centred under it in the pane, as the PWA's is (§6.4).
- **Placeholder:** "Message the main agent". Replies only ever go to the main agent.
- **A collaborator's session has none** (#519): the reply box is absent, and the read-only line
  stands in its place (§6.7).
- **Controls:** Attach, then Send (Bridge, icon only, with a tooltip naming the shortcut:
  Ctrl+Enter, or ⌘↩ on Apple).
- **Send answer shares Send's edge and receipt** (#773): a question card's Send answer, and an
  approval's Approve once, end at the same right edge as Send, and show the same in-flight and
  outcome states as Send: its ring, its check and the exception-only line, with the same quiet
  window and announcements (the owner's #600 decision). The states #600 gave a tapped option
  (its ring on the option, the option kept pressed, "one attempt per prompt") belong to Send
  answer instead, as §6.4's "While an answer goes" says: the ring is on Send answer, and the
  chosen value stays selected and read-only. No client draws a ring on an option.
- **Platform styling:**
  - Windows and the PWA: a card with a 2 px Bridge bottom edge.
  - macOS: a capsule field.
  - iOS: a capsule field over the keyboard.
  - Android: a filled field on `surfaceContainer`.

### 6.6 Machines

A page of its own on every client (the PWA's `#/machines`, the app's Machines). One row per
machine, online ones first, then the account's offline machines (#213):

- **Line 1:** the machine icon, the name, and the trailing state ("Viewing", "Online",
  "Offline"). The icon and the state align with line 1, as a session row's glyph does (§5.1).
- **Line 2:** at most two facts: how many sessions, and the version (with "(dev)" for a build
  from a working copy). A read-only machine says "View only", and a machine whose discovery is
  held says "Discovery held", in the version's place; one that is both says "Discovery held". It
  says so while the hold stands, whether or not its banner was dismissed (#788). The host's reason
  is never on the row: it is the line's tooltip and its accessible description, both reasons
  where there are two. No device id on the row.
- **Inside the row, under its two lines, in its text column:** the machine's updates (what its
  app found, how the last update ended, Check and Install) and, for an offline machine, Forget.
  Every action is the same small secondary button. Check for updates, for every machine at once,
  sits under the rows in the same column.
- **A machine away (§6.1):** listed once, as offline ("Offline", and "Last seen 2m ago" on line
  2), with Forget, both while its rows are kept and after they have gone. A client whose relay
  keeps no roster lists it so only while its rows are kept.

### 6.7 Collaborators (#519)

A collaborator is another GitHub account linked to yours (`agent-principals.md`, #522). Their
sessions are theirs: you can see them, never act on them. Decided 2026-11-03; the boards and the
reasons are in [proposals/519-collaborators.md](proposals/519-collaborators.md).

**Identity.** Every name here comes from verified fields only. The bold @login has two
sources, never a free-text name: on a message, the sender's `driverFrom.login`
(`agent-principals.md` §6.1); on a person header, a link or the Collaboration page, the
collaborator's login that the host resolves from the link's T2-verified GitHub user id at
display time (§7.11.3), or the locally set link name where there is no T2 proof. A collaborator's machine name is theirs, so it is shown as "*WS-LAPTOP* ·
their name" on headers and in italics elsewhere. A sender's session title is secondary, quoted
after "Their session:", one line with an ellipsis, and whole in the tooltip.

**Person, then machine.** The list gains a person level: **Mine** first, then each
collaborator by @login, alphabetical, with their machines nested under them. A collaborator's
header has the person glyph and an outlined-diamond **External** tag. The filter is "Show sessions
from": Everyone (the default), Mine, and one entry per collaborator. It never shows counts.
- **Windows:** chips under the list header (Everyone · Mine · @*login* …); past three
  collaborators the rest fold into "More".
- **PWA desktop:** a "Show sessions from" menu button in the list header, ending with "Manage
  collaborators…".
- **PWA phone:** one chip showing the choice, which opens a bottom sheet with the same list.
- **Search** finds their sessions too, in a separate **Collaborators** group after your own
  results.

**What each grant shows** (`agent-principals.md` §7.4, §7.12). The other side's grant decides
what you see of their sessions, and the row never shows more:
- **Status only:** typed status, no free text. Line 1 names the agent in secondary text,
  "copilot session", where a title would be; line 2 reads "Working · status only". Opening it
  shows the header and, in place of the response, "@*login* shares status only: no titles or
  text." A waiting session keeps the outlined card.
- **Status with titles:** when @*login* also grants the separate person-view display grant
  (titles in your app only, never to your agents), line 1 is the title; the rest is as status
  only, and the opened view says "@*login* shares titles and status, not text."
- **Content:** the title and the **latest response**, as below. The latest response is shown
  only under a content grant.

**Read-only.** A collaborator's row follows §2.2 rule 9. Opening it shows only what the link's
read projection carries: under a content grant, the title, the status, the **latest response** ("Latest response · 2
min ago") and whether it waits. Never their prompts, questions, reasoning, tool history or
delegates: the projection carries no `subagents` (`agent-principals.md` §7.4), so their rows
have no delegate nest or count. The header reads "@*login* · *machine* (their name) · *status*" with the
External tag. The reply box, the action bar, Stop, Transfer, Launch here and every swipe and
menu verb are **absent**, not disabled. In the reply box's place, one line: "Read-only ·
@*login*'s session. Only @*login* can reply, approve or stop it." While it waits, an outlined
External card says "@*login*'s agent is waiting for @*login*." and "Only @*login* can see what it
asked and answer it.", with no question and no buttons.

**External-agent provenance** (on your own sessions' messages):

| Sender | Mark | Label |
|---|---|---|
| You | none | none |
| Your own agent | filled Agent diamond, Agent left edge | "Sent by copilot on *DESK*" |
| An external agent | **outlined** External diamond, dashed External outline, no filled edge | "**external** · Sent by an agent on *WS-LAPTOP* · **@welshofer**", then "Their session: “…”" |
| Any agent that carries external content | its own mark, plus a second line with a small outlined External diamond | "Carries **external** content from **@welshofer**" |
| An external agent whose host lists further origins | its mark, plus a neutral line, as that host's claim | "@welshofer reports external content from @octo-dev" |
| Any agent that carries other retained content | its own mark, plus a second neutral line, no External colour or diamond | "Carries automation content from *Home Assistant*", "Carries forwarded content", "Carries content of unknown origin" |

**Retained origins** (`agent-principals.md` §5.8): a message whose `driverFrom.origins` is not
empty keeps them visible beside the immediate sender, whoever that is, so outside content never
arrives looking like your own agent's alone. The wording follows each origin's category, one
line per category:
- `external:<account>`: "Carries **external** content from **@login**", logins joined with
  commas, with "another account" only for an external origin whose login isn't verified. Only
  this line takes the External colour and the outlined diamond.
- `automation:<connector>`: "Carries automation content from *Home Assistant*" (the
  connector's name), neutral. Your own automations are not another account.
- `forwarded`: "Carries forwarded content", neutral.
- `unknown:<machine>`, or `origins` reported as `"unknown"`: "Carries content of unknown
  origin", neutral, never guessed into another category.

**Established or reported.** Only origins your own hosts recorded are stated as fact; that
includes the `external:<account>` your host adds itself for a message from another account.
Origins another account's host lists are its claim, never verified by a resolved @login
(`agent-principals.md` §4.3, the `provenance` row), so they read as that sender's report, in
neutral text without the External diamond: "@welshofer reports external content from
@octo-dev", "@welshofer reports automation content", "@welshofer reports content of unknown
origin". Listed origins that name your own account are dropped. The held-message card shows
the same lines under its sender.

**Involving external agents.** A filter chip in the list header (in "More filters" on a phone),
with the outlined diamond, and a window of **1 h · 24 h · 7 days**, 24 h by default. It shows
your own sessions whose `Session.external.lastAt` is inside the window, and their line 2 reads
"External agents: sent, read · 2 min" from `Session.external.kinds` (logins join it once #522
carries them). Empty: "No external agents in the last hour" (or day, or 7 days), "None sent to,
read or were refused by your sessions.", and [Show 24 hours] (the next window; absent at 7 days)
and [Clear filter].

**A message from an external agent, held for you.** With a link's "Ask me before each message"
opt-in, a message from its agents to an allow-all session, or to one whose mode is unknown, is
held until the host's absolute expiry for it (`agent-principals.md` §5.7: 60 minutes for a quiet
hold, 5 to 15 for a pushed one); without it, such a message is refused. It is the one
collaborator item that is yours: a pending question on its target session, counted in N. It is
**never pushed on its own** (§7). Its card is unlike an approval's:
- the title "Message from an external agent", the outlined diamond, and "Held · 52 min left",
  counted down to the host's expiry, never a client's own timer;
- a From / To block: *From* "external · an agent on *machine* (their name) · **@login**", their
  session title quoted below, then its retained origins as the sender's report ("@*login* reports external content from
  @*other*", never stated as fact); *To* your session, its agent, machine and mode;
- the **whole** message **exactly as the host will type it** (`agent-principals.md` §5.7): one
  line, the host-built label first ("[Agent Bridge · from an agent on *link name* · not typed by
  your user · external: @*login*]", §6.4), then the words with the delivery escapes already
  applied (a copied marker typed as "(Agent Bridge", a token-initial @ typed as ＠). The label
  holds only fields this host established: the name you gave the link and the verified login,
  never the sender's own machine name or an origin it merely reports. It is in the
  code face, escaped and never read as Markdown, exactly as an approval's (§6.4), in a scrolling
  box with a visible scrollbar, under "Exactly as it will be typed, label included: N characters.
  Not formatted.", N counting that whole text;
- one consequence line: "Approving makes this **your** instruction to an allow-all session. It
  can run commands on *machine* without asking.", or for an unknown mode "Approving makes this
  **your** instruction. Slipgates can't tell whether this session asks before it runs commands on
  *machine*.";
- **Deny**, **Approve once**, **Allow @*login* here for… ▾** (5, 15, 30 or 60 minutes, nothing
  chosen), in that order, all secondary: no Bridge primary makes any of them the easy one;
- once the host reports the hold expired, "Expired · not delivered", and it leaves Needs you;
- while a window is open, a quiet strip on the session: "Allowing @*login*'s agent here", "11 min
  left · 3 messages delivered" and [Stop allowing @*login*].

**Revocation and suspension.** When a link ends or is suspended, the collaborator's group is
replaced **at once** (no fade, in every motion setting) by one quiet line, "@*login*'s sessions
are hidden", with the reason and Details (the link's history). An open session of theirs
becomes "This session is no longer shared", the reason, "What you saw of it was removed from this
device." and [Back to sessions]. It is never a notification and never in Needs you.

| Cause | Reason |
|---|---|
| They revoked the link | Revoked by @*login* |
| You revoked it | Revoked by you |
| GitHub write access lost | Link suspended: write access removed |
| A pinned workspace's remote changed | Link suspended: a pinned workspace's remote changed |
| The link expired (its descriptor's own expiry, 30 days unless renewed) | Link expired: @*login* hasn't renewed it |
| Their read grant removed | @*login* stopped sharing read |
| Collaboration paused on this machine | Collaboration paused on *machine* |

**Settings › Collaboration**, after Devices:
- **Discovery** is opt-in ("Find collaborators who use Slipgates on *repo*"). A suggestion is a
  grey dot on the Collaboration item and a card on the page, with [Review and connect] [Not now]
  [Never for this repo]; never a push, a toast or a count.
- **Links:** each collaborator and repository, what each side shares, and the status in words
  and shape: Active (filled Bridge dot), Suspended (filled Needs you dot, the reason and its one
  fix, [Re-confirm workspaces]), Revoked (hollow Neutral dot, the date and who ended it). Opened,
  a link shows its history (accepted, verified, renewed, suspended, revoked, each with its cause
  and device), [Message audit] and [Revoke…].
- **The link dialog:** "What @*login* can do", nothing ticked: **Read status** (working, waiting
  or idle; no titles, no text) with, under it, **Show titles in @*login*'s app** (the separate
  person-view display grant of §7.12: only @*login* sees them, in their own app; their agents
  still get status only), **Read content** (titles and the latest response), Reply and
  Launch, the separate grants of `agent-principals.md` §7.4. Read content carries its warning:
  "@*login* and their AI provider may process and retain what their agents read, under their own
  policies. Read-only access and later revocation don't retract copies." The two allow-all
  opt-ins follow, each with its warning, and the read one repeats that retention. Then
  "Workspaces (pinned now)"; "Who can end this link", which asks for a revoker off this machine,
  a separate device or an offline recovery key ("Add a device or a recovery key that can end
  this link", [Add *PHONE*] [Recovery key…]); "Identity", which says "Verified by @*login*'s
  GitHub signing key" when that proof checks; and the link code, read aloud: "Link code
  (optional, stronger)" with that proof, and "Link code (required)" without it (§7.11.3).
  "Accept with Windows Hello" (or the platform's presence check) stays off until a scope, a
  workspace and an off-machine revoker exist and the identity is verified, by the key or by
  matching codes.

**Home Assistant card.** Gated on the `collaborators` feature (CARD_VERSION, or
`hello.features`). A card without it is never sent collaborators' sessions, and a held message
reaches it only as "A message from an external agent is waiting. Open Slipgates to review it.",
never its text. A card with it shows "Driven by an external agent · @*login*" on your own
sessions and one line, "Collaborators' sessions: in the Slipgates app". No card approves a held
message.

### 6.12 Restored sessions (#780)

**Not built yet:** planned for #780 Host B and its client slices; the field names here are the
plan's, not a shipped contract.

After a reboot or a host crash, the host starts again the sessions that were running and says so.
Decided 2026-11 on #780; the protocol is #780's plan, and nothing here adds to it. Every element
below reads one field, named in brackets. A client shows only what the host sends: it never
works out on its own that a session died or came back.

A restored session always opens idle. Nothing is typed into it, and nothing is sent for you.

**The row mark** (`restored.at`, `restored.cause`)

- **Where:** text on the row's line 2 (§6.1), as one of its two facts. The other fact is the
  usual one, so it follows the facts-on-a-line rules (#214).
- **Text:** "Restored after a reboot · 03:12" when `cause` is `reboot`, and "Restored after the
  host restarted · 03:12" when it is `crash`. The time is `restored.at` in the client's time
  format, as every time on the row is.
- **Form:** the row's existing secondary text tokens. No new glyph, no colour, no badge. The
  status glyph still says what the session is doing (§2.2), which is idle.
- **Lifetime:** the mark and Continue share one lifetime. Both go when a reply is delivered or
  the session starts working, whichever comes first, and after 24 h or at the end of this host
  run at most (the host stops sending `restored` then). Neither ever comes back: a client never
  keeps the mark longer, and never brings it back once it has gone.

**The machine notice** (`hello.machine.restore`, announced through `hello.changed`, #646)

- **Where and how:** one banner per machine, in the same form as the Discovery held banner
  (#293, #786): an informational InfoBar over every page, in the window's banner area (§7),
  below that machine's Discovery held banner (Platforms, below, gives each client's place). It is
  closable.
- **Content:** a title and the lines below, with no line limit: every failure and its reason is
  shown. A line whose list is empty is left out. The banner grows to fit, but the banner area as
  a whole, every banner included, takes at most a third of the window's height (a quarter on a
  phone); past that, the area scrolls inside itself. Every notice's full text and buttons stay
  reachable by keyboard and screen reader, and the page under the banners is never pushed out of
  view.
  - Title: "*machine* restarted after a reboot at 03:12" (`cause: reboot`) or "*machine*'s host
    restarted at 03:12" (`cause: crash`), from `at`. With `signedInAt`, the reboot title ends
    "… · restored when you signed in at 08:40".
  - "Restored *n* sessions" (`restored`).
  - When `midTurn` is non-zero: "Can continue: *title*, *title*". The titles are those of this
    machine's sessions whose `restored.midTurn` is true, from the client's own session list, so
    the host sends no extra field. Each title opens its session, where Continue waits. A session
    continued since, here or on another device, drops out, and the line is left out once none
    remain. If the list doesn't have them yet, the line reads "*n* were mid-turn", with no promise
    to continue, until it does.
  - "Questions lost: *title*, *title*" (`questionsLost[].title`). The question itself died with
    the session; the notice says so, and nothing reasks it.
  - "Couldn't be restored: *title* (*reason*)", one per entry in `failed`, with the host's
    `reason` as sent. The plan's "waiting on folder trust at the machine" is one such reason.
  - "*n* sessions weren't restored again because they died within 10 minutes" (`loops`).
- **Offered only:** when the client is read-only beside agent-ha-bridge (G5), or when the
  machine's `sessions.restoreOnStart` is off (`offered`), the notice still says what died, and
  the "Restored …" line reads instead "*n* sessions can be restored at the machine". It offers no
  button: restoring happens at the machine. Under G5 that stays so; with the setting merely off,
  a Restore button with a confirmation, from any client, is #948.
- **Dismissal:** per device, as #786's: closing it hides it on this device only, it is not saved
  across app restarts, and it comes back only if the host sends a new notice. Its close button is
  named "Dismiss restore notice on *machine*".
- **Lifetime when not dismissed:** while the host sends `hello.machine.restore` for the machine.
  It goes when the notice is gone or replaced.
- **No notice** when nothing died: the host sends none, and the client shows none.

**Continue** (`restored.midTurn`, `restored.continue`, the `session.reply` operation)

- **Where:** a small secondary button, "Continue", in the session pane directly above the reply
  box (§6.5), left-aligned with it. Never in the row, the card, the notice or a notification.
- **When:** only while the session's `restored.midTurn` is true, with the mark's lifetime: the
  host clears it once a reply is delivered or the session starts working, or after 24 h or at the
  end of this host run at most, and the button goes with it.
- **What it does:** sends `restored.continue`, verbatim, through the reply box's ordinary Send
  path: the `session.reply` operation (capability `sessions.reply`) with
  `{ id, request, text: restored.continue, progress: true }`. The
  client does not edit, translate or rebuild the text. Whatever is typed in the reply box stays
  there, unsent.
- **Progress:** the §6.5 receipt (ring, check, Try again; #600, #617). The `request` follows the
  ordinary Send rule ([v1-sessions.md, `session.reply`](../protocol/v1-sessions.md#sessionreply),
  whose `request` has `session.answer`'s semantics): resending after a lost response, and Try
  again after a failure with no outcome, reuse the same `request`, so the host types it at most
  once and returns the first outcome. Only Try again after a `busy` or `refused` outcome sends a
  **new** `request`, because the host remembers each outcome for ten minutes and would only
  repeat it. `unconfirmed` is never retried: the receipt says to check at the machine, as §6.5's
  does. While it is in
  flight the button is disabled and keeps focus (A4).
- **In the conversation:** shown as the person's own message, exactly as a typed reply is. It is
  not agent-driven, so #489's marking does not apply.
- **After:** the button goes once the reply is delivered, with no "Sent" state. Focus moves only
  if it is still on Continue then, and goes to the session's reply box; focus anywhere else stays
  where it is (A4, §6.4's receipt rule).
- **Never automatic:** no client presses it, on start, on reconnect or on a timer.

**Push and toast** (Web Push kind `restored`; `hello.machine.restore`)

- One per machine per start, and only if something died. The host decides; a client never adds
  one per session.
- **Text:** title "*machine* restarted", body "Restored *n* sessions · *m* need you" when there
  are lost questions or failures, else "Restored *n* sessions". When offered only: "*n* sessions
  can be restored at the machine".
- **Sound:** none. Decision #764 keeps sound for pending questions and approvals only, so this
  notice is silent on every platform. The push payload sets `notification.silent: true`, so an
  older service worker that shows kind `restored` as a `question` stays silent too.
- **Opening it** opens the machine's session list, with the notice over it.
- An older service worker shows kind `restored` as a `question`; that is accepted.

**The Settings toggle** (`sessions.restoreOnStart`, per machine, through `SettingsRequest`)

- In the Settings host section, per machine: a switch, "Restore sessions after a reboot or
  crash", on by default.
- Help text, one line: "Starts the sessions that were running when this machine stopped. Off:
  you're told what stopped, and nothing starts."
- A read-only client shows the switch disabled, with the value the host sends.

**Older clients** (no `sessions.restore` in `hello`)

They ignore `restored` and `hello.machine.restore`: no mark, no notice, no Continue. The
restored session is an ordinary idle row, and its conversation is intact. The push still
arrives, as a `question`.

**States**

| | Reboot (`cause: reboot`) | Host crash (`cause: crash`) |
|---|---|---|
| Restored, idle | Mark "Restored after a reboot · 03:12"; counted in the notice | Mark "Restored after the host restarted · 03:12"; counted |
| Restored mid-turn (`midTurn`) | Mark; Continue above the reply box; "mid-turn" line | Same |
| Offered only (G5 or setting off; `offered`) | No mark (nothing started); notice "*n* sessions can be restored at the machine" | Same |
| Failed (`failed`) | No mark; notice "Couldn't be restored: *title* (*reason*)" | Same |
| Loop (`loops`) | No mark; notice "*n* sessions weren't restored again …" | Same |
| Question lost (`questionLost`, `questionsLost`) | Mark; notice "Questions lost: *title*"; no question shown | Same |

**Accessibility** (§9.1)

- Continue's accessible name includes the session: "Continue *title*" (A1). One verb (A7).
- The notice is a polite live region, announced once when it arrives, never again on a
  reconnect or a re-render (A6).
- Keyboard order: the notice, then its close button, then the next banner's close button or the
  page (A3, A4).
- Reduced motion: the notice and Continue appear and go without animation (A10).
- The mark is text, so it is read with the row; it needs no description of its own.

**Copy**

| Use | Text |
|---|---|
| Row mark | "Restored after a reboot · *time*" / "Restored after the host restarted · *time*" |
| Notice title | "*machine* restarted after a reboot at *time*" / "*machine*'s host restarted at *time*" |
| Signed in | "restored when you signed in at *time*" |
| Restored | "Restored *n* sessions"; "Can continue: *title*, *title*"; "*n* were mid-turn" |
| Offered | "*n* sessions can be restored at the machine" |
| Lost | "Questions lost: *title*, …" |
| Failed | "Couldn't be restored: *title* (*reason*)" |
| Loop | "*n* sessions weren't restored again because they died within 10 minutes" |
| Close | "Dismiss restore notice on *machine*" |
| Button | "Continue" (name "Continue *title*") |
| Push | "*machine* restarted" / "Restored *n* sessions · *m* need you" |
| Toggle | "Restore sessions after a reboot or crash" |

**Platforms**

- **Windows app:** InfoBar (Informational) in the window's banner area over every page, below
  that machine's Discovery held InfoBar (§7, Windows: Banners); Continue a standard secondary
  Button; the toast through the app's ordinary notification path (§7).
- **PWA:** the same notice as one of §7's app banners, at the top of the content above the page,
  over every page, after the app banners §7 lists. The PWA has no Discovery held banner (it
  says "Discovery held" on the machine's row, §6.6), so there is none to sit below. The restore
  notice alone is an exception to §7's "no icon and no dismiss" app banners; every other PWA
  banner keeps §7's rule. The push through `sw.js`, kind `restored`.
- **Apple (#260, owner welshofer):** note only. The same fields; the native form is that track's
  decision.
- **Home Assistant:** read only. `sensor.agent_bridge_<machine>_restore` (state: the last restore
  time; attributes: the notice), a `restored` attribute on each session's status sensor, and one
  persistent notification per restore. No Continue in Home Assistant.

## 7. Platform shells

**Notifications, on every client (#253):** a question still pending after 2 s is notified
once, and an answer anywhere clears its notification (`docs/protocol/v1-sessions.md`,
Notifications): the Windows app withdraws its toast, while Web Push replaces it in place with a
silent "no longer waiting" notice, or the finished turn's, since every push must show a
notification or iOS revokes the permission (`pwa/sw.js`). Opening one opens the session at its
question, as choosing its row does.
- **An approval's notification only opens its question** (#253 decision 7, #372). It never
  offers Approve once or Deny: the OS draws its body as plain text and cuts it short, so it
  can show neither the whole command nor the escapes and notes that hidden, bidi and
  right-to-left characters get on its card (§6.4, #165, #220). Windows toasts, Web Push and the
  Apple notifications (#260) follow this, and a button on a toast an older build left in
  Notification Center only opens.
- **Other questions never answer on a tap** (#773): a notification offers no option buttons.
  A question that is one text field keeps a text box and Send answer where the platform has
  them, since typing then sending is the card's own pattern; it still checks, before sending,
  that the question is the one shown. Every other question's notification only opens its
  card.
- The count each platform shows is Needs you's N (§6.1).
- **Collaborators (#519):** their sessions are never notified, badged or counted. A message
  from an external agent held for you is counted in N but **never pushed on its own**, unlike
  an approval (`agent-principals.md` §5.7, normative). A person can opt in to one aggregated
  push an hour at most, with a count and no text ("3 agent messages are waiting"), which opens
  Needs you.

**Windows (WinUI 3):**
- **Window:** Mica, with a custom 48 px title bar carrying the mark, the app name (12/16)
  and a centred search box. System caption buttons.
- **Search:** finds sessions on every machine by the words of their title, repository, branch,
  agent or machine, titles starting with them first, then the most recently updated, whatever
  machine the list shows, and opens the one chosen as a notification does.
- **Navigation:** a compact `NavigationView` rail (Sessions, Machines, Launch, Usage;
  Settings at the foot) with a selection pill.
- **Content:** a layer with an 8 px top-left radius.
- **Controls:** ListView rows with a 3×16 Bridge pill on selection, SelectorBar for
  machines (those that don't fit under More, §6.1), an Accent button for New, subtle icon buttons
  for header actions, and a `ToggleButton` for Needs you · N.
- **Tray and toasts:** the tray's tooltip and the rail's Sessions badge carry N ("Slipgates
  · 3 need you"). A question's toast follows the rules above: an approval's toast has
  Open only. Every toast is silent (#754): none plays Windows' default sound, and any sound
  comes back only as an owner-approved spec here first.
- **Banners** sit over every page, `InfoBar`s in this order (#293):
  - What stops the app from its job: the host not running, refusing the app, connecting, or
    view only. Warning, with no close button: it goes once that is put right.
  - "Discovery held: …" ([v1-sessions.md](../protocol/v1-sessions.md#discovery-held)), one per
    held machine, its name first when there is more than one. Informational, with the
    `InfoBar`'s close button, named "Dismiss Discovery held on *machine*" (§9.1 A1) and reached
    by Tab. It hides that banner only, and only how it is shown: the host still holds, and is
    told nothing. It stays hidden while that machine's hold has the same reason, and comes back
    when the reason changes, when the hold clears and comes back, or when the app restarts;
    dismissals are never saved. Focus goes on to what Tab reaches next: the next banner's close
    button, or the page (§9.1 A4). Nothing is announced.

**macOS (SwiftUI, #260):**
- **Sidebar:** a translucent source list (a Needs You · N filter item, Pinned, Sessions,
  Machines), 28 px rows, and a Bridge fill on the selected row. The toolbar's filter menu has
  the same toggle.
- **Menu bar:** an extra whose label is N. Its menu names the waiting sessions (machine and
  title, oldest first, at most 8) and Open Agent Bridge. An item opens its session at the
  question. The menu never shows a question's words or answers one.
- **Dock:** the badge is N.
- **Toolbar:** unified, with title and subtitle, search, and icon buttons.
- **Inspector:** a property grid with right-aligned labels.
- **Keyboard:** ⌘↩ sends.

**iOS (SwiftUI, #260):**
- **Sessions:** large title with the filter and New buttons on the title row (no empty
  nav-bar row above it), a search field, and inset grouped sections. The filter menu's first
  item is Needs you · N, and the filter button shows N while anything waits.
- **Session:** a back button, a 28/34 title, the conversation, and delegates in a sheet.
- **Navigation:** a tab bar (Sessions, Machines, Usage, Settings).
- **Gestures:** swipe actions on rows (Pin, Ask main agent to stop).
- **Badge:** the app icon's badge is N.

**Android (Compose, later):**
- **Sessions:** a Material 3 search bar, filter chips (Needs you · N first), list items, an
  extended "New session" FAB, and a navigation bar.
- **Session:** a top app bar, assist chips for delegates, and the composer on
  `surfaceContainer`.

**PWA:**
- **Look:** follows the Windows design at every width (#166), with no imitation of other
  platforms' controls.
- **Desktop:** the Windows layout with a 56 px web app bar in place of the title bar (mark,
  name, search, machines online, account).
- **Phone:**
  - a 24/32 title row with Search and New, and the sort menu between them;
  - machine tabs;
  - a bottom navigation bar (Sessions, Machines, Usage, Settings) with a Bridge indicator. New
    is the way to Launch.
  - The app bar comes back only as a strip while the connection has news (connecting,
    reconnecting, paused, an error), and as the search row while searching.
- **App banners** sit at the top of the content, above the page, one card each (`card` fill,
  `stroke2`), with no icon and no dismiss, in this order:
  - "A new version of the app is ready." · Reload (primary). A release is offered only once it
    has fully installed (#647).
  - After three failed installs in a row under the release in control: "A new version of the app
    keeps failing to install. You're still on the version you had." · Try again (secondary; it
    reloads). It goes once a release installs or takes over, giving its place to the new-version
    banner (#652).
  - The demo banner: "Demo: sample sessions, no machine connected." · Leave demo (secondary).
  - "This app is open in another window or tab." · Use here.

  The first two are statuses, their words read out through a live region that is always on the
  page (one unhidden with its words already in it isn't reliably read); the last is an alert (§9.1 A6).
  Each comes and goes as §5.3's Toast and banner: in from above, and out the same way, quicker
  (under reduced motion, a cross-fade). The demo banner is there from the start and never moves.
- **Pages are routes** in the address's hash (`#/sessions`, `#/machines`, `#/launch`, `#/usage`,
  `#/settings`): a reload stays on the page, and the browser's own back goes between them.
  A session chosen is `#/sessions/<machine>/<session>`: a new entry on a phone, so the
  browser's back returns to the list; on a desktop it replaces the last, so back still goes
  between pages. A window narrowed to a phone's width with a session chosen gets the list's
  entry under it.
- **Search** narrows the sessions list to the sessions whose title, repository, branch, folder,
  agent or machine has it. Rows don't move while it narrows the list.
- **A folder-trust question** waiting on a machine that takes no launches puts a Needs you dot on
  the way to Launch (the rail's Launch, the phone's New).
- **Under the sessions,** one line of each agent's fullest usage leads to Usage.
- **Usage names the account** (#225), on every group and every figure ("GitHub Copilot ·
  danswett 12%"), in full: two of one agent's accounts differ by nothing else. Short of room the
  line wraps; the account never gives way to the agent's name or the age. Which machine took a
  reading is for diagnosis, in a tooltip or said to a screen reader, never on the line.
- **Platform layer (#168):** detection only for shortcut hints, safe areas and back
  navigation (`pwa/js/platform.js`). Never a reskin: no control, colour, icon or layout differs.
  - **Detection,** once at start-up, on `<html data-platform data-standalone>`: Chromium's client
    hints, otherwise the user agent; a Mac with a touch screen is iPadOS. Values: `windows`,
    `macos`, `ios`, `ipados`, `android`, `chromeos`, `linux`, `unknown`. `unknown` is Windows.
  - **Nothing depends on it being right.** Ctrl and ⌘ both work everywhere; only the hint
    follows the platform (⌘↩ and ⌘K on Apple's, Ctrl+Enter and Ctrl+K elsewhere).
  - **Safe areas:** only an app installed on iOS or iPadOS draws under the notch and the home
    indicator (`viewport-fit=cover`), and there the app bar, the bottom navigation (or the rail)
    and the reply box keep clear of them. Everywhere else the browser keeps the page out of them,
    so a platform taken for another still has its safe areas kept clear. Installed on iOS or
    iPadOS, a tap doesn't wait for a double tap.
  - **Back:** on a phone, a delegate opened in a pushed session is a step of its own. The
    system's Back (Android's button and gesture, iOS's edge swipe in an installed app, the
    browser's) goes delegate → session → list, then leaves the app; the header's Back is
    always the list. The step follows what shows: one while a delegate is open (another opened
    takes its place, and its step), gone once it is closed or hidden (by hand, behind another
    tab, or because its machine no longer lists it, even on Forward to it), and there again when
    the window narrows to a phone's with one open. Forward to it opens the delegate again,
    finished meanwhile or not.

**App icon (#167):** the Slipgates mark ([`mark.svg`](../../design/icons/mark.svg),
[brand.md §4](brand.md#4-the-mark)) in white on an ink plate
(`#0E1114`), in each platform's container. It is one icon in every theme, since no platform
swaps an app icon with the theme. The sizes are in
`tokens.json` `appIcon`, on each container's own grid, and `tools/design-gen` draws every
file from them ([design/README.md](../../design/README.md#app-icon)).
- **Windows:** a tile: a rounded square with radius 14 on a 72 grid, a 1-unit edge
  (`#2A2F36`) inside it so the tile still shows on a dark taskbar, and the mark 44 across. At
  24 px and under, the tile has no edge and draws the mark's native cut for its size
  ([`mark-16`, `mark-20` and `mark-24`](brand.md#42-clear-space-and-sizes)), so every bar
  lands on whole pixels. The app's `.ico` holds every size Windows asks for, from 16 to 256,
  and serves the exe, the window, the tray and the installer.
- **macOS:** a rounded square on Apple's 1024 grid, 824 across with radius 185 and the mark
  504 across. Its drop shadow comes with the asset catalog (P5). The menu bar draws the
  native 16 px cut as a template image. Both are generated into `design/apple/Assets.xcassets`.
- **iOS:** full bleed and opaque, with the mark 44 across on a 72 grid. The system masks it
  to its squircle. Its dark and tinted appearances are the mark alone on transparency.
- **Android:** an adaptive icon on the 108 dp grid: the plate is the background layer, the
  mark 40 across is the foreground, and the mark in white is the monochrome layer for themed
  icons. The notification icon is the native 24 px cut in white. Both are generated into
  `design/android`.
- **PWA:** the Windows tile as the favicon and the manifest's `any` icons (192 and 512), the
  iOS icon at 180 as `apple-touch-icon`, and maskable icons (192 and 512) that are full bleed
  with the mark 35 across on a 72 grid. That keeps the mark inside the safe zone at Android's
  proportion. The same mark without its plate, in white, is the `monochrome` icon Android's
  themed icons draw; and the native 24 px cut in white, at 96 px, is a notification's badge,
  as the Android client's notification icon.

## 8. Copy

- **Status words** come from §2.2 only. Don't invent synonyms.
- **Names:**
  - "Main agent" (never "parent").
  - "Delegate" in the UI ("sub-agent" in code and specs).
  - A delegate's name is always its safe label (§6.3), never the text the host sent, and
    never reads as "Main agent" nor adds a fact to a line (no "·" of its own, and read aloud,
    no comma, §9). Wherever a line says who asks or works ("asks", "task:"), the label
    follows the word "Delegate", which the main agent's own lines never have.
  - "Needs you".
  - "End session" (not "Stop" for the whole session).
  - A prompt you didn't type (§6.4): "Scheduled prompt · #4", "Sent by an agent", "Automatic
    prompt", "same as last time", "Show prompt" and "Hide prompt". Never "your message".
- **Attention words (#253):** "Needs you · N", "Showing N of M", "N on *machine*", "N more on
  other machines", "Nothing else needs you"; on a question, "Main agent asked", "You
  answered", "No longer waiting" and "Answer at the machine"; in Tools and the delegate
  inspector, "Waiting on you" and "Asks you". "Answered" only for an answer this device sent
  and the host confirmed.
- **Answer words (#773):** "Send answer" (sentence case on every client, Apple included),
  "Recommended", "Required" and "Other…"; a field's inline error says what to do: "Choose an
  option", "Choose at least one", "Choose Yes or No", "Enter a number", "Enter an answer". No
  type hints ("Choose one", "Yes or no"): the control says it.
- **A question's words** appear in the conversation only, never on a row, in Needs you, in
  Tools, in the inspector or in a menu.
- **Numbers before nouns:** "3 delegates", "1 background command".
- **Approval notifications** say "waiting for your approval" and offer only Open (§7): an
  approval is approved in the app, never from a notification.
- **No sentences explaining the UI,** no exclamation marks, and no emoji. The one exception is
  a collaborator's read-only lines (§6.7), which say whose session it is and who can act on it,
  because a control that is absent can't carry a tooltip.
- **Collaborator words (#519):** "Mine", "Everyone", "Show sessions from", "Manage
  collaborators…", "External", "*machine* · their name", "· read-only", "Waiting for @*login*",
  "copilot session" and "· status only", "@*login* shares status only: no titles or text.",
  "@*login* shares titles and status, not text.", "Show titles in @*login*'s app", "@*login*
  reports external content from @*other*", "Driven by an external agent · @*login*", "External agent · @*login*" (narrow), "Involving
  external agents", "Message from an external agent", "Approve once", "Allow @*login* here
  for…", "Expired · not delivered", "Stop allowing @*login*", and the reason lines (§6.7). The
  product name in them is Slipgates. Never "gate" in a status or error line.
- **What Copy copies is what is on screen** (§6.4, #220): a code block's escapes, right-to-left
  letters' included, are copied as drawn, so the text pasted is never different from the text read.
  Reading Hebrew or Arabic in code and in approvals is the harder for it, by the owner's choice (#253).
- **An approval reads as it runs** (§6.4, #220): an approval's message is drawn exactly as
  written, fence markers included, in the code face; nothing in it is read as Markdown.
- **Code notes** (§6.4, #220): "Contains hidden characters" and "Contains right-to-left text",
  exactly so, beside the block they describe. An approval's card has no Copy. Hidden includes
  what draws blank but runs as part of a word (U+00A0 and the other spaces but U+0020, U+2800;
  #546), which the Needs you lane refuses as it refuses a bidi control.

## 9. Accessibility

### 9.1 The accessibility contract

These rules hold on every client: the Windows app, the PWA and the Apple and Android apps to
come. Other sections give a surface's words, names and targets; the rules come from here, and
where a section words a rule more loosely, this section wins. A client change that breaks a
rule is a bug, whoever reviews it. **Enforced by** names the suite in
[quality/catalog.yml](../../quality/catalog.yml) and the test or rule in it that fails when the
rule is broken; **manual** means no automated check exists yet, so review checks it by hand. A
known violation is allowlisted in its check with its issue, covers only the elements it names,
and is never a precedent. **Not yet enforced** names the issue that tracks a rule no check covers yet. The parts of A1, A3, A4, A7, A8, A12, A13 and A14 that settle #365, #366, #369, #371, #373, #678 and #699 were approved by the owner on 2026-11-04 (#707).

| # | Rule | Enforced by | Spec for |
|---|------|-------------|----------|
| A1 | **The name says what is shown** (WCAG 2.5.3). A control's or a row's accessible name starts with the words it shows, in the order they are shown; an icon-only control is named for its action and what it acts on. Every other fact the control draws is in its name or its accessible description, so nothing seen goes unheard. A control repeated on each row names its row: "Set up Copilot CLI", "Remove *label* from draft". How a row's name is built is in §9.2; a session row's name goes on from there with its second line's facts and its age, in the order drawn and joined with commas, never moved to an accessible description. What else §9.2's name says and the row doesn't draw (its machine while the list shows one machine, a status's later facts, the status under "Driven by an agent", and "driven by an agent" under a rule 10–12 status drawn in its place) follows the age, so the name still starts with what is drawn. | PWA: axe `label-content-name-mismatch` in `a11y-pwa`, which every session row's name passes; `pwa-ui`'s "a session row is named for the words it draws" and "every glyph is named with its state". Windows: manual | #365, #678, #699 |
| A2 | **Repeated controls have their own keys.** On Windows, every control a person can act on and every status that changes, when repeated on each row, has an AutomationId unique in its window, built from the row's stable key, never from its position. Only a part nobody acts on (a template's text) may share an id. In the PWA an `id` is never repeated, and repeated controls are told apart by their names (A1). | Manual | #678, #699 |
| A3 | **Focus order is the visual order** (WCAG 2.4.3). Tab and Shift+Tab walk the regions and their controls in the order they are laid out at the current width; arrows move within a list or a group of tabs (§9.2). On a desktop the list head's Sort sits on its first line, before New, so the page's one order (Sort, then New) is the drawn order at both widths. The list pane is at most 320 px, too narrow for the title, Sort and New on one line, so until it can widen the desktop head is always two lines: the title and Sort, then the count and New. | PWA: `a11y-pwa`'s keyboard.spec, "Tab walks … in the order they are laid out" on a desktop and on a phone, and "the list head draws Sort on its first line, before New". Windows: manual | #371 |
| A4 | **Focus stays where the person is.** A control whose request is in flight keeps keyboard focus: it stays focusable, says it's busy, and ignores further presses until the outcome (in the PWA `aria-disabled="true"`, never `disabled`, as Forget on Machines does). A re-render keeps focus on the same control, found by its key. When a control goes, focus moves to a target its surface's section names, never to the page or the window; when Launch starts a session, that target is the new session's reply box. A pop returns focus to the row that pushed the screen; a closed menu, sheet or dialog returns it to the control that opened it. | PWA: `a11y-pwa`'s keyboard.spec (the reply, End session and Launch walks; Back on a phone; the overflow menu). Focus kept in flight and focus on the reply box after Launch are not yet enforced, tracked in #369. Windows: manual | #369, #600 |
| A5 | **Escape and back go back one step.** Esc closes the innermost menu, sheet, popup or dialog first (a dialog's Esc is its Cancel). With none open, Esc, the browser's back and the system back gesture pop one pushed screen (§5.3, §7's back stack), and the header's Back goes where §7 says (on a phone, always the list), with focus as A4. | PWA: `a11y-pwa`'s keyboard.spec, "Escape closes it with focus back on More"; Esc on a pushed session is manual (#370). Windows: manual | #370 |
| A6 | **Announcements are polite, and said once.** A status that changes on its own is a polite live region (PWA `role=status`; Windows `AutomationProperties.LiveSetting` Polite with a LiveRegionChanged event), on the page and empty before its first words (§7). Assertive (`role=alert`; LiveSetting Assertive) is only for a problem that stops what the person is doing and needs them to act, for example Launch's problem line, a reply or answer that failed, and §7's "This app is open in another window or tab". Progress, success and revocation are never assertive. A change is said once: a focus move, a name and a live region never say the same thing twice, and a control's state is said by its role (pressed, expanded, busy), not repeated in its name (§6.4). Each surface sets its own rate limit: Needs you at most every 5 s (§9.2); the reply receipt says nothing in its first 400 ms, then at most one stage a second (only the later of two is spoken), and always its outcome (the owner's #600 decision, whose text reaches §6.5 with #617). | Manual (`a11y-screen-reader` is planned) | #600, #678, #699 |
| A7 | **One verb per state.** An action button says, with one verb, what it does in the current state, and its words change when the state does (for example "Set up" when missing and "Repair" when broken, #678). A row in a state with nothing to do shows no button rather than a disabled one, as §6.7's collaborators are absent, not disabled; §6.1's controls disabled while a machine is away stay as they are. A toggle keeps its words; its pressed or checked state says the rest. | Manual; no button instead of a disabled one is not yet enforced, tracked in #678 | #678, #699 |
| A8 | **Contrast:** text 4.5:1, and glyphs and the non-text parts that show state or focus 3:1, against what they sit on, in light, dark and high contrast (§3's Contrast). Words that would be Bridge on light's `selected` tint take `text` instead, and every foreground and background pair a client draws is listed in `tokens.json` `color.contrast`, so `a11y-contrast` checks it. | Tokens: `a11y-contrast` (`tokens.json` `color.contrast`). PWA: axe `color-contrast` in light, dark and forced colours in `a11y-pwa`, and on a selected machine's words in light. Not every drawn pair is listed yet: `danger.text` on light's selected tint over the shell is 4.18:1, drawn by a selected machine's refused update (`.machine-error`), and isn't listed, tracked in #858. Non-text 3:1 is manual. Windows: manual | #366 |
| A9 | **High contrast uses system colours.** In Windows high contrast and the browser's forced colours, every role and surface takes a system colour (§3's Theme, `tokens.json` `hc`), and shape and words still carry each state (§2.1). | PWA: `pwa-ui`'s theme.spec, "in forced colours every role is a system colour", and `a11y-pwa`'s forced-colours axe run (Chromium); that shape and words still tell states apart is manual. Windows: manual | |
| A10 | **Reduced motion** follows each platform's setting, as §5.3's reduced-motion column and §9.2 say. | PWA: `a11y-pwa`'s reduced-motion.spec. Windows: manual | #659 |
| A11 | **Targets:** at least 44×44 on touch platforms and 32×32 with a pointer on desktop; never under WCAG 2.5.8's minimum anywhere (24×24, or its spacing exception). | PWA: axe `target-size` in `a11y-pwa` (WCAG 2.5.8 only); 44 and 32 are manual | |
| A12 | **Keyboard focus is always shown** (WCAG 2.4.7). In the PWA the ring is drawn through `:focus-visible` (§3: Bridge is focus), never `:focus` alone, and outlines are never hidden for the whole page; on Windows it is the system's focus visuals. A ring is drawn only for focus moved by a key: a press, a touch, or the app coming back from a notification draws none. Focus still moves for a screen reader; in the PWA the one element focused without a key draws no ring until a key is pressed, and a text field keeps the browser's ring wherever focus came from, since it shows where typing goes, as does focus the browser brings in from its address bar or another window while the page stayed on screen (its Tab, whose key the page never sees). Nothing else that looks like focus marks the session a notification opened: it is shown selected. | PWA: `a11y-pwa`'s keyboard.spec ("with focus shown"; "a session opened with a touch … no ring drawn"; "the session a notification tapped … opens … no ring"; "as the app starts … no ring"; "a notification for the session already open … takes the title's ring away"; "focus the browser brings in from its address bar … keeps the browser's ring"), and `pwa-e2e`'s "a notification's link opens its machine and session" (cold and warm, through the real link and the worker's message). A real iPhone's notification is the owner's check. Windows: manual | #373 |
| A13 | **Content stays selectable.** Conversation, code, prompts, questions and every field stay selectable and copyable; never a blanket `user-select: none`. A control's own words (a button, a tab, a menu item), a row's title, a session's header, the app bar's title (on a phone, the page's title row, which stands in for it) and a banner's words are not selected by a press, a long press or a double tap; anything a person may want to copy stays selectable. | PWA: `pwa-ui`'s ui.spec, "chrome's words are not selectable, and the conversation, code, a question and every field are". Windows: manual | #373 |
| A14 | **Risky settings carry warning weight.** A setting that weakens a safeguard (bypassing the agent's permission prompts) is drawn with warning weight: Needs you's text colour for its explanation, or an InfoBar while it is on. Its confirmation names exactly what changes and has Cancel as its default button. | Not yet enforced, tracked in #699 | #699 |

### 9.2 Names, keys and announcements by surface

- **Glyphs** carry their state as an accessible name. A row reads as "Plan Migration to
  Windows App, delegating to 3 agents, DEV-VM1": its parts (title, status, driver, machine;
  a strip item's name, state, time and parent) are joined with commas, while the status's own
  facts keep " · ". So a delegate's name read aloud has no comma of its own: "," and the
  commas drawn like it (U+055D,
  U+060C, U+066B, U+07F8, U+1363, U+1802, U+1808, U+201A, U+2E41, U+2E4C, U+3001, U+A4FE,
  U+A60D, U+A6F5, U+FE10, U+FE11, U+FE50, U+FE51, U+FF0C, U+FF64) become a space, and a run of
  spaces one. A name of "x, idle, DEV-VM2" reads "Delegate x idle DEV-VM2 asks", never as
  facts of its own; what is drawn keeps them.
- **Reduced motion** stops all glyph animation, and turns every other motion into an
  instant change or a short cross-fade (§5.3). Nothing relies on motion alone.
- **Keyboard:** desktop lists move with arrows, Alt+↑/↓ reorders (#164), Shift+F10 or the
  menu key opens a row's menu, and Ctrl/⌘+K focuses search. The shortcut is named where
  search is (the Windows search box's tooltip, on hover or when Tab reaches it) and nowhere
  else: no shortcut hint opens because the window was activated, a click landed or Ctrl+K was
  pressed (#256). Needs you · N is a toggle in the tab order after the machine tabs and before
  the list. Space or Enter toggles it, and Enter on a waiting row puts focus on its question's
  heading.
- **Collaborators (#519):**
  - A collaborator's row reads "External session, @*login*, *machine*, their name, *title*,
    working, read-only". A person header is a group named "@*login*, external collaborator, 2
    machines", with its machines as nested groups.
  - The read-only line is the pane's description, read once when it opens. An external
    message's name starts "External agent message from @*login* on *machine*".
  - A held message is announced as Needs you items are, title first ("Message from an external
    agent, from @*login*, held, 52 minutes left", from the host's expiry, as the card shows); its text is a focusable scrolling region named
    "Message text as it will be typed, N characters".
  - Revocation is announced once, politely ("@*login*'s sessions are hidden. Revoked by
    @*login*."), never as an alert.
  - The person chips and sheet, and the window segments ("Window"), are radio groups.
- **Needs you (#253):**
  - The toggle reports its pressed state, and is named for its count and scope: "Needs you, 3
    on every machine", or "Needs you, 1 on ULTRA, 3 on every machine" with a machine chosen.
  - A change in N is announced once, politely ("3 need you"), at most every 5 s, and not while
    the person types in the reply box.
  - The question's heading (level 3) is where focus lands, from a waiting row, a toast or a
    notification alike, never one of its answers: an approval's first is Approve once, which a
    stray Enter or Space would press unread (#484). Its accessible description is
    the question's first line. Approve once and Deny are named for their session and machine
    ("Approve once: *title* on *machine*").
  - In high contrast, the toggle takes the system's button colours (ButtonFace and ButtonText,
    and Highlight and HighlightText when pressed). The `needs` glyph is Highlight with
    HighlightText ink, swapped on the pressed toggle's Highlight fill so its disc still shows.
    The Tools link is a system hyperlink.
- **A question card (#773):** each field is named for its title. A required field says so
  where the role supports it: on a radio group (`radiogroup` with `aria-required`) and on a
  text or number field (`required`; `IsRequiredForForm` on Windows, on the radio list or the
  text box). A required multiple choice is a group whose accessible description includes
  "Required", and no checkbox is itself marked required, since that would require every
  option. A radio group, Yes and No included, is one tab stop with arrows inside it. "Recommended" is part of its option's
  name ("Use the cache, Recommended"). Send answer is named "Send answer"; Approve once and
  Deny as Needs you says. Pressing Send answer with something missing moves focus to the first
  field missing, whose error is its accessible description and is said once, assertively
  (A6). While an answer goes, its controls are read-only, not disabled, so they keep focus
  and say their value (A4).

## 10. Mock-up index

The canvas has these boards, rendered as images in [mockups/](mockups/README.md). On the
canvas, each platform board has a light/dark switch.

| Board | Shows |
|---|---|
| Design language | Principles, glyphs, colour roles, icons, app icon, hierarchy, typography, layout |
| Windows · dark / light | Three panes, delegating session, inspector on; Needs you · N over the list |
| macOS · light / dark | Sidebar with the Needs You · N item, token row, inspector |
| PWA desktop · dark / light | The Windows board with the web app bar |
| iOS Sessions / Session · dark / light | List with the Needs you filter; session with the delegate sheet |
| Android Sessions / Session · light / dark | List with the Needs you chip and FAB; a session waiting on you while delegates run |
| PWA phone / phone session · dark / light | Windows-styled phone list and session |
| Status rules | The table in §2.2, drawn |

The collaborator surfaces (#519) are drawn on the Proposal #519 boards
([proposals/519-collaborators/](proposals/519-collaborators/)).

The states #253 added (the toggle's states, rows, the question card's forms, Tools and the
delegate inspector) are drawn on the Proposal #253 boards, which stay on the canvas as their
reference ([proposals/253-attention/](proposals/253-attention/)).
