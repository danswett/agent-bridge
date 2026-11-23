# Row 17: typing a five-type form into a real Copilot form (design note)

**Status: design note. Docs only: nothing here is built, enabled or run.** It states the gap left open by the row 5 and
row 17 evidence work, the options, and a recommendation. **Only option 4 is a change for the owner** (it departs from
[host-answers.md](../host-answers.md)); options 1 to 3 add tests and fixtures and change no behavior. Part of
[#1396](https://github.com/danswett/agent-bridge/issues/1396); companion to
[ha-row17-native-forms.md](ha-row17-native-forms.md) and the row 5 and row 17 evidence in
[ha-s4-evidence-matrix.md](ha-s4-evidence-matrix.md). No live run is proposed in this change.

## The gap

Row 17 keeps one native named-field form on every client: a choice, a multi-select, a boolean, text and a number, answered
by name. The host and the PWA validate that whole form the same way (#4010). What no test does is **type** such a form
into a Copilot form through the host's answer path, because the host test harness cannot draw the multi-select field.

## What exists

- **The keys are specified.** [host-answers.md](../host-answers.md), "The keys (Copilot)": a form is typed field by field, each
  field its own writes, held to the rows of the form at that field (`CopilotFormScreen`, read from the CLI's `MIo`, `Uxn`,
  `Da` and `Ft`). For a multi-select: Up (N-1) times to the top row, one walk down the checkbox list, Space only on rows whose
  state must change (a schema default opens some ticked), each toggle its own send 350 ms apart; Enter alone when the defaults
  are the answer. An "Other" in a checkbox list is refused (the same document's table of Copilot forms).
- **The planner implements it.** `src/AgentBridge.Host.Delivery/Copilot/CopilotPlanner.cs`, `Multi`: it maps the values to
  rows, refuses an "other" and counts outside `minItems`/`maxItems`, and builds the key list above.
- **Tests that touch it.** `CopilotPlannerTests` has `A_checkbox_list_ticked_outside_its_item_counts_is_refused_before_any_key`.
  `AnswerFlowTests` models Copilot's form screen (`Framed`, `FieldsScreen`, `AskFieldsAsync`) for lists, Yes/No, text and
  number boxes, and keeps one recorded screen (`TallScreen`, read with `agent-bridge-injector --screen`, #1123 and #1193).

## What is missing

| Gap | What it is |
|---|---|
| **A. The multi key walk is untested.** | Nothing asserts the planner's keys for a multi-select: the Up count, the walk, Space only on changed rows, defaults ticked, nothing to toggle. This is a plain planner unit test and needs no screen. |
| **B. The harness has no checkbox screen.** | `FieldsScreen` draws no checkbox rows, so a multi-select field cannot go through `AnswerFlowTests`, and `CopilotFormScreenTests` has none either. The repository holds no drawing of a Copilot checkbox row, ticked or not, nor of the caret on one. Only the hint words are known: `CopilotFormScreen` accepts "space toggle" among its hints and a planner comment quotes the line. (The one `☐` in the tests, in `AgentReplyFlowTests`, is another prompt's screen, not Copilot's checkbox list.) |
| **C. Two facts are assumed, not seen live.** | That Up stops at the first row of a checkbox list as it does in a single-choice list (host-answers.md says so: "assumed rather than seen live"), and what a ticked default looks like. Also, by design, **a checkbox list's ticks are never read** from the screen ("What no plan can see"), so a multi-select is confirmed only by the transcript record, never by the screen. |

## Options

| Option | What it is | Cost | Risk and what it proves | Behavior change |
|---|---|---|---|---|
| **1. Model it from the CLI source.** | Extend the harness with a checkbox-list screen: a caret that stops at the top, Space toggling the row under it, Enter accepting, drawn as the CLI's source draws it. | Small to medium: a model of about 100 to 150 lines and the five-type host test. | Proves the planner and the host's write loop against **the model**, not the CLI. The model would encode the same two assumptions as the planner (gap C), so it can pass while the real CLI differs. Must be labelled model-based. | None. |
| **2. Record real screens (goldens).** | Capture real Copilot forms with `agent-bridge-injector --screen`: a five-type form, a multi-select with defaults ticked, Up pressed at the top row, at a wide and a narrow console. Replay them as fixtures, as `TallScreen` was. | Medium, and **live**: it needs a disposable VM and SRE, and the owner's say-so per [AGENTS.md](../../AGENTS.md) (live tests run on disposable VMs). | Real evidence for gap B and C. Pinned to the CLI version captured (1.0.105 or 1.0.106 today); needs a re-capture when it changes. | None. |
| **3. Both, in order (recommended).** | Do the planner unit tests (gap A), then option 1 behind a seam where a recorded screen replaces the modeled rows, then option 2 as a named later step. The matrix marks the typed five-type case "model-based" until the goldens land. | Smallest first step; the live step is separate and can wait. | Gets the host loop tested now and keeps the claim honest; the live step turns the claim into evidence. | None. |
| **4. Refuse multi-select until it is walked live.** | The host lists a Copilot multi-select form `answerAtMachine`, as it does for free text, instead of typing it. | Small code change plus doc and test changes. | Removes typing that has never been seen live, at the price of a shipped feature: a person could no longer answer such a form from a phone. **OWNER CHANGE:** host-answers.md specifies that multi is typed; refusing it departs from that document. | **Yes.** |

## Recommendation

**Option 3.** In order, each its own small PR and none of them behavior-changing:

1. **Planner unit tests** for the multi walk (gap A): defaults ticked and nothing to toggle, a toggle on each side of the caret,
   the Up count, a refused "other", the count limits. No screen involved.
2. **A checkbox-list model in the harness**, behind a seam, and the five-type form typed through `AnswerFlowTests`. The test
   asserts the keys and the recorded answer, never ticks read from the screen, since the host does not read them.
3. **Update the matrix** so the typed five-type case reads "model-based".
4. **The goldens step (live; not part of this change).** SRE, on a disposable VM and with the owner's say-so, captures the screens
   listed under option 2 and checks the Up-at-the-top assumption on the real CLI. The modeled rows are then replaced by the
   recordings and the matrix claim becomes evidence.

Option 4 stays on the table only as the fallback if the goldens show the assumed keys are wrong, or if Quality or the owner
prefers to refuse what has not been seen live. It is **not** recommended now, and it needs the owner's decision.

## What this note does not do

It runs nothing live, types into no real session, adds no code and changes no behavior. It does not claim the five-type form
works in a real Copilot form: that stays open until the goldens step. It does not touch the 30 live approvals or the MCP
answer tool, which remain open under row 5.
