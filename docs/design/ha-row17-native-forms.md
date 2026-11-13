# S4 row 17: native named-field forms

Reconciliation for [#2400](https://github.com/danswett/agent-bridge/issues/2400),
under [#1396](https://github.com/danswett/agent-bridge/issues/1396).
This is an inventory and acceptance plan, not live evidence or a DONE claim.
It changes no runtime, wire contract, signing rule, approval, stop or push delivery.

## What row 17 retains

The old HA `f1` through `f12` helpers are positional per-field dropdowns, not
function keys. Dropping that workaround from the native experience does not drop
multi-field questions. Retain one native named-field form on every required client:
PWA, Windows, Apple (Mac and iOS separately), MCP agent and host CLI.
Optional HA remains a separate compatibility lane under
[#2407](https://github.com/danswett/agent-bridge/issues/2407).
Apple is parked under [#1882](https://github.com/danswett/agent-bridge/issues/1882)
and remains last in the implementation/rehearsal order.

The pass text below comes from Engineering's supplied summary of the S4 matrix,
[PR #2409](https://github.com/danswett/agent-bridge/pull/2409), head `75001259`,
from #2383. That matrix was not on the inspected main and was not read directly.
Do not treat its summary as a merged document or a completed rehearsal.

## Evidence boundary

Source snapshot: `9a0acf32e28fca6e03542c0b7b35a20498789581`.
The following source documents were read whole:
[parity](../parity.md), [Windows](../app-windows.md),
[Apple parity](../apple-parity.md), and the [checklist](../parity-checklist.md).
The UI/model files linked below were read whole for PWA, Windows and Apple.
Reading source establishes an implementation observation, not a passing test run.
No live client, real-host transcript or new screen capture was exercised here.

Incomplete reads are deliberately not promoted to Built:

- HA: read `HaConnector.Answers.cs` and `HaDecision.cs`; did not complete discovery,
  card rendering, daemon comparison or runtime/delivery traces.
- MCP: read `mcp/README.md` and `mcp/src/tools.mjs`; did not read server registration,
  pipe authorization or a complete question-to-answer tool trace.
- Host CLI: read the opening dispatch section of `Program.cs` through the HA
  commands; did not finish its remaining dispatch/help, service or delivery trace.
- Did not directly read the pending matrix or the linked issue bodies #55, #736,
  #1146, #1401, #1949, #45, #50, #161 or #1419; only Engineering's context supplied
  their relationship to #2400. Did not read the agent CLI compatibility test plan
  or complete existing form test suites. No cross-repository runtime trace was done.

## Reconciling the documents

| Sources that disagree or differ in scope | Disposition for row 17 |
| --- | --- |
| Parity section 2 calls historical forms Verified; Apple parity still lacks real-host acceptance. | Apple's client-specific acceptance boundary wins for Apple; parity's historical proof is not all-client proof. Keep implementation and live evidence separate. |
| Parity invariant 4 calls Other Verified; section 2 holds Other under #908. | Open for Product/#908. Section 2's narrower refusal boundary governs this inventory; the old invariant cannot authorize sending. This note does not decide #908. |
| Windows overview lists Other generically; its availability and sendability depend on the question and host. | Current question/capability guards and actual host outcomes win over an overview. A generic control description proves neither every form nor every Other value is sendable. |

Parity's audit is historical (its stated baseline is `2a25120`).
Its section 15 also predates the existing MCP directory; neither its old Missing
label nor the presence of that directory proves a native MCP answer surface.
Do not silently relabel old verification as a new all-client acceptance result.

## Client inventory

Built below means a named editor and answer-building implementation were inspected,
not that the row-17 live contract passed. Unknown means the complete client path
is not established by this read, even where partial source is present.

| Client | Inspected implementation / partial observation | Row-17 status and retained gap |
| --- | --- | --- |
| PWA | [Question UI](../../pwa/js/ui/question-view.js) and [answer model](../../pwa/js/answers.js): named choice, boolean, text, number and multi controls; typed named `fields` values. | Built; fresh five-field live proof, refusal and draft lifecycle evidence still required. Historical parity leaves multi-select untested live. |
| Windows | [Question UI](../../src/AgentBridge.App/QuestionView.cs) and [model](../../src/AgentBridge.App.Core/QuestionViewModel.cs): native controls for all five kinds, dictionary keyed by field name, validation before submit. | Built; fresh five-field live proof and draft/refusal evidence required. Existing Other descriptions are not a pass. |
| Mac | Shared [question model](../../apple/Sources/AgentBridgeAppleCore/BridgeQuestion.swift) and [SwiftUI actions](../../apple/Apps/SessionActions.swift): named bindings and all five editors. | Built shared editor; separate Mac real-host acceptance and question-draft lifecycle unproven. Parked, last, #1882. |
| iOS | Same shared model and SwiftUI actions; a shared implementation is not an iOS run. | Built shared editor; separate iOS real-host acceptance and question-draft lifecycle unproven. Parked, last, #1882. |
| MCP agent | [Tools](../../mcp/src/tools.mjs) return question data on reads; inspected reply sends text, not a named form answer. Server/tool exposure trace incomplete. | Unknown; establish the actual native named-field surface, supported write authority and answer path before planning implementation. Do not substitute free-text reply for form submission. |
| Host CLI | [Program dispatch](../../src/AgentBridge.Host/Program.cs) exposes session reads and serving in the inspected portion. A host service capable of receiving answers is not a CLI form editor. | Unknown; finish command/help and answer-path inventory. No native CLI form acceptance is established. |
| Optional HA | [Answer connector](../../src/AgentBridge.HomeAssistant/HaConnector.Answers.cs) still uses positional field selects; [planner](../../src/AgentBridge.HomeAssistant/HaDecision.cs) limits forms to one text-like field. | Unknown end-to-end; legacy compatibility is not the native replacement or row-17 proof. Replacement/runtime gap belongs to #2407. |

### What is, and is not, gone

The inspected PWA, Windows and Apple editors use names and typed values, not the
twelve HA helper entities. PWA-generated local DOM ids are not HA field selectors.
There is no observed dependency on the dropdown workaround in those editors.
This does **not** establish removal from every client or from the repository.

The HA connector still pins `_fN` entities and translates their positions back into
source field names. Its planner treats both text and number as text-like, so the
canonical form's two such fields exceed that compatibility path's one-field limit.
Do not remove or bypass those live compatibility controls in this documentation cut.
Whether optional HA gains a native replacement is a #2407 design/runtime question;
it cannot be recorded as passed by pointing at the old selectors.

## Live contract to retain

Use the checklist's P4 five-field question without Other, in one native surface.
The same values must appear under the same names in the actual agent transcript:

| Name | Kind | Offered / entered value |
| --- | --- | --- |
| `colour` | choice | Offer red/green/blue; choose `green`. |
| `confirm` | boolean | Choose `true`, not a string or choice label in the transcript. |
| `name` | Unicode text | Enter `Zoë 🚀`; retain the decoded Unicode text unchanged. |
| `count` | number | Enter numeric `3`, not text `"3"`. |
| `extras` | multi-select | Offer x/y/z; choose `x` and `z`, retained as both selections. |

One form must carry all five names together. A single-field demonstration, five
unrelated prompts, a text-only reply, positional helper values or a design decision
cannot satisfy this contract. Screens must show the fields and selections; keep
the transcript's actual values, not merely a delivery toast or result code.
No Other value is part of this positive contract. Preserve existing required,
type, capability, current-question and at-machine guards; this note relaxes none.

Carry over the checklist boundaries without expanding their historical evidence:

- E2-03 supplies P4 and the expected transcript fields. Its existing actor is PWA;
  repeat the contract per client rather than asserting that row already covers all.
- E2-05 supplies P4o: an Other-enabled multi-field question must not offer remote
  typing it cannot deliver; explain the refusal and direct the person to the machine.
  Keep the refusal and unchanged transcript as evidence, not a successful answer.
- E2-08 supplies the unsent-draft/restart and answer-at-terminal checks. Record
  what remains after host restart and how an unused draft is marked once the
  terminal answers; do not infer question-draft retention from reply-draft support.
- E2-09 is Claude-specific P12 single choice (`Pick one=beta`, including its review
  Enter); it is an adapter check, not five-field proof.

Run the five-field contract three times per client per rehearsal stage, as required
by the supplied matrix pass text. Preserve stage and run identity; do not combine
three runs across stages or reuse one client's run for another.
Mac and iOS need separate records even when their UI/model source is shared.
If a client cannot expose or deliver the form, record that lane as blocked/unknown,
not skipped-to-pass and not covered by another client.

For each run keep client/host builds, agent and version, stage, run number,
session/question identity, named field screen(s), submitted values, actual
transcript values and outcome. Keep refusal and draft checks with their own
screens/transcript observations, including any no-send result.
Store evidence through the existing rehearsal process with secrets removed;
this note supplies no evidence bundle and no new acceptance waiver.

## Bounded follow-ups

Each proposed code/document cut remains at most 250 changed lines. These are
follow-ups, not implementation authorization or claims that tests already exist.
Engineering must reconcile the completed traces before choosing code cuts.

| Cut | Bounded outcome / prerequisite |
| --- | --- |
| PWA contract evidence | Reuse the native named editor; add only missing focused contract assertions, then collect the required live runs and refusal/draft evidence. |
| Windows contract evidence | Separate exact-head cut for the same five-field, refusal and draft contract; do not mix approval rendering or delivery changes into it. |
| MCP inventory first | Finish server registration, authority and named-answer exposure trace; reconcile the stale parity claim. List the smallest retained surface gap before a tests-first implementation cut. |
| Host CLI inventory first | Finish command/help and delivery trace; distinguish JSON read exposure from a usable native named-field form. Resolve the retained gap before implementation. |
| Optional HA, #2407 | Inventory discovery/card/runtime and decide the replacement boundary without breaking the retained adapter. Old dropdowns cannot supply the no-workaround live proof. |
| Apple, #1882, last | Separate Mac and iOS real-host acceptance; first reconcile question-draft lifecycle against the shared view's reset behavior. Do not broaden parked Apple work here. |
| Product reconciliation | Resolve the Other invariant/section-2 disagreement under #908; keep the positive row-17 form Other-free meanwhile. |

Any later change touching signing, protocol, approval, stop or push delivery needs
the Trust pair and both model families on its exact head, not this note as consent.
Later code cuts need tests first and Quality's hosted Windows/Ubuntu assertion-red
proof and final-push admission. No local builds or quality run are authorized here.

## Completion boundary for this cut

This documentation cut is checked by whole-file reading and offline
`git diff --check`, with the stat, full head SHA and validation-log hash handed to
Engineering and Quality. It has no test/build execution or hosted-red requirement.
Both model families read the frozen documentation head whole; Quality owns
admission, PR creation, gates and merge.
Row 17 remains open until its retained client gaps and live contracts are evidenced.
