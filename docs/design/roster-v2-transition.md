# Roster v2: person-authorized transition (#1825)

**Status: proposed design, not implementation or a rollout grant.**
Engineering requested this docs-only slice on 2026-11-11 (Pacific), after
[#1358](https://github.com/danswett/agent-bridge/issues/1358) closed.
Engineering must review this note and rule on the code cuts before code starts.
Quality alone owns gates, two different-model-family receipts on each exact head,
arming and merges.

## Scope and existing contracts

The outcome is a one-way authorization, per trusted roster, to verify v2 host adds.
It does not approve any particular host, change operation authorization, or make
`fleet.find` return `found`.

The governing contracts are [devices.md](../protocol/devices.md),
[device approval, sections 5.2, 6.3, 7.1, 7.2 and 8.1](device-approval.md),
and [agent principals, section 8.10 V1-V2](agent-principals.md).
The implementation sequence belongs to
[#1825](https://github.com/danswett/agent-bridge/issues/1825);
[#1776](https://github.com/danswett/agent-bridge/issues/1776) registry signing
and reader verification are separate.

Today `CommandAction` has no `accept-host-sign` action.
`RosterVerifier.AcceptHostSign` is init-only and defaults to false in production;
the PWA verifier also defaults to false. `HostRosterReader` reads an existing
four-field pin and retains a verifier in memory, but has no disk write API.
`DeviceRoster.Apply` reconstructs the relay verifier without v2 enabled.
`SecuritySettings` reads only `off`; live observe reporting is not implemented
at this head. Only the pin-reader foundation (#1830) merged; observe remains the
follow-up dependency described by devices.md, despite #1358's closed issue state.
These are foundations, not proof that the transition or enrollment works.

No code, vectors, settings, live enrollment, deployment, ChannelHub change,
enforcement, owner paging or alerting is included in this note. A later change
must not recreate #1358 observe work or combine #1776 registry work with this one.

## Who authorizes

Only an active, non-probationary, non-held person's device in the pinned roster
may authorize the transition, using its recorded presence mechanism. Resolve its
bound kind and `sign` key from the verified roster, never the relay's hello kind,
a display name, a bearer token, or an agent's credential.

A host's `hostSign` key proves only host-key possession. It cannot authorize the
transition, sign an entry as `by` or `cosigner`, sign a person command, or veto.
A second signature alone confers no command authority: the command has one `by`,
which must independently satisfy every person-command check. An otherwise
eligible person is not disqualified merely because it has cosigned an entry.
`presence: none` and backup-eligible WebAuthn credentials cannot authorize.

The phone presents one card: "Allow this host to understand roster v2",
showing the receiving host, account, generation and genesis fingerprint.
Approve authorizes only understanding v2; it approves no joining host or key.
Pending host fingerprints are not in the signed command and must not be presented
as approved by this touch. Approve requests the normal presence check.
Neither a script, an agent nor a relay response can press it for the person.
Fleet automation may prepare descriptors; it never creates consent, answers a
trust prompt, or asks the owner to run commands on a VM.

The transition grants permission to understand v2, not permission to append an
add. Each host add still requires its own person's signed approval and the
enrollment transcript binding the exact `dh`, `hostSign`, account and generation.
Host enrollment is a separate approval, never implied by this card; its later
UI cut must not weaken the mutual enrollment checks.

**First-host bootstrap is separate from transitioning an already pinned host.**
The later enrollment cut must first establish trust through person-authenticated
mutual enrollment or an explicit local person action: a person compares the genesis
fingerprint out of band with an already trusted source. A script or agent cannot
perform that human trust decision. Verify a v1 roster prefix rooted in that
person-authenticated genesis and durably pin it. Never derive this trust from
whatever genesis the relay serves or a host's own descriptor.
Only then may the host verify and persist the person's transition command,
and only after that commit may it evaluate its own v2 add. A prefix pin alone
never means "enrolled": the host stays pending until its complete verified roster
contains the matching, person's approved v2 add. Engineering must rule on this
bootstrap sequencing before the enrollment cut; no automatic pin creation is granted.

## Proposed command and capability precondition

Add `accept-host-sign` to the existing person-command action table, not a new
signature domain. Its body has exactly:

```text
v, action, request, by, target: { machine }, nonce, issuedAt, expiresAt, heads
```

`v` remains 1; `action` is `accept-host-sign`. Existing strict JSON, field,
UUID, device-id, hash-order, size, time and nonce rules apply unchanged.
`sig` covers the exact body bytes with
`F("agent-bridge person command v1", bodyBytes)`.
WebAuthn uses the existing `command` presence challenge over that same body.
No `to`, `choice`, session, question, cosigner or unsigned enable flag is allowed.

`target.machine` is one receiving host. The request travels on the authenticated
channel from `by`, with that host's 32-byte hello nonce. The signed heads must
belong to the already pinned account, generation and full genesis, and include
or descend from every durable accepted head. A command naming another roster's
heads cannot select that roster or create a pin. No trust on first use.

Before offering or accepting the action, every active rostered verifier must
advertise v2 support. The [#2372 evidence contract](../protocol/devices.md#signed-verifier-support-evidence-2372-design-2366-implementation)
defines the exact wrapper, signed fields, frontier/set encodings and design vectors.
It covers the pinned account, generation and genesis, exact enrollment and key
purpose, supported versions, complete active set, current frontier, receiving host,
fresh collection nonce/request and bounded lifetime. Check hosts and person
clients, including held/probationary active devices, not just online or serving
machines or those selected for enrollment. A relay list, remembered version or
authenticated channel alone is not evidence.

**Signing-mechanism decision (#2372, design only):** hosts sign their own evidence
with the `hostSign` enrolled in their exact active v2 add, never as a substitute
for person consent. Person clients need a distinct, roster-enrolled capability
key (`capSign`), not their generic `sign`, DH or WebAuthn key.
The signature domain is `F("agent-bridge verifier support v1", bodyBytes)`;
the protocol section registers the separate frontier, set and key-binding labels.
Legacy DH-only hosts and clients lacking the purpose-enrolled key are unsupported.
No DH signing, DH-derived MAC, AP5-lite key, self-supplied key or unsigned flag.

The host-key prerequisites are #1776 slices #2369 (custody/public descriptor),
#2370 (registry signing) and #2371 (reader verification), with #2289 enrollment
and the person-capability-key schema/custody/enrollment slice still required.
This note grants none of those implementations and invents no bootstrap: a v2
add unavailable to the pinned reader cannot supply a trusted capability key.
Any circular first-host enrollment/transition dependency needs the separate
bootstrap ruling above, never temporary trust in an unenrolled descriptor.
#2366 must implement evidence verification and reviewed snapshot/replay extension
after this design and its key prerequisites; it still cannot enable v2.
Until every required enrollment and evidence check is available, return
`unsupported-verifier`. No positive transition (#2288 slice 2b-2) is authorized.

Missing, offline, outdated, unverifiable or frontier-mismatched evidence is
`unsupported-verifier`; do not infer support or automatically remove a device.
A normal person-approved roster revocation of a lost or offline verifier is
allowed. Once its removal is verified and applied, recompute the required active
set; a dead device need not block the transition forever. Do not bypass the normal
revocation signatures, seniority, presence or application rules by deleting a row.
Recheck if the roster frontier or active set changes before commit. Serialize
verification and commit against concurrent roster updates and requests.
No such signed capability advertisement is claimed to exist today.
Even valid evidence proves only an enrolled key self-attested support, not that
its software is honest. A lying verifier can claim support it cannot implement;
a withholding verifier can deny service until ordinary revocation removes it.
These are availability residuals, not permission to bypass the evidence gate.

One command authorizes one host's durable copy of this roster transition, not
every host at once. Multiple targets need different signed bodies, nonces and
requests. Engineering requires one WebAuthn assertion per host over that host's
own body and challenge. Reject any multi-body batch challenge; an assertion for
one body cannot authorize another. One card may contain sequential touches, each
naming its receiving host with explicit per-step results, never one confirmation
silently reused for multiple bodies.

## What the receiving host verifies and commits

This is a new privileged mutation even when ordinary roster enforcement is off.
It has no pre-roster success path to preserve. In order:

1. Require a readable local pin and a verified retained reader rooted in it.
   Sync missing ancestry without replacing the pin. Reject identity mismatches,
   missing ancestry or regressed frontiers; relay mode never supplies authority.
2. Strictly parse the command. Run the existing person-command checks in their
   documented order: action, machine, signer, probation/held, signature, presence,
   host-key separation, lifetime, future, expiry, floor, nonce and held heads.
   Use the later of wall clock and durable high-water mark, with the durable floor.
   Also require forward-or-equal coverage of the durable frontier as above.
3. Require freshness under device-approval section 6.3, including its restart and
   clock-discontinuity rules. This action is not the `attest` exception.
   Observe mode must not turn a failed freshness check into a mutation.
4. Require the authenticated support evidence for the entire current active set.
   Reject a reused request using the durable replay store, not `CommandMemory`
   or the AP5-lite in-memory replay cache.
5. Commit the authorization, exact pinned identity, accepted frontier, used request,
   high-water mark and highest accepted `issuedAt` durably before enabling v2.
   If any check or write fails, leave the prior state unchanged and report failure.
6. Only after durable commit may this roster's verifier evaluate a v2 add.
   Preserve the retained graph, probation running times, held/quarantine state
   and accepted heads; rebuilding must not restart or shorten probation.
   Never implement this as a process-wide or caller-supplied boolean.

**Persistence decision:** keep the existing four-field `roster-pin.json` and its
4,096-byte limit unchanged. Use a separate versioned, owner-only transition
snapshot beside it, with its own 256 KiB bound and at most 512 live replay records.
Create that separate store only after a successfully read pin and verified command;
it never creates, expands, overwrites or re-pins the original pin.

The transition snapshot binds the exact pinned account, generation and genesis,
one to four sorted accepted heads, the transition bit and replay/clock state.
Require both the pin and this snapshot's identity to match and require forward
coverage of both stores' heads before use; no snapshot means "not authorized".
Commit the snapshot in one atomic replacement (private temp file, flush, rename),
so "request consumed" and "switch enabled" are never in different writes.
This action does not write the pin: it claims no atomic transaction across two
files. Reject a full replay store rather than evict an unexpired request.
No private key, bearer token, descriptor key material or raw exception belongs in
either store. Any pin-format or pin-limit change needs a separate reviewed cut.

Prune a request only after `expiresAt` plus two minutes has passed by the persisted
high-water mark. Preserve the highest accepted `issuedAt` and reset floor when
pruning. A local clock reset keeps the existing person-only, agent-refused rule;
it never clears the transition or permits an old request to run again.
The writer must keep the original snapshot if replacement fails and must not
publish success before the replacement is durable. Crash/restart after commit
recovers the committed authorization; a lost acknowledgement is not consent
to execute the same request again. A new request may report "already authorized"
without disabling, re-pinning or re-executing the transition.

Exact snapshot schema and platform atomic-write guarantees are part of the
reviewed persistence cut, not permission to weaken the existing pin reader.
A transition-capable reader that cannot parse this separate snapshot must fail
closed for this mutation, never use pin-only state as authorization, overwrite
the snapshot with an older one or silently turn v2 back off.
Owner-only storage and atomic writes do not prevent a local attacker or backup
restore from replacing the whole snapshot, including its replay records,
high-water mark and floor. Without an independently protected anti-rollback
anchor, whole-snapshot restoration is a residual risk; this note does not solve
it or authorize restoring old state as a recovery step.

## Failures, observe mode and rollback

The existing codes keep their meanings; new host-only codes below are proposed
for vectors and Engineering review, not changes to today's wire contract.

| Condition | Result; durable effect |
|---|---|
| Missing pin or roster | `unpinned` or `no-roster`; no transition, no pin creation |
| Damaged, inaccessible, public, oversize or unknown-schema snapshot | `pin-unreadable`; fixed content-free message, no repair |
| Wrong account, generation or genesis | `pin-mismatch`; keep the old pin and authorization |
| Missing ancestry or older heads | `heads` or `head-regress`; no replacement |
| Host or agent presented as command signer | `signer`; no mutation |
| Person on probation or held | `probation`; no mutation |
| Signature, presence or key-purpose failure | Existing `signature`, `presence` or `host-key`; no mutation |
| Expired, future, below-floor or wrong-nonce command | Existing specific code; no mutation |
| Stale host or missing support evidence | `stale` or `unsupported-verifier`; no mutation |
| Reused request or full replay store | Proposed `replay` or `replay-full`; no mutation |
| Failed flush/replace | Proposed `transition-store`; old snapshot remains authoritative |

A host or client that has not transitioned must fail closed on a v2 add: hold or
quarantine it without applying it, preserve the prior verified graph and report
the blocked transition. Never fork a replacement roster, change genesis or
silently drop the entry. Bounded holding/retry and recovery behavior need the
separate reviewed consumer cut below; no current implementation is claimed.

`off` is today's only implemented mode and remains the default. Live `observe`
mode is a dependency not yet built, not behavior delivered by this note or #1830.
Its intended contract is to allow ordinary operations and report would-refuse
reasons with bounded recent reports and an overflow counter; that integration
remains separate work and must not be claimed as passing evidence here.
Neither current `off` nor future `observe` enables v2, bypasses transition checks,
changes Noise intros, or turns on the #1359 enforcement gate.
Failed transition attempts do not block unrelated existing session operations.
Future status reports use fixed reason codes, device ids, hashes, generation and
bounded counts, never keys, tokens or payloads.

The accepted switch is one-way for its pinned roster identity. A restart, config
change, relay failure, roster omission or binary downgrade cannot revoke it or
copy it to another generation. A different roster needs explicit person-approved
pinning and a new transition. Do not recover by deleting state, silently resetting
the generation, restoring an older snapshot, or toggling `AcceptHostSign` off.
Failure recovery before commit is a fresh person command after the cause is fixed.
After commit, keep state and fix forward; a roster reset is a separate explicit
person action, not an automatic rollback or an agent repair.

## Missing consumers and proposed test-first code cuts

**Out of #1825 slice 2:** relay-verifier and PWA-reader transition recovery is a
separate reviewed cut, not permission to change ChannelHub or how this slice reads
the roster from the relay. The capability mechanism above is specified by #2372,
not implemented by it.
Its purpose-key enrollment and #2366 evidence code remain separate blocked
dependencies. Slice 2 cannot silently absorb those prerequisites.

The relay currently reconstructs a v1-only verifier. Host acceptance alone will
not make `roster-append` store a v2 add; a successful host command must not be
reported as successful enrollment. PWA readers also need the same authorized
transition state before replaying a v2 history. Engineering defers distribution
and restart recovery to that separate reviewed cut: distribute only the signed
person command plus a verified frontier, never a boolean. Each consumer still
verifies authorization for its own pinned context; a command naming another host
cannot directly authorize its transition. The cross-consumer recovery mechanism
remains for that review. Non-transitioned consumers hold fail-closed.
No live enrollment until these dependencies are accepted. Never fix this by
setting `AcceptHostSign = true` globally or trusting a relay boolean.

Proposed order, not code grants; each PR remains at most 400 changed lines,
sensitive, two families on the exact head, with full red/green output retained:

| Cut | Predecessor | Failing-first proof before implementation |
|---|---|---|
| Command contract, C#/PWA parsers and shared vectors | This note reviewed and Engineering ruling | Valid person accepted; host/agent signer refused; cosig-only wrapper refused; probation and no presence refused; malformed/extra fields refused identically |
| Signed support evidence (#2366, blocked dependency) | #2372 design reviewed; #2369-2371 host-key work, #2289 enrollment and person capability-purpose-key enrollment | devices.md's positive/negative vectors, including generic person key and legacy host refusal; missing/offline verifier, forged signature, wrong add/generation/frontier/nonce and changed active set refuse; applied person-approved revocation removes a lost verifier from the required set |
| Durable host transition and verifier handoff | Both above, retained-reader integration and separate snapshot schema | Existing 4,096-byte pin unchanged; replay after restart; torn/failed snapshot write; wrong generation/genesis; clock rollback/floor; stale/regressed heads; v2 refused before and accepted only after commit; probation preserved |
| Relay and PWA-reader transition recovery (out of slice 2; separate review) | Engineering decision on signed-command/frontier distribution | v2 append/rebuild and client replay require verified authorization; non-transitioned consumers hold without forking or silently dropping; omission or forged flag never enables it |
| Enrollment descriptor and person's approved add | Transition consumers above and approved bootstrap sequence | Self-approved add refused; pin-only bootstrap stays pending; binding/transcript mismatch refused; pending until own verified pinned v2 add matches |

Split any oversized cut further before implementation. This design PR changes no
behavior, so it has no runtime failing-first claim; those tests belong to the
subsequently authorized code PRs. Run `quality/run.ps1` and the selected docs checks
here, retaining complete output and disclosing any unavailable checks.
Do not enroll live hosts, change `security.roster`, deploy, or start #1776 as a
consequence of merging this note. `fleet.find` stays the canonical name lookup;
no machine/session node ids are embedded in configuration or this design.
