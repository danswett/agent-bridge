# Proposal #950: a calm return to the app

> **Decided 2026-11-05.** Creative's rules 1–7
> ([comment](https://github.com/danswett/agent-bridge/issues/950#issuecomment-6674037482)) were
> accepted by Product the same evening
> ([comment](https://github.com/danswett/agent-bridge/issues/950#issuecomment-6674052677)), and
> rule 8 was added from the owner's phone capture
> ([comment](https://github.com/danswett/agent-bridge/issues/950#issuecomment-6674075510)). They
> are in [ui.md §6.1](../ui.md#61-session-list) ("Coming back to the app"), with §2.2, §6.4,
> §6.5, §7, §8 and §9.2 amended to match. **ui.md is the spec.** The owner may change the 3 s
> grace; the 1 s cue and the 600 ms point stand unless the owner changes them.

Issue: [#950](https://github.com/danswett/agent-bridge/issues/950). Builder: Client Experience,
after [#941](https://github.com/danswett/agent-bridge/issues/941) and
[#935](https://github.com/danswett/agent-bridge/issues/935).

These are interaction rules: no colour, glyph, size or word style changes, so there are no new
canvas boards. What changes is *when* things show, which a timeline says better than a still.
The machines in the examples are made up.

## 1. The problem

The owner, 2026-11-05, on an iPhone: switching back to the PWA from another app, every row
briefly shows red while it reconnects, and "connecting" rows appear above the sessions and go
again. It reconnects fast; the visuals don't keep up.

It was the spec, not a bug:
- §2.2 showed the `failed` glyph and "Reconnecting…" on every row from the first moment a
  machine's link was down, with no grace, so a reconnect of under a second flashed every row red.
- The connection news (the PWA's `hostNote()` lines, #396, and the phone's connection strip) was
  inserted above the list, so the rows jumped down and back up.
- An offline machine also put a three-line sentence over the list on every visit ("Studio-PC is
  offline. Its sessions are as it last sent them, and can't be answered or ended from here until
  it is back."), news its rows already gave.

## 2. Decided (rules 1–8)

1. **A 3-second grace** from the return (`visibilitychange` to visible, `pageshow` from the
   back/forward cache, `online`; Windows waking or changing network). Rows, the open session and
   its machine tab look as they last did. Arrivals update in place; order changes use §5.3's
   Reorder.
2. **The hold applies at once.** Held controls are `aria-disabled` from the return, drawn
   disabled at 600 ms. A press before then does nothing and shows "Updating…" in text2 under the
   control, never a toast. Opening and reading a session always work.
3. **A cue only when it's news:** "Updating…" in text2 in a fixed slot beside the title, from 1 s
   while any list isn't whole. Never announced, never moves. A return under 1 s shows nothing.
4. **At 3 s,** a machine that isn't back shows the away states in place. If the client's own link
   is down, the strip takes the title row's place at the same height, announced once, politely.
5. **Nothing pushes the rows.** No "Connecting…" rows. A machine with no kept list says so in the
   fixed slot ("Connecting to 2 machines…"). Banners showing stay put; none is newly inserted
   above the list.
6. **A real outage reads as before** after the grace, the 15-minute rule included. Notifications
   and Needs you follow the hold.
7. **Platforms:** the PWA first; Windows takes rules 1–5 on waking from sleep; Apple and Android
   when un-parked.
8. **No connection sentence over the list.** Offline, Connecting, Opening a channel and Couldn't
   reach go. An offline machine's reply box and question say "Studio-PC is offline · you can
   answer once it's back" in text2, in place of their actions. The tab and the Machines page carry
   the machine's state. Only an actionable error (Couldn't reach, unsupported) shows, in the
   fixed slot, one line, naming the machine.

## 3. Timeline

One machine, Studio-PC, with a kept list of five sessions, one of them open with a question
waiting. The person comes back to the PWA at t = 0.

| Element | 0 to 600 ms | 600 ms to 1 s | 1 s to 3 s | From 3 s, machine still not back |
|---|---|---|---|---|
| Rows' glyph and words | As last seen | As last seen | As last seen | `failed` glyph and "Reconnecting…" or "Offline · last seen…", in place |
| Row order and positions | Unchanged; an arriving change uses Reorder | The same | The same | The same: nothing moves |
| Open session, machine tab | As last seen | As last seen | As last seen | Away state; the question and reply box say why once the machine is offline |
| Held controls (Send answer, Send, End session…) | `aria-disabled`, drawn as before; a press shows "Updating…" under it | Drawn disabled | Drawn disabled | Disabled, or replaced by the offline line |
| Title row's slot | Empty | Empty | "Updating…" (or "Connecting to N machines…") | The same, unless the strip has taken the title row |
| Connection strip | None | None | None | Only if this client's own link is down: in the title row's place, announced once |
| Sentences or rows above the list | None | None | None | None |
| Needs you, badge, notifications | Follow the hold, as today | The same | The same | The same |

If Studio-PC's list is whole at any point, its column stops there: the cue goes at once, its
controls work again, and everything left is fresh data drawn in place.

## 4. Acceptance for the build

The build's PR shows captures, each in light, dark and high contrast, plus one pass under reduced
motion:
- **A return under 1 s:** nothing on screen changes but fresh data in place.
- **A 2-second return:** only the "Updating…" cue appears, from 1 s, and goes when the list is
  whole. No red glyph, no strip, no row moves.
- **A 5-second return:** the away states at 3 s, in place, and the strip in the title row's place,
  with no row movement.

Tests that read as sentences, among them: a held control is `aria-disabled` before it is drawn
disabled; a press during the first 600 ms sends nothing and shows "Updating…"; nothing is
inserted above the list on a return; the cue is never in a live region; the strip is announced
once.

## 5. Not decided here

Listed for Creative and the owner rather than guessed:
- Where the title row's fixed slot and the strip sit on the PWA desktop and the Windows app,
  which have an app bar or title bar rather than the phone's title row.
- What the Windows app shows at the end of the grace if its own link to the host is still down,
  given rule 5 (no new connection banner above the list) and that it has no strip.
- What the slot shows when an actionable error and "Updating…" or "Connecting to N machines…"
  are due at once.
- During the grace, held questions leave Needs you's count at once (rule 6), so N, and the rows
  the pressed filter shows, can change before the rows do.
- Where focus goes when an offline machine's line replaces a focused control (§9.1 A4).
