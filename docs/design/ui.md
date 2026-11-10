# UI design: the session workbench

The design for every Slipgates client: the Windows app, the PWA, and the later macOS,
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
and the boards are in [proposals/519-collaborators.md](proposals/519-collaborators.md).
First-run setup and Settings (#384: readiness, Settings' sections and the guided setup) were
decided by the owner on 2026-11-03 and added here (§6.8–§6.10); the reasoning and the boards
are in [proposals/384-first-run.md](proposals/384-first-run.md). Most of it isn't built yet:
each part says what ships today and marks the rest **Not built yet (#384 slice *N*)**, for
the slices in the proposal's §11, or **Parked** with the issue that parked it. The
accessibility contract (§9.1) gathers the accessibility rules already decided here and the WCAG
requirements they rest on, each with the check that enforces it, and the rules the owner
approved for it on 2026-11-04 (#707). Delegate events in the conversation (§6.4, #661) and the
history beside them (#292) were decided by the owner on 2026-11-04 and are proposed: not yet
built (#661 and #292 build them).* The
owner's mock-ups live in a private design canvas, "Slipgates — Session Workbench", and
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
   accessibility labels carry the detail. The one exception is Settings and first-run setup
   (#384, §6.9, §6.10): a step's subtitle, and one factual line of consent, security or cost
   that states a consequence before the action it is about, since a tooltip may never be seen.
6. **Every element has a purpose** (the owner, 2026-11-05). Nothing is drawn twice on one
   screen. If two elements show the same fact, one goes. A design review checks this first.

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

A readiness check (#384, §6.8) is not a session, and reuses five glyphs: `done` for Working,
`idle` for Missing, `ended` for Not selected, `failed` for both Blocked and Failed (the words
tell them apart), and `working` while it is Checking. So `done` is still never a session's.
**Not built yet (#384 slice 1):** today's Settings shows each check as plain text, with no
glyph.

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

An agent that needs sign-in, a turn that ended on an agent error, and a working session that
has gone silent each have a rule of their own (§6.13, #800). None of them ever reads as idle or
working.

While a session's machine is away (#269), none of the rules apply: what the row last said may no
longer be so. It shows the `failed` glyph (a host unreachable, §2.1) and, in secondary text,
"Offline · last seen 2m ago" once the machine has left the relay's presence, or "Reconnecting…"
while it is listed but its link is down; rule 7's overlay does not apply. A row's line 2 is
"Offline · *machine*". Nothing on it can be acted on (§6.1, "A machine away").
- **Not during a return's grace (#950).** For 3 s after the app comes back to the screen, every
  row looks as it last did: no `failed` glyph and no "Reconnecting…", though nothing on it can
  be acted on until its machine's list is whole (§6.1, "Coming back to the app"). Only once the
  grace has run out does a machine that isn't back show these away states, changing the glyph
  and words in place. Before #950 they showed from the first moment the link was down, so a
  reconnect of under a second flashed every row red.

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

### 4.4 Text size (PWA)

The PWA's Settings has **Text size**: Smaller, Default and Larger, a row of three toggle buttons
under "On this device only." It is for a person whose device draws the ramp too big or too small
(a tablet in landscape gets the desktop ramp of §4.3), and it is the web's stand-in for the
system text size the native apps already follow.

- **Scale:** Smaller is 0.9×, Larger is 1.15×, and Default is §4.3 exactly. Every role, its line
  height and anything sized in `rem` scale together, so the ramp keeps its proportions. No role
  goes under 11 px (§4.2's floor for mono, held for every role here).
- **What doesn't scale:** text fields stay 16 px at every size, because Safari zooms the page
  into a field set smaller. Icons, spacing and hit targets stay as they are; only a width set
  in `rem` to hold its text grows with that text.
- **Per device:** the choice is kept in this browser's storage, never synced to the person's
  other devices or to a machine. Default keeps nothing.
- **Before the first paint:** the stored choice is applied before the page is drawn, so the
  app never opens at one size and then jumps to another.
- **Narrow phones:** a page title never breaks inside a word. Where the Sessions title, Sort and
  the icons don't fit on one line (under 370 px, or 390 px at Larger), the header takes the list
  pane's two lines: the title and Sort, then the count and the icons, so Tab's order is still
  the order drawn (§9.1).

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
    Delegate events on touch are the exception: §6.4 gives their rows 44 and their lines a gap.
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
  panes scroll on their own. On a phone, the session's header floats over its conversation
  and collapses as the conversation scrolls (§6.2, #620). From 760 nothing collapses.
- **A pushed session** takes the whole screen on a phone, with the reply box at its foot: the
  bottom navigation gives way to it, and Back (the header's, or the platform's own) returns
  to the list.

**Resizing (the Windows app, #297; the PWA, #1030).** A divider between the list and the
session, and between the session and the inspector, wherever both show. The widths above are
the defaults; at two panes the list is 320 too, as long as the session keeps its narrowest.
Everything below holds in both clients; the PWA's differences are at the end.
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
- **The PWA** (`tokens.json` `layout.pwa`, the same defaults and limits) from 760 up; a phone
  under 760 keeps its one column and has no divider.
  - **Kept** in the browser's local storage, beside Your order, across reloads. Never sent to
    a host, like the app's.
  - **The divider** is an element with `role="separator"`, `aria-orientation="vertical"`,
    `aria-valuenow` its pane's width in px and `aria-valuemin`/`aria-valuemax` its limits,
    labelled "Resize the session list" or "Resize the delegate inspector". It is in the tab
    order; Left and Right move it 16 px, as the app's do. It drags with pointer events, so a
    mouse, a pen and a finger move it alike.
  - **Touch:** the line stays a hairline, but its hit area is 44 wide (`dividerTouch`) under
    a coarse pointer, centred on the line and over the panes' edges, and 8 (`divider`)
    otherwise. Nothing is drawn wider for touch.
  - **Reset:** a double-click or a double-tap puts its pane back to its default. Not a long
    press: that is the browser's own (selection, the context menu) and nothing tells a person
    it's there.
  - **Colours** as the app's: clear at rest, `stroke2` while pointed at, Bridge while dragged
    or focused (a visible focus ring, §9), and system colours under `forced-colors`.
  - **Motion:** none. The pane follows the pointer; a reset jumps, under reduced motion or not.
### 5.3 Motion

*Signed off by the owner on 2026-11-02 (#232). That includes three calls: platform
transitions keep their own timing, the in-flight progress ring is the only loop besides the
glyphs, and scrolling to a question never moves a conversation the person has scrolled back
through.*

> **Decided by the owner on 2026-11-04, D1–D6 (#659).** This section's text for "nothing pops"
> (principle 4; the Disclosure, Arriving and leaving, Easing aside, Status glyph, Activity dot,
> Value change and Toast and banner rows; and "Motion on data updates") is the spec, every
> decision option A. The options and reasoning are in
> [proposals/659-no-pop-motion.md](proposals/659-no-pop-motion.md); the clients follow in #684
> (Windows) and #685 (PWA).

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
4. **Nothing pops, and rows never move on their own** (§1, principle 2; #659). Nothing
   appears, disappears, or changes its size or its value without at least an ease, on any
   platform. An item arriving fades in as its slot opens, and its neighbours ease aside to
   make room; an item leaving fades out as its slot eases closed. A neighbour only ever eases
   to where the change put it, keeping its order, so the list never looks as if it reordered
   itself. What arrives in a burst arrives as one change, and a list the person has scrolled
   away from doesn't move under them (Easing aside, below).
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
| Disclosure | Show reasoning, Show prompt, Tool history, the finished delegates, the Ended group, a session group's header (§6.1, #767), the machine list, a delegate's details, a fold of delegate events (§6.4) | Opening or closing it | The chevron turns. Opening, the space eases open as the content fades in and settles 4 down, and what is below eases down with it (Easing aside). Closing, the content fades out, quicker, and the space eases closed. The space is laid out once: what moves is clipped and translated, never resized every frame | `disclosure`: standard, decelerate; fast out; nudge. The space: `shift` | Windows: `Expander`'s own motion where it is one; Show prompt's compact disclosure, the tokens on the chevron and the content (`Motion.Turn`, `Motion.Enter`); what is below it as Easing aside says. PWA: the tokens on the chevron and the content; the space as Easing aside | Instant |
| Arriving and leaving | A session row (also as Needs you shows or hides it), a Needs you entry, a delegate in the strip, a call in Tools, a line in the conversation (a delegate event, the Now line, a prompt you didn't type (#489), a receipt, a note, a question's card), an empty-state note, the Jump to latest pill (§6.4) | A host adds or removes it; a question is asked or answered; Needs you is turned on or off; the conversation is scrolled more than a screen from its end, or back to it (the pill) | The item fades in as its slot opens, and its neighbours ease aside (Easing aside). Leaving, it fades out where it was, quicker, as its slot eases closed. A session that ends, or leaves the list, while no card is held is the same: its card fades out where it was and the cards below ease up over `shift` to close the gap, as the cards a held card passes do in Reorder; they never jump into place (#659, replacing #605's departure jump). Items arriving in the same frame arrive together, as one block; one arriving while that block still eases joins its move. An approval's card and its command fade where they land and never travel of their own (D3, #659) | `listItem`: standard, decelerate; fast out | Windows: the list's `ItemContainerTransitions` stays empty, because `AddDeleteThemeTransition` slides an item in from the side and `RepositionThemeTransition` keeps its own timing; a composition fade on the item, and its neighbours as Easing aside. PWA: the tokens on the item; a copy of a leaving item, which nothing can find or tap, fades where it was. Apple: `.transition(.opacity)` in a change animated with the `standard` spring. Android: `animateItem` with the tokens' fade and placement specs | Cross-fade; a session card's gap closes with a short cross-fade (`value`), with no travel |
| Easing aside | Every list and stack: the session list, Needs you, the strip, Tools, the conversation, the inspector, and what is below a banner or a disclosure | Something above or beside it arrives, leaves, opens, closes or changes height | The neighbour eases from where it was to where the change put it, keeping its order. Only a translation runs: the layout changes once, and nothing is laid out again every frame. A second change while it moves retargets it from where it is, on `decelerate` so it never slows to a stop and starts again, never queued behind it. When the person has scrolled away from the edge things arrive at (the top of Tools, the end of a conversation they scrolled back through), what they are reading stays where it is: the scroll is anchored to it, and an "N new" pill at that edge says what came and takes them there. A response streaming in grows as it arrives, unanimated. Anything moved above an answerable question is a move for Scroll to a question's wait, which counts from where the move comes to rest | `shift`: standard, standard | Windows: an implicit composition animation on each item's `Offset` (and `Translation` for what a layout change moves), not a theme transition. PWA: FLIP (measure, lay out once, translate from the old place to none) with the Web Animations API. Apple: the change inside `withAnimation` on the `standard` spring. Android: `animateItem`'s placement spec, and `animateContentSize` on a block | Instant |
| Status glyph | A row, the session header, the delegate strip, a delegate event's line in the conversation (§6.4) | The state changes, such as working → idle | The old glyph cross-fades into the new one, with its colour, and the words cross-fade (Value change). The arc and the orbit turning are §2.1's, not this | `glyph`: fast, standard | The same on every platform: opacity on the two glyphs | Instant |
| Activity dot | A row's trailing edge (§6.1) | The session updates while its row isn't open; the row is opened | The 6 px dot fades in, and later fades out. It never pulses, grows or repeats. The title's weight cross-fades (Value change) | `activityDot`: standard, decelerate; fast out | The same on every platform | Instant |
| Value change | Details' values (Status, Model, Background, Turns, Compactions, Process, Updated), the status words on a row, in the header and in the strip; counts (Needs you · N, N finished, N new, Showing N of M); a title's weight; usage figures and bars | The value changes | The old value fades out as the new one fades in, in the same place, so nothing beside it jumps: numbers are tabular, and the value's cell keeps the wider of the two until the fade ends. A usage bar's fill eases to its reading (a horizontal scale). Changed at once: a value that changed less than 1 s before (it never strobes), an age or an elapsed time ticking, and a value drawn for the first time | `value`: fast, standard | Windows: the old text as a second `TextBlock` in the same cell, faded by composition. PWA: an absolutely placed copy of the old text faded out over the new. Apple: `.contentTransition(.opacity)`. Android: `Crossfade` | Cross-fade |
| Selection | The list's 3×16 Bridge pill; the rail's pill | Choosing a row or a destination | The pill slides from the old row to the new one when both are on screen, and fades in when the old one isn't. The row's fill changes at once | `selection`: standard, standard | Windows: `NavigationView`'s own pill on the rail, and the same motion on the list. Apple: the system's selection | Instant |
| Press | Every button, and every row | Pointer or touch down, and release | The pressed fill shows at once on press, and eases back on release. Nothing scales | `press`: fast, standard | Windows: the controls' own visual states. PWA: a transition on the background. Apple: the system button styles. Android: the ripple | Instant |
| Send and Answer | The reply box; a question card | Send, Send answer, Approve once, Deny | Your message shows at once as your bubble, rising 12 as it fades in. An answer's outcome fades in on the card. While the request is in flight, the button that sent it (Send or Send answer, never an option, #773) shows the platform's progress ring, which stops with the request. Decided, not yet built (#600; builds in #616, #625): the ring is the only in-flight signal (§6.5), turning while the request is open and still while this device waits for a machine that isn't connected. Delivered, the ring cross-fades to the `check` icon, which holds about 1 s and cross-fades back to the button's rest glyph; the button never changes size or fill. An exception's line (§6.5) fades in place in the room reserved for it under the box, or on the question card, and fades out in place when it goes; nothing pops and nothing below it moves. Today both clients show Sending…, Sending again… and "Delivered. The session has your reply." for a reply, and "Delivered. The session has your answer." for a question card's answer. Nothing waits for the motion to finish | `acknowledge`: standard, decelerate; small | Windows: `ProgressRing`. PWA: the answer's ring. Apple: `ProgressView`. Android: `CircularProgressIndicator` | Cross-fade; the ring is still |
| Toast and banner | In-app toasts; banners such as a new version being ready (a machine going offline is never a banner, §6.1, #950) | The event it reports | It slides 12 in from its edge and fades in, and what is below eases down to make room (Easing aside). It leaves the same way, quicker, and what was below eases back | `toast`: standard, decelerate; fast out; small | Windows: `InfoBar` with the tokens, on leaving too. PWA: the tokens on the banner coming in; a leaving one's copy, which nothing can find or tap, goes back the way it came, and the space eases closed over `shift`. System notifications are each OS's own | Cross-fade |
| Sheet | The delegate sheet on iOS (§6.3), and any phone sheet | Opening a delegate; dragging between detents; dismissing | The sheet rises from the bottom edge and the scrim fades in. While dragged it follows the finger with no easing, and on release it settles at the nearest detent | `sheet`: emphasis, decelerate; standard out | iOS: `.sheet` with `.presentationDetents([.medium, .large])` and the system's spring. Android: `ModalBottomSheet`. PWA: the tokens on transform | Cross-fade at the detent. A drag still follows the finger |
| Menu and flyout | The row menu, Sort, the overflow menu, flyouts | A click, a long press, Shift+F10 or the menu key | It fades in and settles 4 from its anchor. Choosing an item acts at once, and the menu fades out | `menu`: fast, decelerate; fast out; nudge | Windows: `MenuFlyout`'s own. PWA: the tokens, on a popover where supported. Apple and Android: the system menus | Instant |
| Scroll to a question | The open session's conversation, and the list when a notification opens a session | A question arrives while the person is at the end of the conversation; a toast, the tray, the menu bar, a waiting row or (in the PWA, until #253 reaches it) a Needs you entry opens a session; Jump to latest is chosen (§6.4), which scrolls to the end as the person asked, scrolled back or not | The view scrolls smoothly to the question or the row. It never scrolls a conversation the person has scrolled back through (a response growing below on its own doesn't count: the reader is still at its end), is touching, or has the pointer over, nor one in a page or app in the background. For 500 ms after a question appears in a conversation on screen (arriving, shown as its session opens, shown again as its tab, the Sessions page or the app comes back (the window taking the focus back included, visible all along or not), moved as the inspector is shown or hidden or the window crosses a layout width, moved where it is by anything above it changing height (the title wrapping, a tool line, the response, the reasoning or your message, a kept draft dismissed or an answer's receipt going, the delegate strip or a banner appearing or going), by the conversation being pulled back as it gets shorter, or sideways as the window or a pane is narrowed or widened, or becoming answerable as its machine's hold lifts or its machine starts taking answers), or is scrolled into view (a notification's answer focused included), a click or tap on its choices, Approve once, Deny or Send is ignored, so a click already on its way to what was there can't answer it. Needs you's Approve once, Deny and Resend wait the same way after the lane changes (an entry arriving, going or moving, and its answers moved by an entry above growing as its answer goes, by anything above the lane changing height, a banner or the app bar appearing included, or by the list being pulled back as rows below get shorter or go while it is scrolled to its end; an answer's status appearing below the last answers, or the list scrolling, moves nothing and doesn't restart it) or comes back on screen, and while a session opened from it scrolls the list to its row. A smooth scroll to it counts from where it comes to rest; a scroll the person makes after that doesn't restart the wait, unless something above the answers changes height in the same moment, which counts as a move (the scroll and the move can't be told apart). A scroll pulled back to its end (what it scrolls got shorter, or it got taller) is never set aside as the person's: it counts as a move. A click or tap on an answer first reads where the answers are: moved since they were last read (an update drawn in the moment before the click), or unreadable, the click is ignored and the wait starts. A key acts at once | `scroll`: emphasis, standard | Windows: `StartBringIntoView` with `AnimationDesired`; the answers' places read after each layout pass and scroll (`AnswerWatch`) and as a pointer presses one, setting aside only a scroll the person makes (a wheel, a finger, a key or the scroll bar). PWA: `scrollIntoView` with smooth behaviour, at the browser's pace; the places read on `ResizeObserver` and scroll events (`watchMoves`) and as a click comes. Apple: `ScrollViewReader`. Android: `animateScrollToItem` | Instant: a jump |
| Reorder | The session list (#164) | Alt+↑/↓; Move up or Move down; a drag by the handle; an order change arriving during a return's grace (§6.1, #950) | By key or menu, the moved row slides to its new place, and the row it passed takes its own new place at once. In a drag, the row follows the pointer, a line shows where it will land, and on drop it settles into place. No other row moves | `reorder`: standard, standard | Windows: no `ReorderThemeTransition`, which slides the neighbours. Apple and Android, later: the system list's drag, whose gap follows the finger | Instant |
| Collapse | A phone's session header (§6.2, #620) | Scrolling the conversation up 64 or down 32 in one direction, mid-conversation; a tap on the bar's title; focus moving into the header | The header slides down over the conversation, or back up under the bar. The conversation never moves. Near the top there is no motion of its own: the header rides with the scroll | `collapse`: standard, standard | PWA: the tokens on transform. Apple: the large title's own collapse. Android: the top app bar's scroll behaviours | Cross-fade |

**Not in the design:**
- **Loops** other than the two glyphs and an in-flight progress ring: no pulsing dot, badge
  or Needs you toggle, no shimmer, no animated placeholder.
- **Parallax, bounce and overshoot,** on content or on controls. Every curve stays within
  0–1, and every spring has a damping fraction of 1.
- **Motion on data updates:**
  - A response streaming in shows as it arrives, with no typing effect and no fade per
    chunk.
  - Ages and elapsed times tick in place: a clock moving isn't news. Counts, words and
    percentages cross-fade (Value change), and never roll.
  - A usage bar eases to each reading over `value` (#659, D4), a single short scale. It used to
    take each reading at once, so that several bars easing every few minutes wasn't motion on
    its own; at 83 ms it reads as a change in place, not a sweep.
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
   - A session that needs sign-in is never counted in N nor shown by the filter: the sign-in
     banner owns it (§6.13, owner decision 2026-11-05). Its row still says so where it lives.
   - A question on a machine that is away, or back but not yet confirmed by its next full list,
     is neither counted in N nor shown by the filter, nor raised as a notification (#269). The
     exception is a return's 3 s grace (#950, "Coming back to the app", rule 6): N and the
     filter's rows stay as they were, with the question held, until the grace is over.
   - **Look:** the `needs` glyph (`idle` at none), then "Needs you · N". It is a control, so it
     takes the platform's button styling and never a status colour. Pressed, it has the
     selection's look (the quiet fill, the 3×16 Bridge pill, semibold words) and, at its
     trailing edge, "Showing N of M", or "N on *machine*" with a machine chosen. It is 36 px tall
     on a desktop and 44 on a phone.
   - **Pressed:** the list shows only the rows waiting on you, in Pinned, your groups and
     Sessions, in their own order and under the Sort menu's choice. Sections and groups with
     none, and Ended, are hidden. A collapsed group holding one shows open while the filter is
     on, and is collapsed again when it is turned off: the filter never changes a group's
     collapsed state ("Groups", below).
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
3. **Your groups** (#767), each a named group that collapses, in your order ("Groups", below).
4. **Sessions,** in your order (#164): the rows in no group.
5. **Ended · N,** collapsed.
6. **Collaborators** (#519), once you have a link: one group per collaborator, by verified
   @login, with their machines nested, after your own sessions (§6.7). Needs you's N never
   counts their items, and its filter never shows them.

**Order (#164).** Rules the clients share; the PWA and the Windows app keep the order in
their view models (`pwa/js/session-order.js`, `App.Core`), not in the markup.
- **Per device.** Kept on the device (the PWA's `localStorage`, a file beside the Windows
  app's folds). Never sent to a host: it isn't protocol. Your groups are kept with it, and
  roam with it once #759 does ("Groups", below).
- **New sessions** go to the top of Sessions once, then stay put, in no group unless they
  continue a grouped session ("Groups", below). A machine's first list is
  placed together, newest first, once the machine has sent all of it.
- **Moving** is the person's only: Alt+↑/↓ on a row; a drag by the handle that shows on hover
  (mouse or pen); or the row menu, opened by a long press on touch, a right-click, or
  Shift+F10 / the menu key.   Its items are Move up, Move down, Pin (or Unpin), Move to group, and for a row in a
    group, Remove from group (#767). Alt+↑/↓ and Move up and Move down cross the edges between your groups and Sessions
  ("Groups", below), but never into or out of Pinned: Pin and Unpin do that. A row dragged
  into or out of Pinned is pinned or unpinned; a row dragged into a group, or onto a group's
  header, open or collapsed, joins that group.
- **Pins:** a pinned row goes to the foot of Pinned, and an unpinned one to the top of
  its group, or of Sessions when it is in none. Pinning keeps a row's group: it shows in
  Pinned only, and comes back to its group when unpinned.
- **Sort menu:** "Your order" (the default), "Most recent" and "Machine" (by machine name,
  then your order). It sorts within Pinned, within each group and within Sessions; the groups
  themselves stay in your order. Rows move only in Your order.
  Where the order can't be kept (storage blocked or full), the list is Most recent and Your
  order can't be picked.
- **Ended** holds the sessions a host removed while the client was running: the most
  recently ended first, at most 20, until the client restarts. A machine that goes offline
  or out of view ends nothing. An ended session leaves its group for Ended, which is never
  grouped, and keeps its membership for a resume ("Groups", below).
- **Unread:** a row whose session updated since the row was last open: the selected row (an
  unfolded card in the PWA until #179 lands). A session seen for the first time starts read.

**Groups (#767).** *Decided by the owner on 2026-11-04 (#767): groups the person makes; their
names and membership roam; collapsed or expanded is per device; a new group starts expanded.*
Groups are the person's own, for purpose ("Team", "Personal"). Nothing groups a session by
itself: not its repository, folder, machine or the session that launched it. Suggestions from
launch lineage may come later, on top of this, once hosts report it.
- **Where:** between Pinned and Sessions, in your order. A row is in at most one group. A
  group spans machines: with a machine chosen or a search on, each group shows only the rows
  that match, and a group with none is hidden. With All and no search, an empty group shows,
  its header reading "· 0", until it is deleted. Ended and Collaborators are never grouped.
- **Filters open, never toggle.** While Needs you's filter or a search is on, a collapsed group
  holding a row that matches shows open, and its header reports expanded, as drawn; it can't
  be collapsed until the filter or search is off. Then it is collapsed again: the stored state
  is unchanged.
- **The header** is a §5.3 Disclosure, 36 px tall on a desktop and 44 on a phone, with the
  list's leading inset: a chevron (→ collapsed, ↓ expanded), the name in the body face,
  semibold, then "· N", the rows shown in it on the machine chosen, in `text2`. A pinned row
  keeps its group (Pins, above) but shows, and is counted, only in Pinned: no group's count,
  needs or dot includes it. Its trailing edge, while it is collapsed:
  - with any row in it waiting on you: the `needs` glyph and "N needs you", in Needs you's
    text colour. A collapsed group never hides a question: Needs you's N counts its rows,
    its filter shows them (above), and notifications and badges are unchanged (§7);
  - otherwise, with any row in it unread: the 6 px Bridge dot;
  - otherwise nothing.
  Expanded, the trailing edge is empty, since its rows show their own state. A header has no
  status glyph, age or second line: a group isn't a session.
- **Toggling:** a click or tap on the header, Enter or Space, → and ← (§9.2). It changes at
  once, as §5.3's Disclosure: its rows' space opens or closes and nothing below slides.
  Collapsing a group holding the open session keeps it open; keyboard focus on a row inside
  goes to the header (§9.1 A4).
- **Its menu** (the ⋯ button that shows on hover or focus, a right-click, a long press on
  touch, or Shift+F10 / the menu key): Rename group, Move up, Move down, Delete group. Alt+↑/↓
  on the header moves the whole group among your groups; a drag by its handle does the same.
- **Making and filling a group:** a row's Move to group lists your groups, then "New group…";
  Remove from group is beside it in the row's menu. On a phone it is a sheet (§5.3's Sheet) with the
  same items, the row's current group checked. "New group…" asks for a name (an inline field
  on the header on a desktop, the sheet's field on a phone), makes the group at the foot of
  your groups, expanded, and moves the row into it. A name is 1–40 characters after trimming,
  shown as plain text, and unique among your groups ignoring case; at most 32 groups.
- **Where a moved row lands:** Move to group, a drop on a header, or New group… put the row
  at the top of the group. A row moved into a collapsed group leaves its place as §5.3's
  Arriving and leaving says, and keyboard focus goes to that group's header, whose name says
  the rest (§9.2).
  Remove from group puts it at the top of Sessions.
- **Keys across edges:** Alt+↓ on a group's last row moves it to the top of whatever follows
  (the next expanded group, or Sessions); a collapsed group in the way is passed over as one
  step, never entered, so the moved row stays on screen. Alt+↑ on the first row of Sessions or
  of a group moves it to the foot of the expanded group above, passing collapsed ones the same
  way. At the top of the first group, or the foot of Sessions, nothing moves.
- **Renaming or deleting** never ends, hides or reorders a session. Delete group asks nothing:
  its rows go to the top of Sessions in their order, a pinned member stays pinned and leaves
  the group, focus goes to the first of the moved rows (or, with none, the next header or row,
  else the one before it, else New), and it is announced politely ("*name* deleted. 3 sessions
  moved to Sessions"). An empty group stays until it is deleted.
- **Kept and roaming:** groups are kept with your order (above). Their names, their order and
  which sessions are in them roam with your order, pins and sort once #759 lands: keyed by
  machine id + session id, last writer wins per change, in the same record as the order and
  under its privacy rules (ids and the names you gave, never titles or paths). Collapsed or
  expanded is per device and never roams; a group
  first seen on a device, made there or arriving from another, starts expanded. Until #759
  lands, all of it is per device.
- **A resumed session keeps its group by its resume lineage.** A session's membership outlives
  its ending: it is kept for 30 days after the session was last listed, so a resume finds it.
  - Resumed on its own machine, by anyone (a client's `session.launch` with `resume`, or at the
    machine), it keeps its id (v1-launch.md), so its key is unchanged: it goes back to its
    group, at the top, whether or not this client saw the launch.
  - Brought from another machine (`transfer`), it is resumed under a new id. The client that
    asked knows the lineage from the launch's result (`launched.transfer`'s `from` and
    `session`, with `launched.resume` its new id here), and puts the new key in the old one's
    group, which then roams like any other change.
  - Only a session whose own key has no kept membership and whose lineage no client recorded
    (a new id from a transfer no client saw, an agent that started afresh, or membership
    already dropped) is a new session: the top of Sessions, in no group. A host never reports
    lineage today; if it does later, it is used the same way.

**A machine away (#269).** A host's relay link drops now and then (a restart, an update, a
network blip), often for under a minute. While its machine has left the relay's presence, and the
client's own relay link is up on the same account:
- Its rows stay where they are, read as stale (§2.2; after a return, only once the grace below has
  run out), and so do the session open, its machine tab, your order and pins.
- Nothing on them can be done: answering, Approve once and Deny, replying, attaching, End session,
  and launching or resuming on that machine are disabled. Their questions leave Needs you, the
  badges and notifications (after a return, once the grace below is over). In an offline machine's session, the reply box and the question say
  why in place of their actions (§6.4, §6.5; #950).
- Back again, they can be acted on only once the machine's next full list (its snapshot) has
  confirmed that the session and its question still exist, never on the copy kept meanwhile. A
  session that ended meanwhile goes to Ended then, and a question that ended never becomes
  answerable. A question notification withdrawn meanwhile comes back silently if still asked.
- The same hold applies, for a moment, to a machine still connected whose events had a gap (some
  were lost, so it is asked for its full list again; v1-sessions.md): its rows can be acted on
  again once that list is whole (#427).
- After **15 minutes** away its rows go, and the machine is listed as offline (§6.6). It goes at
  once when it is forgotten, when the client is signed out or refused, or when the account changes.

**Coming back to the app (#950).** A phone suspends the PWA when it leaves the screen, and its
links drop; coming back, they reconnect, usually in well under a second. Coming back is calm:
the list keeps what it last knew while it catches up, it stays honest about what can't be done
yet, and connection news shows only when it is news. These are interaction rules; the visual
language doesn't change. Decided 2026-11-05 (Creative; Product accepted rules 1–7), with rule 8
added the same evening; the timeline is drawn in
[proposals/950-calm-return.md](proposals/950-calm-return.md).
1. **A 3-second grace.** It starts when the app comes back to the screen: the PWA's
   `visibilitychange` to visible, `pageshow` from the back/forward cache, or `online`; the
   Windows app waking from sleep or changing network. While it lasts, every row, the open
   session and its machine tab look exactly as they last did: no `failed` glyph, no
   "Reconnecting…", no strip and no banner. Anything that arrives updates in place, and an order
   change uses §5.3's Reorder motion.
2. **The hold still applies, at once.** As in "A machine away" (#427), nothing on a session is
   acted on until its machine's full list is whole again. Its controls are `aria-disabled` at
   once, so a screen reader is never told a held action works. They are drawn disabled only once
   the hold has lasted 600 ms. A press before then does nothing and shows "Updating…" in text2
   under that control, never a toast. Opening a session and reading it work throughout.
3. **A quiet cue only when it's news.** If any machine's list is still not whole 1 s after the
   return, the title row shows "Updating…" in text2, in a fixed slot beside the title. It
   doesn't move, isn't announced, and goes the moment every list is whole. A return that
   finishes in under 1 s changes nothing on screen. On the PWA desktop and in the Windows app,
   the list pane's own header row (the one holding the Sessions heading) plays the phone title
   row's part, and the slot is at its end. Neither the slot nor the strip ever goes above the
   list or into the window chrome.
4. **When the grace runs out (3 s),** a machine that isn't back shows the away states on its rows
   (§2.2): the glyph and words change in place. If the client's own link is still down, the
   connection strip replaces the title row's content (the list header row's, on a desktop and
   in the Windows app), at the same height (§7). It is announced once, politely. The Windows
   app does the same, and no longer raises its "connecting" banner for this case.
5. **Nothing pushes the rows.** There are no "Connecting…" rows above the sessions, ever. A
   machine with no kept list says so in the same fixed slot ("Connecting to 2 machines…"), never
   as rows. Banners already showing when the app comes back stay put. A new connection banner is
   never inserted above the list.
6. **A real outage reads as it always has** once the grace is over, including the 15-minute
   rule above. During the grace, rule 1 wins for what is drawn: a held question keeps its row,
   in the list and under Needs you's filter, and N (and so the badges) doesn't change, though
   the question is held at once (`aria-disabled`, rule 2). Once the grace is over, a machine
   still away has today's behaviour: its questions leave Needs you, the badges and
   notifications. Notifications follow the hold as they do now.
7. **Platforms:** the PWA first. The Windows app takes rules 1–5 on waking from sleep, with the
   same 3 s. Apple and Android take them through §7 when they are un-parked.
8. **A machine's connection news is never a sentence over the list.** Offline, Connecting,
   Opening a channel and Couldn't reach all go from above the list (the PWA's lines from
   `hostNote()`, #396).
   - Inside an offline machine's session, the reply box and the question say why, in text2, in
     place of their usual actions: "*machine* is offline · you can answer once it's back" (§6.4,
     §6.5). If focus was on a control the line replaces, focus moves to the line, which takes
     `tabindex="-1"`. It isn't announced separately: its words are what is read as focus lands.
     Focus never drops to the page.
   - The machine tab and the Machines page (§6.6) carry the machine's own state.
   - Only an error the person can act on (Couldn't reach, unsupported) shows, in rule 3's fixed
     slot, on one line, naming the machine ("Couldn't reach *machine*"). While it shows,
     "Updating…" doesn't. With more than one, the slot shows the first in machine order, plus
     "and N more" if that fits the line.

| Time after the return | Rows, open session, machine tab | A held session's controls | Title row's slot | Connection strip |
|---|---|---|---|---|
| 0 (return) | As they last were; anything arriving updates in place | `aria-disabled` until the machine's list is whole; drawn as they were. A press does nothing and shows "Updating…" under the control | Empty | None |
| Every list whole before 1 s | Fresh data, in place | Back to normal | Never shown | None |
| 600 ms, hold still on | As they last were | Drawn disabled | Empty | None |
| 1 s, any list not whole | As they last were | Drawn disabled | "Updating…", or "Connecting to N machines…" for machines with no kept list; an actionable error ("Couldn't reach *machine*") in their place. It goes when every list is whole | None |
| 3 s, grace over | A machine not back: the away states (§2.2), in place. The rest as they are | Disabled as §6.1 says; an offline machine's session says why (rule 8) | As at 1 s, unless the strip has replaced the title row's content | Only if the client's own link is down: in the title row (or list header row), at its height, announced once, politely |

Through the grace, Needs you's N, its filter's rows and the badges stay as they were (rule 6).
On the PWA desktop and in the Windows app, "title row" is the list pane's header row (rule 3).

The owner may change the 3 s grace; the 1 s cue and the 600 ms point stand with it unless the
owner changes them too.

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
    "· View only" for a read-only machine (§6.6), "connecting…" while it connects, "offline"
    while it is away ("A machine away"), and "bridge isn't running" once its host is gone (§6.14).
  - **Keyboard:** the arrows move through the tabs shown, Tab goes on to More, and Enter or Space
    opens its menu. Touch and the mouse reach More as any button.
  - It holds at the narrowest list (264, §5.2) and in one column. The PWA's tabs still scroll
    sideways (#351).
- macOS: a "Machines" sidebar section.
- iOS: a toolbar button.
- Android: filter chips.

### 6.2 Session header

- **Title:** the session name.
- **One meta line:** glyph · status text (rule set §2.2) · machine · repo. A phone's is
  shorter (below).
- **Actions:** icon buttons with tooltips: Pin, Details (everything removed from rows),
  and toggle inspector. End session lives in Details on desktop, and in the overflow menu
  on phones.
- **Phone:** Back (to the list) leads the header, and Pin, Details and End session are in its
  overflow menu.
- **Phone: at rest (#620; the owner, 2026-11-05).** The title, one meta line and the tabs: 152
  tall at 390 × 844 with a two-line title, in every delegate state.
  - **The title:** 24/32 bold, between Back and More, **at most two lines** with an ellipsis.
    The full title is the h2's text, so it is read in full, and it is in Details.
  - **One meta line, never wrapped:** glyph · status (§2.2) · machine, and the account only
    when it isn't the person's own ("Delegating to 2 agents · DEV-VM2"). The status is never
    shortened. The account, then the machine, give way with an ending ellipsis, and their full
    text is still read. The repository is in Details.
  - **The tabs, on their own row of 48:** Conversation, Delegates, Tools.
    - **There is no delegate strip on a phone,** whatever the delegates are doing. The
      Delegates tab carries what it carried.
    - **While delegates are running,** the tab's label has their count: "Delegates 2", read as
      "Delegates, 2 running". With none running, there is no count. The count is the
      session's own delegates, not the ones they delegate to in turn.
    - **While a delegate is asking,** the tab leads with the `needs` glyph, and its name says
      so: "Delegates, 2 running, 1 asking". Asking counts a delegate at any depth, since the
      question is the person's wherever it comes from. The question itself is in the
      conversation, naming the delegate (#661, §7.0), and is answered there without opening the tab.
    - The count and glyph change in place, so the row never changes height.
    - The tabs are one Tab stop with arrows inside, as every tab list.
  - Tablet, desktop and the Windows app keep the header above, chips included.
- **Phone: collapse on scroll (#620).** A session opens with its header in full, as above. The
  header floats over the conversation's top, and the conversation scrolls under it.
  - **Near the top,** scrolling down slides the title, meta line and tabs up under a
    pinned 48 bar: Back, the session's glyph, the title on one line with an ellipsis (17/22,
    semibold), More. The header follows the scroll with no easing (iOS's large title). The
    bar's fill, hairline and title fade in (`crossFade`) once the big title is under it (40).
    Once the header has gone under the bar (the at-rest height less 48), it is compact.
  - **Mid-conversation,** 64 up in one direction brings the whole header back over the
    conversation (`collapse`), and 32 down sends it away. The count restarts when the
    direction changes, so reading back and forth changes nothing. A tap on the bar's title
    brings it back, and the top always shows it in full.
  - **Only the person's own scrolling counts:** a finger, a wheel, a trackpad, the keys or the
    scroll bar. A scroll the app makes (to a question, a restored position) and a response
    growing below leave the header as it was, except on a screen shorter than twice the
    header (a landscape phone), where a scroll to a question folds it to the bar first.
    There the header is also capped at the conversation's height and scrolls within itself,
    so focus inside it is never out of view. Overscroll is clamped, so a bounce never reads
    as a scroll up. Scroll to a question puts it 8 below what the header covers (the bar, or
    the whole header if it was showing). The question guard (§5.3) is unaffected: the
    question never moves as the header collapses or returns.
  - **While the session waits on you,** the bar's glyph is `needs` on its disc.
  - **The title stays the one h2.** The bar's copy is drawn, not read; the status is read from
    the meta line. Nothing is announced. Focus moving into the header brings it back, and it
    stays while focus is there, until a press on the conversation. Focus the app puts on the
    title as a session opens (#707) doesn't hold it: the person's own scrolling folds it as
    usual. The conversation's scroll padding is what the header covers plus 8, so focus
    never lands under it. Tab order is Back, More, the tabs, then
    the conversation. The tabs are one tab stop, on the selected tab; Left and Right move
    and select, wrapping, and Home and End go to the first and last.
  - **Reduced motion:** coming back and going are a cross-fade with no travel; near the top
    the header still follows the scroll. The thresholds don't change.
  - **Only the conversation collapses it.** On the Delegates, Tools and Details tabs the header
    stays whole.
  - **From 760, and in the Windows app,** the header never collapses. A session never opens
    collapsed. Native clients use the platform's own: `NavigationStack`'s large title on
    Apple; `exitUntilCollapsedScrollBehavior` then `enterAlwaysScrollBehavior` on Android.
- **Details** is a property list, the same rows in the same order with the same words in every
  client (#1087; the owner, 2026-11-06: one set, nothing either client showed dropped). A row
  whose value the host didn't send is left out, never shown empty; Status, Agent, Updated and ID
  are always there. Then End session.

  | # | Row | Value | Shown |
  |---|---|---|---|
  | 1 | Status | The status words (§2.2), e.g. "Working", "Needs sign-in", without a time: rows 2 and 4 carry it. While away, the away words with their "last seen" | Always |
  | 2 | In this state | "for 12m": how long in this status | Not when idle or away |
  | 3 | Message | The host's sentence for what is wrong (#800), plain text, never a link | When sent |
  | 4 | Not responding since | Clock time it went silent (§6.13) | When not responding, not away |
  | 5 | Driven by | The driver's principal, else "an agent" | When agent-driven, not away |
  | 6 | Agent | The agent's name, e.g. "Copilot", "Claude", "Codex" | Always |
  | 7 | Model | As the host reports it | When sent |
  | 8 | Machine | The machine's name | When known |
  | 9 | Repository | owner/name | When sent |
  | 10 | Branch | Mono | When sent |
  | 11 | Folder | Mono; a long path gives up its middle, never a word | When sent |
  | 12 | Started | Local clock time; another day adds the date; the full timestamp on hover | When sent |
  | 13 | Updated | "2m ago" | Always |
  | 14 | Background | "2 background agents · 1 background command" | When any |
  | 15 | Turns | "12 turns", "1 turn" | When sent |
  | 16 | Compactions | "3 compactions" | When more than 0 |
  | 17 | Process | "pid 4312", mono | When sent |
  | 18 | ID | The full session id, mono | Always |

  The groups run state (1-5), who (6-7), where (8-11), when (12-13), activity (14-16), then
  identifiers (17-18). Agent and Model sit together because the model is part of who is
  working. A value carries its unit ("12 turns", "pid 4312"), so it still reads right when it
  is copied or read alone. A value wraps rather than being cut off; only Folder shortens, and
  only by its middle. A label may wrap too. While the session's machine is away (§2.2), Status
  reads the away words, "Offline · last seen 5m ago" or "Reconnecting…", whose time is the only
  one in the state group; In this state, Not responding since and Driven by are left out, since
  they may no longer be so. The other rows stay as last known, and Updated says how old they are. Every value can be selected and copied. The ages and durations tick
  with the row's own clock. The PWA draws it as a `dl`; the Windows app reads each row as
  "Row, value". Both clients take the list from one shared definition where they can, and a
  test in each checks its rows against this table.
  At three panes it heads the inspector, over Tools (each call: glyph, name and arguments in
  mono, age). Below three panes Tools is a tab beside Conversation, and Details takes the
  conversation's place until pressed again. Delegates (#176) join the inspector and the tabs: a
  delegate chosen takes the inspector in place of Details and Tools (§6.3).
- **Model, effort and context on a running session** (#1142, planned; Product accepted
  2026-11-07). Not built: the table above and `session-details.json` don't change until the
  host reports these per session (#1142 PR 2). Then Effort and Context join Model in the who
  group, and switching (#1142 PR 3) works as follows.
  - **What the host sends, per axis:** the current value; the allowed values, each with `value`,
    display `label`, cost `tier` and `isDefault`; `applies` (`live`, `nextTurn`, `launch` or
    `resume`); any `pending` change (`value`, `by`, `at`); and a refusal's reason. The UI
    offers nothing the host didn't list. The host builds those lists by interrogating the
    agent live, never from remembered or cached values, so the UI offers only what the agent
    reported as supported now.
  - **Rows:** a value shows its `label` in the text face (§4.2), and the raw value only when there
    is no label. A value the host didn't send is left out, as for any row; one the agent can't
    report reads "Not reported", never a guess. These rows never appear on a session row
    (§6.1) or in the header.
  - **Changeable or not:** an axis this session can switch is a button, named "Model, Sonnet
    5.5, change". One it can't is a plain row with the reason in secondary text, "Set at
    launch" or "Changes on resume". The row stays visible so nobody hunts for a missing
    control.
  - **The chooser:** a sheet below 760, inline in the inspector at three panes, a RadioButtons
    flyout in the Windows app. It lists only this session's allowed values, the current one
    checked and the agent's default marked "Default". A costly value carries a "Higher cost"
    tag in words, never colour alone. The new-session sheet uses the same chooser, labels and
    tags, fed from the machine's launch options (#1142 PR 1), so launching and switching look
    the same.
  - **Costly change:** only a move to a higher cost tier asks first: "Switch to Opus 5.5? It
    costs more per turn than Sonnet 5.5." The buttons are Switch and Cancel, with focus on
    Cancel. A same-tier or cheaper move doesn't ask. An agent caller must pass the same confirm
    flag. Every change is audited, whoever made it (owner decision, 2026-11-07).
  - **States, in words on the row and announced politely:**

    | State | Row reads |
    |---|---|
    | Requested | "Switching to Opus 5.5…"; the value stays the old one |
    | Mid-turn | "Opus 5.5 from next turn" |
    | Applied | The new value, only once the session's own reading shows it; never on faith |
    | Refused | The old value and "Not changed: {reason}" |
    | Unconfirmed after the host's timeout | "Couldn't confirm the switch. Still showing Sonnet 5.5." |
    | Machine away (§2.2) | The button is disabled: "Machine offline" |
    | Another device's pending change | Shown on every device, so two people don't race |

  - **Attribution:** each applied change adds one event line to the conversation, "Model
    changed to Opus 5.5 by {device or agent}", with the "Higher cost" tag when it applies.
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
- **Phone:** no chips (§6.2, the owner 2026-11-05). The Delegates tab carries the running count
  and the `needs` glyph, and a delegate's question is answered in the conversation. iOS opens
  a delegate from the tab in a sheet with medium and large detents. Android and the PWA open it
  in place or as a pushed screen.
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
  one opened is pushed in the list's place with "All delegates" to go back. A
  delegate stays open across a resize, and as it finishes (its fold opens).
- **Present tense:** the strip, the Delegates tab and the chips are a delegate's only live
  status. The conversation keeps a past-tense record of its events (§6.4, #661), and the
  inspector holds the detail.
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
    Disclosure motion (§5.3): the chevron turns, the space eases open or closed over `shift` with
    what is below easing with it, the text fades, and it is instant under reduced motion. Open, it
    reads "Hide prompt" and shows all of it in reading
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
- **Time on messages** (#1619). Every message says when it was sent, from the times the
  history already holds; no protocol change. Seven rules, one formatter per app, one shared
  test table:
  1. *Which time.* When it was sent, never when it was received or drawn. A prompt takes its
   turn's time; a reply, its first response part's.
  2. *Reading it.* Under 10 s `just now`; 10 to 59 s `Ns ago`; 1 to 59 min `N min ago`,
   rounded down. The relative window wins over the calendar day (11:50 PM reads
   `20 min ago` at 12:10 AM). From 60 min, the viewer's local clock time (`7:41 PM`),
   with the date for another day (`Nov 9, 7:41 PM`) and the year for another year. Clock
   and date order follow the viewer's locale. A time ahead of this machine's clock reads
   `just now`, never a future time.
  3. *The exact time.* The local date and time with seconds and the offset (`Mon Nov 9, 2026,
   7:41:07 PM UTC-08:00`; an offset, as a Windows zone name gives no reliable initials).
   Hover and keyboard focus show it as a tooltip, and a click or tap opens a small
   popover; neither moves the message. The accessible name is the exact time, fixed, so a
   screen reader never hears it tick. The time is a button, in reading order after the
   sender, with a 44 px target that does not grow the row.
  4. *Where.* On the label line after the sender for an agent's reply and any prompt you
   didn't type. For your own prompt, in a meta row under it at the trailing edge, a row
   that always exists, so a time arriving later pushes nothing.
  5. *Refresh.* The text changes once a second only while a visible message is under a minute
   old, otherwise once a minute, and is worked out again when shown. Nothing refreshes
   while hidden. Changing text must not reflow the label line.
  6. *No time yet.* The live latest exchange shows none until its history arrives. An
   unreadable turn, and a question with no outcome time, show none. A time is never
   guessed.
  7. *Never order.* Time is a label. Order is by time sent (#1613) and no time moves a message.

  Closed-question and delegate lines move to the same formatter (#1601, #661).
- **Delegate events** (#661, built in the PWA and the Windows app). A past-tense record of the main agent's own delegates starting and reporting back. The strip, the Delegates tab and
  the chips (§6.3) are the present tense and the only live status, and the
  inspector holds the detail, so a line never says a delegate is working and never shows the
  working glyph. A fold's "still working" count (D3, below) is the one exception: a live count
  taken from the same delegate state (`subagents`), with no glyph. Each line is meta style, with
  the delegate icon for a start and the outcome's glyph for a report-back. Each name links to its
  delegate, and the word "Delegate" stays outside the link.
  - **Where (D1):** each event sits between messages by its time. A report-back sits just above
    the main agent turn it started (subagents.md §3). Events are never gathered under the
    latest reply.
  - **One line per delegate per gap (D2):** a delegate that starts and reports back between the
    same two messages is one line, "Delegate *Run full test suite* · ran 21m, reported back ·
    2h" ("failed" or "ended" as it ended), aged by its report. A start-only line reads "Started
    delegate *Tidy changelog* · 9m", and if that delegate reports back before the next message
    the line becomes the merged form in place. Its glyph and words change as §5.3's "Status
    glyph" and "Arriving and leaving" rows say. If the longer text wraps onto another line,
    what is below it moves as §5.3 says for a change in height.
  - **Every delegate named (D3):** delegates started within 60 s of each other share a line,
    "Started *Tidy changelog* and *Update screenshots* · 9m". With three or more it reads
    "Started 3 delegates: *A*, *B* and 1 more", and "1 more" expands the line to name the rest.
    "*n* more" meets §9.1 A11's size: 44×44 on touch, 32×32 with a pointer. Its text can stay
    small; the hit area makes up the size.
    On touch every row of every line, on one row or wrapped, is 44 high, so each name and "*n* more"
    fills its row: a 44 target over no other name. Lines sit 8 apart, so a line's own rows are
    always closer together than the next line and a wrapped line reads as one. With a pointer,
    lines keep the meta style's own leading.
    One that reports back in the same gap leaves the shared line for its own merged line, in place.
  - **Runs fold (D3):** 2 or more lines in a row between two messages fold into one (#1063; it
    was more than 3), drawn
    "5 delegates · 4 reported back › 1 still working". It shows no working glyph. It is closed
    until opened, and expands in
    place to its lines, indented under a rule (Disclosure, §5.3), and is remembered open or
    closed per session for the visit, never saved to disk. The PWA and the Windows app both
    draw it, with the same words and counts.
    - **The count is live, and never guessed.** Each delegate in the run is in exactly one
      count, from its entry in `subagents` ([v1-sessions](../protocol/v1-sessions.md#subagent))
      while it has one:
      - `status` `working` or `waiting` counts it **still working**. A live entry wins over
        anything known before, so a background delegate woken again moves back here;
      - `idle` or `ended` with `outcome` `done` counts it **reported back**, and with `outcome`
        `failed`, **failed**;
      - `idle` or `ended` with no `outcome` (the host never saw it end) counts it **ended**.

      Once its entry leaves `subagents` (the host trims finished ones first), the last outcome
      known for it, or its report-back event, is kept. A delegate the host left out with no
      outcome known (only `subagentsMore` counts it) is in the total only, never counted as
      working or as reported back.

      Counts of 0 are left out ("5 delegates · 3 reported back, 1 failed › 1 still working").
      They change in place as state arrives. At 0 still working the link goes, and the line is
      the button alone.
    - **Focus when the link goes (§9.1 A4):** if focus is on the fold's toggle (its button) or
      its "still working" link when that link goes: while the fold stays, focus moves to the
      fold's toggle; if the fold goes too, it moves to that delegate's name in its line, or to
      the first delegate's name. If the focused control is no longer drawn (hidden in place),
      it counts as lost focus and follows the same rule. Focus anywhere else stays where it is,
      and the view never scrolls to follow it (H3a).
    - **Two sibling targets, never one inside the other:** the fold's button, then the "1 still
      working" link after it in the tab order.
    - **The chevron is the button's.** It ends the button's text, inside its target and focus
      ring, and turns as it opens. The link follows after a 12 gap, with its own focus ring.
      Each target meets §9.1 A11's size, and the link's target never covers the chevron.
    - **The link opens a delegate** where the live status shows it (§6.3): its strip item, or
      its chip on a phone. If the phone's chips give way to the Delegates tab (#629), it opens
      the delegate's entry there. With one still working, it opens that delegate. With more,
      "*n* still working" opens the earliest started of them, with the others beside it in
      the same strip or list, and its accessible name says so ("2 still working, opens the
      first").
  - **Earlier (D4):** until history loads (below, #292), the events older than the oldest message
    shown fold into one line at the top of the conversation, "Earlier · 4 delegates started, 3
    reported back ›". It expands to their lines, in time order, which don't fold again.
  - **Time and access:** the age is relative; the tooltip and the accessible name give the
    absolute time in the viewer's local time ("Started 12:41 PM, reported back 12:55 PM"). The
    zone is added only where a time could be misread, such as a machine in another zone ("12:41
    PM PST"). Repository records stay in Pacific; this is the product's display. A fold is a
    button opened and closed with Enter or Space, with the "1 still working" link beside it,
    not in it (above). The button's accessible name is the whole summary, in the order shown
    ("5 delegates · 4 reported back, 1 still working"), as §9.1 A1 asks. Whether it is open is
    exposed apart from the name, by `aria-expanded` (Windows: the ExpandCollapse pattern), and
    is never in the name.
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
      another (§6.5). While its machine is away it stays, disabled, as §6.1's controls do; once
      that machine is offline, the card says "*machine* is offline · you can answer once it's
      back" in text2 in place of its actions, and focus on an action it replaces moves to that
      line (§6.1, #950 rule 8). Why
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
      and check. On the normal path the card's status line stays empty; it speaks only for an
      exception (Still sending…, a stall, Waiting for *machine*, or an outcome other than
      Delivered), never says Up next, since a question holds the session, and never says
      "answered" before the host confirms. Confirmed, Send answer shows the check for about 1 s
      and the card resolves to "You answered · *value*" (§6.5), with no badge; a Needs you
      entry, where a client still draws one, says the same. Not confirmed says "Sent, but not
      confirmed. Check the conversation.": the answer is final and is never resent, and
      nothing offers a second one. It points at the conversation, never at the machine (#696
      rule 5: no remote desktop); #467's remote check later confirms it in place. Decided, not yet built (#600; builds in #616, #625). Today both clients show Sending…, Sending again… and "Delivered. The session has your answer." If focus is on Send answer when it changes: when Try again takes its place,
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
  - **Permissions asked together come one at a time** (#690): Copilot asks one for each of a
    batch of parallel tool calls. The card shows the earliest, with "1 of *N* permissions
    waiting" under its header (`question.waiting`), on every surface: the PWA, the Windows app
    and Home Assistant's card. While others wait it is answered at the machine
    (`answerAtMachine`: no controls, Deny included) whenever the host cannot tell it from them
    on the session's screen: Copilot draws another ahead of it (a path request asked before a
    shell one); its kind is not shell or path, whose dialogs the host knows; its own text has a
    run of spaces that can wrap onto a blank row; any waiting request's text, its own included
    (an argument's name too),
    holds a hidden character, a right-to-left letter, a reference to one, a line-drawing
    character or a tab; another waiting request has nothing drawn to check the screen against;
    or another would be drawn the same. So an escaped approval among others is answered at the
    machine until it waits alone, unless its only escapes are in a path request's access kind,
    which its dialog never draws as written (v1-sessions.md, "Escaped approvals"). That can clear under the
    same id as the others are answered. The next is a new question with its own id once this one
    is answered, so a second tap or a stale button never answers it. A change only to the waiting
    count refreshes the queue label, not the question's meaning: an answer already on its way
    keeps its delivery state, and a failed answer keeps Try again for that original request.
  - **An approval is drawn as it runs,** since Approve once allows all of it: exactly as
    written, in the code face, never read as Markdown, with its hidden characters,
    right-to-left letters and bare carriage returns escaped and the code notes beside it, and
    no Copy ("An approval's card", below, #220). It wraps and is never cut, so a long command or a
    diff keeps Approve once. One the host escaped anything in is approved only through the
    confirmation sheet ("Escaped approvals", below, #803), which replaced the one-tap Approve
    once of the owner's "escape and allow" (#253); while other Copilot permissions wait it is
    almost always answered at the machine instead (#690, above).
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
    In PWA and Windows cards, the host's `answerAtMachineReason: "shape"` adds a second line,
    exactly "This kind of question can only be answered at the machine.", in Needs you's text
    colour, after "Answer at the machine · *machine*". Both lines are accessible text, not a live
    region. No reason, or an unknown one, keeps today's single line; no client guesses the reason.
    This does not change the row, attention or HA text.
  - **Resolved:** the card is one line (#1281): "✓ You answered · *value*" or "Question closed", with a
    "Show question" button (`aria-expanded`) that opens the record below in place; "Hide question"
    closes it. The line has no controls and no default chip, and for an approval never carries
    the approval's text. A "Question answered" or "Question closed" is announced once as it collapses.
    Focus stays on the button, and moves to the line when the card collapses. The record: the card quietens, with no Needs you fill, and reads "Main agent asked"
    (or "Delegate *name* asked") over the question as it was shown. Below that:
    - with this device's answer record for the question, the record's progress and outcome as
      the card shows them: "You answered · *value*" once confirmed; "Sent, but not confirmed.
            Check the conversation." for `unconfirmed`; refused, failed and stale as they are;
    - with no record (answered elsewhere, or the call ended without an answer), "No longer
      waiting", since the protocol doesn't say what was answered elsewhere.
  - **History:** a resolved question stays as a read-only item where it was asked, across later
    turns: the latest 20 per session, for as long as the client runs, none across a restart.
- **Now:** the tool the main agent is running, as a meta line under its reply (glyph, then the
  tool's name and arguments in mono). Not while the session waits on a question: the card says
  what is happening.
- **History** (#292). *Proposed: decided by the owner on 2026-11-04, each as recommended
  ([proposals/661-delegate-events.md](proposals/661-delegate-events.md) §3), not yet built
  except as follows: the PWA's card draws the turns before the latest exchange, read-only, above
  it, and opens at the latest (#1285, first part); the start line is still to come in the PWA.*
  The Windows app now draws the stored turns above the latest exchange, oldest first, read-only,
  from the session history client (#292 W2a): the response text of each turn with its prompt, at
  most 500 turns, with the H5 lines below. It opens at the latest exchange once, when the turns are
  first drawn, and never scrolls to the bottom by itself afterwards. Reasoning, tool lines,
  delegate events, questions and loading older turns on scroll are still to come. With history, a
  session's whole conversation is the one
  column, oldest at the top, in the app and the PWA alike.
  - **H0 A turn** is one prompt and everything until the next: the prompt (the person's, or a
    scheduled or agent-sent one, marked as A prompt you didn't type says, above), the main
    agent's response, reasoning and tool lines, the delegate events between them, and the
    questions and approvals asked in it, each with its outcome. Pages, counts and the limit are
    in turns.
  - **H1 Scroll-back:** a session opens at its latest message. Scrolling up reads back through
    the same column; there is no separate history view.
  - **H2 Loading earlier:** the next page loads on its own as the reader comes within a screen of
    the top, and a "Load earlier messages" button at the top does the same for a keyboard, a
    screen reader or a retry. The view keeps the first item on screen where it is, so nothing
    being read jumps as a page lands above it. While a page loads, the top reads "Loading
    earlier messages…". A page is about 20 turns.
  - **H3 Jump to latest:** more than a screen up, a "Jump to latest" pill sits above the reply
    box. A message that arrives meanwhile is added below without moving the view, and the pill
    counts it: "2 new · Jump to latest". The pill fades in and out as Arriving and leaving
    (§5.3), and nothing pops (#659). Choosing it scrolls smoothly to the end, as Scroll to a
    question does (§5.3). At the end, the view follows new messages; sending a message always goes to the end.
  - **H3a A question while reading back:** a question or approval that arrives while the person
    reads history is added at the end like any message, and the pill reads "Question · Jump to
    latest" in Needs you's text colour. Nothing scrolls on its own (Scroll to a question never
    scrolls a conversation scrolled back through, §5.3), so no answer control moves under the
    pointer. Its card's answer guard (500 ms, §5.3) starts as §5.3 says: as the card comes on screen,
    or, after Jump to latest, where the smooth scroll comes to rest. It arms again whenever its
    controls move in place (#491).
  - **H4 What history holds:** everything stays where it happened.
    - Delegate events follow the rules above. The Earlier line shrinks as pages load, its events
      taking their places between messages, and goes once the start is reached.
    - A resolved question stays where it was asked, read-only, as far back as history goes, in
      place of the latest 20 kept now (Questions, above).
    - An approval stays as its resolved card, drawn exactly as written and escaped (An
      approval's card, below), and never offers Approve once again.
    - The Now line shows only under the latest reply.
  - **H5 States,** each one meta line:
    - empty: "No messages yet", centred;
    - the first page loading: "Loading conversation…", centred, with the reply box usable;
    - an earlier page failed: "Couldn't load earlier messages" and Try again, at the top, with
      what is shown kept;
    - a host that sends no history: "Earlier messages aren't available from this machine",
      above the oldest message shown;
    - the start reached: "Start of session · Mon, Nov 9, 8:31 AM", in local time as above;
    - a turn the host couldn't read: "A turn here couldn't be read", in its place, the turns
      around it kept;
    - a turn cut short (over the host's cap): its text as sent, ending "Cut short · see the
      machine".
  - **H6 Limit:** a client keeps about 500 turns of a session, draws only those near the screen,
    and lets the far end go as more load; scrolling back loads them again. A host keeps what its
    transcript holds, and past that the top reads "Start of available history".
  - **H7 Search:** none within a session until #289, which searches every turn of every
    session. A search of the loaded turns alone would miss the rest and read as complete.
  - **H8 What the design needs from the protocol** (the wire format is #292's): turns with
    stable ids, in order, each with its time; a page of turns before a given turn (a
    before-cursor) and whether older ones remain; a flag on a turn cut short or unreadable; and
    live turns carrying the same ids, so a turn loaded twice is drawn once.
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
- **Escaped approvals** (#803; copy and layout by the creative director, the token signed off by
  the owner): an approval with `escaped` (v1-sessions.md, "Escaped approvals").
  - **The card** (PWA and app): Approve becomes **"Approve once…"**; the ellipsis says it opens
    the sheet. Deny stays one tap. The notes stay above the buttons. Without
    `sessions.approveEscaped` there is no disabled button: Approve is not shown (one verb per
    state), and its place says why. Not an approved device: "Approve this at *machine*, or from a
    device approved there.", with **How to approve a device** opening the help text. A machine
    whose `approve` is `machine`: "*machine* takes this approval only at the machine." One whose
    `approve` is `tooMany` (more escapes than a question marks): "Too many hidden characters to
    show here. Approve this at *machine*." Deny stays.
  - **The confirm sheet** (full screen on a phone, a dialog on a wide PWA as tall as what it holds
    up to 90% of the window, a ContentDialog in the app). Title "Approve a command with hidden characters?"; line 2 "*session title* on
    *machine*". The warning, in the warning role, each sentence only where it applies, then the
    last always: "Boxed codes like `\u000D` are characters that run but aren't drawn." /
    "Boxed codes like `\u202E` and right-to-left letters can reorder what you see." /
    "Approving runs everything below, boxed characters included." Then the command, in full,
    never cut, in the code face at body size, left to right (`unicode-bidi: plaintext`), wrapped,
    in a scrollable read-only region that is focusable and scrolls from the keyboard (Page Down,
    End). **The confirm sits at the end of the command, inside the scrolled content,** so it
    cannot be reached without the end shown and never sits under the opener's tap point; it
    stays disabled for 1 s after it first comes into view, which stops a double tap (held, in forced
    colours, it is the system's disabled cue: `GrayText` text and border, no fill). Its label
    is "I've read it. Approve once", its accessible name "Approve once: I've read the escaped
    command". "Scroll to the end to approve." shows until the end is in view; when the confirm
    becomes available, a polite announcement says "Approve is now available". **Cancel** is pinned
    in the header (the app's Close button): the default action, the Escape action, and where
    focus starts. Back or Cancel sends nothing. The sheet traps focus; under reduced motion it
    appears without travel. The command is signed when the confirm is pressed, never on opening,
    and lasts five minutes from that press, however late the machine's link returns. Pressed
    while the machine is out of reach, the sheet stays open saying "Not sent" directly under the
    confirm, which is the Try again. One that could not be sent within its five minutes is never sent: the card says it
    expired and offers **Approve once…** again.
  - **Escape tokens** (owner sign-off, outline only): each host mark (`marks`) is its `\uXXXX`
    text in the code face and size with a 1 px outline in the Danger role (`Highlight` in forced
    colours), a 2 px radius, 2 px side padding and no fill. Text the agent typed itself, `\u202E`
    included, is never boxed. A token's accessible name is "hidden character, *Unicode name*"
    ("right-to-left override"), or "right-to-left letter, …". The warning's own codes are boxed
    the same way. Contrast: AA for text, 3:1 for the outline, in light, dark and forced colours.
  - **Windows app Settings**, under the folder-trust devices, says who may approve these and why,
    whether folder trust is on or off: the folder-trust devices (only while folder trust is on),
    the devices `config.json` lists by short id, or only at the machine.
  - **Home Assistant:** Deny only, under the line "Approve from the *app* or at the machine.", or
    "Approve at the machine." when `approve` is `machine` or `tooMany`. It has no device key, and its users include agents.
  - **Among other Copilot permissions waiting** (#690): the approval is `answerAtMachine` until
    it waits alone (unless its only escapes are in a path request's access kind), and that wins
    over everything above on every surface: no "Approve once…", no sheet, and no Deny, Home
    Assistant's included.
  - **Needs you, toasts, pushes:** no special case. Never an inline Approve (#202); a tap opens
    the card.
  - The Mac, iPhone and iPad app does not have the sheet yet: its Approve on one is refused by the
    host with the reason, and Deny goes (a known difference, #832).
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
  window and announcements (the owner's #600 decision, below). As §6.4's "While an answer
  goes" says, the ring is on Send answer, and the chosen value stays selected and read-only.
  No client draws a ring on an option.
- **Phone: one line at rest (#935, the owner, 2026-11-05).** Below 760 the box starts at one
  line, 52 tall, with Attach before the text and Send after it, as the native composers do. It
  grows a line at a time to six lines of text, then scrolls inside.
- **While a question waits** (§5.3), one line above the box in `needs` text: "Answer its
  question first. Go to the question". The link is a scroll the app makes to the question
  (§6.2: it leaves the header as it was) and puts focus on its first choice. With the notice,
  the reply area is 99 at rest on a phone.
- **While its machine is offline** (§6.1, #950 rule 8), the box says why in text2, in place of
  its usual actions: "*machine* is offline · you can answer once it's back". Focus on a control it
  replaces moves to the line (§6.1, rule 8). During a return's hold, its controls are held as
  §6.1's rule 2 says.
- **Platform styling:**
  - Windows and the PWA: a card with a 2 px Bridge bottom edge.
  - macOS: a capsule field.
  - iOS: a capsule field over the keyboard.
  - Android: a filled field on `surfaceContainer`.
- **Phone: compact while reading (#935).** Below 760 the reply area floats over the
  conversation's foot, and mirrors the header (§6.2). A session opens with it in full.
  - **Near the end,** it rides down with the scroll, and past its room it is compact: a
    pinned 48 bar (plus the safe area) holding a one-line copy of the box, the draft's first
    line or the placeholder. The copy is drawn, not read, and is not a tab stop; a tap on it
    brings the box back in full and focuses it. The box slid away behind the bar is clipped at the
    conversation's foot, so on an installed iPhone none of it shows in the home indicator's band
    below the bar (#1078).
  - **Away from the end,** 32 up sends it away and 64 down brings it back, with its own
    count. The end always shows it in full. A draft is never hidden: the bar shows its first
    line. Like the header, it never expands over the focused control: while that control
    would be under it, it waits, still counting, until the control would stay in view.
  - **Focus anywhere in the reply area** brings it back at once and holds it. A scroll the
    app makes keeps it as it was, unless it lands at the end, where it shows in full. On a
    screen shorter than twice the header it folds with the header.
  - **Reduced motion:** a cross-fade, as the header's. From 760 it never collapses.
- **The receipt** (#600; decided by the owner 2026-11-04 and revised the same day to "quiet
  unless something's wrong", #617; the boards are the Proposal #600 set, §10) says where a
  reply or an answer has got only when something needs saying. This section is the spec; the
  proposal page ([proposals/600-reply-progress.md](proposals/600-reply-progress.md)) keeps the
  reasoning. **Decided, not yet built (#600; builds in #616, #625).** Today both clients show Sending…, Sending again… and "Delivered. The session has your reply." for a reply, and "Delivered. The session has your answer." for a question card's answer.
  - **The normal path is quiet.** While a reply or an answer is on its way, Send's progress
    ring (an answer's: Send answer's) is the only thing that shows it; nothing appears under
    the box or on the card's status line, whatever stage the host reports. When it is
    delivered, Send cross-fades from the ring to the `check` icon, holds it about 1 s
    (`sendCheckHold`), and cross-fades back to rest. Send answer does the same, and
    the card then resolves to "You answered · *value*" (below). There is no badge and no
    "Delivered" line.
  - **The line is for exceptions only.** A line under the box, and on a question card its
    status line, appears only in the states marked *line* below, and fades in place
    (`acknowledge`); it goes when the next stage or the outcome makes it untrue, fading out
    in place. It is words only, with no ring or badge: Send's ring already says the request
    is open.
  - **Its room is reserved.** From Send the line keeps the room the longest outcome needs
    ("Sent, but not confirmed. Check the conversation before sending it again."), two lines at
    phone width (a narrow pane included) and one on a wide window, empty unless it has
    something to say, so neither an exception nor the outcome moves the box or the card's
    controls. The room stays until the box is next typed in or the session is left, so the
    check and its return to rest move nothing either. On a question card the line is there,
    empty, from Send answer. A refusal whose reason is longer still may grow, as today's does.
  - **Timing.** Nothing is shown or spoken for the first 400 ms after Send. A spoken stage
    is the furthest reached, so a backlog is never played, at most one a second, and an
    outcome is spoken at once, skipping any stage not yet spoken.
  - **The states,** for a reply. An answer has the same rows, but never Up next (a question
    holds the session), ends in "You answered" (below), and has "Answer" for "Reply" in what
    is spoken.     An answer that is not confirmed says "Sent, but not confirmed. Check the
        conversation.": it is final and never resent, so nothing invites a second answer. For a
        reply or an answer, the sentence points at the conversation, never at the machine (#696
        rule 5: no remote desktop); #467's remote check later confirms it in place:

    | State | When | Seen | Colour | Spoken (polite, at most one a second; a failure assertive, §9.1 A6) |
    |---|---|---|---|---|
    | Quiet | 0–400 ms after Send | Send's ring | – | nothing; Send is busy |
    | Uploading | attachments, before the request | *line*: today's ("Uploading 2 attachments… 40%") | text2 | as today |
    | Sending | from 400 ms until the machine has it | Send's ring | – | nothing; Send's busy state says it once (A6) |
    | Sent | progress `received` | Send's ring | – | "Reply sent" |
    | Passed to the agent | progress `typed` | Send's ring | – | "Reply passed to the agent" |
    | Up next | `typed` with `working: true` (replies only) | Send's ring | – | "Reply up next, after the agent's current step" |
    | Delivered | result `delivered` | Send's check for about 1 s, then rest (an answer: the card resolves to You answered · *value*) | the check in Send's own ink | "Reply delivered"; "Answer delivered. You answered *value*" |
    | Still sending | nothing from the machine 3 s after Send; any other wait while connected; a retry | *line*: Still sending… | text2 | "Reply still sending" |
    | Sent, stalled | `received`, then nothing for 5 s | *line*: Sent · still waiting on *machine* | text2 | "Reply sent, still waiting on *machine*" |
    | Passed, stalled | `typed` on an idle session, then nothing for 10 s | *line*: Passed to the agent · not picked up yet | text2 | "Reply passed to the agent, not picked up yet" |
    | Still being delivered | a resend met the first copy in flight | *line*: the stage reached · still being delivered | text2 | "Reply still being delivered" |
    | Waiting for the machine | held on this device while the machine isn't connected | *line*: Waiting for *machine* · it goes when *machine* is back; Send's ring stands still | text2 | "Reply waiting for *machine*" |
    | Waiting for discovery (#1064) | a resume the host holds back while its discovery is held (`busy`, "Held: …") | *line*: *machine* is still finding its sessions. Try Launch again in a moment. (two sentences, no "·"; words only: nothing is resent on its own, Launch stays enabled, and the line is announced politely once) | text2 | the line's words |
    | Not confirmed | result `unconfirmed` (host: a reply 60 s, an answer 15 s), or the client's 9 min | *line*: Sent, but not confirmed. Check the conversation before sending it again. (an answer: Sent, but not confirmed. Check the conversation.) | Needs you's text colour | the sentence |
    | Busy, stale | result `busy` or `stale` | *line*: today's ("Not sent: …") | Needs you's text colour | the words |
    | Refused, failed | result `refused`; the request failed | *line*: today's ("The machine refused this reply: …", "Couldn't send it: …") | Danger | the words |

    An in-flight line's secondary words (after "·") are text2 too; an outcome's guidance or
    reason keeps its outcome's colour. "Up next" has no stall: the agent is busy, and that is
    expected. A stall's line goes when the next stage or the outcome arrives; resends are
    unchanged, and today's "Sending again…" is retired. With an outcome other than Delivered
    Send returns to rest at once, with no check. The receipt never drives the header: the
    header keeps its own state from presence and the link (§2.2), so "Offline" once the
    machine has left, or "Reconnecting…" while its channel comes back, and a question's row
    says Waiting on you until the host clears it.
  - **Send's check** is the `check` icon (`design/icons/check.svg`; `icon-check` in the
    PWA sprite, `AbIconCheck` on Windows), stroked like Send's own arrow and drawn at its
    size in its place, in Send's ink on its Bridge fill. Windows draws it as a Path with the
    same geometry, never Segoe's CheckMark. Send stays the same size and colour throughout:
    only its glyph cross-fades (§5.3). In forced colours it takes Send's own system colours,
    as the arrow does (the glyph is drawn in the button's current colour; §9.1 A9).
  - **You answered · *value*:** a confirmed answer's words on the resolved card, the same on
    the card and in a Needs you entry, in text2 with no badge. The value is:
    - a choice: the label of the option chosen;
    - free text: its first line, with runs of white space as one space, cut at the last
      word break within 40 characters (or at 40, with no break after the 20th) and ended
      with "…" when longer. It is drawn as plain text, isolated in its own direction;
    - a form of several fields: nothing, so the line reads "You answered".

    A screen reader hears "Answer delivered. You answered *value*", with the same value.
  - **Older hosts** (no `sessions.progress`) report no stage, so nothing says Sent, Passed to
    the agent or Up next for them, not even to a screen reader: the client never invents a
    stage nobody reported. Everything else is the same: Send's ring, Still sending… at 3 s,
    the waiting words, and Send's check at Delivered (or You answered).
  - **Timings** from the host (#598) are never shown; they stay in the client's local
    marks and logs.
  - The quiet window, the one-a-second announcement limit, `sendCheckHold` and the stall
    thresholds are named constants in one place per platform.

### 6.6 Machines

A page of its own on every client (the PWA's `#/machines`, the app's Machines). One row per
machine, online ones first, then the account's offline machines (#213):

- **Line 1:** the machine icon, the name, and the trailing state ("Viewing", "Online",
  "Offline"). The icon and the state align with line 1, as a session row's glyph does (§5.1).
- **Line 2:** at most two facts: how many sessions, and the version (with "(dev)" for a build
  from a working copy). A read-only machine says "View only", and a machine whose discovery is
  held says "Discovery held", in the version's place; one that is both says "Discovery held". It
  says so only once a hold has lasted two minutes, counted for each hold from when the client
  first saw it: a shorter one (a routine start or end of discovery) changes nothing on the row,
  one that clears takes the words away at once, and one that comes back counts from its own
  start (#1064). The host's reason
  is never on the row: it is the line's tooltip and its accessible description, both reasons
  where there are two. No device id on the row.
- **Inside the row, under its two lines, in its text column:** the machine's updates (what its
  app found, how the last update ended, Check and Install) and, for an offline machine, Forget.
  Its update line shows a "Needs you" line from the host (#800) in any phase, in the host's words.
  While the six-hour backstop skips for a question waiting (below), it reads exactly
  "Update ready · waiting for a question to be answered", never "answer at the machine".
  Every action is the same small secondary button. Check for updates, for every machine at once,
  sits under the rows in the same column.
- **Update now** (#1159; built in the PWA, slice 3; the Windows app's is slice 4; Creative's rules, 6711605555, are spelled out in
  [v1-update.md](../protocol/v1-update.md#what-the-person-sees)): beside Install, only when an
  update is ready and this device can carry it out; otherwise the row says why in text2. A quiet
  machine updates on one press; a busy one, or one the bridge can't read, confirms first, listing
  each busy session by name with "Mid-turn", "Question waiting" or "Approval waiting", then
  "They pause while the bridge restarts, usually under a minute.", with **Update now** and
  **Cancel**, focus on Cancel (as #1142). One that turns busy during the download waits: "Update
  ready · waiting for the current turn to finish" (Product, #1161), for at most 15 minutes from
  the press. The restart
  reads "Restarting to update…", never "Updating…" (#950); the result reads "Updated to *v*" or
  "Not updated: *reason*", the reason fixed by its code, never the machine's text (#826).
- **The machine's own window while it updates** (#1167; [app-windows.md](../app-windows.md)):
  not a row on another device, so "Updating" is right here. With the window open as the install
  begins, its content becomes one centred state, with no buttons: an indeterminate progress ring,
  "Updating to version *v*" (a heading that takes keyboard focus, so it is read) and "*Product* closes
  for a moment and opens again by itself. Your agents keep running.", on the page's layer, the
  window colour in high contrast. Nothing under it is in reach: the rail and pages are out of the
  tab order and the automation tree, and search is disabled; if the install doesn't go ahead,
  they come back and focus returns where it was. Velopack's own window never shows (the apply is silent). The new
  version opens the window again and shows "Updated to version *v*." as a closable success bar
  with **What's new**; with the window closed, the toast as before. A start still on the old
  version after the installer started shows the update bar as a warning, "Couldn't install
  version *v*. *Product* is still on *old*.", with **Try again**, and a toast too when the window
  was closed: never silent, never an error's own text. Where the installer never started, nothing
  was attempted, and nothing is said.
- **When an update is ready** (owner decision on #689, 6674007815; Product, 6674016765; Creative,
  6674096426): a per-machine setting, a two-choice picker, never a toggle. Each choice carries its
  note.
  - **Restart to update**, *For a machine you use*: the default, for a new install and any machine
    not set. A release downloads in the background and waits for the person's Restart to update
    (#516); after six hours it says "Needs you", at most daily.
  - **Install between turns**, *For a machine agents use*: the owner sets it on dev boxes and VMs
    until #384's machine role exists. The update installs by itself at the first moment no session
    on the machine has a turn running or a question waiting, once its sessions are read (a few
    seconds), with no two-minute wait. The six-hour backstop installs on a machine that never goes
    quiet, over a turn running, but an update never strands a waiting question: while one waits, or
    whether one does can't be told, the backstop skips, and the machine says so ("Update ready ·
    waiting for a question to be answered", and "Needs you" in its log, at most daily).
  - Set in the Windows app's Settings for now. On the phone it is a row in that machine's Settings,
    the current choice in text2; tapping it opens the same two choices with their notes (#801).
- **Who can sign Copilot in (#972 slice 2; Creative, 6700914747):** inside the row, under its two
  lines, in its text column like its updates, and only when the machine has "Let my devices sign
  Copilot in on this machine" on (it offers `signin.devices`). Lines 1 and 2 stay as they are.
  Read-only: no Add, no Remove and no disabled button, since the setting is the Windows app's.
  - One line: "Can sign Copilot in: iPhone, Work laptop", with "(this device)" after this device's
    name. Over three devices it reads "iPhone, Work laptop and 2 more"; every name is in the line's
    tooltip and its accessible text.
  - Under it, one line: "Removing a single device comes later."
  - Empty: "No devices can sign Copilot in here yet.", then "Adding a device comes in a later
    update."
  - Unreadable: "Can't read the device list on this machine." alone. Sign-in is refused while the
    list can't be read; nothing implies a device is allowed when it isn't known.
  - If Trust rules that names don't go over the relay, the line reads "2 devices can sign Copilot
    in" instead, in the same place. Trust accepted the lists with names (6700902097).
- **A machine away (§6.1):** listed once, as offline ("Offline", and "Last seen 2m ago" on line
  2), with Forget, both while its rows are kept and after they have gone. A client whose relay
  keeps no roster lists it so only while its rows are kept.
- **A machine whose bridge isn't running (§6.14, proposed):** still "Offline" on line 1, and
  line 2 says "Bridge isn't running · since *time*" in place of "Last seen…".
- **Diagnose (#802, v1-diagnostics.md):** in another connected machine's row, after its updates,
  a quiet small secondary **Diagnose** when its host offers it. A host that does not offer it
  shows, in that place, the passive line "Can't be diagnosed from this device · update Slipgates
  on <machine>", never a button that cannot act. This machine's own row has neither. The
  states, the same on both clients except where said:
  - **Asking:** Diagnose does nothing more until the answer (the PWA's reads "Diagnosing…"; the
    app's is disabled).
  - **Review:** the snapshot as plain monospace text, headed with the product, the machine and the
    Pacific time. The PWA wraps it within the row, so a 400 px phone never scrolls sideways; the
    app keeps lines whole in a read-only box that scrolls within 360 px. Under it the box "I have
    read this and it is fine to share", and **Copy**, disabled until the box is ticked, beside
    **Close** (the PWA's Diagnose reads "Diagnose again"). Close is the default: Escape anywhere
    in the report, on its Copy and Close too, closes it, and Close and Escape both put focus back
    on Diagnose.
  - **Refused or unreadable:** one line in the row's error style, in the person's words
    (v1-diagnostics.md "In the clients"), never the host's text; Diagnose stays pressable.

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
  line, the host-built label first ("[Slipgates · from an agent on *link name* · not typed by
  your user · external: @*login*]", §6.4), then the words with the delivery escapes already
  applied (a copied marker typed as "(Slipgates", a token-initial @ typed as ＠). The label
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

### 6.8 Readiness (#384)

One model of whether a machine is ready, behind Settings › Overview, each agent's page, first
run's steps and the PWA's read-only view alike. Every check is one row: the glyph, its name, a
reason with its last observation's time, the state's words, and at most two fixes. The states,
drawn on the [states board](proposals/384-first-run/proposal-384-states.png):

| State | Glyph (§2.1) | Words | For Finish |
|---|---|---|---|
| Not selected | `ended`, Neutral | text3 | Never counted, and never success |
| Missing | `idle`, Neutral | text2 | Not passed |
| Needs you | `needs` | Needs you's text colour | Not passed |
| Working | `done`, Neutral | text2 | Passed, with its evidence |
| Blocked | `failed`, Danger | Danger's text colour; names what blocks it ("Blocked by hook trust") | Not passed |
| Failed | `failed`, Danger | Danger's text colour | Not passed |
| Checking (while it runs) | `working`, Active | text2 | Not yet |

**What ships today:** the host reports six states (`ReadinessState`: Unknown, Missing,
NeedsAction, Working, Blocked, Failed), and Settings shows them as plain text. Not selected,
Checking and the glyphs are **Not built yet (#384 slice 1)**, and Finish (§6.10) is slice 5's.

- **No success colour** (#384 decision 1). Working is the neutral ring with a check; colour is
  spent only on Needs you, Blocked and Failed. Blocked and Failed share a shape on purpose
  (decision 2): neither is the person's to answer in that row, and the words say which.
- **Working needs evidence.** A written file, a running process, a heartbeat older than its
  interval or a hook from another session is not Working. Installed is not signed in, signed
  in is not entitled, registered is not trusted, and trusted is not received.
- **Not selected is never success,** and its dotted ring and text3 keep a list of them from
  reading as Working, in colour or in greyscale.
- **The checks,** each its own row: an agent's executable (version and home), sign-in, access
  (entitlement), ownership, registration, activation and trust, its test session (launched
  through the bridge's own launch path, seen, and its test message received: "Session seen"),
  and the last hook from that session; the host's heartbeat; launch authorization;
  connectivity; and, where enabled and supported, the reply and approval route and each
  selected device's path. The words for each state of each are in the proposal's
  [state matrix](proposals/384-first-run.md#51-the-state-matrix).
- **Required for Finish (§6.10):** the host's heartbeat and launch authorization; for each
  *selected* agent, each of its checks, except that an agent whose version is unknown (§6.10
  step 2) may finish as Not verified; connectivity, unless the person chose local only; and
  the reply and approval route and selected device paths, where enabled and supported. A phone
  not yet paired reads Not paired and may be paired later; it never reads as a device path
  that passed.
  "Last hook" is Working only for a hook from the session started for it, after its last change.
- **Agency** is an agent of its own that shares Copilot CLI's hooks and sign-in, and its rows say
  so ("Uses Copilot CLI's sign-in", "Reports through Copilot CLI's hooks"; decision 13).
- **A change applies to new sessions.** Editing a folder, the permission mode or a hook runs the
  checks it affects again, and sessions started before it read "New session needed · 1 session
  started before this change", with Show sessions. Nothing is stopped for them.
- **Home Assistant** has no check, step or editor here. Discover reports what legacy coexistence
  and the migration guards find, and leaves them where they are.

### 6.9 Settings (#384)

**What ships today:** Settings is one scrolling page (`SettingsView`): the host, this
machine's name, Relay and sign-in, "Agents on this machine" (each agent's row, below), "Your
devices" with the two answer toggles of Workspaces and trust, this app's toggles, and the
Host log. The host section's "Restore sessions after a reboot or crash" switch (§6.12) is
**Not built yet (#780)**. The sectioned pane below and its table are **Not built yet (#384
slice 5)**. The card for an agent installed later (#231, slice 1) ships on that page, at its top,
with its grey dot on the rail's Settings item, "Settings, new agent found", until the Agents item
exists. It is offered only for Copilot CLI, Claude Code and Codex whose version was read: an
agent whose version couldn't be read has no card, and its row keeps its Set up. Removing an
agent's connection also holds its card at that version. The PWA's Settings also has Text size
(§4.4), on this device only, and **Keyboard check** (#1209): while on, each key pressed anywhere
in the app is listed (key, code, Option, Control, Command, Shift, what had focus, never its words,
and whether the app used it), so a hardware keyboard can be read back on a real device; nothing is
saved or sent.

**An agent's row today** (the creative director's #784 words; #705's per-row names and
verbs). Every state is words on the row, with at most one verb:
- **Not installed:** "Not installed", with **Install CLI** (named "Install CLI for *Agent*")
  where the app can install it. Where it can't (Agency), there is no verb, and the line says
  how: "Not installed · install it on this machine".
- **Installing:** "Installing…". Install CLI stays pressed with its ring (A4), and **Cancel**
  sits beside it, named "Cancel CLI installation".
- **Afterwards** the row goes straight to the state the check finds. A failed install reads
  "Install failed · *reason*", with Install CLI offered again. The reason is from a
  categorised set, never the installer's own text, and the row shows no output lines and no
  Copy details.
- **Set up, not yet tested:** an agent whose hooks are set up but that no new session has
  tested yet reads "Not tested yet" (#705). Once the host has handled one of its hooks (Agency: the shared Copilot CLI hooks), it reads "Ready" (#1221); a row with failed hooks, an unknown setup or an older host keeps its own words instead.
- **A host the app can't act on:** "Read-only · *reason*" when the host reports `blocked`
  (for example "Read-only · this machine's bridge is older": a person never sees "host", #959,
    #1022);  "Hook ownership couldn't be checked · *reason*" (or "Hook intake couldn't be
  checked · *reason*") when the hooks have
  failed and their `hookSetup` is `unknown`, the reason being one of access denied, file in use
  or couldn't read ("Can't check" alone when the host gives neither); and "Can't check · this
  machine's bridge is older" only when `hookSetup` is `unknown` and the hooks are neither blocked
  nor failed, since a current host sends `unknown` only with those. All three are `text2`, with
  no status colour and no verb, and never fall back to Ready, Not set up or Needs repair.

First run and Settings are the same editors over the same host state: nothing is set up in
first run that Settings can't change, check or repair. On Windows, Settings has its own pane of
sections beside the rail (§7), with Settings selected on the rail:

| Section | What it holds | Board |
|---|---|---|
| **Overview** | Every readiness check, grouped This machine, Agents and Other devices; Check again; Diagnostics; Resume setup while setup is unfinished | [Overview](proposals/384-first-run/proposal-384-settings-overview.png) |
| **Agents** | A tab per agent: Installation (Executable, Home), Account (Sign-in, Access), Hooks (Registration, Hook trust, Last hook, Running sessions); Run test session, Repair hooks, Remove bridge integration | [Agents](proposals/384-first-run/proposal-384-settings-agents.png) |
| **Accounts** | Slipgates' own (the GitHub account, Relay, this machine's name), each agent's sign-in, and Devices | [Accounts](proposals/384-first-run/proposal-384-settings-accounts.png) |
| **Workspaces and trust** | The four groups below. **Parked (#699)**, but for its two shipped toggles | [Workspaces and trust](proposals/384-first-run/proposal-384-settings-workspaces.png) |
| **General** | The host (Restart, Stop, start at sign-in, Restore sessions after a reboot or crash (§6.12), Dev Box keep-awake), this app (start at sign-in, notifications, updates, pre-releases, version), and Troubleshooting (Host log, Set up again) | [General](proposals/384-first-run/proposal-384-settings-general.png) |

Collaboration (§6.7) keeps its own section, after Accounts, whose Devices it follows.

- **Overview is the health list** (decision 5): there is no separate Health page. The nav's
  badge leads to it. An agent's row opens its Agents tab. An agent found but not set up reads
  Not selected with **Set up**: an agent installed later is offered, never opted in.
- **An agent installed later** (#231; decided by the owner on 2026-11-04, option (a)) is
  offered the way Collaboration's discovery is (§6.7): a grey dot on the Agents nav item, and
  a card at the top of Overview, "*Agent* *version* is installed · Set it up to see its
  sessions in Slipgates", with **Set up** and **Not now**. Set up does what the agent's row's Set
  up does; Not now hides the card and the dot for that agent at that version, until
  its version changes. It is never a toast, a push or a count, it is not Needs you, and it is
  never offered on a read-only host (beside agent-ha-bridge, the row offers Review migration).
  Nothing is drawn twice (the 11-05 rule): while the card shows, it holds the agent's only Set
  up, and the agent's Overview row reads Not selected with no verb. Once Not now hides the
  card, the row's Set up returns.
- **Needs you on a section:** an agent's tab carries the Needs you dot while that agent needs
  the person, and the Agents nav item carries the same.
- **Remove bridge integration** is a subtle button, and removes only Slipgates' own
  registrations and blocks. Uninstalling a CLI or its credentials is never on this page.
- **Devices that can sign Copilot in** (#972 slice 2; Creative, 6700914747): under "Let my devices
  sign Copilot in on this machine", and only while that is on in the running host: the switch applies
  at the host's next start, and until then the list shows who still can. Read-only, from the
  machine's device roster (#55); the Settings device-list pattern, no new visual language.
  - Heading: "Devices that can sign Copilot in".
  - One row per device. Line 1 is the name, with "(this device)" after this app's own entry. Line 2
    is the kind and platform: "Phone · iOS", "Browser · Windows", "App · Windows". No device id,
    key, generation or presence code.
  - Under the list, one line: "Removing a single device comes later. To stop them all, turn this
    off." No Remove, no Add and no disabled button.
  - Empty: "No devices can sign Copilot in here yet.", then "Adding a device comes in a later
    update." Until #55's roster ships, every machine reads this.
  - Unreadable: "Can't read the device list on this machine." alone. Sign-in is refused while the
    list can't be read.
- **Dev Box keep-awake** is shown only on a Dev Box, Off by default, with its consequence on
  its row ("Signs in with az login · an awake Dev Box keeps billing").
- **Workspaces and trust** is **Parked (#699)**, except its two answer toggles, which ship
  today on Settings' page: "Let this app and my devices approve Copilot permission prompts",
  On by default, and "Answer folder-trust questions from my devices", Off by default. The
  rest of this item is the parked design, kept for when #699 resumes: four groups, each a
  separate decision with its own default and owner. No control combines them, and nothing
  defaults to Allow all.
  - **Folders Slipgates may start sessions in:** each approved folder, with **Subfolders**
    (Off) and **Remove**, then **Add folder**. None until added, and the exact folder only.
    Subfolders ships only once the host supports subfolder scope (decision 7); until then it
    isn't drawn. A path with a space is quoted by every launch; a system folder is refused.
  - **Each agent's own folder trust:** what each CLI reports. The CLI decides, and Slipgates
    never pre-trusts.
  - **Permission prompts:** New sessions, **Standard prompts** by default.
  - **Answers from my devices:** "Let this app and my devices approve Copilot permission
    prompts", On by default as today, as its own row (decision 6); "Answer folder-trust
    questions from my devices", **Off** by default; and the paired devices, or "No devices
    paired" with **Pair a phone**.
- **Remote clients** (the PWA, and later Apple). **Not built yet (#384 slice 5):** today the
  PWA's `#/settings` holds only Notifications, Demo and Account, and shows no machine's
  Settings. The design: they show a machine's Settings and readiness **read
  only** (decision 10; [phone](proposals/384-first-run/proposal-384-phone.png)): "*machine* ·
  Online · checked 14:52 · read only", then "Change these on *machine* · Settings in the
  Windows app", then Agents, This machine and Trust. A row that needs the person reads "Needs
  you on *machine*". No control is drawn that the device can't use; an action appears remotely
  only where an existing explicit approval path already allows it (#67).

### 6.10 First-run setup (#384)

**Not built yet (#384 slice 5).** Today there is no guided setup: a machine is set up from
Settings' page (§6.9), each agent from its row.

**Interim (slice 1):** until the stepper lands, Settings > Agents shows a plain **Get started** list
above the agent rows (§6.9). It reads the same readiness as the rows, names the one next step, and
adds no host operation. "Sign in to the CLI itself" is always **Yours to check**, because the host
cannot verify it yet (#691). The list hides once every step is Done, Yours to check or Optional. Each
step is read to a screen reader with its state and position first, for example "Next, step 3 of 6,
Connect its hooks". The *Start a new session* step is also Done once the host's own new-session test of an installed agent has passed (slice 3, #715). The stepper below replaces it.

Settings with a guide. Six steps, in a stepper that **replaces Settings' own pane** while setup
runs; the app rail stays, with Settings selected, so Sessions and Machines are one click away
and setup never traps the person (decision 4). The steps, in order, with their boards
([every board](proposals/384-first-run/contact-sheet.png)):

1. **Discover** ([board](proposals/384-first-run/proposal-384-discover.png)), "Read only:
   nothing on this machine changes until you choose". This machine (the host, and "Fresh
   install" or "Earlier setup found"), Agents (each Found with its version and path, or Not
   found; **Scan again**) and Accounts. A machine set up earlier gets an info bar, "Earlier
   setup found · its working settings are kept", with **Review migration** (the existing
   reversible path), what is **Kept**, and what another tool owns, Blocked and **Not
   changed**, with **Details**. Fresh setup never takes over another tool's state.
2. **Agents** ([board](proposals/384-first-run/proposal-384-agents.png)), "Found agents are
   selected. Choose any others to install." A checkbox row per agent: found ones checked,
   missing ones unchecked and Not selected. **An agent's version** (#231; Head of Product's
   decision, 2026-11-04, [#696](https://github.com/danswett/agent-bridge/issues/696#issuecomment-6651872822))
   sets how it's offered:
   - **Tested:** checked.
   - **Newer than tested:** checked, with "newer than tested" after the version.
   - **Unknown:** always listed, never left out, and **unchecked**, reading "Version not
     verified". Checking it shows one line under the row: "We couldn't read *Agent*'s
     version. Setup may not work; we'll check it afterwards." After setup its row reads
     **Working** once evidence arrives (§6.8), or **Not verified** (the `idle` ring, plain
     words, never Working's ring). Not verified doesn't hold Finish; Overview keeps the row
     with **Check again**.
   - **Older than the supported minimum:** no checkbox and no Set up. It reads "Update
     *Agent* to set it up", and Scan again picks up the update.

   The same rules apply to an agent installed later (§6.9) and to its row in Agents.

   Checking a missing agent never installs it: its row
   reads **Install needed** (Needs you) and asks for consent first. **What ships** (from
   Settings' Install CLI, §6.9) is one combined Windows Package Manager dialog, "Install
   *Agent* and its required runtimes?": it names the Windows Package Manager community source
   and its agreements, says required runtimes may ask for Windows administrator approval,
   that existing CLIs aren't upgraded and that nothing else (hooks, sign-in, folder trust, a
   restart) is asked for, and that Agency is installed through the person's organization. Its
   buttons are **Install CLI and runtimes** and **Cancel**, Cancel the default. There is no
   separate runtime step: winget brings the runtimes the CLI needs, and the app refuses to
   install Agency. **Not built yet (#384 slice 3):** the consent card drawn on the boards,
   which names the npm package, the command run, what it needs and where it installs, with
   **Install**, **Not now** and **Open the official page**; and **Install Node.js** for a
   missing runtime through the same card (decision 12).
   - **Installing:** the row's words as §6.9: "Installing…", Install CLI held pressed with its
     ring, and **Cancel** ("Cancel CLI installation"). No package-manager lines are shown. The
     person may go on ("Installing continues if you go on").
   - **Install failed:** "Install failed · *reason*", the reason from a categorised set, with
     Install CLI offered again; no error lines and no Copy details. Continue stays available;
     a failed install only keeps that agent from Finish ("Codex CLI won't be verified until
     it's installed").
3. **Accounts** ([board](proposals/384-first-run/proposal-384-accounts.png)), Slipgates' own
   account first, since the relay and devices depend on it. Its device code is drawn large in
   the code face with the page's address, **Copy code and open the page** and **Cancel**, and
   says when it expires. Each CLI signs in through its own supported flow. The app never asks
   for a password and never shows a token or credential: only what the CLI reports.
   Installation, sign-in and access are always three rows.
4. **Hooks** ([board](proposals/384-first-run/proposal-384-hooks.png)), "Slipgates adds only
   its own hooks and keeps everything else." Registration, owned instructions, and for Codex,
   **Hook trust** ("Codex asks you to trust a new hook. Slipgates never answers this.", Needs
   you, **Open Codex**) and the hook command, quoted and run once before it is written: a
   command known not to run is never installed. Sessions started earlier get the **New session
   needed** info bar, with **Show sessions**; they are not stopped.
5. **Workspaces** ([board](proposals/384-first-run/proposal-384-workspaces.png)), "Separate
   decisions, each with its own default. None of them is Allow all.": the four groups of §6.9.
   **Parked (#699)** with them; until it resumes, setup has no Workspaces step beyond the two
   shipped toggles.
6. **Verify** ([running](proposals/384-first-run/proposal-384-verify.png),
   [blocked](proposals/384-first-run/proposal-384-blocked.png),
   [ready](proposals/384-first-run/proposal-384-ready.png)). A real test session per selected
   agent, only once the person allows it. The Test sessions card names the folder, the agents,
   the first message ("Reply with the word ready. Don't change any files."), **Close the test
   sessions afterwards** (checked by default, decision 8), and who allowed it ("By you, in this
   app, at 14:35"). Checks run one at a time, each a row, and the foot reads "Checking 6 of 11".
   - **Blocked:** "Finish waits for 3 checks · Fix them, or deselect what you don't need." The
     checks that aren't passed come first, under Needs you, each with its one fix (a relay that
     timed out offers **Try again** and **Use local only**); the Working ones follow, with
     **Check again**. Deselecting an agent or choosing local only are the ways past it, and
     neither reads as success.
   - **Ready:** "Ready on this machine · Checked 14:41 · 3 test sessions closed". Choosing
     local only puts a neutral **Local only** chip in the header and Relay reads **Off**, with
     no warning colour (decision 9). Nothing skipped reads as Working.

- **The stepper:** each step's row has its readiness glyph (§6.8; `idle` before it starts) and
  one line of state ("3 agents", "Codex hook trust", "Checking 6 of 11"). The current step is
  the selection pill (§2.1), never a status colour. The foot reads "N of 6 steps done".
- **The footer:** **Save and exit**, then **Back** and **Continue**, or **Finish** on Verify,
  with a one-line reason beside them whenever Continue means less than all done ("1 needs you,
  1 blocked, 1 failed"). Finish is enabled only once every required check (§6.8) is Working.
- **Save and exit** is on every step but Ready. It keeps the choices and what is unfinished,
  never a "done" flag: on return every check runs again. The app then opens on Settings ›
  Overview, which reads "Setup isn't finished · *what*" with **Resume setup**, and the pane's
  foot keeps **Resume setup · 5 of 6**. Resume opens the first step that isn't finished. An
  install cut short by a crash, a sign-out or a restart reads **Install interrupted** (Needs
  you) with **Check again**, which finds it installed or offers Try again; a retry never
  registers anything twice.
- **Agents installed later:** setup offers only what it finds while it runs. An agent installed
  after setup finishes is offered in Settings (§6.9's card), never by opening setup again.
- **Narrow windows** (under 760, §5.2): the stepper becomes one line, "Step 2 of 6 · Agents",
  a six-part progress bar and a **Steps** button that opens the list as a flyout
  ([board](proposals/384-first-run/proposal-384-narrow-agents.png)). Settings' pane becomes a
  list page, and each section a pushed page with a "‹ Settings" back row; an agent's tabs
  become the list of agents one level up
  ([board](proposals/384-first-run/proposal-384-narrow-settings-agents.png)). A row keeps its
  actions on its line where they fit, and otherwise puts them under its reason.
- **Motion** is §5.3's, with nothing added: choosing a step is a Tab switch (its pill moves as
  Selection does), the consent card opens as a Disclosure, a narrow window's sections push and
  pop, and the Steps list is a flyout. The only loops are Checking's `working` glyph (§2.1) and
  Install CLI's ring while an install runs, and both are still under reduced motion. There is
  no progress bar and no percentage.

### 6.11 Settings › Agents: one readiness state per agent (#783)

Decided by the owner on 2026-11-04 (D1–D7, each option A). This builds on #705, which adds
per-row names and one verb per state. It refines how Settings (§6.9, #384) draws the agents on
this machine and the host. The check rows of Readiness (§6.8, #384) are unchanged. Until #384's
sections land, this section applies to the Windows app's current Settings page.

Today each agent is about eight lines of equal weight with no mark, and every card carries Set
up / Repair and Remove connection. You can't tell whether anything is needed. Now each agent has
**one state, one mark, one headline and at most one verb**, and everything else is in Details.

**The roll-up state** (D1, D2). Each installed agent's card shows exactly one of these:

| State | Mark (§2.1) | Headline | Verb |
|---|---|---|---|
| Ready | `done` in `ready` | "Ready · last hook 3:01 PM" (or "· test session passed 3:01 PM") | None |
| Not tested yet | `idle`, Neutral | "Set up · not tested yet" | **Run test session** (#716). Until it ships: no button, and a hint. "Start a session in it to confirm" |
| Not set up | `needs` | "Not set up" | **Set up** |
| Needs repair | `failed`, Danger | "Setup didn't finish" | **Repair hooks** |
| Managed elsewhere | None; neutral secondary text, no status colour | "Read-only · *why*" ("another bridge profile owns its hooks", "this machine's bridge is older" or "set in this bridge's settings") | None |
| Couldn't be checked | None; neutral secondary text, no status colour | "Hook ownership couldn't be checked · *why*" ("access denied", "file in use" or "couldn't read"), or "Hook intake couldn't be checked · *why*" | None |
| Can't check | None; neutral secondary text, no status colour | "Can't check · this machine's bridge is older" | None |
| Setting up (while it runs) | `working`, Active | "Setting up…" | The pressed verb (Set up or Repair hooks) stays, busy (A4) |
| Not installed | None | One quiet line in text3: "*Agent* · not installed" (D7) | **Install CLI** where the host can install it, as a subtle button on that line; otherwise "*Agent* · not installed · install it on this machine" and none. Installing, Cancel and "Install failed · *why*" follow [app-windows.md](../app-windows.md) |

- **Ready needs evidence** (D2), the same rule as §6.8's Working. An agent is Ready when either:
  - a hook from that agent was handled by the host's current run after the agent's last Set up
    or Repair; or
  - a test session passed (#716).

  Agency runs on Copilot CLI's hooks, so a handled Copilot CLI hook counts for Agency too (#1221).
- **The other states also need evidence.** A bridge setup record, with this profile's hook
  intake on and nothing heard yet, is Not tested yet, never Needs repair, so a healthy machine
  never shows Repair on its rows. No setup record, or intake off for this profile, is Not set
  up, with Set up, which turns intake on (#705). Four states come first, in this order,
  whatever the record and intake say:
  - hooks the host holds back (`blocked`) are Managed elsewhere;
  - hooks the host reports `failed` with `hookSetup: unknown` are Couldn't be checked;
  - `hookSetup: unknown` beside any other hooks comes only from an older host, and is Can't
    check;
  - an incomplete setup (`hookSetup: incomplete`, registration failed after the record was
    written) is Needs repair, with Repair hooks.

  Repair is offered only for `incomplete`. The host has no signal for hooks changed outside
  Slipgates, so no card claims it. The three neutral states offer no Set up, Repair or Remove,
  and a blocked row hides Install CLI.
- **Sign-in and trust** are never shown as checked until the host can observe them. They stay
  neutral rows in Details and don't affect the roll-up.

**The `ready` colour** (D1; partly amends #384 decision 1, "No success colour"). There is one
green role, `ready`:
- dark #3FB950, with words in #56D364;
- light #1A7F37;
- WindowText in high contrast.

It colours only the roll-up mark of each agent and of the host (Running). It is never used on
a check row, a count, a badge or a notification. It is always beside its word ("Ready",
"Running"), never colour alone. Check rows in Details keep §6.8's neutral Working. The role
joins `tokens.json` with the implementation, measured like the other roles (§3).

**The card.**
- The first line is the mark, the agent's name and the headline, and the verb on the right if
  there is one.
- **Details** (§5.3; D4) is collapsed when the agent is Ready and open otherwise. It holds:
  - the executable's path and version (the version shown once);
  - the agent's home;
  - the neutral check rows (hooks, sign-in, trust, last hook);
  - **Run test session**, when Ready (as a secondary button);
  - at its foot, **Remove bridge integration…** (D5), only when the host reports a setup to
    remove (`hookSetup` `completed` or `incomplete`). It is hidden on Not set up and on the
    neutral states, since the host refuses a removal without a setup.
- **Remove bridge integration…** is a subtle button in Danger's text colour. It asks for
  confirmation, naming the agent and saying what stays (the CLI, its sign-in and its sessions).
  Cancel is the default. "Remove connection" is no longer on the card.
- No verb is drawn disabled: a verb that doesn't apply is hidden. The one exception is A4's
  busy verb: while its request runs, the pressed verb stays in place, focusable, with its
  ring, says it's busy, and ignores presses.
- **When the outcome lands** (here and on the host card below), the busy verb gives way to the
  new state's verb. If focus was on it, focus moves to that verb, or to the card's headline
  when the new state has none (Ready and the neutral states). If focus was elsewhere, it stays
  there. Stop… returns focus from its confirmation to Stop…, which is then the busy verb.

**The host** (D6) is a card spanning the full width above the agents:

| State | Mark | Verbs |
|---|---|---|
| Running | `done` in `ready`, "Running · since 2:58 PM" | **Restart**, **Stop…** |
| Stopped | `idle`, Neutral, "Stopped" | **Start** |
| Starting or stopping | `working`, Active | The pressed verb (Start, Stop… or Restart) stays, busy (A4); the others are hidden |

- **Stop…** asks for confirmation (sessions on this machine stop reporting). Cancel is the default.
- The process id, the version and the profile move into the host card's Details.

**Reflow** (D3). The page fills its window, and isn't a fixed column:

| Window width | Layout |
|---|---|
| ≥ 1280 | Settings' section pane (240) and the page: the host card spanning the full width, then agent cards in a grid of up to three columns, each at least 340 wide (three at 2000, two at 1280) |
| 760–1279 | Section pane (208) and the page: the grid falls to two columns, then one |
| < 760 | The sections as a pivot at the top; one column |

- The grid gap is 16 (§5.1).
- Prose inside a card keeps to about 75 characters a line.
- Cards in a row share a height only up to their first line; an open Details grows its own card.

**Accessibility** (§9.1).
- Each card is a group named "*Agent*, *state*, *headline*" (for example "Copilot CLI, Ready, last
  hook 3:01 PM").
- Each button names its agent ("Set up Claude Code", "Run test session, Agency", "Remove bridge
  integration, Codex").
- Details is a button with `aria-expanded` (UIA ExpandCollapse).
- A state change is a polite per-row announcement ("Claude Code, Ready"), never an alert.
- Every confirmation's default is Cancel.

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
  bridge restarted · 03:12" when it is `crash`. The time is `restored.at` in the client's time
  format, as every time on the row is.
- **Form:** the row's existing secondary text tokens. No new glyph, no colour, no badge. The
  status glyph still says what the session is doing (§2.2), which is idle.
- **Lifetime:** the mark and Continue share one lifetime. Both go when a reply is delivered or
  the session starts working, whichever comes first, and after 24 h or at the end of this host
  run at most (the host stops sending `restored` then). Neither ever comes back: a client never
  keeps the mark longer, and never brings it back once it has gone.

**The machine notice** (`hello.machine.restore`, announced through `hello.changed`, #646)

- **Where and how:** one banner per machine: an informational InfoBar over every page, in the
  window's banner area (§7), after the app banners (Platforms, below, gives each client's
  place). It is
  closable.
- **Content:** a title and the lines below, with no line limit: every failure and its reason is
  shown. A line whose list is empty is left out. The banner grows to fit, but the banner area as
  a whole, every banner included, takes at most a third of the window's height (a quarter on a
  phone); past that, the area scrolls inside itself. Every notice's full text and buttons stay
  reachable by keyboard and screen reader, and the page under the banners is never pushed out of
  view.
  - Title: "*machine* restarted after a reboot at 03:12" (`cause: reboot`) or "*machine*'s bridge
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
  a Restore button with a confirmation, from any client, is #948 (PROPOSED below: *Restore from
  a client*).
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
  repeat it. `unconfirmed` is never retried: the receipt says to check the conversation, as §6.5's
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
- **Text:** title "*machine* restarted" after a reboot, "*machine* · the bridge stopped" after a
  crash ("bridge", never "host", Creative #1022), body "Restored *n* sessions · *m* need you" when there
  are lost questions or failures, else "Restored *n* sessions". When offered only: "*n* sessions
  can be restored at the machine".
- **Sound:** none. Decision #764 keeps sound for pending questions and approvals only, so this
  notice is silent on every platform. The push payload sets `notification.silent: true`, so an
  older service worker that shows kind `restored` as a `question` stays silent too. **Except
  (#970, proposed):** a restore notice that replaces a "bridge isn't running" alert, on that
  alert's tag, alerts as Needs you does, with the question sound, never silently: the person
  still has something to answer (§6.14).
- **Withdrawal:** #780 withdraws no restore push today. If it ever does, it withdraws on
  whichever tag the notice went on, and once withdrawn nothing about a replaced gone alert comes
  back (§6.14).
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
| Restored, idle | Mark "Restored after a reboot · 03:12"; counted in the notice | Mark "Restored after the bridge restarted · 03:12"; counted |
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
| Row mark | "Restored after a reboot · *time*" / "Restored after the bridge restarted · *time*" |
| Notice title | "*machine* restarted after a reboot at *time*" / "*machine*'s bridge restarted at *time*" |
| Signed in | "restored when you signed in at *time*" |
| Restored | "Restored *n* sessions"; "Can continue: *title*, *title*"; "*n* were mid-turn" |
| Offered | "*n* sessions can be restored at the machine" |
| Lost | "Questions lost: *title*, …" |
| Failed | "Couldn't be restored: *title* (*reason*)" |
| Loop | "*n* sessions weren't restored again because they died within 10 minutes" |
| Close | "Dismiss restore notice on *machine*" |
| Button | "Continue" (name "Continue *title*") |
| Push | "*machine* restarted" (reboot) / "*machine* · the bridge stopped" (crash); "Restored *n* sessions · *m* need you" |
| Toggle | "Restore sessions after a reboot or crash" |

**Platforms**

- **Windows app:** InfoBar (Informational) in the window's banner area over every page, after
  the app banners (§7, Windows: Banners); Continue a standard secondary
  Button; the toast through the app's ordinary notification path (§7).
- **PWA:** the same notice as one of §7's app banners, at the top of the content above the page,
  over every page, after the app banners §7 lists. The restore
  notice alone is an exception to §7's "no icon and no dismiss" app banners; every other PWA
  banner keeps §7's rule. The push through `sw.js`, kind `restored`.
- **Apple (#260, owner welshofer):** note only. The same fields; the native form is that track's
  decision.
- **Home Assistant:** read only. `sensor.agent_bridge_<machine>_restore` (state: the last restore
  time; attributes: the notice), a `restored` attribute on each session's status sensor, and one
  persistent notification per restore. No Continue in Home Assistant.

**Restore from a client (#948): PROPOSED, for Creative**

**Not built, and not decided.** Design first: the words are placeholders for Creative, and the
host's side is [restore-run.md](../protocol/restore-run.md) (`restore.run`, capability
`sessions.restore.run`). Nothing here changes the notice above until it is accepted. Folder trust
stays a person's at the machine (#947).

- **When the notice offers it:** the notice names sessions in `offered`; its `why` is `off` or
  `pending` (decided, Product P2 on #1033); the machine's hello offers
  `sessions.restore.run`; and the machine is not read-only. Otherwise the Offered line stays
  "*n* sessions can be restored at the machine", with no button: under G5, on a host or a channel
  that can't act (an older host, a device the host doesn't take as a person's), in Home Assistant,
  and when the notice names none (all in `offeredMore`).
- **The line and the button:** the Offered line reads "*n* sessions weren't restored: restoring
  after a reboot or crash is off on *machine*" (`off`) or "*n* sessions can be restored" (`pending`), and
  after it a secondary button, "Restore…" (named "Restore sessions on *machine*", A1). Never in a
  row, a card, a push or a toast.
- **The confirmation** (a dialog; Platforms below):
  - Title: "Restore *n* sessions on *machine*?"
  - Line: "Each opens idle, where it stopped. Nothing is typed into them."
  - The list: the notice's named sessions in its order, at most 5, one a line, then "and *m*
    more" for the rest it names. Each title is drawn as a title is everywhere (§6.3: cleaned as a
    name an agent picks is, #263, with hidden and bidi characters shown as visible escapes), on one
    line with an ellipsis
    at the dialog's width; one with no title reads "*Agent* session" ("Copilot session"). After it,
    in secondary text, what the notice says of it: "· was mid-turn", "· a question was lost", "·
    stopped again soon after its last restore" (`loop`).
  - With `offeredMore`: "*k* more aren't listed here. Resume them from Launch."
  - Buttons: "Restore *n*" (primary) and "Cancel". Focus starts on **Cancel**, since this acts on
    another machine (restore-run.md C3). Escape and the dialog's close are Cancel.
- **What Restore sends:** `restore.run` with every id the confirmation listed, the notice's `at` as
  `offer`, a fresh `request` and `progress: true`. One press, one request; a lost response is sent
  again with the same `request` after 90 s with no frame; a new press after a `busy` or `refused`
  takes a new one.
- **While it runs:** the Offered line reads "Restoring *i* of *n*…", from the progress frames, and
  the button shows §5.3's ring, disabled and keeping focus (A4). Closing the notice doesn't stop
  it. As each session leaves the offer the host tells every device (`hello.changed`), so another
  device's notice shrinks with it; a press there meanwhile is `busy`: "A restore is under way from
  another device."
- **After** (each line on this device only, until the notice changes or is dismissed):
  - **All restored** (`restored`): the Offered line and the button go, and the notice's "Restored
    *n* sessions" line counts them, with "Can continue: …" for those mid-turn, as at start. The
    rows get their restored mark from the host as at start.
  - **Partly** (`partial`): "Restored *a* of *n*", then a line for each that didn't start:
    "Couldn't be restored: *title* (*reason*)" (`refused`, `failed`), "Not tried: *title*
    (*reason*)" (`busy`) or "Started, not confirmed: *title*. Check at the machine."
    (`unconfirmed`). With any still offered, the button comes back as "Try again…", which confirms
    again with what is left.
  - **None** (`none`): "Couldn't restore any of the *n* sessions", then the same lines.
  - **Refused** (the whole request): "Couldn't restore: *reason*", the host's words. The offer is as
    it was. If the reason is that the machine became read-only, hello takes the capability away
    and the line goes back to "… can be restored at the machine".
  - **Busy** (the whole request): "Couldn't restore now: *reason*. Try again in a moment."
- **Never automatic:** no client presses Restore on start, on reconnect or on a timer.

| State | Offered line | Button |
|---|---|---|
| Can act (`sessions.restore.run`, not read-only) | "*n* sessions weren't restored: …" / "*n* sessions can be restored" | "Restore…" |
| G5, or a client that can't act | "*n* sessions can be restored at the machine" | None |
| Running | "Restoring *i* of *n*…" | Ring, disabled |
| All restored | Gone; "Restored *n* sessions" | None |
| Partly or none | "Restored *a* of *n*" / "Couldn't restore any …", and a line each | "Try again…" while any is offered |
| Refused or busy | "Couldn't restore: *reason*" / "Couldn't restore now: …" | As before the press |

**Accessibility** (§9.1): the button's name says the machine (A1). The dialog is modal, its title
its name, focus kept inside it and starting on Cancel; closing it returns focus to the button
(A3, A4). The notice's live region says "Restoring *n* sessions on *machine*" once when it starts,
and the outcome once when it ends, never each step (A6). Reduced motion: no animation but the ring's
own (A10).

**Platforms:** Windows app, a ContentDialog, the button a standard secondary Button in the InfoBar.
PWA, the app's own confirmation dialog, as End session's. Apple (#260): note only. Home Assistant:
none, read only as today.

| Use | Text (PROPOSED, for Creative) |
|---|---|
| Offered, can act | "*n* sessions weren't restored: restoring after a reboot or crash is off on *machine*" / "*n* sessions can be restored" |
| Button | "Restore…" (name "Restore sessions on *machine*"); after a partial run "Try again…" |
| Confirmation | "Restore *n* sessions on *machine*?"; "Each opens idle, where it stopped. Nothing is typed into them."; "and *m* more"; "*k* more aren't listed here. Resume them from Launch." |
| List facts | "was mid-turn", "a question was lost", "stopped again soon after its last restore" |
| Confirmation buttons | "Restore *n*", "Cancel" |
| Running | "Restoring *i* of *n*…"; announced "Restoring *n* sessions on *machine*" |
| Outcomes | "Restored *a* of *n*"; "Couldn't restore any of the *n* sessions"; "Not tried: *title* (*reason*)"; "Started, not confirmed: *title*. Check at the machine."; "Couldn't restore: *reason*"; "Couldn't restore now: *reason*. Try again in a moment."; "A restore is under way from another device." |

### 6.13 Sign-in needed, agent errors and Not responding (#800)

A session must never look healthy while its agent is dead. When an agent stops because its
sign-in expired, because a turn ended on an error, or because it went silent mid-turn, the row
says so in words. One sign-in fixes every session for that machine, agent and account, so the
action lives on the machine, not on each row. The field names follow Connection's contract
(`Session.attention`, #818); one adapter file per client maps them.

**Status rules** (they extend §2.2; first match wins, in this order)

| Where in §2.2 | When | Glyph | Status text | Action |
|---|---|---|---|---|
| After rule 1 | `attention.kind: signInNeeded` with `recovery: pending` (signed in; the host hasn't seen the model answer yet) | `idle` | "Signed in · waiting for the session to confirm" | None. A session that died mid-turn is to get a Continue like §6.12's (planned, #780); not built yet |
| After the row above | `attention.kind: signInNeeded` with any other `recovery` (or none) | `needs` | "Needs sign-in", with the machine as line 2's second fact (§2.2); the agent and "@*account*" are in the banner and Details | **Sign in**, only with the `sessions.signin` capability and `signIn: phone`; no button otherwise (A7) |
| Refines rule 3 | `working` with `attention.kind: notResponding` | `failed` | "Not responding", with the machine as line 2's second fact; the trailing age says since when, and Details gives the time | None; there is nothing safe to press yet |
| Before rule 4 | Main agent idle, and `attention.kind: error` | `failed` | "Not authorised", "Rate limited" or "Agent error", from `attention.errorClass` `auth`, `rate` or `other` (an unknown class reads as "Agent error"; a new class gets its words here first), plus "· N delegates still running" when background agents survive the turn | None on the row; Details gives the host's sentence for it (`attention.message`) |

The error rule sits after the question rules and before rules 4 and 5, so background work
that survives a failed turn never hides the error behind "Delegating".

- Needs you counts questions and approvals only (owner decision, 2026-11-05): the banner owns
  sign-in, so `signInNeeded` doesn't count in Needs you, its N or the app badge (§6.1, §7), and
  the filter doesn't show it. Nor do "Not responding", the error rows and "waiting for the
  session to confirm": nothing a person does on the row fixes them. The row itself is
  unchanged, found where it lives in the list and by search.
- **Signing in isn't recovery.** A completed sign-in (`signin.done`) only moves the session to
  "waiting for the session to confirm". It is recovered only when the host clears
  `signInNeeded` under #800's clear gate: a main-agent reply with model words or a tool
  request, dated after the latest sign-in failure, with no newer failure. If the next turn
  fails sign-in again, the row goes straight back to "Needs sign-in". The client never clears
  either state on its own.
- None of them ever shows "Idle", "Working" or a stale "last active" time. An older client that
  reads an unknown status as idle is the bug this section fixes; the protocol decides whether
  that needs a capability.
- `attention.message` is the host's composed sentence (`AgentErrors.Templates`, #841), never
  the agent's or the CLI's own words. It is shown only in Details, as plain text through the
  hidden-text check, never as a link or Markdown.
- **A *reason* is the host's, never the machine's words.** Wherever a line below ends in
  "· *reason*", the reason is the host's categorised reason in plain words, from a known set.
  Raw text from the machine or the CLI is never shown, verbatim or trimmed: it belongs in
  diagnostics (#802). A line with no reason from that set reads without one ("Sign-in
  refused"). The set for a refused start, by the host's reason: "too many requests, try again
  later", "another sign-in is under way", "too many sign-ins waiting on this machine" and "not
  available from this device".

**Machine banner** (one per machine, agent and account, rolled up from the sessions' `attention`)

- An informational banner at the top of the session list, above Needs you, as both clients ship
  it. One per (machine, agent, account), never one per session. It isn't §6.12's restore notice,
  which is planned for #780 and goes in the window's banner area over every page.
- Title: "*Agent* on *machine* needs sign-in" plus "· @*account*" when known. Line 2: "*n*
  sessions affected: *A*, *B*" ("1 session affected: *A*" for one). Each title is a link that
  opens that session; there is no separate line of titles. The titles come from the client's own
  records (never from text in the record), each drawn as a host's sentence is: revealed (a hidden
  or right-to-left character as its escape) and at most 120 characters. A link keeps its comma,
  so no line starts with one, and each is a target at least 32px tall with a pointer and 44px on
  touch, the height of its line box (§9.1 A11).
- One button, **Sign in**, only with the capability and `signIn: phone`. Without the capability
  there is no button and no instruction: a missing capability doesn't prove the agent can't sign
  in from a device.
- "Sign in at the machine · *reason*" shows only when the host itself says the provider has no
  device sign-in (`signIn: machine`, with its reason). This is the one line that sends the person
  to the machine, so it says why, in a reason from the host's set. `attention` has no field for
  that reason yet (a follow-up); until it has, the line reads "Sign in at the machine" alone.
  The issue that leaves a provider so carries `needs-rdp` (#696).
- **A `signIn` value the client doesn't know makes no claim:** the banner and the row say
  "Needs sign-in" with no button and no instruction, never phone sign-in and never "at the
  machine".
- Not dismissible while it lasts: it goes by itself once every affected session is recovered.
- **Opened from a sign-in push** (`?host=<id>&signin=<agent>`, #969): the PWA shows that machine's
  sessions list (from another page, or in place of a session open over the list on a phone), and
  once its list is whole puts focus on that agent's banner's action (Sign in, or its line when
  there is nothing to press there). If no session for that agent still needs sign-in, the machine
  shows as it is, with nothing said; an agent the client doesn't know is ignored. The link picks a
  banner and nothing more.
- States (each client's adapter maps them from the sessions' `attention` and `recovery`, and
  the sign-in request it started):

| State | Banner shows | Button |
|---|---|---|
| Needed | Title; "*n* sessions affected: *A*, *B*" | Sign in |
| At the machine (`signIn: machine`) | "Sign in at the machine · *reason*" | None |
| Checking (`recovery: checking`; the host is looking for a stored sign-in) | "Checking for a sign-in that still works…" | Sign in |
| Signing in elsewhere (`recovery: signingIn`, and no request from this device) | "Waiting for sign-in" | Show code (joins the sign-in under way) |
| Code shown | "Waiting for sign-in · code expires *time*" | None (the sheet is open) |
| Code expired | "Code expired" | Try again (a new request) |
| Signed in, confirming (`recovery: pending`) | "Signed in · waiting for *n* sessions to confirm" | None |
| Recovered (the host cleared `signInNeeded`) | "Signed in · *n* sessions recovered" for 60 s, then gone; or "Recovered by itself" when no person signed in | None |
| Refused (the machine refused the request) | "Sign-in refused · *reason*" | Try again (a new request) |
| Failed | "Sign-in failed" (`signin.done` carries no reason) | Try again (a new request) |

A session that fails sign-in again while confirming returns the banner to Needed, with its
count, and its row to "Needs sign-in". Sessions that confirm leave the count one by one.

**The sign-in sheet** (`signin.code`, only on a person's device)

- Opened by **Sign in** on the banner, the row or the push, and only on a person's press. No
  client starts it on its own.
- **Before the code arrives:** "Starting *Agent*'s sign-in on *machine*…" with the progress
  ring, and Cancel. If the host's answer refuses it (`outcome: refused`, or an outcome the
  client doesn't know), the sheet closes and the banner shows "Sign-in refused · *reason*" with
  Try again. If the request fails without an outcome (no answer, or an error reply), the sheet
  closes and the banner shows "Couldn't start sign-in. Try again." as an alert line under its
  own verb; if the machine can't be reached, the line is "*machine* can't be reached now".
  Neither ever carries raw text from the machine or the CLI: a reason is the host's, by the
  rule above.
- **With the code:**
  - Line 1: "Open this page and enter the code." The page is the allowlisted URL as plain text
    (for example `github.com/login/device`) with **Open**.
  - The code in the monospace face at the display size, with **Copy**.
  - "Expires *time*", in the device's own time zone and time format, as every time in the app.
  - Buttons: **Copy** is the one primary, on every client: copying the code is the step that
    moves a phone's sign-in forward, and Open follows it. Open and Cancel are secondary. Focus
    starts on Cancel, and Escape is Cancel. In the app, Copy is also the sheet's default: Enter
    copies the code (but on Open or Cancel, whose own Enter presses them). Nothing is copied or opened without a press.
- **A URL not on the allowlist, or a malformed code:** the sheet shows "This sign-in code
  couldn't be shown." with **Try again** (a new request) and Cancel. No link and no raw text,
  and it doesn't send the person to the machine: that's only for the host's `signIn: machine`.
- **Expired:** the code is hidden and the sheet says "Code expired" with Try again, even before
  the host updates.
- **Success:** the sheet closes and the banner goes to "Signed in · waiting for *n* sessions to
  confirm", not to recovered. Nothing is typed for the sessions, so each confirms on its next
  turn. A session that died mid-turn is to offer a **Continue** like §6.12's, from `signin.done`'s
  `continue` list; neither is built yet (§6.12's is planned for #780, and it reads only
  `restored.midTurn`).
- The code and URL never appear in a notification, a toast, Home Assistant or a log, even after
  a press. They reach the clipboard only when the person presses Copy.

**Pushes, toasts and Home Assistant notices**

One pattern everywhere: *Agent* on *machine*, then the state. *Agent* is the host's own name
for it, as the host sends it: Copilot, Claude or Codex. Every channel uses that plain title and
never adds a count to it; the count, and for Home Assistant the sessions' names, go in the body.

| State | Title (pushes, toasts, HA) | Body |
|---|---|---|
| Needs sign-in | "*Agent* on *machine* needs sign-in" | Pushes and toasts: "*n* sessions affected" ("1 session affected"). HA notice: "*n* sessions: *A*, *B*" ("1 session: *A*"), naming three at most |
| Not responding | "*Agent* on *machine* isn't responding" | `attention.message` |
| Not authorised | "*Agent* on *machine* wasn't authorised" | `attention.message` |
| Rate limited | "*Agent* on *machine* hit a rate limit" | `attention.message` |
| Agent error | "*Agent* on *machine* stopped with an error" | `attention.message` |

- **The body:** for sign-in, one notice per (machine, agent, account): on pushes and toasts the
  count of sessions affected (`v1-sessions.md`, Push `signIn`), and on Home Assistant's notice
  the count with the sessions' names (`ha-connector.md`, Needs sign-in). For the other four, the
  host's composed sentence (`attention.message`, from `AgentErrors.Templates`, #841). Never the
  agent's or the CLI's own words, the account, the code or the URL. The titles' verbs match those
  templates, so a notice and the session's line read the same.
- **Tiers** (how each one interrupts) are #913's.
- **What sends today:** only sign-in pushes from the host (#928): one per (machine, agent,
  account), as soon as the first session needs it and again at most hourly while any still
  does. Home Assistant sends the same sign-in notice to the phones its answer buttons go to.
  Not authorised and Rate limited get no push of their own; the row says them, and Home Assistant
  shows them as the session's status, as shipped. Not responding, and the Copilot wedge the host recognised, send a "looks stuck" notice from Home Assistant (#687) on the first evidence: one per stuck episode, withdrawn when the session takes a new event, never over an open question (told again only after an observed recovery, never on a timer; it moves to the Slipgates push path with #1396) ([runbooks/stuck-session.md](../runbooks/stuck-session.md)).
- **Sound:** the question sound (owner decision, 2026-11-04, extending #764). A sign-in blocks
  every session on that machine, agent and account, as a question does. The hourly limit keeps
  it from nagging. The payload does not set `notification.silent`.

**Accessibility** (§9.1)

- The banner is a polite live region, announced once per new (machine, agent, account) (A6).
- Sign in's accessible name: "Sign in to *Agent* on *machine*" (A1).
- The sheet traps focus and starts on Cancel (A3, A4). When it closes, focus goes to the button
  that opened it if that button still exists (Cancel, failure, expiry). On success the button
  is gone, so focus goes to the banner's "Signed in · waiting…" line, which takes
  focus programmatically only; if the banner has already gone, to the session list's heading.
  A sheet opened from a push uses the same rule. The code is read character by character ("Code: A B C D, 1 2 3 4").
- Reduced motion: the ring is still; the banner and sheet appear without animation (A10).
- Every state is told apart by its words as well as its glyph.

**Copy**

| Use | Text |
|---|---|
| Row | "Needs sign-in · *machine*"; "Signed in · waiting for the session to confirm"; "Not responding · *machine*" (age trailing); "Not authorised"; "Rate limited"; "Agent error" |
| Banner | "*Agent* on *machine* needs sign-in"; "*n* sessions affected: *A*, *B*" ("1 session affected: *A*"); "Checking for a sign-in that still works…"; "Waiting for sign-in"; "Waiting for sign-in · code expires *time*"; "Code expired"; "Signed in · waiting for *n* sessions to confirm"; "Signed in · *n* sessions recovered"; "Recovered by itself"; "Sign-in refused · *reason*"; "Sign-in failed"; "Sign in at the machine · *reason*"; "Couldn't start sign-in. Try again."; "*machine* can't be reached now" |
| Sheet | "Starting *Agent*'s sign-in on *machine*…"; "Open this page and enter the code."; "Expires *time*"; "Code expired"; "This sign-in code couldn't be shown." |
| Buttons | "Sign in"; "Show code"; "Open"; "Copy"; "Cancel"; "Try again" |
| Push, toast and HA notice titles | "*Agent* on *machine* needs sign-in"; "… isn't responding"; "… wasn't authorised"; "… hit a rate limit"; "… stopped with an error". No channel adds a count to the title. Body for sign-in: "*n* sessions affected" on pushes and toasts; "*n* sessions: *A*, *B*" ("1 session: *A*") on HA's notice. `attention.message` for the others |

**Platforms**

- **Windows app and PWA:** the banner at the top of the session list (in the list's header in the
  app) and a modal sheet (ContentDialog in the app, a dialog in the PWA).
- **Apple (#260, owner welshofer):** note only. The same fields; the native form is that
  track's decision.
- **Home Assistant:** read-only. The session's status says what the row says ("Needs sign-in",
  "Not responding", "Not authorised", "Rate limited", "Agent error"). Its sign-in notice follows
  the pattern above: the plain title, and "*n* sessions: *A*, *B*" as its message. Never the code
  or URL, and no Sign in.

### 6.14 A machine whose bridge isn't running (#970)

**Proposed; not built.** The words are Creative's and the design is confirmed by Product, on #1022 (6691202710, 6691211514). When a machine's host
has stopped, nothing on it can tell the person: pushes, toasts and Home Assistant notices all
come from the host. The relay outlives it, so the relay decides when a host is gone, and both
clients say so from the relay's presence (`presence.known[].offline.gone`,
[host-gone.md](../protocol/host-gone.md)). The design is Product's
([#800 6676984805](https://github.com/danswett/agent-bridge/issues/800#issuecomment-6676984805)).

**When it shows.** Only once the relay sets `gone`: the host went without announcing it, with
evidence that the bridge stopped while the machine is up, and it hasn't come back within
5 minutes, which leaves time for #805 to restart it first. So:

| The machine | What the clients show |
|---|---|
| Its host went less than 5 minutes ago | What §6.1's "A machine away" shows now: "Reconnecting…", then "Offline · last seen…" |
| Its host announced the departure: an update, a restart, a stop, sleep, shut down, a reboot, sign-out (#805) | #805's planned state, never this |
| It went silent (switched off, asleep, off the network) and nothing shows the bridge stopped | "Offline", as now, and no push or toast, even with work in flight (Product, #1022: we can't truthfully say the bridge stopped) |
| Its host is gone | This section |

It goes as soon as the host is listed online again, and when the machine is forgotten or the
account changes.

**On the Machines page (§6.6).** The machine's row stays an offline row, with line 2 saying why:

- Line 1: the machine icon, the name, and "Offline", as any offline row.
- Line 2: "Bridge isn't running · since *time*", in place of "Last seen…". The since time is
  `gone.since`, when the host first went in this episode, which is earlier than "last seen"
  when it came back briefly in between. The name isn't repeated: it is on line 1 (every element
  has a purpose).
- Inside the row: Forget, as for any offline machine, and nothing else yet. When #801's remote
  restart exists it adds its one action here and on the banner, in #801's words. Never an
  instruction to go to the machine, now or later.

**On the machine tab (§6.1).** A banner at the top of the session list, above Needs you, in the
place and style of §6.13's machine banner: "*machine*'s bridge isn't running · since *time*".
One per machine; it shows with that machine's tab chosen and with All. Not dismissible while it
lasts; it goes by itself when the host is back. No button until #801's. The tab's tooltip and
accessible name, and its entry in the "N more" menu, read "*machine* · bridge isn't running".
The machine's rows keep §2.2's away treatment ("Offline · last seen…") until §6.1's 15 minutes
take them; the banner says why, so the rows don't repeat it.

**Never claim an agent state we can't see.** Nothing in the line, the row or the banner says
what the sessions are doing: the host was the only thing that could see them, and it's gone.
The agents may well still be running at the machine. Nor does any of it say why the host
stopped ("crashed", "killed"): the relay can't know.

**Time.** "since *time*" is when the relay saw the host go (`gone.since`), in the viewer's local
time zone and time format, as every time in the app (§6.4): "since 8:31 AM" today; "since
yesterday, 11:02 PM"; "since Tue, 11:02 PM" within the last week; "since Nov 3, 11:02 PM"
before that. No zone is added: it is one instant shown in the viewer's own zone, not another
machine's clock. The tooltip and accessible name give the full date and time ("since Friday,
November 6, 8:31 AM").

**Tone.** Plain and calm, a fact rather than an alarm: the machine is named, the state is said,
and there is no exclamation, no "error" and no blame. The banner is informational, never the
`failed` colour (§3): nothing went wrong with what the person did.

**The push and the toast.** One notice, only if any agent there, a delegate included, was
working or waiting when the host went (the host arms it in advance; the relay sends it, v1-sessions.md
[Host gone](../protocol/v1-sessions.md#host-gone-970)). A machine that's simply off, with nothing
in flight, notifies nothing.

| | Title | Body |
|---|---|---|
| Push (PWA), toast (Windows app) | "*machine*'s bridge isn't running" | "It stopped while sessions there were working or waiting on you." |
| When the host is back with nothing to restore (push only, silent, replacing the one above) | "*machine*'s bridge is running again" | "You can reach its sessions again." |

- **One notice on return, never two** (Creative, #1022). If the bridge's return brings §6.12's
  restore notice (sessions went with it, whatever the cause), that notice replaces the alert in
  place, on the same tag, and "…is running again" is never sent: it would say the sessions can
  be reached when some were lost. "…is running again" goes only when there is nothing to
  restore: once the bridge has been back 2 minutes and its restore check has found nothing. A
  device the alert didn't reach gets the restore notice as today. The Windows app toasts once
  per episode, even if the bridge flaps, and removes its gone toast once the bridge has been back
  2 minutes, or replaces it with the restore toast, so that is the only one left. The mechanism is
  in host-gone.md ("When the host comes back").
  - The restore notice that replaces the alert **alerts** as Needs you does today, with the
    question sound, never silently: the person still has something to answer.
  - Once §6.12's withdrawal takes the restore notice away, nothing about the alert comes back:
    the alert was replaced, not stacked.

- **Tier:** Needs you: it interrupts, with the question sound, sent once per episode (#913).
  It doesn't count in Needs you's N, its filter or the app badge: N counts questions and
  approvals only (owner decision, 2026-11-05), and nothing on a session row fixes this. #805's
  rule for a machine offline unexpectedly still governs every machine that isn't gone
  ([v1-sessions.md](../protocol/v1-sessions.md#notification-class-913), "What a host classes
  today"); this section governs one that is.
- **Once.** One per machine per episode, at most one an hour per machine and three a day per
  account, shared with #805's no-return notice. A device on screen gets it too: the banner is
  easy to miss on another tab.
- **The body** is sealed by the host before it went, so it never carries the time, a count or
  a session's name (any of them could be stale). The notification's own time says when it came.
- **Opening it** opens the client on that machine's tab, with the banner.
- **The Windows app** gets no Web Push: it raises the same toast once from the relay's presence
  (`gone.alert`), which the relay sets whenever work was in flight, whether or not any browser
  was subscribed. The app on the machine whose host died alerts the same way, as any other
  client.
- **Home Assistant:** nothing new. Its connector runs inside the host, so it can't say this.

**Accessibility (§9.1).** The banner is a polite live region, announced once per machine and
episode (A6). The row's accessible name reads "*machine*, offline, bridge isn't running since
*full time*". Every state is told by its words, not its colour.

**Copy**

| Use | Text |
|---|---|
| Machines row, line 2 | "Bridge isn't running · since *time*" |
| Banner | "*machine*'s bridge isn't running · since *time*" |
| Machine tab tooltip and "N more" entry | "*machine* · bridge isn't running" |
| Push and toast title | "*machine*'s bridge isn't running" |
| Push and toast body | "It stopped while sessions there were working or waiting on you." |
| Push when back with nothing to restore (silent) | "*machine*'s bridge is running again"; "You can reach its sessions again." |

**Platforms**

- **Windows app and PWA:** the row on the Machines page, and the banner at the top of the
  session list (in the list's header in the app), as §6.13's.
- **Apple (#260):** note only. The same fields; the native form is that track's decision.

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
- The count each platform shows is Needs you's N (§6.1), which never counts a session that needs
  sign-in (§6.13).
- **Collaborators (#519):** their sessions are never notified, badged or counted. A message
  from an external agent held for you is counted in N but **never pushed on its own**, unlike
  an approval (`agent-principals.md` §5.7, normative). A person can opt in to one aggregated
  push an hour at most, with a count and no text ("3 agent messages are waiting"), which opens
  Needs you.
- **Session groups (#767)** change nothing here: a session in a collapsed group is notified,
  badged and counted as any other, and opening its notification expands nothing but shows its
  session.

**Session groups on each platform (#767).** The rules are §6.1's on every client; only the
controls are each platform's. Where marked *to verify*, the pattern is still to be checked
against that platform's own guidelines before it is built.
- **Windows:** the header is an item of the sessions `ListView`, drawn as a disclosure button
  with the ExpandCollapse automation pattern, so arrows, selection and drag stay the list's own,
  rather than an `Expander` per group or `ListView`'s grouped `CollectionViewSource`, whose
  headers don't collapse on their own. *To verify* against Fluent's list and tree guidance.
- **PWA:** the header is a `<button aria-expanded aria-controls>` in the list's arrow order,
  and its rows sit in an element with `role="group"` named for the group. A phone's Move to
  group is a sheet.
- **macOS:** each group is a sidebar section that collapses (`Section(isExpanded:)` in the
  source list), between Pinned and Sessions; Move to Group is a submenu of the row's context
  menu, and a drag onto a section adds to it. *To verify* against the macOS sidebar guidelines.
- **iOS:** each group is a collapsible section of the inset grouped list
  (`Section(isExpanded:)`), its header a button; Move to Group is a submenu of the row's
  context menu, and a swipe action stays Pin. *To verify* against the iOS list guidelines.
- **Android:** each group is a header item in the `LazyColumn`, a disclosure with its state as
  `stateDescription` ("Expanded", "Collapsed"); a row's long press opens a
  `ModalBottomSheet` with Move to group. *To verify* against Material 3 lists.

**Lines that outlive their cause, on every client (#1193):** a line says something about a
cause: a question waiting, a press counting down, a machine busy. When the cause is gone, an
amber or red line becomes a neutral closed line, or goes. It never sits. The rule, and what each
line does:
- **A closed line stays at least 5 s** (`LINGER_READ_MS`, as an entry in the Needs you lane,
  §6.1), so it can be read, and then goes unless it is part of the conversation (below).
- **A question that has left the session's list** is shown as the question as it was asked, kept
  in the conversation, with "No longer waiting" in neutral words (§6.4, Resolved): never amber,
  never red, never Try again, and never "Your answer:" (only the words of an answer the person
  gave say that: one in flight, or one that may have gone in). It stays through the next question
  and the next turn. The host cannot tell a question the agent withdrew from one answered at
  the machine or on another device, so no cause is said: there is no "Cancelled" and no
  "Answered on another device". A stale answer that carries the host's reason for a form it
  cancelled part answered (`formReset`) is the exception: it says the answer was **not delivered**,
  keeps what was typed to read and copy, and offers Try again while the question is still listed.
- **Not confirmed** keeps its words: amber while the question is live, neutral once it is in the
  history. If a later check says it was delivered, it reads "You answered".
- **Typed text kept for a question that went** is a neutral note with Dismiss, not amber. It
  stays through the next turn, and goes on Dismiss or once that text is sent.
- **"Try again in a moment"** (busy, for an answer, reply, End session or launch) goes when the
  2 s pause ends, as the control comes back.
- **A reply's** not-sent, refused and cancelled lines go with the next edit of the box or the
  next turn; "not confirmed" goes with the next turn, when the conversation shows whether it
  went in. A line with Try again (could not send) stays while it can be tried.
- **A lapsed "End session NOT confirmed"** is neutral, and goes after 5 s.
- **A launch's own complaint about what is picked** ("Not launched: pick a workspace…") stays
  until the setting it names changes, not until the machine's next list redraws the page.
- **A folder-trust question the machine stops listing** is one neutral line, "*Machine* no longer
  lists this question.", for 5 s, and then goes. What the person's own answer did (started,
  declined, not confirmed) keeps its words.

Windows follows the same rule in its own words for the same lines (#1195); the PWA is checked by
`test/answer.spec.mjs`, `reply.spec.mjs`, `stop.spec.mjs` and `launch.spec.mjs`.

**Coming back to the app, on every client (#950):** §6.1's "Coming back to the app" rules hold
where each client comes back: the PWA first, on returning to the screen or a network change; the
Windows app, rules 1–5, on waking from sleep, with the same 3 s grace and the slot and strip in
the list pane's header row, never in the title bar; Apple and Android when
they are un-parked.

**Windows (WinUI 3):**
- **Lines that outlive their cause (#1193, #1195):** an error or warning line lasts only
  while its cause stands. When the question it was about closes, a refused, stale or failed
  answer line becomes the neutral "No longer waiting" (no tone, never prefixed "Your answer:"),
  is kept 5 s so it can be read, then goes; a busy line goes with its 2 s pause. A held
  answer ("offline", the machine was not connected so nothing was sent) says why it could not
  go, so it is treated like a refused one. Only delivered and unconfirmed ("Sent, but not
  confirmed") lines stay. A toast that says an
  answer did not go is withdrawn when its question closes, and one that would arrive after is
  not shown.
- **Window:** Mica, with a custom 48 px title bar carrying the mark, the app name (12/16)
  and a centred search box. System caption buttons.
- **Search:** finds sessions on every machine by the words of their title, repository, branch,
  agent or machine, titles starting with them first, then the most recently updated, whatever
  machine the list shows, and opens the one chosen as a notification does.
- **Navigation:** a compact `NavigationView` rail (Sessions, Machines, Launch, Usage;
  Settings at the foot) with a selection pill. Settings opens its one page (§6.9). **Not built
  yet (#384 slice 5):** Settings' own pane of sections beside the rail, which first-run
  setup's stepper replaces while it runs (§6.9, §6.10).
- **Content:** a layer with an 8 px top-left radius.
- **Controls:** ListView rows with a 3×16 Bridge pill on selection, SelectorBar for
  machines (those that don't fit under More, §6.1), an Accent button for New, subtle icon buttons
  for header actions, and a `ToggleButton` for Needs you · N.
- **Tray and toasts:** the tray's tooltip and the rail's Sessions badge carry N ("Slipgates
  · 3 need you"). A question's toast follows the rules above: an approval's toast has
  Open only. Every toast is silent (#754): none plays Windows' default sound, and any sound
  comes back only as an owner-approved spec here first.
- **Banners** sit over every page, `InfoBar`s in this order (#293, #1064):
  - What stops the app from its job: the host not running, refusing the app, connecting, or
    view only. Warning, with no close button: it goes once that is put right. On waking from
    sleep or changing network, "connecting" is not raised for it (#950): during §6.1's 3-second
    grace nothing shows, and after it, if the link is still down, the connection strip replaces
    the list header row's content at the same height (§6.1, rules 3 and 4). A banner already
    showing when the app comes back stays put.
  - **There is no "Discovery held" banner** (#1064). A hold is shown on the machine's row once it
    has lasted two minutes (§6.6), and a resume it holds back says so in the reply line (§6.7).

**The banner rule, in the PWA and the Windows app (#1064):** a banner is for a state that lasts.
- **Grace:** a state that calls for a banner shows it only once it has stood for 3 seconds
  (the same grace as §6.1's); one that ends sooner is never shown.
- **Minimum:** once shown, a banner stays at least 10 seconds, even if its state ends sooner,
  so it never flashes. If the state returns inside that time the banner simply stays.
- **Motion:** a banner opens and closes by animating its height with the `toast` motion
  (§10); under reduced motion it appears and goes at once. Toasts stay overlays and never
  move the page.
- **Which:** this governs the banners a state raises (the PWA's "can't update" and "this
  device was replaced"; the Windows app's host-not-running, refusing, connecting and view-only
  banner). A banner a person can close, or that carries an action the person asked for (an
  update, a restore notice, a sign-in), is not held back by it.

**macOS (SwiftUI, #260):**
- **Sidebar:** a translucent source list (a Needs You · N filter item, Pinned, your groups,
  Sessions, Machines), 28 px rows, and a Bridge fill on the selected row. The toolbar's filter menu has
  the same toggle.
- **Menu bar:** an extra whose label is N. Its menu names the waiting sessions (machine and
  title, oldest first, at most 8) and Open Slipgates. An item opens its session at the
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
- **Session:** a top app bar, assist chips for delegates on a tablet (a phone has the
  Delegates tab, §6.2), and the composer on
  `surfaceContainer`.

**PWA:**
- **Look:** follows the Windows design at every width (#166), with no imitation of other
  platforms' controls.
- **Desktop:** the Windows layout with a 56 px web app bar in place of the title bar (mark,
  name, search, machines online, account).
  - **Coming back (#950):** the list pane's header row (the Sessions heading) holds the fixed
    slot at its end, and the connection strip replaces that row's content at the same height,
    as the phone's title row does (§6.1, rules 3 and 4). Neither ever goes into the web app bar
    or above the list.
- **Phone:**
  - a 24/32 title row with Search and New, and the sort menu between them;
  - machine tabs;
  - a bottom navigation bar (Sessions, Machines, Usage, Settings) with a Bridge indicator. New
    is the way to Launch.
  - The app bar comes back only as a strip while the connection has news (connecting,
    reconnecting, paused, an error), and as the search row while searching.
    - **The strip replaces the title row's content, at the same height** (#950), so nothing below it
      moves. After a return it shows only once §6.1's 3-second grace has run out with this
      client's own link still down, and it is announced once, politely. A machine's own news
      is never the strip's: it is on its rows, its tab and the Machines page (§6.1, rule 8).
      Before #950 the strip and connection lines were inserted above the list, and the rows
      jumped down and back up on every return.
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
- **A launch not confirmed whose session registers late** (#891, #896) says "Session started." in
  its status, on Launch for that machine only. The PWA follows one such launch per machine, and the
  app follows one launch in all. A launch whose outcome names no session (a Codex's) is not
  followed until the host names it (#983), and a start whose session isn't known (no card to open)
  says "Session started. Open it from the list."
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
- **Session groups (#767):** a group's name is the person's own words, never changed or
  translated. The words around it: "Move to group", "New group…", "Remove from group", "Group
  name", "Rename group", "Delete group", "*name* · N" on the header, "N needs you" on a
    collapsed one (and "unread", said only), and "*name* deleted. N sessions moved to Sessions". Never "folder", "tag" or
  "category".
- **Approval notifications** say "waiting for your approval" and offer only Open (§7): an
  approval is approved in the app, never from a notification.
- **No sentences explaining the UI,** no exclamation marks, and no emoji. The exceptions are
  a collaborator's read-only lines (§6.7), which say whose session it is and who can act on it,
  because a control that is absent can't carry a tooltip, and Settings' and setup's one-line
  consent, security and cost lines and step subtitles (§1, principle 5; #384).
- **Setup and Settings words (#384):**
  - States: Not selected, Not found, Not signed in, Not checked, Not registered, Not trusted,
    Needs you, Working, Blocked, Blocked by *X*, Failed, Checking, Waiting for you, Queued,
    Found, Signed in, Registered, Trusted, Approved, Kept, Not changed, Not paired, Local only,
    New session needed, Install needed, Installing, Install failed, Install interrupted,
    Version not verified, Not verified, newer than tested.
  - Agent rows today (#784, #705): "Not installed"; "Not installed · install it on this
    machine"; "Installing…"; "Install failed · *reason*"; "Read-only · *reason*"; "Hook
    ownership couldn't be checked · *reason*"; "Hook intake couldn't be checked · *reason*";
    "Can't check · this machine's bridge is older"; "Not tested yet". Actions: Install CLI, Cancel
    (named "Cancel CLI installation"); the consent dialog's Install CLI and runtimes.
  - Version lines (#231): "We couldn't read *Agent*'s version. Setup may not work; we'll
    check it afterwards."; "Update *Agent* to set it up".
  - Titles: Set up; Discover; Agents; Accounts; Hooks; Workspaces; Verify; Ready on this
    machine. Settings' sections: Overview, Agents, Accounts, Workspaces and trust, General.
  - Actions: Continue, Back, Finish, Save and exit, Resume setup, Set up again, Scan again,
    Check again, Install, Not now, Cancel, Try again, Sign in, Sign in again,
    Open Codex, Repair hooks, Run test session, Remove bridge integration, Add folder, Remove,
    Pair a phone, Use local only.
  - The product name in them is Slipgates.
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
- **Receipt words** (§6.5, #600; Decided, not yet built (#600; builds in #616, #625): today both clients show Sending…, Sending again… and "Delivered. The session has your reply." for a reply, and "Delivered. The session has your answer." for a question card's answer). On screen, only for an exception: Still sending… · Sent ·
  still waiting on *machine* · Passed to the agent · not picked up yet · still being
  delivered · Waiting for *machine* · it goes when *machine* is back; outcomes other than
  Delivered keep today's words, except that not confirmed points at the conversation, never
  the machine: "Sent, but not confirmed. Check the conversation before sending it again." (an
  answer: "… Check the conversation.", never inviting a second answer). On a resolved card: You answered · *value* (the option's
  label, free text's first line cut to about 40 characters, nothing for a form). Spoken
  only, never shown on the normal path: Reply sent · Reply passed to the
  agent · Reply up next, after the agent's current step · Reply delivered (and "Answer" for
  an answer). Delivered is Send's check, never a line or a badge. Older hosts use the same
  words, less the stages they don't report. Never "typed", "queued", "injected", "in the
  terminal" or "Sending again…". Never "answered" before the host confirms.
- **Return words (#950):** "Updating…" (the title row's cue, and under a held control pressed);
  "Connecting to N machines…" (the same slot, for machines with no kept list); "*machine* is
  offline · you can answer once it's back" (the reply box and the question). An error the person
  can act on (Couldn't reach, unsupported) is one line in the same slot, naming the machine
  ("Couldn't reach *machine*", with "and N more" when there are others and it fits), in place of
  "Updating…".
  Never a sentence over the list about a machine's connection: no "*machine* is offline. Its
  sessions are as it last sent them…", "Connecting to *machine*…" or "Opening an end-to-end
  encrypted channel to *machine*…" there.

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
| A5 | **Escape and back go back one step.** Esc closes the innermost menu, sheet, popup or dialog first (a dialog's Esc is its Cancel). With none open, Esc, the browser's back and the system back gesture pop one pushed screen (§5.3, §7's back stack), and the header's Back goes where §7 says (on a phone, always the list), with focus as A4. In a text field Esc is the field's, never a pop: search clears and closes, the reply box keeps its words and its focus, and an input method's composition ends; Tab or Shift+Tab out of it first. | PWA: `a11y-pwa`'s keyboard.spec, "Escape closes it with focus back on More", and on a phone Escape from a pushed session, from a delegate, and past the overflow menu, the reply box, search and a composition (#370). Windows: manual | |
| A6 | **Announcements are polite, and said once.** A status that changes on its own is a polite live region (PWA `role=status`; Windows `AutomationProperties.LiveSetting` Polite with a LiveRegionChanged event), on the page and empty before its first words (§7). Assertive (`role=alert`; LiveSetting Assertive) is only for a problem that stops what the person is doing and needs them to act, for example Launch's problem line, a reply or answer that failed, and §7's "This app is open in another window or tab". Progress, success and revocation are never assertive. A change is said once: a focus move, a name and a live region never say the same thing twice, and a control's state is said by its role (pressed, expanded, busy), not repeated in its name (§6.4). Each surface sets its own rate limit: Needs you at most every 5 s (§9.2); the reply receipt says nothing in its first 400 ms, then at most one stage a second (only the later of two is spoken), and always its outcome (the owner's #600 decision, whose text reaches §6.5 with #617). | Manual (`a11y-screen-reader` is planned) | #600, #678, #699 |
| A7 | **One verb per state.** An action button says, with one verb, what it does in the current state, and its words change when the state does (for example "Set up" when missing and "Repair" when broken, #678). A row in a state with nothing to do shows no button rather than a disabled one, as §6.7's collaborators are absent, not disabled; §6.1's controls disabled while a machine is away stay as they are. A toggle keeps its words; its pressed or checked state says the rest. | Manual; no button instead of a disabled one is not yet enforced, tracked in #678 | #678, #699 |
| A8 | **Contrast:** text 4.5:1, and glyphs and the non-text parts that show state or focus 3:1, against what they sit on, in light, dark and high contrast (§3's Contrast). Words that would be Bridge on light's `selected` tint take `text` instead, and every foreground and background pair a client draws is listed in `tokens.json` `color.contrast`, so `a11y-contrast` checks it. | Tokens: `a11y-contrast` (`tokens.json` `color.contrast`). PWA: axe `color-contrast` in light, dark and forced colours in `a11y-pwa`, and on a selected machine's words in light. A selected machine's refusal line (`.machine-error`) is drawn under the selected block on the plain row surface, never on the tint (#858). Non-text 3:1 is manual. Windows: manual | #366 |
| A9 | **High contrast uses system colours.** In Windows high contrast and the browser's forced colours, every role and surface takes a system colour (§3's Theme, `tokens.json` `hc`), and shape and words still carry each state (§2.1). | PWA: `pwa-ui`'s theme.spec, "in forced colours every role is a system colour", and `a11y-pwa`'s forced-colours axe run (Chromium); that shape and words still tell states apart is manual. Windows: manual | |
| A10 | **Reduced motion** follows each platform's setting, as §5.3's reduced-motion column and §9.2 say. | PWA: `a11y-pwa`'s reduced-motion.spec. Windows: manual | #659 |
| A11 | **Targets:** at least 44×44 on touch platforms and 32×32 with a pointer on desktop; never under WCAG 2.5.8's minimum anywhere (24×24, or its spacing exception). "On touch" means touch is the only pointer: a tablet with a trackpad or mouse attached takes pointer sizes, as a desktop does (PWA: `(pointer: coarse) and (not (any-pointer: fine))`, never `(pointer: coarse)` alone, which an iPad keeps with a trackpad). | PWA: axe `target-size` in `a11y-pwa` (WCAG 2.5.8 only); 44 and 32 are manual | |
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
  facts of its own; what is drawn keeps them. The same goes for the title and for a semicolon
  or a sentence stop (". ", "! " or "? " and one at the end, as a run; a dot inside a word such
  as "v1.2" stays): each becomes a space, so a title of "Fix. Idle; done" reads "Fix Idle done"
  and can't end the line early or read as a fact. Only the frame's own commas, "driven by an
  agent" and the like are read as written. The app and the PWA give the same line for the same
  session, and a delegate's name is judged for "Main agent" after it is cut to its shown length.
- **Reduced motion** stops all glyph animation, and turns every other motion into an
  instant change or a short cross-fade (§5.3). Nothing relies on motion alone.
- **The phone's collapsing header** (§6.2, #620) hides nothing from assistive technology.
  The folded parts stay in the tree and the tab order, focus reaching them brings the header
  back, and collapsing is never announced. The bar's one-line copy is drawn only: the title is
  read from the h2 and the status from the meta line. Nothing focused in the conversation
  lands under the header, and the header never expands over the focused control. In high contrast the bar is `Canvas` with a
  `CanvasText` hairline. The reply area's compact bar (§6.5, #935) is the same: its copy of the
  draft is drawn only, focus anywhere in the reply area brings the box back in full, it never
  expands over the focused control, and focus never lands on a hidden control.
- **The reply and answer receipt (#600; Decided, not yet built (#600; builds in #616, #625): today both clients show Sending…, Sending again… and "Delivered. The session has your reply." for a reply, and "Delivered. The session has your answer." for a question card's answer)** is a polite live region (`role=status` on the PWA;
  `AutomationProperties.LiveSetting` Polite with a LiveRegionChanged event on Windows),
  present and empty from Send. The normal path shows nothing, but its stages are still
  announced: Sent, Passed to the agent, Up next and Delivered, prefixed "Reply" or
  "Answer" (Sending is not: Send's busy state already says it, once, A6), nothing in the first 400 ms, at most one a second (only the later of two is
  spoken), and the outcome always. An exception's line is announced as it appears; a reply
  or answer that failed (Refused, failed) is said assertively, as A6 says. Send's
  check is not announced on its own: "Reply delivered" says it, and Send's accessible name
  stays "Send" (Send answer's, "Send answer"). In forced colours Send's ring and check take Send's own system colours, as
  its arrow does (A9), at full opacity, including the still ring of "Waiting for *machine*":
  on Send's Highlight fill GrayText would fall below A8's 3:1, so stillness and the waiting words
  say the request is held, never a dimmed ring. Every word on the line is CanvasText, so
  every failure says itself in words. Under reduced motion the ring is still, the ring, the
  check and the rest glyph swap over `crossFade`, and a line fades in over `crossFade`
  instead of rising; the quiet window and the announcement limit still apply.
- **Coming back to the app (#950, §6.1):** a held control is `aria-disabled` from the moment
  of the return, before it is drawn disabled at 600 ms, so it is never said to work while it is
  held. The "Updating…" cue is never announced. The connection strip, if the grace runs out with
  the client's own link down, is announced once, politely. When an offline machine's line
  replaces a focused control (rule 8), focus moves to the line (`tabindex="-1"`), whose words
  are read as focus lands, with no separate announcement; focus never drops to the page.
- **Keyboard:** desktop lists move with arrows, Alt+↑/↓ reorders (#164), Shift+F10 or the
  menu key opens a row's menu, and Ctrl/⌘+K focuses search. The shortcut is named where
  search is (the Windows search box's tooltip, on hover or when Tab reaches it) and nowhere
  else: no shortcut hint opens because the window was activated, a click landed or Ctrl+K was
  pressed (#256). Needs you · N is a toggle in the tab order after the machine tabs and before
  the list. Space or Enter toggles it, and Enter on a waiting row puts focus on its question's
  heading.
- **Focus stays on a pressed button (#369).** While its request is on its way, Send, End session
  and Launch (and Forget, on Machines) are never disabled, which would drop keyboard focus to
  the top of the page. Each says it can't be pressed (`aria-disabled`), looks it, and does
  nothing when pressed again, by key, click or the form's submit, so a reply, an end or a
  launch goes once. Focus stays on it through the outcome, and between End session's two
  presses; a delivered reply, which empties the box and so turns Send off, gives focus back to
  the box. Send with nothing to send, or held by a question, is disabled as before. When the
  session ends, its view and row go, and focus that was in either goes to the row that takes
  its place, or the row before it if it was last, or else the Sessions heading. On a phone the
  list comes back with focus on the Sessions heading, as Back gives it when its row has gone.
- **Session groups (#767):**
  - A group's header is a button that reports expanded or collapsed by its role (PWA
    `aria-expanded`; Windows the ExpandCollapse pattern), never in its name. Its name is what
    it shows: "*name*, 12 sessions"; collapsed, with "1 needs you" or "unread" after it when
    its trailing edge shows the mark or the dot. Its AutomationId is built from the group's
    id, never its name or
    position (§9.1 A2).
  - In the list's arrow order, headers sit among the rows: ↑/↓ move through headers and the
    rows of expanded groups. → on a collapsed header expands it, and on an expanded one moves
    to its first row. ← on an expanded header collapses it, and on a row in a group moves to
    its header. Enter or Space on a header toggles it.
  - Alt+↑/↓ on a row moves it across group edges as §6.1 says; on a header, it moves the group.
    Shift+F10 or the menu key opens a header's menu as a row's.
  - A move into or out of a group is announced once, politely: "Moved to *name*", "Removed
    from *name*", unless focus moves to the group's header, whose name says it instead.
    Toggling is said by the role alone (§9.1 A6).
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
- **Delegate events (#661, proposed, not yet built):** a fold of delegate events is a
  disclosure button named for its whole summary, with `aria-expanded` (Windows: ExpandCollapse)
  for open or closed. Its "1 still working" link is a sibling after it in the tab order, never
  nested in the button, and opens that delegate. With none still working, there is only the
  button (§6.4). If focus is on the toggle or its link when the link goes, it moves to the
  fold's toggle while the fold stays; if the fold goes too, to that delegate's name in its
  line, or to the first delegate's name. A focused control no longer drawn counts as lost
  focus and follows the same rule. Focus anywhere else stays where it is, and the view never
  scrolls to follow it (A4, H3a). "*n* more" meets A11's size (§6.4).
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
- **Setup and Settings (#384):** these follow §9.1; what each surface adds is below.
  - **The stepper** is a list (`role="list"`) of links, the current one `aria-current="step"`.
    Each step is named for its label, state and line, joined with commas ("Hooks, needs you,
    Codex hook trust"; A1). The step's title is the level 1 heading, and its groups level 2.
  - **Focus** (A3, A4): when a step opens, focus lands on its title. Tab goes from there
    through the step's controls, then the footer (Save and exit, Back, Continue or Finish). The
    stepper is reached with Shift+Tab from the title, or F6. A disabled Finish stays
    focusable, its reason its accessible description ("Finish, unavailable, 1 needs you, 1
    blocked, 1 failed").
  - **Announcements** (A6): install progress and Verify each have one region that says changes
    of state, never percentages ("Installing Codex CLI", "Codex CLI installed", "Codex CLI
    install failed", "Check 6 of 11: Copilot CLI hook, working"), at most one every 5 s while a
    step runs; a failure isn't held back by that limit. The device code is a heading, its text
    spelled out for screen readers.
  - **Settings' agent rows** (A1, A2, A7; #678): each action is named for its row ("Set up
    Claude Code", "Repair hooks, Codex CLI", "Remove bridge integration, Copilot CLI"), with
    a key of its own. An action's progress and result are said from its own row's region (A6).
    When a row's verb goes (a row that reaches Working has none), A4's target is the row's next
    action, or else the row itself. Today's rows (§6.9): Install CLI is named "Install CLI for
    *Agent*" and, while its install runs, stays focusable and pressed with its ring (A4); Cancel
    beside it is named "Cancel CLI installation". A Read-only, couldn't-be-checked or Can't
    check row has no button, and its words are its name.
  - **The card for an agent installed later** (§6.9) is a group named for its line ("Claude
    Code 1.0.3 is installed"), its buttons named for the agent ("Set up Claude Code", "Not now,
    Claude Code"; A1). It never takes focus when it appears, and is announced once (A6), only
    while Overview is open. After Not now, A4's target is the next card, or else Overview's
    first row. The Agents item's dot is in its name ("Agents, new agent found").
  - **Keyboard:** arrows move within the stepper, an agent's tabs and toggle groups; Space
    toggles checkboxes and switches; Enter activates; Alt+← is Back; Esc closes the consent
    card without installing (A5). Checkboxes, switches and the combo box report their state,
    and every switch is named for its row ("Subfolders, C:\Projects\sample, off").
  - **High contrast** (A9; decision 11; [board](proposals/384-first-run/proposal-384-blocked-contrast.png)):
    Working's check ring is WindowText (CanvasText on the web), not Neutral's GrayText, which
    reads as disabled. Needs you and Danger take their system colours, and their shapes carry
    the state. The current step is Highlight with HighlightText ink.

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

The delegate events (#661) and the history (#292) are drawn on the Proposal #661
boards: before and after, the run fold and Earlier expanded, and history scrolled up, on a phone
and the desktop, dark and light ([proposals/661-delegate-events.md](proposals/661-delegate-events.md)).

The reply and answer receipt (#600, §6.5) is drawn on the Proposal #600 boards, decided by
the owner 2026-11-04 ([proposals/600-reply-progress/](proposals/600-reply-progress/)),
and revised the same day to "quiet unless something's wrong": Send's ring and check on the
normal path, the exception lines, "You answered · *value*" and older hosts, each in dark,
light, contrast and reduced motion.

First-run setup and Settings (#384, §6.8–§6.10) are drawn on the Proposal #384 boards: every
step, every Settings section, the compact layout, high contrast, the PWA phone's read-only view
and the readiness states ([proposals/384-first-run/](proposals/384-first-run/), on one
[contact sheet](proposals/384-first-run/contact-sheet.png)).

Coming back to the app (#950, §6.1) changes when things show, not how they look, so it has no
boards: its timeline and the captures its build must show are in
[proposals/950-calm-return.md](proposals/950-calm-return.md).
