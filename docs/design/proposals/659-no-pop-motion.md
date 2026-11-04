# Nothing pops in or out (#659)

**Status: Proposed, pending owner decisions D1–D6.** Nothing here is built. The proposed
text is in [ui.md §5.3](../ui.md#53-motion), marked as a proposal; the tokens are in
[design/tokens.json](../../../design/tokens.json) `motion.catalogue` (`shift` and `value`).
Everything shown uses sample data (machines WORKSTATION and BUILD-VM).

The owner's direction, given while looking at the Windows app's session view:

> Review the overall motion guidelines again. Nothing in our UI (anywhere, any platform) should
> pop in or out. Everything should at least have a gentle ease. For example in the Tools
> section new calls should ease in while the rest ease down. All these little details make
> everything feel more polished.

Today's spec (signed off in #232) deliberately did the opposite in two places: "A row arriving
fades in where it lands; its neighbours take their new places at once rather than sliding"
(principle 4), and a disclosure's "space opens at once: nothing below it slides". The reason
was §1's "rows never move on their own": a list whose rows slide looks as if it reordered
itself. This proposal keeps that reason and changes the rule. A neighbour eases only to where
the change put it, never past another, so the order still never changes on its own; what
changed is that nothing jumps any more.

Prototype: [659-no-pop-motion/prototype.html](659-no-pop-motion/prototype.html), one
self-contained file to open in any browser, with a "Today (pop)" / "Proposed" switch.

## 1. Audit

Every element that appears, disappears, changes size or changes value, on every surface ui.md
specifies. **All of it is code-reading** of `main` at 0d8c7cf9 (the Windows app in
`src/AgentBridge.App` and `src/AgentBridge.App.Core`, the PWA in `pwa/js/ui`); none was measured
on a running client. "Pops" means the element, or what is next to it, changes in one frame.

| Element | ui.md today | Windows app | PWA | Pops? |
|---|---|---|---|---|
| Session row arriving | §5.3 Arriving and leaving: fades in where it lands; neighbours at once | Fades in: `Motion.AfterLayout(…, Motion.Enter(row, "ListItem"))`, SessionsPage.xaml.cs:177; `ItemContainerTransitions` empty, SessionsPage.xaml:171-195 | Fades in: `enter(el, 'listItem')`, views.js:197 | The row fades; **neighbours jump** (as specified) |
| Session row leaving | Same row: "its space closes at once and it fades out where it was" | **No leave animation found**: the row is removed from the bound collection | A non-interactive copy fades where it was, views.js:125 | **Windows: pops, contrary to the spec.** PWA: fades; neighbours jump |
| Needs you | §6.1: a toggle (#253), not a lane; §5.3 Push: "the list's Needs you lane is there at once" | **Still a lane** (ListView.Header, SessionsPage.xaml:197-313, `HasNeedsYou`); the lane box pops by `Visibility`; only an entry's glyph fades, SessionsPage.xaml.cs:182-194 | Lane; only `.needs-mark` fades, motion.js:48-78; entries and commands appear at once (the pre-#253 exception in Arriving and leaving) | **Yes**, by design for safety; Windows' lane box pops too |
| Needs you · N count, "Showing N of M" | Ages, counts change in place | Text binding | Text replaced | Yes (as specified) |
| Ended group | §5.3 Disclosure | WinUI `Expander`, SessionsPage.xaml:321 (the control's own motion) | `<details>`; content enters with `disclosure` on open, motion.js:120-137; list rebuilt with `replaceChildren`, views.js:221 | Space opens at once (as specified); a row joining Ended pops |
| Pinned, Sessions headers | Not in §5.3 | Visibility toggles, SessionsPage.xaml:327-328 | `hidden`, sessions-page.js:732 | Yes |
| Machine tabs | §5.3 Tab switch (the indicator) | `SelectorBar` indicator | Indicator slides; the bar is rebuilt and shown with `hidden`, views.js:499-502 | Tab appearing or going pops |
| Sort change (#628) | §6.1 Sort menu; no motion for re-sorting | List re-populated | `replaceChildren`, sessions-page.js:702 (the menu); rows re-ordered at once | Yes |
| Header collapse (#620, open) | Proposed `collapse` token in PR #629 | Not built | Not built | n/a (#629 eases it) |
| Delegate strip chips | §5.3 Arriving and leaving names "a delegate in the strip" | `ListView` with empty transitions and no `Motion.Enter`, SessionView.xaml:181-234; status and facts swap | Items appended and removed in place, delegates-view.js:237-249 | **Yes; contrary to the spec on both clients** |
| "N finished" fold | Same | Text binding | `textContent`, delegates-view.js:224 | Yes |
| Your message | §5.3 Send and Answer: rises 12 and fades | `Acknowledge`, SessionView.xaml.cs:137 | `enter(e.user, 'acknowledge')`, session-card.js:707 | No |
| Main agent's response | Not in the design: streams with no fade | Text binding | Markdown replaced wholesale, session-card.js:691 | Grows in place (as specified) |
| The Now tool line | §6.4 Now; no motion | Visibility toggle | `hidden`, session-card.js:675 | Yes, and everything below it jumps |
| Timeline events ("Started delegate X · 6h", "reported back") | §6.4 Delegate events; no motion | `ItemsControl`, no motion, SessionView.xaml:346-381 | `replaceChildren`, session-card.js:755 | Yes |
| Show reasoning | §5.3 Disclosure: content fades 4 down; space at once | WinUI `Expander`, SessionView.xaml:335-342 | `<details>`; content `enter`s on open, motion.js:120-137; closing is at once | Space jumps, closing pops (as specified) |
| Question card | §6.4; §5.3 Push: "a session asking a question only fades in" | `QuestionCard` Visibility toggle, SessionView.xaml:262-266 | `replaceChildren` into the question slot, session-card.js:1146 | Yes |
| Answer receipt | §5.3 Send and Answer | `Acknowledge`, SessionView.xaml.cs:142 | `enter(…, 'acknowledge')`, session-card.js:1151 | No |
| "Not sent" and error notes | Not in §5.3 | Inline `InfoBar`, SessionView.xaml:269 | `hidden`, reply-view.js:245-251 | Yes |
| Tools list | §6.2 Details/Tools; no motion | `ItemsControl` in a `StackPanel`, ToolList.xaml; **rebuilt on every refresh**: `History.Clear()` then `Add` each of `TakeLast(50).Reverse()`, SessionCardViewModel.cs:381-386 | Refetched and rebuilt with `replaceChildren`, session-card.js:1385-1410 | **Yes: every call and every neighbour pops on each refresh** |
| A tool call's age | Ages change in place | Computed when the list loads (`Words.Ago`) | `ago()` when the row is built | Changes only on rebuild |
| Details values | §6.2; ages and counts in place | `ItemsControl` synced by key, SessionCardViewModel.Panes.cs:41-65; values are text bindings; type ramp is tabular (DesignResources.xaml:174-206) | Facts rebuilt with `replaceChildren`, session-card.js:836 | Yes (as specified) |
| Status words (row, header, strip) | §5.3 Status glyph: "The words change at once" | Glyph cross-fades, StatusGlyphView.xaml.cs:119-120; words bind | Glyph cross-fades, glyph.js:42-68; words set | Words pop (as specified) |
| Title weight (unread) | §5.3 Activity dot: "The title's weight changes at once" | Dot fades, SessionRow.xaml.cs:133-134 | Dot `enter`/`leave`, session-card.js:882-884 | Weight pops (as specified) |
| Banners (offline, update, replaced, stuck) | §5.3 Toast and banner: slides 12 in and fades, leaves the same way | `Motion.Banner`: enters with Toast, MainWindow.xaml.cs:43-44, SessionsPage.xaml.cs:26; **no exit**; content below jumps | `reveal()` enters, motion.js:110-114 (main.js:206, 229, 655); **hiding sets `hidden` at once**; content below jumps | **Leaving pops on both, contrary to the spec**; content below jumps on both |
| Host and offline notes | Toast and banner | `FleetNote`, an InfoBar in `ListNotes` (SessionsPage.xaml:153-160), enters through `Motion.Banner` (SessionsPage.xaml.cs:26) and closes at once | `hidden`, sessions-page.js:715-716, 762-763 | **Leaving, yes** (Windows); **both ways** (PWA) |
| Toasts | Toast and banner | System notifications (the OS's own) | None in-app | n/a |
| Menus (row, Sort, overflow) | §5.3 Menu and flyout | `MenuFlyout`'s own | `enter`/`leave` with `menu`, row-menu.js:50, 61 | No |
| Push, pane, tab | §5.3 rows | SessionsPage.xaml.cs:164-168, 542, 571; SessionView.xaml.cs:110-112 | sessions-page.js:621-669; session-card.js:966-971 | No |
| Usage bars and figures | Not in the design: "Usage bars take each reading at once" | n/a | Bars instant (app.css:431); summary rebuilt, usage-view.js:45-51 | Yes (as specified) |
| Empty-state notes ("No tool calls yet", "No sessions") | Not in §5.3 | Visibility toggles | `hidden`, sessions-page.js:788-793 | Yes |
| Launch view sections | Not in §5.3 | `InfoBar`, LaunchView.xaml:90 | `hidden` on many fields, launch-view.js:180-473 | Yes |
| Delegate inspector (Task, Latest, Tools, nested) | §6.3; Disclosure for Reasoning | `Expander`, DelegateInspector.xaml:111 | Blocks shown with `hidden`, delegates-view.js:417-473; tools rebuilt, :446 | Yes |
| Home Assistant cards | §2 rules apply to the card's words; no motion specified | n/a: HA's own frontend draws them | n/a | Out of scope (HA's motion) |
| Apple (iOS, macOS) and Android | §5.3 Platforms column | Not built | Not built | Spec only; this proposal fills in their columns |

### Where shipped code already contradicts today's spec

From the table, against the signed-off text (not this proposal):

1. **Windows: a leaving session row pops.** The spec says it fades out where it was.
2. **Windows and PWA: the delegate strip's chips pop.** Arriving and leaving names "a delegate
   in the strip".
3. **Windows: Needs you is still a lane,** where §6.1 (#253) made it a toggle, and the lane's
   box appears by `Visibility`.
4. **Windows and PWA: a banner leaves at once,** where Toast and banner says it "leaves the same
   way, quicker". The PWA's host and offline notes don't use `reveal()` at all.

Not a contradiction, but loose: the `disclosure` token has a `fast out` exit, which neither the
row's text ("closes the space at once") nor either client uses; closing just removes the content.

Tools, timeline events, Details values and empty-state notes aren't contradictions: today's
spec doesn't cover them, which is the gap the owner saw.

## 2. The rule

> **Nothing appears, disappears, or changes its size or its value without at least an ease,
> on any platform.**

It comes in four motions, three of them already in the catalogue:

| Change | What the person sees | Token |
|---|---|---|
| Something arrives | It fades in as its slot opens; its neighbours ease aside | `listItem` (the item), `shift` (the slot and neighbours) |
| Something leaves | It fades out where it was, quicker, as its slot eases closed | `listItem` exit, `shift` |
| Something opens or closes | The space eases open or closed; the content fades | `disclosure`, `shift` |
| A value changes | The old value cross-fades into the new, in place | `value` |

And the principles that hold it together:

- **Order never changes on its own.** A neighbour eases only from where it was to where the
  change put it. Nothing passes anything else, so §1's "rows never move on their own" holds:
  what moves is the space, not the order.
- **A change still takes effect at once** (principle 3). The new layout is real the moment
  the change comes; the motion draws the way there. A tap lands on where things are, and a
  second change retargets what is moving, from where it is, never queued behind it.
- **Bursts are one change.** What arrives in the same frame arrives together: one slot
  opening, one shift of the neighbours. What arrives while that is still easing joins it,
  retargeting the move from where everything is drawn, never queued behind it.
- **What the person reads doesn't move.** When they've scrolled away from the edge where
  things arrive (Tools' top, the conversation's end), the list keeps what they look at in
  place: the scroll is anchored, and a pill at that edge says "3 new" and takes them there.
- **Clocks aren't news.** Ages and elapsed times tick in place. A value that changed less
  than a second ago changes at once, so a busy value never strobes.
- **One exception:** a response streaming in grows as it arrives, as today: easing each chunk
  would lag the text behind the agent.

### 2.1 Arriving and leaving, with neighbours easing aside

An item arriving takes its place in the layout at once. Then:

1. its neighbours are drawn translated back to where they were and ease to none, `shift`
   (standard duration, standard easing: a move on screen eases at both ends, principle 5);
2. the item fades in over `listItem` (standard, decelerate) while its slot opens, clipped to
   the slot, so it never shows over its neighbours.

Leaving is the mirror: the item fades out over `listItem`'s exit (fast, accelerate) where it
was, as a copy nothing can find or tap, and its neighbours ease up into the space over
`shift`. Both start together, so the whole change ends inside 167 ms.

The item never slides in from the side, which is why WinUI's `AddDeleteThemeTransition` stays
out: its entrance is a slide from the leading edge with its own timing (D6).

### 2.2 High churn: Tools while an agent works

The owner's example. An agent at work can add a call every few hundred milliseconds, and
several within one frame after a pause. The rule for any list that grows at one edge:

- **Coalesce.** Calls that arrive in the same frame are one insertion: one block fades in,
  and the rest of the list eases down once, by the block's height. The prototype's burst of
  five shows a single shift.
- **Retarget, never queue.** A call arriving while the list is still easing starts a new
  shift from wherever each row is drawn, to its new place, decelerating so it never stalls
  or turns back. Nothing waits for anything.
- **Anchor when scrolled away.** When the list isn't at its top,
  what the person reads stays where it is: the new calls go in above, the scroll offset grows
  by their height in the same frame, and an "N new" pill at the top says how many came. Its
  count cross-fades (`value`). Pressing it scrolls to the top (`scroll`) and lets it go;
  scrolling to the top by hand does too.
- **Ages stop jittering.** A tool's age is tabular, and ticks in place on its own minute.

Today both clients rebuild the whole list on every refresh (SessionCardViewModel.cs:381-386;
session-card.js:1402), so the first implementation step is a keyed update that knows which
calls are new.

### 2.3 Disclosure

Opening Show reasoning (or Tool history, the finished delegates, Ended, the machine list, a
delegate's details):

1. the chevron turns (`disclosure`);
2. the content's space is laid out once, at its full height;
3. what is below is drawn translated back up by that height and eases down (`shift`);
4. the content is clipped to the opening space, fading in and settling 4 down
   (`disclosure`: standard, decelerate; nudge).

Closing: the content fades out over `disclosure`'s exit (fast, accelerate), then the space
closes in the layout and what was below eases back up over `shift`. What a person sees is the
space easing open and shut, but nothing is laid out again on every frame.

### 2.4 Value changes

A value that changes (D4 says which) cross-fades over `value` (fast, standard: 83 ms on
Windows and the PWA): the old text fades out as the new fades in, stacked in the same cell. The
cell keeps the wider of the two for the fade, so nothing beside it shifts, and numbers are
tabular (§4.3) so a count changing a digit doesn't reflow either. The title's weight going
from semibold to regular is two stacked copies cross-fading in the same way.

Not animated:
- the first value drawn (a list loading isn't a change);
- an age or elapsed time ticking ("just now" → "1m" → "2m"), since a clock moving isn't news;
- a value that changed less than 1 s before: it changes at once, so a value that changes on
  every update (a busy turn count) never strobes;
- a streaming response, as above.

### 2.5 Approvals and the click guard (#491, #253)

Today an approval's command never slides, and Needs you is "there at once", so a click on its
way to what was there can't land on Approve once. The 500 ms guard in Scroll to a question
already treats anything above a question changing height as a move, and restarts the wait.

With neighbours easing, moves get longer: a shift of up to 167 ms instead of a jump, and 250 ms
when a disclosure closes (its 83 ms fade, then the 167 ms shift). So:

- **anything easing above an answerable question is a move,** and the 500 ms wait counts
  from where it comes to rest, as a smooth scroll's does today (so at most 250 + 500 ms);
- **a key still acts at once,** as today;
- the question is what D3 decides: whether an approval's own card and command may now fade
  in where they land (Recommended), or stay at once as today.

In every option the command's own text never travels on its own: no slide, no per-line
fade, nothing a reader has to chase.

### 2.6 Reduced motion

Under each platform's setting, nothing travels: `shift` is instant (neighbours take their
new places at once), the disclosure's space opens at once, and items and values cross-fade
over `crossFade` (83 ms on Windows and the PWA) or change at once, as each entry's `reduced`
says. The "N new" pill still appears (it is information, not motion), cross-faded.

### 2.7 Performance

- **Compositor only.** Every motion here animates opacity, a translation or a clip. Height is
  never animated: the layout changes once, and what moved is drawn from its old place to its
  new (FLIP in the PWA; composition `Offset` animations in WinUI, which don't touch layout or
  hit testing; SwiftUI and Compose animate their own placement).
- **A clip, not a height.** The opening space is a clip on a wrapper (`clip-path: inset()` in
  CSS; a composition `InsetClip` in WinUI), so the content isn't laid out per frame. CSS's
  `grid-template-rows: 0fr → 1fr` is the simpler fallback, at a layout per frame for one
  element; the prototype uses the clip.
- **Bounded work.** Coalescing means a busy Tools list does one measure and one animation set
  per frame at most, and only rows on screen (plus one screen beyond) animate.
- **limitMs holds.** Every entry stays within 300 ms. The longest chain is a disclosure
  closing: an 83 ms fade, then a 167 ms shift, 250 ms in all.

## 3. Tokens

Two catalogue entries, in catalogue order (after `listItem` and after `activityDot`, so they
don't collide with `lift` from #605 or `collapse` from #629, which go after `reorder`):

```json
"listItem":    { "duration": "standard", "easing": "decelerate", "exit": { "duration": "fast", "easing": "accelerate" }, "reduced": "crossFade" },
"shift":       { "duration": "standard", "easing": "standard", "reduced": "instant" },
...
"activityDot": { "duration": "standard", "easing": "decelerate", "exit": { "duration": "fast", "easing": "accelerate" }, "reduced": "instant" },
"value":       { "duration": "fast", "easing": "standard", "reduced": "crossFade" },
```

| Entry | Windows and PWA | Apple | Android | Reduced |
|---|---|---|---|---|
| `shift` | 167 ms, `(0.33, 0, 0.67, 1)` | spring 0.25 s, damping 1 | 200 ms, `(0.2, 0, 0, 1)` | instant |
| `value` | 83 ms, `(0.33, 0, 0.67, 1)` | 150 ms, easeInOut | 100 ms, standard | cross-fade |

`disclosure` and `listItem` keep their values; what changes is that their space is now timed by
`shift`. A `shift` that interrupts one still running restarts from where each item is drawn on
`decelerate` instead of `standard`, so a moving item never slows to a stop and starts again;
the catalogue has no field for this, so it is written into ui.md's Easing aside row. No new
durations or easings: both entries use the existing tiers. tools/design-gen
generates `--ab-motion-shift-*`, `--ab-motion-value-*`, `AbMotionShift*` and `AbMotionValue*`;
nothing reads them until the clients implement this. (App.Core's `MotionCatalogue.Names` still
lists 15 entries; the Windows issue adds the two.)

## 4. Prototype

[659-no-pop-motion/prototype.html](659-no-pop-motion/prototype.html): one file, no network.
The switch at the top flips between **Today (pop)**, what the clients do now, and **Proposed**.
Reduced motion and light/dark are switches too, and the page follows the system's setting at
first. Durations are the Windows and PWA tokens.

- **(a) Tools.** "1 call", "Burst of 5", and "Stream" (a call every 120–400 ms, with
  occasional bursts). Scroll the list down and calls keep coming above without moving what
  you read; the "N new" pill takes you back.
- **(b) Sessions.** "Add row", "End a row" and "Ask a question" (the Needs you count and its
  entry). Rows fade in and their neighbours ease down.
- **(c) Show reasoning,** with the turn's lines below easing as the space opens and closes.
- **(d) Details,** with "Change values": Status, Model, Turns, Compactions and Updated
  cross-fade; a second change inside 1 s goes at once.
- **(e) A banner** ("BUILD-VM is offline") that pushes the page down and back.

Tested with Playwright 1.56 in Chromium and WebKit
([prototype.spec.mjs](659-no-pop-motion/prototype.spec.mjs); run it from `pwa/` with
`npx playwright test --config ../docs/design/proposals/659-no-pop-motion/playwright.config.mjs`):

- in Proposed, a new Tools call is mid-fade 40 ms in (opacity strictly between 0 and 1) and
  the row below it is mid-move (a non-zero translation); in Today, both are already at rest;
- a burst of five calls makes one shift: the rows below move once, by five rows' height;
- a call arriving mid-move retargets it: sampled every frame, the rows below only move down,
  and end two rows lower;
- with the list scrolled away, a call arriving keeps the row in view at the same place on
  screen, the pill says "1 new", and pressing it returns to the top and hides it;
- reduced motion: every animation started (Tools, rows, values, banner, reasoning) has no
  translation or clip and lasts at most 83 ms;
- a session row leaving fades where it was (an inert copy) while the row below eases up;
- Needs you arrives with its first entry as one insertion, the entry fades without travel,
  and its count cross-fades for the next;
- Show reasoning's space eases (the line below it is mid-move), and closing too;
- a Details value cross-fades without the cell narrowing, and a second change inside 1 s is
  at once;
- the banner pushes the page down with an ease, and leaves the same way;
- nothing runs past 300 ms;
- and no test leaves a script error on the page.

## 5. Implementation notes per platform

- **Windows (WinUI 3).** A `Motion.Shift` helper in the app: before a change, record each
  realized container's position; after layout, set each container's visual `Translation`
  to its old place and animate to zero with the `AbMotionShift*` resources (or an
  `ImplicitAnimationCollection` on `Offset` for an `ItemsRepeater`). The list's
  `ItemContainerTransitions` stays empty. Tools moves from `Clear`/`Add` to a keyed merge, and
  inserts in batches per 167 ms. A leaving row needs a stand-in: a `Visual` snapshot
  (`CompositionVisualSurface`) or keeping the container until its fade ends. Values: a
  `CrossFadeText` control holding two `TextBlock`s. Banners: `Motion.Banner` gains an exit.
  `AnswerGuard` (App.Core/Motion.cs) already counts a scroll offset change; the shift adds
  "re-arm when a shift above a question comes to rest".
- **PWA.** `motion.js` gains `flip(container, mutate)`: measure the children, run `mutate`,
  measure again, and `animate()` each moved child's translate from the old place, cancelling
  and retargeting a running one from its current transform. Tools and timeline events move
  from `replaceChildren` to a keyed merge. `reveal()` gets a `conceal()`, and every banner and
  note uses them. `watchDisclosures` gains the space (clip) and the exit fade. `watchMoves`
  (session-card.js) already sees geometry changes; it needs to count a running shift as moving
  until it ends.
- **Apple (later).** `withAnimation(.spring(response: 0.25, dampingFraction: 1))` around list
  changes, `.transition(.opacity)` on items, `.contentTransition(.opacity)` on values,
  `ScrollViewReader` and `.scrollPosition` for the anchor.
- **Android (later).** `LazyColumn` with `Modifier.animateItem(fadeInSpec, placementSpec,
  fadeOutSpec)` timed by the tokens, `AnimatedVisibility` with `expandVertically` for
  disclosures, `Crossfade` for values, and `LazyListState.requestScrollToItem` to keep the
  anchor.

## 6. The ui.md text

The proposal edits §5.3 in place, with a banner at the top of the section saying so:

- principle 4 becomes "Nothing pops, and rows never move on their own";
- Disclosure: the space eases (`shift`) and the content fades out, quicker, on closing;
- Arriving and leaving: covers Tools, the strip, the conversation's lines and Needs you; the
  neighbours ease aside; bursts arrive together; approvals per D3;
- **Easing aside** (new, `shift`): what moves a neighbour, retargeting, the anchor and "N new",
  and the click guard;
- Status glyph and Activity dot: the words and the title's weight cross-fade;
- **Value change** (new, `value`): which values, the 1 s cap, and what never animates;
- Toast and banner: what is below eases, and Windows' exit;
- "Motion on data updates": ages tick in place, counts cross-fade, usage bars ease.

Principle 5 and the Reorder row, which #605 changes, are untouched.

## 7. Decisions for the owner

**D1. How neighbours move.**
- **A. Ease at both ends: `shift`, standard duration (167 ms Windows and PWA, 200 Android,
  the 0.25 s spring on Apple), standard easing. (Recommended)** A neighbour is already on
  screen, so it is "a move on screen" in principle 5's terms, and matches Reorder.
- B. Decelerate: starts at full speed and settles. Snappier, but a row that leaps off its
  place reads as a jump at 167 ms.
- C. Emphasis duration (250 ms): smoother, but a busy Tools list spends most of its time
  moving.

**D2. High churn and the scroll anchor.**
- **A. Coalesce arrivals in the same frame into one insertion; retarget moves in flight
  (later arrivals join the move); when scrolled away, anchor what is read and show an "N new" pill. (Recommended)**
- B. Coalesce, but no anchor: the list eases down under the reader even when scrolled away.
  Simpler, but what they read slides away while they read it.
- C. Throttle: at most one insertion per 300 ms, the rest queued. Smooth, but lags the agent
  and breaks "never waits for itself".

**D3. Approvals and the click guard.**
- **A. Approvals ease like everything else, but never travel of their own: the card and its
  command fade in where they land, the neighbours ease aside, and any shift above an
  answerable question re-arms the 500 ms wait from where it comes to rest. Needs you entries
  the same. (Recommended)** The guard already handles moves; the fade is opacity only, so
  the command is never read moving.
- B. Keep today's exception: an approval's card, its command and Needs you entries appear at
  once, and while an answerable question is on screen, shifts above it are instant. Safest,
  but the one place that still pops is the one the person is looking at.
- C. Everything moves, the card sliding 12 in like a toast. Most consistent, but a sliding
  command is harder to read, and the guard has to cover the card's own travel.

**D4. Which values cross-fade, and how often.**
- **A. Words, counts and the title's weight cross-fade over `value` (83 ms); ages and
  elapsed times tick in place; a value that changed under 1 s ago changes at once; usage bars
  ease to each reading over `value` (a scale). (Recommended)**
- B. As A, and ages cross-fade too, at most once a minute each.
- C. As today: values change in place; only the glyph cross-fades.

**D5. The disclosure's space.**
- **A. The space eases: laid out once, what is below eased over `shift`, the content clipped
  and fading. (Recommended)**
- B. The space eases with CSS's `grid-template-rows: 0fr → 1fr` and WinUI's `Expander` as is:
  simpler, with a layout per frame.
- C. As today: the space opens and closes at once; only the content fades.

**D6. The platforms' own transitions.**
- **A. Keep each control's own motion where the control owns it (WinUI `Expander`,
  `MenuFlyout`, `NavigationView`; iOS `NavigationStack` and sheets; Material's), and draw list
  arrivals, shifts and value changes ourselves from the tokens on every platform, since
  `AddDeleteThemeTransition` and `RepositionThemeTransition` slide items in from the side with
  their own timing. (Recommended)**
- B. Replace them all with our own for one feel everywhere. More to build and maintain, and
  the app stops moving like the controls around it.
- C. Use the platform's list transitions (`AddDeleteThemeTransition`, SwiftUI's default,
  Material's) as they come. Least work, but each platform's list feels different and Windows'
  slides from the side.

## 8. Every board

| Board | Dark | Light |
|---|---|---|
| Tools: a call arriving, today and proposed; a burst; scrolled away | [proposal-659-tools-strip.png](659-no-pop-motion/proposal-659-tools-strip.png) | [proposal-659-tools-strip-light.png](659-no-pop-motion/proposal-659-tools-strip-light.png) |
| Disclosure: Show reasoning opening and closing, and a Details value changing | [proposal-659-disclosure.png](659-no-pop-motion/proposal-659-disclosure.png) | [proposal-659-disclosure-light.png](659-no-pop-motion/proposal-659-disclosure-light.png) |
| Decisions D1–D6 | [proposal-659-decisions.png](659-no-pop-motion/proposal-659-decisions.png) | [proposal-659-decisions-light.png](659-no-pop-motion/proposal-659-decisions-light.png) |

The sources are `design/canvas/Proposal-659-*.dc.html`, on the canvas under "#659 · Nothing
pops". To render them: from `tools/design-mockups`, `npm install`, then
`node render.js ../../design/canvas ../../docs/design/proposals/659-no-pop-motion Proposal-659-`.

## 9. What follows

Once the owner decides, the proposal banner comes off §5.3 and these issues get filed:

1. **Windows app: nothing pops.** `shift` and `value` in App.Core's catalogue; a `Motion.Shift`
   helper; leaving rows fade (fixing contradiction 1); the strip's chips fade and shift (2);
   Tools as a keyed list with coalescing, the anchor and "N new"; timeline events, the Now line
   and the question card arrive and leave; banners leave (4) and push; Details values and
   status words cross-fade; the click guard re-arms on a shift.
2. **Windows app: Needs you as a toggle** (3), if not already tracked under #253.
3. **PWA: nothing pops.** `flip()` and `conceal()` in motion.js; Tools and timeline events as
   keyed lists; the disclosure's space and exit fade; banners and notes through
   `reveal`/`conceal` (4); value cross-fades; usage bars ease; `watchMoves` counts a shift; and
   motion.spec.mjs tests for each.
4. **Apple and Android:** nothing to file until they are built; their columns in §5.3 are
   the spec.

## 10. Overlap with open PRs

- **#605** (the reorder spec, #592) changes principle 4's neighbour wording, adds `lift` after
  `reorder` and edits §6.1. This PR rewrites principle 4 too; whichever lands second keeps
  both ideas (a reorder's passed row now eases with `shift` rather than "at once").
- **#629** (the compact header, #620) adds `collapse` after `reorder`. No conflict: `shift`
  and `value` sit earlier in the catalogue.
- `design/canvas/canvas.json` is one line in every PR; the #659 boards sit below #620's
  (y 32000 on), so a rebase only joins the entries.
