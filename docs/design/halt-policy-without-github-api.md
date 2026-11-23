# Halt protection without a client GitHub API dependency

Status: proposed, design only; Brief 257 (Engineering brief 256), Nov 21, 2026 PT.
Sensitive: update trust, publication and discovery; two independent model families,
including Claude, through Quality. This document changes no signing configuration, workflow or
relay deployment, and no runtime behavior except the interim relay renewal (below, owner-approved
Nov 22).

Tracking: [rate-limit refusal](https://github.com/danswett/agent-bridge/issues/3553)
and [release-list relay](https://github.com/danswett/agent-bridge/issues/927).
This proposes replacing the source/freshness parts of
[fast fleet updates, section 4](updates-fast-fleet-update.md#4-halt-policy);
that document and shipped behavior remain current until implementation is admitted.

## What the source does today

`ReleaseDiscovery` reads the release list through `releases.slipgates.com`, but
still asks `api.github.com` after six hours (`GithubRevalidateEvery`), on bootstrap,
on a person's discovery check (`pressed`), and on fallback. Rate-limit-only relay
grace is bounded to 24 hours; it is not an independent freshness authority.

`HaltPolicy.cs` separately asks
`https://api.github.com/repos/danswett/agent-bridge/releases?per_page=10`: the first ten releases, because `halts.json` is on the newest and a hundred releases of a dozen assets each ran past the 4 MiB cap, so the read failed on every machine (seen on one machine running v0.1.7522, Nov 23).
`GitHubHaltListSource`, in `ReleaseEligibility.cs`, selects the highest numeric
canonical `v0.1.N` with `draft: false`, including pre-releases. It requires exactly
one asset named `halts.json` in that release, its exact repository/tag
`browser_download_url`, `size` in 1..65536, and a 32-byte `sha256:` metadata digest.
It downloads that browser URL, checks size/hash, and parses the strict body:

```json
{"v":1,"halted":["0.1.7214"]}
```

**No release-list field directly marks a release halted.** `draft` and `tag_name`
select the policy carrier; `assets.name`, `browser_download_url`, `size` and
`digest` authenticate its bytes through GitHub HTTPS metadata. Only canonical
versions in `halted` block installation; `prerelease`, release title and notes
are not halt signals. Missing/malformed bytes are not an empty halt list.
The current 1024-entry limit and canonical numeric version rules apply.

`ops/halts.json` is the reviewed authoring source. Normal publication uploads it
before exposing a release; `halts-upload.yml` can replace the newest release's
asset after a reviewed halt edit without building a new app. An older release's
embedded policy alone therefore cannot report a new emergency halt.

The shared cache/lock reserves an hourly policy attempt durably. Direct 200 or
usable 304 renews its observation; freshness is strictly less than six hours.
Known halts survive failure and older policy releases. Shared discovery backoff
can suppress this policy request too. Both calls are anonymous API requests;
conditional requests do not remove their shared per-IP rate-limit dependency.

`UpdateService.RefreshPolicyAsync` retains known halts on failure, with
`Fresh: false`. `Fresh` rechecks the six-hour deadline at action time;
`NotChecked` distinguishes stale from unreadable. `PolicyCode` always refuses
known halts, but permits a single person's signed `unchecked: true` press when
policy is unavailable; batches never get that exception. Acceptance and departure
recheck protection. `AutoUpdater.PolicyUnchecked` holds automatic between-turn
installs when its supported snapshot is not fresh; a null optional snapshot
currently means no hold. `UpdaterHaltPolicy` starts unknown, not empty.

The existing Worker caches **unsigned GitHub release-list JSON**, not packages or
policy signatures. Its cache is per edge, with 300-second anonymous freshness
(60 with its configured token) and up to 86400-second storage. It can serve stale
bytes after refresh failure; `Age` is transport-controlled. A digest copied from
that same unsigned response is not an independent trust root.

## Interim: renew from the relay's list while GitHub's policy read fails (owner-approved Nov 22 for refusals; widened Nov 23)

The signed anchor below is slices 2 and 3 and is not built. Until it is, the policy read has a
second source so a rate-limited machine does not age out of protection (a machine logged "GitHub policy
unavailable; stored policy was not renewed" all day, and its release-list relay grace ends 24 hours
after GitHub last answered).

**Rule** (`ReleaseDiscovery.ReadHaltPolicyAsync`, `CdnPolicyList`): only when GitHub's policy read
fails (a 403 or 429 refusal, any other status such as a 5xx or 404, no answer at all, a body past 4 MiB, or a 200 that is not a release list), and the release list's relay grace is open (the `RelayWindow` rule: GitHub
answered here within `GithubRelayAtMost`, 24 hours), the same list is read from the relay
(`releases.slipgates.com`, `per_page=10`; the Worker defaults `page` to 1 and keys its cache on both). The relay's answer must be its own 200 for that page,
say its `Age` (at most `CdnOldest`, one hour), and be no larger than a GitHub list. It then goes
through the same verification as GitHub's: the highest canonical `v0.1.N` with `draft: false`, exactly
one `halts.json` whose size and sha256 the list names, **downloaded from `github.com`**, and the
strict body. **The relay path never clears a halt:** a relay policy that omits any halt already known is
refused whole, and an older release than the one known is ignored; only GitHub's own answer can
clear one (a reviewed edit). Any failure leaves the policy unrenewed. **The renewal is stamped
`min(now - Age, graceEnd - GithubRevalidateEvery)`**, where `graceEnd` is `GithubRelayAtMost` after the later of
GitHub having last answered here and the relay having last served a verified halt policy (below), so its six hours of freshness
(also at action time, in the app and the host) never run past the grace. The relay is not asked while the shared backoff or the hour reservation holds
the read. GitHub's own answer always wins, and it is asked first every hour.

**Widened Nov 23 (Engineering R8b, P0 on one machine, v0.1.7522): owner-approved (Nov 23, relayed by Engineering R8b; the Chief records it on #2225, and its link goes here once that comment exists).** The Nov 22 approval below
names refusals (403 and 429). On that machine the policy read failed for another reason (its 100-release list was past the 4 MiB cap, a
status-0 failure), so no refusal ever reached the relay and the policy aged out; the same would hold for a 5xx or a timeout. The
relay is now tried on any failed GitHub policy read, with every bound above unchanged (open grace, `Age` at most an hour, the carrier
downloaded from `github.com`, never clears a halt, stamped inside the grace), and the unrenewed line names what GitHub actually
answered (an HTTP status, or that nothing came back). The relay window itself is unchanged: it opens from GitHub having answered the
release list here, or from a rate-limit bootstrap, so a machine GitHub has never answered still gets no relay renewal.

**Where it is stored, and the positive signal (so a fleet read can tell a renewing machine from a stale one).** The stored policy
is in the shared state file `%LOCALAPPDATA%\AgentBridge\app\release-discovery.json` (the app folder under the product's data root,
beside its lock file), under the key `haltPolicy`: `policy` (the verified release and its `halted` list), `at` (the time the policy
counts from; fresh for six hours after it, never past the relay grace), `notBefore` (the next ask), `attemptedAt`, `attempt`,
`verified` and `eTag`; `gitHubAnsweredAt` beside it is when GitHub last answered the release list here. Every successful renewal
also logs ONE line: `update policy: renewed from GitHub, release 0.1.N, At=<ISO time>` (`GitHub (not modified)` for a 304, and
`the relay's list, its copy <age> old; <what GitHub answered>` for the relay path). A read that does not renew logs the
`GitHub policy unavailable (<what GitHub answered>); stored policy was not renewed` line instead. Either can appear on a healthy
machine (one failed attempt during an outage, with the relay or the next hourly try renewing it), so read a machine by its renewal
lines: a policy that is current has a renewal line within the last six hours, and one with only `not renewed` lines for longer than
that is the stale case.

**Grace extension (owner decision, Chief, Nov 23, recorded on #2225):** a halt policy the
relay served that passes the whole verification here also extends the relay grace, as a GitHub answer does. "Verified" means the same
checks as GitHub's answer (the carrier named by the list, downloaded from `github.com`, its size and sha256 matching what the list
names, the strict body), the relay's own 200 with an `Age` of at most an hour, no halt already known omitted, and a release not older
than the one known; this is a digest check, not a signature, as the signed anchor below is still not built. State: `relayVerifiedAt`
in `release-discovery.json`; the grace counts from the later of `gitHubAnsweredAt` (or the rate-limit bootstrap) and it, and
`gitHubAnsweredAt` stays GitHub's alone. An older list, one that omits a known halt, one over its `Age`, or one that fails
verification moves nothing. It still never removes a known halt.

**The 72-hour cap (owner decision, Chief, Nov 23):** the relay's verified lists extend the grace only until 72 hours after GitHub last
answered here (`gitHubAnsweredAt`, GitHub's answer to the release list, which the update check makes). The grace therefore ends at the
earlier of 24 hours after its anchor and 72 hours after `gitHubAnsweredAt` (`ReleaseDiscovery.GithubRelayExtendedAtMost`, `RelayGraceEnd`);
when it ends, the relay is not asked and the policy goes stale, as before. A GitHub answer to the release list restarts both clocks and the
extensions resume (the shared refusal backoff must lapse first). A GitHub answer to the *policy* read renews the policy itself but does not
restart these 72 hours, so the cap is measured against the same `gitHubAnsweredAt` the update check's relay rules use.

**Residual, recorded (Nov 23):** the verification is a digest check, not a signature (the signed anchor below is not built). A
compromised or stale relay that keeps serving an older but still verified list (the same release as the one known, its `Age` within the
hour) keeps the grace open, and a NEWER halt stays hidden from this machine, but **for at most 72 hours since GitHub last answered the
release list here**; after that the policy goes stale and the machine holds unasked installs (a GitHub answer to the release list ends that, as it restarts the clocks). It can never forge a halt or
clear a known one. A follow-up issue for real signing is Quality's to file; the signed anchor below remains the real fix.

**Accepted risk** (owner, Nov 22, for refusals; evidence: the owner's answer to Chief S16 at about 20:00 PT, recorded at [#2225](https://github.com/danswett/agent-bridge/issues/2225#issuecomment-7091142370)): the relay is unsigned transport, so a compromised or stale relay
can serve an older list. Its older `halts.json` then renews the stored policy and a **new halt can stay
hidden for up to the grace, which the relay's own verified lists extend to at most 72 hours since GitHub last answered the release list (see the cap above)**. It can never forge a halt or clear a known one: the carrier's bytes
come from `github.com` and must match the listed digest, and an older release never replaces a newer
one. This is the same class of risk as the relay withholding an update. It relaxes this document's
"a compromised CDN cannot renew freshness" rule only inside that grace, and only when GitHub's own policy read fails.

**The signed anchor** (slices 2 and 3 below) stays the real fix and removes this path.

## Choice: A, signed browser-download policy, with a fixed current anchor

Choose candidate A rather than deploying a new policy endpoint through the CDN.
The authoritative bytes are a small `halt-policy.json` release asset plus its
signed SHA-256 catalog, `halt-policy.cat`. Browser downloads do not spend the
GitHub release API quota. No client token, new key or relay deployment is needed.

Each app release carries the pair for provenance, but **clients must not treat
their target release's embedded copy as current**. Provision one dedicated,
published policy release with fixed tag `halt-policy-v1`, excluded from app
selection/pruning, and pinned URLs:

```text
https://github.com/danswett/agent-bridge/releases/download/halt-policy-v1/halt-policy.json
https://github.com/danswett/agent-bridge/releases/download/halt-policy-v1/halt-policy.cat
```

The protected publisher replaces this anchor pair on every release, every halt
change, and hourly even when neither changes. A torn replacement produces an
unverifiable pair and cannot clear a halt. Keep the previous verified snapshot
only until its original expiry; publication must verify both uploaded bytes.
The fixed anchor is a required refinement to candidate A: a per-release asset
alone cannot discover a later halt without an API list or trusted current pointer.
Do not use `/releases/latest`, which selects full releases rather than the
numerically newest app pre-release.

Candidate B can later mirror this **same signed pair**; it would need owner-approved
deployment and identical verification, not comparison to an unsigned relay digest.
A compromised CDN cannot be allowed to self-renew freshness with `Age: 0`.
A adds two small direct downloads and an anchor publisher, but avoids a second
production service change in this cut. GitHub download outages still reduce
availability; removing API dependence is not removing GitHub hosting.

## Signed contract and trust boundary

Use the existing app publisher's Artifact Signing identity, not a repository
write token as signing authority. A Windows Authenticode catalog binds the exact
JSON bytes by SHA-256. Verify catalog membership, WinVerifyTrust chain/revocation,
timestamp, code-signing EKU, Artifact Signing Public Trust EKU and the **same
owner identity EKU pin** required for the app's own executables. A valid vendor
signature or merely a valid certificate is insufficient. Never run the catalog
or any downloaded executable. JSON or `.nupkg` is not magically Authenticode-signed.

Catalog signing/verification with that existing provider is a proposed mechanism,
not proven by the current code. Require fixture verification and an SRE-owned
provider compatibility proof before activation. If this provider cannot sign
catalogs with the required pin, return the design to Engineering; do not substitute
a new signing key, relaxed pin or unsigned policy.
[Signing documentation](../app-windows.md#signing) describes signing as off until
owner-enabled; no deployed signing status is inferred here.

Strict schema v1 of the new envelope contains repository and purpose
(`danswett/agent-bridge`, `update-halt-policy`), monotonic `sequence`,
`issuedAt`, `expiresAt`, the existing bounded `halted` versions, and a bounded
release projection for discovery. Reject duplicate fields, unknown schema,
wrong purpose/repository, noncanonical versions, invalid time/sequence, excessive
depth/size and conflicting duplicate identities. JSON cap: 64 KiB; catalog cap:
256 KiB, enforced while streaming even without Content-Length; 10-second request
deadline. Publisher overflow fails publication, never truncates halts.

The release projection names the newest complete eligible release for each
architecture/channel and stable/pre-release choice, at most four descriptors.
Each signs its tag, channel, pre-release status, feed browser URL, SHA-256 and
size; target feed bytes are checked before consuming their package URLs/hashes.
Only this repository's canonical release-download paths are accepted, with
redirects restricted to the reviewed GitHub release-asset delivery origins.
Cross-repository, userinfo, query tricks and non-HTTPS redirects are refused.
Package integrity and existing publisher checks are not replaced by this policy.

This projection is necessary: replacing `HaltPolicyList` alone leaves the forced
six-hour/pressed discovery API request in the Update now path. Both app and host
derive candidate discovery from the verified projection, applying channel,
pre-release and installed-version checks. It replaces API revalidation and
fallback for this path; failure cannot quietly invoke `api.github.com`.
Only verified projections advance a per-channel floor; fabricated higher versions
in unsigned transport cannot poison that floor.

## Freshness, replay and failure

`expiresAt` must be greater than `issuedAt` and no more than six hours later.
`issuedAt` must not be in the future. Install eligibility requires
`issuedAt <= now < expiresAt`, plus a verified, durably saved snapshot. Hourly
republish provides availability margin, not an extension of the six-hour bound.
At exactly expiry, protection is unavailable (person exception only, below). A 200, 304, cache
hit, retry or process restart never renews signed validity. Prefer cached verified
bytes within their remaining validity; no transport timestamp supplies freshness.

Persist sequence, body hash, signed validity, known halts, channel floors and
observed expiry under the existing installation-wide lock/atomic replacement.
Reject sequence rollback and same-sequence/different-body equivocation; retain
known halts and mark such answers unreadable. Replaying the same signed body is
idempotent, not a new check time. Only a higher, valid publisher-signed policy can
clear a previously observed halt. Sequence is independent of app release number,
so a policy-only change can halt the current release.

A first-time client can be shown an older valid policy and miss a newly published
halt for the remainder of its signed lifetime, **at most six hours**. The signer
is trusted to publish correct current state; withholding cannot force an automatic
unchecked install or extend the old policy indefinitely with an honest clock. It can deny
service after expiry. It cannot cause package downgrade past installed/channel
floors, fabricate signed halts, or clear a halt already protected by a newer
persisted sequence.

Expiry observed by a process is terminal for that sequence, including after
restart when saved. A detected backwards/future clock or failed state write makes
protection unavailable; never refresh from a local receipt timestamp. Arbitrary
same-user disk/clock rollback can restore an old unexpired snapshot after restart;
no rollback-resistant-storage guarantee is claimed. Local compromise is outside
this transport boundary; new installs also have no previous sequence anchor.

The first newer post-save time observation is saved under the installation lock.
Later final observations are kept in memory and rechecked for backwards time and
expiry; expiry first observed after that save is latched durably too. Restarts
recover only saved observations: a failed save or unsaved final sample has no
restart guarantee. Retained known halts still refuse in the current process.

Transport failure, missing asset, hash/signature/pin failure, malformed content,
timeout, cancellation or persistence/lock failure is explicit unavailable/stale
state, never an empty checked policy or "up to date". Known halts still refuse.
Ordinary installs may retain a still-valid durably verified prior snapshot after
a transport outage, but contradictory/equivocating policy marks it unavailable.
Automatic installs, batch installs and unknown/null protection are never unchecked.
Recheck validity and known halts at download/install/departure commit boundaries.
Separate policy retry state from legacy API backoff; unrelated API quota cannot
suppress browser-policy refresh. Bounded Retry-After/backoff schedules transport
retries but never extends signature validity.

## Publishing authority and owner decisions

Halt edits remain reviewed `ops/halts.json` changes landed by Quality. Only the
protected current-main publisher, using its existing authorized signing identity
through OIDC, may generate/sign the envelope. A release maintainer or compromised
GitHub write credential without signer authority can delete/replace assets for
denial of service, not produce an accepted new halt policy. Delayed jobs reread
current main under one publication lock and allocate a higher sequence; they may
not overwrite a newer generation. Clearing halts requires a reviewed policy edit.
Signing credentials are never installed on clients.

**Owner decisions (owner-approved 15:24 PT Nov 21):**

1. **Install policy B.** When the policy cannot be verified, keep a person's
   confirmed, warned, signed and audited single-machine install (as Nov 19,
   12:06 PT); it gives no blanket never-unchecked guarantee. Known halts always
   refuse; automatic, batch and null protection fail closed. So the reader reports
   distinct unavailable, known-halted and verified outcomes for the wiring.
2. **Existing signer and fixed anchor, yes.** Sign with the existing app
   release-signing identity (no new key) and publish the pair at the fixed
   `halt-policy-v1` anchor on every release and halt change.

No key rotation/new key or relay deployment is authorized by this design.

**Where sequences live.** The client's highest accepted sequence lives in its
installation state beside `release-discovery.json`, under the same lock. Lost or
replaced state is a new install: no anchor, at most six hours of exposure. The
publisher's sequence is the anchor's own verified sequence plus one, never a
counter reset, so clients that reject lower sequences cannot be locked out.

## Ordered slices and focused tests

Each implementation PR stays under 400 WHOLE lines; split rather than exceed it.
No later slice starts until this design is CLEAN and the Chief sends the go.

1. **This PR: one design file, below 300 lines.** Whitespace, local links and source
   consistency only; no app/host execution or CI polling.
2. **Publisher prerequisite, coordinated with SRE/Quality, separate small cuts.**
   Owner approvals and catalog-provider proof first; strict envelope builder,
   serialized sequence/time issuance, signed catalog, fixed-anchor publication and
   post-upload byte verification. Synthetic tests: current-main re-read, same app
   new halt, clearing review, delayed job, torn pair, signing off/failure, wrong
   publisher, descriptor/size overflow and pruning exclusion. Workers deliver
   workflow changes to Quality, never push them or deploy anything themselves.
3. **PR 2 series: verified reader/cache and host/app wiring.** Red-first fixture
   feeds/catalogs and injected clock: valid empty versus missing, bad hash/EKU/pin,
   future issue, exact expiry, replay/rollback/equivocation, restart/clock rollback,
   failed atomic save/lock, concurrent profile budget, stale retained halt and
   higher-sequence clearing. Exercise new install, older-than-six-hour legacy
   state, pressed discovery, both channels/rings, forged version floor and held API
   budget. Assert zero `api.github.com` requests through full Update now acceptance,
   discovery, download and departure, including failure paths. Cover install policy
   B explicitly, automatic/batch/null holds and late expiry/halt at commit.
4. **PR 3: refusal wording and retry metadata**, folding
   [the wording issue](https://github.com/danswett/agent-bridge/issues/3553).
   Synthetic host-to-app refusal proves code/retry preservation, not a guess about
   VM4. Legacy rate-limit evidence reads "bad-release check unavailable: GitHub
   rate limit; try again after HH:MM PT"; new-source failures name that source and
   reason. Retry time comes from actual scheduled retry, formatted in Pacific
   including DST/date rollover; missing time is not invented. Test null/unknown
   codes keep the generic fallback and stale historical quota is not current.

All executable tests use synthetic feeds, clocks and fixture certificates, serial
with one CPU on approved disposable/Azure test hosts. No live GitHub update calls,
test agent sessions on live machines, signing-key changes or deployment rehearsal.
The consumer runs in every installation's existing app/host, with local cache state;
publication runs in protected CI, with its durable sequence at the policy release.
No particular fleet node is required; losing one node affects only that installation.
