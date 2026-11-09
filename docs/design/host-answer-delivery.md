# Host answer delivery: resilient, observable, never wedged

Status: design. The owner's decisions of 2026-11-07 are recorded in section 10.1, and the questions
still open are in section 10.2. Part of #1132.
Written 2026-11-07 (Pacific). Code is cited at `origin/main` c66b2335 (line numbers checked at a97060a3; the cited files did
not move between the two). Host fix PR #1137 (branch
`fix/form-screen-1123`, in review; see the PR for its current head) is cited where this design
builds on it. These descriptions were checked against head a2760e91.

## Why

The host is the bridge's background service on each machine. It is one of the most important pieces of
the product. When a person answers a question, approves a permission or sends a reply from their phone,
the host has to get it into an agent CLI session that is running in a terminal. On 11-07 three
answers failed in one night:

* #1123: at 3:36 AM, a three-field form arrived only part-answered (1 of 3 fields).
* #1101: at 23:33 on 11-06 and 00:58 on 11-07, an Enter was held and every retry was refused.
* At 04:21 a three-choice question was refused with "the session's screen no longer showed the
  question's form" (#1132).

#1093 adds a fourth: the phone said "already typed" for an answer that had in fact been delivered.

The owner's rules for this work (#1132):

* A session must never wedge.
* The host presses no blind keys. It presses Enter only when the screen is positively that exact
  question.
* The phone never sends a blind "press Enter".
* Every failure must be explainable from a beta tester's feedback report alone.

This document covers ten things:

1. How delivery works today, and how it fails.
2. Which structured channels exist, and what about them is verified.
3. Full observability (the first slice to build).
4. One delivery state machine.
5. Screen-read discipline.
6. A library of real screens and a canary for each CLI release.
7. Soak and chaos tests, and the success numbers.
8. Security rules and trust boundaries.
9. A slicing plan, with the related issues mapped to the slices that close them.
10. The owner's decisions, and the questions still open.

Wording rule: anything a person sees says "machine", never "host" or "relay". Times in this
document are Pacific.

---

## 1. How answers reach a session today

### 1.1 Components

| Piece | File | What it does |
| --- | --- | --- |
| Question listing | `src/AgentBridge.Host.Core/Parsing/QuestionMapper.cs` (line 9); `CopilotPermission.cs` (`CopilotDialogs`, line 77) | Turns a transcript `ask_user` call or a permission request into the card a phone shows: kind, fields, options, whether it can be answered remotely. |
| Answer service | `src/AgentBridge.Host.Delivery/AnswerService.cs` | Checks, plans, types and confirms one answer. The Copilot form path runs from about line 494 to line 580. Permissions are about lines 1000–1110, Claude about 1130–1225, Codex about 1250–1280. |
| Form recognition | `src/AgentBridge.Host.Delivery/Copilot/CopilotFormScreen.cs` | Decides whether the screen positively shows this question's form at field N (`Locate`, line 115). |
| Permission recognition | `Copilot/CopilotPermissionAnswer.cs` (`CopilotPermissionScreen`, line 229) | Finds the exact "Yes" row's digit, or "No (Esc)" as the last row. |
| Words shown | `Copilot/WordsOnScreen.cs` | Counts the last 16 non-space characters of typed words, to see whether they reached the screen. |
| Key plan | `KeyPlan.cs` | Each write is `KeySend(Text, Enter, …, Expect, Checks)`. `Expect` holds the write to rows read earlier. `ScreenCheck` is at line 31 (`BlankFrom` is at line 41). |
| Injector | `src/AgentBridge.Injector/Program.cs` | Attaches to the session's console. It reads the visible window (`ReadConsoleOutputCharacterW`, line 201) and writes keys (`WriteConsoleInputW`, line 232). It refuses a write ("fail screen 0", lines 129 and 133) unless every expected row still reads exactly the same. |
| Typing slot | `DeliveryGate.cs` | Lets one answer type at a time across sessions. `OutcomeMemory` (line 197) gives an identical retry the first outcome. |
| Attempt record | `AttemptLedger.cs` | Kept on disk in `answer-attempts.json`, per agent, session and question. A question that had keys typed into it once is never typed into again (`AlreadyTyped`, line 20). |
| Hooks | `src/AgentBridge.Hooks/SpoolContract.cs` (line 52); `docs/host-hooks.md` (about lines 231–270) | Tell the host about events. Copilot's `ask_user` hook replies `{"permissionDecision":"allow"}` at once. Nothing answers through a hook today. |
| Timeline | `DeliveryTimeline.cs` (line 33 shows the format) | Writes one log line of stage timings per delivery. |
| Contract and docs | `docs/host-answers.md` (Copilot keys 43–310, permissions 312–427, Claude 429–452, Codex 454–502, checks before and after typing 504–590); `docs/protocol/v1-sessions.md` | What is typed, and what each outcome means. |

### 1.2 A Copilot form answer, step by step

1. The host waits for the typing slot (`gate.WaitToTypeAsync`).
2. It checks the following before any key:
   * the older bridge is not running;
   * the question is still listed;
   * the transcript holds the call, from the main agent and not resumed (lines 513 and 515);
   * no other prompt is waiting;
   * the form's schema is readable and matches the card;
   * the field plan works (`CopilotPlanner.Plan`).
3. It claims the question in the attempt record (line 549).
4. `TypeFieldsAsync` (line 644) handles each field in turn:
   1. It waits for the field to show and the screen to hold still for two reads in a row
      (`SettledAsync` line 853, `FieldShownAsync` line 874; 250 ms apart, up to 3 s).
   2. It types the words, holding the write to the whole screen as just read (`Expect`, plus
      `BlankFrom` below it).
   3. It waits for the words' tail to show (`AwaitOnScreenAsync`, line 895).
   4. Only then does it press the field's Enter, as a write of its own.
5. It confirms by waiting, for up to 15 s, for the CLI's `human_response.recorded` to match field by field.
   * If the answer is not confirmed in time, it is marked "unconfirmed" and re-read later (`Recheckable`, line 579).
   * If an Enter was held, the result reads "typed into the prompt but not submitted whole… finish it
     at the machine" (line 567).

### 1.3 Failure modes seen

