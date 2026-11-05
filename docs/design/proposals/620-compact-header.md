# Proposal #620: the phone session header collapses as you scroll

> **Revised (owner, 2026-11-05): the at-rest header shrinks too.** From a phone screenshot, the
> owner called the at-rest header "a massive waste of space". A first ruling kept a strip of
> working delegates sharing the tabs' row. A second, at 12:05 PT, replaced it: **no delegate
> strip on a phone at all**, since it repeats the Delegates tab ("Every element should have a
> purpose").
>
> 1. One meta line, never wrapped.
> 2. The Delegates tab carries the running count and the `needs` glyph ("Delegates, 2 running,
>    1 asking"). With none running, there is no count.
> 3. A delegate's question stays in the conversation, naming the delegate.
> 4. The tabs keep their own row. At rest: the title, the meta line, the tabs.
> 5. Collapse on scroll as below (D1, D2).
>
> D3–D6 are superseded for the header. The design is in [At rest,
> revised](#at-rest-revised-2026-11-05). It measures **152 pt at rest** on a 390 × 844 phone,
> against Product's estimate of about 150.
>
> **Decided (owner, 2026-11-04).** The owner chose every recommended option:
>
> - **D1:** collapse on scroll, riding with the finger near the top, then 64 pt up to come back
>   and 32 pt down to go.
> - **D2:** the bar holds Back, the glyph, the title on one line, and More.
> - **D3–D6:** everything at rest, and the bottom, stays as it is today. *(Superseded on
>   2026-11-05 for the header, above. The reply box and notice still stay as they are.)*
>
> The text in [the ui.md section](#the-uimd-text) is now in [ui.md](../ui.md) §5.2, §5.3, §6.2,
> §6.3 and §9, and the `collapse` motion is in `tokens.json`, both from #629. **ui.md is the
> spec.** This page stays as the reasoning behind it, and where the two differ, ui.md wins.
> The PWA builds it in [#631](https://github.com/danswett/agent-bridge/issues/631).
> Issue [#620](https://github.com/danswett/agent-bridge/issues/620). The prototype is
> [prototype.html](620-compact-header/prototype.html): open it on a phone.

## The problem

On a phone, an open session gives about half the screen to fixed chrome. The owner's screenshot
(the PWA on an iPhone, dark) shows all of this, top to bottom:

- a two-line title;
- a meta line on two lines;
- the delegate chips;
- the Conversation / Delegates / Tools tabs;
- at the foot, a two-line "answer first" notice and a two-line reply box.

The conversation got about 45%.

The owner narrowed the issue: *"Normally it's fine, although it does seem like maybe if I start
scrolling all that stuff at the top should collapse."* So this proposal is about **scrolling**:

- **At rest, the header stays exactly as it is today.**
- Scrolling down slides it away under a one-line bar, and scrolling back up brings it back.
- Changing the at-rest header (the title size, the chips and tabs) and the bottom area are
  optional, secondary decisions. Each recommends keeping today's design.

## Principles

1. **The conversation is the content; the header is a way back to context.** While you read, the
   header should cost one line. When you want it, it should be one gesture away.
2. **Nothing you are reading moves.** The header slides off the conversation, and comes back
   over it. The text under your finger never jumps, reflows or loses its place.
3. **Never hide what matters now.** A pending question stays visible in the bar as the `needs`
   glyph. Focus is never hidden.
4. **Native where it exists** (§5.3). iOS has the large title, which collapses to an inline title.
   Android has the collapsing top app bar. Both behaviours are combined here.

## Measurements

Today's PWA was rendered with Playwright, in demo mode, at four phone sizes (CSS viewport, touch,
`isMobile`). The session had a two-line title, a delegate chip row (two delegates still running)
and a pending question. The heights were read from the elements on screen.

The **conversation** is the scrolling area between the tabs and the reply area. *Scrolled* is the
reading state: past the top of the turn, or on the way to the question.

| Viewport | Header · chips · tabs today | Reply area | Conversation today, at rest and scrolled | Proposed at rest | **Proposed, scrolled** (48 pt bar) | Gain |
|---|---|---|---|---|---|---|
| iPhone SE, 375 × 667 | 151 · 61 · 45 = 257 | 196 | 214 pt, **32%** | 214 pt, 32% (unchanged) | **423 pt, 63%** | ×2.0 |
| iPhone 15, 390 × 844 | 151 · 61 · 45 = 257 | 196 | 391 pt, **46%** | 391 pt, 46% (unchanged) | **600 pt, 71%** | ×1.5 |
| iPhone Pro Max, 430 × 932 | 133 · 61 · 45 = 239 | 196 | 497 pt, **53%** | 497 pt, 53% (unchanged) | **688 pt, 74%** | ×1.4 |
| Android, 412 × 915 | 133 · 61 · 45 = 239 | 196 | 480 pt, **52%** | 480 pt, 52% (unchanged) | **671 pt, 73%** | ×1.4 |

- **Today, nothing collapses.** The conversation is the same at rest and scrolled.
- The 390 × 844 row matches the owner's "about 45%".
- The header is 151 at 375 and 390, because the meta line wraps to three lines there. At 412 and
  430 it fits on two lines.
- **The reply area** is 196: the two-line notice (42), the 2-row box (128) and padding.

**Optional changes** (the secondary decisions, measured the same way with injected CSS):

| Change | 375 × 667 | 390 × 844 | 430 × 932 | 412 × 915 |
|---|---|---|---|---|
| Scrolled, with a one-line notice (D6 · 2): the reply area is 175 | 444, 67% | 621, 74% | 709, 76% | 692, 76% |
| Scrolled, with a box that starts at one line (D5 · 2): the reply area is 120 | 499, 75% | 676, 80% | 764, 82% | 747, 82% |
| Scrolled, with both: the reply area is 99 | 520, 78% | 697, 83% | 785, 84% | 768, 84% |
| At rest, chips and tabs in one row (D4 · 2): saves 53 | 267, 40% | 444, 53% | 550, 59% | 533, 58% |
| At rest, chips in the Delegates tab (D4 · 3): saves 61 | 275, 41% | 452, 54% | 558, 60% | 541, 59% |
| At rest, a title with no limit, four lines | 150, 22% | 327, 39% | 433, 46% | 416, 45% |

- **The collapse does most of the work.** It adds 191–209 pt while you read: more than every
  optional change put together.
- At rest, the only bad case is a long title. Today a title has no line limit, so one of four
  lines takes 64 pt more. The collapse folds it away as soon as you scroll, which is why D3
  still recommends keeping it.
- **On a device**, the home indicator's safe area (34 pt on a Face ID iPhone) comes off the
  reply area's foot as well. That is the same before and after, so these numbers leave it out.

## The design

### At rest, revised (2026-11-05)

This replaces "At rest: unchanged" below. ui.md §6.2 has the spec.

- **The title:** 24/32 bold, between Back and More, at most two lines with an ellipsis. The full
  title stays the h2's text, so it is read in full, and it is in Details.
  - A two-line limit is the only way to the owner's target. A title with no limit can run to
    four lines and 64 pt more.
- **One meta line, never wrapped:** glyph · status · machine, plus the account only when it isn't
  the person's own ("Delegating to 2 agents · DEV-VM2"). The status never shortens. The account,
  then the machine, give way with an ellipsis. The repository goes to Details.
- **The tabs, on their own row of 48.** No delegate strip on a phone.
  - "Delegates 2" while two run, read "Delegates, 2 running". No count when none run.
  - While one asks, the tab leads with the `needs` glyph, read "Delegates, 2 running, 1
    asking". The question is answered in the conversation, never only in the tab.
  - The row never changes height, so nothing below moves.

**Measured** (the prototype, Chromium and WebKit, a two-line title):

| Viewport | Header at rest, today → revised | Conversation at rest, today → revised | Scrolled |
|---|---|---|---|
| iPhone SE, 375 × 667 | 257 → **152** | 214 (32%) → **319 (48%)** | 423 (63%) |
| iPhone 15, 390 × 844 | 257 → **152** | 391 (46%) → **496 (59%)** | 600 (71%) |
| iPhone Pro Max, 430 × 932 | 239 → **152** | 497 (53%) → **584 (63%)** | 688 (74%) |
| Android, 412 × 915 | 239 → **152** | 480 (52%) → **567 (62%)** | 671 (73%) |

- It is 152 in every delegate state (running, asking, none) and with a four-line title
  (clamped). With a one-line title it is 120.
- The three tabs fit at 375 with room to spare: the last ends at 276 with the glyph.
- The meta line stays one line with a 32-character machine name.
- Nothing scrolls sideways, and the page has no errors.
- The collapse distance is now 152 − 48 = 104.

**Keyboard and screen reader** (checked in both engines):
- Tab goes Back, More, the tabs (one stop, arrows inside), then the conversation.
- There is one h2.

**Captures** (390 × 844, reduced motion):

| State | Capture |
|---|---|
| Two delegates running | [dark](620-compact-header/rest-dark.png) · [light](620-compact-header/rest-light.png) · [high contrast](620-compact-header/rest-hc.png) |
| One asking | [dark](620-compact-header/rest-asking-dark.png) · [light](620-compact-header/rest-asking-light.png) · [high contrast](620-compact-header/rest-asking-hc.png) |
| None running | [dark](620-compact-header/rest-none-dark.png) |
| Long title and long machine name | [dark](620-compact-header/rest-long-dark.png) |
| Scrolled | [dark](620-compact-header/rest-scrolled-dark.png) |
| The Delegates tab focused | [dark](620-compact-header/rest-focus-tab-dark.png) |

The prototype shows one asking with `?d=ask`, and none running with `?d=0`.

**Where it applies:** the PWA on a phone, and native phones later. Tablet, desktop and the
Windows app don't change, chips included. The owner asked Creative to apply the same test there;
that is a separate proposal.
### At rest: unchanged

*(Superseded on 2026-11-05 by [At rest, revised](#at-rest-revised-2026-11-05).)*

A session always opens at rest, with the header in full:

- Back, the title and More, with the meta line under the title;
- the delegate chips;
- the tabs.

Coming back to the top of the conversation always returns it to this.

### Collapsing: tied to your finger near the top

The header floats over the top of the conversation. The conversation's scroll area starts below
the header, padded by its full height. As you scroll down from the top:

- **The header rides up with the conversation.** The title, the meta line, the chips and the
  tabs slide up under a pinned **48 pt bar**. Nothing jumps, and no gap opens. It follows your
  finger with no easing and no threshold, as iOS's large title does.
- **The bar holds Back (44), the session's glyph (18), the title on one line with an ellipsis
  (17/22, semibold) and More (44).** The bar's background, a hairline under it and its title
  fade in (crossFade) once the big title has gone under it (after 40 pt).
- **Once the full header has gone under the bar,** it is compact. The distance is the at-rest
  height less 48: 209 pt at 390 × 844. From there on, the bar is all the chrome above the
  conversation.
- **If you stop or reverse within that distance,** the header follows your finger. Reaching the
  top shows it in full again.

### Coming back, and going again: thresholds with hysteresis

Further down the conversation, the header doesn't follow every pixel. That would make reading
back a paragraph pull the header over the text.

- **64 pt of scrolling up, in one direction,** brings the whole header back. It slides down over
  the conversation (`collapse`: standard duration and easing), the way Safari's and Chrome's bars
  return. The conversation under it doesn't move.
- **32 pt of scrolling down, in one direction,** sends it away again.
- **The count restarts whenever the direction changes,** so the small back-and-forth of reading
  never changes anything. Coming back takes twice the distance of going.
- **A tap on the bar's title** brings the header back.
- **Reaching the top** always shows the header in full, at rest.

### What counts as scrolling

Only your own scrolling counts: a finger, a wheel, a trackpad, the keys or the scroll bar.

The header ignores scrolls that you didn't make, and keeps the state it had before them:

- the conversation scrolling to an arriving question (§5.3, Scroll to a question);
- a notification opening the session at its question;
- a restored scroll position;
- a response growing below.

**iOS's rubber-band overscroll** is clamped to the scroll range, so bouncing at the top or the
end never reads as a scroll up.

### The question guard

**The 500 ms question guard (§5.3) is unaffected.** The header floats over the conversation,
so collapsing or returning never moves the question, and so never restarts the wait.

Scroll to a question keeps the header as it was, and puts the question 8 pt below whatever the
header covers: the bar, or the whole header if it was showing. A question is never left under it.

### Waiting on you

**While the session waits on you,** the bar's glyph is the `needs` glyph on its disc (§2.1), in
the same colours as the meta line's, so "Waiting on you" survives the collapse on screen. The
bar's copy is drawn only: a screen reader reads the status from the meta line, which stays in
the tree while it is folded away.

**When the question is answered,** the glyph cross-fades to the new state, as every status glyph
does (`glyph`).

### Focus

- **Focus is never hidden.** Everything the collapse hides stays in the accessibility tree and
  the tab order: the meta line, the chips and the tabs.
- **When focus moves into the header,** whether by Shift+Tab, a VoiceOver or TalkBack swipe, or
  a programmatic focus, the header comes back first. It stays while focus is in it. Scrolling
  down doesn't send it away until focus leaves.
- **Nothing focused in the conversation lands under the header.** The conversation's scroll
  padding is what the header covers plus 8 pt: the bar, or the whole header while it is back.
- **Back and More are always in the bar,** at 44 × 44.

### Where it applies

| Where | What changes |
|---|---|
| **PWA on a phone** (< 760, a touch device): iPhone and Android | This proposal. |
| **PWA from 760 up** (tablet, desktop) | **Nothing.** From 760 the window never scrolls (§5.2), and the header has room: its meta is one line, and the conversation has most of the height. |
| **Windows app, narrow** (< 760; the window's minimum height is 600) | **Nothing,** for now. It is a desktop window driven by a mouse, a trackpad and keys, with no touch scrolling to tie the header to, and its minimum height leaves the conversation about 280 epx. If the owner wants it, the same rules could follow later through App.Core, the shared view model, as a follow-up issue. |
| **Native iOS, later** | `NavigationStack` with `.navigationBarTitleDisplayMode(.large)`. The title collapses to inline as the conversation scrolls, which is this proposal's "near the top" behaviour. The chips and tabs sit in the scroll view's header, so they scroll away with it. iOS doesn't bring a large title back mid-list; there, a tap on the inline title or the status bar returns to the top. Native where it exists: the system's own behaviour wins. |
| **Native Android, later** | `MediumTopAppBar` (or `LargeTopAppBar`) with `TopAppBarDefaults.exitUntilCollapsedScrollBehavior` near the top, and `enterAlwaysScrollBehavior` for the return mid-conversation. That is Material's own pairing, and it matches the PWA's. |

Not proposed: opening a session already collapsed, in landscape or in a short window. The
at-rest header is the owner's, and the first scroll takes it away.

## Accessibility

- **Screen readers:**
  - The title stays the session's one **h2**. It is restyled and never removed or duplicated.
    The bar's one-line copy is visual only (`aria-hidden`), and the heading is what's read.
  - The status is read from the meta line, which stays in the tree while folded. The bar's
    glyph is drawn only, so it is never read twice.
  - **Collapsing and returning are never announced.** They change no content, only what is
    drawn, and an announcement on every scroll would be noise.
- **Keyboard:**
  - Focus moving into the header expands it, and the header holds while focus is inside.
  - Tab order is unchanged: Back, More, the chips, the tabs, then the conversation. The bar
    and header come before the conversation in the document, though they are drawn over it.
  - Page Up, Page Down, Home and End scroll the conversation and count as your own scrolling;
    Home brings the header back in full.
- **Reduced motion:**
  - Nothing travels. Near the top, the header still follows your finger: that is the content
    scrolling, not an animation.
  - Coming back and going again are a cross-fade (`crossFade`, 83 ms in the PWA), with no
    slide.
  - The thresholds don't change.
- **Forced colours (high contrast):**
  - The bar and the header are `Canvas`, with a `CanvasText` hairline under the bar, so the
    edge between the bar and the conversation is never just a tint or a shadow.
  - The glyph keeps its system-colour form (§9).
  - The tabs' indicator is `Highlight`.
- **Touch targets:** Back, More and the bar's title are each at least 44 pt tall.
- **Text size:** the bar's title is one line at any text size, with an ellipsis, and the bar
  grows with the text (minimum 48). The full title is always the heading, and it is in Details.

## Secondary decisions: the at-rest header and the bottom

*(For the header, D3 and D4 are superseded on 2026-11-05 by [At rest,
revised](#at-rest-revised-2026-11-05). D5 and D6 still stand as decided: the reply box and the
notice stay as they are.)*

These are optional. **Each recommends keeping today's design**, because:

- the owner finds the at-rest header fine;
- the collapse returns the room while reading;
- the measurements show no strong reason otherwise.

They are here so the owner can take any of them on with the collapse, and the boards show each.

- **The title (D3).**
  - Today: 24/32 with no line limit. A long title of four lines takes 64 pt more at rest.
  - Options: a two-line limit with an ellipsis, with the full title in Details and the heading's
    accessible name; a smaller 20/26 title, a new phone-only size; or a meta line on one line,
    the repository giving way first.
  - If the owner wants one change at rest, the two-line limit is the cheapest. It costs nothing
    for the usual two-line title.
- **The chips and tabs (D4).**
  - Option: one row of tabs then chips, scrolling sideways. It saves 53, but the chips start off
    screen at 390 and narrower.
  - Option: the chips move into the Delegates tab, which says how many are running
    ("Delegates · 3"). It saves 61, but loses the at-a-glance strip §6.3 gives phones.
- **The reply box (D5).**
  - Today the box starts at two rows, with Attach and Send under the text.
  - Option: one line with Attach and Send beside the text, 52 pt tall, as the native composers
    do. It grows a line at a time to six lines (144 pt of text), then scrolls inside.
- **The notice (D6).**
  - Today it is two lines: "The session is waiting for an answer to its question. Answer that
    first, then reply."
  - Option: one line, "Answer its question first. Go to the question". The link is a
    programmatic scroll (it doesn't touch the header), and it focuses the question's first
    choice.

## The ui.md text

The text proposed for ui.md. The owner accepted the recommendations, and #629 applied it,
spelling a few points out in full (the tab order, reduced motion and the native clients).
Where this differs from ui.md, ui.md wins. Sections not listed stay as they are. This is the 2026-11-04 text: the 2026-11-05 at-rest header is in ui.md §6.2, not here.

**§5.2, Layout by window width** (append to the **Scrolling** bullet):

> On a phone, the session's header floats over its conversation and collapses as the
> conversation scrolls (§6.2). From 760 nothing collapses.

**§5.3, Catalogue** (a new row, after Reorder):

> | Collapse | A phone's session header (§6.2) | Scrolling the conversation up 64 or down 32 in one direction, mid-conversation; a tap on the bar's title; focus moving into the header | The header slides down over the conversation, or back up under the bar. The conversation never moves. Near the top there is no motion of its own: the header rides with the scroll | `collapse`: standard, standard | PWA: the tokens on transform. Apple: the large title's own collapse. Android: the top app bar's scroll behaviours | Cross-fade |

And in `tokens.json`'s `motion.catalogue`:
`"collapse": { "duration": "standard", "easing": "standard", "reduced": "crossFade" }`.

**§6.2 Session header** (append under **Phone**):

> - **Collapse on scroll (#620).** A session opens with its header in full. The header floats
>   over the conversation's top.
>   - **Near the top,** scrolling down slides the title, meta line, chips and tabs up under a
>     pinned 48 bar: Back, the session's glyph, the title on one line, More. The header follows
>     the scroll with no easing. The bar's fill, hairline and title fade in (`crossFade`) once
>     the big title is under it (40).
>   - **Mid-conversation,** 64 up in one direction brings the whole header back over the
>     conversation (`collapse`), and 32 down sends it away. The count restarts when the
>     direction changes. A tap on the bar's title brings it back, and the top always shows it
>     in full.
>   - **Only the person's own scrolling counts.** A scroll the app makes (to a question, a
>     restored position) and a response growing below leave the header as it was, except on
>     a screen shorter than twice the header (a landscape phone), where a scroll to a question
>     folds it to the bar first; there the header is also capped at the conversation's height
>     and scrolls within itself, so focus inside it is never out of view. Overscroll
>     is clamped. Scroll to a question puts it 8 below what the header covers, and the
>     conversation's scroll padding does the same for focus.
>   - **While the session waits on you,** the bar's glyph is `needs` on its disc.
>   - **The title stays the one h2.** The bar's copy is drawn, not read; the status is read
>     from the meta line. Nothing is announced.
>     Focus moving into the header brings it back, and it stays while focus is there.
>   - **From 760, and in the Windows app,** the header never collapses.

**§6.3 Delegates, phone** (append to the chip strip's paragraph):

> The strip scrolls away with the header on a phone (§6.2) and comes back with it.

**§6.5 Reply box:** D5 and D6 keep its size at rest. D7 (#935) added "Phone: compact while
reading", and a sentence in §6.2's mid-conversation bullet.

**§9 Accessibility** (append):

> - **The phone's collapsing header** (§6.2) hides nothing from assistive technology. The
>   folded parts stay in the tree and the tab order, focus reaching them brings the header
>   back, and collapsing is never announced. In high contrast the bar is `Canvas` with a
>   `CanvasText` hairline.

## Decisions

Each decision lists its options with the recommended one first and in bold. **The owner chose
the recommended option of every decision (2026-11-04).**

**D1. Collapse behaviour** (the core).
1. **Collapse on scroll down, tied to your finger near the top; bring it back with 64 pt up,
   32 pt down sends it away; the top always shows it in full.** This is iOS's large title near
   the top and Material's enterAlways further down.
2. Collapse the same way, but come back only at the top or on a tap on the bar (iOS's native
   behaviour, with no return mid-conversation). This is calmer, but the tabs are a long scroll
   away.
3. Always compact: open with the bar, and show the full header on a tap. This gives the most
   room, but changes the at-rest header the owner likes.
4. No collapse, with a smaller at-rest header instead (D3 · 3 plus D4 · 3). This saves about
   110 pt at rest, but much less than the collapse gives while reading.

**D2. What the bar holds.**
1. **Back, the session's glyph, the title on one line, More; the `needs` glyph on its disc
   while it waits.**
2. The same, but the bar says "Waiting on you" in place of the title while a question waits.
   This is louder, but loses the title.
3. No glyph. This is quieter, but a pending question disappears with the meta line.

**D3. The title at rest** (secondary).
1. **Keep it: 24/32, no line limit.**
2. 24/32, at most two lines, with the meta line on one line (the repository giving way first).
3. 20/26, at most two lines, with the meta line on one line.

**D4. The chips and tabs at rest** (secondary).
1. **Keep both rows.**
2. One row: tabs, then chips, scrolling sideways (saves 53).
3. The chips move into the Delegates tab, which shows a count (saves 61). *(Chosen on 2026-11-05, with the `needs` glyph on the tab and a two-line title.)*

**D5. The reply box's starting height** (secondary).
1. **Keep two rows, with Attach and Send under the text.**
2. One line, with Attach and Send beside it, growing to six lines (saves 76).

**D6. The notice** (secondary).
1. **Keep today's two-line notice.**
2. One line, "Answer its question first. Go to the question", with a link to the question
   (saves 21).

**D7. The reply area while reading** (#935; *the owner chose 1, 2026-11-05*). The owner
reported that the reply box "sits on too much space when it's not being typed in". Product
asked for one collapse rule and one motion for the header and the box (#696).
1. **Mirror the header: it rides down near the end, is a 48 bar with the draft's first line
   past its room, comes back with 64 down or a tap, and the end and focus always show it in
   full.** Mid-conversation the header and the box move in opposite directions, so the
   conversation gains about 110 pt while reading, and nothing is ever a long scroll away.
2. Collapse the same way, but come back only on a tap or at the end. This is calmer, but
   the box is out of reach while reading back.
3. Keep it as today (D5 and D6). This changes nothing, but leaves the owner's report open.

## Boards

Every board comes in dark and light, and is labelled "Proposal #620 · Decided (owner, 2026-11-04)".
All the boards on one page: [contact-sheet.png](620-compact-header/contact-sheet.png).

| Board | Dark · light | What it shows |
|---|---|---|
| PWA on an iPhone, 390 × 844 | [dark](620-compact-header/proposal-620-phone.png) · [light](620-compact-header/proposal-620-phone-light.png) | Today at rest and scrolled; proposed at rest (unchanged), scrolled down and scrolled back up, with measured heights |
| Four phones | [dark](620-compact-header/proposal-620-sizes.png) · [light](620-compact-header/proposal-620-sizes-light.png) | Today and scrolled at 375 × 667, 390 × 844, 430 × 932 and Android 412 × 915: the table above |
| PWA on Android, 412 × 915 | [dark](620-compact-header/proposal-620-android.png) · [light](620-compact-header/proposal-620-android-light.png) | The same collapse, read as Material's collapsing top app bar |
| The rules | [dark](620-compact-header/proposal-620-rules.png) · [light](620-compact-header/proposal-620-rules-light.png) | At the top; riding up with the finger; compact; 40 up stays; 64 up comes back; waiting on you |
| Optional, at rest (D3, D4), superseded by the rest-*.png captures above | [dark](620-compact-header/proposal-620-rest.png) · [light](620-compact-header/proposal-620-rest-light.png) | A long title today; a two-line limit; 20/26; tabs and chips in one row; chips in the Delegates tab |
| Optional, the bottom (D5, D6) | [dark](620-compact-header/proposal-620-bottom.png) · [light](620-compact-header/proposal-620-bottom-light.png) | Today's reply area; a one-line notice; a one-line box; both; the box grown |
| Accessibility | [dark](620-compact-header/proposal-620-access.png) · [light](620-compact-header/proposal-620-access-light.png) | Focus expands the header; the screen reader's reading; high contrast (Night sky, Desert); reduced motion |

There is no Windows board: the Windows app doesn't change (see [Where it applies](#where-it-applies)).

The boards are generated: `node render.js ../../design/canvas ../../docs/design/proposals/620-compact-header Proposal-620-`
from [tools/design-mockups](../../../tools/design-mockups/README.md). Their sources are
`design/canvas/Proposal-620-*.dc.html`, from y = 33000 on the canvas, below the other proposals' boards.

### The prototype

[prototype.html](620-compact-header/prototype.html) is a phone-sized page with no dependencies
and no network. It shows the collapsing header over a scrolling conversation, follows the
system's light or dark theme, and honours reduced motion and forced colours. On a desktop it
draws a 390 × 844 frame.

It was tested with Playwright in **WebKit** and **Chromium**, at 390 × 844 with touch, dark and
light, with motion and with reduced motion. The tests cover:

- it opens at rest;
- it rides with the scroll near the top;
- it is compact past the room, with only the 48 pt bar;
- 40 up stays compact, and 64 up comes back;
- 20 down stays, and 32 down folds it away;
- reading back and forth leaves it compact;
- a tap on the bar's title brings it back;
- focus in the header expands it, and holds it;
- the top shows it at rest;
- a scroll to the question keeps the header as it was, with the question below it;
- the tab order is Back, More, the chips, the tabs, then the conversation;
- there is one h2;
- the reduced-motion cross-fade and the 167 ms slide;
- no sideways overflow, no errors and no network requests;
- in Chromium, a real touch drag collapses it and brings it back.

For D7 (#935), a second run covers the reply area, in both engines, with motion and with
reduced motion: it opens in full; rests at the end; rides near it; is compact past its room,
with the draft's first line in the bar; 40 down stays and 70 down comes back; 20 up stays and
40 up sends it; the header and the reply area never both take full room mid-conversation; a
tap on the bar focuses the box; focus on Send brings it back; a scroll to the question keeps
it, or shows it in full when it lands at the end; Send is hit-testable at rest; no overflow
and no errors. Captures were taken in dark, light and high contrast.

## What follows

- **Done:** the owner decided D1–D6 on #620 (2026-11-04), and D7 for #935 (2026-11-05). In
  #629, this page was labelled with the decision, and the text went into `ui.md` and `tokens.json` (the `collapse` entry,
  generated by design-gen).
- **The PWA builds it in [#631](https://github.com/danswett/agent-bridge/issues/631).** Its
  tests cover:
  - the thresholds and the restart on a change of direction;
  - programmatic scrolls and a growing response;
  - overscroll;
  - focus expanding the header;
  - reduced motion;
  - nothing from 760 up.
- The Windows narrow layout is a separate follow-up, only if the owner wants it.
