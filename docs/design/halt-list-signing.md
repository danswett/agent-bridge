# Halt list: real signing

Status: proposed, design only; follow-up #3893 (owner decision, recorded on #2225), Nov 23, 2026 PT.
Sensitive: update trust, keys and publication. Two independent model families through Quality.
This document changes no runtime behavior, signing configuration, workflow, relay deployment or key. **It needs an owner decision on key
custody (below) before any code**, and no agent holds, generates, copies or handles a private key at any step.

Builds on [halt-policy-without-github-api.md](halt-policy-without-github-api.md), which chose a signed anchor (its candidate A) and
described it with Authenticode catalog signing. This document compares that mechanism with the one the issue names, a **pinned public
key**, and recommends one.

## What exists, and the gap

- The halt policy is a small list of halted release versions that stops an update to a known-bad release (`HaltPolicy.cs`,
  `GitHubHaltListSource`). The app and the host verify it, and an unverified or stale policy holds installs nobody asked for.
- Its only trust root today is **GitHub**: the list's release names a `halts.json` asset with a size and a sha256 digest, and the client
  downloads that asset from `github.com` and checks the digest. There is no signature code.
- #3778 and #3862 let a machine renew the policy from the **relay's copy** of the same release list when GitHub's own read fails. The relay
  is unsigned transport, so a relay-delivered list is trusted only because its carrier asset is fetched from `github.com` and matches the
  digest the list names. #3862 caps the relay's grace extensions at 72 hours since GitHub last answered, and a relay list never removes a
  known halt.
