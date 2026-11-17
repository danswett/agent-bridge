# Durable replies: owned-text cleanup and draft-independent transport

Status: proposed contract, not implemented. Issue #2780, parts B and C.
Sensitive: yes, host input path; requires two independent model-family design reads.
This document alone does not close #2780 or assert its acceptance tests pass.

## Scope and inputs

This spec-only cut starts at main `8053c86951b434014fbe8ec9d78ae6c8af82f9bb`.
Carried changed lines: zero. Predecessor code is referenced, not copied or merged.
Implementation must reconcile these inputs before any production change:

| Input | Exact head | Contract retained |
| --- | --- | --- |
| C1, `builder/ab-2780-ingress-141bea4e` | `32082e576aa41eb6a44b9bca66d195a94c7d08d1` | Durable ingress, per-session FIFO, restart fencing, acknowledgements by sender rid |
| A, `builder/ab-2780-requeue-ba95a89c` | `ce789f6c5ceddd1af2c853a1a9e61f66b1301145` | Idle hold, no-effect Busy/Refused retry, draft retention, per-attempt request identities |
| #2808, `sg-ab2808-inputbox-82f49a6f` | `f8039ab017e84a5e552964f307d0921722388986` | Native prefix evidence, SHA-256, screen guards, fingerprint deduplication and expiry |

Part B adds a cleanup proof and its connection to durable requeue.
Part C proposes a CLI-supported, non-terminal submission channel.
No authorization, trust, permission-answering, attachment-limit or 16 KiB change.
No production code, executable tests or fixtures are introduced here.
RED/GREEN commits are not applicable to this documentation-only cut.
The scenarios below are requirements for later failing-first implementation tests,
not executed tests or evidence of current CLI support.

## One definition of agent-owned text

Reuse #2808's `InjectionResult` and `WorkerInjector.ReplyWriteEvidence`; do not
add a second ownership matcher in HA or infer ownership from a transcript.
The payload is the exact one-logical-line `InjectableText.Collapse` result sent
to the native worker, not the original unnormalised message.

`TextWritten = n` means a native-proven prefix of that payload's UTF-16 code
units. For a partial native write of r key records, n is ceiling(r / 2):
a key-down has an effect even if its key-up was not written. Bounds must hold.
`TextFingerprint` is uppercase hex SHA-256 of UTF-8 bytes of that exact prefix,
using the same `InjectionResult.Fingerprint` helper and encoding behaviour.
An odd record count or split surrogate is not rounded to another text value.
Missing, malformed or unsupported evidence is unknown, never a zero-length proof.
`EnterWritten` is separate: false proves no submit key was written; null or true
does not permit automatic resubmission. Native writes prove input records,
not that the CLI has consumed them.

Carry the evidence under the session id, process id AND process start identity,
sender rid, persisted attempt number, host request id, and delivery admission.
A matching payload or screen without that provenance is an unknown draft.
Compare the complete anchored box using #2808's rendering-aware `Remainder`
semantics; do not broaden them to substring, suffix or fuzzy matching.
Spaces lost in rendering, ambiguous wrapping or unsupported Unicode renderings
cannot grant ownership. A proven person edit invalidates ownership even if
the person restores the identical visible text (the edit-away/edit-back case).

Keep #2808's independent deduplication identity: SHA-256 of its existing JSON
serialization of peer, session id, text, attachment references and mode.
It is not the native prefix fingerprint and is not the sender rid.
Reuse its bounds: eight outstanding fingerprints per session, 64 sessions,
two minutes waiting for first key, two-minute uncertain fingerprint retention,
and 15-second recovery patience, all cancellation-aware.
Expiry evicts bounded metadata; it never proves cleanup, absence of submission,
or permission to replay. Duplicate rid receipts remain the durable inbox's job.
Loss of the in-memory fingerprint cache on restart must not defeat that fence.

## B: guarded clear, with proof before and after

