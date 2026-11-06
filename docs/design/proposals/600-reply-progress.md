# Proposal #600: reply and answer progress, one receipt updated in place

> **Decided (owner, 2026-11-04). The normative text is [ui.md §6.5, "The receipt"](../ui.md#65-reply-box)**
> (with §5.3, §6.4, §8, §9 and §10); this page is the reasoning behind it, and where the two
> differ, ui.md wins. The owner approved every recommended option, D1 to D7 (recorded on #600
> and #617), then settled five follow-up points about 09:10 PT the same day (#477): the check
> badge is the new `check` icon, a Bridge disc with the check cut out (knocked out to Canvas in
> forced colours), drawn from `/design` on both clients; "You answered · *value*" takes the
> option's label, free text's first line cut to about 40 characters, or nothing for a form, the
> same in Needs you as on the card; and older hosts get the same Delivered and badge, and the
> same quiet window, waiting words and 3 s "Still sending…". That last point supersedes D7.
>
> **Revised the same day: quiet unless something's wrong.** On re-review the owner chose option A
> ([#617 comment](https://github.com/danswett/agent-bridge/pull/617#issuecomment-6639329675)).
> Send's progress ring is the only in-flight signal, with no line under the box; on Delivered,
> Send cross-fades to a stroked check for about 1 s and returns to rest. The line appears, in its
> reserved room, only for an exception (Still sending…, the stalls, still being delivered,
> Waiting for *machine*, Uploading, and every outcome other than Delivered), with the same words,
> thresholds and colours as below. The screen reader still hears each stage, politely, at most
> one a second. The Delivered check badge is gone, and `check` is now Send's stroked success
> glyph. Where this page describes a receipt line for the normal stages or a check badge, the
> revision and ui.md §6.5 win; the boards are redrawn to it.
> Issue [#600](https://github.com/danswett/agent-bridge/issues/600) (parent #597). The host side is #598 / PR #611.

Written for the owner, who decides, and for whoever builds #600. Read the problem, then
[the state table](#the-states), then [the decisions](#decisions). The boards are listed at
[the end](#every-board).

## The problem

After Send, the reply box shows "Sending…" and, with luck, "Delivered. The session has your
reply." Between the two is anywhere from about 1.2 s on an idle session to a minute on a
working one (#597). The user can't tell a slow agent from a stuck machine, and some send
again, which is how a reply ends up typed twice.

PR #611 lets the host say how far a request has got. A client that sends `"progress": true`
to a host that advertises `sessions.progress` hears, before the result:

```json
{"re": 7, "progress": {"stage": "received"}}
{"re": 7, "progress": {"stage": "typed", "working": true}}
{"re": 7, "ok": true, "result": {"outcome": "delivered", "timings": {"typed": 352, "recorded": 1190, "confirmed": 1361}}}
```

- `received` comes before any check, so a request the host then refuses hears it too.
- `typed` means every key has gone, so the outcome can only be `delivered` or `unconfirmed`.
  `working: true` means the agent takes it at its next step. An answer never carries
  `working`, because a question holds the session.
- A resend with the same request id hears again the stages it has already reached.
- The host settles a reply within 60 s, and an answer within 15 s of typing it
  (`AnswerOptions.ConfirmTimeout`), or reports `unconfirmed`.

This proposal decides what the user sees and hears for each of those, and what they see when
the stages stop arriving.

## Principles

1. **One receipt, updated in place.** The line that says "Sending…" today keeps its place and
   its height from Send to the outcome, reserving from Send the room the longest outcome needs
   (two lines on a phone). It changes words; it never adds a row, a toast or a
   step list.
2. **"Answered" stays special.** Only a confirmed answer says "You answered" (§6.2, §6.4).
   Until then, the receipt says where the answer has got and never that it was answered.
3. **No new visual language.** The receipt reuses what already exists:
   - the progress ring (§5.3);
   - the check badge: as proposed, the Finished glyph's check in the S glyph family (the
     Slipgates brand spec, PR #558). As decided, the new `check` icon in `/design`, a filled
     disc with the check cut out, so both clients draw one geometry;
   - the existing colour roles.
   It never draws a session glyph and never uses a status word from §2.2: Working, Idle or
   Waiting on you.
4. **Words carry the state.** Colour and the ring add to the words. They never replace them, so
   high contrast and screen readers lose nothing.
5. **Quiet when it's fast.** On an idle session `received` arrives in tens of milliseconds and
   `typed` in about a third of a second, so the receipt shows nothing until it has something
   worth saying, then shows the furthest stage reached. A fast delivery never flickers
   through every stage.
6. **Older hosts behave as today.** Without `sessions.progress`, nothing changes. (Proposed;
   the owner chose otherwise: older hosts get the new waiting words and Delivered. See D7.)

## Where it sits

- **Reply:** the status line under the reply box. This is today's `role=status` paragraph in
  `reply-view.js`, and `ReplyViewModel.OutcomeText` in the Windows app.
- **Answer:** the status line inside the question card, which is `question-view.js` and
  `QuestionViewModel`. Once the answer is confirmed, the card goes quiet ("Main agent asked")
  and the same line becomes the existing confirmed receipt, "You answered *value*". No
  second line appears.

## The states

These are the recommended words, D1 option 1. Timings are from Send unless a row says
otherwise. "Ring" is the in-flight progress ring of §5.3. "Check" is the check badge on its
own (principle 3), 14 px on the PWA and 16 px on Windows. The client states (`sending`,
`waiting` and its reasons, `failed`) are today's, in `reply-view.js` and `ReplyViewModel`.

### Replies

| State | Protocol | The user sees | Indicator | Colour role | Screen reader (polite, once) | High contrast · reduced motion | Shows |
|---|---|---|---|---|---|---|---|
| Quiet window | submitting | nothing on the line; Send shows its ring | Send's ring | Send button as today | nothing (Send is `aria-busy`) | ButtonText ring · ring still | 0–400 ms |
| Uploading | attachments, before the request | today's words, for example **Uploading 2 attachments… 40%** | ring | words text2, ring Bridge | as today | as Sending | unchanged; the stages follow once the request goes |
| Sending | no frame yet | **Sending…** | ring | words text2, ring Bridge | nothing (Send says busy) | ring Highlight, words CanvasText · ring still | from 400 ms until `received` |
| Sent | `received` | **Sent** | ring | words text2, ring Bridge | "Reply sent" | as Sending | at `received`, no earlier than 400 ms |
| Passed to the agent | `typed` | **Passed to the agent** | ring | words text2, ring Bridge | "Reply passed to the agent" | as Sending | at `typed`, after Sent's 400 ms dwell |
| Up next | `typed`, `working: true` | **Up next** · after the agent's current step | ring | words text2, ring Bridge | "Reply up next, after the agent's current step" | as Sending | at `typed`; no stall timer |
| Delivered | result `delivered` | **Delivered** | check | words and badge Bridge | "Reply delivered" | badge Highlight, words CanvasText · shows at once | at once, skipping any stage not yet shown |
| Still sending | no `received` after 3 s | **Still sending…** | ring | words text2, ring Bridge | "Reply still sending" | as Sending | 3 s; words only, the resend policy is unchanged |
| Sent, stalled | `received`, no `typed` after 5 s | **Sent** · still waiting on ULTRA | ring | words text2, ring Bridge | "Reply sent, still waiting on ULTRA" | as Sending | 5 s after `received` |
| Passed, stalled | `typed`, idle, no result after 10 s | **Passed to the agent** · not picked up yet | ring | words text2, ring Bridge | "Reply passed to the agent, not picked up yet" | as Sending | 10 s after `typed` |
| Still being delivered | client `waiting`, reason "still being delivered" (a resend met the first copy in flight) | the furthest stage reached, then · still being delivered, for example **Passed to the agent** · still being delivered | ring | words text2, ring Bridge | "Reply still being delivered" | as Sending | while the first copy is in flight (today: "The machine is still delivering it…") |
| Still sending, no answer | client `waiting` for another reason while connected (a lost response, a timeout) | **Still sending…** | ring | words text2, ring Bridge | "Reply still sending" | as Sending | until the retry, as today |
| Waiting for the machine | client `waiting` while ULTRA isn't connected (queued on this device) | **Waiting for ULTRA** · it goes when ULTRA is back | still ring | words text2, ring in Send's ink at full opacity | "Reply waiting for ULTRA" | ring in Send's own colours at full opacity (GrayText on Highlight is under 3:1), words CanvasText · already still | while disconnected |
| Not confirmed | result `unconfirmed`, or the client gives up | **Sent, but not confirmed.** Check at ULTRA before sending it again. | none | Needs you text | the sentence | CanvasText | host 60 s (an answer: 15 s); client 9 min, as today |
| Busy, stale | result `busy` or `stale` | today's words, for example **Not sent:** an answer or another reply to this session is being delivered. Try again in a moment. (shortened on the boards) | none | Needs you text | the words | CanvasText | at once |
| Refused, failed | result `refused`; client `failed` | today's words: **The machine refused this reply:** *reason*; **Couldn't send it:** *reason* | none | Danger text | the words | CanvasText | at once |
| Older host (decided) | no `sessions.progress` | **Sending…**, then **Still sending…** at 3 s and the waiting words, then **Delivered** with the check badge; never a stage it didn't report | ring, then check | as in-flight, then as Delivered | as in-flight, then the outcome | as in-flight, then as Delivered | 400 ms quiet window; 3 s stall |

### Answers

Answers have the same rows with "Answer" in place of "Reply" in what the screen reader
says. There are four differences:

- **No Up next.** An answer never carries `working`, so it goes Sent, then Passed to the
  agent, then the outcome.
- **The outcome is "You answered".** The confirmed line reads **You answered** · *value*,
  with the check badge in Bridge and the value in text2. The screen reader says
  "Answer delivered. You answered *value*". This is today's confirmed copy (§6.4). The only
  change is that it now has stages before it.
- **The header doesn't move early.** The row and the header keep saying Waiting on you until
  the host clears the question. The receipt never pre-empts the session's state.
- **A shorter confirmation wait.** The host waits up to 15 s after typing an answer for the
  session to record it (`AnswerOptions.ConfirmTimeout`), not a reply's 60 s, so "Sent, but
  not confirmed" can come sooner for an answer. The client's 9 min limit is the same.

### Timing and flicker

- **Quiet window: 400 ms.** Nothing appears on the line for the first 400 ms. Send's own ring
  (§5.3 Send and Answer) is the immediate feedback.
- **Dwell: 400 ms.** Once an in-between state shows, it stays at least 400 ms before the next
  in-between state replaces it. When the window ends, the receipt shows the furthest stage
  reached, so it never plays a backlog.
- **Outcomes are never held.** A result shows at once and replaces whatever is showing.
- **What that looks like on an idle session.** PR #611's example settles at about 1.4 s, with
  `typed` at 0.35 s. When the window ends at 0.4 s, the receipt goes straight to "Passed to
  the agent", so "Sent" never flashes. "Delivered" follows about a second later. Only an
  outcome inside 400 ms skips the in-between words entirely; in practice that is a refusal,
  busy or stale.
- **Stall words replace, they don't add.** A stall changes the secondary words of the current
  state. The ring keeps turning because the request is still open. Stall words go once the
  next stage or the outcome arrives.

### Stalls and failures

- **Nothing heard (3 s).** A request with no `received` after 3 s says "Still sending…". This
  is words only. The resend policy doesn't change: the client resends, with the same request
  id, only after a send fails, then waits `retryMs` (PWA 3 s, Windows 2 s), as today.
  "Still sending…" also replaces today's "Sending again…": the user doesn't need to know how
  many tries there were.
- **Stall thresholds:**
  - **Sent, stalled (5 s):** the machine is checking, or another delivery holds its single
    typing turn.
  - **Passed, stalled (10 s):** this applies only to an idle session, which normally records
    a prompt within 2 s.
  - **Up next:** no threshold, because waiting is expected there.
- **When the host times out,** it reports `unconfirmed` at 60 s for a reply, or 15 s after
  typing for an answer, and the receipt joins the
  existing path: "Sent, but not confirmed. Check at ULTRA before sending it again." The words
  stay in the reply box, as today, so nothing is lost.
- **Refusals that arrive after Sent.** `received` comes before every check, so a reply can show
  "Sent" and then "The machine refused this reply" or "Not sent: …". That is honest: the
  machine had it and then said no. The line simply changes to the outcome, with no check and
  no ring.
- **`waiting` has three causes, and only one is a lost connection.**
  - **Still being delivered.** A resend met the first copy still in flight. The receipt keeps
    the furthest stage, adds "· still being delivered", and the ring keeps turning.
  - **Connected, but no answer.** A lost response or a timeout reads "Still sending…".
  - **Not connected.** Only a request queued on this device while there's no channel to the
    machine reads "Waiting for ULTRA", with the ring drawn still in text3, because nothing is
    moving. The session's header keeps its own §2.2 state, independently of the receipt:
    "Offline" if the machine has left the relay's presence, "Reconnecting…" if it's still
    present and only the channel is coming back. The ring turns again when the connection
    is back.

## Words

The words have three jobs:

- say where the reply has got without internal terms (keystrokes, injection, the terminal);
- never sound like "Answered";
- never sound like a session's state.

### Options

| Stage | 1. Plain (recommended) | 2. The issue's | 3. Minimal |
|---|---|---|---|
| submitting | Sending… | Sending… | Sending… |
| `received` | Sent | Sent | Sent |
| `typed` | Passed to the agent | Typed | *(stays Sent)* |
| `typed`, working | Up next · after the agent's current step | Queued | Sent · the agent is busy |
| `delivered` | Delivered (answer: You answered · *value*) | Delivered | Delivered |

**Why option 1.**
- "Typed" names the mechanism (keys into a terminal), which the user never sees and
  shouldn't need to know. "Passed to the agent" says the same thing from the user's side.
- "Queued" reads as stuck behind other people's work, and it collides with the
  offline-queued state on this device. "Up next" says nothing is wrong and that the agent
  will get to it, and the secondary words say when.
- Option 3 is the calmest, but it hides the one distinction users ask about: whether the
  machine has it but the agent hasn't taken it yet.

### How they stay distinct

- **From "Answered":** no in-between state says "answer" or "answered". Only the confirmed
  line says "You answered", and only for an answer this device sent (§6.2).
- **From the status glyphs:** the receipt never draws an S glyph. It borrows only the check
  badge, at receipt size, beside words. It never uses §2.2's words "Working", "Idle" or
  "Waiting on you"; Up next says "the agent's current step", not "Working". On the boards,
  the header keeps the session's glyph so the two can be compared side by side.
- **From the #489 attribution:** attribution (`activity.userMessageFrom`, PR #501) labels a
  *bubble* by who sent it ("Scheduled prompt", "Sent by an agent"), in agent colour, on
  the bubble itself. The receipt is never on a bubble. It sits under the box or in the
  question card, and it is only about what this device sent, in Bridge and text2.

## Accessibility

- **Screen readers.**
  - The line is a `role=status`, `aria-live=polite` region on the PWA. On Windows it uses
    `AutomationProperties.LiveSetting="Polite"`, raised with a LiveRegionChanged event.
  - Each visible change is announced once. Changes are coalesced to at most one a second:
    if two arrive inside a second, only the later one is spoken. The outcome is always
    announced.
  - Each announcement starts with its subject, "Reply" or "Answer", because the line has
    no context on its own.
  - "Sending…" is never spoken. Send's `aria-busy` (Windows: the button's busy state)
    already says it, and saying it as well would double up on every send.
- **High contrast** (forced colours; Windows contrast themes):
  - the ring and the check badge are Highlight, with the check knocked out to Canvas;
  - every word is CanvasText, including the secondary words, which lose their dimming;
  - Needs you and Danger text become CanvasText too, which is why every failure says it in
    words: "not confirmed", "Not sent", "refused", "Couldn't send it".
- **Reduced motion.**
  - The ring stops turning (§5.3, as today).
  - A change of words uses `crossFade` (83 ms on Windows and the PWA) instead of
    `acknowledge`'s small rise. Nothing slides or grows.
  - The quiet window and the dwell still apply: they are about reading, not motion.
- **Contrast in the default themes.**
  - Words in text2 on the layer: the same as today's secondary text, so they pass.
  - Delivered in Bridge on the layer: as today's links, so it passes.
  - The ring and the badge are non-text indicators beside words, so they need 3:1, and
    Bridge meets it in both themes.

## Timings

Recommended: **not in the main UI** (D4). The `timings` object, and the client's own marks,
stay where #600 puts them:
- `performance.mark` entries on the PWA;
- the app's log on Windows;
- the host's log line (checked, turn, inject, typed, recorded, confirmed, settled, sent).

A number like "1.4 s" means nothing to someone waiting on an agent, and it invites comparing
one machine with another.

## Per platform

- **PWA.**
  - `describeReply` and `describeAnswer` gain the stages; `#show` adds the quiet window and
    the dwell.
  - The check badge is an inline SVG of the check (principle 3).
  - The ring is the existing `.progress` ring.
  - `forced-colors: active` maps the ring and badge to Highlight and the text to CanvasText.
- **Windows.**
  - `ReplyViewModel` and `QuestionViewModel` own the stage, the words, the timers and the
    announcement text. App.Core stays testable without a UI.
  - The view draws a 16 px `ProgressRing` (stilled under reduced motion) or the check badge
    beside the text.
  - The receipt keeps its height from Send to the outcome: it reserves the longest
    outcome's lines (two at phone width, one on a wide window) from Send.
- **Both.** The thresholds (400 ms quiet window, 400 ms dwell, 3/5/10 s stall) are named
  constants in one place per platform, so they can be tuned after the owner sees them live.

## The ui.md text

This is the text as proposed. The owner approved it on 2026-11-04 with every recommended
option, and it is now in `ui.md`, where §6.5's "The receipt" and §9 carry it in full (using
*machine* for ULTRA). Read `ui.md` for the spec.

**§5.3, the "Send and Answer" catalogue entry, add:**

> When the host reports progress, the receipt under the box, or in the question card, changes
> its words in place. It shows nothing for the first 400 ms. Each in-between state stays at
> least 400 ms, and an outcome shows at once. Each change of words enters with `acknowledge`,
> as the receipt does today, and with `crossFade` under reduced motion. The
> receipt's ring turns while the request is open. It is still under reduced motion, and
> while this device is waiting for a machine that isn't connected.

**§6.5 (reply box), new subsection "The receipt":**

> A line under the box says where the reply has got. It changes in place, never adds a
> row, and keeps its height from Send to the outcome: from Send it reserves the room the
> longest outcome needs ("Sent, but not confirmed. Check at *machine* before sending it
> again."), two lines at phone width (a narrow pane included) and one on a wide window, so
> neither a stage nor the outcome moves the box. A refusal whose reason is longer still may grow, as today's does.
>
> - *Sending…* until the machine has it (shown only after 400 ms; Send's ring covers the
>   first moments).
> - *Sent* when the machine has it.
> - *Passed to the agent* when it has gone to the agent; *Up next · after the agent's
>   current step* if the agent was working.
> - *Delivered*, with the check badge in Bridge, when the host confirms it.
>
> In-flight words are text2 with the ring in Bridge. Delivered is Bridge. If nothing
> follows Send for 3 s, the receipt says *Still sending…* (words only; resends are
> unchanged); if *Sent* has nothing after it for 5 s, *· still waiting on ULTRA*; if
> *Passed to the agent* has nothing after it for 10 s on an idle session, *· not picked
> up yet*. *Up next* has no stall. A resend that meets the first copy in flight keeps
> the stage and adds *· still being delivered*. Only a request held while the machine
> isn't connected says *Waiting for ULTRA*, with the ring still. An outcome other
> than Delivered keeps the words in §8 and its colour role, with no shape. A host without
> `sessions.progress` shows *Sending…* until the outcome, as before. (Superseded: see D7.)

**§6.4 (question card), add after the confirmed receipt:**

> Before it is confirmed, the card's status line goes through the same stages as a reply's
> (Sending…, Sent, Passed to the agent), never Up next, and never "answered". Confirmed, it
> becomes *You answered · value* with the check badge.

**§8 (copy), add to the receipt words:**

> Sending… · Still sending… · Sent · Passed to the agent · Up next · after the agent's current
> step · Delivered · still being delivered · Waiting for *machine* · it goes when *machine*
> is back. Never "typed",
> "queued", "injected" or "in the terminal". Never "answered" before the host confirms.

**§9 (accessibility), add:**

> The receipt is a polite live region. Each change is announced once, prefixed "Reply" or
> "Answer", at most one a second, and the outcome is always announced. "Sending…" isn't
> spoken; the button's busy state says it. In forced colours the ring and badge are
> Highlight, and every word is CanvasText.

## Decisions

Each decision lists the options with the recommended one first and in bold. **The owner chose
the recommended option for every decision, D1 to D7, on 2026-11-04.**

**D1. The words.**
1. **Plain: Sending… / Sent / Passed to the agent / Up next · after the agent's current step /
   Delivered.**
2. The issue's: Sending… / Sent / Typed / Queued / Delivered.
3. Minimal: Sending… / Sent / Delivered, with "· the agent is busy" when working.

**D2. Whether the in-between states show, and when.**
1. **After a 400 ms quiet window, each staying at least 400 ms; outcomes at once.** About
   300–500 ms is the useful range, and 400 ms is the middle of it.
2. Every stage as it arrives. This is the most honest option, but it flickers on idle
   sessions.
3. Only after 1 s. Even calmer, but a working session's "Up next" arrives late.
4. Only the outcome, as today, with stage words only in a stall. This gives up most of
   #600's point.

**D3. The stall thresholds.**
1. **Per stage: 3 s with nothing heard, 5 s after Sent, 10 s after Passed on an idle session,
   none for Up next.**
2. A single 5 s for every stage, Up next excepted.
3. A single 10 s for every stage, Up next excepted.

**D4. Whether to show the timings.**
1. **Nowhere in the UI. They stay in local marks and logs.**
2. In the Delivered receipt's accessible description and tooltip only ("Delivered in
   1.4 s").
3. In a session's Details.

**D5. The indicator.**
1. **The ring while open and the check badge on Delivered; nothing on other outcomes.**
2. The ring only, as today, so Delivered is words alone.
3. The S mark filling in stage by stage. This is rejected because it reads as the Working
   glyph and is a new visual language.

**D6. The colour of the in-flight words.**
1. **text2, so Delivered (Bridge) stands out as the one good outcome.**
2. Bridge, as today.

**D7. Older hosts.**
1. **Unchanged: today's Sending… and outcome copy.**
2. Fake the stages from client-side timers. This is rejected because it would claim
   progress nobody reported.

Superseded by the owner on 2026-11-04 at about 09:10 PT (#477): older hosts keep the 400 ms
quiet window, "Sending…", the 3 s "Still sending…" and the waiting words, and end in the same
"Delivered" (or "You answered · *value*") with the check badge. They still never show a stage
nobody reported, so option 2 stays rejected.

## Every board

Each board is drawn in dark and light (the decided-details and older-host boards also in contrast and reduced motion), from `design/canvas/Proposal-600-*.dc.html`, and every
one is labelled "Proposal #600 · Decided (owner, 2026-11-04)". Each frame is a `Proposal-600-Frame`.

| Board | What it shows |
|---|---|
| [proposal-600-phone-reply.png](600-reply-progress/proposal-600-phone-reply.png) · [light](600-reply-progress/proposal-600-phone-reply-light.png) | PWA phone, a reply to a working session: Send's ring at 0 ms, Sent and Up next spoken but not drawn, Send's check on Delivered, then rest. Nothing appears under the box. |
| [proposal-600-windows-reply.png](600-reply-progress/proposal-600-windows-reply.png) · [light](600-reply-progress/proposal-600-windows-reply-light.png) | The Windows app: Send's ProgressRing, the check on Delivered, rest, and the one exception, "Passed to the agent · not picked up yet". |
| [proposal-600-answer.png](600-reply-progress/proposal-600-answer.png) · [light](600-reply-progress/proposal-600-answer-light.png) | An answer in the question card: a single-choice question sends on a tap, so the tapped option carries the ring and its check while the other options are disabled (creative director, 2026-11-04, under option A; ui.md §6.5 "Tap to answer"), then "You answered · Fixes first", on the phone and in a narrow Windows pane. The status line stays empty while the answer goes. Two more frames follow ui.md §6.5's retry rules: "Sent, but not confirmed." stops the ring and keeps the options disabled with the tapped one pressed, never offering a second answer, since a prompt takes one attempt; and a request the host answered with an error (failed) keeps them so and shows today's Try again, which resends the same request; a lost connection instead waits and resends on its own. |
| [proposal-600-paths.png](600-reply-progress/proposal-600-paths.png) · [light](600-reply-progress/proposal-600-paths-light.png) | The exceptions, the only time a line appears, in seven frames: Still sending…, Sent · still waiting, Passed to the agent · still being delivered, Waiting for ULTRA (Send's ring still), Not confirmed, Refused, and an older host. |
| [proposal-600-access.png](600-reply-progress/proposal-600-access.png) · [light](600-reply-progress/proposal-600-access-light.png) | High contrast (Night sky on dark, Desert on light), with Send in its own system colours; reduced motion; and what a screen reader hears. |
| [proposal-600-states.png](600-reply-progress/proposal-600-states.png) · [light](600-reply-progress/proposal-600-states-light.png) | Every state drawn as a table: what Send shows, and the line for the exceptions only. |
| [proposal-600-decided.png](600-reply-progress/proposal-600-decided.png) · [light](600-reply-progress/proposal-600-decided-light.png) · [contrast](600-reply-progress/proposal-600-decided-contrast.png) · [contrast light](600-reply-progress/proposal-600-decided-contrast-light.png) · [reduced](600-reply-progress/proposal-600-decided-reduced.png) · [reduced light](600-reply-progress/proposal-600-decided-reduced-light.png) | "You answered · *value*" after the button's check: a tap question's label, free text cut at about 40 characters, a form's bare "You answered", a Needs you entry, and the Windows card. |
| [proposal-600-older.png](600-reply-progress/proposal-600-older.png) · [light](600-reply-progress/proposal-600-older-light.png) · [contrast](600-reply-progress/proposal-600-older-contrast.png) · [contrast light](600-reply-progress/proposal-600-older-contrast-light.png) · [reduced](600-reply-progress/proposal-600-older-reduced.png) · [reduced light](600-reply-progress/proposal-600-older-reduced-light.png) | Older hosts: Send's ring, Still sending… at 3 s, the same check on Delivered, an answer's "You answered", and the Windows app. |
| [contact-sheet.png](600-reply-progress/contact-sheet.png) | All of the above, side by side. |

To render: in `tools/design-mockups`, `node render.js ../../design/canvas
../../docs/design/proposals/600-reply-progress Proposal-600-`.

## What follows

- The owner decided D1 to D7 on 2026-11-04, every recommended option, and on re-review the same
  day chose "quiet unless something's wrong" (above), which drops the normal-path line and the badge.
- The text is in `ui.md` (PR #617), normative in §6.5, and the boards are labelled "Decided (owner,
  2026-11-04)". The owner's follow-up points (the badge, "You answered · *value*", older hosts) are
  in ui.md and on the decided-details and older-host boards.
- #600 builds it on both clients, after PR #611 lands. Its tests cover:
  - the quiet window;
  - the dwell;
  - an outcome jumping past stages that never showed;
  - each stall threshold;
  - the coalesced announcements;
  - an older host.
