# Agent CLI compatibility test plan

Status: plan. Part of #1138 (lab), under #1132 (host answer delivery). Builds on the clean-VM
runner of #736 and the design in `docs/design/host-answer-delivery.md` (#1135).

## 1. Purpose, cadence, owners

The host drives each agent CLI by reading its screen and typing keys. A new CLI build can
redraw a form in a way the host does not recognise (1.0.105, #1123, #1137) and break delivery
for every user at once. Sessions the host launches are pinned to the last Copilot CLI version
that passed this plan. This plan is how the pin moves forward, quickly and safely.

| When | What runs |
|---|---|
| Nightly | **Detect.** Check for a new Copilot CLI, Claude Code and Codex release. Record version, date, release notes link. Nothing else if there is no new version. |
| Every new version | The full plan below, once per version and environment. Copilot CLI first; Claude Code and Codex follow (section 8). |
| Every host release that touches delivery | Layer A only (corpus replay), against the current pin. |
| Every field failure | Add the screen to the corpus (section 5), then rerun layer A on candidate and pin. |

| Owner | Owns |
|---|---|
| Quality | This plan, the corpus, the report, the promote/hold call |
| Engineering | Matcher and delivery fixes found by a failing case; automating manual cases |
| SRE | Lab VMs, nightly detect job, spend cap, disposable accounts |
| Product | Wording a person sees when the pin holds or a session runs an unvalidated version |

Layers used in the cases:

* **A. Corpus replay.** Saved real screens replayed through the host's form reader with no
  terminal and no CLI. Fast and deterministic; runs on every version.
* **B. Live host smoke.** The candidate CLI in a real console on a lab VM, launched through
  the host, driven by a scripted client over the host's own protocol.
* **C. Phone/PWA end to end.** A real PWA in a browser (and one phone) through the lab relay.

## 2. Pass and fail rules

1. **Any failed case holds the pin.** There is no "mostly passed". An owner-accepted exception is
   written into the report and the case marked `waived` with an issue number and the owner's name.
2. **Safety invariant (hard).** The host never types a key unless the screen is positively that
   question's form at that field. A key typed into the wrong place, a blind Enter, or a key typed
   on a screen that was not positively the form is a **safety failure**: the pin holds, the
   candidate is blocked from every channel, and an issue is filed at once with the capture.
3. **Allowed endings for any answer attempt:** `delivered`, `out of date` (card retired with a
   plain message), or `failed` with a one-tap retry. Not allowed: a half-answered form left
   behind, a wedged session, a card saying refused when the CLI took the answer (#1093), an
   attempt that never ends.
4. **Refusing is acceptable only on a steady screen and with a named reason key.** A refusal of a
   shape the pin delivers is a regression, even though it is safe.
5. **Every attempt leaves its record** (T-31) with a reason key on every non-delivered ending.
6. **Compared with the pin, not with perfection.** A case that already fails on the pin is a known
   gap with an issue; it does not hold the candidate but must not get worse (same or better
   outcome and reason key).
7. Timing: acknowledgement on the phone within 2 s of send; delivery confirmed within 15 s on the
   lab VM (the host's own confirm limit); a 6-field form within 60 s.

## 3. Test matrix

| Axis | Values |
|---|---|
| OS and CPU | Windows 11 x64 (full B and C); Windows 11 Arm64 (full B; C smoke subset of Q-01, Q-03, R-01 only) |
| Window size (columns x rows) | narrow 60x25; standard 100x30; wide 200x30; tall 100x60; short 100x15 (form taller than window) |
| Terminal host | Windows Terminal (default); legacy conhost. Narrow and short sizes run in both; other sizes in Windows Terminal, plus one standard-size pass in conhost |
| CLI version | **candidate** (new), **pin** (current), **previous** (the pin before it, to prove rollback works) |
| Agent CLI | Copilot CLI (all cases); Claude Code (cases marked CC); Codex (cases marked CX) |

Budget per candidate: layer A on every size and all three versions; layer B on
{x64, Arm64} x {narrow, standard, short} x {candidate, pin}, with conhost as above; layer C on
x64 standard, candidate and pin (full), plus the Arm64 smoke subset (Q-01, Q-03, R-01) at standard
size. `previous` runs layer A plus one live pass (L-01, Q-01, Q-06, R-01).

## 4. Test cases

Format: **ID** name. Steps. Expected. `[Auto|Manual | layers]`. A Manual case is on the list to
automate as the lab's slices land.

### 4.1 Launch, session, events

**L-01 Launch and discover.** Install the candidate on a clean VM, sign in with the lab account,
launch a session through the host. Expected: it is listed within 10 s with the right agent,
folder and version string; no first-run screen the host does not know (a new one is a failure and
a capture). `[Auto | B]` CC CX

**L-02 Session list and status.** Start 3 sessions: idle, working, waiting on a question.
Expected: the phone shows each state within 5 s of a change; an ended session leaves the list.
`[Auto | B, C]` CC CX

**L-03 Hook and event intake.** Run a turn that calls a tool, asks a question, and ends.
Expected: prompt, tool, question and stop events arrive in order with the fields the host reads;
the CLI accepts the hook's reply (Copilot: `ask_user` hook's `permissionDecision: allow`). A
changed hook schema or a hook not fired fails. `[Auto | B]` CC CX

**L-04 Busy and idle detection.** A long tool run, a streaming answer, an idle prompt. Expected:
busy during the first two, idle only at a prompt that takes text; a spinner or stream never reads
as idle; no reply is typed while busy. `[Auto | A, B]`

**L-05 Compaction.** Run until the CLI compacts. Expected: session stays listed, returns to idle,
hooks keep firing, a later question is answerable. `[Manual | B]`

**L-06 Resume.** End a session and resume it with the CLI's own resume. Expected: it links to the
same session or lists as new with a clear label; a later question is answerable or refused
`answerAtMachine`, as on the pin. `[Manual | B]`

**L-07 Delegates.** A delegate streams output while the parent asks a question. Expected: delegate
output does not break the form read (steady-screen rule); answered, `out of date`, or
`answerAtMachine` exactly where the pin does. `[Manual | B]`

**L-08 CLI banners and update prompts.** Capture first launch, an update-available banner, a
what's-new screen, a trust-this-folder prompt, a rate-limit or model notice. Expected: none is
taken for a form or an idle prompt, nothing is typed into them, and the person is told the
session needs the machine. The CLI must not self-update mid-session; if it does, report it.
`[Manual | A, B]` CC CX

**L-09 Auth and token expiry (#915).** Lab account only: expire or revoke the token, continue a
session. Expected: the host shows needs-sign-in, types nothing into the sign-in screen, and
recovers when the lab account signs in. `[Manual | B]` CC CX

### 4.2 Replies and questions

**R-01 Reply delivery.** Send a plain reply from the scripted client and the PWA to an idle
session. Expected: typed once, Enter as its own send, confirmed from the prompt hook; the same
request id sent again does not type twice. `[Auto | B, C]` CC CX

**R-02 Reply refused when unsafe.** Reply while the composer holds text, while busy, and while a
form is open. Expected: not typed; the reason key says which; the card shows what is there (#804).
`[Auto | B]`

**Q-01 Single choice.** 3 options, then 9; choose first, middle, last. Expected: `delivered`;
`human_response.recorded` matches. `[Auto | A, B, C]` CC

**Q-02 Multi-choice.** Checkbox list; tick none, some, all; defaults pre-ticked. Expected: only
rows whose state must change are toggled; empty only where `minItems` allows. `[Auto | A, B]` CC

**Q-03 Text.** A short word; a 300-character answer that wraps; one with a hyphen and a non-ASCII
letter. Expected: Enter goes only after the words show on screen; recorded answer identical.
`[Auto | A, B, C]`

**Q-04 Number with min and max.** At min, at max, inside; decimal and negative; one outside.
Expected: plain decimal delivered; an outside value is refused before any key. `[Auto | A, B]`

**Q-05 Other option.** Other in a single choice, a multi-field form, and a checkbox list.
Expected: same as the pin (today refused before any key with `otherNotSendable`, #908, #876,
#146). If a candidate draws Other differently, capture it; this plan does not unblock Other.
`[Auto | A, B]`

**Q-06 Multi-field form.** 2, 4 and 6 fields mixing choice, boolean, multi, text, number.
Expected: all fields in schema order, each committed once, confirmed field by field.
`[Auto | A, B, C]`

**Q-07 Six-label long tab bar.** 6 fields with long headings at 100 and 60 columns, so the bar
wraps onto two rows. Expected: recognised at both; a bar where a heading cannot fit is refused
before any key. `[Auto | A, B]`

**Q-08 Wrapped questions.** Text broken mid-word, after a hyphen (`ag-` / `ent`), after a slash,
between CJK characters; markdown the CLI renders away (bold, code, lists); an ellipsis.
Expected: recognised (#1137, #848); card and screen may differ only in white space and
rendered-away markdown. `[Auto | A, B]`

**Q-09 Long descriptions.** Descriptions of 1, 5 and 20 lines; option labels near the row width.
Expected: recognised, or refused on a steady screen with a reason key; never a blind key.
`[Auto | A, B]`

**Q-10 Narrow window.** 60 columns: key-hints row wrapped across rows (#910), headings
truncated. Expected: recognised, or refused before any key with a reason key telling the person
to widen the window. `[Auto | A, B]`

**Q-11 Short window and scroll.** 100x15, form taller than the window, heading scrolled off.
Expected: refused before any key, or recognised if the CLI keeps the heading visible. Never keys
against a screen with the heading missing. `[Auto | A, B]`

**Q-12 Resize mid-answer.** During a 4-field answer, resize 100 to 60 columns after field 2, and
back. Expected: finishes after a steady re-read, or stops before the next key as `failed`; no
field repeated, no Enter without the form. `[Auto | B]`

**Q-13 Animated rows.** Spinner, thinking row, streaming output above the form, a delegate's
output, a clock in the status line, while an answer is typed, at 3 output rates. Expected: re-read
until steady and deliver, or end `failed` before any further key; never part-answered (#1123,
#1012). `[Auto | B]`

**Q-14 Scroll and repaint.** Scroll history up and down, and force a repaint (Ctrl+L) during the
form. Expected: as Q-13. `[Manual | B]`

**Q-15 Part-answered form resume.** Kill the host after field 1 is typed; restart; resend. Also:
a person typed field 1 by hand at the machine. Expected: the host reads the form, sees field 1
taken, then finishes or resets it as designed; ends `delivered` or `failed` with retry; never
stuck on "already typed once" (#1101, #1007). `[Auto | A, B, C]`

**Q-16 Stale card.** Answer at the machine, then from the phone. Also ask a second question and
answer the first card. Expected: `out of date`, card retired with a plain message, no key typed.
`[Auto | B, C]` CC CX

**Q-17 Esc and cancel.** Cancel a form with Esc at the machine; press Esc mid-answer. Expected:
card retires, nothing more typed, session returns to idle and takes the next reply. `[Auto | B]`

**Q-18 Duplicates and two devices.** One request id sent twice, and from two devices. Expected:
typed once; both devices see `delivered`, not "refused" (#1093). `[Auto | B, C]`

### 4.3 Permission prompts

**P-01 Approve.** Shell command, path access, file edit, file read, URL, MCP tool, custom tool,
each approved from the phone. Expected: keys only against the matching prompt; the tool runs.
`[Auto | A, B, C]` CX

**P-02 Refusal.** Deny each kind; also with `permissions.remoteApproval` off. Expected: deny
reaches the CLI and the tool does not run; with the switch off, `answerAtMachine` and no key.
`[Auto | A, B]`

**P-03 Unsupported prompts.** Hooks, memory, workflows, extensions, run outside sandbox, a prompt
with unreadable details. Expected: `answerAtMachine`, zero keys. `[Auto | A, B]`

**P-04 Escaped characters and concurrent prompts.** A control or right-to-left character in the
prompt; two prompts waiting. Expected: Approve takes the two-step confirmation; the listed prompt
is the one on screen. `[Auto | A, B]`

**P-05 Claude Code and Codex shapes.** Claude `AskUserQuestion` (1 to 4 questions, choice and
multi); Codex hook approval. Expected: as `docs/host-answers.md`. `[Auto | A, B]` CC CX

### 4.4 Connection, restart, observability

**N-01 Slow relay.** Add 1.5 s latency and 5% loss between PWA and relay, then host and relay.
Expected: every delivery ends in an allowed state; no double typing; the PWA never says refused
for a delivered answer. `[Auto | C]`

**N-02 Offline relay and reconnect probe (#1130).** Cut the PWA's network after a send (half-open
socket), restore after 10 s. Expected: probe after 5 s, drop and reconnect within 3 s more,
one resend with the same request id, host types at most once; the card says "Connection changed.
Trying again.", not raw relay text. `[Auto | C]`

**N-03 Host restart mid-answer.** Restart the host while field 2 of 4 is typed, and while a reply
waits for its Enter. Expected: the attempt ends `delivered`, `failed` with retry, or `out of
date`; the question stays pending on the phone (#954); a taken field is not typed again.
`[Auto | B, C]`

**N-04 CLI crash and exit.** Kill the CLI during an answer. Expected: `failed` with a reason key;
session shows ended; no key goes to the shell that replaces it. `[Auto | B]`

**T-31 Delivery record per attempt (#1132).** For every case that sends an answer, reply or
approval, read the host's per-attempt record. Expected: exactly one record per attempt, with
request id, CLI version, shape (for example `choice3+text`), outcome, reason key on every
non-delivered ending, reads and re-reads, stage timings. **Privacy invariant: no screen,
question, option, answer, path or command words in the record, the screen outline, the host log
or relay metrics; counts, kinds, lengths and positions only.** Check: seed every case with unique
random marker words and fail if any marker appears in any of those. `[Auto | B, C]` CC CX

**T-32 Outline on refusal.** Force a refusal. Expected: a redacted outline is kept in the local
buffer and stays on the machine unless the tester sends it. `[Auto | B]`

## 5. Real-screen corpus

The corpus is the plan's memory. Each entry is one real screen from a real CLI build.

**An entry** is a folder `tests/corpus/<agent>/<cli-version>/<id>/` with:

* `screen.json`: the visible rows as read from the console, the window columns and rows, and the
  terminal host (Windows Terminal or conhost);
* `card.json`: the question or permission card the host was given;
* `expect.json`: the expected reading (form with field index and kind, or not-a-form with the
  check or reason key) and the keys the host may send, if any;
* `meta.md`: CLI version, date, window size, how the screen came about, issue number, who redacted.

**Taking a capture.**
1. In the lab, use the injector's read-only screen read (it sends no key) to save the visible
   window at the moment of interest: form opened, each field reached, mid-typing, wrapped,
   mid-animation, after a refusal. For animated screens take two reads 200 ms apart.
2. Save the per-attempt record and outline for the same moment.
3. For each new question shape, capture at every window size and both terminal hosts.

**Redaction.** Lab content is made-up, so lab captures need none. A capture from outside the lab
(a field failure, sent with the person's consent) is redacted by script: every letter and digit
in question, option, answer and path text becomes a same-width marker of the same character class.
Layout characters (rules, arrows, boxes, hints such as `enter accept`) stay; wide characters keep
their width. A second person checks the result before it is committed. No account names, hosts,
tokens, repository names or paths from outside the lab are kept.

**Storage.** In the repository: lab-made and redacted entries only, as small text files. Anything
un-redacted stays in the lab's storage and is deleted after 30 days.

**Replay.** The corpus test feeds each `screen.json` and `card.json` to the host's form reader and
compares with `expect.json`. A second check holds the safety invariant: for every screen the
reader calls not-the-form, the plan of keys must be empty. Replay runs on every host build and on
each candidate's new entries.

**After every field failure (required).** A failure is a refusal, a part-answered form, a wrong
"refused" message, or any #1132-family issue. In the PR that fixes it:
1. add the failing screens and the reason key to the corpus;
2. add a case here if the shape is new (next free ID);
3. rerun layer A on candidate, pin and previous, and note the run in the version's report.

A fix without a corpus entry is not complete.

## 6. Report, promote, hold, roll back

**Where the pin lives.** One file the host reads: `config/agent-cli-pin.json`, created by the
first lab slice (until then the pin is the version the host's launch code names, and the first
report records it). Per agent: `{ "pin", "min", "max", "previous", "validated": [...] }`. Moving
it is a normal PR that links the report, shipped through the normal release channel (signed,
staged per #579). The host warns when a hosted session runs a version outside `min`..`max` or not
in `validated`, and never blocks a person's own use of the CLI.

**Report per CLI version:** `docs/test-plans/reports/<agent>-<version>.md` (or lab storage with a
link on #1138) holding:

* header: agent, version, release date, release notes link, run date, host build SHA, runner,
  cells run;
* result table: case ID x cell: `pass`, `fail`, `waived`, `flaky` or `not run`; for each fail the
  reason key from the per-attempt record and a link to the capture;
* diff against the pin: cases whose result or reason key changed;
* safety numbers: keys typed on a non-form screen (must be 0), part-answered forms (0), privacy
  marker hits (0);
* numbers versus the pin: answer success rate, p50 and p95 time to acknowledgement and to
  confirmed, refusals by reason key;
* new corpus entries;
* decision (promote, hold or rollback) and who made it.

**Promote.** All cases pass under section 2, the report is complete, safety numbers are zero, a
second person reviewed it. One PR sets `pin` to the candidate, `previous` to the old pin, adds the
candidate to `validated`, widens `max` if needed. Merge, staged rollout, SRE watches refusal keys
for 48 h.

**Hold.** Any failure. The pin stays. File one issue per distinct failure with case ID, capture,
reason key and diff against the pin; tell the team and post the report on #1138. After the fix,
rerun the failed cases; rerun the whole plan if the fix touched the matcher or delivery code.

**Roll back.** If a pinned version regresses in the field (a refusal key rises, #1132-family
reports, a server-side CLI change): a revert PR sets `pin` back to `previous` and goes on the fast
path; announce on #1138; verify L-01, Q-01, Q-06 and R-01 on the restored version in the lab. Keep
a `previous` chain of at least two versions. A version newer than `max` gets a warning, or a
refusal of hosted launch where decided, and the person's own terminal is never touched.

## 7. Lab constraints

* **Disposable accounts only.** One lab account per agent, with no access to any real repository,
  organisation or secret. CLI tokens (#915) belong to the lab account, live in the lab's own
  secret store, and are never in the repository or in a capture.
* **Never the owner's machines or anyone's real machine.** Clean VMs from the #736 runner, rebuilt
  per run, no real Home Assistant, no real folders.
* **No production relay deploys.** The lab has its own relay. Any production relay change needs
  the owner's approval and is outside this plan.
* **Made-up content.** No real data in prompts, answers or captures.
* **Cost cap.** Detect is free; the plan runs only for a new version. SRE sets a monthly cap on VM
  hours and model use with the owner. At the cap the run stops, reports `not run`, and the pin
  holds. Model cases use the cheapest model and short prompts; layer A needs no model. Arm64 and
  conhost full cells run only after x64 standard passes.
* **No flaky passes.** A failing case may be rerun once; a pass on rerun is `flaky`, carries the
  first failure's reason key, and counts as a failure until fixed or waived.
* **Evidence retention.** Lab captures and records are kept 30 days; redacted corpus entries stay
  in the repository.

## 8. Claude Code and Codex

Same plan and rules, with their own corpus folders. Cases marked CC or CX apply, using that
agent's forms (Claude `AskUserQuestion`; Codex hook approvals). Their pins use the same file and
procedure and start report-only (warn, do not refuse) until each has a corpus and a first full
report.