- **The gap (residual recorded in #3862):** a compromised relay can keep serving an older but digest-consistent list and hide a NEWER
  halt for up to 72 hours while GitHub's API is unreachable. Its `Age` header is outside every digested payload, so it is a staleness
  hint, not a bound. Real signing closes this: authenticity and issue time are proven by a signature the relay cannot forge.

## Goals and non-goals

Goals: a list whose **origin and issue time are cryptographically proven** whatever transport carried it; rollback and replay protection;
a key lifecycle (rotation, revocation, compromise) that does not depend on the list it protects being readable; a release-train step that
no agent can influence beyond requesting it.

Non-goals: a new transport (the relay and the fixed anchor stay as designed); signing the app packages (already handled by the
publisher's identity, [app-windows.md](../app-windows.md#signing)); any key handling by an agent; building anything before the custody
decision.

## Threats

| Threat | What it does | Closed by |
|---|---|---|
| Compromised or stale relay/CDN | Serves an older (or edited) list; spoofs `Age` | Signature over the exact bytes including `issuedAt`; freshness from the signed time, never a header |
| Rollback | An old validly signed list replaces a newer one | Monotonic signed `sequence`, persisted by the client; a lower one is refused |
| Replay past expiry | A once-valid list is served long after | Signed `expiresAt`; an expired list renews nothing |
| GitHub release/account compromise | Replaces the asset and its digest together | The signature: the attacker has no private key |
| Private key compromise | The attacker signs lists, including ones that clear halts | Revocation and rotation (below); containment by never letting a *clearing* list be accepted from a lone new key |
| Key loss | No one can sign: policy ages out everywhere | Offline recovery key and a documented break-glass release (below) |
| Clock skew | A client rejects good lists or accepts old ones | Bounded skew allowance; the signed times are compared with the local clock, and a future `issuedAt` is refused |
| Torn publication | An unverifiable pair clears nothing | Fail closed: unverifiable means unrenewed, never an empty list |

## Mechanism: two options, one recommendation

**Option 1: Authenticode catalog** (the existing document's candidate A). A catalog binds the JSON bytes by sha256 and is signed with
the app publisher's existing signing identity, verified with the platform's trust API against a pinned owner-identity EKU. For: no new key.
Against: the catalog signing capability with this provider is unproven (the existing document says so and requires an SRE compatibility
proof first); verification uses a **Windows-only** trust API, while the host's verification code is shared with Linux CI and a future
non-Windows host; the trust root is a vendor's certificate chain and revocation service, whose outages and policy changes we do not
control; rotation is the vendor's, not ours.

**Option 2: a pinned public key** (what #3893 names). A detached signature over the canonical envelope bytes, verified with a public key
compiled into the app and the host. For: portable (`ECDsa` P-256 with SHA-256 is in the .NET base library, and P-256 is already in
use here for the Noise handshake), no vendor service in the verification path, rotation and revocation under our control, the whole
verification is a small pure function with test vectors. Against: we own a key and its lifecycle (the custody decision).

**Recommendation: Option 2, ECDSA P-256 with SHA-256, pinned keys.** Keep Option 1's anchor structure (the fixed `halt-policy-v1`
release carrying the signed pair, re-published on every halt change and at least daily) and its strict envelope. Option 1 stays a fallback
if the owner decides against holding a key. The algorithm is a recommendation, not a decision; Ed25519 would be preferable where available
but is not in the base library today.

## The signed envelope

One JSON file, `halt-policy.json`, plus its detached signature `halt-policy.sig` (raw r||s, base64url). Strict schema v1, canonical
bytes (the file's own exact bytes are signed; the client never re-serializes):

```json
{ "v": 1, "purpose": "update-halt-policy", "repository": "danswett/agent-bridge",
  "keyId": "<first 8 bytes of sha256 of the public key, hex>",
  "sequence": 1042, "issuedAt": "2026-11-23T20:00:00Z", "expiresAt": "2026-11-24T20:00:00Z",
  "halted": ["0.1.2349"], "revokedKeys": [] }
```

Rejected: duplicate or unknown fields, a wrong `purpose` or `repository`, a noncanonical version, a bad time, `expiresAt <= issuedAt`
or longer than a stated maximum window, depth or size over the cap (64 KiB, enforced while streaming). **`issuedAt` and `expiresAt` are
inside the signed bytes**: this is what makes the relay's `Age` header unnecessary as a security input.

## Verification rules (client, app and host)

1. The file's size is within the cap; the signature verifies over its exact bytes with a key in the **pinned set** whose `keyId` matches;
   a key listed in the client's own revoked set, or in the list's `revokedKeys` signed by a *different* still-trusted key, is refused.
2. `purpose` and `repository` match; `issuedAt` is not more than a small skew in the future; `now < expiresAt`.
3. `sequence` is **strictly greater than the highest verified sequence stored** on this machine (the existing state file gains a
   `haltSequence`). Equal is accepted only for the identical bytes (a re-fetch); lower is refused.
4. Freshness for the grace and for six-hour action-time freshness is `now - issuedAt`, from the signed time. The relay's `Age` is ignored.
5. A halt already known is **retained unless a signed list with a higher `sequence` removes it**: with authenticity proven, clearing
   no longer needs GitHub's own answer, but it needs a higher signed sequence and a key that has been trusted for the minimum overlap.
6. Any failure leaves the policy unrenewed (as today); it never becomes an empty list.

## Transport

Unchanged: the fixed anchor release asset pair is the source of truth, fetched from `github.com` browser downloads (no API quota); the
relay serves the same bytes as plain transport. **The relay path no longer needs the digest-of-carrier or the grace cap for safety**,
because a relay can no longer forge or age a list; it can only withhold one, which the signed `expiresAt` bounds.

## Key lifecycle

- **Pinned set.** The app and the host embed a list of public keys: the current key and a *next* key. Rotation = signing with the next key
  after a release has shipped it to the fleet; the old key then moves to the revoked set in a later release. The overlap is at least two
  full release cycles, so a machine one update behind still verifies.
- **Revocation.** (a) The signed list's `revokedKeys` revokes any pinned key *other than the signer*; (b) an app/host release removes a pin.
  Neither relies on the revoked key's cooperation. A client that cannot reach a release carrying the revocation stays exposed: this is why
  the private key's custody is the central decision.
- **Compromise response.** Revoke through the next key and ship an urgent release; because a compromised key could sign a *clearing*
  list, rule 5's overlap requirement stops a lone, newly introduced key from clearing a halt until it has been in a shipped release.
- **Loss.** A recovery key, held offline, is also in the pinned set from day one and can sign only a list that adds a key. Break-glass:
  a release with a new pin set, installed by a person's press (the policy-unknown hold does not stop a press).
- **Provisioning is not an agent task.** Key generation, storage, backup and rotation are owner/SRE operations. This design specifies
  only what the software must accept; no agent receives, derives or copies key material, and no key appears in a repository, log,
  issue or message.

## The release-train signing step

A protected publisher job (the existing release publisher's protected environment) requests a signature for the new envelope: it computes
the bytes, asks a **non-exportable signing service** (a managed HSM or key vault *sign* operation, or an owner-held hardware key) to sign
them, verifies the signature against the pinned public key itself, and uploads the pair to the fixed anchor. The runner never holds the
key. It re-signs with an incremented `sequence` and a fresh `issuedAt`/`expiresAt` on every release, every halt change, and on a schedule.
**Cadence is a decision:** a 24-hour `expiresAt` re-signed every 6 hours bounds a withheld list to a day and costs four short runs a
day, against an hourly cadence's shorter bound and higher cost (Actions capacity is being conserved until Dec 1).

## What it replaces, and what stays

- Once signed lists are accepted everywhere, the **digest-only relay check and its carrier download are retired**.
- The **72-hour cap stays as defence in depth** for one release cycle after retirement, then the owner decides whether to keep it: with a
  signed `expiresAt` the cap is redundant, but it also bounds the damage of a signing-side mistake (an over-long `expiresAt`).
- The never-clears-a-known-halt rule for the *relay* path is superseded by rule 5 (signed higher sequence), which applies to every transport.

## Ordered slices (each at most 400 whole lines, tests first, sensitive)

1. **Verifier and vectors (no key).** A pure `HaltListVerifier` in Host.Core with fixed test vectors made from a **throwaway test
   key generated inside the test**, never a real one: valid, wrong key, tampered byte, wrong purpose, past/future time, lower sequence,
   revoked key, oversize, duplicate field. No wiring.
2. **State and wiring, dual-accept.** `haltSequence` in the state file; the policy read prefers a verifying signed pair from the anchor
   and the relay, and **falls back to the current digest path** when no signed pair exists. Signed freshness replaces `Age`.
3. **Publisher step.** The protected job and its dry-run, **after the custody decision and an SRE-provisioned key**; verified by the job
   checking its own signature against the pinned key before upload.
4. **Pin the real key and retire.** Ship the real pins, then retire the digest-only path after the owner confirms the fleet has updated.

## Failure matrix to test

Unsigned list during dual-accept (digest path, unchanged); signed list with a bad signature (refused, digest path not used for it);
signed list valid but older sequence (refused); expired (refused, policy goes stale as today); future `issuedAt` (refused); signing key
revoked by a later list; relay withholds the signed list for longer than `expiresAt` (policy stale, installs hold); GitHub unreachable
and relay serves a valid signed list (renews, no grace cap needed); torn pair (unrenewed, nothing cleared).

## Owner decisions needed before any code

1. **Key custody:** where the private key lives (a managed HSM / key vault signing operation, or an owner-held hardware key) and who may
   authorize a signature, a rotation and an emergency revocation.
2. **Mechanism:** Option 2 (pinned ECDSA key, recommended) or Option 1 (the publisher's Authenticode identity, which needs the provider
   compatibility proof first).
3. **Cadence and window:** the re-sign interval and maximum `expiresAt` window (recommended: re-sign every 6 hours, 24-hour window).
4. **Recovery key:** whether an offline recovery key is held, and by whom.
5. **After retirement:** keep or drop the 72-hour cap.