Only the current FIFO head may request cleanup of its own attempt. A later
request may acquire admission for clear-only reconciliation as in #2808,
but cannot submit the old text or release the FIFO head without the same proof.
No recovery may press Enter after a final Unconfirmed receipt.
Cleanup is a separate, explicitly correlated operation, not a resubmission.

Before any clear, all of the following must be true together:

1. The original native worker has exited and its input records have been drained
   or fenced from later consumption. There is no concurrent sender or recovery.
2. The same live Copilot process and session remain idle, outside any question,
   startup/trust/permission prompt, launch hold, shell or vim-normal mode.
   Existing bridge-active, target and transcript guards still allow the action.
3. Native count and prefix fingerprint agree, with `0 < n <= payload.Length`
   and `EnterWritten == false`. No independent acceptance evidence exists.
4. A fresh read shows exactly one complete main input box anchored immediately
   above its valid footer, with blank rows below. Its entire content matches
   the owned prefix, with no person suffix, hidden region or ambiguous content.
5. Ownership has not expired or been invalidated by an edit, process change,
   restart, cancellation or loss of provenance.
6. The CLI can atomically compare the draft's ownership token and edit revision
   and clear only that revision. A stale revision must make no mutation.
   It also fences buffered keys from reappearing after the clear.

The last check is additional mutation authority, not a different definition of
owned text. Native counts, fingerprints and screen checks remain necessary.
Current screen checks alone cannot prove it: a person may edit between read
and key consumption, or restore identical text. A host semaphore does not
exclude the person's keyboard. Never claim these races are closed by two reads.

The terminal clear candidate from #2808 is Ctrl+E followed by Ctrl+U for the
single logical line, with exact box-row expectations and footer checks before
the destructive write: footer present, no unsafe mode or running-turn hint,
and blank rows below. It contains no Enter, Ctrl+Q, Escape or selection shortcut.
It is NOT authorised by this spec when atomic edit exclusion is unavailable.
Prefer a CLI compare-and-clear operation satisfying check 6; any future terminal
equivalent must prove the same exclusion and input-drain guarantees first.

After clearing, require an authoritative completion receipt for the same
session incarnation, attempt, ownership token and revision, confirming both
empty owned buffer and that this attempt was never accepted for submission.
Also re-read the complete anchored screen when using the terminal adapter.
A worker's `Typed` result, an empty-looking box or missing transcript alone
is insufficient. Empty can mean submitted; an old worker can repopulate it.

Persist that cleanup receipt before calling inbox `Requeue(node, rid)`.
Only then clear `StartedAt`; preserve the original FIFO position, payload,
sender rid and enqueue time. Increment the persisted attempt on next Start,
derive a fresh host request identity as in A, and retry no sooner than the
reply sweep while idle. Never append a replacement at the tail.
A crash after proof storage but before requeue may finish this transition
idempotently; a crash before proof storage remains Unconfirmed.

When any proof is absent, do not clear, submit, restore or stash the draft.
Retain an uncertain-attempt fence and report Unconfirmed with a reason code;
known untouched drafts instead keep the unstarted reply queued.
Read-only reconciliation may find a definitive receipt later. If an operator
intervenes, a plain "box is empty" is not a no-submission receipt.
Never ask the person to approve trust or permissions through this path.

## C: draft-independent submission

Propose an authenticated local data channel to the exact running interactive
Copilot session. It submits a prompt object to the CLI's conversation input,
not to its editable terminal buffer, and never opens a second session.
Use a local user-scoped IPC transport with OS access control and explicit
session-incarnation binding; no unauthenticated listener or remote endpoint.
It must leave the person's draft bytes, cursor, selection, undo and mode intact,
including edits made concurrently with delivery. Do not copy/clear/restore it.

The CLI must explicitly advertise and implement this versioned contract:

