# #592: reorder by lifting the card while the others make room

> **Decided (owner, 2026-11-04).** The owner approved the recommended option for all seven
> decisions in §14 (D1–D7), recorded on #592 and #605. §13's text is now in
> [ui.md](../ui.md) (§5.3 Lift and Reorder, §6.1 Moving, §9), and the `lift` motion token is
> in `design/tokens.json`. ui.md is the spec and wins wherever this page and it disagree. The
> PWA (#593), Windows (#594) and native (#595) work follows.
>
> **Corrected (2026-11-04)** from the implementations, #633 (PWA) and #637 (Windows), and the
> design track's answers on #593 and #594: the order is committed on release and the settle
> is only drawn (§5, §12.1); under reduced motion only the held card follows the pointer, and
> the hint only fades (§10); a press during the settle acts on the card now in that place
> (§5); the Pin chip is a solid Bridge fill (§6); the drag shadow is two tokens both clients
> read (§3, §11); the empty Sessions placeholder (§6); Windows' pen-only haptic (§9.1); the
> grip's column only in Your order (§9.1); the hint shows once by itself (§9.1, D5); the
> answer guard (§10); and a recycled held card (§5). The prototype follows them.

*Written for the owner by the design track, and decided on 2026-11-04.* Issues:
[#592](https://github.com/danswett/agent-bridge/issues/592) (the interaction),
[#593](https://github.com/danswett/agent-bridge/issues/593) (PWA touch),
[#594](https://github.com/danswett/agent-bridge/issues/594) (Windows) and
[#595](https://github.com/danswett/agent-bridge/issues/595) (Apple and Android). It uses the
Slipgates visual language from [#558](https://github.com/danswett/agent-bridge/pull/558): the
S glyph family for session states, with glyphs at 20 px in rows. Only the reorder interaction
changes.

The mock-ups are in [592-reorder/](592-reorder/), with every board on one
[contact sheet](592-reorder/contact-sheet.png). They are rendered from boards in
[`/design/canvas`](../../../design/canvas) (`Proposal-592-*.dc.html`, all drawn by
`Proposal-592-Frame`) with [tools/design-mockups](../../../tools/design-mockups/README.md).
There is also an interactive [prototype](592-reorder/prototype.html) (§12). The demo machines
are ULTRA and DEV-VM1, and every session is made up.

## 1. The problem

ui.md §5.3 has a drag where "the row follows the pointer, a line shows where it will land …
No other row moves". In practice:

- **The PWA can't be reordered by touch at all.** `#drag` in `pwa/js/ui/views.js` handles
  mouse and pen only. On a phone a long press opens the menu, and Move up and Move down are
  the only way to move a session, one place at a time.
- **On Windows the drag is the system's OLE drag.** `SessionRow` sets `CanDrag` and fills
  `DragUIOverride` with a translucent snapshot and a "Move below" / "Move above" caption. The
  owner calls the ghost and the caption "pretty awful". The rows don't react, so the eye has
  to find a thin line to see where the drop will land.
- **Nothing feels physical.** The thing you hold isn't the card; it's a picture of it, or a
  line.

The owner's direction for #592: **the whole card lifts, and the other sessions ease out of the
way to make room as you drag it.**

## 2. Principles

1. **You hold the card itself.** No ghost, no copy, no caption. The card you pressed rises
   above the list and stays under your finger or pointer.
2. **The list answers the hand, not the clock.** The card moves 1:1 with the pointer, with no
   easing and no lag. Only the neighbours animate, and only because the card passed them.
3. **The gap is the landing place.** The neighbours part to leave a gap exactly where the card
   will land. There is no insertion line to read.
4. **One motion for every way of moving.** Drag, Alt+↑/↓, Move up and Move down, and
   screen-reader actions all part the list the same way, with the existing `reorder` token.
5. **It still never moves on its own.** This amends §5.3 principle 4 narrowly: rows slide only
   *while the person is moving one*. Arrivals and departures keep their fade, and the
   neighbours of an arriving row still take their places at once.
6. **Native first.** Each platform keeps its own drag and long-press timing, haptics and
   accessibility actions. The look (lift, gap and settle) is shared.

## 3. The lift

The lift is the moment the card becomes the thing in your hand. It runs over `lift` (§11:
`fast`, `decelerate`: 83 ms on Windows and in the PWA, 150 ms on Apple and 100 ms on Android).

| Property | At rest | Lifted |
|---|---|---|
| Scale | 1 | **1.02** about the card's centre (D1). 1.02 grows a 340 px card by 7 px, which is enough to read as raised without covering the row above or below. 1.03 reads as "zoomed" on a phone. |
| Shadow | none | The drag shadow, from two tokens (§11): `0 8 16` in `surface.dragShadow` plus `0 0 2` in `surface.dragShadowEdge`. Dark: black at .28 and .24. Light: .14 and .12. That is Fluent's 16 dp shadow, the height of a flyout. Windows draws it as a Composition `DropShadow` on the card from `AbDragShadow` and `AbDragShadowEdge` (a `ThemeShadow` would shadow the group header too), and the PWA as a `box-shadow` from `--ab-drag-shadow` and `--ab-drag-shadow-edge`, so the two lifts look the same. |
| Surface | the row's fill (transparent, or `selected`) | **`card` with a 5% Bridge tint** (4% in light): `color-mix(card 95%, bridge)`. It reads as the same card, picked up. A selected card keeps its `selected` overlay on top. |
| Edge | none | A 1 px `stroke2` outline, so the card's edge holds on a busy list in light theme. |
| Corner | `radius.control` (4) | `radius.card` (8 on Windows and in the PWA; 10 on iOS, the cell radius). |
| Grip | In Your order only, in a reserved 24 column: `text3` on touch; hidden until hover with a mouse. No grip and no column in other sorts | `text`, with the cursor `grabbing` |
| Selection pill | the 3×16 Bridge pill, if selected | It stays and moves with the card. Selection doesn't change. |
| Z-order | in the list | Above every row and header. Below the app bar, the title bar, menus and toasts. Lifting never covers the chrome. |

**High contrast (forced colours).** Forced colours drop shadows and backgrounds, so the lift
can't rely on them. The lifted card gets a **2 px `Highlight` outline**, inset, and no shadow
or tint. The scale stays. The gap is a 1 px dashed `GrayText` outline, and the header cue and
the chip use `Highlight` and `CanvasText`. On WinUI that is `SystemColorHighlightColor` (the
generator's `hc` table already maps these). See the
[Access board](592-reorder/proposal-592-access.png), frame 1.

## 4. The gap, the neighbours and the pointer

- **The card follows the pointer 1:1,** with no easing, from the offset where it was picked
  up. It moves vertically only in one column (horizontal drift is ignored), and freely in a
  grid (§8).
- **The gap.** The place the card left becomes the gap: an empty slot one card tall, with a
  `subtle` fill and an inset `stroke` hairline at `radius.card`. As the card moves, the gap
  moves to where it would land. **The insertion line is removed.**
- **Neighbours make room.** The landing slot is the slot whose centre is nearest the card's
  centre, with a 1 px hysteresis so it doesn't flicker on a boundary. When it changes, every
  card between the old gap and the new one slides one slot (one card height plus the list's
  row gap) toward the old gap. They use **`reorder`** (`standard`, `standard` easing: 167 ms on
  Windows and in the PWA, 250 ms on Apple and 200 ms on Android). A slide that is interrupted
  retargets from where it is; it never queues.
- **Nothing else moves.** Headers stay fixed unless the gap crosses into the other group (§6).
  The list's height doesn't change, and the scroll position moves only by auto-scroll (§7).
- **Updates wait.** While a card is lifted, a host's changes to the list (arrivals, departures,
  status) apply to the model at once but are drawn only after the drop. A status glyph inside a
  row still cross-fades. If the lifted session itself is removed by its host, the drag cancels
  and the card fades out where it is. If a virtualized list recycles the held card's view
  mid-drag, the card is put back rather than carried on, and that is announced as a cancel.

## 5. Release, settle and cancel

**Drop.** **The order is committed at release.** The card then **settles from where the
pointer left it into the gap** over `reorder` (D7), and lowers at the same time: scale to 1,
shadow and tint out, and the corner back to 4. The settle is only drawn. The session is in its
new place while the card is still settling (§5.3 principle 3), so a key or a menu during it
acts on the committed order and nothing overwrites it afterwards. A press during the settle
acts on the card now in that place: it is resolved against the committed layout, not against
cards still sliding there.

**Cancel.** The card goes back to where it came from, with the same settle, and the
neighbours close the gap there:

- **Escape** while lifted.
- **Dropping outside the list.** When the pointer is more than 32 px outside the list (beside
  it, or past the bar), the gap goes back to the origin, and a neutral **Cancel** chip with a ×
  replaces the age. Coming back inside resumes the drag.
- **`pointercancel`** (the system took the pointer), losing the window's focus, the page being
  hidden, or a second touch point (a pinch).
- The lifted session ending, or the sort leaving Your order.
- The list recycling the held card's view (§4).

Announced: "Cancelled. Back at position *N* of *M*."

## 6. Groups: Pinned and Sessions

ui.md already says a row dragged into the other group is pinned or unpinned. This makes it
visible before the drop. See the [Pinned board](592-reorder/proposal-592-pinned.png).

- **Headers never lift, and never swap** with a card. While the gap is in the card's own group,
  that group's header stays put, and the lifted card may pass over it.
- **Crossing.** When the nearest slot is in the other group, the gap opens there: at the end of
  Pinned from below, or at the top of Sessions from above. The header between eases one slot
  with `reorder`, because its group grew or shrank. Two cues show what the drop will do:
  - The **target group's header turns Bridge** (`Highlight` in forced colours) and gains
    "· drop to pin" or "· drop to unpin".
  - A **Pin or Unpin chip** replaces the card's age: a solid Bridge fill with the on-Bridge
    text (`bridge.onDark`, `onLight`; `--ab-bridge-on`). The proposal's Bridge text on a 14%
    Bridge fill gave about 4.2:1 in light, under 4.5:1.
- **Drop** pins or unpins exactly as the menu would, at the dropped position. Announced:
  "Pinned. Position 3 of 3 in Pinned." or "Unpinned. Position 1 of 10 in Sessions."
- **The last pinned card leaving** leaves a dashed placeholder, "Drop here to keep it pinned",
  so the group stays open to drop back into. It disappears on drop. **The last card in Sessions
  leaving** for Pinned leaves the mirror of it, "Drop here to unpin".
- **With nothing pinned there is no Pinned header, so a drag can't pin.** Pin is in the menu.
  (A drop zone that appears from nowhere mid-drag would move the whole list.)

## 7. Auto-scroll near the edges

See the [Edges board](592-reorder/proposal-592-edges.png).

- **Zone:** 10% of the list's visible height, clamped to 40–80 px, at the top and the bottom
  of the list (below the app bar, above any bottom bar).
- **Speed:** `v = 1000 px/s × (depth ÷ zone)²`. That is a quarter of the speed halfway in, and
  1000 px/s at the edge. A pointer beyond the edge counts as full depth. Speed is applied per
  frame (`requestAnimationFrame`, or the platform's frame callback) as `v × frame time`, and
  fractions carry over, so it is smooth at any frame rate.
- **Arming:** only after the card has moved 4 px since the lift, so lifting a card that already
  sits in the zone doesn't start a scroll.
- While the list scrolls, the landing slot is re-evaluated each frame, so the cards the card
  passes keep making room. Leaving the zone stops the scroll at once (no momentum).
- Reduced motion doesn't change auto-scroll: it is the person's own movement.

## 8. Wide screens: two columns

The session list is one column on every client today. #592 asks for room for a grid, so the
rule is set now. **If** the list flows into columns on a wide window, the order reads **left to
right, then down** (row-major), within each group. Headers span the full width. The gap is one
card, and the cards between the old and new gap shift one position along that order. A card
at the end of a line moves to the start of the next with the same `reorder` slide (diagonally,
not out and back in). The nearest slot is by the distance between centres in both axes. See
[Edges](592-reorder/proposal-592-edges.png), frame 2, and the prototype's Columns 2 setting.

## 9. Input

### 9.1 Touch

See the [Touch board](592-reorder/proposal-592-touch.png).

- **Long press to lift** anywhere on the card (D3). **iOS 500 ms**: UIKit's drag-lift and
  context-menu delay. **Android 400 ms**: `ViewConfiguration.getLongPressTimeout()`'s default,
  which Compose's `detectDragGesturesAfterLongPress` uses. **PWA:** the same per platform,
  detected from the user agent (500 elsewhere) (D2). On the grip, a **150 ms** hold or a 4 px
  move lifts, because the grip never scrolls (`touch-action: none`). Android's 400 ms is the
  default since Android 12; older versions use 500. **Windows** holds 500 ms on touch, and
  starts a drag after 10 px on touch and 4 px with a mouse.
- **During the hold** the card shows `selected` at once (Press) and sinks to 98.5% over the
  hold, linearly, so the hold has visible progress. With reduced motion it doesn't sink.
- **Scroll or drag.** Moving more than **10 px** (the existing `LONG_PRESS_SLOP`) before the
  hold completes is a scroll. The hold is cancelled and nothing lifts. The card sets
  `touch-action: pan-y`, so until the lift the browser owns the scroll natively. After the lift
  the page stops scrolling (a non-passive `touchmove` calls `preventDefault`), and the list
  auto-scrolls instead.
- **Hold, then release without moving** opens the row's menu, exactly as a long press does
  today. Moving after the lift drags. One gesture serves both.
- **Haptic:** one light tick at the lift (D4). That is `UIImpactFeedbackGenerator(.light)` or
  `.sensoryFeedback(.impact(weight: .light))` on iOS, `HapticFeedbackType.LongPress` on
  Android, and `navigator.vibrate(10)` in the PWA where it exists (Android Chrome; iOS Safari
  has no web vibration). No tick per slot. **On Windows only a pen ticks:** WinUI has no
  haptics API for a touch screen, which is a platform limit with no substitute (no sound and
  no extra motion).
- **Suppress the system's own long press:** `-webkit-touch-callout: none`, `user-select: none`,
  `-webkit-user-drag: none` and `-webkit-tap-highlight-color: transparent` on the card, and
  `contextmenu` prevented for touch. There is then no iOS callout, loupe or link preview, and
  no Android text selection.
- **The browser takes the pointer.** On iOS Safari and Android Chrome, a long press can end
  in `pointercancel` even though the touch continues. The PWA then follows the same touch's
  `touchmove`, `touchend` and `touchcancel` until it ends; only a cancel without a touch
  stream cancels the drag.
- **The grip stays visible on touch,** in `text3` (D5), so moving is discoverable. It, and the
  24 column it sits in, exist only in Your order. The column is reserved there on touch and
  with a mouse alike, so rows don't shift as the grip comes and goes. In Most recent and
  Machine there is no grip and no column, and the row's content stays where it is today.
- **The hint** is a toast, "Press and hold a session to move it. Or use Move up and Move down
  in its menu." It **shows once by itself**, the first time a touch user sees the list in Your
  order with at least 2 sessions that can move, and a flag on the device stops it showing
  again. **Tapping the grip shows it** every time. It never shows in another sort, or when a
  filter leaves fewer than 2.

### 9.2 Mouse and pen

- **The grip** shows on hover (as today) and has the `grab` cursor. Pressing it lifts at once.
- **The card body** lifts after 4 px of movement with the button down (D3). A click without
  movement still opens the session, and a right-click still opens the menu.
- Pointer capture starts at the lift, not at the press. Capturing at the press would retarget
  the click.

### 9.3 Keyboard and menu

Alt+↑/↓ on a focused row, and Move up or Move down in the menu, move the session one place.
They use **the same make-room motion**: the moved card and the neighbour it passes trade places
together with `reorder`. Today only the moved row slides and the other jumps. There is no lift,
because nothing is being held. Focus stays on the moved card, and it is scrolled into view
(`nearest`) if it left the viewport. At the first or last place of a group, Alt+↑/↓ crosses
the boundary only if Pin or Unpin would. Today's rule (moves stay within the group) is kept,
so it does nothing and announces "Top of Pinned." or "End of Sessions.".

## 10. Accessibility

- **Announcements** (a polite live region in the PWA; `RaiseNotificationEvent` with
  `MostRecent` on Windows; `AccessibilityNotification.Announcement` on Apple;
  `announceForAccessibility` or a live region on Android):
  - lift: "Picked up *title*, position *N* of *M* in *group*.";
  - while dragging, debounced 500 ms and only when the slot changes: "Position *N* of *M*." or
    "Pinned, position *N* of *M*. Drops as pinned.";
  - drop: "Moved to position *N* of *M* in *group*.", "Pinned. Position *N* of *M* in Pinned."
    or "Unpinned. …";
  - cancel: "Cancelled. Back at position *N* of *M*.", also when the list recycles the held
    card (§4);
  - the held session ending: "*title* ended. Move cancelled.", as the others close the gap
    with `reorder` (instantly under reduced motion). The prototype's Simulate switch shows it.
- **Touch screen readers.** VoiceOver and TalkBack can't long-press-drag reliably, so each
  card exposes the menu's moves as **custom actions**: Move up, Move down and Pin (or Unpin).
  These are `accessibilityAction(named:)` on Apple, `customActions` in Compose semantics, and
  in the PWA the row's menu (Shift+F10, or a double-tap-and-hold, which opens it as today).
  Each is announced as above. See [Access](592-reorder/proposal-592-access.png), frame 4.
- **Reduced motion** (see Access, frame 2):
  - no sink, no scale and no shadow; the lift is a 2 px Bridge outline on `card`;
  - the gap and neighbours jump to their places, the drop is instant, and the cancel is
    instant (D6);
  - only the held card moves, following the pointer, because that is the person's own
    movement, not animation (as §5.3 Sheet already does);
  - the hint toast only fades, in place, rather than sliding in.
- **Answers wait.** A move must never land a tap on a Needs you answer that slid under the
  finger. The lift arms the answer guard (§5.3 Scroll to a question), it is held through the
  drag, and it lasts 1 s after a drop, a cancel, an abandoned hold or a keyboard or menu move.
- **The Pin chip's contrast:** on-Bridge text on a solid Bridge fill passes 4.5:1 in both
  themes (§6).
- **High contrast:** §3.
- **Hit targets:** in Your order, the grip's hit area is the full card height and 24 px wide.
  The card is ≥ 44 px tall on touch.

## 11. Tokens

Decided: `motion.catalogue.lift` is a token, because both clients time the lift from the
generated motion (`--ab-motion-lift-*` in the PWA, `AbMotionLift*` on Windows). **The drag
shadow is two tokens too** (corrected), because both clients came to read it:
`surface.dragShadow` (the `0 8 16` key shadow; dark black at .28, light .14) and
`surface.dragShadowEdge` (the `0 0 2` edge; .24 and .12), with `WindowText` in high contrast,
where the outline replaces them. They generate `--ab-drag-shadow` and `--ab-drag-shadow-edge`
for the PWA's `box-shadow`, and `AbDragShadow` and `AbDragShadowEdge` for Windows'
Composition `DropShadow`. The chip uses the existing `bridge.onDark` and `onLight`. The
other values stay in ui.md's text. The long press, its slop and the haptic are each
platform's own, so the native clients read them from the system. The PWA keeps them as
constants beside its long-press menu's. The lift scale and the tint (a mix of two existing
tokens) are used by the PWA and Windows lifts alone. The table below is the original
proposal, kept for the record; its `shadow.drag` became the two `surface` tokens:

| Token | Value | Use |
|---|---|---|
| `motion.catalogue.lift` | `fast`, `decelerate`; reduced: `instant` | The lift: scale, shadow, tint, corner |
| `motion.catalogue.reorder` | **unchanged**: `standard`, `standard`; reduced: `instant` | Neighbours, the settle, the cancel, keyboard moves |
| `shadow.drag` (Windows and PWA, dark) | `0 8px 16px rgba(0,0,0,.28), 0 0 2px rgba(0,0,0,.24)` | The lifted card. WinUI: a Composition `DropShadow` (corrected from `ThemeShadow`) |
| `shadow.drag` (light) | `0 8px 16px rgba(0,0,0,.14), 0 0 2px rgba(0,0,0,.12)` | |
| `drag.liftScale` | 1.02 | D1 |
| `drag.longPress` | iOS 500, Android 400 ms (the platforms' own) | D2 |
| `drag.slop` | touch 10, mouse 4 px | |
| `drag.autoScroll` | zone 10% (40–80 px); 1000 px/s; exponent 2 | §7 |

The drag shadow is new because `surface.*.shadow` (one colour at .18) has no geometry; the
geometry stays in ui.md's text. Apple and Android use their system list's lift shadow rather
than this.

## 12. The prototype

[prototype.html](592-reorder/prototype.html) is one self-contained file, with no
dependencies and no network. It implements this page's behaviour on a list of 12 demo sessions
in Pinned and Sessions, with the S glyphs drawn inline. Open it in any browser. On a phone, use
the link in the PR, or serve the folder and open it over the local network.

- **Pointer Events** for mouse, pen and touch: the touch long press, neighbours easing out of
  the way, the gap, auto-scroll, crossing the Pinned boundary, cancel outside the list,
  Escape, `pointercancel` handed over to touch events, hold-and-release opening the menu, a
  tap on the grip showing the hint, and Alt+↑/↓.
- **Tune** (the panel at the bottom): sliders for lift scale, shadow strength, long-press ms,
  neighbour duration, drop settle and lift duration. There are also switches for theme, high
  contrast, motion (System, Reduce or Full), one or two columns, and the haptic. The sort
  button in the bar switches to Recent, which hides the grips. **Copy values** puts the
  settings on the clipboard as JSON, to paste into a reply on #592.
- `prefers-reduced-motion` and `forced-colors` are honoured. The toggles simulate them where
  the device can't switch.

**Tested with Playwright 1.56** (Chromium and WebKit):

- desktop mouse in dark and light: grip drag, body drag past 4 px, click still opens, crossing
  to pin, Escape and outside-the-list cancel, Alt+↓, auto-scroll, two columns;
- reduced motion: outline-only lift, 0 ms neighbours;
- Chromium with Pixel 7 touch emulation: the 400 ms Android long press; moving before the lift
  cancels it; hold-and-release opens the menu and keeps the order; the menu's Move down; the
  grip hint; the `pointercancel` handover to `touchmove`; `touchcancel`;
- WebKit with iPhone 14 emulation: the 500 ms iOS timing, the long press and move, and no
  callout or text selection.

WebKit on Windows can't construct `Touch`, so the handover test runs in Chromium only. Every
run ended with no page errors.

**Corrected and tested again** (2026-11-04), with tests that fail on the earlier prototype:
the order is committed at release, so Alt+↓ pressed during the settle keeps both moves; a
press during the settle opens the card now in that place; the hint keeps its place and only
fades under reduced motion; the Pin chip is a solid Bridge fill with on-Bridge text; Recent
has no grip and no grip column; and the hint shows once by itself on touch in Your order, then
only on a grip tap. Also: dropping the last pinned card settles straight into the final layout,
with Pinned closed, and a key move while a card is lifted puts the card back first and is
kept.

Screenshots of the prototype mid-drag:

| | |
|---|---|
| [Touch drag, dark (Chromium, Pixel 7)](592-reorder/prototype-touch-drag-dark.png) | [Touch drag, light](592-reorder/prototype-touch-drag-light.png) |
| [Touch drag, WebKit (iPhone 14)](592-reorder/prototype-webkit-touch-drag-dark.png) | [Mouse drag, light](592-reorder/prototype-mouse-drag-light.png) |
| [Crossing to pin](592-reorder/prototype-pin-cross-dark.png) | [Reduced motion](592-reorder/prototype-reduced-motion.png) |
| [Two columns](592-reorder/prototype-grid-dark.png) | |

### 12.1 Per-platform implementation notes

**PWA (#593).** Replace `#drag` and the insertion line (`app.css`'s drag rules) with one Pointer
Events state machine on the list: idle, then pending (the hold), then lifted, then settling.

- **Neighbours:** a `transform: translateY()` per card with `transition: transform
  var(--ab-motion-reorder-duration)` and `--ab-motion-reorder-ease`. The DOM order changes only on drop, in one `#moved` call that
  replaces today's FLIP.
- **The lifted card:** `position: fixed` (or a transform layer above the list) with
  `will-change: transform`, moved in `pointermove` by `translate3d`, with no transition.
- **Drop:** commit at release, in one `#moved` call, then settle: a transition from the
  pointer to the gap's rectangle that is only drawn. A key, a menu or a press during it acts
  on the committed order, and a press is hit-tested against the committed layout.
- **Shadow:** `box-shadow: 0 8px 16px var(--ab-drag-shadow), 0 0 2px
  var(--ab-drag-shadow-edge)`.
- **Wiring:** `setPointerCapture` at the lift; `touch-action: pan-y` on cards and `none` on
  the grip; a non-passive `touchmove` listener while lifted; and the `pointercancel` handover
  in §9.1.
- **Order:** `session-order.js` gets a single `move(id, group, index)` so a cross-group drop is
  one change.
- **The long-press menu:** `LONG_PRESS_MS` and `LONG_PRESS_SLOP` stay. The menu opens on
  release if nothing moved.

**Windows (#594).** Drop `CanDrag`, `DragStarting`, `DragUIOverride` and the "Move
below/above" caption, and stop using OLE drag-and-drop for an in-list move. Instead:

- **Pointer handling:** handle `PointerPressed`, `PointerMoved` and `PointerReleased` on the
  grip and the row, with `CapturePointer` at the lift (4 px, or at once on the grip). Touch
  uses a `Holding`-free timer of 500 ms.
- **The lift:** animate the lifted container's `Scale` and `Translation` through
  `ElementCompositionPreview` / `Visual` properties. Draw the shadow as a Composition
  `DropShadow` on the card's visual from `AbDragShadow` and `AbDragShadowEdge` (corrected: a
  `ThemeShadow` would shadow the group header too). Follow the pointer by setting
  `Translation` X/Y each `PointerMoved` (never `Visual.Offset`, which XAML layout owns), with
  no animation, and raise the container's `Canvas.ZIndex` above the others.
- **Neighbours:** a `Vector3KeyFrameAnimation` on each displaced container's `Translation`,
  using `reorder`'s duration and its `CubicBezierEasingFunction` (0.33, 0, 0.67, 1). Read both
  from `Motion`.
- **Commit, then settle:** `ObservableCollection.Move` once at release, then a settle that is
  only drawn, from the pointer to the card's new place. The list keeps the selection rules in
  `SessionsPage` (the reorder is still a remove and an insert to `ListView`). If `ListView`
  recycles the held container mid-drag, put the card back and announce a cancel.
- **Haptic:** only a pen ticks. WinUI has no haptics API for a touch screen, and nothing
  replaces it.
- **Auto-scroll:** `ScrollViewer.ChangeView(null, offset, null, disableAnimation: true)` per
  `CompositionTarget.Rendering` frame.
- **Escape and `PointerCaptureLost`** cancel. Announce with `RaiseNotificationEvent`.
- **High contrast:** a 2 px `SystemColorHighlightColor` border instead of the shadow.
- **Hover:** the grip stays as today: shown on hover, and always in touch mode.

**Apple (#595).** On iOS and iPadOS, use SwiftUI `List` with one `ForEach` per group and
`.onMove`, so the system provides the lift, the parting and the haptic. Put cross-group moves
through `.onMove` on a single `ForEach` with the Pinned header row `.moveDisabled(true)`, or use
`.draggable`/`.dropDestination` between sections. Add `.accessibilityAction(named:)` for Move
up, Move down and Pin, and `.sensoryFeedback(.impact(weight: .light), trigger:)` at the lift.
macOS: the same `List` `.onMove` drag. Its lift is the system's, and only the gap behaviour must
match.

**Android (#595).** In a `LazyColumn`, use `detectDragGesturesAfterLongPress` on the item. The
lifted item draws through `graphicsLayer { scaleX = 1.02f; scaleY = 1.02f; shadowElevation =
8.dp.toPx(); translationY = drag }` with a raised `zIndex`. The neighbours use
`Modifier.animateItem(placementSpec = tween(200, easing = standard))`, with the model reordered
live, or an offset animation per item if the model waits for the drop. Add
`HapticFeedbackType.LongPress` at the lift and `semantics { customActions = … }`. Auto-scroll
with `LazyListState.scrollBy` per frame.

## 13. The ui.md text

The text as proposed. ui.md now carries it, expanded: the lift is its own catalogue row,
because each `motion.catalogue` token has one, and the touch, edge and cancel rules are
spelled out under Moving. ui.md also carries the corrections at the top of this page, which
the quotes below predate. Where the two differ, ui.md wins.

**§5.3, principle 4**, amended:

> 4. **Rows never move on their own** (§1, principle 2). Only the thing that changed animates.
>    A row arriving fades in where it lands; its neighbours take their new places at once
>    rather than sliding, so the list never looks as if it reordered itself. The one exception
>    is a person moving a row (Reorder): then the rows it passes ease aside to make room.

**§5.3, the Reorder row**, replacing today's:

> | Reorder | The session list (#164, #592) | A long press (touch) or a drag (the grip at once, the card after 4 px); Alt+↑/↓; Move up or Move down; a screen reader's actions | **Lift:** the card rises: scale 1.02, the drag shadow, a 5% Bridge tint on `card`, an 8 corner. It stays above the rows and below the bar. **Drag:** the card follows the pointer 1:1, with no easing. The cards it passes ease one slot aside, so a gap opens where it will land; there is no insertion line. Crossing into the other group moves its header one slot, turns the header Bridge with "drop to pin" or "drop to unpin", and puts a Pin or Unpin chip on the card. Near the list's edge it scrolls (§6.1). **Drop:** the card settles from the pointer into the gap and lowers. **Cancel** (Escape, outside the list, `pointercancel`): it settles back. **By key, menu or action:** the card and the card it passes trade places together | `lift`: fast, decelerate; then `reorder`: standard, standard | Windows: pointer capture and Composition (`Scale`, `Translation`, `ThemeShadow`), not OLE drag, `CanDrag` or `DragUIOverride`, and no `ReorderThemeTransition`. PWA: Pointer Events, with transforms on the tokens. Apple: `List` with `.onMove`. Android: `detectDragGesturesAfterLongPress` with `animateItem` | No sink, scale or shadow: a 2 px Bridge outline marks the lifted card. The gap, the neighbours, the drop and the cancel are instant. The card still follows the pointer |

**§6.1, the Moving bullet**, replacing today's:

> - **Moving** is the person's only:
>   - a long press on a card (500 ms on iOS, 400 ms on Android; 150 ms on the grip), which
>     lifts it, and then a drag;
>   - with a mouse or pen, a drag by the grip that shows on hover, or by the card once it has
>     moved 4 px;
>   - Alt+↑/↓ on a row;
>   - the row menu, opened by a long press released without moving, a right-click, or
>     Shift+F10 / the menu key. Its items are Move up, Move down and Pin (or Unpin).
>
>   While a card is held, the cards it passes ease aside, and the gap they leave is where it
>   lands. Moving 10 px before a long press completes scrolls instead. Keys and menu moves stay
>   within the row's group. A card dragged across a group's header is pinned or unpinned, and
>   the header says which before the drop. Near the list's top or bottom edge (10% of its
>   height, 40–80 px) the list scrolls, faster the nearer the edge. Escape, a drop outside the
>   list, or the system taking the pointer puts the card back. On touch the grip stays visible,
>   and tapping it explains how to move a session.

**§9, Keyboard**, add after "Alt+↑/↓ reorders (#164)": "with the same make-room motion as a
drag (§5.3), announcing the new position".

## 14. Decisions

Decided (owner, 2026-11-04): the recommended option, listed first, for every decision.

**D1. Lift strength.**
1. **Scale 1.02 with the 16 dp drag shadow and a 5% tint (recommended; decided).** It reads as raised
   on a phone and a desktop without covering the rows around it. Fluent's flyout height.
2. Scale 1.03 with the same shadow. More dramatic; the card overlaps its neighbours by 1–2 px
   at phone widths.
3. No scale; shadow and tint only. The calmest, but on a phone the finger hides most of the
   shadow, so the lift is easy to miss.

**D2. Long-press duration.**
1. **The platform's own: iOS 500 ms, Android 400 ms; the PWA matches the device (recommended; decided).**
   It feels the same as every other long press on that phone.
2. A flat 500 ms everywhere. Simpler; slightly sluggish on Android.
3. A flat 400 ms. Snappier; risks lifting during a slow scroll start on iOS.

**D3. What lifts on touch.**
1. **A long press anywhere on the card, or a short hold on the grip (recommended; decided).** The whole
   card is the target, as in iOS and Android lists. The grip is a faster, scroll-free handle.
2. The grip only. No conflict with the long-press menu, but a 24 px target is easy to miss,
   and the menu then needs its own long press anyway.

**D4. Haptic.**
1. **One light tick at the lift only (recommended; decided).** It confirms the hold without buzzing
   during the drag.
2. A tick at the lift and a lighter one each time the gap moves (iOS's picker feel). Busy on
   long moves.
3. None.

**D5. The grip on touch.**
1. **Visible on every card in `text3`, and tapping it shows the hint (recommended; decided).** Moving is
   discoverable, and the grip doubles as a fast handle (D3). As built: the grip is in Your
   order only, and the hint also shows once by itself, the first time a touch user sees Your
   order with 2 or more sessions that can move (§9.1).
2. Hidden on touch; shown only while a card is lifted. A cleaner row; moving is undiscoverable
   except by the menu.

**D6. Neighbours with reduced motion.**
1. **They jump; reduced motion animates nothing in a reorder except the card following the
   pointer (recommended; decided).** That matches §5.3's Reorder row today ("Instant") and the Sheet's
   rule.
2. They cross-fade into place (`crossFade`, 83 ms). Softer, but a fade of a moving list is
   still motion, and it costs a second copy of each card.

**D7. Drop settle duration.**
1. **`reorder` (167 ms on Windows and the PWA, 250 ms Apple, 200 ms Android) (recommended; decided).**
   The same token as the neighbours, so the card and the gap arrive together.
2. `emphasis` (250 ms Windows and PWA). Heavier and more deliberate; a long drop can feel
   sluggish.
3. `fast` (83 ms). Crisp, but from far away the card appears to jump.

## 15. Every board

Each board is drawn in dark and light. All are labelled "#592 · Decided (owner,
2026-11-04)".

| Board | Dark | Light |
|---|---|---|
| PWA phone: rest → press → lifted → dragging, neighbours parted → drop settle | [phone-strip](592-reorder/proposal-592-phone-strip.png) | [light](592-reorder/proposal-592-phone-strip-light.png) |
| Windows desktop: the same five moments, with a mouse | [windows-strip](592-reorder/proposal-592-windows-strip.png) | [light](592-reorder/proposal-592-windows-strip-light.png) |
| Crossing the Pinned boundary: pin, unpin, the last pin | [pinned](592-reorder/proposal-592-pinned.png) | [light](592-reorder/proposal-592-pinned-light.png) |
| High contrast, reduced motion, Alt+↓, screen-reader actions | [access](592-reorder/proposal-592-access.png) | [light](592-reorder/proposal-592-access-light.png) |
| Touch: the grip's hint, a swipe scrolls, the hold, hold-and-release menu, no callout | [touch](592-reorder/proposal-592-touch.png) | [light](592-reorder/proposal-592-touch-light.png) |
| Auto-scroll, two columns, cancel outside, the speed curve | [edges](592-reorder/proposal-592-edges.png) | [light](592-reorder/proposal-592-edges-light.png) |

To render them again, from `tools/design-mockups`:

```
node render.js ../../design/canvas ../../docs/design/proposals/592-reorder Proposal-592-
```

## 16. What follows

1. **ui.md and tokens:** done on #605, and corrected there from #633 and #637.
2. **#593, the PWA:** the Pointer Events reorder (§12.1). That fixes touch.
3. **#594, Windows:** the Composition reorder (§12.1). That retires the ghost and the caption.
4. **#595, Apple and Android:** the native lists' drag, matched to §3–§10 when those clients
   are built.