| # | Failure | Where it comes from | Seen in |
| --- | --- | --- | --- |
| F1 | **Any repaint anywhere refuses the next key.** Streaming output, a spinner, the status line or a delegate's output all count. The injector compares every row exactly (Program.cs line 129). Once some keys have gone, the form is left part-answered: "the session's screen changed before the next key could go" (AnswerService line 692, WorkerInjector line 190). | Whole-screen `Expect` | #1123, #1012 |
| F2 | **A screen that never holds still never settles.** The two-equal-reads rule (line 750) fails while the agent streams, so the Enter is held. | `SettledAsync` | #1101 |
| F3 | **Words that never show in 3 s hold the Enter.** The answer is left typed but unsubmitted. Every retry is then refused "already typed once" (`AttemptLedger.cs` line 20). | `WordsOnScreen`, ledger | #1101, #1007, #1093 |
| F4 | **The form is not recognised.** The question text is compared raw to the drawn rows with a prefix walk (CopilotFormScreen lines 139–148), so any of these returns `NotForm`: markdown rendered away, mid-word wraps, an ellipsis, wide characters, a tab bar wrapped onto two rows (line 167), a hints row wrapped in a narrow window, or a form taller than the window (its heading scrolled off, line 122). PR #1137 traced the 04:21 refusal to a wrap after a hyphen. Copilot CLI 1.0.105 breaks lines wherever Unicode line breaking allows, and at a content width of 100 it broke the question after `ag-`. The host joined the rows with a space, so the message check failed nine times on a steady screen. **This was reproduced from the CLI's drawing code, not from a kept screen**: the question had since been answered at the machine. | Exact text matching | #1132 (04:21), #910, #848 |
| F5 | **A part-answered form cannot be resumed or reset.** The attempt record is all or nothing, so a person whose first field went in has no way to finish from the phone. | `AttemptLedger` | #1123, #1101, #1007 |
| F6 | **The phone says "refused" for what was delivered.** The first-answer memory is kept in memory only, a second device gets a plain refusal, and the PWA keeps answer ids in memory only (`pwa/js/answer-service.js` line 55). | `OutcomeMemory`, PWA | #1093 (PR #1100 fixed same-device retries) |
| F7 | **Shapes the host does not drive.** Each is sent to the machine: subagents, some field limits, some permission kinds, Claude permissions, Codex approvals without a hook record, resumed sessions, questions too large to show. | Many | #998–#1014 |
| F8 | **Nothing to look at afterwards.** `host.log` has free-text lines and one timings line. It keeps no screen, no reason key, no CLI version and no question shape. | Logging | All of the above |
| F9 | **A form too wide for the window** was refused as `form.not-shown.bar-window` (#1137). Fixed in #1554: the windowed tab bar is accepted and the form answered field by field; a hidden title is caught when the bar reaches it. | Console width | #1123, #1137; slice 16 (not delivered) |

PR #510 (35 commits, 81 reviews, 11-03 to 11-07) is the history of this matcher. Most of its review
findings were matcher patches, and most failed closed (safe, but the answer was refused):

* a highlight repaint taken as settled;
* a B→A→B repaint (a1a779bb);
* a permission dialog shown before its transcript entry (b73dbcd3);
* screen-wide scans of "1. Yes" (1a69825a, cf1b831a);
* the console growing below (e3065a4e);
* delegates' asks (53cf376c, 1df6b329);
* placeholders (5c1d5147, feef6079).

The lesson is that exact matching is safe but brittle, and every CLI repaint is a new liveness bug.

Tests: `tests/AgentBridge.Host.Tests/AnswerFlowTests.cs` (2404 lines) covers these cases:

* repaints (746, 1980, 2010);
* held Enters and identical retries (823, 847, 872);
* permission prompts opening mid-form (971–1865);
* the transcript being replaced (1659).

**No test streams output during a form, and none uses the three-field choice, text, choice form of
#1101.**

---

## 2. Structured channels: what is verified and what is unknown

Typing into a terminal is the root of F1–F5. A channel that hands the CLI an answer as data would
remove most of them. What I found on this machine (11-07):

**Copilot CLI 1.0.107** (`copilot --help`, `copilot help acp|config|permissions`, and the installed
bundle `%LOCALAPPDATA%\copilot\pkg\win32-x64\1.0.107\cli-main.js`):

| Channel | Verified | Unknown |
| --- | --- | --- |
| `--acp` (Agent Client Protocol over stdio) | The flag and help topic exist. Events carry `agentId` for subagents. | Whether `ask_user` forms reach an ACP client as data, and how it answers them. An ACP session is launched by the client: it is not a person's existing terminal. |
| `--ui-server` ("TUI + server mode", a hidden flag found in the bundle) | Exists. It refuses to combine with `--server`/`--headless`. | Its transport, its authentication, and whether it is meant for outside use. This is the most promising option: a person keeps the terminal, and the host answers as data. |
| Pending-request API inside the bundle (`handlePendingUserInput`, `handlePendingElicitation`, `permissions.handlePendingPermissionRequest`, `getPendingUserInputRequests`) | These functions exist. Remote and attached sessions answer through them (`respondToUserInputViaApi`). | Whether any of them is reachable from another process without a GitHub-hosted remote session. |
| `/remote`, `remoteSessions`, `ahpEnvironment.*` (remote control from GitHub web and mobile) | Exist in `help commands` and `help config`. `requireConnectionBinding` is on by default. | Whether a local client may use them, and the account and trust implications. The answer would go through GitHub, not through this product. |
| Hooks (`hooks` in settings, `.github/hooks/*.json`) | Our `ask_user` hook returns `permissionDecision` and is honoured. | Whether a pre-tool hook may return an *answer* to `ask_user`, or approve or deny a permission prompt in place of the terminal. |
| `-p`/`--output-format json`, `--no-ask-user` | Exist. | Not useful: non-interactive mode cannot ask. |

**Claude Code 2.1.302:** `--input-format stream-json` / `--output-format stream-json` and
`--permission-prompt-tool` exist, but only with `--print`, the SDK mode. `--remote-control` exists.
Unknown: whether an interactive session can take answers that way.

**Codex 0.166.0:** `app-server` (experimental), `remote-control` and `--remote <ADDR>` (the terminal
UI attached to an app server) exist. Unknown: the approval and question methods of the app server.

**Conclusion.** No channel has been shown to answer a question in a session a person started
themselves in a terminal. Likely result: a structured channel works only for sessions the host
launches (which the product already does, `Launch/LaunchService.cs`), and the screen path stays for
everything else. That is why the plan hardens the screen path first and runs a time-boxed spike in
parallel (slice 14).

---

## 3. Full observability (slices 1–3)

This is the owner's requirement: before we fix every failure mode, any beta tester's feedback
report must tell us what happened. It applies to answers, replies and approvals. Everything recorded
is pseudonymous, not anonymous. The full record stays on the machine unless the tester sends it in a
report. Only an enum-only event leaves automatically, and only after an opt-in that is off by
default (section 3.11).

### 3.1 What is recorded today, and the gap

Today `host.log` has these:

* free-text lines per step (AnswerService lines 182, 190, 554, 691, 711, 730, 753, 793, 802, 1058,
  1155, 1224 and 1278);
* one timings line per delivery (`DeliveryTimeline.cs` line 33, for example
  `checked 2, turn 3, inject 4-25, typed 25, recorded 1190, confirmed 1361`).

Timestamps on these lines are in UTC (#611).

**PR #1137 (in review) closes part of the gap for Copilot forms.** It adds:

* one `answer-attempt v=1` line per form answer (`Copilot/CopilotAnswerTrace.cs`). The line has the
  request, session, CLI version, field count and shape (for example `choice3+text`), reads,
  re-reads, steady, retries, the failed check and its row, fields taken, stage timings (slot, typed,
  confirmed), the total and the outcome. It has no screen or answer words. Its keys keep their order
  and new keys are only ever appended.
* a named failed check (`CopilotFormScreen.Diagnose` returns a `FormCheck`: heading, bottom-rule,
  under, message, bar, bar-window, field, field-heading, hints or prompted, with its row);
* a named refusal, `bar-window`, for a tab bar that hides some titles. When the titles do not all
  fit across the console, Copilot CLI 1.0.105 draws only the labels around the selected field,
  between arrows. A hidden title cannot be checked, and a form differing only in a hidden title
  reads the same at field 1. So the host refuses it with nothing typed, rather than typing into
  earlier fields. A live six-field form at 102 columns hit this (section 1.3, F9);
* a word-free screen outline at each refusal (row count, widest row, blank rows, rule and heading
  positions).

Slice 1 builds on #1137 rather than replacing it (section 9). The table below is the gap that is
left once #1137 lands. Approvals, replies, Claude and Codex are still uncovered, and so are the
phone's side and the "answer at the machine" offers.

Gaps:

| Needed | Today |
| --- | --- |
| A stable reason key for each refusal, hold or retry | English sentences that change with each wording fix. #1137 names the form check for Copilot forms only. |
| CLI name and version | Not logged. #1137 logs it for Copilot forms. |
| Question shape | Not logged. #1137 logs it for Copilot forms. |
| Number of screen reads, and whether they were steady | Not logged. #1137 logs it for Copilot forms. |
| What the screen looked like at a refusal | Nothing. The 04:21 refusal could be explained only by reproducing the CLI's drawing (#1137). #1137 adds an outline for form refusals. |
| "Answer at the machine" offers (made when listing, not when answering) | Not logged (#1023) |
| The phone's side: stages, reconnects, which message was shown (as a template id) | Not kept (#1128) |
| One id joining phone, relay and machine | Request ids exist, but nothing carries one id end to end for a report |
| Attachment to Send feedback | Not wired (#733, #738) |

### 3.2 (a) A delivery record for every attempt

Each answer, reply and approval writes one structured record (JSON lines) when it ends. This
generalises #1137's `answer-attempt v=1` line to every kind of delivery and every CLI. Each field of
that line maps to a field below: `reads`/`rereads`/`steady` → `reads`; `check` → `reason`; `taken` →
`fieldsDone`; `stages` → `stages`. Slice 1a keeps writing the `v=1` line unchanged until the
triage tool reads the new record, so logs from #1137's builds stay readable. The record is also
updated at each stage, so a crash still leaves a partial record:

```json
{
  "v": 1, "at": "2026-11-07T11:21:04.118Z", "sendId": "snd-7f3a…", "request": "a-0b7e3c1d", "diagnosticId": null,
  "kind": "answer", "agent": "copilot", "cliVersion": "1.0.107", "hostVersion": "0.1.3310",
  "session": "s-1f2e…(hashed)", "shape": { "type": "form", "fields": 3, "kinds": ["choice","text","choice"], "options": [3,0,2] },
  "stages": { "received": 0, "checked": 2, "slot": 3, "screenRead": 40, "keys": [[60,"field 1"],[910,"field 2"]], "enter": [[430,1]], "recorded": null, "confirmed": null },
  "reads": { "count": 14, "steady": false, "reReads": 6 },
  "events": [ {"t": 920, "key": "screen.changed-partway", "field": 2, "outline": "o-3"} ],
  "outcome": "failed", "reason": "screen.changed-partway", "fieldsDone": 1, "retryable": true
}
```

Rules:

* Times are milliseconds after `received`. The header time is in UTC, as #611 requires; the triage
  tool shows Pacific.
* **Two ids, never confused.**
  * The **send id** (`sendId`, section 3.7) is minted by the phone or app for each Send press and
    joins the phone's, relay's and machine's sides. It stays in the local record and in manual
    reports. It never goes in the automatic event.
  * The **diagnostic id** (`diagnosticId`, section 3.11.3) is random, minted for one failed attempt,
    and is set here only once an automatic event for that attempt has actually been sent. It is
    `null` otherwise, as in the sample, and always while diagnostics are off.
* **Identifiers in the local record.** The record holds **no install id**, ever. It holds no
  identifiers beyond the local send id and the per-machine hashed session id, except that once an
  automatic event for an attempt has actually been sent, that attempt's record also holds that one
  attempt's diagnostic id. **The record never uploads by itself.** It leaves the machine only inside
  a report the tester sends after seeing it in the exact preview of "Report this" or Send feedback
  (PR #1139: the local record "holds no install id and no diagnostic id (a diagnostic id exists only
  once an event is sent), and it never uploads").
* Session ids are hashed per machine. **Session names and titles are never written to the record.**
  The Diagnostics page shows a session's name by reading it live from the running session list when
  the page is open; the record holds only the hashed id, so no saved, exported or sent copy has it.
* No answer text, reply text, question text, option text, command text or file path is ever
  recorded. Only counts, kinds and lengths are.

### 3.3 Reason keys

Every refusal, hold, retry and "answer at the machine" offer gets a dotted key. The English sentence
a person sees maps from the key, so wording fixes do not break counts. First set, taken from today's
strings:

* **Form recognition:**
  `form.not-shown` (NotForm), `form.drawn-under` (Under), `form.wrong-field`,
  `form.permission-over`, `form.unheaded-field`, `form.ambiguous`. `form.not-shown` takes a
  sub-key from #1137's `FormCheck`: `form.not-shown.heading`, `.bottom-rule`, `.under`, `.message`,
  `.bar`, `.bar-window`, `.field`, `.field-heading`, `.hints` and `.prompted`. These use #1137's names
  (`CopilotFormScreen.Name`), so a `v=1` line and a new record agree.
* **Screen:**
  `screen.changed-before` (WorkerInjector 187), `screen.changed-partway` (190),
  `screen.unsettled` (AnswerService 753), `words.not-shown` (802), `field.not-shown` (793),
  `next-field.not-shown` (730).
* **Attempt record:**
  `ledger.already-typed` (AttemptLedger 20), `ledger.unreadable` (88).
* **Question state:**
  `question.subagent` (515), `question.resumed` (513), `question.gone`, `question.too-large`,
  `question.other-pending`.
* **Delivery plumbing:**
  `injector.not-started` (208), `injector.write-failed`, `confirm.timeout`, `confirm.mismatch`.
* **Offers:**
  `offer.<shape>`, one per No-RDP gap (#998–#1014). This covers (f) and #1023: offers are logged
  when a question is listed, counted per day, and included in the health view.

### 3.4 (b) Redacted screen outline at every refusal

The outline records the screen's shape, never its words:

* the size of the window, and the cursor row;
* for each row:
  * a class: blank, rule, heading, tab bar, field heading, option, hints, input, other;
  * its length;
  * its count of wide characters;
  * a short keyed hash (HMAC with a per-machine key that never leaves the machine), so two reads can
    be compared without revealing text.
* which recognition check passed or failed, at which row;
* the outline before and after a changed write, so a diff shows *which* rows moved (above the form,
  in it, or below it).

The outline cannot be reversed into text. Known strings (the heading "Copilot needs information.",
hint words, rule characters) are matched and named, not hashed.

**Optional fuller capture (opt-in).** The feedback flow (#733) can offer "include the screen of the
failed answer". It shows the person the exact text, with known secret patterns masked using the
existing redaction rules of #738. It is sent only if they tick it. It is never collected
automatically.

### 3.5 (c) Ring buffer, feedback and support bundles

* **The buffer.** The machine keeps the last 500 delivery records and the last 50 outlines in
  `%LOCALAPPDATA%\AgentBridge\diagnostics\deliveries.jsonl`. It rolls by count and age (7 days).
  It is on by default, because it holds no content.
* **Send feedback.** Send feedback (#733) attaches the buffer to the sealed bundle (#738;
  `docs/protocol/feedback.md`) only after the tester has seen it in the preview. The relay sees
  nothing of it. The preview says in plain words what is attached: "how your last 40 answers and
  replies went: times and reasons, never what you wrote".
  * **Diagnostic ids are stripped from this export.** A buffer export never carries a
    `diagnosticId`, so it cannot link a report to automatic events without a per-attempt choice.
    The only way a diagnostic id enters a report is a per-attempt tick box in the preview (section
    3.11.2), which every report path uses, Send feedback included.
* **"Report this".** A "Report this" link on any failed or unconfirmed delivery pre-selects that
  delivery's record (section 3.11.2). Its diagnostic id follows the same tick box rule.
* **Support bundles.** Support bundles (the app's Help, Save diagnostics) include the same file,
  with diagnostic ids stripped in the same way.

### 3.6 (d) The phone's and app's side

The PWA and the Windows app keep their own ring of the last 200 sends. Each entry has:

* the send id (section 3.7);
* when the person pressed Send answer;
* when the relay acknowledged, and when each progress stage arrived (received, typed, delivered;
  #598, #600);
* any reconnects in between, with their cause:
  * a probe timeout (#1130);
  * a socket closed by the relay;
  * the network changed;
  * "the relay connection changed" (#1128);
* **what the person was shown** for the outcome, as a reason key and a message template id (for
  example `outcome.refused.form-not-shown` with template `t-refused-2`), and which button they
  pressed next. **Never the rendered text.** Rendered strings can carry question or field words, such
  as "Answer to 'X': …". The exact wording is recovered from the template id and the app version.
* Nothing else free-form: no question, option, answer or session text. The ring is built from the
  same kind of allow-listed schema as the machine's record (section 3.11.1).

This ring is kept in IndexedDB on the phone and in `%LOCALAPPDATA%` for the app. It is attached to
the same feedback bundle, so one report from a phone carries both sides. This also fixes F6: answer
ids that survive a reload let a retry reuse its id.

### 3.7 (e) One send id end to end, inside the private channels

* **Created on the client.** The client creates a **send id** (`sendId`) for each Send press. A
  retry of the same answer keeps the id and adds an attempt number.
* **Carried on every hop.** It goes in the request envelope (phone → relay → machine). The relay
  logs it with timings only. The machine's delivery record and every log line for the delivery
  carry it.
* **Triage.** The triage tool joins the three sides by this id, from a manual report. The send id never
  goes in the automatic event, which has its own random **diagnostic id** (section 3.11.3).
* **Protocol change.** Carrying the id changes `docs/protocol/v1-sessions.md`, a sensitive path.
  The field is optional, and old clients get one minted by the machine.

### 3.8 (g) Metrics and a health view

Daily counts are computed on the machine from the buffer. They are shown in the app's Diagnostics
page, and they never leave the machine on their own. A fleet view is built only from the opt-in
automatic events (section 3.11.3), which are default off and pseudonymous, not anonymous. Until
those are enabled, fleet numbers come from the canary, the soak runs, and the owner's own machines.
The counts are:

* **Success:** answer, reply and approval success rate (delivered ÷ sent), per agent and per CLI
  version.
* **Speed:** time to acknowledge (Send press to "received") and time to deliver, as the median and
  the 95th percentile.
* **Failures:** refusals and offers ranked by reason key; part-answered forms (target: zero);
  unconfirmed answers that later turned delivered.
* **By CLI version:** a CLI version that is new this week is shown side by side with the previous
  one. A rise in any refusal key after a CLI update is flagged.

The canary (section 6) and the soak tests (section 7) report the same numbers.

**What the fleet view can and cannot show.** The automatic events report failed attempts only, and
section 3.11.3 deduplicates and caps them, so they carry no count of sends. The fleet view therefore
shows failure counts and refusal keys ranked against each other, by agent and CLI version, and never
a fleet success rate or delivery-time percentile. Rates and timings come only from the machine's own
counts, the canary, the soak runs and the owner's machines. Fleet-wide rates would need a separate,
privacy-reviewed daily total of sends and coarse timing buckets; that is not part of this design and
needs its own Compliance review before it is proposed.

### 3.9 (h) Privacy and security review

* **Never logged:** answer words, reply words, question text, option labels, command text, file
  paths, session titles in uploads, tokens, or keys. A test feeds known canary strings through every
  path and fails if any of them reaches the buffer.
* **Hashes are keyed per machine.** The key never leaves it, so hashes cannot be matched across
  machines or brute-forced against a dictionary off the machine.
* **Retention:**
  * the local buffer: 7 days or 500 records;
  * a manual feedback bundle: a 30-day hold with no deletion, then lifecycle deletion at day 31
    (`docs/protocol/feedback.md` line 47; #731 item 5, #732). This is a different rule from the
    telemetry maximum. It is not one blanket "30 days" promise, and it does not by itself delete
    decrypted private issues or attachments. The #744 beta check covers the hold and testers'
    rights.
  * an automatic event: at most 30 days (proposed), including any derived copies, in the #741
    pipeline, only once SRE has proved it (section 3.11.3);
  * the opt-in screen capture: only inside a feedback bundle, under that bundle's rule.
* **Who can read it:**
  * the tester, in the app's Diagnostics page (plain words, Pacific times);
  * the feedback inbox tool and its owners (#740, `docs/feedback-inbox.md`), and readers of the
    private feedback repository, for manual reports only;
  * named support staff with least-privilege access to the #741 pipeline, for automatic events
    only;
  * nobody through the relay, which only ever sees sealed bytes;
  * nobody through public issues, which are sanitised templates (#740) and never carry a
    send id, a diagnostic id or diagnostic content.
* **Review.** Trust (#749) reviews the redaction list and the canary-string test before slice 1
  merges.

### 3.10 (i) Triage recipe

A feedback report should be explainable in minutes. `docs/triage/deliveries.md` (written with slice
1) gives a table that maps each reason key to its likely cause, the first thing to check, and the
issue that tracks it. The first rows:

| Reason key | Likely cause | Check | Tracked by |
| --- | --- | --- | --- |
| `screen.changed-partway` | Output streamed above the form during typing | Outline diff: changed rows above the form's frame | #1123, slice 6 |
| `screen.unsettled` | Spinner or stream never held still | `reads.steady=false`, high `reReads` | slice 6 |
| `form.not-shown` | Text mismatch (markdown, wrap, wide characters) or a form taller than the window | Outline: heading row missing, or message rows count ≠ expected | 04:21 case (hyphen wrap, #1137), #848, #910, slice 7 |
| `words.not-shown` | Slow CLI echo, or the pinned Other box | `stages.keys` → no `enter`; field kind | #1101, #908 |
| `ledger.already-typed` | A retry after a partial delivery | `fieldsDone`; earlier record with the same send id | #1007, slice 8 |
| client `reconnect` then a late "received" | Half-open socket | Client ring cause = probe timeout | #1128, #1130 |

A command, `agent-bridge diag deliveries <bundle>`, prints one line per delivery joined across
phone, relay and machine, in Pacific time, with the outline diff for each refusal.

### 3.11 Two channels: the local record with "Report this", and the opt-in automatic event

**The rulings this section follows** (the Feedback Team on #1132, 11-07 04:37 PT, with the decision
posted at 04:37 PT; the same amendment is on #741 and #732). Two channels, kept apart:

* **Tester-written reports.** These are encrypted feedback through the sealed upload of #739, filed
  in the private #740 repository (`danswett/slipgates-feedback`). It holds only what a tester chose
  to send after seeing a preview.
* **Automatic delivery diagnostics.** These go **only** to the separate #741 Application Insights
  beta health-event pipeline.
  * That pipeline is kept logically apart from operational telemetry, with restricted support
    access.
  * Automatic diagnostics never go to the feedback repository or to a public issue.
  * **This design does not assume the pipeline is deployed.**
* **Pseudonymous, not anonymous.** Records in both channels are pseudonymous. This document never
  calls them anonymous, and neither may any notice or setting. Compliance (#741 comment 6707151446)
  treats them as personal data under EEA and UK GDPR. The install id allows singling out and
  linking over time. The per-attempt diagnostic id can link a private report to an event. So no
  notice, setting or document may say the ids are "joined to nothing else" while the manual-report
  join (section 3.11.4) exists.
* **Opt-in worldwide, default off (Product, 2026-11-07, 04:49 PT, #741 comment 6707151446).** This
  supersedes every earlier default-on wording, including the owner's default-on wish and the
  per-region idea.
  * The automatic event is sent only after a tester chooses "Allow diagnostics". It is the same
    rule everywhere: no region-based shortcut and no geolocation.
  * While it is off, there is no collection, no queueing and no upload.
  * Refusing costs nothing: the app works the same without diagnostics.
  * Continued use is never taken as agreement, and consent is never bundled into the beta terms.
  * Turning it off in Settings deletes unsent events. Turning it back on uploads nothing from the
    time it was off.
  * Withdrawal does not erase events already received: they are kept until they expire at 30 days,
    and the notice says so (PR #1139).
* **The owner's five decisions still hold** (section 10.1). Decision 1, "default off now", is now
  the only design. Turning the default on would need a new Product and Compliance decision, and
  this design does not plan for one.

The triggers, the same for both channels: a delivery that ends in one of these:

* a refusal;
* a held Enter;
* an unconfirmed outcome;
* an out-of-date card;
* an "answer at the machine" offer.

The privacy rule of #731–#733 holds throughout. Testers' prompts, code, session titles, file paths
and transcripts never leave their machines unless the tester explicitly attaches them to one report
after seeing them. The relay stays blind. No diagnostic, diagnostic id, send id or report content ever goes
into a public issue.

#### 3.11.1 The local record (not gated; can be built now)

The full per-attempt record (section 3.2) and the redacted screen outlines (section 3.4) stay on the
machine. The phone's send ring (section 3.6) stays on the phone.

* **No install id** in the local record, ever. No identifiers beyond the local send id (and the
  per-machine hashed session id); a diagnostic id only for an attempt whose automatic event was
  actually sent, and then only that attempt's id.
* **It never uploads by itself.** It leaves the machine only in a report the tester sends from the
  exact preview of "Report this" or Send feedback (section 3.11.2).

* **Built from an allow-list.** The record is built from a versioned schema, `delivery-record/v1`.
  A serializer writes only named, typed and bounded fields, and anything not on the list is never
  read in. It is an allow-list, not scrub-after.
* **Redaction tests.** A corpus of adversarial values runs through every field. A test fails if any
  of them appears in the output, whether plain, base64 or percent-encoded. The values include:
  * tokens such as `ghp_…` and `sk-…`;
  * `C:\Users\<name>` paths;
  * hostnames and email addresses;
  * session titles;
  * secrets inside option labels;
  * over-long values, control characters and wide characters;
  * values that look like reason keys;
  * JSON injection;
  * exception messages carrying paths;
  * question, field and option text in rendered phone messages (such as "Answer to 'X': …").

  The phone's and app's send ring (section 3.6) runs through the same corpus and the same test as
  the machine's record.

#### 3.11.2 "Report this" (not gated)

A manual report is independent of telemetry and never needs telemetry consent.

* **What it attaches.** "Report this" on a failed card (#733's UX, #738's bundle) attaches that
  attempt's local record and outline, after the tester has seen them in the exact preview.
* **The diagnostic id: one tick box rule for every report path.**
  * **Stripped unless ticked.** `diagnosticId` is removed from *any* record attached to a report:
    a "Report this" record, a Send feedback or Settings report, a buffer export or a support
    bundle. It stays only where the tester ticked that attempt's box in the preview.
  * **A box only when an event was sent.** The box appears only if the automatic event for that
    attempt was actually sent (uploaded, not just queued). If it is queued or was never made, there
    is no box and no id, the preview says so in plain words, and the report still goes. Slice 2 ships before slice 3,
    so until slice 3's events are actually sent there is never a box and never a diagnostic id.
  * **Defaults.** In "Report this", the box for the attempt being reported is ticked by default and
    can be unticked, because the tester chose that attempt. In Send feedback and Settings reports,
    the recent attempts with a sent event are listed with their boxes unticked by default.
  * **What the preview shows.** The id is shown next to its box. No other path adds a diagnostic id
    to a report (section 3.5).
* **Inside the bundle.** The send id of section 3.7 lives only inside the sealed bundle. It joins
  the phone's, relay's and machine's sides for triage.

#### 3.11.3 The automatic event (buildable now; uploads enabled only after the gates)

The slice is built now behind the off default: a kill switch in config, and no network call while
off. **A build and config flag, `Diagnostics:DeliveryEventsAvailable`, is false until gates (b)
and (e) close.** While it is false:
* the transport is not built in or not wired, so no upload is possible whatever the tester's choice;
* the consent UI is hidden: no first-run prompt, no "Send optional diagnostics" setting and no
  "Diagnostics sent" list. Testers are never asked to consent to something that cannot lawfully run;
* a test asserts both, for a release build with the flag false.

Turning the flag on is its own reviewed change, made after gates (b) and (e) close. Uploads then still
need (a), (c) and (d). **Uploads stay disabled until all of these have passed.** Each is a separate, recorded step.
Compliance's finding approves local-only design work and an off-by-default build. It does not
approve enabling uploads.

* **(a) The #732 independent security review.** Two model families review the expanded schema, the
  install id, retention and the linking. Engineering is staffing it.
* **(b) A lawful basis and a truthful notice.** Counsel, chosen by the owner, confirms the basis and
  notice for the opt-in tier. The attorney-only questions are consolidated on #744: EEA ePrivacy,
  the UK PECR rules as they stand after 5 February 2026, controller and processors, and transfers.
  These are not questions two model-family reviews can certify. The opt-in rule above does not wait
  on them; only enabling uploads does.
* **(c) The `docs/design.md` section 5.10 amendment, PR #1139** (drafted by the Feedback Team). Main's
  section 5.10 does not yet allow this pseudonymous beta tier, so the tier does not ship until #1139
  is reviewed and merged. This design does not repeat its text; #1139 is the authority.
* **(d) SRE's proof of retention, exports and deletion** in the deployed pipeline:
  * per-table and total retention;
  * exports, support copies and derived copies;
  * the SDK's metadata suppression;
  * **no client IP or geolocation stored.** Application Insights stores both by default, so they must
    be disabled or masked at ingestion, and SRE proves this at the upload gate (PR #1139);
  * access, and deletion.

  Microsoft documents that a nominal 30-day Log Analytics workspace can keep data for 31 days unless
  `immediatePurgeDataOn30Days` is on, and table overrides also count. So no "deleted within 30 days"
  promise is made until the chosen pipeline is shown to meet it.
* **(e) The owner's approval of #733's UX**, and the notice text with the verified retention
  sentence and the controller's contact details (#762).
* **Quality verifies** (a), (d) and the written disposition before admission (#1132, 11-07 04:38
  PT).

**A local beta does not wait for this slice.** Slices 1 and 2, the local record and the "Report
this" bundle, are independent of it.

**Off is a tested invariant.** While diagnostics are off, whether by default, by the tester, or by
the kill switch:

* no event is built;
* nothing is queued;
* no install id exists (it is created only after opt-in);
* no network call is made to the pipeline;
* any queued events and the install id are deleted at the moment it turns off;
* turning it on again uploads nothing from the time it was off.

The slice includes three tests:

* One drives every failure trigger with diagnostics off. It asserts an empty queue, no install id
  and zero calls on a fake transport.
* One turns diagnostics off with events queued. It asserts the queue and the install id are deleted
  and nothing is sent.
* One turns diagnostics off, drives failures, and turns them on again. It asserts that nothing from
  the off period is sent.

There is one event per failed attempt, schema `delivery-failure/v1`. It is versioned and strictly
allow-listed, and is a subset of the local record. The receiving pipeline rejects unknown fields,
and CI fails if a field is added that is not an enumeration or a bounded bucket.

| Field | Values |
| --- | --- |
| `schema` | `delivery-failure/v1` |
| `event` | answer, reply, approval |
| `agent` | copilot, claude, codex, other |
| `cliVersion` | major.minor bucket from a fixed list, or `other` |
| `appVersion`, `hostVersion`, `channel` | release versions from the app's fixed list, or `other`; stable or prerelease |
| `platform` | windows-x64, windows-arm64, pwa |
| `stage` | received, checked, slot, screen-read, typing, enter, confirm, offer |
| `reason` | a reason key from the fixed list in section 3.3, or `other` |
| `outcome` | refused, held, unconfirmed, out-of-date, offer |
| `shape` | `type` (choice, multi, text, form, permission, reply); `fields` (1, 2, 3, 4+); `options` (0, 2, 3–5, 6+) |
| `reReads` | 0, 1–2, 3–9, 10+; `steady` true or false |
| `attempt` | 1, 2, 3+ |
| `elapsed` | <1 s, 1–3 s, 3–15 s, 15–60 s, 60 s+ |
| `diagnosticId` | a random 128-bit id, fresh for this attempt and never reused |
| `install` | the diagnostic install id (rules below) |

**The install id.**

* It is cryptographically random and is created only after the tester chooses "Allow
  diagnostics".
* It is never derived from a device, account or machine id. The machine keeps no mapping from an
  old id to a new one.
* It is replaced at least every 30 days, on "Reset my diagnostics id", and on fresh consent after
  withdrawal.
* It is deleted when diagnostics are turned off.
* A test checks that a support join (a report's diagnostic id) cannot link events across a
  rotation, or keep them past their retention.

**The diagnostic id** (the rulings on #1132 and #741 call it the per-attempt correlation id). It is
random and minted per failed attempt. It encodes nothing about the user,
session, screen or content. It does not contain the install id and is not derived from it or from
the send id. It is never reused. Only the local record keeps the link between it and the send id.

**Errors use a finite, approved vocabulary.** The `reason` and `stage` enums are the only error
fields. There are no exception types, stack frames or exception text, even where #741 lists "type
and frames only": these can carry paths, source symbols and content. Raw crashes and screens stay on
the machine unless the tester previews them and attaches them to a manual report.

**A structural outline in the event.** This is allowed later, and is **off until approved**. The
ruling allows an outline in the event only as finite, preapproved structural categories and bounded
counts, generated on the machine. Until #732 and the independent security review approve those
exact fields and their leakage tests, the outline stays local and none is sent automatically. The
candidate fields are:

* row counts by class (rule, heading, tab bar, field heading, option, hints, input, other),
  bucketed;
* which identity check failed, as an enumeration;
* whether rows changed above, inside or below the frame, as true/false.

No row hashes, no lengths beyond buckets, no words.

**Never in the event:**

* pixels, screenshots, screen words, or hashes of any content;
* answer, reply, question, option or command text;
* paths, user names, hostnames, account names or logins;
* session ids or titles;
* raw exceptions, exception types, stack frames or payloads;
* arbitrary strings.

The application SDK's automatic collection and identifying metadata are turned off. That covers
device name, user, IP-derived location, and auto-collected dependencies and exceptions.

**Limits:**

* **Dedupe.** At most one event per failure signature (reason + agent + CLI version bucket + shape
  type) per hour per install. Repeats only add to a count on the next event.
* **Daily cap.** At most 20 events a day per install, under #741's daily cap.
* **Offline.** Unsent events wait in a small queue: at most 50, none older than 24 hours, rebuilt
  from the schema at send time. Each event's age counts from when it was collected, and a retry
  never restarts it. The queue never blocks a delivery.
* **Kill switch.** `Diagnostics:DeliveryEvents=off`, set by machine policy or fleet config, stops
  collection and clears the queue.
* **Triggers.** Only the host's own delivery outcomes trigger an event. Nothing a phone, relay,
  agent or session prints can cause one.

**Retention and access.**

* 30 days is a proposed maximum for events and any copies derived from them (exports, summaries,
  dashboards' stored queries). It is not a verified setting. Expiry starts when the event was first
  collected, and a retry, export, support join or derived copy never restarts it. The need for 30
  days must be justified in the #732 amendment.
* Access is least-privilege: named support staff only.
* SRE must prove the actual destination, access list, retention, exports and deletion (gate (d))
  before any upload is enabled.

**Consent UX.** This follows Compliance's wording (#741 comment 6707151446), subject to the owner's
approval of #733's UX.

* **First run.** The heading is "Help improve Slipgates with optional diagnostics". The body is
  Compliance's proposed text: it names what is sent (health events about setup, updates, delivery
  failures and connection problems; versions and channel; bounded timing and count categories; a
  random installation id and a random id for each attempt), where it goes (Microsoft Azure
  Application Insights, for the Slipgates team), and that it is pseudonymous, not anonymous. It says
  what is never included (prompts, answers, commands, code, session titles, paths, screen text,
  screenshots), and that Send feedback is a separate choice.
  * Two buttons, **"Not now"** and **"Allow diagnostics"**, with neither preselected and of equal
    weight.
  * A visible Privacy notice link.
  * The verified retention sentence and the controller's contact details are added before release.
  * The text is not broadened silently: any new event category is named.
* **Settings.** The toggle is **"Send optional diagnostics"**, off at first.
  * The helper text is Compliance's: turning it off stops collection and uploads and deletes unsent
    events; it does not erase events already received; Send feedback works separately.
  * There is a link to "What is sent and how to request deletion".
  * The current state is always shown, and turning it off is as easy as turning it on.
* **"Diagnostics sent".** A list in Settings and the PWA menu shows each event's time (Pacific), its
  reason, and the exact fields sent.
* **"Reset my diagnostics id"** replaces the install id. It is never described as deleting records
  already uploaded.
* **Withdrawal and erasure** are explained separately. Erasure of received events goes through a
  private rights route, which uses a reference the tester already has (a report reference or diagnostic id).
  No extra identifying data is created just to find a requester.
* **Consent evidence** is kept to a minimum and separately justified. No diagnostic payload is kept
  just to prove consent.

#### 3.11.4 How one failure is joined across channels

1. The attempt fails. The machine writes the local record. If the tester has opted in, it also
   queues the event, carrying a fresh random diagnostic id. Once the event is sent, the local record
   stores that id.
2. The tester presses "Report this". The bundle carries the local record and outline. If the event
   was actually sent, the preview shows that diagnostic id with its box ticked. The tester can untick it;
   if they do, the id is stripped from the attached record.
3. Triage joins the private report and the pseudonymous event by diagnostic id. This join is
   exactly why the ids are linkable personal data, and the notice and preview say so. Inside the
   report, the send id joins the phone's, relay's and machine's sides. Neither the id nor any
   diagnostic content is ever copied into a public issue.

### 3.12 Privacy review

This checklist must pass before uploads from the automatic event (slice 3) are enabled. The opt-in
rule, default off worldwide, is already decided (Product, 2026-11-07, #741 comment 6707151446). This
is not legal advice, and this document claims no legal sign-off. Compliance's finding is advisory
and is not a merge receipt. Items marked **human/legal** need counsel, whom the owner chooses. The
attorney-only questions are consolidated on #744.

| # | Check | Who answers | Status |
| --- | --- | --- | --- |
| 1 | **Gate (b).** Lawful basis and notice for the opt-in tier. Compliance treats the events as personal data under EEA and UK GDPR, and treats prior consent as the defensible rule. Open for counsel: EEA ePrivacy Article 5(3) by country; UK PECR regulation 6 and Schedule A1 (changed 5 February 2026); controller, processors and transfers. Nothing here is a region shortcut: the tier is opt-in everywhere. | **human/legal**: counsel chosen by the owner (#744) | open, **gate** |
| 2 | **Gate (c).** `docs/design.md` section 5.10 amended for this pseudonymous tier: PR #1139 (Feedback), reviewed and merged | Feedback Team + reviewers of #1139 | open, **gate** |
| 3 | IP address exposure at the endpoint: what does Application Insights (and anything in front of it) log or derive from the sender's IP? Is IP masking and location-off configured and verified? No client IP or geolocation may be stored (PR #1139); SRE proves it at the upload gate (d). | SRE + #732 review | open, part of **gate (d)** |
| 4 | Linkability over time: is the install id created only after opt-in, replaced at least every 30 days and on reset or fresh consent, never derived from a device or account id, never inside the diagnostic id, and kept with no old-to-new mapping? Is there a test that a support join cannot defeat rotation or retention? | #732 review | open |
| 5 | Linking to reports: a report that carries a diagnostic id ties a pseudonymous event to a signed-in tester's report. The notice and the exact preview say so, and nothing claims the ids are "joined to nothing else". | #732 review + owner (#733 UX) | open |
| 6 | **Gate (a).** The #732 independent security review by two model families: schema leakage, the install id, retention and linking (items 3–5 and 7 feed it). Every field is an enumeration or a bucket; the error vocabulary is finite, with no exception types, frames or text; the adversarial redaction tests pass for both schemas; the SDK's auto-collection is off; the pipeline rejects unknown fields. | Engineering staffs it | open, **gate** |
| 7 | **Gate (d).** SRE proves per-table and total retention, exports, support copies, metadata suppression, access and deletion, including the 31-day Log Analytics behaviour (`immediatePurgeDataOn30Days`, table overrides). No "deleted within 30 days" promise until then. | SRE; Quality verifies | open, **gate** |
| 8 | **Gate (e).** The owner approves #733's UX. The notice (#762) carries the verified retention sentence and the controller's contact details, and the first-run and Settings text follows Compliance's wording (section 3.11.3). | owner; Feedback Team | open, **gate** |
| 9 | The off invariant: off deletes unsent events and the install id, nothing is queued while off, and turning it on again uploads nothing from the off period. All three are tested. | Quality | open |
| 10 | Who can read the events, and how support reads them for one tester's report without browsing others' events. A private route for erasure requests, which creates no extra identifying data. | SRE + Feedback Team | open |
| 11 | Manual-report retention (a 30-day hold with no deletion, then deletion at day 31) is described separately from the telemetry maximum; the #744 beta check covers it. | Feedback Team (#744) | open |

---
## 4. One delivery state machine

Every answer, reply and approval is in exactly one state, and the phone shows it in plain words:

```
received ─► checking ─► typing(field i of n) ─► submitted ─► delivered
     │           │              │                    │
     │           ├─► out of date (question gone, answered at the machine, session resumed)
     │           ├─► needs the machine (a shape not yet driven; offer logged with its key)
     │           └─► failed, nothing typed ── one-tap Retry (safe: nothing was typed)
     │                          │
     │                          └─► paused at field i ── host resumes when the screen shows field i,
     │                                                   or resets the form only when positively on it
     └─► (identical retry, same sender) ─► same state as the first; never a second typing
```

Rules:

1. **Never half-answered as a final state.** A form that has some fields in is in "paused at field
   i", never in "failed". The host resumes it only when the screen positively shows *this* form at
   field i, with fields 1..i−1 committed (their tab marks, or a fresh read of what they hold). Until
   then it holds no keys and re-reads at a steady pace for up to 60 s.
   * **A field left typed but not submitted** (`typed-unsubmitted` in rule 3) is never typed again.
     On resume it gets only its Enter, and only after the words for that field are verified on
     screen in that field (invariant 2), or for a choice field, the highlighted row is the chosen
     option. If they are not shown exactly, there is no Enter: the form goes to reset (rule 2).
     This keeps invariant 4: no field of one question is typed twice.
2. **Resume first, then reset, and only when the screen is positively this exact form (owner,
   2026-11-07).** The owner chose both:
   * **First, resume** at the next field not yet committed (rule 1). A typed-but-unsubmitted field
     gets only its verified Enter, never a second typing.
   * **If resuming fails, reset and redo.** The host presses Esc to cancel the form, so the agent
     hears "cancelled" and may ask again. The answer is typed into the re-asked question **only if
     its schema matches the original card exactly**:
     * the same message, the same number of fields in the same order, and for each field the same
       kind, title, options (text and order), limits and defaults, compared field by field;
     * bound to the same card id: the host records the original card's id and schema when it presses
       Esc, and the re-asked question takes that card id only on an exact match;
     * then it goes through every check again from field 1, as a fresh typing of that card.
     If the re-asked question is reworded, has different fields or options, or cannot be read as
     that schema, nothing is typed. The new question becomes a new card on the phone, and the old
     card shows the ways out (rule 5).
   * **If the agent never re-asks.** The host waits up to 60 s after the Esc for a re-asked
     question. If none arrives, nothing is typed: the card is retired as out of date ("The question
     was cancelled at the machine and not asked again") and offers the ways out (rule 5). The
     answer is never typed into whatever the session shows next.
   * Both steps happen only when the frame reads positively as *this* form (heading, message, tab
     bar and field headings all matched). If the screen is anything else, there is no Esc and no
     key. The card stays "paused" and offers the phone's ways out (rule 5).
   * The redo is tried once only. If it fails, the card offers the ways out. It never loops.
   * #1137 leaves resume out of scope: its ledger refuses a second typing. Slice 8 adds both steps.
3. **Idempotent by request.** A request id (bound to sender, session and question, PR #1100;
   `DeliveryGate.cs` `OutcomeMemory`, line 210) and the answer's digest identify a delivery. The
   record moves from all or nothing to a per-field record (`fields: [done, done, typed-unsubmitted,
   —]`), persisted on disk with the first outcome.
   * **No answer text on disk.** The persisted record holds the per-field states, the outcome and a
     keyed digest, never the answer text. The digest is an HMAC under a per-machine secret key, kept
     protected by the operating system (DPAPI) and never sent off the machine. It is not an unkeyed
     hash, which a short choice answer could be guessed from. The answer itself lives only in memory
     for the attempt; after a host restart (slice 10) a resume waits for the phone to send it again,
     and that resend must match the keyed digest.
   * **Repeats are per sender.** A repeat of the same request id from the same sender, for the same
     session and question, joins the existing delivery, as `OutcomeMemory` decides today. The same
     id from a different sender is not a repeat. An answer from another device is its own request:
     it never types into a part-answered form, and it is told so ("part of an earlier answer is
     already in") with the ways out.
   * A *different* answer to a part-answered form is refused with "part of an earlier answer is
     already in; finish or reset it", never typed on top.
4. **Out of date retires the card.** The card is retired with a plain message ("This question was
   answered or closed at the machine"), never shown as a red "refused".
5. **Every failure has a way out on the phone** (#1123's acceptance criterion; #773 means each is an
   explicit button):
   * **Look again**: re-sync the question from the machine;
   * **Send as a message instead**: the answer goes as a reply, written as "Answer to 'X': …";
   * **Retry**: only when nothing was typed, or when resuming is safe (rule 1, including the
     Enter-only rule for a typed-but-unsubmitted field).

   The phone never gets a "press Enter".
6. **Wording.** Unconfirmed says "Check the conversation", never "at the machine" (#967, #995).

---

## 5. Screen-read discipline

1. **Hold keys to the form's own frame, not the whole screen.** `Expect` is narrowed to the rows
   from the frame's top rule to its bottom rule, plus `BlankFrom` below it. Output streaming *above*
   the frame no longer refuses a key; anything inside or below the frame still does. The check is
   done in the injector, inside the same console attach as the write (Program.cs lines 123–133), so
   there is no gap between check and write. This is the main fix for F1, F2 and #1123.
   * It only works if the frame's position is pinned. When the stream scrolls the frame up, the
     injector must find the frame again by its rules and heading, not by row number, and compare its
     rows relative to the frame's top.
   * This is sensitive (the injector, `Host.Delivery`). **The owner approved it on 2026-11-07, with
     Trust's agreement: Trust must agree that the frame plus "nothing below it" is enough identity
     before slice 6 ships.** Until then, slice 6 can be built and reviewed but not merged.
2. **Steady double reads per frame.** #1137 adds the first part: a read that does not show the form
   at the next field is read again at each poll for up to 2 s (`FormRecheck`) before a refusal.
   Keys go only when a read positively shows the form. This design extends that so settling
   compares the frame only, so a spinner elsewhere does not hold the Enter. The bound is up to 3 s
   per field, then up to 3 more tries before pausing (state "paused", not "failed").
3. **A normalised comparison of the card against the screen.** #1137 does the first step: the
   question, the tab bar and the key hints are compared with all white space left out. That covers
   the hyphen and mid-word wraps, a tab bar wrapped inside the frame, and wrapped hints (#910).
   Every other character is still compared in order, and field headings are still compared exactly.
   Slice 7 grows this into one function, used for the message, tab bar, field headings and hints,
   that also does the following:
   * drops markdown markers (`**`, `` ` ``, `_`, `#`);
   * drops white space before comparing, so mid-word and hyphen wraps match (#848; done in #1137);
   * accepts a tab bar or hints row wrapped across rows (#910; done in #1137), and a field heading
     wrapped the same way;
   * treats the CLI's `…` truncation as "prefix matches";
   * counts wide characters as two cells;
   * normalises Unicode (NFC).

   The *identity* rules stay strict: exactly one heading, the frame closed, nothing drawn under it,
   and the field heading at the right place. Loosening comparison never loosens identity.
4. **Tall forms.** When the heading has scrolled off and the hints row and field heading are
   visible, the form is identified by them plus the transcript's pending call. Keys are allowed only
   if no other prompt can be on screen, so this needs Trust's ruling (an open question).
5. **Named failures.** Every refusal returns a reason key (section 3.3) and records an outline.

---

## 6. A library of real screens, and a canary for each CLI release

* **The corpus.** `tests/AgentBridge.Host.Tests/Screens/<cli>/<version>/<case>.txt` holds real
  screens, with words replaced by same-length placeholders (wide characters kept as wide). Each comes
  with a JSON file stating the expected result: the field index, or a reason key. Sources:
  * every outline and opt-in capture from feedback;
  * the #510 review history (the cases in section 1.3);
  * #1137's test screens, first: the hyphen and mid-word wraps of the 04:21 question (CLI 1.0.105,
    width 100), the three hint shapes of a narrow window (#910), a form read mid-redraw, output
    scrolled above the form during typing, a field drawn differently when its Enter was refused,
    and a six-field form at 102 columns whose tab bar shows only the labels around each field
    (expected: `form.not-shown.bar-window`, nothing typed).
    Each is filed under the CLI version it was drawn from, and its expected reason key is #1137's
    named check (section 3.3);
  * #910 (narrow window), #848 (mid-word wraps, Chinese, Japanese and Korean text), #908 (pinned
    Other box).

  A replay test runs every screen through `CopilotFormScreen`, `CopilotPermissionScreen` and
  `WordsOnScreen`.
* **The canary.** When a new Copilot, Claude or Codex release appears, a VM job installs it and does
  the following:
  * replays the corpus;
  * drives a scripted session that asks each question shape (one choice, three choices, the
    three-field choice, text, choice form, a narrow window, a tall form, output streaming during the
    form, a permission prompt mid-form);
  * answers it through the real host;
  * reports the health numbers (section 3.8).

  A failure files an issue and holds the version back.
* **Pinning (owner, 2026-11-07).** Sessions the app launches are pinned to the last CLI version that
  passed our release check (the canary). Copilot's `--no-auto-update` exists (verified). The host
  records the passing version per CLI, and launches with that version. A version that fails the
  canary is held back until a fix ships. Sessions a person started themselves run whatever they
  run. Their CLI version is in every record, so the health view shows a regression per version.
  * **Not yet verified:** how to launch one chosen installed version. This machine keeps Copilot's
    versions under `%LOCALAPPDATA%\copilot\pkg\win32-x64\<version>`, and `--no-auto-update` stops
    the update. Slice 11 checks that a launch can be held to the passing version, and does the
    same for Claude and Codex.

---

## 7. Soak and chaos tests, and success numbers

A VM harness (on rehearsal VMs only, never the owner's machines; `docs/parity-checklist.md`) runs
for hours. It mixes:

* answers to every shape;
* agents streaming output during forms;
* window resizes mid-form;
* permission prompts opening mid-form;
* delegates and subagents;
* `--resume` while a question is open;
* host restarts with a question pending (#954);
* phone reconnects (#1128);
* two devices answering at once.

Targets for beta (owner to confirm):

* answer success rate of at least 99.5% for supported shapes;
* **zero part-answered forms left that way for more than 60 s**;
* zero wrong answers;
* time to acknowledge under 1 s median and 3 s at the 95th percentile;
* every non-delivered outcome with a reason key.

The #604 experiment (the shortest safe gap between a burst of keys and its Enter, and confirming
replies from Copilot's prompt hook) runs on this harness.

---

## 8. Security rules and trust boundaries

**Trust boundaries:**

* **Phone → relay.** Authenticated, and the relay sees request envelopes only.
* **Relay → host.** The machine trusts only paired devices.
* **Host → injector.** A child process, attached to one session's console.
* **Injector → CLI.** Key events only.
* **Hooks → host.** Spool files written by the CLI's hook processes. Hooks are user-writable, so
  their content is data, never instructions.

**Invariants** (each with a test; review by Trust, #749):

1. A key is written only when, in the same console attach, the frame reads positively as this
   question at this field. No key is written on a guess.
2. Enter is pressed only after the words for that field are shown in the field, or for a choice
   field, when the highlighted row is the chosen option.
3. The phone cannot send keys. It sends answers. No "press Enter", no "resubmit", no raw key
   channel, ever.
4. A field of one question is never typed twice. A resume continues at the first field not yet
   committed; a field already typed but not submitted gets only its Enter, after invariant 2's
   check. A reset uses Esc only when positively on this exact form. The redo after it is typed only
   into a re-asked question whose schema matches the original card exactly and takes its card id;
   it goes through the full checks and is tried once. If the agent does not re-ask within 60 s,
   nothing is typed and the card is retired with the ways out. No answer text is ever written to
   disk; the persisted record holds a per-machine keyed digest (section 4, rule 3).
5. A different answer is never typed over part of an earlier one.
6. Answers are always sent with an explicit Send answer (#773). "Send as a message instead" is an
   explicit, separate button.
7. Diagnostics never contain the person's words (section 3.9).
8. A structured channel, if adopted, gets the same rules: answer only the pending request id it was
   asked for, and check the session and question match.

---

## 9. Slicing plan, and which slice closes which issue

Every slice under `src/AgentBridge.Host.Delivery/**`, `src/AgentBridge.Injector/**`,
`src/AgentBridge.Protocol/**` or `docs/protocol/**` is on the **sensitive** list
(`quality/merge-rule.yml` lines 115–188). It needs independent review by both model families (#1132).
Each slice is a single small PR.

Observability comes first (slices 1–3), so that every later fix can be measured and any tester's
report explains itself. Slices 1 and 2 are not gated on the privacy checks. Slice 3 can be built now
but stays off. **A beta does not wait for slice 3**: slices 1 and 2 ship on their own.

**Already in review: PR #1137** (host fix for #1123, sensitive, two-family review). It names the
failed form check, logs a word-free outline, re-reads for up to 2 s before a refusal, compares the
question, tab bar and hints without white space, refuses a tab bar that hides titles (`bar-window`,
nothing typed), retries a `ScreenChanged` write held to a fresh
read, and writes one `answer-attempt v=1` line per form answer. The slices below build on it and
do not redo it: slice 1a extends its line, slices 4 and 5 start from its tests, slice 6 builds on
its re-read and retry, and slice 7 on its white-space-free matching.

| Order | Slice | Sensitive? | Closes / part of |
| --- | --- | --- | --- |
| 0 | This design | no | part of #1132 |
| **1** | **The local record**, starting from #1137's `answer-attempt v=1` line and `FormCheck` names: allow-listed per-attempt record, reason keys, redacted outlines, ring buffer, the phone's and app's send ring, send id, triage recipe and tool, offer keys and counts, local health view (sections 3.1–3.10, 3.11.1). It can split into: 1a host record and keys (extends #1137's line to approvals, replies, Claude and Codex; reason keys use #1137's check names); 1b outlines; 1c client ring and send id (protocol); 1d health view. | yes (1a, 1b, 1c) | closes #1023; gives #998–#1014 their counts; part of #1128 |
| **2** | **"Report this" bundle**: "Report this" on failed cards attaches that attempt's record and outline after the preview (section 3.11.2). Until slice 3's events are actually sent, there is no diagnostic id box: slice 2 never shows or attaches a diagnostic id. | yes (client and host) | part of #733, #738 |
| **3** | **The opt-in automatic event**, `delivery-failure/v1` (section 3.11.3). Opt-in worldwide, default off. **Buildable now; uploads enabled only after gates (a)–(e).** Kill switch. Install id created only after opt-in. No collection, queue or network call while off; off deletes unsent events; re-enabling uploads nothing from the off period, all tested. Compliance's first-run and Settings wording. Finite error vocabulary. **A build/config flag hides the consent UI and blocks the transport until gates (b) and (e) close.** | **yes**: the #732 two-family review of schema, install id, retention and linking | part of #741. **Gates before enabling uploads:** (a) the #732 two-family security review (Engineering); (b) lawful basis and notice from counsel (#744); (c) the `docs/design.md` section 5.10 amendment, PR #1139; (d) SRE's proof of retention, exports and deletion, including the 31-day Log Analytics behaviour, and that no client IP or geolocation is stored; (e) the owner's approval of #733's UX and the notice (#762). |
| 4 | Failing-first tests: the three-field choice, text, choice form (#1101's ask), output streaming during a form (#1123), narrow window (#910), mid-word wrap (#848), tall form, markdown message. #1137 already adds the hyphen and mid-word wraps (04:21), wrapped hints (#910), a mid-redraw read, a retry after a scroll, and a six-field form whose tab bar hides titles, refused as `bar-window` with nothing typed; slice 4 adds the rest. | no (tests) | groundwork for #1123, #910, #848 |
| 5 | Real-screen corpus and replay test, seeded first from #1137's test screens, then #510 and slice 1's outlines (section 6) | no | part of #1132 |
| 6 | Hold keys to the form's frame, and settle on the frame (sections 5.1–5.2), building on #1137's re-read and retry. **Approved by the owner (2026-11-07); ships only once Trust agrees** that the frame plus "nothing below it" is enough identity | **yes** (injector; Trust's agreement before merge) | with slice 8, closes #1123 and #1012 |
| 7 | Normalised comparison (section 5.3) | **yes** | closes what #1137 leaves of #910 and #848 (item 1) |
| 8 | Delivery state machine: per-field record; resume at the next field first (a typed-but-unsubmitted field gets only its verified Enter, never a retype), then Esc-cancel and redo once, only when the screen is positively this exact form and the re-asked schema matches the original card exactly (same card id), with a 60 s wait and nothing typed if it is not re-asked (owner, 2026-11-07); a persisted first outcome with a per-machine keyed digest and no answer text on disk; repeats joined per sender (section 4) | **yes** (Trust review) | closes #1123, #1007, #1093; replaces #1101's proposal |
| 9 | Phone ways out: Look again, Send as a message instead, retire out-of-date cards (section 4, rules 4–5) | **yes** (protocol) | #1123's acceptance criterion; closes #1014 and #1010 (fetch the full question); part of #1012 |
| 10 | Questions survive a host restart: re-list, re-verify and keep the record (no answer text on disk; a resume waits for the phone to resend, matched by keyed digest) | **yes** | closes #954 |
| 11 | Canary per CLI release, and pinning launched sessions to the last version that passed it (owner, 2026-11-07; section 6) | no (CI and VM); the launch change is host code | part of #1132 |
| 12 | Soak and chaos harness, and targets (section 7), with #604's experiment | no | closes #604; measures #467 |
| 13 | Coverage on the screen path: Other through the pinned input box; a no-schema `ask_user`; field limits | **yes** | closes #908, #887 (if supported), #1000; follows the #876 decision; #146 (say why Other is missing) |
| 14 | Structured-channel trial (time-boxed, run in parallel from slice 4): Copilot `--ui-server`/ACP, Claude stream-json/`--permission-prompt-tool`, Codex app server, for sessions the app launches. **If the trial works, those sessions use the channel by default** (owner, 2026-11-07); sessions a person started keep the screen path | spike: no; adoption: **yes** | if it holds, adoption closes #998, #1002, #1003, #1004, #1005, #1008, #947, #1001/#42 |
| 15 | Reply path parity: show what is in the composer and offer Clear and send, using the same frame-scoped reads | **yes** | closes #804; with slice 6, #467 |
| 16 | **Not delivered.** Keep wide forms answerable (F9) by launching sessions at a console width that fits every tab title, or by having the host widen the console of a session it launched. #1160 did this (a minimized start, then a 250-column widen) and was reverted: a minimized console is not taken by Windows Terminal, so launched sessions opened as bare console windows the owner could not type into. Launches now open in a normal window and take its own width (host-launch.md, Width); the 250-column promise is given up. Forms too wide for the window keep the refusal and its "answer at the machine, or widen the window" message. Open: another way to keep wide forms answerable that leaves the console to Windows Terminal. | **yes** (launch path; Trust reviews any resize of a session's console) | F9 stands for launched sessions; part of #1123 |

### 9.1 Every related issue in #1132, mapped

Issues in the same failure family:

| Issue | State | Failure mode | Slice |
| --- | --- | --- | --- |
| #1123 part-answered form | open | F1, F2, F5 | part fixed by #1137 (re-read, retry, named check); closed by 6 + 8 (+ 9 for the way out) |
| #1093 "already typed" though delivered | open | F6 | 8 (persisted first outcome); 1c |
| #1109 session wedged by a watcher shell | open | Not delivery: the session cannot take the prompt. Slice 1 shows it as "queued, session busy in a shell". | 1 (visibility); the fix is in the team scripts |
| #910 wrapped hint row in narrow windows | open | F4 | #1137 (three hint shapes); 7 for the rest |
| #848 mid-word wraps; no-schema offered then refused | open | F4, F7 | item 1: #1137, with 7 for markdown and wide characters; item 2: 13 |
| #908 Other through the pinned box | open | F3, F7 | 13 |
| #876 remote Other on enum fields | open | product decision | 13 follows it |
| #804 composer not empty | open | reply path F7 | 15 |
| #467 sent prompt unconfirmed after 60 s | open | F1/F2 on replies | 6 + 15; measured in 12 |
| #604 burst-to-Enter gap; confirm from prompt hook | open | timing | 12 |
| #146 why Other is missing | open | F7 (explain) | 13 |
| #1101 one safe completion of a held Enter | closed | F3, F5 | superseded by 8 (resume) |
| #825 reply typed without reading the box | closed | security history | invariant 1 |
| #766, #768, #882, #887 form schema checks | closed | F7 history | corpus cases (5); #887's open part in 13 |
| #852 quadratic default check | closed | performance history | none (the replay test keeps it fast) |
| #598, #600 progress and stage timings | closed | groundwork | 1 builds on them |
| #967, #995 unconfirmed wording | closed | wording | section 4, rule 6; a wording test in 9 |

No-RDP gaps (each is a place where delivery sends the person to the machine). Every one gets an
offer key and counts in slice 1, which closes #1023:

| Issue | Gap | Slice that closes it |
| --- | --- | --- |
| #998 | subagent's question (AnswerService 515, 1141) | 14 (events carry `agentId`); on the screen path, 13 if a subagent's form is drawn the same and the transcript names it |
| #1000 | Copilot form field the host can't drive | 13 |
| #1001 | Claude question shapes (and #42 free text) | 13 on the screen path; 14 if Claude's stream channel works |
| #1002 | Copilot permission kinds not driven | 13 (screen), 14 (structured) |
| #1003 | Claude permission prompts | 14 (`--permission-prompt-tool` or a hook decision) |
| #1004 | Codex approval without the hook's record | 14 (app server) |
| #1005 | Codex approval not yet checked against hooks | 14; slice 1 measures how often |
| #1007 | answer already typed once | 8 |
| #1008 | Claude Bypass Permissions confirmation at launch | 14 (launch with the flag already confirmed), with Trust |
| #1010 | question too large to show | 9 (fetch the full question on demand); protocol |
| #1012 | screen changed before anything was typed | 6 (fewer cases) + 8 (re-read and retry automatically, since nothing was typed) |
| #1014 | session resumed while a question was open | 9 |
| #1023 | count the offers | 1 |
| #954 | question waiting across a host restart | 10 |
| #947 | folder trust from the phone | 14 (the launch path), with Trust; outside answer delivery proper |

Design, trust, feedback and privacy issues:

* #749 (the Trust epic) reviews slices 1, 2, 3, 6, 7, 8, 9, 10, 13, 14 (adoption) and 15: every
  slice marked sensitive.
* #773 is invariant 6.
* #76 (the HA connector's reply-box boundary) can reuse slice 6's frame reads later. It is not part
  of this plan.
* #731, #732, #733, #738, #739, #740 and #741: slices 2 and 3 follow their bundle format, consent
  flow and privacy rule. Slice 3's uploads are gated on all of (a)–(e) in section 3.11.3: the #732
  two-family review, counsel (#744), the section 5.10 amendment (PR #1139), SRE's retention and
  deletion proof, and the owner's approval of #733's UX and the notice.

---

## 10. The owner's decisions, and the questions still open

### 10.1 Decided by the owner on 2026-11-07 (about 04:48 Pacific)

1. **Automatic diagnostics ship default off now.** Product's later decision (2026-11-07, 04:49 PT,
   #741 comment 6707151446) settles the rest: opt-in worldwide, default off, with no region
   shortcut. This supersedes the earlier default-on path, so no default-on change is planned.
2. **Part-answered forms: resume first, then reset.** The host resumes at the next field not yet
   committed (a field already typed gets only its verified Enter, never a retype). The redo after Esc
   goes only into a re-asked question whose schema matches the original card exactly. If that fails, it presses Esc to cancel the form and redoes the answer once. Both steps
   happen only when the screen is positively that exact form (section 4, rule 2; slice 8).
3. **Narrow the key check to the form's own box: yes.** Trust must agree that the frame plus
   "nothing below it" is enough identity before it ships (section 5.1; slice 6).
4. **Structured channels: trial first.** If the trial works, sessions the app launches use the
   channel by default. Sessions a person started keep the screen path (slice 14).
5. **CLI updates: pin.** Sessions the app launches are pinned to the last CLI version that passed
   our release check (section 6; slice 11).

### 10.2 Still open

1. **Trust's agreement to narrowing the key check** (decision 3). Does the frame plus "nothing
   below it" identify the question well enough for keys? Slice 6 does not merge without it.
2. **Counsel for the opt-in tier** (gate (b); section 3.12, item 1). The owner chooses counsel to
   confirm the lawful basis and notice. The questions are consolidated on #744. We cannot answer
   them, and the safe default (off, opt-in) does not wait on them; only enabling uploads does.
3. **A structural outline in the automatic event** (section 3.11.3): yes, once #732 approves the
   exact fields, or never?
4. **Tall forms** (section 5.4): is a form whose heading has scrolled off answerable?
5. **Beta targets** (section 7): are 99.5% success and zero part-answered forms after 60 s the
   right targets?