| Required support | Meaning |
| --- | --- |
| Live-session attachment | Bind the existing interactive session and incarnation, with authenticated same-user access and explicit support negotiation |
| Structured enqueue | Accept text and supported attachment references without touching the draft; treat text as data, never slash/shell commands |
| Guarded admission | Atomically enforce idle-only policy, no open question/trust/permission prompt and the expected session incarnation |
| Stable identity | Accept the sender rid plus persisted attempt identity and payload digest; reject same identity with different content |
| Durable idempotency | Persist acceptance before acknowledgement and prevent duplicate insertion across disconnect/restart; do not expire identity records while retry remains possible |
| Status lookup | Return accepted, rejected-with-no-effect, or unresolved for that exact identity; absence is not rejection unless authoritative and fenced against late arrival |
| Ordered receipt | Return a stable acceptance sequence and receipt proving insertion into the conversation queue; support lookup after receipt loss |
| Safe cancellation | Say whether cancellation prevented acceptance; timeout/cancel alone is never a no-effect guarantee |
| Optional owned-draft cleanup | Supply atomic ownership/revision compare-and-clear, input-drain fencing and the completion receipt required by B |

Names in this table describe required semantics, not existing methods or flags.
The host still enforces its existing caller authorization, holds, limits and
per-session admission before requesting a CLI action; IPC is not a guard bypass.
Permissions and trust remain person's decisions, not prompt submissions.
This slice retains idle-only FIFO dispatch; it does not add working-turn steering.

Mark delivered only when a correlated authoritative receipt proves acceptance
into the conversation, not when IPC bytes were written or a turn merely started.
Publish the sender's rid acknowledgement from that receipt. Durably retain
accepted identity before releasing the head. An acceptance receipt proves
delivery, not completion of the requested agent work.
After a disconnect, reconcile the SAME transport identity; do not create a new
attempt or fall back to terminal keys while acceptance is unresolved.

