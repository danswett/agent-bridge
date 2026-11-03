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
2026-11-02; the reasoning is in [proposals/253-attention.md](proposals/253-attention.md).* The
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

Motion: `working` turns once per 1.2 s, and `delegating`'s satellites drift once per 8 s.
Both are still under reduced motion. Nothing else animates on its own: every other motion
answers the person or a change, and is in the catalogue in §5.3.

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
| 2 | A delegate has a pending question (`waitingOn`, or the question's `agent`) | `needs` | "Delegate *name* asks", or "… · answer at the machine" when the host can't prove where the answer lands (`answerAtMachine`). The word "Delegate" is always there, so no name reads as "Main agent asks" (§6.3). A delegate the list left out is "A delegate asks" |
| 3 | The main agent is working | `working` | "Working", or "Working · task: Delegate *name*" while a foreground task agent runs (a running `kind: task` sub-agent whose starters are all task agents too; one a background delegate started is that delegate's work) |
| 4 | Main agent idle, background agents running | `delegating` | "Delegating to N agents", N from `background.agents` (a nested delegate counts once, under its parent, and a list cut short still counts them all) |
| 5 | Main agent idle, only background commands | `delegating` | "N background commands" |
| 6 | Nothing running | `idle` | "Idle" |
| 7 | **Overlay.** Any of the above while an agent drives | the same glyph plus `driven-badge`; `working` and `delegating` turn from active to agent | The same words, in agent colour, with "Driven by an agent" on rows |
| 8 | **Refines rule 4.** The agent can't list its sub-agents (a host older than `subagents`, or an agent whose transcript counts its delegates without saying which they are): `background.agents` counts them but neither `subagents` nor `subagentsMore` lists any | `delegating` | "Delegating to N agents · details not reported". Nothing is ever wrongly attributed |

While the host ends a session (`status: ending`), none of rules 1–6 apply: the row shows the
`ended` glyph and "Ending session…" in secondary text, and rule 7's overlay still applies.

While a session's machine is away (#269), none of the rules apply: what the row last said may no
longer be so. It shows the `failed` glyph (a host unreachable, §2.1) and, in secondary text,
"Offline · last seen 2m ago" once the machine has left the relay's presence, or "Reconnecting…"
while it is listed but its link is down; rule 7's overlay does not apply. A row's line 2 is
"Offline · *machine*". Nothing on it can be acted on (§6.1, "A machine away").

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
| Agent | `#A78BFA` | `#6D4AE0` | Driven by an agent, and a prompt an agent or a schedule sent (§6.4), and nothing else |
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
    character (IoskeleyMono configuration, a tailed `l`, a normal 600 width), in
    [`/design/fonts/agent-bridge-mono`](../../design/fonts/agent-bridge-mono/README.md).
    It was first built at 560 to fit more per line, but beside the system face that read
    as condensed, so it was rebuilt at 600, close to Cascadia Mono's and Consolas'
    proportions (#295).
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
- **Inline mono** (mono on a line of text, #215) sits as if it were the same face:
  1. **Matched x-height.** It is scaled so its x-height matches the text face's: the scale is
     the text face's x-height over Agent Bridge Mono's, per platform, from `tokens.json`
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

Letter-spacing follows each platform's own tracking: SF's per-size values on Apple and
Material 3's on Android. Never add tracking to Segoe UI.

## 5. Space, shape, layout and motion

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

- **Which session:** the one chosen, or with none chosen yet on a desktop, the first row.
  A phone shows the list until one is chosen.
- **The inspector's toggle** is remembered on the device.
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
| Disclosure | Show reasoning, Show prompt, Tool history, the finished delegates, the Ended group, the machine list | Opening or closing it | The chevron turns, and the content fades in and settles 4 down. Its space opens at once: nothing below it slides. Closing turns the chevron back and closes the space at once | `disclosure`: standard, decelerate; fast out; nudge | Windows: `Expander`'s own motion where it is one. PWA: the tokens on the chevron and the content | Instant |
| Arriving and leaving | A session row (also as Needs you shows or hides it), a delegate in the strip | A host adds or removes it; a question is asked or answered; Needs you is turned on or off | The item fades in where it lands. Leaving, its space closes at once and it fades out where it was. Neighbours never slide | `listItem`: standard, decelerate; fast out | Windows: the list's `ItemContainerTransitions` replaced by this fade, because `AddDeleteThemeTransition` and `RepositionThemeTransition` slide the neighbours. PWA: the tokens on the row; a copy of a leaving row, which nothing can find or tap, fades where it was. Until #253 reaches the PWA, its Needs you entries and an approval's command appear at once and never fade or move: only an entry's mark fades in. Apple: `.transition(.opacity)`. Android: `animateItem` with no placement spec | Cross-fade |
| Status glyph | A row, the session header, the delegate strip | The state changes, such as working → idle | The old glyph cross-fades into the new one, with its colour. The words change at once. The arc and the orbit turning are §2.1's, not this | `glyph`: fast, standard | The same on every platform: opacity on the two glyphs | Instant |
| Activity dot | A row's trailing edge (§6.1) | The session updates while its row isn't open; the row is opened | The 6 px dot fades in, and later fades out. It never pulses, grows or repeats. The title's weight changes at once | `activityDot`: standard, decelerate; fast out | The same on every platform | Instant |
| Selection | The list's 3×16 Bridge pill; the rail's pill | Choosing a row or a destination | The pill slides from the old row to the new one when both are on screen, and fades in when the old one isn't. The row's fill changes at once | `selection`: standard, standard | Windows: `NavigationView`'s own pill on the rail, and the same motion on the list. Apple: the system's selection | Instant |
| Press | Every button, and every row | Pointer or touch down, and release | The pressed fill shows at once on press, and eases back on release. Nothing scales | `press`: fast, standard | Windows: the controls' own visual states. PWA: a transition on the background. Apple: the system button styles. Android: the ripple | Instant |
| Send and Answer | The reply box; a question card | Send, Answer, Approve once, Deny | Your message shows at once as your bubble, rising 12 as it fades in. An answer's outcome fades in on the card. While the request is in flight, the button shows the platform's progress ring, which stops with the request. Nothing waits for the motion to finish | `acknowledge`: standard, decelerate; small | Windows: `ProgressRing`. PWA: the answer's ring. Apple: `ProgressView`. Android: `CircularProgressIndicator` | Cross-fade; the ring is still |
| Toast and banner | In-app toasts; banners such as a host going offline | The event it reports | It slides 12 in from its edge and fades in, then leaves the same way, quicker | `toast`: standard, decelerate; fast out; small | Windows: `InfoBar` with the tokens. System notifications are each OS's own | Cross-fade |
| Sheet | The delegate sheet on iOS (§6.3), and any phone sheet | Opening a delegate; dragging between detents; dismissing | The sheet rises from the bottom edge and the scrim fades in. While dragged it follows the finger with no easing, and on release it settles at the nearest detent | `sheet`: emphasis, decelerate; standard out | iOS: `.sheet` with `.presentationDetents([.medium, .large])` and the system's spring. Android: `ModalBottomSheet`. PWA: the tokens on transform | Cross-fade at the detent. A drag still follows the finger |
| Menu and flyout | The row menu, Sort, the overflow menu, flyouts | A click, a long press, Shift+F10 or the menu key | It fades in and settles 4 from its anchor. Choosing an item acts at once, and the menu fades out | `menu`: fast, decelerate; fast out; nudge | Windows: `MenuFlyout`'s own. PWA: the tokens, on a popover where supported. Apple and Android: the system menus | Instant |
| Scroll to a question | The open session's conversation, and the list when a notification opens a session | A question arrives while the person is at the end of the conversation; a toast, the tray, the menu bar, a waiting row or (in the PWA, until #253 reaches it) a Needs you entry opens a session | The view scrolls smoothly to the question or the row. It never scrolls a conversation the person has scrolled back through (a response growing below on its own doesn't count: the reader is still at its end), is touching, or has the pointer over, nor one in a page or app in the background. For 500 ms after a question appears in a conversation on screen (arriving, shown as its session opens, shown again as its tab, the Sessions page or the app comes back (the window taking the focus back included, visible all along or not), moved as the inspector is shown or hidden or the window crosses a layout width, moved where it is by anything above it changing height (the title wrapping, a tool line, the response, the reasoning or your message, a kept draft dismissed or an answer's receipt going), by the conversation being pulled back as it gets shorter, or sideways as the window is narrowed or widened, or becoming answerable as its machine's hold lifts or its machine starts taking answers), or is scrolled into view (a notification's answer focused included), a click or tap on its choices, Approve once, Deny or Send is ignored, so a click already on its way to what was there can't answer it. Needs you's Approve once, Deny and Resend wait the same way after the lane changes (an entry arriving, going or moving, and its answers moved by an entry above growing as its answer goes, by anything above the lane changing height, a banner or the app bar appearing included, or by the list being pulled back as rows below get shorter or go while it is scrolled to its end; an answer's status appearing below the last answers, or the list scrolling, moves nothing and doesn't restart it) or comes back on screen, and while a session opened from it scrolls the list to its row. A smooth scroll to it counts from where it comes to rest; a scroll the person makes after that doesn't restart the wait, unless something above the answers changes height in the same moment, which counts as a move (the scroll and the move can't be told apart). A key acts at once | `scroll`: emphasis, standard | Windows: `StartBringIntoView` with `AnimationDesired`. PWA: `scrollIntoView` with smooth behaviour, at the browser's pace. Apple: `ScrollViewReader`. Android: `animateScrollToItem` | Instant: a jump |
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
  session's header or Details, and the row reads in full to a screen reader (§9).

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
- **Your messages:** a bubble at the right edge of the conversation's column (card fill,
  platform radius), at most 85% of the column's width, so a short message beside a short reply
  is still at the right (#255).
- **A prompt you didn't type** (#489): a schedule's (`activity.userMessageFrom` `schedule`), another
  agent's (`agent`), or one the host knows only as not yours (`other`, and any value a client
  doesn't know). It is **never a bubble**, and never at the right, which is yours only. A prompt
  from you, and every prompt from a host that doesn't send the field, keeps the bubble.
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
  - **Repeats fold.** When a schedule's prompt has the same words as that schedule's previous one,
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
  radio, checkbox and text controls, with Answer in Bridge, or Approve once (Bridge) and Deny.
  - **One card per question id:** an update to the same question changes its card in place,
    and never adds a second.
  - **An approval is drawn as it runs,** since Approve once allows all of it: exactly as
    written, in the code face, never read as Markdown, with its hidden characters,
    right-to-left letters and bare carriage returns escaped and the code notes beside it, and
    no Copy ("An approval's card", below, #220). Approve once stays offered whatever it holds,
    the owner's "escape and allow" (#253). It wraps and is never cut, so a long command or a
    diff keeps Approve once.
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
  fenced `echo rm -rf ~` inside `bash -c '…'` runs `rm -rf ~`. A Codex approval carries its
  command in prose ("Codex wants to run: …"). Other questions and the conversation keep their
  Markdown.
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

### 6.5 Reply box

- **Width:** the conversation's column, the pane's width up to the reading measure (§4.2), whatever
  has been typed (#294), and centred under it in the pane, as the PWA's is (§6.4).
- **Placeholder:** "Message the main agent". Replies only ever go to the main agent.
- **Controls:** Attach, then Send (Bridge, icon only, with a tooltip naming the shortcut:
  Ctrl+Enter, or ⌘↩ on Apple).
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
  held says "Discovery held", in the version's place. The host's reason is never on the row: it
  is the line's tooltip and its accessible description. No device id on the row.
- **Inside the row, under its two lines, in its text column:** the machine's updates (what its
  app found, how the last update ended, Check and Install) and, for an offline machine, Forget.
  Every action is the same small secondary button. Check for updates, for every machine at once,
  sits under the rows in the same column.
- **A machine away (§6.1):** listed once, as offline ("Offline", and "Last seen 2m ago" on line
  2), with Forget, both while its rows are kept and after they have gone. A client whose relay
  keeps no roster lists it so only while its rows are kept.

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
- **Other questions keep their buttons** where the platform has them and they fit: the options
  of a single choice, or a text box and Send. Each still checks, before sending, that the
  question is the one shown.
- The count each platform shows is Needs you's N (§6.1).

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
- **Tray and toasts:** the tray's tooltip and the rail's Sessions badge carry N ("Agent
  Bridge · 3 need you"). A question's toast follows the rules above: an approval's toast has
  Open only.

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

**App icon (#167):** the mark ([`mark.svg`](../../design/icons/mark.svg)) on a dark plate
(`#0F1F22`), in each platform's container. It is one icon in every theme, since no platform
swaps an app icon with the theme, so the mark is always Bridge's dark value. The sizes are in
`tokens.json` `appIcon`, on each container's own grid, and `tools/design-gen` draws every
file from them ([design/README.md](../../design/README.md#app-icon)).
- **Windows:** a tile: a rounded square with radius 14 on a 72 grid, a 1-unit edge
  (`#23464A`) inside it so the tile still shows on a dark taskbar, and the mark 44 across. At
  24 px and under, the tile has radius 3 on a 16 grid, no edge, and the mark 13 across, so
  the ring stays a pixel thick. The app's `.ico` holds every size Windows asks for, from 16 to
  256, and serves the exe, the window, the tray and the installer.
- **macOS:** a rounded square on Apple's 1024 grid, 824 across with radius 185 and the mark
  504 across. Its drop shadow comes with the asset catalog (P5).
- **iOS:** full bleed and opaque, with the mark 44 across on a 72 grid. The system masks it
  to its squircle.
- **Android:** an adaptive icon on the 108 dp grid: the plate is the background layer, the
  mark 40 across is the foreground, and the mark in white is the monochrome layer for themed
  icons.
- **PWA:** the Windows tile as the favicon and the manifest's `any` icons (192 and 512), the
  iOS icon at 180 as `apple-touch-icon`, and maskable icons (192 and 512) that are full bleed
  with the mark 35 across on a 72 grid. That keeps the mark inside the safe zone at Android's
  proportion.

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
- **A question's words** appear in the conversation only, never on a row, in Needs you, in
  Tools, in the inspector or in a menu.
- **Numbers before nouns:** "3 delegates", "1 background command".
- **Approval notifications** say "waiting for your approval" and offer only Open (§7): an
  approval is approved in the app, never from a notification.
- **No sentences explaining the UI,** no exclamation marks, and no emoji.
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

- **Contrast:** every text pair meets 4.5:1 and every glyph 3:1, in light, dark and high
  contrast.
- **Touch targets:** at least 44×44 on touch platforms, 32×32 on desktop.
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
- **Needs you (#253):**
  - The toggle reports its pressed state, and is named for its count and scope: "Needs you, 3
    on every machine", or "Needs you, 1 on ULTRA, 3 on every machine" with a machine chosen.
  - A change in N is announced once, politely ("3 need you"), at most every 5 s, and not while
    the person types in the reply box.
  - The question's heading (level 3) is where focus lands, and its accessible description is
    the question's first line. Approve once and Deny are named for their session and machine
    ("Approve once: *title* on *machine*").
  - In high contrast, the toggle takes the system's button colours (ButtonFace and ButtonText,
    and Highlight and HighlightText when pressed). The `needs` glyph is Highlight with
    HighlightText ink, swapped on the pressed toggle's Highlight fill so its disc still shows.
    The Tools link is a system hyperlink.

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

The states #253 added (the toggle's states, rows, the question card's forms, Tools and the
delegate inspector) are drawn on the Proposal #253 boards, which stay on the canvas as their
reference ([proposals/253-attention/](proposals/253-attention/)).
