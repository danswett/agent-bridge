# Proposal: adopt the Apple session layout's improvements across clients

> **Status: decided by the owner on 2026-11-04 (D1–D6), each as recommended (§5).** ui.md
> carries the decided text. Nothing here is built yet. It changes no product code, and nothing
> in `apple/`: the Apple apps belong to #260, and §6 lists what to route there. It follows #477
> (the design system) and reads the Apple workbench of #358, #382, #385 and #588 at #588's head
> `cb636bf5`.

The macOS workbench in #588 lays a session out in three columns: a sidebar, the conversation,
and a "Session inspector". Some of it is better than what the spec says today, some is the right
macOS rendering of a rule we already have, and some departs from decisions the owner has
already signed off. This proposal sorts each element into one of four verdicts:

- **ADOPT everywhere:** it improves the shared design, so ui.md gets text for every client.
- **APPLE-ONLY idiom:** the platform's convention, specified as the Apple rendering of a shared
  rule.
- **DIVERGES, Apple should change:** it conflicts with an approved decision, so it is routed to
  #260 (§6). This PR doesn't touch Apple code.
- **NEEDS OWNER DECISION:** §5.

**Mock-ups.** There are seven boards, which use only sample data. They are on the canvas as
"Proposal · Apple layout …", and rendered in [apple-layout-adoption/](apple-layout-adoption/):

- `proposal-apple-layout-windows-before.png`: the Windows app today, from reading
  `src/AgentBridge.App`.
- `proposal-apple-layout-windows.png`: the same session, as proposed. Pins A1–A8 mark each
  change.
- `proposal-apple-layout-pwa-desktop.png`: the PWA at desktop width, as proposed.
- `proposal-apple-layout-phone-conversation.png`, `-phone-tools.png` and
  `-phone-details.png`: the PWA at phone width, as proposed.
- `proposal-apple-layout-apple-reference.png`: the macOS layout of #588, redrawn and numbered
  1–30. Each number's verdict is in the legend beside it and in §1 below.

The pins on the proposed boards mean:

- **A1:** the model in the composer footer.
- **A2:** the receipt under the box, from #617, unchanged.
- **A3:** the Running card pinned at the top of Tools.
- **A4:** two-line tool rows.
- **A5:** Started, relative, with the absolute time on hover.
- **A6:** the "In this state" label.
- **A7:** "Main agent", unchanged.
- **A8:** "Show reasoning", unchanged.

Findings about the Windows app and the PWA come from reading the code at `origin/main`, not
from running either. They are marked *(code)*.

## 1. Inventory

The "#" column keys to the Apple reference board. Apple paths are at #588's head:
- `W` is `apple/Apps/SessionWorkbench.swift`;
- `D` is `apple/Apps/SessionDetail.swift`;
- `A` is `apple/Apps/SessionActions.swift`;
- `P` is `apple/Sources/AgentBridgeAppleCore/SessionPresentation.swift`;
- `H` is `apple/Apps/SessionHistoryView.swift`.

