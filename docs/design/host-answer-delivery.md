# Host answer delivery: resilient, observable, never wedged

Status: design. The owner's decisions of 2026-11-07 are recorded in section 10.1, and the questions
still open are in section 10.2. Part of #1132.
Written 2026-11-07 (Pacific). Code is cited at `origin/main` a97060a3. Host fix PR #1137 (branch
`fix/form-screen-1123`, head 8544da2f, in review) is cited where this design builds on it.

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
  under, message, bar, field, field-heading, hints or prompted, with its row);
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
| The phone's side: stages, reconnects, the exact text shown | Not kept (#1128) |
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
  "v": 1, "at": "2026-11-07T11:21:04.118Z", "correlation": "c-7f3a…", "request": "a-0b7e3c1d", "attemptId": null,
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
* Session ids are hashed per machine. The tester's own copy shows the session name, but the
  uploaded copy does not.
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
  `.bar`, `.field`, `.field-heading`, `.hints` and `.prompted`. These use #1137's names
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
* **"Report this".** A "Report this" link on any failed or unconfirmed delivery pre-selects that
  delivery's record (section 3.11.2).
* **Support bundles.** Support bundles (the app's Help, Save diagnostics) include the same file.

### 3.6 (d) The phone's and app's side

The PWA and the Windows app keep their own ring of the last 200 sends. Each entry has:

* the correlation id;
* when the person pressed Send answer;
* when the relay acknowledged, and when each progress stage arrived (received, typed, delivered;
  #598, #600);
* any reconnects in between, with their cause:
  * a probe timeout (#1130);
  * a socket closed by the relay;
  * the network changed;
  * "the relay connection changed" (#1128);
* **the exact text shown to the person** for the outcome, and which button they pressed next.

This ring is kept in IndexedDB on the phone and in `%LOCALAPPDATA%` for the app. It is attached to
the same feedback bundle, so one report from a phone carries both sides. This also fixes F6: answer
ids that survive a reload let a retry reuse its id.

### 3.7 (e) One per-send id end to end, inside the private channels

* **Created on the client.** The client creates a per-send correlation id for each Send press. A
  retry of the same answer keeps the id and adds an attempt number.
* **Carried on every hop.** It goes in the request envelope (phone → relay → machine). The relay
  logs it with timings only. The machine's delivery record and every log line for the delivery
  carry it.
* **Triage.** The triage tool joins the three sides by this id, from a manual report. The id never
  goes in the automatic event, which has its own random per-attempt id (section 3.11.3).
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

### 3.9 (h) Privacy and security review

* **Never logged:** answer words, reply words, question text, option labels, command text, file
  paths, session titles in uploads, tokens, or keys. A test feeds known canary strings through every
  path and fails if any of them reaches the buffer.
* **Hashes are keyed per machine.** The key never leaves it, so hashes cannot be matched across
  machines or brute-forced against a dictionary off the machine.
* **Retention:**
  * the local buffer: 7 days or 500 records;
  * a manual feedback bundle: 30 days, the owner's decision for all feedback (#731 item 5, #732;
    `docs/protocol/feedback.md` line 47);
  * an automatic event: 30 days, including any derived copies, in the #741 pipeline (#732);
  * the opt-in screen capture: only inside a feedback bundle, under the same 30 days.
* **Who can read it:**
  * the tester, in the app's Diagnostics page (plain words, Pacific times);
  * the feedback inbox tool and its owners (#740, `docs/feedback-inbox.md`), and readers of the
    private feedback repository, for manual reports only;
  * named support staff with least-privilege access to the #741 pipeline, for automatic events
    only;
  * nobody through the relay, which only ever sees sealed bytes;
  * nobody through public issues, which are sanitised templates (#740) and never carry a
    correlation id or diagnostic content.
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
| `ledger.already-typed` | A retry after a partial delivery | `fieldsDone`; earlier record with the same correlation | #1007, slice 8 |
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
  calls them anonymous, and neither may any notice or setting.
* **Opt-in, default off.** The automatic event is sent only when a tester opts in to beta
  diagnostics. It can be turned off in Settings at any time. Turning it off stops collection and
  upload, and clears any records not yet sent. The build ships **default off**.
* **Default-on is a separate, reviewed change (owner, 2026-11-07).** The owner has decided that
  automatic diagnostics ship default off now. Turning them on by default is a separate, reviewed
  change, made only after checks (a) and (b) pass (section 3.12). It is not designed for here.
  Because the data is pseudonymous, the Feedback Team's gates apply.

The triggers, the same for both channels: a delivery that ends in one of these:

* a refusal;
* a held Enter;
* an unconfirmed outcome;
* an out-of-date card;
* an "answer at the machine" offer.

The privacy rule of #731–#733 holds throughout. Testers' prompts, code, session titles, file paths
and transcripts never leave their machines unless the tester explicitly attaches them to one report
after seeing them. The relay stays blind. No diagnostic, correlation id or report content ever goes
into a public issue.

#### 3.11.1 The local record (not gated; can be built now)

The full per-attempt record (section 3.2) and the redacted screen outlines (section 3.4) stay on the
machine. The phone's send ring (section 3.6) stays on the phone.

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
  * exception messages carrying paths.

#### 3.11.2 "Report this" (not gated)

A manual report is independent of telemetry and never needs telemetry consent.

* **What it attaches.** "Report this" on a failed card (#733's UX, #738's bundle) attaches that
  attempt's local record and outline, after the tester has seen them in the exact preview.
* **The correlation id.** The report includes the attempt's correlation id (section 3.11.3) **only
  if an automatic diagnostic was actually recorded and sent for that attempt**. The id is visible in
  the preview. Otherwise the id is left out, and the report still goes.
* **Reports from Settings.** A report sent from Settings lists the recent attempts that have an
  automatic diagnostic. The tester can see each id and untick it.
* **Inside the bundle.** The per-send correlation id of section 3.7 lives only inside the sealed
  bundle. It joins the phone's, relay's and machine's sides for triage.

#### 3.11.3 The automatic event (buildable now; enabled only after checks (a) and (b))

Product accepted the #741 amendment as written (11-07). Two checks must pass before the event is
enabled anywhere:

* **(a) The #732 independent security review.** Two model families review the schema, the install
  id, retention and the linking. Engineering is staffing it.
* **(b) The EEA/UK personal-data question.** It has no owner yet; Product asks the owner in the
  morning.

Until both pass, the build ships **default off with no uploads**. The slice is built now behind that
default. Further conditions (#1132, 11-07 04:38 PT):

* **Quality verifies** both reviews and the actual retention and access controls before admission.
* **Default-on may later be allowed per region.** Jurisdictions not cleared for default-on stay
  opt-in.
* **The install pseudonym is personal data.** It is pseudonymous personal data, not anonymous data.
* **The issue edits are not the approval.** The #732 design amendment and the privacy notice still
  need their own reviewed changes.

**Off is a tested invariant.** While diagnostics are off, whether by default, by the tester, or by
the kill switch:

* no event is built;
* nothing is queued;
* no network call is made to the pipeline;
* any queued events are cleared at the moment it turns off.

The slice includes two tests:

* One drives every failure trigger with diagnostics off. It asserts an empty queue and zero calls
  on a fake transport.
* The other turns diagnostics off with events queued. It asserts the queue is cleared and nothing is
  sent.

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
| `correlation` | a random 128-bit id, fresh for this attempt and never reused |
| `install` | #741's approved rotatable pseudonymous install id |

**The correlation id.** It is random and minted per attempt. It encodes nothing about the user,
session, screen or content. It is not derived from the install id or the per-send id, and it is
never reused. Only the local record keeps the link between it and the per-send id.

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
* raw exceptions or payloads;
* arbitrary strings.

The application SDK's automatic collection and identifying metadata are turned off. That covers
device name, user, IP-derived location, and auto-collected dependencies and exceptions.

**Limits:**

* **Dedupe.** At most one event per failure signature (reason + agent + CLI version bucket + shape
  type) per hour per install. Repeats only add to a count on the next event.
* **Daily cap.** At most 20 events a day per install, under #741's daily cap.
* **Offline.** Unsent events wait in a small queue: at most 50, none older than 24 hours, rebuilt
  from the schema at send time. The queue never blocks a delivery.
* **Kill switch.** `Diagnostics:DeliveryEvents=off`, set by machine policy or fleet config, stops
  collection and clears the queue.
* **Triggers.** Only the host's own delivery outcomes trigger an event. Nothing a phone, relay,
  agent or session prints can cause one.

**Retention and access.**

* Events, and any copies derived from them (exports, summaries, dashboards' stored queries), are
  kept 30 days and then deleted (#732).
* Access is least-privilege: named support staff only.
* SRE must verify the actual destination, its access list and its retention before the event is
  enabled anywhere.

**Consent UX** (subject to the owner's approval of #733's UX):

* The opt-in is offered at first run and in Settings, in plain words. It shows one real event,
  field by field, with no surprises:
  "Send diagnostics when an answer or reply doesn't get through. Each note says which step failed
  and a reason code, with a random id for this install. It is pseudonymous, not anonymous. It never
  includes what you wrote, your files, your screen or your session names."
* A "Diagnostics sent" list in Settings and the PWA menu shows each event's time (Pacific), its
  reason, and the exact fields sent.
* One-tap off stops collection and upload and clears pending records.
* "Reset my diagnostics id" rotates the install id at once.

#### 3.11.4 How one failure is joined across channels

1. The attempt fails. The machine writes the local record. If the tester has opted in, it also
   queues the event, carrying a fresh random correlation id.
2. The tester presses "Report this". The bundle carries the local record and outline. If the event
   was actually recorded, it also carries that correlation id, shown in the preview.
3. Triage joins the private report and the pseudonymous event by correlation id. Inside the report,
   the per-send id joins the phone's, relay's and machine's sides. Neither the id nor any
   diagnostic content is ever copied into a public issue.

### 3.12 Privacy review

This is a checklist that must be passed before the automatic event (slice 3) can be enabled. It is
not legal advice, and this document claims no legal sign-off. Items marked **human/legal** need a
person with authority to answer them. Product is asked (#741) to assign an owner for those items.

| # | Check | Who answers | Status |
| --- | --- | --- | --- |
| 1 | **Check (b).** The Feedback Team treats the random install pseudonym as pseudonymous personal data. Under what lawful basis may it be collected from testers in the EEA, the UK and elsewhere? Does default-on with a notice suffice there, or is opt-in required? | **human/legal**: a qualified reviewer that Product is to find (owner, 2026-11-07); not yet named | open, **gate** |
| 2 | Default-on with a notice versus opt-in: is a first-run notice with an off switch enough anywhere, or is explicit consent needed by region? The build stays default off until this and check 1 are answered. Turning the default on is then a separate, reviewed change (owner, 2026-11-07). | **human/legal** | open |
| 3 | IP address exposure at the endpoint: what does Application Insights (and anything in front of it) log or derive from the sender's IP? Is IP masking and location-off configured and verified? | SRE + #732 review | open |
| 4 | Linkability over time: events from one install share an install id until it rotates. Is a 30-day rotation plus the tester's reset enough? Do the correlation ids and the dedupe counts add any further linking? | #732 review | open |
| 5 | Linking to reports: a report that carries a correlation id ties a pseudonymous event to a signed-in tester's report. Is that acceptable under the notice, and is it shown plainly in the preview? | #732 review + owner (#733 UX) | open |
| 6 | **Check (a).** Schema leakage, install id, retention and linking (checks 3–5 and 7 feed it). Every field is an enumeration or a bucket. Do the adversarial redaction tests pass for both schemas? Is the SDK's auto-collection off? Does the pipeline reject unknown fields? | the #732 independent security review, two model families; staffed by Engineering | open, **gate** |
| 7 | Retention and deletion of 30 days for events and all derived copies, with least-privilege access, verified in the deployed resource | SRE | open |
| 8 | Interaction with the owner's 11-04 opt-in decision (#731 item 3), his 11-07 default-on wish, and his 11-07 decision that the default stays off now and that turning it on is a separate, reviewed change. The amendment is recorded in #732's §5.10 change and the privacy notice (slipgates.com/privacy, #762). | owner via #732 | open |
| 9 | The kill switch and the off switch: does turning it off clear pending records, and is that tested? | Quality | open |
| 10 | Who can read the events, and how support reads them for one tester's report without browsing others' events | SRE + Feedback Team | open |

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
     └─► (identical retry, any device) ─► same state as the first; never a second typing
```

Rules:

1. **Never half-answered as a final state.** A form that has some fields in is in "paused at field
   i", never in "failed". The host resumes it only when the screen positively shows *this* form at
   field i, with fields 1..i−1 committed (their tab marks, or a fresh read of what they hold). Until
   then it holds no keys and re-reads at a steady pace for up to 60 s.
2. **Resume first, then reset, and only when the screen is positively this exact form (owner,
   2026-11-07).** The owner chose both:
   * **First, resume** at the next field not yet committed (rule 1).
   * **If resuming fails, reset and redo.** The host presses Esc to cancel the form, so the agent
     hears "cancelled" and asks again, and then answers the new question with the same answer. The
     new question is a fresh request: the same answer digest goes through the full checks again,
     from field 1.
   * Both steps happen only when the frame reads positively as *this* form (heading, message, tab
     bar and field headings all matched). If the screen is anything else, there is no Esc and no
     key. The card stays "paused" and offers the phone's ways out (rule 5).
   * The redo is tried once only. If it fails, the card offers the ways out. It never loops.
   * #1137 leaves resume out of scope: its ledger refuses a second typing. Slice 8 adds both steps.
3. **Idempotent by request.** A request id (bound to sender, session and question, PR #1100) and the
   answer's digest identify a delivery. The record moves from all or nothing to a per-field record
   (`fields: [done, done, typed-unsubmitted, —]`), persisted on disk with the first outcome. Any
   device's identical retry joins the existing delivery. A *different* answer to a part-answered
   form is refused with "part of an earlier answer is already in; finish or reset it", never typed
   on top.
4. **Out of date retires the card.** The card is retired with a plain message ("This question was
   answered or closed at the machine"), never shown as a red "refused".
5. **Every failure has a way out on the phone** (#1123's acceptance criterion; #773 means each is an
   explicit button):
   * **Look again**: re-sync the question from the machine;
   * **Send as a message instead**: the answer goes as a reply, written as "Answer to 'X': …";
   * **Retry**: only when nothing was typed, or when resuming is safe.

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
    scrolled above the form during typing, and a field drawn differently when its Enter was refused.
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
   committed. A reset uses Esc only when positively on this exact form. The redo after it answers
   the agent's new question, through the full checks, and is tried once.
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
but stays off.

**Already in review: PR #1137** (host fix for #1123, sensitive, two-family review). It names the
failed form check, logs a word-free outline, re-reads for up to 2 s before a refusal, compares the
question, tab bar and hints without white space, retries a `ScreenChanged` write held to a fresh
read, and writes one `answer-attempt v=1` line per form answer. The slices below build on it and
do not redo it: slice 1a extends its line, slices 4 and 5 start from its tests, slice 6 builds on
its re-read and retry, and slice 7 on its white-space-free matching.

| Order | Slice | Sensitive? | Closes / part of |
| --- | --- | --- | --- |
| 0 | This design | no | part of #1132 |
| **1** | **The local record**, starting from #1137's `answer-attempt v=1` line and `FormCheck` names: allow-listed per-attempt record, reason keys, redacted outlines, ring buffer, the phone's and app's send ring, per-send id, triage recipe and tool, offer keys and counts, local health view (sections 3.1–3.10, 3.11.1). It can split into: 1a host record and keys (extends #1137's line to approvals, replies, Claude and Codex; reason keys use #1137's check names); 1b outlines; 1c client ring and per-send id (protocol); 1d health view. | yes (1a, 1b, 1c) | closes #1023; gives #998–#1014 their counts; part of #1128 |
| **2** | **"Report this" bundle**: "Report this" on failed cards attaches that attempt's record and outline after the preview (section 3.11.2) | yes (client and host) | part of #733, #738 |
| **3** | **The opt-in automatic event**, `delivery-failure/v1` (section 3.11.3). **Buildable now, enabled only after (a) and (b).** Default off. Kill switch. No network call while off. Tests that nothing is sent or queued while off. | **yes**: two-family review of the schema and linking | part of #741. **Gates before enabling:** (a) the #732 independent security review (staffed by Engineering); (b) the EEA/UK personal-data answer (no owner yet). Also #732's §5.10 amendment and notice, the owner's approval of #733's UX, and SRE's check of the destination and retention. |
| 4 | Failing-first tests: the three-field choice, text, choice form (#1101's ask), output streaming during a form (#1123), narrow window (#910), mid-word wrap (#848), tall form, markdown message. #1137 already adds the hyphen and mid-word wraps (04:21), wrapped hints (#910), a mid-redraw read and a retry after a scroll; slice 4 adds the rest. | no (tests) | groundwork for #1123, #910, #848 |
| 5 | Real-screen corpus and replay test, seeded first from #1137's test screens, then #510 and slice 1's outlines (section 6) | no | part of #1132 |
| 6 | Hold keys to the form's frame, and settle on the frame (sections 5.1–5.2), building on #1137's re-read and retry. **Approved by the owner (2026-11-07); ships only once Trust agrees** that the frame plus "nothing below it" is enough identity | **yes** (injector; Trust's agreement before merge) | with slice 8, closes #1123 and #1012 |
| 7 | Normalised comparison (section 5.3) | **yes** | closes what #1137 leaves of #910 and #848 (item 1) |
| 8 | Delivery state machine: per-field record; resume at the next field first, then Esc-cancel and redo once, only when the screen is positively this exact form (owner, 2026-11-07); persisted first outcome for any device (section 4) | **yes** (Trust review) | closes #1123, #1007, #1093; replaces #1101's proposal |
| 9 | Phone ways out: Look again, Send as a message instead, retire out-of-date cards (sections 4.4–4.5) | **yes** (protocol) | #1123's acceptance criterion; closes #1014 and #1010 (fetch the full question); part of #1012 |
| 10 | Questions survive a host restart: re-list, re-verify and keep the record | **yes** | closes #954 |
| 11 | Canary per CLI release, and pinning launched sessions to the last version that passed it (owner, 2026-11-07; section 6) | no (CI and VM); the launch change is host code | part of #1132 |
| 12 | Soak and chaos harness, and targets (section 7), with #604's experiment | no | closes #604; measures #467 |
| 13 | Coverage on the screen path: Other through the pinned input box; a no-schema `ask_user`; field limits | **yes** | closes #908, #887 (if supported), #1000; follows the #876 decision; #146 (say why Other is missing) |
| 14 | Structured-channel trial (time-boxed, run in parallel from slice 4): Copilot `--ui-server`/ACP, Claude stream-json/`--permission-prompt-tool`, Codex app server, for sessions the app launches. **If the trial works, those sessions use the channel by default** (owner, 2026-11-07); sessions a person started keep the screen path | spike: no; adoption: **yes** | if it holds, adoption closes #998, #1002, #1003, #1004, #1005, #1008, #947, #1001/#42 |
| 15 | Reply path parity: show what is in the composer and offer Clear and send, using the same frame-scoped reads | **yes** | closes #804; with slice 6, #467 |

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

* #749 (the Trust epic) reviews slices 1, 2, 3, 6, 7, 8, 9, 10 and 13.
* #773 is invariant 6.
* #76 (the HA connector's reply-box boundary) can reuse slice 6's frame reads later. It is not part
  of this plan.
* #731, #732, #733, #738, #739, #740 and #741: slices 2 and 3 follow their bundle format, consent
  flow and privacy rule. Slice 3 is gated on #732.

---

## 10. The owner's decisions, and the questions still open

### 10.1 Decided by the owner on 2026-11-07 (about 04:48 Pacific)

1. **Automatic diagnostics ship default off now.** Turning the default on is a separate, reviewed
   change, made only after check (a), the #732 two-family security review, and check (b), the
   EEA/UK personal-data question, have both passed (sections 3.11.3 and 3.12). Product is to find
   the legal reviewer for check (b).
2. **Part-answered forms: resume first, then reset.** The host resumes at the next field not yet
   committed. If that fails, it presses Esc to cancel the form and redoes the answer once. Both steps
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
2. **A legal reviewer for the EEA/UK question** (decision 1; check (b), section 3.12, item 1).
   Is a pseudonymous install id personal data there, and does it need opt-in? This needs a human or
   legal answer that we cannot give. Product is to find a reviewer; none is named yet.
3. **A structural outline in the automatic event** (section 3.11.3): yes, once #732 approves the
   exact fields, or never?
4. **Tall forms** (section 5.4): is a form whose heading has scrolled off answerable?
5. **Beta targets** (section 7): are 99.5% success and zero part-answered forms after 60 s the
   right targets?
