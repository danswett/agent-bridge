# Feedback and diagnostics privacy design, draft v1

**Status: draft for review, not an implemented or approved protocol.** This is the design
note for [#732](https://github.com/danswett/agent-bridge/issues/732). It changes no code
and enables no collection. Nothing in F3, F4 or F7 is built until this note has had its
independent security review and two-family technical review.

It builds on what already exists and defers to it where it is more exact:
[feedback bundle](feedback-bundle.md), [sealed envelope](protocol/feedback-envelope.md),
[feedback upload](protocol/feedback.md), [inbox](feedback-inbox.md),
[custody](feedback-custody.md) and the merged beta diagnostics tier in
[design §5.10](design.md#510-observability-required-decided-2026-10-27)
(#1139, #1165: opt-in worldwide, off by default, pseudonymous install ID, no
collection, queueing or upload while off). It must agree with the draft request-ID
correlation contract, [#1189](https://github.com/danswett/agent-bridge/pull/1189)
(`docs/feedback-correlation.md`, tracking #1186), and does not settle its open
decisions. Where this note needs one of them, it says so under
[Dependencies on #1189](#dependencies-on-1189-open-decisions).

Platforms in scope: Windows x64, Windows Arm64 and the phone PWA. macOS is parked; nothing
here is designed or built for it.

## Fixed rules

1. **Opt-in, off by default.** Optional diagnostics are opt-in worldwide with no planned
   default-on revisit. Manual feedback is always a separate, per-report choice.
2. **No collection while off.** With diagnostics off, nothing is collected, queued or
   uploaded as a diagnostic event. Refusing costs no functionality.
3. **The relay stays blind** (design §5.3, §5.10). It sees ciphertext sizes and outcomes
   only, never plaintext, a read URL or a key.
4. **Allowlist redaction.** What may be sent is what a reviewed schema names. Removing
   patterns from free text is a backstop, never the control.
5. **Nothing leaves the machine unless the tester attaches it after seeing it.** This
   applies to manual feedback and any support disclosure. Optional diagnostics are governed
   by the opt-in and the content-free allowlist instead (no per-event preview). Neither
   describes normal encrypted delivery of sessions to the tester's own phone, which is the
   product working. Diagnostics are the only channel without a per-item preview.
6. **No feedback content or tester identity in public issues.** This repository is public.
7. **No claim of end-to-end tracing.** A link between records exists only where both
   records exist and were approved for collection. A missing link is shown as missing.

## What each channel is

| Channel | Initiated by | Goes to | Consent |
| --- | --- | --- | --- |
| Manual feedback | The tester, one report at a time | Sealed to the feedback public key, uploaded through the write-only relay route to the private feedback container, then to the private feedback repository | The tester's act of pressing Send after the preview. Nothing is remembered between reports |
| Optional diagnostics (#741) | The app, only while the tester has allowed it | A separate Application Insights health-event pipeline, never the feedback store | A first-run choice and a Settings switch; withdrawable |
| Local per-attempt record (#1135) | The app | Stays on the machine, never uploaded | None needed; holds no install ID and no diagnostic ID |
| Relay aggregate health (§5.10) | Automatic, unchanged | Aggregated and anonymous; own off switch | Unchanged by this note |

The two upload channels never share a pipeline, a key, a store or a retention rule.
Allowing diagnostics does not send feedback, and sending feedback does not turn
diagnostics on.

## Feedback bundle contents

The bundle is the existing `slipgates-feedback.v1` ([feedback-bundle.md](feedback-bundle.md)):
versions, agents detected, allowlisted config, journal, crashes, the tester's note, an
optional screenshot and optional session turns. The caps there stand (2 MiB total, 1 MiB
screenshot, 20 turns, gather bounded at 10 seconds). The PWA produces the same schema with
its own bounded event and log lists. This note adds three rules:

* **Session turns, screenshot and note are tester-chosen.** Session turns are off by
  default and name one session the tester picks. The screenshot is optional and visible.
  Each item can be removed in the preview before sending.
* **Correlation fields are optional, previewed and claims, not proof.** A report may carry
  an operation attempt ID or a diagnostic attempt ID only if the tester can see it in the
  preview and a matching record was actually made. The upload reference and report ID
  rules are in #1189 and are not repeated here. No bearer ticket or upload URL is ever
  a bundle field.
* **Unavailable parts say so.** A source that cannot be read becomes `"unavailable"`; the
  preview says which parts are missing and must not imply that an automatic diagnostic was
  sent when none was. The report still sends.

### Conflict with the allowlist rule in today's bundle

Journal lines and crash records are currently included as redacted raw text, and the note
and optional turns are free text. That is pattern removal, not an allowlist. The design
requirement is:

* **Structured fields are allowlisted.** Versions, agent names, config scalars, outcome and
  reason codes, durations and counts come from a reviewed vocabulary. Anything not named is
  dropped, and a count of dropped items may be reported.
* **Journal and crash entries move to structured records** (timestamp bucket, component,
  event code, approved outcome and reason, version) rather than raw lines. Until then they
  are shown in full in the preview and treated as content the tester is attaching.
* **The only free text is what the tester writes or chooses**: the note and, if switched
  on, the named session's turns. Pattern redaction (`FeedbackRedactor`) stays as a last
  check and cannot be the reason a field is considered safe. Broader redaction is #1187.

## Diagnostics event contents

Exactly the merged §5.10 amendment; it is restated here so the review has one place to
check it, and §5.10 wins on any difference.

* **Allowed:** event type, a finite approved outcome and reason vocabulary (no exception
  text or stack frames), app and host version, channel, bounded timing and count buckets,
  and an optional screen outline of locally generated structural enums and buckets.
* **Never:** prompts, answers, commands, code, titles, paths, names, screen words or
  pixels, arbitrary strings, raw exceptions, content-derived hashes, account or device IDs.
* **Identifiers:** a random install ID created only after opt-in, rotated at least every
  30 days and on reset or re-consent, with no old-to-new mapping; a separate random ID per
  attempt, never reused and never containing the install ID. Both are pseudonymous
  personal data, not anonymous.
* **Caps:** daily event caps and a bounded local queue, with no queue at all while off.
* **Added by the SDK:** nothing. Client IP and geolocation must not be stored (disabled or
  masked at ingestion), and SRE proves that, and that no other SDK-added field is kept,
  before upload is enabled. Reviewers verify the actual payload, not this description.

### Is a §5.10 amendment needed?

No change to the merged text is required by this note. Three clarifications are recorded
here instead and do not alter the tier: the 30-day telemetry maximum is separate from the
manual-feedback lifecycle below; ordinary encrypted delivery to the tester's phone is not
"diagnostics"; and the install ID is pseudonymous personal data. If a reviewer finds the
merged text contradicts one of these, the fix is a one-paragraph amendment in its own PR.

## Redaction pipeline

Order, for manual feedback, on the tester's machine:

1. Gather from allowlisted sources into a structured bundle.
2. Apply `FeedbackRedactor`'s removal to every text field.
3. Show the preview of the exact canonical bytes (see the UX contract).
4. On Send, re-scan the final bytes with the same patterns, decoding escapes (up to four
   passes). A hit refuses locally with no network request and tells the tester which
   field to change.
5. Seal, then upload.

The check at step 4 uses the same pattern source as step 2, so the two cannot disagree.
Redaction failures fail closed: a scan error is a refusal, never a send.

## Sealing

The existing v1 envelope stands: X25519 ephemeral key, HKDF-SHA-256, AES-256-GCM, key ID
and random 16-byte bundle ID authenticated, padded to the size bucket. The client holds
only the public key and key ID, compiled in; the private key is held only by the feedback
team's decryption tool (the inbox worker, per [custody](feedback-custody.md)). Rules:

* There is no plaintext, email or relay-readable fallback. With no recipient key, sending is
  unavailable and says so.
* The plaintext sealed is byte-identical to the bytes the tester previewed. Any change to
  the report after the preview, including adding a reference, requires a new preview.
* The relay receives padded ciphertext and a size; it cannot decrypt, list or read back.
* Production key, key ID and the key handoff packet remain the custody document's pending
  items. Nothing here reads, creates or moves key material.

The exact order of preview, sealing, upload-slot request, expiry and retry is
[a #1189 open decision](#dependencies-on-1189-open-decisions); the constraints this note
places on it are listed there.

## Storage

| Store | Contents | Who can read |
| --- | --- | --- |
| Relay feedback container (blob) | Padded ciphertext only, random blob name | The relay can create only. The inbox worker's read-only identity reads. Testers and the relay cannot |
| Private feedback repository (`danswett/slipgates-feedback`) | Decrypted report as bounded private issue chunks | The named feedback reader roles only; privacy of the repository is confirmed before each write |
| Receipts and suppression metadata | Bundle name, a processed or deleted state and a time, no content | The worker and named support roles |
| Private operational lookup (#1189, SRE) | Approved random IDs only; no content, account or device identity | Named support roles, audited |
| Telemetry pipeline | Allowlisted diagnostic events | Least-privilege operators; separate from feedback |

Public issues receive only the existing deterministic, allowlisted rendering (error type,
stage, versions, OS, architecture, `unknown` tester count, fingerprint) and nothing from a
report: no text, logs, IDs, upload references or joins. The tester count stays `unknown`
because a count inside client-encrypted content is forgeable.

## Retention

Owner decision: 30 days. Each store needs its own verified mechanism; a policy on one store
does not delete the others. "Verified" below means SRE has shown it on the real resource.

| Data | Retention | Mechanism | Status |
| --- | --- | --- | --- |
| Sealed upload in the feedback container | Cannot be deleted for 30 days (immutability policy), deleted at day 31 | Time-based retention plus lifecycle rule | Configured per upload doc; lock state and deletion behaviour to be verified by SRE |
| Decrypted private issue chunks | At most 30 days from the original upload time, sooner on a deletion request | Worker deletion sweep keyed to the upload time | Not implemented; the processor currently uses processing time and its source interface lacks upload time |
| Receipts, suppression metadata, failure records | Only what idempotent retry and no-replay-after-delete need, at most 90 days, never unbounded (owner decision, Nov 10) | Worker | Owner decided 90 days; after any backup restore, deletions are re-applied |
| Private operational lookup | At most 30 days from the original upload or collection, not extended by joins or retries | SRE mechanism (#1189 item 4) | Owner decided 30 days; SRE confirms how |
| Diagnostic events | At most 30 days from original collection, not restarted by retry, export or join | Per-table analytics retention, bounded local queue | Proposed maximum; SRE proof required before upload |
| Relay logs | 7 days, outcomes and sizes only, no account, device or ticket | Existing | Unchanged |
| Local preview, note and screenshot | Memory only; cleared when the window closes | Existing | No offline queue, nothing written to disk |
| Local per-attempt record (#1135 / #1189) | Open (Connection); within the 30-day maximum | Open | See the dependency list |
| Consent record | Until the tester changes it; kept separate from diagnostic payloads | Local setting | Proposed |

Two points must be said plainly in the tester notice: the 30-day immutability hold means
the encrypted upload cannot be removed before day 30 even on request (the readable copy
can be), and nominal 30-day settings in some Azure services can retain 31 days unless set
exactly. No deletion promise is published until the real configuration supports it.

## Deletion

* **Withdrawing diagnostics consent** stops collection and upload, clears the install ID
  and deletes unsent events and the local queue. It does not erase events already received,
  which expire at 30 days. Re-enabling creates a new install ID and sends nothing from the
  off period. An upload already in flight is cancelled where the client can still
  abort it; one that has already reached the telemetry store is treated as received and
  expires at 30 days.
* **A feedback deletion request** goes through a private route that asks for a report
  reference and nothing else about the tester. On a valid request the worker removes the
  decrypted issue chunks and any partial writes, removes the lookup entry, and keeps only
  minimal suppression metadata, kept at most 90 days (owner decision, Nov 10), so a retry does not recreate the
  report. After any backup restore, the deletions are re-applied (owner decision, Nov 10). Closing an issue or destroying a key does not count as deletion.
* **Limits stated honestly:** the immutable ciphertext remains until day 31; provider
  residuals and backups are described, not denied; whether a legal erasure request can
  override the immutability hold is a recorded residual legal question, not decided here.
* **This request path is not usable until the Sent-screen reference is decided.** A tester who
  closed the window has no reference. The published tester notice must not promise
  self-service deletion until then. See the conflict list
  for the proposal that the Sent screen offers a copyable report reference.

## Per-account quota

Quota is a relay-side abuse limit, not a tracking record. Existing defaults, from
[feedback upload](protocol/feedback.md): 8 MiB per bundle; 3 requests per minute, 10 per
day and 40 MiB per day per account. Refusals map to `RateLimited` (retry after 1 minute)
and `Quota` (retry after 1 hour) and carry no report content. The ledger is in memory, holds
counters only, is forgotten on restart and is never logged with an account, device or
ticket. The inbox separately caps public issue creation at ten per hour and pauses rather
than dropping when over cap. Diagnostics have their own daily event caps and do not draw
on the feedback quota. Quota must not be used as a lookup key (#1189).

## Consent and preview UX contract

This is the contract the implementers build to and the reviewers test against. Copy is
Compliance's from the #741 final advisory and is a requirement on meaning, not final
design. It applies on Windows and the phone PWA; each client has its own choice.

**First run (diagnostics).**
* One screen, headed "Help improve Slipgates with optional diagnostics", with the body
  Compliance gave on #741, the verified retention sentence, operator and contact, and an
  accessible link to the full privacy notice.
* "Not now" and "Allow diagnostics" are equally prominent, neither preselected, and the
  default focus is not on "Allow diagnostics". Closing the screen is "Not now".
* Nothing is collected, queued or uploaded before an explicit "Allow diagnostics".
  Choosing "Not now" never degrades functionality and is not nagged again; the choice
  lives in Settings.

**Settings.**
* A switch labelled "Send optional diagnostics", off initially, with Compliance's helper
  text. Turning it off withdraws consent, stops collection and uploads and deletes unsent
  events, with no extra confirmation step that makes it harder than turning it on.
* Links to what is sent and to the private deletion-request route.
* Until the feature is implemented and reviewed, the switch is absent or disabled and the
  beta notice says diagnostics are not enabled and cannot be turned on yet.

**Preview before sending feedback.**
* Opening "Send feedback" gathers and shows the exact content; **nothing is sent until the
  tester presses Send**, and Send is disabled until the preview has rendered.
* The preview is the exact canonical bytes that will be sealed, shown as one row per item
  with an expander, each removable; session turns are off by default and named.
* It states which correlation or diagnostic references are included, and which are
  unavailable, in plain words, and never implies a diagnostic exists when none was sent.
* Any change to the note, screenshot, session option or attached references regathers and
  previews again; a late gather result is dropped. The Send click binds to the exact
  previewed bytes.
* A failed upload keeps the reviewed preview in memory for a manual Try again. A retry may
  not alter content without a renewed preview. Closing the window clears everything.
* The same preview applies whether or not diagnostics are on.
* Controls are keyboard and screen-reader reachable and the preview text is real text, not
  an image.

**After sending.** The tester sees plain success or the failure reason from the existing
outcome table, with no report content in any failure message.

## Tester privacy notice (draft content)

For the privacy notice at slipgates.com/privacy (address decided 2026-11-04; hosting is
#762) and the in-app links. The notice must describe only what is actually enabled;
[#744](https://github.com/danswett/agent-bridge/issues/744) owns the lawful-basis and
factual-notice review, and this draft does not certify it.

* **Sessions to your phone** are delivered end-to-end encrypted. The relay carries
  ciphertext it cannot read. This is how the product works and is not diagnostics.
* **Feedback you send** is a separate choice you make each time. You see exactly what will
  be sent first. It is encrypted on your device to a key only the Slipgates feedback team's
  tool holds, uploaded through our relay (which cannot read it), and stored in a private
  repository the team reads. We do not put feedback or who sent it in public issues.
* **How long we keep it:** the encrypted copy is held at least 30 days and deleted at
  day 31; the readable copy is kept at most 30 days from upload. Deletion on request is not
  promised until the report-reference decision is made (see Deletion); the encrypted copy
  cannot be removed before day 30.
* **Optional diagnostics** (when available): off unless you allow them, in your first-run
  choice or Settings. Current beta wording: "Optional automatic diagnostics are not enabled
  in this beta and cannot be turned on yet. Their design excludes session content; they
  must pass technical and privacy reviews before any uploads are enabled." Future wording is
  Compliance's #741 text, with the verified retention sentence added. These records are
  pseudonymous, not anonymous.
* **What we never collect automatically:** prompts, answers, commands, code, session
  titles, paths, screen text or screenshots.
* **Relay logs** hold outcomes and sizes, no accounts or devices, for 7 days.
* The notice makes no claim that a request can be traced across all components.

## Dependencies on #1189 open decisions

This note does not decide these. It records the constraint each places on the answer, so
the two documents do not diverge.

1. **Upload reference issuance and the order of preview, sealing, expiry and retry**
   (Relay and Send Feedback). Constraints from here: the preview precedes sealing; the
   plaintext sealed equals the previewed bytes; the tester approves any reference before it
   is sealed in; if a slot is requested before sealing, the exact size must already be
   determinable from the approved bytes; an expired ticket needs a new slot without
   changing approved content; a retry never discloses plaintext to the relay; the bearer
   ticket or URL never enters the bundle, preview, logs or issues. The existing flow
   (seal, then request a slot) is the baseline until revised.
2. **Ambiguous PUT success** must be handled idempotently. Requirement on the outcome:
   a retry after a lost response leaves one private record, not two, and a deletion request
   removes every copy. This requirement fails today: a retry gets a new slot and blob name,
   so one report can leave two sealed blobs and two readable copies, and only a key that
   joins them, which #1189 must supply, fixes it. How uploads are keyed or deduplicated is #1189's decision; today's
   inbox keys on blob name, and a retry gets a new slot and name.
3. **Connection's request-ID sources and retention.** Requirement on the outcome: nothing
   from them is added to a report unless it was actually recorded and is shown in the
   preview, and any retention is within the 30-day maximum above. Their storage, lifetime
   and ID form stay Connection's decision.
4. **SRE's private lookup.** Constraint: random IDs only, audited, named roles, with the
   retention and deletion in the tables above. An absent record is not proof of failure.

## Conflicts to resolve (also listed on #732)

1. Allowlist rule versus the current journal, crash and free-text handling (above).
2. "30 days" versus the immutable hold and day-31 deletion: the promise to testers must
   state both, and "30 days maximum" applies to readable copies, lookups and diagnostics,
   not the ciphertext.
3. The processor's retention clock uses processing time, not upload time.
4. A deletion request needs a report reference, while #1189 keeps report IDs private and
   the client keeps nothing on disk. Proposal: show a copyable report reference on the Sent
   screen, only if the tester wants it, never stored.
5. Whether the Windows host follows the Windows app's diagnostics switch is not stated in
   §5.10; the proposal is that one machine has one choice, owned by the app.

## Verification before anything is built

Reviewers and the implementers' tests use synthetic data and prove: with diagnostics off,
no event is collected, queued or uploaded; withdrawal clears the ID and unsent events and
re-enabling sends nothing from the off period; the exact preview equals the sealed bytes;
the final scan refuses planted secrets in every field including encoded forms; the relay
holds no plaintext and cannot read back; public issues receive no content or identity; a
retry after a lost response yields one record; deletion removes the readable copy and the
lookup entry without replay; retention and deletion work on the actual stores; and nothing
in the product or notice claims end-to-end tracing.

## Review and gates

This is a sensitive documentation change. It needs two independent receipts from different
model families and an independent security review of the exact head, plus Engineering's
review, before it leaves draft. It stays a draft until the upload-reference issuance and
the preview and sealing order (dependency 1) are settled by the Quality, Relay and Send
Feedback leads. Quality owns
admission of any actual build; this document approves nothing for collection.