Availability: this build's CLI documentation (1.0.113) does not document the
complete live-session contract above. Neither non-interactive `-p`, resume,
an SDK-created session, a hidden server flag nor ACP existence proves it.
Existing [structured-channel research](host-answer-delivery.md#2-structured-channels-what-is-verified-and-what-is-unknown)
also distinguishes a client-created session from the person's live terminal.
Implementation needs a supported CLI contract and focused conformance proof;
this design neither invents an API nor declares the capability available.

Until that support exists, keep A's safe idle/no-effect retry path: a person
draft stays intact and the head waits. Do not advertise draft independence.
Never use terminal fallback after an uncertain structured send. Explicit
no-effect capability rejection before submission may select the existing
terminal path only with an empty verified draft and all its existing guards.
Missing atomic cleanup support means B cannot promise automatic clearing:
leave uncertain text untouched, report Unconfirmed, and keep #2780 open.

## Outcomes and FIFO rules

Failed below means a definitive no-delivery failure, not a replacement wire enum:
use the existing Refused/Busy result and reason where applicable. Temporary
no-effect holds publish queued, not a final Failed acknowledgement.
Unconfirmed means delivery or cleanup is uncertain, never "failed to send".
Delivered requires correlated acceptance. No inference comes from identical
transcript text, lack of a transcript record, or observation timeout.

| Failure or condition | Classification | Retry and FIFO disposition |
| --- | --- | --- |
| Busy, or safe pre-key race with proven zero effect | Queued; Failed only if definitively abandoned | Same head, next sweep when idle; zero effect is cleanup proof |
| Person-edited, pre-existing or unknown draft before any write | Queued, not Unconfirmed | No clear; same head waits for safe transport or verified empty draft |
| Known prefix, no Enter, all B proofs and cleanup receipt valid | Queued after cleanup | Persist proof, then clear StartedAt and retry same head with fresh attempt |
| Prefix count/hash unknown, unsupported native plan, edit, expired ownership or screen mismatch after a write | Unconfirmed | No keys and no retry; persist fence before settling head |
| Enter true/null, acceptance unknown, or transcript confirmation timed out | Unconfirmed | Lookup only; never clear-and-replay merely because text remains |
| Cleanup denied before mutation due to stale draft revision | Unconfirmed for original attempt | Preserve draft and StartedAt; no automatic retry without new complete proof |
| Cleanup interrupted, worker exit/drain unknown, or post-clear receipt missing | Unconfirmed | Preserve StartedAt and fence; no retry even if box appears empty |
| Restart with StartedAt but no persisted cleanup/acceptance proof | Unconfirmed | No terminal replay; reconcile identity, keep attempt fence |
| Structured timeout/disconnect or cancellation after possible send | Unconfirmed | Same-identity lookup only; no new attempt or terminal fallback |
| Structured authoritative rejection with no effect (busy) | Queued | Same head, idle/sweep retry; new attempt only after durable no-effect proof |
| Unsupported transport, before any submission | Queued if fallback can wait; otherwise Failed | No destructive fallback; retain head while waiting or explicitly settle definitive refusal |
| Invalid/oversize payload, revoked authority, ended target, permanent guard refusal with proven no effect | Failed | No automatic retry; persist final refusal, then release head |
| Durable storage/cleanup-proof write failure | Failed only if zero effect proven; otherwise Unconfirmed | Stop dispatch, surface storage error, do not clear StartedAt or advance in memory |
| Duplicate sender rid or host fingerprint | Existing pending/final result | No new delivery or FIFO entry; expiry alone never permits uncertain replay |

For unresolved terminal attempts, retain C1's final Unconfirmed receipt and
allow later FIFO entries to drain only after that outcome and fence are durable;
this does not requeue the uncertain entry. A remaining draft can still hold later
terminal entries. No later entry may act before the prior attempt is settled.
For structured acceptance ambiguity, hold later dispatch until status resolves
or the CLI provides an ordering fence excluding late insertion of the old send.
This stricter fence prevents a delayed old IPC request overtaking a later one.

## Placement, persistence and future validation

`ReplyService.cs` owns admission, transport choice and cleanup state.
`Copilot/CopilotInputBox.cs` remains the single read-only screen interpretation.
`Windows/WorkerInjector.cs` supplies native evidence and guarded writes, never
declares a rendered match authoritative. `Host/HaReplies.cs` carries correlation
and idle policy, not draft-matching logic. The HA connector owns durable
StartedAt/requeue transitions and rid receipts; do not expose raw drafts in HA.

The adapter runs on whichever node hosts the target session, never a named VM.
If that node leaves, nothing submits to its stale process identity. New live
nodes can serve their own sessions; moving a target needs explicit rebinding.
No new long-running service or central node is introduced by this spec.
C1's durable inbox is inherited local storage, not replicated durability.
Do not silently treat it as fleet-safe. Before cross-node handover, protected
relay state must hold inbox order, attempt fences and receipts with a single
writer lease; without that migration, hold affected work rather than replay.
Ephemeral screen evidence never survives handover as valid ownership proof.

Log only correlated ids, attempt/revision, reason, counts, timing and outcome.
Do not log draft/payload text, full fingerprints usable as public payload probes,
IPC credentials or screen captures. Persist proof references under existing
protected storage and retention rules; diagnostic expiry never erases a fence
that is still needed to prevent replay.

Future failing-first tests must cover: two simultaneous senders; busy-to-idle
FIFO; native odd partial counts and unknown Enter; SHA mismatch; matching
agent prefix plus person suffix; edit-away/edit-back; mode/dialog/process
changes; expiry; person edit between read and clear; delayed native consumption;
cleanup receipt loss; crash on either side of proof persistence/requeue; and
duplicate rid/fingerprint across restart. None may produce a blind extra key.
Transport conformance tests must prove a person draft survives byte-for-byte,
with cursor and undo unchanged, while two replies arrive in FIFO order.
Drop acceptance receipts and reconnect: exactly one insertion per identity,
no terminal fallback, and no later insertion overtakes an unresolved earlier one.
Use contract fakes first, then a disposable supported CLI environment; no live
owner session, trust prompt, permission confirmation or machine reconfiguration.