| # | Element | ui.md today | Windows / PWA *(code)* | Apple (#588) | Verdict |
|---|---|---|---|---|---|
| 1 | Sidebar filter menu | §7 macOS: "the toolbar's filter menu has the same toggle" (Needs you). The Sort menu is #605; "Show sessions from" is #566 | Windows: the Needs you toggle, the machine SelectorBar and the `…` menu. PWA: the same in its list header | `W` L106-127: a `line.3.horizontal.decrease` menu with Order and Machine | NEEDS OWNER DECISION (D6) |
| 2 | Machines button | §6.6: Machines is a page | Windows: the rail. PWA: `#/machines` | `W` L340: a `desktopcomputer` toolbar button | APPLE-ONLY |
| 3 | Window title | §7 macOS: "unified, with title and subtitle"; §6.2: one meta line | Both: a header with the title and the meta line | The title only, with no subtitle | DIVERGES |
| 4 | Search | §7 Windows: one search over every machine | Windows: centred in the title bar. PWA: in the app bar | `W` L103: `.searchable(prompt: "Search all machines")`, trailing in the toolbar | APPLE-ONLY (the macOS placement of §7 search) |
| 5 | Needs you | §6.1 (#253): a filter toggle, not a lane | Both: a toggle | `W` L80: a section of its own | DIVERGES |
| 6 | Machine name on the row | §6.1: two lines; line 2 is status · machine | Both: two lines | `W` L163-184: L183 puts the machine on a third line. It isn't a misplaced group header (point j) | DIVERGES |
| 7 | Status glyphs | §2.1: the shared glyph set, in status colours (§3) | Both: the token glyphs | SF Symbols in system colours | DIVERGES |
| 8 | Row line 2 | §2.2, §6.1: status text · machine, with the age trailing | Both: so | "Copilot · Idle": the agent and status, with no age | DIVERGES |
| 9 | Ended sessions | §6.1: "Ended · N", collapsed | Both: so | `W` L86: "Recently ended", open | DIVERGES |
| 10 | Your message | §6.4: a right-aligned bubble | Both: so | `D` L67: `Section("You")` | DIVERGES |
| 11 | Agent's message label | §6.4: "Main agent" when the host says the words are its own, else the agent's name | Both: "Main agent" | `D` L70: `"\(agent.capitalized) response"` | DIVERGES (point f) |
| 12 | Reasoning | §6.4: "Show reasoning" / "Hide reasoning", closed | PWA: `session-card.js` L536. Windows: the same | `D` L87: `DisclosureGroup("Reasoning")`. The bold title is the model's own Markdown | No change (point g) |
| 13 | Working indicator | §5.3: no shimmer, no looping decoration; the status word is "Working" | Both: the glyph and "Working" | `apple/Apps/HostWorkingIndicator.swift` L10: a shimmering "Host is working…" | DIVERGES (and see #659) |
| 14 | Reply receipt | #617: one line *under* the box, changing in place | Windows: `ReceiptText` "Your answer: …" on a question. PWA: `reply-view.js` | `A` L136-137, L276: "Last action" with "Delivered" *above* the editor | DIVERGES (point b) |
| 15 | Placeholder | §6.5: "Message the main agent" | Both: so (`reply-view.js` L146) | `A` L170: "Message this session" | DIVERGES |
| 16 | Status chip in the composer | §6.2: the status is in the header's meta line | Neither has one | `A` L183, L212-225: a generic circle or lock, with the status word | DIVERGES (point a) |
| 17 | Model in the composer | §6.2: Model is in Details only | Neither has one | `A` L191: the model name, with help "Session model" | NEEDS OWNER DECISION (D1) |
| 18 | Send button | §6.5, §3: Bridge | Both: Bridge | `A` L232: `Color.blue` | DIVERGES |
| 19 | Composer shape | §6.5 macOS: a capsule field | Windows and PWA: a card with a Bridge bottom edge | A rounded field with the footer inside it | APPLE-ONLY |
| 20 | Attach menu | §6.5: Attach, unspecified beyond that | Windows: an Attach button, "Attach files". PWA: a file picker | `apple/Apps/MacAttachmentMenu.swift` L61-63: Attach File…, Choose Photo…, Paste Photo | APPLE-ONLY (spec the shared rule; point a) |
| 21 | Limits sentence | Principle 5: say a limit when it applies | Windows: "0 / 16 KB" in the footer | `apple/Apps/AttachmentComposer.swift` L77: "Up to 10 files. Images are reduced…" in the menu | DIVERGES |
| 22 | "Session inspector" | §6.2, §7: the inspector, toggled from the header | Windows: "Show the inspector" / "Hide the inspector" (`PaneLayout.cs` L86) | `W` L236: a visible heading "Session inspector" | APPLE-ONLY (point e) |
| 23 | Details styling | §7 macOS: "a property grid with right-aligned labels" | Both: a two-column list | A grouped inset list, values trailing | APPLE-ONLY (amend §7 to match) |
| 24 | "Working directory" | §6.2: Folder | Both: Folder | `P` L132: "Working directory" | DIVERGES |
| 25 | Times | §6.2: Started; "how long in this state" (no form given) | Windows: "For" (a duration) and "Updated" (relative), no Started (`SessionCardViewModel.Panes.cs` L68-86). PWA: Started absolute, "In this state" | `P` L149, L231-235: every time absolute, in Pacific with " PT" hard-coded | NEEDS OWNER DECISION (D4), which includes the hard-coded zone |
| 26 | Time in this state | §6.2: "how long in this state" | Windows "For", PWA "In this state" | "Status since" | ADOPT one label (D5) |
| 27 | End session | §6.2: End session in Details. The host makes a busy session take two presses (`v1-sessions.md`, `sessions.stop`) | Windows: "Press End session again to end it" (`EndSessionViewModel`) | `A` L246, L263-264: an "End main session" heading, then "End Session", then "Force End" | DIVERGES (point e) |
| 28 | Running call | §6.2: Tools lists every call the same way | Windows: the running call is the top row (`ToolList.xaml`). PWA: the same, plus the conversation's "Now" line (L541-544) | `D` L105: a "Current tool" card over a "Tool history" disclosure (L147) | ADOPT (D3) |
| 29 | Tool row | §6.2: "glyph, name and arguments in mono, age" | Both: one mono line, `name summary age` | Name, status, summary in the text face, timestamp: four lines | ADOPT two lines, summary first (D2) |
| 30 | Tool history extras | §1, principle 5: no helper sentences | Neither has any | `H` L19 "Loading tools…", L43 "Latest N tool calls · newest first"; "Done" on every row | DIVERGES |

## 2. Points

**(a) The composer footer.** Apple puts three things in the box's footer: a status chip, the
model and Attach. The status chip says what the header already says (§6.2's meta line), and
with a generic circle in place of the session's glyph (§2.1) it says it less well. On macOS the
status belongs in the toolbar's subtitle (§7 macOS), where #588 has nothing. So the chip
diverges, and the subtitle is the fix.

The model is different. A person writing a message is deciding what to ask *this model*, and
today the model is only in Details, which is closed below three panes. A read-only model name
in the footer, in text 3, costs nothing when the session is busy and answers "which model am I
talking to?" without opening anything. It doesn't belong in the header, which is already full
on a phone (#629). The proposal makes it read-only. Choosing a model from the composer would
need a protocol verb, and none exists. (D1)

The attach menu is the right macOS shape: a menu with Attach File…, Choose Photo… and Paste
Photo, the ellipsis marking the items that open a dialog. Its limits sentence ("Up to 10 files.
Images are reduced…") breaks principle 5: a limit is said when it is hit, beside the thing it
stopped, as the Windows "0 / 16 KB" does. The shared rule for every client becomes: Attach
opens the platform's own picker (a menu where the platform offers more than one source), and a
file over the limit is refused with the reason at the box.

**(b) "Last action · Delivered" vs #617.** The owner signed #617 off on 2026-11-04: one receipt
line *under* the box (or on a question's card), changing in place through "Sending…", "Sent",
"Passed to the agent", "Up next" and "Delivered", with no heading. Apple's receipt sits above
the box, with a "Last action" heading. #617 wins: a receipt under the box is next to the Send
that caused it, it moves nothing above the caret when it appears (which the no-pop rule of #659
needs), and it's one rule for every client. Apple's receipt is routed to #260 (§6). This
proposal changes nothing in #617's text.

**(c) Current tool and Tool history.** Apple's best idea. Today a running call is just the top
row of Tools, and it scrolls away. A **Running** card pinned at the top of Tools, holding every
running call, answers the question someone opens Tools to ask ("what is it doing right now?")
without reading the list. Below it, the history keeps today's order.

Apple's second idea is the row itself. Its intent line, in the text face, reads as a sentence
("Run the upload tests 20 times"), where today's mono `powershell Run the upload tests 20
times` reads as a command. So, ADOPT two-line rows:
- Line 1 is the `summary` in the text face.
- Line 2 is `name · age` in meta, with the name in mono.
- A call that failed adds "Failed" in the Failed colour. "Done" isn't spelt out, since the
  glyph says it; "Running" isn't either, since the card says it.

A protocol caveat: the `summary` is the tool's `description` argument when it has one, else a
short form of its main argument (`v1-sessions.md`, Tools). So line 1 is sometimes a sentence
and sometimes a path, such as `src/Upload/UploadClient.cs`. That's fine in the text face; both
read. A `summarySource` field would let a client tell the two apart, but that's a protocol
follow-up, and nothing here needs it.

Grouping by status: no, beyond the Running card. Grouping Done and Failed would break the
order of events, which is what history is for. A failed call keeps its place and its colour.

Motion: the card and the list follow #659. A new call eases in at the top of the card, and
when it ends it takes its place in the history by when it started, newest started first, as
today. It doesn't jump to the top; the calls around it ease apart. Nothing pops.

**(d) Details.**
- **The label.** "Status since" (Apple), "For" (Windows) and "In this state" (the PWA) are
  three labels for one fact. Adopt "In this state", the PWA's, everywhere, showing a duration
  ("4 min"), and in every state, idle included: how long a session has sat idle is worth
  knowing. "Status since" reads as a time, "For" reads oddly without its subject, and "In
  this state" is already ui.md's phrasing. (D5)
- **"Working directory" vs "Folder".** "Folder" is shorter, is what Windows and the PWA say,
  and is plain English (principle 1). Apple should change.
- **Times.** Relative times scan better ("2h ago" against a list of others), and absolute ones
  answer "when exactly?" So: relative in Details, with the absolute time on hover, long-press
  and in the accessible description. ui.md doesn't yet say which time zone a client shows.
  The proposal is the device's, since a person reading a time reads it against their own
  clock. Apple's " PT" is a fixed zone, which would be wrong for anyone outside Pacific.
  (D4)
- **Started.** Windows has no Started row, though §6.2 lists it. Windows adds it.
- **Grouped inset styling** is how macOS draws a property list. §7's "property grid with
  right-aligned labels" is amended to match #588: a grouped inset list, labels leading, values
  trailing.

**(e) "Session inspector" and End session.** "Inspector" is the platform word on macOS, and
the toggle is named for what it shows. A visible "Session inspector" heading over Details is
harmless but needless: the pane is obviously the session's. That's APPLE-ONLY at most.

End session is the real point. The host makes a busy session take two presses, ten seconds
apart at most (`v1-sessions.md`, `sessions.stop`). Windows says "Press End session again to
end it". Apple has an "End main session" heading, an "End Session" button and then a "Force
End" button, with a helper sentence. "Force" suggests something beyond what the host does, and
"main" implies there's another kind of session to end. The shared rule, written into §6.2:
- One button, "End session", in the destructive colour.
- For a session that isn't idle, the first press turns the button into "End session: press
  again" for ten seconds, and the second press ends it.
- The button's accessible name carries the seconds left.

**(f) "You" and "Copilot response".** §6.4 marks your message as a right-aligned bubble, and
the agent's words as "Main agent" when the host says they're its own, else the agent's name.
"Copilot response" would read "Claude response" for another agent, and it says "response" for
words that are often a question or a plan. Section headings for every turn also double the
conversation's height in labels. Apple should change.

**(g) The reasoning title.** The bold "Figuring out a fetch rebase" is the model's own
Markdown (its reasoning summary starts with a bold line), not a UI element. Nothing to adopt.
Keep "Show reasoning", closed by default.

**(h) "Search all machines".** The trailing toolbar search field is how a macOS app searches,
and its prompt says exactly what §7's search does. APPLE-ONLY: the macOS placement of the one
search rule. Windows keeps its centred title-bar box, and the PWA its app-bar box.

**(i) The sidebar toolbar icons.** The filter menu is the natural macOS home for every list
control at once:
- Needs you · N (§7 already puts it there);
- Sort, from #605: Your order, Most recent, Machine;
- "Show sessions from", from #566.

It's a decision because #566 and #605 drew those as separate controls on Windows and the PWA.
(D6)

The Machines button is APPLE-ONLY. It opens the Machines page, as the rail does on Windows.

**(j) The machine label below its rows.** It isn't a misplaced group header. `W` L183 draws the
machine as each row's *third line*, so a row's machine sits under its status, where it looks
like a heading for the row below. §6.1's row has two lines, with the machine on line 2 after
the status, and it doesn't group by machine unless Sort says Machine (#605). Route it: two
lines, the machine on line 2.

## 3. What changes in ui.md

Marked in ui.md as "Decided by the owner on 2026-11-04". Each bullet that depends on a decision
names it, and the rest follow from rules already signed off. Nothing in tokens changes.

- **§6.2 Details:**
  - "In this state" (D5), a duration, in every state;
  - Started and Updated are relative (D4), and In this state's tooltip is the time the state
    began. Each shows the absolute time on hover, long-press and in the accessible name, in the
    device's time zone;
  - End session is pressed twice when the session is busy.
- **§6.2 Tools:**
  - a Running card at the top, holding every running call (D3);
  - two-line rows (D2): the summary first in the text face, and "Failed" spelt out only for a
    call that failed. An `ask_user` call and the call an approval holds keep their #253 form,
    and a question waiting isn't a running call.
- **§6.5 Reply box:**
  - the model, read-only in text 3, at the footer's trailing edge before Send (D1);
  - Attach opens the platform's picker (on macOS, Attach File…, Choose Photo… and Paste
    Photo), and a limit is said when it is hit;
  - the receipt is #617's.
- **§7 macOS:**
  - Details as a grouped inset list, and the toggle named "Inspector";
  - the trailing "Search all machines" field;
  - the filter menu as the home of Needs you, Sort and "Show sessions from" (D6);
  - Machines as a toolbar button;
  - the toolbar's subtitle carries §6.2's meta line.
- **§10:** the boards.

## 4. Conflicts with work in flight

- **#617 (reply receipt):** Apple's "Last action" receipt above the composer conflicts with it,
  and #617 wins (point b). The model in the footer (D1) sits *inside* the box, and #617's line
  sits *under* it, so they don't collide. The boards draw both.
- **#659 (no pop):** Apple's shimmering "Host is working…" is a looping decoration, which §5.3
  rules out and #659 reinforces. The Running card must ease in and out as #659 says. When a
  call ends it moves from the card to the history; it never vanishes and reappears.
- **#566 and #605:** compatible; D6 is only about where their controls live on macOS.
- **#629 (compact phone header):** no conflict. Nothing here adds to the header, and the model
  stays out of it on purpose.
- **#557/#558 (Slipgates type and brand):** no conflict. The boards use today's tokens.

## 5. Decisions for the owner

*Decided by the owner on 2026-11-04: D1–D6, each the option marked (Recommended).*

1. **D1, the composer footer.**
   - **(Recommended)** Show the model, read-only, in text 3.
   - Show the model and a status chip.
   - Show neither, and leave the model in Details only.
2. **D2, the tool rows.**
   - **(Recommended)** Two lines: the summary in the text face, then `name · age`.
   - Keep today's single mono line.
3. **D3, the running call.**
   - **(Recommended)** A Running card pinned at the top of Tools.
   - Keep it as the top row.
4. **D4, times in Details.**
   - **(Recommended)** Relative, with the absolute time on hover, long-press and in the
     accessible name, in the device's zone.
   - Absolute in Details only.
5. **D5, the label for the time in this state.**
   - **(Recommended)** "In this state".
   - "Status since", with an absolute time.
   - "For".
6. **D6, the macOS sidebar filter menu.**
   - **(Recommended)** One menu holding Needs you · N, Sort (#605) and "Show sessions from"
     (#566).
   - Separate toolbar controls, as Windows draws them.

## 6. For #260 (Apple), in plain words

Nothing here is a bug report against the Apple PRs. These are the places where the Mac app
departs from decisions already signed off:

1. **Toolbar.** Put the session's status line under the window title: its glyph, status,
   machine and repository. Then drop the status chip from the composer.
2. **Sessions list.**
   - Needs you is a filter in the menu, not a section of its own.
   - Rows have two lines: the title, then the status and machine, with the age trailing. The
     machine on a third line is what reads as a group label in the wrong place.
   - Use the shared status glyphs and colours, not SF Symbols in system colours.
   - Say "Ended · N", collapsed, in place of "Recently ended".
3. **Conversation.**
   - Draw your message as a right-aligned bubble, not a "You" section.
   - Label the agent "Main agent" (or the agent's name, as §6.4 says), not "Copilot response".
   - Drop the shimmering "Host is working…": the glyph and "Working" say it.
4. **Composer.**
   - The placeholder is "Message the main agent".
   - Send is Bridge, not system blue.
   - The receipt is #617's single line under the box, not "Last action" above it.
   - Say the attachment limit when it's hit, not in the menu.
5. **Details.**
   - Say "Folder", not "Working directory".
   - Follow D4 and D5 once they're decided. D4 as recommended means relative times, and the
     device's time zone rather than Pacific hard-coded.
6. **End session.** One "End session" button, pressed twice for a busy session, with no "End
   main session" heading and no "Force End".
7. **Tools.**
   - Rows have two lines, as in D2. Don't spell out "Done".
   - Drop the "Latest N tool calls · newest first" and "Loading tools…" helper lines; use a
     plain spinner.
