# Device approval and the signed roster (#55)

**Status: decided design, not built.** The owner decided every §11 decision on 2026-11-04, each
option (a). Each slice is still security-reviewed independently before merge (§12). It refines
design.md [§5.4](../design.md#54-accounts-devices-and-the-signed-roster). Where the two disagree,
this note is the newer reasoning; design.md §5.4 is to be updated to these decisions.
A bare § number below is a section of this note; design.md's are always named as such.

The aim: a device that signs in with the account's GitHub login can do nothing until a
person approves it, on a device that is already trusted, by matching codes on both screens.
Every host then acts only for approved devices, and anything only a person may do needs a
fresh proof that a person was at an approved device, not just that device's key on a channel.

Revision 2 (2026-11-03) answers the independent security review of revision 1. It makes enrollment
mutual (§4.2), adds seniority and probation (§3.4), replaces the fork rules with one acceptance rule
(§3.5), requires presence-proven person commands (§7.2), adds freshness (§6.3), and gives pending
devices nothing (§7.1).

## 1. Where things stand

| Today | Where | Consequence |
|---|---|---|
| The relay admits any key that proves possession and holds a valid account token, and takes `kind` and `name` from the device's own `hello` | `RelayHub.AdmitAsync` | Anything holding the token can connect a fresh key as `pwa` |
| A host completes a Noise handshake with any device in the account's presence and serves it the full `SessionService` handler | `ChannelHub.OnDataAsync`, host `Program.cs` | Any device of the account can read and drive every session (#186 review) |
| Person-only checks read the relay's (self-declared) kind | `SessionTransfers.Unsigner` (`pwa`, `app`, `phone`), `FolderTrustAnswers` and `UpdateService` (`pwa`, `app`), relay `ForgetAsync` (`app`, `pwa`, `phone`) | A compromised host can reconnect as `pwa` and authorize transfers, answer folder trust where it is allowed, press Install, and forget machines (#138 review, #70 comment) |
| The three host checks list person kinds separately, and they differ | as above | The native iPhone client (`phone`, #260) can sign transfers but cannot answer folder trust or press Install |
| Folder trust has a per-machine allow-list of device ids (`launch.remoteFolderTrustDevices`) | `LaunchConfig`, Settings | The list shows the relay's names, so look-alikes are possible (#157 item 4) |
| Device keys are P-256 **ECDH-only**: a TPM key, a non-extractable WebCrypto key, a Secure Enclave key-agreement key | `DeviceKeys`, `pwa/js/noise-p256.js`, the Apple clients | No device can make a signature that a third party can verify. Signed commands are designated-verifier MACs from static-static ECDH (`CommandMac`), which only the signer and one named machine can check |
| The device key is usable by any process of the same user, without a prompt | `DeviceKeys.LoadOrCreate` → `TpmP256.Open`; IndexedDB for the PWA | Channel identity and `CommandMac` prove which key, not that a person acted. Malware or a prompt-injected agent on a machine with the Windows app is that app on every channel |
| The account token is a bearer token, not bound to the device key | `Tokens.Issue` | A stolen token admits any key |

The last three rows shape the design: a roster that every host checks on its own needs a
signature anyone can verify; the token cannot be the gate; and a person-only action needs a
proof of presence, not only a key.

## 2. Threat model

In addition to design.md [§5.1](../design.md#51-threat-model):

| Party | Assumption | What it must not be able to do |
|---|---|---|
| **The account's GitHub sign-in** (a stolen token, a phished device flow, a leaked relay session token) | Can admit new keys to the relay, with any `kind` and `name` | Read or drive a session, pass a person-only check, appear as an approved machine, approve itself, or replace the roster |
| **The relay** (operator, storage, a compromised deployment; it mints tokens) | Untrusted for authorization. It stores and serves the roster, routes frames, and can drop, delay, reorder, replay or withhold anything, and act as any number of devices of its own | Forge or alter a roster entry, forge an approval, make a verifier forget an entry it holds, substitute keys in an enrollment or pose as an approver to a new device without the codes failing, or keep a removed device served on a host that any device holding the removal reaches within the freshness window (§6.3). It **can** deny service, show a pending device a misleading name, and keep a removed device served inside a partition it maintains (§6.3), or at a host that does not yet hold the removal's parent (one the remover has no attestation from, or one behind a just-made confirmation) until the roster is imported there (§3.3) |
| **A compromised machine** (an agent or malware running as the user where a host and the Windows app run) | Holds the host key and the account token, has the use (not a copy) of the app's TPM keys, and can inject input | With the host key: pass a person-only check, add or remove devices, or take another machine's sessions without a person's press. With the app's keys: approve devices or pass person-only checks. The app does either only with a presence proof, which R4 must show same-user code cannot produce (§4.6). It **can**, as that app, read and drive sessions on the account's other machines, as it already can on its own: the residual named in §7.3 |
| **A pending (unapproved) device** | Has completed sign-in and proved its key | Learn anything but that it is pending: no roster, machines, sessions, usage or names; no pushes or blobs. Hosts in `enforce` hold to this from the start; the relay does once the account's relay mode is `enforce` (§4.5), which the owner switches on after the cutover (§8.2) |
| **A newly approved device that turns out to be hostile** (approved by mistake or by a compromised approver) | Holds an approved key | Remove any device that is not junior to it on its own signature, ever, or with a cosigner until its approver has confirmed it; add devices that take effect during its probation (§3.4); touch a device senior to it in any way. Its removals of devices junior to it are not held, and apply at once. It **can** disable devices that are not senior to it by copying their keys: a conflict makes one inactive, and for a device junior to it the copy can displace the genuine enrollment. Both are announced naming it, and undone by removing it, apart from devices its own subtree enrolled and then copied, devices enrolled under a copy, and what either enrolled, which go with it, and apart from a copy that a removal signed on a branch unable to see the original kept, which leaves that device conflicted until it is enrolled again (§3.3) |
| **A removed device** | Was approved; still holds its keys and maybe a valid token | Anything a pending device cannot, once a host learns of the removal. A host learns of it from any device that holds the removal, or goes stale after the freshness window. Two exceptions are stated: a relay-maintained partition (§6.3), and a host that does not yet hold the removal's parent (one the remover has no attestation from, or one behind a signer's just-made confirmation), where a hostile target can make the removal need more evidence than the limits allow, until the roster is imported at that machine (§3.3) |
| **The person at an approved device** | Trusted | They are the only source of approval and of person-only actions |
| **The local pipe and the HA connector** | Unchanged: same-OS-user trust, and Home Assistant credentials respectively | Out of scope for the roster; see §9.5 |

Non-goals: protecting against the person approving an attacker's device on purpose, against
malware that waits for the person to approve something and changes what is approved, and
availability against the relay.

## 3. The signed roster

### 3.1 Keys and presence

**Every approved person's device holds a second key, a P-256 ECDSA signing key (`sign`)**,
beside its ECDH device key (`dh`), created on the device. Some person-only signatures also
need a **presence proof**, evidence that a person verified at that device just now, which a
host can check (§4.6). What a device can offer is recorded in its roster entry as
`device.presence`:

| `presence` | Meaning | Who |
|---|---|---|
| `key` | `sign` itself refuses to sign until the person verifies, enforced outside the app's process | Apple: Secure Enclave `SecureEnclave.P256.Signing`, with `.userPresence` on iPhone and iPad and, on a Mac, only `.biometryCurrentSet` with no password fallback. Windows: only once R4 proves its mechanism (§4.6) |
| `webauthn` | Each presence-proven signature also carries a WebAuthn assertion with user verification from a platform authenticator, over the same message; the credential's public key is in the entry | PWA (R5), recorded only for a device-bound credential on a platform the approver's client counts as proven (§4.6). Windows may use this route too |
| `none` | No presence proof | Any device on a software key (DPAPI, a Keychain key without the Secure Enclave), a Mac app without a Touch-ID-only key, a PWA whose credential does not qualify, and the Windows app until R4 |

A device declares `presence` when it enrolls, and nothing can raise it later without a new
enrollment. **The declaration is the enrollee's claim.** Enrollment proves possession of the keys,
not the policy they were created under. A device that is already compromised when it enrolls
can declare a software key as `key`, or bring a software WebAuthn authenticator that reports
user verification it never did. Where the platform gives one, the approver checks a WebAuthn
attestation statement (packed or TPM attestation from a platform authenticator, against
pinned roots) and records `attested: true`. Synced passkeys usually give no attestation, and
Secure Enclave keys made with CryptoKit have none a third party can check, so for those the
claim stands on the person approving a device they hold. The guarantee is therefore: **presence
holds for devices that were not compromised when they enrolled.** A device compromised later
cannot change its declared policy, and that is what the presence proof is for. Under owner
decision 3(a), only devices recorded with `key` or `webauthn` (which an approver records only for a
qualifying credential, §4.6) can approve or do person-only actions.

**Approved person's devices sign entries** (multi-approver roster, owner
decision 1), within the seniority rules of §3.4. **Hosts never sign** entries and are never
approvers. The roster is rooted in a genesis entry made by the first person's device.

Considered and not recommended:

* *Pairwise MACs per host*, extending today's signed commands. They cannot reach a host that
  is offline at approval time or joins later.
* *A single account root key* held by the first device. Every approval needs that device, and a
  browser evicting the PWA's IndexedDB loses the account. Seniority (§3.4) gives the first
  device the most authority without making it the only approver.
* *Per-host approval lists only*, like `launch.remoteFolderTrustDevices`. They cannot be
  approved from a phone away from the machine, and N devices × M machines does not scale. The
  folder-trust list stays as an extra, per-machine narrowing on top (§9.2).

### 3.2 Entries

An entry is sent as exact bytes with detached signatures:

```json
{ "body": "eyJ2IjoxLC…", "sig": "MEUCIQ…", "cosig": "MEQCIF…", "uv": { "by": { "…": "…" }, "cosigner": { "…": "…" } } }
```

| Field | |
|---|---|
| `body` | The entry's JSON, UTF-8, base64url without padding. Signers and verifiers use these exact bytes; nobody re-serializes |
| `sig` | `by`'s signature over `F("agent-bridge roster entry v1", bodyBytes)` (framing `F` below), never over the raw body, so a roster signature can never be taken for any other signature the same key makes |
| `cosig` | Only where §3.4 needs a second signer: `cosigner`'s signature over `F("agent-bridge roster cosign v1", bodyBytes)` |
| `uv` | One WebAuthn assertion **per signer** whose `presence` is `webauthn`, keyed `by` and `cosigner`, on an entry that needs presence (§3.3). Each has `authenticatorData`, `clientDataJSON` and `signature` (base64url), with challenge `SHA-256(F("agent-bridge presence v1", role, bodyBytes))`, where `role` is `by` or `cosigner`, and each verifies under that signer's own credential key. A removal cosigned by two PWAs therefore carries two assertions |

**Framing.** Every hash, MAC and signature input in this note is
`F(x₁, …, xₙ) = L(x₁) ‖ … ‖ L(xₙ)`, where `L(x)` is the length of `x` in bytes as a 4-byte
big-endian unsigned integer followed by `x`. Labels are their ASCII bytes; the account id is
the UTF-8 bytes of the string the relay's `ready` gives; hashes, nonces and keys are their raw
bytes.

`body` decodes to, for an `add`:

```json
{
  "v": 1,
  "op": "add",
  "account": "<account id>",
  "generation": "<32 lowercase hex, random, fixed by genesis>",
  "seq": 7,
  "prev": ["<entry hash of the parent>"],
  "by": "<signer's device id>",
  "at": "2026-11-02T22:14:05Z",
  "device": {
    "id": "<32 lowercase hex>",
    "dh": "<base64url, 65 bytes>",
    "sign": "<base64url, 65 bytes; absent for a host>",
    "signAlg": "p256-sha256-der",
    "presence": "webauthn",
    "webauthn": { "rpId": "danswett.github.io", "credentialId": "<base64url>", "key": "<base64url, 65 bytes>", "attested": false, "backupEligible": true },
    "kind": "phone",
    "platform": "ios",
    "name": "Travel iPhone"
  },
  "approval": { "method": "code", "transcript": "<base64url, 32 bytes>" }
}
```

| Field | Rules |
|---|---|
| `v` | 1. Any other value is refused; a new version is a deliberate protocol change |
| `op` | `genesis`, `add`, `remove`, `rename`, `merge`, `confirm` (§3.4). Grants (`set grants` in design.md §5.4) are left for later; §7.7 says what that leaves unspecified for `manage.*` |
| `account` | The account id. An entry from another account never verifies |
| `generation` | Chosen at genesis. A reset (§5.5) is a new generation |
| `seq` | 0 at genesis; otherwise one more than the highest `seq` among its parents |
| `prev` | The parents' entry hashes, in **head order** (ascending by the raw 32-byte hashes, compared byte by byte): one parent, or two to four for a `merge`. Absent at genesis |
| `by` | The signer's device id |
| `cosigner` | Only with `cosig` (§3.4) |
| `at` | When the signer says it signed. Shown to people; **never** used for ordering, expiry, probation or freshness |
| `device.id` | `hex(SHA-256(dh)[0..15])`, lowercase: the first 16 bytes of the SHA-256 of the 65 decoded bytes of `dh`, as the relay and transfers already compute it |
| `device.dh`, `device.sign` | Uncompressed SEC1 P-256 points (`0x04 ‖ X ‖ Y`), checked on the curve before any use (the S2 requirement) |
| `device.signAlg` | With `sign`, and only `p256-sha256-der` in v1. This protocol's name, not JOSE's `ES256`: the key is the SEC1 point above and the signature is strict minimal DER `SEQUENCE { INTEGER r, INTEGER s }` with low-S (`s ≤ n/2`). Signers normalize; verifiers refuse high-S or non-minimal DER. WebCrypto's and CryptoKit's raw `r‖s` is converted |
| `device.presence` | `key`, `webauthn` or `none` (§3.1). A host has none of these fields |
| `device.webauthn` | With `presence: webauthn` only: the relying party id, the credential id, the credential's P-256 public key as a SEC1 point (converted from COSE at registration), `attested`: whether the approver verified an attestation statement for it (§3.1), and `backupEligible`: the BE flag of the registration, which every assertion must repeat (§4.6) |
| `device.kind` | **The bound kind**: `host`, `app`, `pwa`, `phone`. `test` is never rostered |
| `device.platform` | `windows`, `macos`, `linux`, `ios`, `ipados`, `android`. For a PWA, the system its browser runs on. Shown to people; the approver's client uses it at enrollment to decide which `presence` to record (§4.6), and verifiers never read it for a decision. Like `presence`, it is the enrollee's claim, which the approver sees beside the device it holds |
| `device.name` | At most 64 Unicode scalar values and 256 UTF-8 bytes. The approver removes every character in the project's hidden-character set before signing: the one `ShownText.HasHidden` tests in `AgentBridge.Protocol` (with `HiddenText.HasHidden`, its twin in `AgentBridge.App.Core`) and `hasHidden` tests in `pwa/js/hidden-text.js`, which a Swift client ports as it is. **Not** the `HIDDEN` pattern in `pwa/js/attribution.js`: that is a different set, made for one-line labels, and a name stripped by it would sign differently. The approver also removes every C0 and C1 control, the tab and the line breaks included: the hidden-character set keeps `\t` and `\n`, which a verifier refuses in a name, so an approver that removed only that set could sign `"Dan's\tPhone"` and every verifier would refuse its add. A verifier never alters a signed name: it refuses one over the limits or holding a C0 or C1 control, and shows any other hidden character as a visible escape, one per scalar value (`\uXXXX`, four uppercase hex digits, up to U+FFFF; `\u{X…}` above; an unpaired surrogate as its own `\uXXXX`), as `ShownText.Reveal` and `hidden-text.js`'s `reveal` do. It never refuses those, since unassigned code points differ between platforms' Unicode versions |
| `approval` | Exactly one of these member sets, by `method`: `{ "method": "code", "transcript": T }`; `{ "method": "qr", "transcript": T, "shown": "approver" | "joiner" }`, where `shown` says which device displayed the QR (§4.3) and `T` is computed as in §4.2; `{ "method": "grandfather" }` (§8.2), with no transcript, since there was no exchange; `{ "method": "genesis" }`, on the genesis entry only. `T` is 32 bytes, base64url. Any other member, a missing one, or `grandfather` or `genesis` with a transcript, refuses the entry |

The exact field set per `op`:

| `op` | Fields besides `v`, `op`, `account`, `generation`, `seq`, `by`, `at` |
|---|---|
| `genesis` | `device` (a person's), `approval` (`method: genesis`, no `transcript`); no `prev` |
| `add` | `prev`, `device`, `approval` |
| `remove` | `prev`, `target` (`{ "id": <device id>, "add": <entry hash of the add it revokes> }`), `keep` (0 to 512 entry hashes of adds in the target's approval subtree that stay, distinct, ascending by their raw bytes; empty in a self-removal), optional `cosigner` |
| `rename` | `prev`, `target` (`{ "id", "add" }`, as in `remove`), `name` |
| `confirm` | `prev`, `target` (`{ "id", "add" }`, as in `remove`) |
| `merge` | `prev` (two to four), optional `adopt` (only ever `true`; §3.5) |

**Strict parsing** (all three implementations, with shared negative vectors in R1). The body
must be I-JSON (RFC 7493): valid UTF-8, no duplicate member names, no lone surrogates. Member
names and enumerated values are case-sensitive. Each object has exactly the fields the table
allows: an unknown or missing field refuses the entry. `seq` is an integer in `[0, 2^31)`,
written without a fraction, exponent or sign. Ids are exactly 32 lowercase hex characters, and
binary fields are base64url without padding that decode to exactly the stated lengths. An
entry's **size** is measured over its fixed content, never over the wrapper a path sends it in:
the body bytes, the decoded signatures, and each assertion's decoded authenticator data, client
data and signature. A `remove` is at most **46 KiB** and every other entry at most **8 KiB**, and a
larger one is invalid on every path: relay append, gossip, enrollment and `roster.submit` (§7.1).
Otherwise a signer could pad an entry with legal JSON whitespace past what a submission carries
while it still fits a channel frame, and an entry every host accepts by gossip could not be
delivered by `roster.submit` as part of a removal's proof. 46 KiB is what fits one channel frame
(65,519 bytes of plaintext) once the wrapper base64url-encodes it: about 62.8 KB with the
envelope. So every valid entry travels in one frame on every path, provided the wrapper is
**canonical**: its members in the order of the table above, with no insignificant whitespace.
Every sender, and every forwarder (the relay, a gossiping device, an approver at enrollment),
re-emits the wrapper canonically rather than passing on what it received. Each assertion's
signature must be minimal DER **with low-S** (`s ≤ n/2`), as `sig` is. An authenticator may
produce a high-S signature, so the signer's client normalizes it (`s` to `n − s`, which verifies
under the same key) before the entry is measured, wrapped or sent, and every verifier refuses an
assertion signature that is high-S or not minimal DER. Each signature then has exactly one valid
encoding, so nobody but the signer can enlarge an entry. Were high-S accepted, anyone could
replace `(r, s)` with `(r, n − s)` without the key, and the minimal DER of the two can differ by
a byte: an entry exactly at its limit would grow past it under the same entry hash. A `remove` keeping 512 adds, with both
signers' assertions, is about 25 KiB; an `add` with the longest WebAuthn credential id and name
is under 6 KiB.

**Entry hash and identity.** The entry hash is `SHA-256(bodyBytes)` over the decoded bytes,
written as base64url without padding wherever it appears (`prev`, pins, `rosterHead`, logs). An
entry's identity is its entry hash alone. Two copies of one body with different signatures are
the same entry: the first valid copy is kept and the second ignored, never treated as a fork.

Kinds stay the authorization classes every check already uses. An iPhone or iPad is `phone`,
a Mac app is `app`, a Mac host is `host`, and `platform` says which. Adding a class later means
adding it to one set (`RosterKinds.IsPerson`) in each implementation.

### 3.3 Validity of one entry

An entry is evaluated in the **history state of its parents**: the state §3.5 computes from all
of the entry's ancestors, **quarantine ignored**. Quarantine is a verifier's local decision about
whether to apply a branch, not part of the branch's history, so it never makes an entry invalid: a
removal or merge signed by a device whose add is quarantined at some verifier is still valid, and
is kept there until the branch is adopted (§3.5 rules 3 and 4). Applying entries is a separate,
later step that does use quarantine. "Active in that state", here and below, means active in the
history state. It is valid only if:

* it parses strictly (§3.2) and `account` and `generation` match;
* `sig` verifies under `by`'s `sign` key (and `cosig` under `cosigner`'s), as recorded by the add
  that made `by` active in that state. That add, not the bare id, is the entry's **signer**:
  budgets, removals, voiding and bars all attach to it. A fabricated add of a real device's `dh`
  with an attacker's `sign` key is another signer, so nothing signed under it spends or affects
  the real device's budget or standing;
* `by` (and `cosigner`) are active person's devices in that state; for `genesis`, `by` is the
  device it adds. Probation is not a validity rule: it delays when a verifier applies a grant
  (§3.4). Confirmation is: a removal that needs a cosigner is valid only if both signers are
  confirmed in that state (§3.4);
* every entry needs presence: each signer's presence is proven, by `presence: key` or by
  `presence: webauthn` with that signer's own `uv` whose BE flag matches the recorded one, and a
  recorded `backupEligible` of false (§3.2, §4.6). That includes a `merge`, since
  it can adopt a branch (§3.5). Under decision 3(a) a signer with `presence: none` cannot make
  entries at all;
* `add`: `device.id` matches `dh`; the id was never added in the parents' history (a removed key is
  never re-added, and a re-approved device makes a new key pair); a person's device has `sign`,
  `signAlg` and `presence`, and a host has none of them. **Conflicting adds**: if the state holds
  two distinct, **non-void** `add` entries for one id (on concurrent branches, so neither saw the
  other), seniority decides, because displacing a device must need the same authority as removing
  it. `add₂`, signed by S₂, may displace the device `add₁` made only if S₂ is senior to that
  device (§3.4), that is, S₂'s own add is an ancestor of `add₁`; and the same the other way round.
  The rule decides over the **whole set** of such adds at once, so that three or more cannot form
  a cycle that verifiers resolve differently. The set is the id's adds that are non-void for every
  other reason (§3.5); this rule's own outcome never feeds back into it. One add in the set
  **wins** if its signer may displace every other add's device and no other add's signer may
  displace it. At most one add can win. If one does, every other add in the set is
  **subordinate**, and void. Otherwise (two that may displace each other, two that cannot, or a
  cycle such as A's add displacing B's, B's displacing C's and C's displacing A's) the id is
  **conflicted**: inactive in every state that holds the set, whichever arrived first, and so with
  no lineage or signing key there. For two adds this is the pairwise rule: the one that may
  displace the other and cannot be displaced wins.
  **A kept add does not displace.** An add that an applying removal of `t` keeps may not displace
  an add of the same id made outside `t`'s subtree: keeping it spared it from the removal, and
  gives it no precedence. Otherwise G, removing H on a branch from before A's add of X, could keep
  H's copy of X, which that history shows as X's only add, and the copy would go on displacing
  the genuine X once the branches join. With the rule the two conflict: X is inactive, never
  active under H's keys, and is enrolled again with a new key. The rule only takes displacing
  power away from kept adds. An outside add still wins only if its signer may displace every
  other add's device, so it can now win only against a kept add whose device its signer is senior
  to and could have removed, and a void outside add, such as one a removed device signs on an old
  head, is in no conflict set at all. Keep sets themselves never change.
  Apart from that rule, subordination is structural: it depends only on which adds are
  ancestors of which, never on removals. So a device H that adds the public key of a device A it is
  not senior to, on a branch where A is absent (A and H added by G on concurrent branches, say),
  achieves nothing: G is senior to H's copy and H is not senior to A, so H's add is subordinate
  and A stays. Conflicts are announced, naming both approvers. A copy that neither side can
  displace still deactivates the device: if G adds A and H on concurrent branches, A adds X, and H
  adds X's public key on its own branch, neither A nor H is senior to the other's add of X, and X
  is conflicted. No public check can tell the copy from the real enrollment, since `dh` cannot
  sign. What bounds it is that the conflict names H: removing H takes H's copy with it, unless the
  removal keeps it, which it can only on a branch that cannot see X's genuine add (§3.3,
  `remove`; §3.5), X is no longer conflicted, and it is active again with no new enrollment.

  **What a hostile approver can do with copies, and what bounds it.** No window or merge rule
  bounds this, and the design does not rely on one: a copy signed on an old parent is quarantined
  at first, but H can adopt it with an adopting merge of its own, which only needs to name the copy's branch
  and need not include the genuine enrollment. So any approver H, with or without a dishonest
  relay, can disable any device that is **not senior to it**, in one of two ways:
  * **conflict** it, when neither add may displace the other (above): the id is inactive;
  * **displace** it, when H is senior to the device and the device's own approver is not senior to
    H's copy (the copy's parent predates that approver's add): the genuine add is then subordinate
    and void, the id stays active **under H's descriptor** (H's `sign` key and presence), and
    everything the genuine add's device signed loses its signer, so its subtree is void. This is
    no more than H could do by removing a device junior to it and adding one of its own, but
    unlike a removal it would otherwise pass silently. So a subordination is **announced like a
    conflict**, naming both approvers, as "*H* replaced the enrollment of *X* that *A* approved".
    The device itself shows "Replaced by *H*" and keeps its keys and roster: it was not removed
    (§6.5 acts only on a `remove` entry), and removing H restores it, unless that removal kept
    the copy (decision 9's kept-copy residual; below).

  What does bound both is structural:
  * H can never copy a device **senior** to it. H's copy must be signed in a state where H is
    active, so its parents descend from H's own add, whose ancestors include every senior device's
    add; and an id is never added twice in one history (above);
  * every copy names H, and is announced;
  * a removal of H takes the copies away. A copy is an add H signed, so it is in H's approval
    subtree whatever id it carries: a removal of H revokes every copy that is an ancestor of it,
    and every copy that is not is H's grant concurrent with the removal and void (§3.5). The
    remover need not hold, find or name any of them, and a copy is never kept where the original
    is in the removal's parents' history (`remove`, below). Conflict sets are formed from adds that are neither void nor revoked (§3.5, the
    order of computation), so once H is removed the genuine adds outside H's subtree are neither
    subordinate nor conflicted: those devices are active again under their own descriptors, with
    their subtrees, and with no new enrollment, even when the honest line was built on top of a
    copy. Some devices are taken with H instead, and are enrolled again with a new key: one H's
    subtree enrolled and then copied, since neither add of its id can be kept and the genuine one
    is in the subtree too; one a copied device enrolled while the copy was its active add, whose
    signer is the copy; and every device either of those enrolled, through the genuine add or the
    copy, since a kept add may have no copy between it and H in its lineage. That is work lost,
    never trust gained. And a removal of H signed on a branch that cannot see a device's genuine
    add can keep H's copy of it, which that history shows as the device's only add; once the
    branches join, the kept copy may not displace the genuine add (§3.3, "A kept add does not
    displace"), the two conflict, and the device is inactive until enrolled again with a new key.

  So the damage is a denial of service, attributed and reversible, never a gain of trust. It
  needs a reset only when no device senior to H is left and the conflicted devices include the two
  cosigners a removal of H would need. Decision 9 states it. Otherwise removing H restores every
  device its copies disabled, apart from those above and a copy such a removal kept. An honest enrollment makes one add, so without a hostile
  approver a conflict needs a dishonest relay or a device enrolled twice while partitioned. A void
  add (§3.5), such as
  one a removed device fabricates on an old head from a key it learned, never conflicts with
  anything;
* `remove`: its `target` names a device id and **the entry hash of the `add` it revokes**, which
  must be active in the parents' state. The seniority rule of §3.4 must hold for the target: it is
  `by` itself (a **self-removal**, which has no `cosigner` and an empty `keep`), or junior to `by`,
  or the removal has a `cosigner`. A removal with a `cosigner` is valid only if the two signers
  pass the cosign test of §3.4 for the target, even when the target is junior to `by`, so a
  removal by two devices always ranks 2 and one by a single device always has a signer senior to
  its target or is a self-removal (§3.5 relies on this). A removal takes the target's **approval
  subtree** with it, except what it keeps, so one removal is the whole of a recovery and nothing
  about it depends on the remover finding or naming the target's devices (§3.4, the cascade):
  * The approval subtree of an add `t` is every add whose **signer** (the add that made `by`
    active in that add's own parents' state, above) is `t` or, transitively, an add of the
    subtree, **whatever its id and whatever its state**: active, held, subordinate, conflicted,
    void or quarantined. Each add's signer is fixed by its own ancestors, so every verifier
    computes the same subtree from the entries alone. The subtree is junior to `t`, and authority
    to remove `t` is authority over it.
  * `keep` names adds of the subtree that stay. Each must be an ancestor of the removal, and
    signed by `t` or by another kept add, so a kept device's approver is kept too. A kept add
    must not be a **copy** (an add of an id that has another add in the removal's parents'
    history), and no add between it and `t` in its approval lineage may be one: otherwise unticking a device that a
    displaced device enrolled would keep, through its approvers, the copy that displaced it and
    the thief's device that made the copy. A keep set is exactly what the removal's signers
    signed, for validity, revocation and precedence alike; nothing later changes it. A copy made on
    a branch the removal's parents cannot see is dealt with where ids conflict instead (§3.3,
    "A kept add does not displace"). The previous revision un-kept such an add when another add of
    its id appeared, and that let a thief revoke an honest kept signer, and so revive its own
    grants, by supplying a copy of the signer's key.
  * The removal **revokes** the target's add and every add of the subtree that is an ancestor of
    the removal and is not kept. The subtree's adds that are not its ancestors (concurrent with it,
    on a quarantined branch, or newer than its parent) are not named and need not be: they are
    grants of devices the removal revokes, made where the remover could not see them, and void
    under §3.5 unless a kept device is in their approval lineage. So the removal is valid whichever of the target's
    adds the remover holds. The previous revision had the remover list each device instead, and
    that failed twice: a device on a branch every verifier quarantined could not be listed, which
    made the prescribed recovery invalid; and an id with two adds (a thief's own add of a key and
    a device of its own adding the same key, one subordinate to the other) could have only one
    listed, after which the other became active under the thief's keys.
  * A self-removal revokes only its signer: the devices it approved are not part of it.
  * A removal other than a self-removal is **invalid if any signer is its target, or is in the
    target's subtree and not kept**: otherwise a stolen D could cosign its own removal with J, a device it added (D's
    lineage cut at D is empty, so the pair would pass the lineage test).

  Under the conditions of §3.5 a removal also bars the target's key from coming back through
  another add. A removal of a fabricated add never touches the device a real add made, even with
  the same id. Whether removals leave a person's device is a property of the state, not of one
  entry (§3.5). A removal **ranks 2** when it is cosigned, and so passes the cosign test for its
  target, and **1** otherwise, which covers every self-removal and every removal by one device. A
  removal counts as one against its signers' budgets however much it revokes.
  There is no flag that voids a target's past grants: its subtree goes except what is kept, and
  its grants that are not ancestors of the removal are void anyway (§3.5);
* `rename`: like a removal, its `target` names the device id and the add it applies to, which must
  be active in the parents' state and be `by` itself or junior to it. The 8-rename quota belongs to
  that add. A device's name is set only by renames naming its **active** add, so a rename of a
  fabricated copy of a device (an add of its `dh` that turns out subordinate or void) neither
  changes the real device's name nor spends its quota. Among those, the name comes from the
  **latest** non-void renames: those with no non-void rename of the same add among their
  descendants. If concurrent branches
  leave more than one, the one with the smallest entry hash wins, in every verifier and delivery
  order, and the Devices page marks the name as changed concurrently. Without a rename, the name
  is the one in its add;
* `confirm`: the target's add is active in the parents' state, `by` is confirmed there and is the
  target's **approver** (the signer of its add), and the target is not already confirmed there. It needs presence like every entry, and an honest client offers it only
  after the target's 72 hours by its own clock (§3.4);
* `merge`: its parents are two to four distinct heads, quarantined ones included, in head order.
  Only a merge with `adopt: true` adopts anything; one without it only joins heads, and its
  descendants are judged as any entry over those parents would be.
  **A merge is never void and never held**, by any rule. Its only effect is adoption (§3.5), and
  adoption is irrevocable: once any valid merge has adopted a branch, every verifier that holds
  that merge treats the branch as adopted, whatever later happens to the merge's signer. Adoption
  only lets the branch's entries be judged by the ordinary rules, so it can never bring back an
  entry that voiding, a bar or a removal takes away, and a hostile signer's merge adopts nothing
  those rules would refuse. Holding a merge for probation would make adoption depend on each
  verifier's clock and on whether a removal arrived first, which is the delivery-order dependence
  irrevocability exists to avoid;
* **Capacity.** Counted over every entry a verifier holds, on every branch, void or not, so that
  storage itself is bounded:
  * per generation, at most **512 adds** and **4,096 entries other than removals**, and no `add`
    in a parents' state that already has **64 active devices**. The 64 is a validity rule on adds,
    not a bound on the state: voiding a copy restores the genuine enrollments it disabled (§3.3),
    and a restoration is never refused, so the state can briefly hold more than 64, as when H
    disables ten of 64 devices, ten replacements are approved, and removing H restores the ten
    (73 active once H is gone). Every add is then refused until removals bring the count below 64.
    Storage stays bounded by the 512 adds;
  * per signer (the add that made `by` active, above), at most **64 adds, 64 confirms, 64 merges
    and 64 removals** per generation, a cascade counting as one removal (§3.4); and each add may be
    renamed at most 8 times.

  An entry that would exceed a cap or its signer's budget is refused and reported. **Removals are
  never refused for the generation's caps**, only for their own signer's budget. So no other
  device, however many entries it signs, can stop a device from revoking: otherwise a hostile
  approver could fill the roster (with renames, say) and block its own removal. The worst case is
  then fixed: 4,096 entries plus 64 removals for each of at most 512 devices, about 37,000
  entries, plus at most 1,024 evidence entries (below).
  Reaching it does **not** need any probation to pass: a device whose grants are held can still
  sign at once, and its entries are stored and count. So one hostile approver can fill the caps
  within minutes, every add announced (§6.4), and an honest account stays in the hundreds. What
  the bound promises is storage, not that filling it is slow. Nothing accepted is ever dropped to
  stay inside it.
  * Honest devices do not exceed a budget quickly: an honest device signs each entry on a head that
    holds its own previous one, so its entries form a line, and 64 adds or removals is the whole
    active-device limit. Exceeding a budget fast takes a key that signs on many branches, which
    only a compromised key does. Which of that key's entries a verifier holds can then depend on
    delivery order. That is accepted: it changes only how much of a hostile key's work is applied,
    and every one of its entries is announced. One consequence is stated, not prevented: a hostile
    device can fill the budget of a device J it added, at one host, with entries the relay never
    sees, before the relay's line reaches that host. The host then refuses J's entries on that line
    as over budget, and with them every later entry descending from them, so it cannot hold the
    heads other devices attest and goes **stale** (§6.3), refusing relay devices, until
    `devices sync` at the machine imports the line without these limits.
  * A generation that reaches a cap or a budget honestly, after years of churn, continues with a
    reset (§5.5) until compaction by a signed checkpoint, which is left for a later slice.
  * **Evidence for a removal of an over-budget signer.** A budget limits what a signer's own
    entries do; it must not let that signer, or the devices it added, block its revocation.
    Otherwise one host takes a hostile approver's 64 sibling adds, the remover signs on a 65th,
    and the host could not accept the removal's parent. Or T, on probation, adds J; J signs 64
    adds on each of several branches; T gossips one set to host X and another to host Y; and a
    removal of T signed on a head synced from Y would need, at X, J's entries beyond J's budget. So
    a removal R is accepted with ancestors over a cap or budget only when all of these hold:
    * R is signed by a device other than its target T (a self-removal brings no evidence);
    * every such ancestor is signed by T or by a device in T's **subtree** (one whose add was
      signed by T or, transitively, by a device in T's subtree) that R does not keep, so R takes
      away exactly what its evidence comes from;
    * R brings at most 256 such entries from each of those signers, no signer has had more than
      256 evidence entries admitted at this verifier, across all removals, and the verifier holds
      at most **1,024 evidence entries in all**. The last bound matters because the entries of a
      subtree can be spread over many signers with only a couple of entries each.

    Evidence is stored and gossiped, used only to check R's validity, and never applied itself, as
    quarantined entries are not. A removal that needs more, or needs over-budget entries of a signer
    outside T and its subtree, is **refused** at that verifier and reported, never a reason to fail
    closed. A removal refused this way shows as unconfirmed at that host (§6.3), and the import at
    the machine below admits it.
  * **Where the remover's client signs.** The limits above are not what makes a removal reach every
    host, because the target chooses how much evidence a removal signed on an arbitrary head needs.
    A single mistakenly approved T, with an honest relay, can add J1 to J6, have each sign about 192
    entries within its budget, and gossip a different set to each host and another to the relay. A
    removal signed on the relay's head would then need more than 1,024 evidence entries at every
    host. The target must not choose what the removal keeps, either: a stolen S that signs adds on
    a branch every verifier quarantines, with renames to push its `seq` up, must not have them
    become ancestors of its own removal, where its renames and confirms would stand and the merge
    that joins the removal would adopt the branch. So the client signs every `remove` on a parent
    chosen like this:
    * **Only unquarantined entries.** Candidates are entries that the remover's own client does not
      quarantine (and so, by inheritance, none of their ancestors is quarantined there). Of each
      host's last attested heads (§6.3) and of the relay's head, only that part counts: a
      quarantined head, and anything only it leads to, is never a candidate. A host's set of
      entries only grows, so a candidate under all of those is held by every attested host, with
      all its ancestors;
    * **What it must hold.** Among itself and its ancestors: the target's add, the signers' adds
      and, for a cosigned removal, their `confirm`s, and the adds the person unticked to keep in
      the cascade (`keep` names only ancestors of the removal, §3.3);
    * **As little of the target's as possible.** Among those, the client takes the **smallest**
      candidate (fewest ancestors, then lowest entry hash) whose ancestors hold no grant signed by
      the target or by a device in its subtree (§3.3), other than kept adds and what they need,
      and never a copy of a key. Everything else the target signed is then concurrent with the
      removal and void (§3.5). It is held by every attested host, so none needs evidence.
    The cascade shows the target's approval subtree as the remover's copy holds it, quarantined and
    held adds included, each ticked for removal unless the person unticks it. Only the unticked
    ones are named (`keep`): the removal revokes every other add of the subtree under its parent,
    and the rest, quarantined ones included, are not under the parent at all and are void by
    position (§3.5). So nothing the remover holds, or fails to hold, can make the removal invalid.
    If every candidate holding the required entries has some of the target's grants as ancestors,
    because the honest line was built on them (they were then unquarantined and announced when
    made, held or not), the client takes the smallest candidate and says which grants it cannot
    void by position. The removal revokes the devices those grants added, unless the person keeps
    them. If
    the required entries are not all held by every attested host (a signer confirmed minutes ago,
    say), the client signs on the unquarantined entry that holds them with the fewest ancestors outside
    the common set, and says which hosts lack it. Those hosts receive the few missing ancestors with
    the removal. Evidence (above) is then needed only at a host this device has no attestation
    from, such as one offline since before the target was added, or for entries the target got onto
    the relay's line ahead of a just-made confirm. Such a host may refuse the removal for evidence;
    it shows as not having confirmed it (§6.3), and the person imports the roster at that machine
    (`devices sync`), a local action that admits the evidence without these limits. That is the
    remaining case, stated in §2 and decision 9.

### 3.4 Seniority, probation and confirmation

A new device that turns out to be hostile must not be able to undo the devices that were
there before it. So:

* **Seniority** follows causality, never `seq`, and is a relation between **add entries**, since
  an add, not a bare id, is a signer (§3.3). A device X is **senior** to Y, and Y **junior** to X,
  when the add that made X active is an ancestor of the add that made Y active; the genesis entry
  is senior to every other. Two devices whose adds are on concurrent branches are neither. `seq`
  alone would not do: a relay that hides newer heads from an established approver can have it
  enroll N at `seq` 11 after H was enrolled at 12 on another branch, and N would then outrank a
  device approved before it. A device may `remove` **itself or any device junior to it**.
  Removing any other device, senior or concurrent, needs a `cosig` from a second device, and is
  valid only if **both signers are confirmed** (below) in the removal's parents' state. A device
  senior to the target may cosign too, to make the removal rank 2 (§3.5); every cosigned removal,
  whatever its target, must pass the test below. Both signers need their own presence proofs.
  Their approvals must also be **independent**. A signer's **lineage** is the
  chain of add entries from its own add up to genesis: its add, the add of the device that signed
  it, the add of the device that signed that one, and so on. Each signer's lineage is **cut** to
  leave out the target's add and every add senior to it, and the two cut lineages must not share
  any add. So neither signer's add may be in the other's lineage (neither added the other, directly
  or through others), and no add left in both may have led to both. Two devices the target
  approved itself are independent: their lineages meet only at the target's add, which the cut
  removes. A hostile device H therefore cannot pair with a device it enrolled, nor pair two devices
  it enrolled (siblings) or their descendants (cousins). A fabricated copy of a device's key is
  another add, with its own lineage. Both signers confirmed and their lineages, cut at the target,
  disjoint: that is the **cosign test** for a target. A removal has one target; a cosigned one is
  valid only if the test holds for it, and then ranks 2 (§3.3); the subtree it takes is junior to
  the target, so the test is not repeated for it. For a target junior to `by` the cut removes `by`'s whole lineage,
  so a senior device can rank 2 with any confirmed device, even one it approved. That gives a
  stolen senior and its own device nothing against a recovery that removes the senior device: the
  approval-ancestry precedence of §3.5 decides between equal ranks.
* **Confirmation** is the clock-free gate on that power. A device is **confirmed** in a state
  when the state holds a `confirm` entry naming its add, signed by its **approver**, the device
  that signed its add, itself confirmed. Being senior to the device is not enough: otherwise a
  confirmed hostile H could confirm, at once, a device B that G had just approved on its own, and
  H and B, whose lineages below G are disjoint, could cosign G's removal after only one
  confirmation mistake. A device H can confirm only devices H approved, whose lineages all contain
  H's add, so none of them can pair with H. A device whose approver is gone before confirming it
  stays unconfirmed: it can do everything else, and is enrolled again from another device if it
  must be able to cosign.
  The genesis device is confirmed from genesis. The signer's client offers "Confirm *Kitchen
  laptop*" only once 72 hours of its own monotonic running time have passed since it accepted the
  target's add, and the person confirms with the presence check. An unconfirmed device can still
  drive sessions, remove itself and remove devices junior to it, but cannot cosign or be one of the
  two signers of a removal of a device that is not junior to it, whatever any clock says. Without
  this, two newly approved hostile devices A and B could cosign removals of G and of every other
  device not junior to them within seconds of their approval, and nothing would be left to remove
  them. With it, they first need their approver to confirm each of them three days later, which
  is the person's second chance to notice.
* **Recovering a lost senior device** therefore needs two surviving **confirmed** approvers whose
  lineages, cut at the lost device, are disjoint. The usual case is two devices that the lost device
  approved and later confirmed. A person who has, say, only the genesis device and one other cannot
  remove the genesis device when they lose it, and nor can one whose devices were not yet confirmed
  when the senior device went: those are resets (§5.5). So is a person whose surviving devices were
  all approved through one device junior to the lost one. That holds for a device that is lost,
  or stolen while locked. A device **stolen with its presence check passable** is different: its
  thief can force the tie of §3.5, voiding every device it approved after the point it forks from,
  and everything those devices enroll, the recovering cosigners and their fresh devices included;
  aimed at a device added after it, the tie voids that device's subtree too. **Recovery is then
  guaranteed only through approvers that are not junior to it** (added before it, or
  concurrently), and for the genesis device, to which every device is junior, a reset can be
  forced. Short of the tie, the thief can revoke, once, every device that was active and junior to
  it on the head it holds, and what the cosigners did after removing it survives; what it or the
  devices it made add does not survive the person's removal of it, except what the person kept,
  unless a device outside its subtree helps revoke one of the removal's signers (§3.5, "What this
  guarantees"). So the person
  enrolls a fresh device straight after the removal, from an approver not junior to the stolen
  device where there is one (§5.4). Decision 5 says this.
* **What that leaves.** One hostile device cannot remove anything above it, alone or with any
  devices it enrolled, however long it waits. Two devices whose approvals came independently from
  the person's own devices can, once their approvers have confirmed both, which takes two approval
  mistakes and two confirmation mistakes, each announced on every device (§6.4). Before that:
  * removing them stops a **takeover**: their grants are held and then void (concurrent with the
    removal) or revoked (in the subtree it takes), so they never add anyone. Unconfirmed, they cannot
    cosign, so they cannot remove anything that is not junior to them;
  * after both are confirmed, unnoticed, they can remove the devices above them and keep going.
    With a relay that partitions the account, the grants they make after removing a senior device
    also survive a removal of them signed by that device (§3.5, "The other side of that rule").
  Decision 9 states both.
* **Probation.** A device added in the last **72 hours** cannot add, rename others, confirm or
  merge: an honest client does not let it sign those, and an honest relay refuses to append them
  by its own clock. An honest approver does not confirm it before then either, but a hostile
  approver can sign a `confirm` at once, which ends the hold below as early as that approver could
  have made the grants itself. A verifier cannot rely on any of that, and treats what such a
  device signs in three ways:
  * its **grants** (adds and renames) are **held**: they and everything signed by devices they
    added take effect only when the probation ends by the verifier's clock, or earlier when the
    verifier holds a `confirm` of it. Its merges are not held, since a merge only adopts (§3.3);
  * its **removals of devices junior to it are never held**. Removals never un-apply (§3.5), so
    holding one would only delay a revocation that is going to happen anyway, and would keep the
    target served meanwhile. Alone it can only reach devices added by entries that descend from its
    own add, and every removal is announced;
  * it cannot take part in a cosigned removal at all until confirmed: that is a validity rule,
    above, not a hold. So a cosigned removal that is valid is never held either. A host back after
    six weeks applies at once the cosigned removal of a stolen genesis device by two confirmed
    devices it has never seen before;
  * the time is not the wall clock: each verifier counts **elapsed monotonic time while it runs**,
    persisted across restarts, from when *it* first accepted the `add`; `at` is never used, and a
    wall-clock jump in either direction counts for nothing. Time a machine spends off does not
    count, so a hold can last longer than 72 hours, never shorter.
  * Entries from other signers apply as usual. A removal of the probationary device itself applies
    at once. Its held grants are then void in two cases, both decided by signed entries rather than
    by a clock:
    * a grant that is not an ancestor of the removal (§3.5), because the device acted
      concurrently with being removed;
    * an add of the target's subtree that is an ancestor of the removal and not kept: the removal
      takes it (§3.3), held or not. This covers the case where the remover had already synced the
      held add before removing: the add is then an ancestor of the removal, but it is revoked all
      the same. The devices the person kept are named in `keep`, and take effect only when the
      target's hold would have ended; the confirmation says so. A later
      self-removal by the target cannot void them (§3.5, "A self-removal cannot undo what another
      removal kept").
  * Anything the voided entries added is void too, transitively. What removal during probation
    guarantees is therefore this: every add by that device, held or not, never takes effect,
    except the ones the person kept. The one exception is precedence (§3.5): if the remover was
    itself revoked by a removal X that shields against the remover's removal, the probationary
    device's grants that descend from X are not voided by it.
  * Holds only delay effects, never change which entries exist or which are void. Every verifier
    therefore reaches the same state once its holds expire.
* **Where a verifier's clock starts.** A verifier that learns of a device late, such as a host
  coming back after weeks offline, starts that device's 72 hours when it first accepts its add,
  so it holds that device's grants for up to 72 hours unless it holds a `confirm` of it. This is an
  **activation delay**: a device added by a device the late verifier sees as new and unconfirmed is
  not active there until the hold ends. It affects only approvals made by unconfirmed devices,
  never removals and never a device's own sessions. Besides a `confirm`, two sources may shorten it,
  both authenticated by the person rather than by the relay:
  * **Enrollment.** With the roster, the approver sends N how much of each device's probation
    remains by its own clock (§4.2 step 7), and N adopts that.
  * **The local app.** A host adopts the earlier first-acceptance times that its own app reports
    over the pipe.
* **Cascade.** Removing a device shows the devices in its approval subtree (the devices it added,
  the devices they added, and so on), each ticked by default. The removal is **one** entry naming
  the device and the devices the person unticked (`keep`, §3.3); it takes every other device of
  the subtree under its parent, and the ones it does not see are void (§3.5), so it never depends
  on the remover's copy being complete. It costs one removal of the remover's budget, however
  many devices it takes, and no fork can hold part of it without the rest. It is never split
  into one entry per device: separate removals of a stolen device's subtree are what let a fork
  that leaves some of them out keep its devices active (§3.5). Unticking a device unticks its
  approvers up to the target, since a kept device's approver must be kept; ticking one ticks the
  devices below it. When the removed device approved any devices, the client asks for a
  cosignature from a second confirmed device whose approval is independent of the remover's, even
  when the remover alone could remove it: a removal that ranks 2 is what keeps the remover's later
  work safe from a pair the removed device made (§3.5). The cosigner picker lists and recommends
  devices outside the target's subtree first, and devices in it last, with a warning. A signer in
  the subtree must be kept, and is never kept silently: it appears in the keep list, with its
  approver and when this client first saw it, and both signing clients apply §6.1's checks to it before the
  person confirms. Devices the person unticks stay active, and what they enroll later is theirs: it is
  in the target's subtree but not an ancestor of the removal, and is void only if they are.
* **Announced.** Every add, remove and rename is shown on every person's device with who did it
  (§6.4).

What this leaves: the most senior device can remove everyone junior to it. A stolen genesis
device whose thief can also pass its presence check (the person's Face ID, passcode or Hello
PIN) therefore controls the generation, and the person's answer is a reset (§5.5). That is
the same exposure as any account whose oldest device is stolen and unlocked.

### 3.5 What a verifier accepts, and the state it computes

Each verifier (host, person's device, relay) keeps a set `S` of accepted entries, closed under
parents and rooted at the generation's genesis, and its **heads**: the entries in `S` with no
child in `S`. Nothing is ever removed from `S`, so "rollback" cannot happen: an offer that
lacks entries the verifier has is simply no news. A verifier accepts an offered entry `e` only if:

1. `e` came from the relay, from an active rostered device on a channel (gossip, §3.6), from the
   approver during this verifier's own enrollment (§4.2), or, on a host, through the write-only
   `roster.submit` of §7.1 from a device the host does not know yet;
2. every parent of `e` is already in `S`, and `e` is valid in their state (§3.3);
3. if a parent of `e` already has a child in `S`, `e` starts a **fork**, and if it extends a head
   it may still be far behind. What happens then depends on the entry:
   * a `remove` is accepted however far back it forks, together with the ancestors it needs. The
     only exception is the evidence rule of §3.3, for ancestors over a budget. A removal only ever
     takes trust away, so taking it in can never be the attack, and refusing it would let delivery
     order decide whether a device stays revoked. Its ancestors are
     **not** accepted for their own effect: any non-removal ancestor that would itself be
     quarantined (below) is quarantined, so delivering a removal cannot smuggle in the `add(X)`
     beneath it. The removal is always valid or invalid by its history state (§3.3). It **applies**
     at once if it would also be valid in the state of its parents computed from unquarantined
     entries alone. If it is valid only through a quarantined entry (its signer, cosigner or target
     was added by one), it is kept, and applies when a merge or a local adoption adopts that branch. Because merges are never void and adoption is irrevocable (§3.3),
     once it applies nothing un-applies it;
   * a `merge` signed with presence is always accepted. With `adopt: true` it is how a person
     adopts a branch, through the reviewed "Tidy" below; without it, it only joins heads and
     adopts nothing, at any verifier, and that is the only kind an honest client signs on its own. A
     merge whose signer was itself added by a quarantined entry adopts nothing until a merge by a
     signer outside the quarantine adopts its branch, so quarantine cannot adopt itself. A merge can
     adopt a copy of a device's key; what bounds that is §3.3's rule on copies, not the merge;
   * anything else is accepted only if its parent is **within the window** and `S` would then have
     at most 4 heads. An entry outside it is
     **quarantined**: it is kept as evidence and reported as relay misbehavior, and its effect is
     not applied, but it is gossiped. It is how a removed device would bring back an add it signed
     on an old head. Quarantined entries count toward the caps and budgets of §3.3 like any
     others, which bounds them, and nothing is dropped.

   **The window.** A parent `P` is within it when at most **8** unquarantined entries of `S` are
   neither `P` nor its ancestors: the entries the signer had not seen. The test applies to every
   entry other than a removal or a merge, whether or not it forks, so extending an open head no one
   merged is judged the same way as forking from it. It is anchored on what the verifier already
   applies, and only grows: a new entry, quarantined or a far-forking removal, never shrinks
   another parent's count, so no entry can refresh the window for an old parent. Before it
   appends, an honest client joins every head that it does not quarantine, with a merge without
   `adopt`, so its entries are within it. It never names a quarantined head in such a merge: that
   is adopted only through the reviewed Tidy, with `adopt: true` (below). The window is
   defense in depth against a relay that replays old branches; no security property in this note
   depends on it alone (§3.3, copies).
4. **Quarantine is inherited.** An entry with a quarantined ancestor is quarantined too, whether
   or not it starts a fork, until a merge adopts that branch. Otherwise the first child of a
   quarantined entry would bypass rule 3: an established signer's quarantined rename followed by
   an add would take effect without a merge, because its signing key is already active. Inherited
   quarantine, like quarantine, affects only application: the entries stay valid by their history
   states. The one exception is the removal rule of rule 3: a removal on a quarantined branch
   applies if it would be valid from unquarantined entries alone, and is kept until adoption
   otherwise.

Two verifiers that received a far fork in different orders can therefore differ only in which
quarantined entries they apply: adds, and removals that depend on them. The verifier that took
them first applies them, and the other
quarantines them. That lasts until a person signs an adopting `merge` (`adopt: true`), which every verifier accepts and
which makes them converge. Every device shows "the device list forked" until then.

**Forks that removals make.** Apart from removals, an honest relay and honest devices never fork:
approvers extend the single head (§3.6). A removal is different by design: its client signs it on
a parent every host already holds (§3.3), which may already have children, so it forks. Such a fork
is expected, is never quarantined (rule 3) and is not reported as misbehavior. It needs no merge to
take effect, since every verifier applies the state of all its heads. The remover's client joins it
straight away: in the same confirmation it signs a `merge` **without** `adopt` of the removal and
the other heads it does not quarantine, with its own presence proof (a second prompt where the
platform needs one per signature). It never names a quarantined head, and it does not sign the
join if the removal's own ancestry is quarantined in its copy, which the choice of parent (§3.3)
rules out. So the join adopts nothing anywhere, even where a verifier quarantines something under
it. Two removers leave the join to the next person's device that appends, which joins every head
it does not quarantine first anyway: one still on probation, whose client does not offer merges,
and a device removing itself, which is not active in any state holding its removal and so could
not sign a merge naming it. "Remove this device" therefore signs only the removal.
While the only branches besides the main line are removals, devices show "a removal is being
joined", not "the device list forked".

A verifier that quarantines is the stricter of the two about
grants: it never makes a device active that the other has not, apart from a device that a copy
the other applied has conflicted there (§3.3). It also waits to apply a removal
signed by a device only that branch added, so the device such a removal targets stays active there
until the merge. That device was approved without the quarantined branch, so waiting gives nobody
trust they were not already given.

**The state** of a set of heads is computed deterministically from the accepted, unquarantined
entries:

* Every `add` that is not void, and not conflicted (§3.3), makes a device active.
* **Removals are monotone.** Every valid `remove` revokes the adds it takes (§3.3), in any order,
  and nothing ever un-applies one. Valid means valid in the state of its own parents (§3.3), which
  a later or concurrent entry cannot change. Revision 2's first draft settled conflicting removals
  by voiding some of them. A host that had already applied a removal could then make the revoked
  key active again when a concurrent entry arrived: an authorization rollback. So no removal is
  ever voided.
  Concurrent removals simply all apply. In the cycle where A (senior to B, B to C, C to D, none
  of them approved by another of them) removes B
  on one branch, B removes C on another, and C and D (cosigned) remove A on a third, A, B and C all
  go and D stays. Each removal is the same entry whatever order a verifier sees them in, so every
  verifier reaches the same set.
* **When no person's device is left.** If the removals leave no active person's device (the last
  two removing themselves or each other concurrently, say), the generation has no approver. Every
  host then refuses all relay devices, and only a reset (§5.5) continues. This needs concurrent
  removals that an honest relay serializes, so it happens only with a dishonest relay or two
  devices acting while partitioned. Keeping a device the person removed would be worse.
* **Revocation.** An add is **revoked** by every applying removal that **takes** it: the removal's
  target, and every add of the target's approval subtree that is an ancestor of the removal and
  not kept (§3.3). It is **barred** by every removal whose bar reaches it (below). Revocation and
  bars are never undone (§3.5, "Removals are monotone"), and a revoked add never makes its id
  active, whatever its state was when it was taken.
* **Voiding** applies to **grants** only (adds, renames and confirms), never to removals or
  merges. A grant signed through an add that is revoked or barred is void unless it is an
  ancestor of **every** applying removal that revokes or bars that add: the device acted
  concurrently with losing it. So is anything a void entry added, and every grant signed by those
  devices, transitively. A barred enrollment is no different from a revoked one here: a removed key
  that comes back through a second enrollment, and is barred, cannot grant anyone access through
  it either. A void `confirm` confirms nothing; a removal that was valid when signed stays valid,
  since validity is decided in its own parents' state. A removed device's concurrent additions
  are therefore void unless precedence (below) shields them, which it does not do against the
  person's recovery ("What this guarantees", below), while its concurrent removals, valid where they were
  signed, stand, limited by the two rules that follow.
* **A self-removal cannot undo what another removal kept.** A self-removal of D voids none of D's
  grants that are ancestors of another applying removal of D that D did not sign. Otherwise a
  stolen probationary B, removed by the person with C kept, could remove itself on a head from
  before C's add and void C and all it enrolls; and a stolen genesis G could remove itself on the
  entry right after genesis and void C1 and C2, which are removing it. Only self-removals get this
  limit: a removal of D by another device is the person's, or is subject to the precedence below.
* **Precedence between removals that revoke each other's signers.** Let R be an applying removal
  one of whose signers is **revoked by** an applying removal M, with R not an ancestor of M. M
  revokes a device if M takes the device's add (above), or if that add is in M's **raw chain**:
  what M would void, directly or transitively, if no removal were shielded. M **shields** what
  descends from it against R, so that no entry with M among its ancestors is void because of R,
  directly or through any chain of voiding that starts at R, when:
  1. M ranks higher than R (§3.3); or
  2. they rank the same and R **yields** to M: some signer of R is in the approval subtree of M's
     target (§3.3), and no signer of M is in the approval subtree of R's target; or
  3. they rank the same, no signer of either is in the approval subtree of the other's target,
     and no signer of M is revoked by R. A removal signed by devices that the other removal's
     target approved, directly or through others, therefore never shields against it at equal
     rank, even when those devices are kept and so not revoked. Without that, devices the person
     kept from a stolen S's subtree, removing a device P on an older branch, shielded what S
     added on top of their removal against the person's removal of S, whenever C1, a signer of
     that recovery, was approved through P.

  R still revokes what it takes. Every input here (targets, keep lists, approval subtrees, ranks
  and raw chains) is read from the entries alone, never from another shield, so the rule has no
  cycles and every verifier computes the same result for the same applied entries.

  **What a removal can reach.** A removal X of `t` takes `t` and adds of `t`'s subtree, which are
  junior to `t`; bars adds of `t`'s id made where its signers were active (below); and voids grants
  signed through those, which are junior to them. So everything X revokes, bars or voids, its raw
  chain included, is an add of `t`'s id or junior to one. A device that is neither is out of X's
  reach, however X ranks. And `t` itself is bounded by who signs X: one signer can remove only
  itself or a device junior to it, and for any device D, two signers that are both in D's
  subtree, or are D and a device in D's subtree, pass the cosign test only at D or at a device
  junior to D, since cut at anything else both lineages still hold D (§3.4). A removal that is an
  ancestor of another entry voids nothing that descends from that entry: an entry signed after it
  was signed by a device active after it.

  **What this guarantees.** Let the person remove S with M. M's signers are outside S's subtree or
  kept by M (§3.3). Call **S's side** S's add and every add of S's subtree with no kept add in its
  approval lineage: what S and the devices it made added, as opposed to what the person kept and
  what kept devices add. Assume that no applying removal with a signer outside S's subtree revokes
  a signer of M: that would take the person's own devices outside S's subtree, or a compromised
  device there, acting against M's signers. Removals by S, by devices on S's side and by kept
  devices are all allowed. The keep list is what M's signers signed; a thief that gets its own
  device onto it, by having the person cosign a removal it built or by being picked as the
  person's cosigner, is kept by definition, which is why both signing clients show the whole
  removal and refuse unconfirmed keeps (§6.1).
  * **Nothing on S's side survives M.** In every state that holds M, no add on S's side is active
    and no grant signed through one applies. The adds on S's side that are ancestors of M are taken
    by M, and revocation is unconditional. Suppose some other add on S's side is not void in a
    state holding M, and let e be one with no such add among its ancestors. e is not an ancestor
    of M. If e descended from M, its signer would be active in a state holding M, but its signer
    is S, which M revokes, or an add on S's side among e's ancestors, which the choice of e
    excludes. So e is concurrent with M, its signer is revoked by M or void because of M, and e is
    void because of M unless a removal X among its ancestors shields it against M. X revokes a
    signer of M, so by the assumption every signer of X is S or in S's subtree, kept or not. Rule
    3 fails for X: a signer of X is in the subtree of M's target S, or X is signed by S alone,
    which M revokes. Rule 2 fails: for M to yield to X, no signer of X may be in S's subtree, so X
    is signed by S alone and ranks 1; at equal rank M ranks 1 too, and its single signer, senior
    to S, is in the subtree of no device S can remove. Rule 1
    fails: if M ranks 2, nothing outranks it; if M ranks 1, its single signer is senior to S, so
    neither an add of X's target's id nor junior to one, and out of X's reach (above). So
    no X shields e, and e is void. Nothing depends on which of S's adds the remover held or
    where any fork starts: a fork from M has S's side revoked, and a fork from before M gives way
    to it.
  * **Everything that descends from M survives S's side, if M ranks 2 and no signer of M is in the
    approval subtree of S or of a device junior to S.** A removal X signed only on S's side reaches
    only S's id and devices junior to S (above). Its signers are not active in any state that
    holds M (the previous point), so X does not descend from M; if X is an ancestor of M it voids
    nothing that descends from M (above); otherwise its signers are revoked by M, taken by it or
    in its raw chain. Its target is S or junior to S, so by the second condition no signer of M is in its
    target's subtree, and some signer of X is in S's subtree unless X is signed by S alone, which
    ranks 1. So X ranks lower than M or yields to it, and M shields what descends from it against
    X, whoever X revokes. If M ranks 1, the first point still holds and M's single signer is out
    of reach, but a rank-2 pair on S's side can revoke a device junior to S that did not sign M,
    and void what that device did after M, since a rank-1 removal cannot shield against it. The
    client therefore asks for a cosignature on every recovery of a device that approved others
    (§3.4).
  * **Recovery from a stolen senior device.** When the person's devices remove a stolen senior S
    with M (rank 2) while S, on an old head, removes them (rank 1, or rank 2 with a device of its
    own), M ranks higher or, at equal rank, S's removal yields to M unless its target is above one
    of M's signers: what M's signers do after M survives. A stolen genesis G's self-removal cannot
    undo C1 and C2 (the self-removal rule), and G's removal of P, which approved C1, ranks 1
    against their rank 2, so what C1 enrolls after removing G stands even though C1's own add is
    revoked.

  **What it does not guarantee: the tie.** A pair on S's side can remove a device above one of
  M's signers, if that device is S or junior to S: S itself when S approved the signer, or a device
  P added after S that approved it. Then neither removal yields, both rank 2, each revokes the
  other's signers, and neither shields. If J and K, made by S on an old branch, cosign the removal
  of S there, keeping themselves, S's adds of C1 and C2 are concurrent with it and void, while J
  and K are void because of the removal of S that C1 and C2 cosign: C1, C2, J and K and
  everything any of them enrolled, on either branch, are void, and if no other approver is left,
  the generation needs a reset (§5.5). Aimed at P, which approved C1, it takes P and C1 and voids
  C1's work, and spares C2. Nothing S's side adds survives either way (the first point above
  holds); what is lost is the person's work. So recovery is guaranteed only through cosigners none
  of whose approvers is S or junior to it, and a stolen genesis, to which every device is junior,
  can force a reset. No tie-break over the entries is safe: the graph is symmetric, and DAG
  position, oldest enrollment and entry hash can each be ground or favour a fabrication on an old
  head. It takes a stolen senior device whose thief passes its presence check, it is announced,
  and decision 9 states it.

  **The other side.** No rule over the entries alone can tell a stolen senior device removed by
  two honest devices from an honest senior device removed by two hostile ones: the graphs are the
  same. So the rule also keeps what two hostile confirmed devices, approved independently, do
  after cosigning the removal of a senior device that removes them on another branch through a
  partitioning relay. That needs two approval mistakes, two confirmation mistakes three days
  later, and the partition; the person's other devices senior to them can remove what they
  added, and if none are left it is a reset. Decision 9 states it.
* **A copy can bring back a removed key.** A bar holds only where the removal's signer was active
  in the barred add's parents' state. A hostile senior H can sign a copy of a removed device's key
  on a parent from before the removal's signer was added, which no bar reaches, and make the id
  active again under H's own descriptor (§3.3), so the removed device's channel key is served
  again. This gives H nothing it could not get by approving a new key of the same device, it is
  announced as a displacement or a conflict naming H, and removing H takes the copy. It is stated
  rather than closed.
* **A removal acts on the adds it takes, and bars its target's key.** Each id has at most one add
  that is neither void, subordinate nor conflicted, so "the device" a removal revokes is well
  defined: the one its target add made active, if any. A removal revokes its target add and the
  subtree adds it takes whatever their state, so a removal of an add that is void, or turns out
  subordinate in step 4 of the order of computation below, still revokes it, and a revoked add
  never joins a conflict set. Beyond that, a valid removal R **bars** every other add `b` of **its
  target's** id that is not an ancestor of R and in whose own parents' state R's signer (and
  cosigner, if any) is active: such a `b` never makes the id active. Bars do not reach the ids of
  the subtree adds R takes: any other add of those ids by a device of the subtree is void anyway
  if R did not see it, and a bar there would only disable a genuine enrollment whose key a copy in
  the subtree had taken (§3.3). This closes a revival. A and B each add X on concurrent
  branches, and C removes A's add before learning of B's. If a concurrent removal of A then voids
  A's add, B's add would otherwise activate X with nobody approving it after C's removal; when C
  was active where B approved X, the bar keeps X revoked.
  * **Bars are monotone.** Both conditions are fixed once R and `b` exist: ancestry never
    changes, and a parents' state is computed from that entry's ancestors alone. So no later entry
    lifts a bar. In particular a removal of C concurrent with R, delivered at any point, changes
    nothing, and nor does any voiding.
  * **Bars reach only devices the remover had authority over.** If R's signer is active in `b`'s
    parents' state, its add is an ancestor of `b`, so it is senior to the device `b` makes and
    could have removed it directly. A hostile device can therefore never bar a device senior or
    concurrent to it, with or without a dishonest relay.
  * **Removed devices and their lineage bar nothing new.** A removed H that makes an add(N) for a
    replacement device's real key on an old head and then removes that add bars nothing: the
    replacement's add came after H's removal, so H is not active in its parents' state. The same
    holds if H first adds J on that old head and J does the fabricating and removing: J is in no
    real add's parents' state at all. H's and J's adds are void, and the real N stays.
  * When C was **not** active where B approved X (C was enrolled on A's branch, say), B's add is
    a grant C had no authority over. X is then active through B, and every device shows that X is
    still active through B's approval although C removed it, so the person can remove it there.
* The genesis entry is never void (§3.3), so the generation's root never disappears from under
  its own state.
* **The order of computation.** A verifier computes the state in one global pass, so that copies
  across ids cannot deadlock it (X2 copying Y while Y2 copies X, say):
  1. Revocation, bars and voiding by removals: which removals apply, what each takes (its target
     and the subtree adds under it that it does not keep, each add's signer read from its own
     parents' state), their ranks, raw chains and bars, the self-removal rule and precedence, then
     the voiding that follows transitively. All of these depend only on ancestry, on parents'
     states, on approval subtrees and on raw chains, never on subordination or conflicts.
  2. Conflict sets: for each id, the adds neither void nor revoked after step 1, formed for every
     id at once.
  3. Winners: each set's winner, if any, from ancestry and from which adds step 1's removals
     keep (§3.3, "A kept add does not displace"), for every id at once.
  4. Subordinate adds are void, and so is everything they added and every grant signed by those
     devices, transitively. This never feeds back into steps 2 and 3: an add that step 4 voids may
     still have made another id conflicted.
  5. Bars, then which ids are active.
  Every step reads only the entries and the results of earlier steps, so the result is the same
  in every order **for the same set of applied entries**. Two verifiers holding the same entries
  can still quarantine different ones (above), and then compute different active sets until an
  adopting merge or a local adoption makes them agree.
* **What that leaves.** A removed device that still holds its keys can sign a removal on a head
  from before its own removal, and that removal stands. Alone, it can only remove devices that
  were active in that old state and junior to it. With a second, independently approved removed
  device (§3.4), the pair can also cosign the removal of a device senior to both, or concurrent
  with them. That holds even if the person removed both during their probation, since removals are
  never held or un-applied. Each target falls once, since the person's re-added devices have new
  keys the removed devices never saw, and such a removal never voids what its target did after the
  removal that revoked the remover, when that removal shields against it (above). A senior
  device re-added this way comes back junior. Removed
  devices can never add trust, every fork and removal is announced, and the person re-adds what was
  removed. This revocation-only denial of service is accepted rather than voiding removals by
  delivery order or by clock, which would let a revoked key come back. Decision 9 states it.
* Probation holds (§3.4) apply to grants only, and are applied last. They delay effects, and never
  decide validity or voiding.

A `merge` joins heads into one. With `adopt: true` it may name a quarantined head to adopt it;
without it, it adopts nothing, whatever it names. Its state is that
computation over its parents. Until one is signed, every verifier already applies the state of
its heads and reports `rosterHead` as all of them in head order. A merge needs a non-probationary
person's device and presence, because adopting a quarantined branch is a decision: an honest
client does not offer it on probation, though verifiers do not hold it (§3.3). So the
device that sees a fork offers "Tidy the device list" on its Devices page, listing what each
branch did, and signs a merge with `adopt: true` when the person confirms. That is the only way an
honest client adopts: the joins it signs on its own, before an append or after a removal, never
carry `adopt`.

**Local adoption.** A merge can fail to help: if a host quarantined the branch on which G
approved and confirmed C1 and C2, their removal of a stolen G is held there, and their own merges
adopt nothing (rule 3), so only G could adopt it for them. A host therefore also adopts a
quarantined branch by **local action**, as it changes generation (§5.5): the app on that machine
over the pipe, or `agent-bridge-host devices adopt` at the machine, refused inside agent sessions
(#67). Both show what the branch does (who it adds and removes) and its head, and the person
confirms at the machine. Adoption is recorded in the host's own store, not as a roster entry, and
is as irrevocable there as a merge's. The person's devices offer it as the remedy when a machine
does not confirm a removal (§6.3). Without it, that case would need a reset.

**Generations.** A verifier never moves to another generation because something remote says
so; only a local action does (§5.5).

**Downgrade.** Once a host or client holds a roster, nothing remote turns enforcement off:
not a relay without roster support, a relay that says there is none, a peer claiming an older
protocol, or a missing capability. Only a local setting does (§8.1).

### 3.6 Where it lives

* **The relay is an untrusted store.** It keeps one set of entries per account, beside the
  machine roster ([roster.md](../protocol/roster.md)) under the same naming and ETag rules, and
  serves it to approved devices only. It appends an entry only if it is valid by §3.5 against
  the relay's own copy. Honest approvers therefore extend its single head, except that a removal
  forks from an older parent by design and is joined by a merge straight after (§3.5). An approver that
  finds the head moved re-reads and re-verifies it. It stops if the new head removed it or already
  added the same device. Otherwise it shows the person what changed and asks for the presence
  proof again: the new `prev` and `seq` make a new body, which the old signature and assertion do
  not cover, and a presence-gated key cannot re-sign silently.
* **The relay never replaces a roster on a token's say-so** (§5.6).
* **Every host and person's device keeps its own verified copy**: the host in its profile
  (private folder, as `PrivateFolder` writes), the apps in their profiles, the PWA in IndexedDB,
  the Apple clients in their container with the heads also in the Keychain.
* **Gossip.** On every channel between two active rostered devices, each says its heads in
  `hello`. The one missing entries fetches them from the other with `roster.sync`. Hosts already
  open channels to each other (`UsagePeers` every 5 minutes, transfers), and the Windows app
  talks to its host over the pipe, so entries spread even if the relay withholds them. A host
  never serves `roster.sync` to an unrostered device (§7.1).

### 3.7 How a host applies it

On each newly accepted entry (relay message at connect and on change, `roster.sync` from a
peer, the chain an approver hands it at enrollment), a host recomputes the state and then:
* closes channels from devices that are no longer active;
* drops their push subscriptions (`push.*`) and anything else it holds per device;
* recomputes who may do what.

A host that cannot read its copy (store unreadable, corrupt file) keeps enforcing the last state
it computed and refuses everything else until it can; unreadable is not absence.

## 4. Enrollment

### 4.1 What the person sees

1. The new device N signs in. It shows **"Waiting for approval"**, a six-digit **code 1**, and on
   a desktop a QR code too. Nothing else: no machines, no sessions.
2. On an approved person's device A that can approve (§4.6), a banner reads "A new device wants
   to join: *Kitchen laptop*, says it is a Windows app, id `3f9a 2c71 b0de 4a5f`, signed in 2 min
   ago". The person opens it and **types code 1** (or scans N's QR), and may edit the name.
3. A then shows **code 2**, and the person **types it on N**. Beside the box, N shows what it is
   joining: "*login*'s devices, started by *Travel iPhone* on Nov 3, fingerprint
   `7d0f 3a2e 1c4b 4d5e`" (§4.2). On a machine, typing code 2 there is the person confirming at
   the machine which roster it joins.
4. A asks for its presence check (Face ID, the passkey prompt, …) and signs. Within a second or
   two, N is a normal device on probation (§3.4).

The other direction exists for a phone. The desktop app shows **"Add a phone"** with a QR code,
the phone scans it (§4.3), and the person confirms on both.

### 4.2 The code exchange

It runs over a Noise `XX` channel between the approver A and the pending device N, which A
opens through the relay; a pending device never opens channels (§4.5). After the handshake each
side knows the other's static key and the handshake hash `h`, which binds both keys, both ids
(the prologue) and the channel. A man in the middle (the relay) would have two channels with
two different `h`. **Each side authenticates the other through the person**, not only A
authenticating N. Revision 1 let N accept any approver, and a relay that is its own approver
knows every code.

1. **N → A: commit.** N picks a 32-byte nonce `nN` and sends
   `C = SHA-256(F("agent-bridge enroll commit v1", h, nN))` with its proposed `kind`,
   `platform`, `name` and `presence`. A person's device also sends its `sign` key (and WebAuthn
   credential), with a signature by `sign` over `F("agent-bridge enroll key v1", h)`, proving it
   holds that key on this channel. A host has no `sign` key.
2. **A → N: offer.** A picks `nA` and sends it. A has not seen `nN`.
3. **N → A: reveal.** N sends `nN`. A checks it against `C` and abandons the attempt otherwise.
4. **Both** compute `T = SHA-256(F("agent-bridge enroll v1", account, h, nA, nN))`, then
   `code1 = U32(SHA-256(F("agent-bridge enroll code1 v1", T))) mod 10⁶` and
   `code2 = U32(SHA-256(F("agent-bridge enroll code2 v1", T))) mod 10⁶`. `U32` is the first 4
   bytes as a big-endian unsigned integer. A code is always the six-digit decimal string,
   zero-padded (`000042`), shown as two groups of three. N shows code 1.
5. **The person types code 1 at A.** A drops spaces, refuses anything that is not exactly six
   ASCII digits, and compares in fixed time. On a match A shows code 2 and sends N the genesis
   entry. N then shows what it is joining ("*login*'s devices, started by *Travel iPhone* on
   Nov 3, fingerprint `7d0f 3a2e 1c4b 4d5e`": the first 64 bits of the genesis entry hash, in groups
   of four) beside the box for code 2.
6. **The person types code 2 at N.** N compares the same way. On a match N sends `ok`; three
   wrong entries end the attempt (§4.4).
7. **A signs** the `add` (`approval.transcript = T`) with its presence proof and appends it at the
   relay. It sends N the roster from genesis through that entry, together with how much of each
   active device's probation remains by A's clock, which N adopts (§3.4).
8. **N verifies** the roster (§3.5) and checks that the newest entry adds exactly its own keys,
   kind and presence, that its signer is A, and that the transcript is its own `T`. It then keeps
   the roster and connects again as an approved device.

Why each piece is there:
* **Commit before offer.** A man in the middle must fix its nonce on one leg before it learns
  the other leg's, so the two `T`s are independent. Each code matches across legs with
  probability 10⁻⁶ per attempt, and each attempt needs a person to type.
* **Code 2, typed at N**, is what N checks that only the person can satisfy. A relay acting as
  its own approver to N knows code 2 of that exchange, but the person reads code 2 off their real
  approver, whose exchange (if any) is with the relay on another leg, so it does not match.
  This defeats the attack the review found: a relay with its own genesis enrolling a pending host
  so that the owner's real approval is refused.
* **The fingerprint at the machine** shows which roster a host is joining. A person who has
  more than one device sees the same fingerprint on every Devices page.
* **Typing, not comparing.** Comparing invites tapping Approve without reading.
* **No secret is disclosed.** Only public keys, nonces and a signed entry cross. No private key
  leaves its device, and a record of the exchange is useless later.

A balanced PAKE (CPace) over one code would give the same mutual property with one code. It is
not used in v1. WebCrypto has no hash-to-curve, so the PWA would need new curve arithmetic in
JavaScript, while two codes use only primitives every client already has.

### 4.3 The QR paths

**A shows the QR (phone joining).** The QR carries A's device id and a fresh 128-bit secret `q`.
The phone (N) scans it and connects to the relay as pending with an **invite hint**,
`hex(HMAC-SHA256(q, F("agent-bridge invite hint v1"))[0..7])`. The relay shows that hint beside
it in `pending`. A opens a channel to each pending device whose hint matches, so routing stays
as §4.5 has it: only a person's device opens a channel to a pending one. After the handshake:
* N sends `HMAC-SHA256(q, F("agent-bridge enroll qr joiner v1", h))`;
* A sends `HMAC-SHA256(q, F("agent-bridge enroll qr approver v1", h))`;
* each checks the other's in fixed time.

The high-entropy `q` stands in for both codes: each side knows the other is the device the
person pointed the camera at. Steps 1-3 and 7-8 of §4.2 still run, without codes. The person
confirms on A (with its presence check) and taps Join on N.

**N shows the QR (a desktop added from a phone).** The QR carries N's id, a hash of its `sign`
key, and `q`. A scans it, opens the channel, and checks N's id and key hash. The two HMACs are
exchanged as above, so N too knows its approver is the device that scanned it.

`q` expires with the attempt and is never stored.

### 4.4 Limits, refusals and lockout

* One enrollment at a time per approver and per pending device; an attempt expires after
  **5 minutes**.
* At A, three wrong code-1 entries for one pending device end its attempts for **15 minutes**,
  and three in an hour lock A's enrollment for 15 minutes. At N, three wrong code-2 entries end
  the attempt, and three ended attempts lock N's joining for 15 minutes. Both are counted by
  the device the person is typing on; the relay's limits (below) only bound load.
* Refused, with the reason at both ends: a wrong code, an expired attempt, a device already
  approved, a key that was removed before ("this device was removed; sign out and in again to
  make a new key"), an unknown kind, a roster at its limit, an approver on probation or without
  presence.
* The relay limits each account to **8 pending devices** and **20 enrollment attempts an hour**,
  and drops a pending socket after an hour.
* Logs record the attempt (a random tag), the outcome and the device ids, never a code, nonce or
  `q`.
* A pending device's name, kind and platform are claims until approved. The approver says so
  ("says it is…"), shows **16 hex characters** of its id in groups of four (8 characters can be
  matched by generating about 2³² keys; 16 would take 2⁶⁴), shows when it signed in, and says
  "Only approve a device you are holding now."

### 4.5 What the relay does for a pending device

The relay verifies the roster too (signatures are public) and uses it as defense in depth,
never as the gate.

**Relay mode.** Appending a genesis does not by itself change what any device gets from the
relay. Each account with a roster has a relay mode, which starts as **`observe`**: the relay stores,
verifies and serves the roster, reports bound kinds for active devices and lists unrostered ones
to person's devices as pending, but otherwise serves every device as it does today. That keeps the
owner's existing apps and hosts working while they enroll one at a time and their hosts are still
in `off` or `observe` (§8.2). The mode moves to **`enforce`** when an active person's device asks
for it on its own relay connection (the Devices page's "Restrict unapproved devices"), or at
genesis when the relay has never admitted another device for the account, as on a new install. It
never moves back, except by a reset (§5.5) or the administration path (§5.6). The request needs no
presence proof: the switch only tightens, and the relay is never the gate, so a hostile approved
device that switches it early causes only an early disruption, the denial of service §2 already
grants. In `enforce`:
* admission tags each socket **approved** or **pending** by whether its key is active in the
  roster;
* `presence` and `known` report the **bound** kind and name for approved devices;
* a pending device's `presence` is empty apart from `pending: true`. It receives no `known`, no
  roster and no heads;
* it never opens channels: its frames are routed only on channels a person's device opened to it;
* it may not use push, the blob store or `forget`;
* approved person's devices see it in a separate `pending` list in `presence`, with its claimed
  name, kind, platform, sign-in time and invite hint;
* a removed device is refused at admission with `removed` (a new close code), so it can say so
  (§6.5).

### 4.6 Presence: who can approve

The property wanted is **presence enforced outside the app**: the proof cannot be produced
unless a person has just verified at that device. A dialog the app shows before using a key that
would sign without it is not that property, since malware running as the user calls the key
directly. Nor is a prompt it can answer by injecting input, and this project's own injector
types into consoles.

| Platform | Presence proof | Status |
|---|---|---|
| Apple, iPhone and iPad | Secure Enclave key with `.userPresence` (Face ID, Touch ID or the device passcode, in the system's own UI, which other apps cannot drive) | `presence: key` |
| Apple, Mac app | A Secure Enclave key with `.userPresence` falls back to the login password in a system dialog, the same prompt a macOS PWA would face, which same-user code may reach. So the Mac app declares `key` only for a key made with `.biometryCurrentSet` and no password fallback (Touch ID only); otherwise it declares `none` until L1 proves the macOS prompts | `presence: key` with Touch ID only; else `none` until L1 |
| PWA | A WebAuthn platform credential (`userVerification: "required"`), registered at enrollment; each proof is an assertion whose challenge is the hash of what is signed. Any host verifies it: `rpIdHash`, the UV flag in the signed `authenticatorData`, `type: webauthn.get`, the origin, the challenge, and the signature under the credential key in the roster | `presence: webauthn` only for a **qualifying** credential (below); otherwise `none` |
| Windows app | R4 must pick one mechanism and **prove** that same-user code cannot satisfy it. It must hold against `SendInput`, UI Automation and this project's console injector, running unelevated at the same integrity level. The candidates are the Windows WebAuthn API (`webauthn.dll`, Windows Hello, which makes ES256 credentials, unlike `KeyCredentialManager`'s RSA keys) and a TPM key with an NCrypt UI protection policy or PIN | `presence: none` until that proof; then `webauthn` or `key` |

What a presence proof does **not** cover: malware that waits for the person's own approval and
swaps what is approved. The confirmation screen shows the device's name, kind and 16-character
id, and the signature covers the entry, but on a compromised approver the screen can lie. Two
cases remain:
* **Synced passkeys.** A credential that the platform syncs exists on the person's other devices
  in that sync account. Each use still needs the person's verification, but on a device that may
  not be proven, so in v1 a synced credential never qualifies (below).
* **A script in the PWA's origin.** Any script on the Pages origin (a repository, Actions or
  Pages compromise, a dependency, or XSS in rendered agent output) can ask for an assertion. The
  system's prompt then appears, but it cannot say what is being approved. The PWA's
  content-security policy and the existing rule that agent output is never rendered as HTML are
  what keep such a script out.

**Which credentials qualify, and who decides.** A WebAuthn assertion with user verification is
only as strong as the system prompt that collected it. On a desktop, the browser's prompt is the
system's own: Windows Hello through `webauthn.dll` on Windows, Touch ID or the login password on
macOS, and whatever the browser provides on Linux. Same-user code can reach those with
`SendInput`, UI Automation or this project's injector unless proven otherwise, exactly the question
R4 answers for the Windows app. A **synced passkey** can also be asserted from any device its
provider syncs it to: an iCloud Keychain passkey registered on an iPhone can be used from the
person's Mac, and a Google Password Manager passkey from Chrome on any desktop.

Verifiers must agree on which devices are presence-proven, and their software versions differ, so
**no verifier consults a list of platforms**. The decision is made once, by the approver's client
at enrollment, and recorded: the approver records `presence: webauthn` only for a qualifying
credential, and `presence: none` for anything else, and nothing can raise it later without a new
enrollment (§3.1). Verifiers read only the recorded `presence`, and check that every assertion
carries the registration's backup-eligible (BE) flag, recorded as `device.webauthn.backupEligible`.
In protocol v1 a credential **qualifies** only if:
* it is **device-bound** (BE clear); synced credentials (BE set) never qualify in v1, and a verifier
  refuses an entry or person command from a `webauthn` device whose recorded `backupEligible` is
  true; and
* its platform is one of `ios`, `ipados` or `android`, whose prompt is the phone's biometric or
  passcode screen; or `windows`, once the approver's client ships R4's proof that Windows Hello as
  browsers reach it cannot be satisfied by same-user code. `macos` and `linux` wait for L1.

In practice this excludes most phone PWAs today. Passkeys made in Safari on iOS and iPadOS are
iCloud Keychain passkeys, which are always synced (BE set), so **an iOS or iPadOS PWA never
qualifies** in v1 and is recorded `none`; on those devices the presence-proven client is the
native Apple client. On Android, passkeys saved to Google Password Manager are synced as well, so
an Android PWA qualifies only with a device-bound credential from a provider that offers one.

An approver's client applies the list of the version it runs. A device enrolled before its platform
was proven stays `none` and is enrolled again to gain presence. Admitting synced credentials, or a
platform this list leaves out, is a later protocol version, not a setting.

So the first presence-proven approvers are an iPhone or iPad running the Apple client
(`presence: key`), the Windows app once R4 has proven its mechanism (Windows Hello credentials
through `webauthn.dll` are device-bound, which the BE flag confirms), a Mac app with a Touch-ID-only
key, and an Android PWA with a device-bound credential where its provider offers one.

A device recorded as `none` can be approved and drive sessions, but cannot approve, sign roster
entries or give person commands. The approver says so when it enrolls one, such as a desktop PWA.

Under owner decision 3(a), only `presence: key` and `presence: webauthn` devices approve or do
person-only actions. Option 3(b) is **not specified by this note**: §3.3 requires presence for
every entry, and letting devices without it approve would need its own design and review.

### 4.7 A machine with an app and a host

Usually a machine has both, and they enroll together. The app is the enrollee. It reads the
host's ECDH public key over the local pipe (an op limited to the app, as `host.manage` is,
[local-pipe.md](../protocol/local-pipe.md)) and asks for both. The person types code 2 into the
app, after it shows the genesis fingerprint. A signs two entries (`app` and `host`) with one
presence proof each, and the app hands the roster to the host over the pipe. A host without an
app (a Linux or Mac host run by hand) enrolls itself with `agent-bridge-host devices join`. It
shows code 1 and the fingerprint in the terminal and reads code 2 there; the #67 guard refuses
it inside an agent session.

## 5. Bootstrap and recovery

### 5.1 The first device

Genesis is an explicit choice on a person's device that can approve (§4.6): **"Start this
account's device list"**, typed `NEW`. It is never inferred from finding no roster, since the
relay can hide one. When the relay says there is a roster, genesis is not offered. When it says
there is none, the device says that if other devices were ever approved, this one should be
approved from one of them instead. Under decision 3(a) the first device is a presence-proven one
(§4.6): an iPhone or iPad running the Apple client, the Windows app once R4 has proven its
presence, a Mac app with a Touch-ID-only key, or an Android PWA with a device-bound credential.

### 5.2 How a host comes to trust a roster

Only by enrollment, where the roster comes from the approver whose code 2 the person typed at
the machine and whose genesis fingerprint the machine showed, or by a local action at the machine
(§5.5). Never by first use of whatever the relay serves. So an attacker's genesis, made with a
stolen token or by the relay, is never trusted by a machine the owner has not typed its code 2 into.

### 5.3 Clients

A person's device trusts the roster it was enrolled into, by the same mutual check. The genesis
device trusts its own.

### 5.4 Losing a device

Remove it from another (§6). Losing a junior device is routine: any device senior to it removes
it. Losing a senior device needs two surviving approvers whose lineages, cut at the lost device,
are disjoint (§3.4), typically two devices the lost one approved itself. A person whose approving
devices are the genesis device plus one other, or whose survivors were all approved through one
device junior to the lost one, cannot remove it, and that loss means a reset (§5.5). The
recommended setup is therefore at least three approving devices, two of them approved directly by
the genesis device and confirmed by it three days later. Genesis is the most senior device, so
the best one is the least exposed: an iPhone or iPad running the Apple client as genesis, say,
approving the Windows apps on two machines. Until an Apple client exists, the Windows app on the
main desk machine (after R4) is genesis, approving a second machine's app and a third approving device, such as an Android PWA with a device-bound credential (an iOS PWA never qualifies, §4.6); that
puts the most senior key on a machine where agents run (decision 3).

**A stolen senior device** is different from a lost one: its thief holds a head from before the
removal, and can still revoke, once, every device that was active and junior to it there,
including the two that removed it (§3.5), without voiding what they did after removing it. But it
can also force the **tie** of §3.5: approve and confirm two devices on an old branch and have them
cosign its own removal, or that of a device added after it, which voids the devices it or that
device approved after that point, the cosigners included, and everything they enroll, fresh
devices too. So recovery is **guaranteed only through approvers not junior to the stolen
device**, and a stolen genesis device can force a reset. What the stolen device and the devices
it made add does not survive the person's removal of it, except what the person kept, unless a
device outside its subtree helps revoke one of the removal's signers (§3.5, "What this
guarantees"). The Devices page, right
after a cosigned removal of a senior device, asks the person to enroll a fresh device (or
re-enroll one already held, with a new key), from an approver not junior to the stolen device
where there is one: that keeps the account's approvers against the one-time revocations, and
against the tie. All of this needs the thief to pass the stolen device's
presence check, since every entry needs one (§3.3); a device stolen while locked can do none of
it, which is why the genesis device is best a phone with its own biometric prompt (decision 3).

### 5.5 Losing every person's device: reset

A **reset** makes a new generation on a new person's device (typed `RESET`), and every device
enrolls again into it. A host moves to the new generation only by a local action at the machine:
* the app there, over the pipe, after a mutual enrollment with the new genesis device; or
* `agent-bridge-host devices reset`, interactively, refused inside agent sessions (#67).

Until then it keeps the old generation, which refuses everyone when nobody in it is left. That is
fail-closed: a machine the owner cannot reach stays unusable remotely until they can.

An optional **recovery key** (owner decision 5(b), not chosen) would have avoided the trip. At genesis, a person's
device makes an extra ECDSA key, shows its private half once as 24 words and a QR code to keep
offline, and enters its public half as a `recovery` approver. Typed into a new device, it signs
that device's `add`. The cost is a long-lived secret that, with the GitHub account, is enough to
take the account.

### 5.6 What the relay does with a new generation

A token alone never replaces a stored roster or changes admission:
* The first genesis for an account with no stored roster is accepted.
* A genesis for an account that has one (a reset) is held for **72 hours**. During that time the
  relay tells every device of the current generation ("a reset of this account's devices was
  requested"), and any active person's device of the current generation can veto it with a
  signed `veto`, which ends it. Devices of the new generation stay pending until it takes effect.
  The `veto` is sent as `{ "body", "sig", "uv" }` like an entry, with `body`
  exactly
  `{ "v": 1, "op": "veto", "account", "generation", "reset", "by", "at" }`: `generation` is the current
  one, `reset` the entry hash of the held genesis, and `by` the vetoing device. `sig` is by `by`'s
  `sign` key over `F("agent-bridge reset veto v1", bodyBytes)`, and the veto needs a presence proof as
  any entry does (`uv` over the same bytes for a `webauthn` device, §4.6). The
  relay accepts it only if `by` is an active person's device in its copy of the current
  generation, the `reset` names the genesis it holds, and the body parses strictly (§3.2).
* A squatted first genesis (one made with a stolen token before the owner's) never reaches a
  machine (§5.2). The owner operates the relay, so it is removed with a relay administration
  action ([relay-deploy.md](../relay-deploy.md) will document it in R2), which no verifier relies on.
* **A veto from a compromised device.** A stolen, unlocked senior device stays an active signer, so
  it can veto every reset, and through the relay alone the replacement generation is never admitted.
  The recovery is the same **administration override**, authenticated out of band: the relay
  operator ends the veto and admits the new generation. For this deployment that is the owner; on
  a shared relay it would be the operator's support process, with the account holder proving who
  they are outside the relay. The override changes only the relay's storage and admission. Each
  machine still moves to the new generation only by the person's local action there (§5.5), so the
  override cannot hand a machine to anyone. Without the override, reset cannot recover through the
  relay while a compromised device keeps vetoing.

This binds an honest relay. A dishonest one can store anything, and no verifier holding a roster
follows it.

## 6. Removal

### 6.1 The person

A person's device that can approve has a **Devices** page listing the roster: name, kind,
platform, 16 hex characters of the id in groups of four, by whom each device was added and when this device first saw it (§6.1),
whether it is on probation, and whether it is online now or when it was last seen. It also shows
the genesis fingerprint. **Remove** takes two presses (the first arms it for 5 seconds, "Press
again to remove", as Forget does), then the presence check. It offers the cascade (§3.4), signs
**one** `remove` naming the target's active add and keeping the unticked devices, appends it, and sends
it to every connected host and device directly as well as through the relay. A more senior
device, or any device when the target approved others (§3.4), asks for a second device's
cosignature; the other device needs its own presence check. The keep list is what keeps a
device, so a thief holding a device J that stolen S made, named "Dan's iPad", can try to get J
onto it in two ways: build `remove(S)` with `keep: [J, …]` itself and ask the person to cosign;
or be picked by the person as the cosigner of their own removal of S, which must then keep J as
a signer. Either way every validity rule passes (J is kept, so it may sign; the cosign test
passes; it ranks 2), and once S is revoked the person's own removal of S is no longer valid. A
check on only one of the two clients does not help, since one of them may be J. So **each**
signing client, initiator and cosigner alike, before its own signature:
* shows the **whole removal**, not only its target: "*Desk app* and *Dan's iPad* remove *Old
  laptop*, keeping:", followed by every kept device, a kept signer included, with its name, 16
  hex characters of its id, its approver, and when this client first saw it (below);
* in the cosigner picker, lists and recommends devices outside the target's subtree first, and
  devices in it last, with a warning;
* asks the person when they last had the target ("When did you last have *Old laptop*?"). The
  answer stays on this client; neither client takes it from the other, since one of them may be
  the thief's;
* treats as **in question** every signer in the target's approval subtree, and every kept device
  that this client first accepted after that time (or, if the person does not know, after this
  client's own enrollment): the window in which a thief could have made them. Each needs the
  person to choose "Keep *Dan's iPad*: I added it myself", with its approver and when this client
  first saw it, and to confirm that a second time. Every other kept device in the subtree needs a
  single acknowledgement, shown in one list. "First saw" and "first accepted" are this client's
  own wall-clock time of first accepting the add, recorded locally when it did: never the add's
  `at`, which its signer chooses and a thief can backdate, never a time handed over at enrollment
  (§4.2) or by the local app over the pipe, and not the monotonic running time that probation
  counts (§3.4). A device enrolled after the theft first sees every add then, so for it every
  kept device of the subtree is in question;
* refuses to sign until each of those is given. The cosigner's client offers instead to start
  the removal from itself with its own keep list; the initiator's offers to pick a cosigner
  outside the subtree where there is one.

So the usual recovery, by two devices the lost device approved, asks for the two signers twice
on each client and for the older kept devices once, in a list. Confirmations asked this often
invite agreeing by habit; that is disclosed in decision 9 rather than prevented. A device kept despite them is removed afterwards like any other, by a device senior to it or two
confirmed devices with disjoint lineages (§3.4). Both signers must be confirmed (§3.4); an
unconfirmed device does not offer or accept a cosignature. An honest client does not offer to remove the last person's device; if
concurrent removals leave none anyway, only a reset continues (§3.5).

A device can remove itself ("Remove this device"), which also signs it out.

### 6.2 Revocation reaching every host

* **Online hosts** get the entry from the relay at once and from the remover directly. They close
  the device's channels, drop its push subscriptions, and refuse it from then on.
* **Offline hosts** get it when they reconnect, from the relay and from the first rostered peer
  they exchange heads with (§3.6). A peer the host does not know yet, because it was approved on a
  branch the host missed, sends its chain through `roster.submit` (§7.1).
* **The relay** refuses the removed key at admission, so an honest relay cuts it off everywhere at
  once.

### 6.3 Freshness, and what it cannot bound

A relay that withholds a removal from a host, and routes only the removed device to it, could
otherwise keep it served indefinitely. Freshness narrows that, and an acknowledgement shows the
person where it has not reached. Neither bounds a relay that partitions the account; that
residual is stated at the end.

* **Head attestation.** On request, an active rostered device sends the host its heads, MACed for
  that host as signed commands are: from static-static ECDH between the two device keys,
  `HMAC-SHA256(k, F("agent-bridge roster head v1", account, generation, heads, host id, nonce))`.
  `nonce` is a fresh 32-byte value the host chose. No presence is needed; it vouches for a roster,
  not an action. If the heads include entries the host lacks, the host fetches them first.
* **Sources.** Every rostered peer that opens or answers a channel attests: other hosts (the usage
  exchange every 5 minutes) and person's devices. So does the app on the same machine over the
  pipe, which needs no relay at all.
* **Fresh** means an attestation the host received within the last **24 hours**
  (`security.roster.freshHours`) from an active device other than the one making the request, with
  heads the host has. The time is the host's own clock from receipt. There is no exception for an
  account with one person's device: a relay could hide an addition and then the removal of the
  formerly only device, and that device's own attestation would keep the host fresh.
* **Stale.** A stale host serves relay devices nothing but `hello`, `roster.*` and attestations
  (owner decision 10), and says so in `host.status`, the app and the log. A host that no other
  device ever contacts (a headless host with no app, on an account with no other host) therefore
  becomes unusable from the relay after 24 hours idle. It recovers when another device of the
  account attests.
* **`selfAttest` is the single-device exception above, re-opened by choice.** The local setting
  `security.roster.selfAttest`, off by default and #67-guarded, lets such a host take the
  requester's own attestation instead, so a relay that hides an addition and then the removal of
  the requester keeps that host serving it. The host says so in `host.status`, the app and the log
  while the setting is on. The attestation is a **person command** (§7.2) with
  `action: "attest"`, `target.machine` this host, the host's `nonce` from `hello`, and `heads`; it
  needs a presence proof, so it cannot be produced by a stolen key alone. The host accepts it only
  if it holds every head named, and those heads include or descend from every head named by the
  last attestation it took from another device: it can keep a host fresh, never move it back.
* **The clock.** Neither clock can measure time a machine spent off if the wall clock also moved
  back meanwhile, so an attestation never survives a discontinuity: after a restart, or when the
  wall clock jumps by more than 5 minutes against the monotonic clock, the host is **stale** until
  it receives a new attestation. Otherwise freshness uses the larger of elapsed monotonic time and
  elapsed wall-clock time since receipt. Other hosts every 5 minutes and the app over the pipe
  refresh it within minutes of a restart.
* **Acknowledgement.** Heads alone do not show that a host applied a removal: it can hold the
  removal and still serve the target, as when the signers' adds are quarantined there (§3.5,
  "Local adoption"). So a host's attestation to a person's device also lists the enrollment adds it
  has **revoked in its applied state**, under the same MAC. After a removal, the remover's Devices
  page counts a machine as confirming only when its attestation lists the target's exact add:
  "Revoked on 3 of 4 machines; *Desk* holds the removal but has not applied it: adopt the branch
  there" or "*Desk* has not confirmed (last heard 2 h ago)". A machine that does not confirm is
  something the person can go and look at.

**What freshness does not bound.** A relay that partitions the account can keep a removed device
served inside that group until the partition ends. The partition keeps a group of hosts, and the
apps on those machines, away from every device that holds the removal, and routes the removed
device to them. The stale peers inside the group attest each other's old heads with fresh nonces,
and nothing inside can tell. Without a trusted clock or an honest party that reaches the group,
no rule can tell such a group from an up-to-date one. So revision 1's claim of a fixed bound is
withdrawn. What the person sees is the acknowledgement above: the partitioned machines never
confirm the removal, and look unreachable from the person's other devices. The person ends it at
such a machine: the app there, or `agent-bridge-host devices sync`, imports the current roster
from a file exported by any of the person's devices. This needs a dishonest relay **and** the
removed device's keys.

### 6.4 Every change is announced

Every `add`, `remove`, `rename` and open fork is shown on every person's device ("*Kitchen
laptop* was added by *Desk app* at 14:05 PT") and logged by every host. Probation (§3.4) gives the
person 72 hours to notice a device they did not approve before its adds and renames take effect,
and confirmation (§3.4) keeps it from cosigning any removal until its approver confirms it, but
neither delays session access: an approved device can read and drive sessions at once, which is
why every add is announced. Seniority means it cannot remove the devices that would notice.

### 6.5 What a removed device sees

The relay's `removed` close code and a host's refusal in `hello` are **advisory**. The relay is
untrusted, and a refusal can be forged by it or by a host. So a device acts on them only after it
holds a **verified removal of itself**: a `remove` entry that applies under §3.5 and **takes its
own enrollment add**, the add of its own keys that it holds, as its target or from the target's
subtree (§3.3), in the generation it holds. One that merely targets its id is not enough, since
it may revoke a fabricated duplicate. Only being taken counts, because only that revocation is
permanent and names the device's own add: a device whose add is **void**, or subordinate to a
copy, can become active again when another entry arrives (a removal of another device that keeps
it, a voided copy), and a **bar** acts through another add of its id, never its own. So for
those it shows "This device is not active: *reason*" and keeps its keys and roster, never
wiping. If B adds C and then removes itself on an old head,
C sees its add void, and is active again when the person's removal of B that keeps C arrives
(§3.5, the self-removal rule), whichever order they come in. Until a verified removal, it keeps
its keys and its roster, says "The relay says this device was removed; not confirmed", and keeps
trying its other peers. A relay that merely claims a removal therefore causes at most a denial of
service, never the loss of a device's trust state.

A removed device can no longer fetch a roster (it is refused admission and `roster.sync`), so the
proof comes with the refusal. It is narrowly scoped:
* the device sends its heads in `hello`, as every device does;
* the relay, refusing it with `removed`, or a host, refusing it in `hello`, attaches a **removal
  proof**: the removal entry and every entry between the device's heads and it. Where the removal
  applies only through an adopted branch (its signers' or cosigner's adds are on a branch that
  the device's own copy would quarantine, §3.5), the proof also carries the `merge` that adopted
  that branch and every entry between the device's heads and that merge. Nothing else is sent;
* the device verifies it from the roster it already holds: the removal must apply under §3.5 in
  the state that roster plus the proof gives.

A host that adopted the branch only **locally** (`devices adopt`, §3.5) has no entry that proves
the adoption to anyone else. It still refuses the device, but sends only what it has, so the device
stays at "not confirmed" until some person's device signs a merge adopting the branch, which the
remover's Devices page offers when it sees a removal applied only by local adoption. That is a
denial of service to the removed device's own cleanup, never trust kept by it.

The proof can include devices added since the device last synced, which it would have seen had it
been online while still approved. It never goes to a device that was never approved, because such
a device has no heads in a generation the relay holds, and the relay and hosts send a proof only to
a key whose own `add` is in it. A relay that withholds the proof leaves the device at "not
confirmed", which is denial of service, not loss of trust.
With the removal verified, it says "This device was removed from *login*'s devices by *Desk app*
on Nov 2.". It deletes what it kept about the account (sessions, usage, notifications, its roster
copy) and its push subscriptions, and offers "Sign in again", which makes a **new key** and starts
enrollment again. A host does the same: with its own removal verified, it stops serving relay
devices, says so in the app and the host log, and keeps serving its own machine's apps over the
pipe.

## 7. Enforcement

### 7.1 Hosts: who gets a channel's ops

**The handshake carries nothing identifying in `enforce`.** Today `ChannelHub` puts the machine's
name and kind in the Noise handshake payload (`intro`), and in Noise XX the responder sends its
payload in message 2, before it learns the initiator's static key from message 3. So an
unrostered initiator would learn the machine's name before any gate ran. In `enforce` a host
therefore sends an empty intro (`{}`) in message 2. As an initiator it learns the responder's
key from message 2, and sends its intro in message 3 only if that key is active in its roster,
and an empty one otherwise. An active device gets the name and kind in the host's answer to its
`hello` instead. Message 2 still carries the host's static key, but that is the key the
initiator addressed, so it reveals nothing the relay's routing did not. A person's device with
roster support does the same toward a peer that is not active in its own copy. R3 changes
`ReadIntro` so that a peer with roster support accepts an empty intro. In `off` and `observe` the
intro is as today, so old peers are unaffected until a host enforces, and then they cannot open
a channel to it at all (§8.4). Emptying message 2 in `observe` too would break every peer not yet updated, whose
`ReadIntro` throws on an intro without `name`, while `observe` is meant to refuse nothing.

**What message 2 stops saying, an active device's `hello` says.** Today's message 2 also carries
`deflate` (the host inflates frames, so the opener may send them compressed) and, from a host that
takes IK, `ik` (the ops it answers from an IK first message). Neither goes in the empty intro,
which stays exactly `{}`: `ik` least of all, since only hosts send `ik: ["hello"]`, so it would say
the kind the empty intro hides. But an opener reads
message 2 for both: `ChannelHub` learns the host's key for IK from it, and forgets the key when
it holds no `ik` (`Learn`), and sends uncompressed frames when it holds no `deflate`
(`PeerInflates`). Left there, every XX handshake with a host in `enforce` would leave an enrolled
device without IK, an extra relay round trip on every open, and without compression. So the
host's answer to an **active, approved** device's `hello` carries a `transport` member,
`{ "ik": [<early ops>], "deflate": true }`, holding what its message 2 would have said, and an
opener with roster support (a host from R3, each client from its own slice), on an approved `hello`, calls `KnownResponders.Learn` with the host's static key from the
handshake and those ops, and sets the channel's `PeerInflates` from `deflate`. An unrostered
device's `hello` carries no `transport`.

**Noise IK (#653) carries nothing identifying either.** A device that holds the host's static key
from an earlier handshake opens with IK instead (`ChannelHub.OpenAsync`). Its first message
carries its static key, its intro and, where the host lists the op in its `ik` intro, one early
request; a host's `EarlyOps` is `hello` only. The host's reply carries its intro and, when it
answered early, that request's result (`ChannelHub.AnswerIkAsync`, `EarlyAsync`). Unlike XX, the
host knows the initiator before it writes anything, so in `enforce` it decides from the
initiator's static key before it builds the reply:

* **Not active in its roster** (unknown, pending, removed or void): the reply's intro is exactly
  `{}`. It has no `name`, no `kind`, no `ik` list (so the opener forgets the host's key and opens
  with XX next time, where message 2 is `{}` too) and no `early` answer. The early request never
  reaches the handler: it is not answered from the first message, the opener asks it again once
  the channel is open, and the gate answers it there as over XX, with `approved: false` and
  nothing else. This holds whether or not the first message asked anything early. Today the hub
  runs `PeerAllowed` (`RemoteAuthorization.Allows`) on the first message (`ReadIntro`) and sends
  nothing to a key it refuses; the roster gate instead lets an unrostered key's handshake
  complete, so that it can learn it is pending, and so the reply must be emptied rather than
  withheld. Otherwise the reply's intro and early `hello` would name the machine to a
  stolen-token key that learned the host's key over XX and then opened with IK.
* **Active:** the reply is as today, and an early `hello` is answered by the same gate and handler
  as a `hello` once open. The gate sits in front of the handler for early and ordinary requests
  alike (`EarlyAsync` and `ServeAsync`), never in `ServeAsync` alone.

The check is made at each reply, against the roster as it is then, so a relay that replays an
active device's first message after its removal gets `{}`, and cannot read even that: an IK reply
decrypts only with the opener's ephemeral and static private keys. A device with roster support opening IK to a key
that is not active in its own roster sends `{}` and no early request in its first message, as it
does in XX's message 3. In `off` and `observe` the IK reply is as today, as XX's message 2 is:
`observe` refuses nothing (§8.1), so until a host enforces, a stolen-token key learns from it, on
either handshake, what it learns today. The host logs each IK opening it would have answered with
`{}`, with the rest of what `observe` reports.

The host's `ChannelHub` also gets a roster gate. For a channel from a device that is not active
in the host's roster, the handshake completes so the device can learn why, and then:

| Op | Unrostered device |
|---|---|
| `hello` | Answered with `approved: false` and nothing else: no heads, no roster, no session, capability, machine or `transport` detail |
| `roster.submit` | Accepted, write-only (below) |
| Everything else, including the other `roster.*` | Refused with `not-approved` |

Decided: **nothing beyond "pending approval"** (owner decision 4(a)). A pending device gets its
roster from its approver during enrollment (§4.2).

**`roster.submit`** exists because the host may be the one that is behind. A device approved on a
branch the host missed, and refused here as unknown, may hold a removal the relay is withholding
from this host. Without a way in, the host's own app, equally uninformed, would keep it fresh and
the removed device usable even though an informed peer can reach it. So an unrostered peer may send
a chain of entries, which the host checks exactly as any offer (§3.5): signatures, validity, budgets.
Since every entry is self-authenticating, who sent it adds no trust. The path discloses nothing:
the answer is only `accepted` or `refused`, with no heads, counts or reasons, whatever the host
holds. Since a channel frame carries at most 65,519 bytes of plaintext (`SecureChannel`), a
submission is sent in **parts**, each one frame, carrying a submission id, its index and the
total. The host stages the parts and checks the submission only when all have arrived, as one
unit, so evidence still arrives together with its removal; it keeps at most one staged submission
per key, discards one left incomplete for 60 seconds, and refuses one over **16 MiB** of entry
sizes in total (§3.2). That holds any removal proof whose evidence holds fewer than about 160
removals padded to their limit: 1,281 entries of 8 KiB is about 10 MiB, plus the removal itself.
Evidence is signed by the target and its subtree, so a hostile target can pad about 160
over-budget removals of its own to 46 KiB each and push a proof past 16 MiB; such a removal is
then refused at that host through `roster.submit`, shown as
not confirmed there, and admitted by `devices sync` at the machine, which decision 9 states with
the other evidence refusals. Every single entry a host could accept by any path fits. It is bounded **per key**: one submission a minute from each key, so a flood from the
relay's own keys cannot crowd out an honest peer's, and at most 256 entries per submission besides
one removal's evidence. A submission may carry a complete removal proof: the removal, its missing
ancestors that are not evidence (at most 256), and the evidence §3.3 admits for it (at most 256
per signer, and never more than the host's remaining share of its 1,024), so at most 1,281
entries, checked together in ancestry order. Otherwise a removal needing 256 evidence entries
could not arrive at all: its ancestors qualify as evidence only through the removal, and the
removal needs its ancestors first. The host
checks submissions holding a removal it does not yet have before any others, and drops the
others first under load. A relay with many keys can still load the host: that is the
denial of
service §2 already grants it, and the honest peer retries. If the chain makes the sender active,
the channel continues as a
rostered one. A pending device has nothing valid to send, so it learns nothing and gains nothing.

For an active device the host also needs to be fresh (§6.3). Then:

| Op | Who |
|---|---|
| Session ops (`sessions.*`, `answer`, `reply`, `launch.*`, `stop`, `usage.*`, `push.*`, `blob`) | Any active device whose bound kind is a person's; hosts only for the host-to-host ops |
| Host-to-host (`usage.get` for `UsagePeers`, `session.transfer`) | An active `host`, with the target, source and `to` all active hosts |
| Person-only actions (below) | A **person command** (§7.2) |

The Noise intro's `kind` and presence's `kind` stay on the wire for old peers and for display,
and are never read for a decision once a host holds a roster.

### 7.2 Person commands

Bound kinds alone do not close #70 or #138 for a compromised machine. The app's channel key is
usable by any process of its user, so "the channel is from an active `app`" proves nothing about
a person. Revision 1 claimed it did; it does not. So every person-only action carries a **person
command**:

```json
{ "body": "eyJ2IjoxLC…", "sig": "MEUCIQ…", "uv": { "…": "…" } }
```

`body`: `{ v: 1, action, request, by, target: { machine, session?, question? }, to?, choice?,
nonce?, issuedAt, expiresAt, heads }`. `sig` is by `by`'s `sign` key over
`F("agent-bridge person command v1", bodyBytes)`, with `uv` over the same bytes where `by` has
`presence: webauthn`.

A host acts only if:
* the body parses strictly, and `sig` and the presence proof verify (`presence: none`, and a
  `webauthn` device recorded as backup-eligible, never pass under decision 3(a), §4.6);
* `by` is an active, non-probationary person's device, its kind read from the roster;
* `target.machine` is this host, and the action and its target match the op;
* the lifetime is at most 10 minutes, it was not issued more than 2 minutes in the host's future,
  and it has not expired;
* `request` was never used before, by a **durable** replay record. Today's `CommandMemory`
  (`src/AgentBridge.Host/SignedCommands.cs`) is in memory and recreated by `SessionTransfers`, so it
  forgets across a restart. R6 replaces it with a record in the host's private store, written and
  flushed **before** the action executes; an action whose record cannot be written does not run.
  Records are kept until `expiresAt` plus the 2-minute allowance has passed by the host's persisted
  wall-clock high-water mark, never by the current reading, and a command whose `expiresAt` is
  before that mark is refused. So a command used once, before a restart or a clock moved back,
  is refused after it. This matters most for transfers, which carry no nonce. The mark has one
  failure of its own: a wall clock that once ran far ahead pushes it into the future, and every
  person command is refused as expired until real time catches up. The host says so, and offers a
  **local reset** at the machine (in the app or `agent-bridge-host devices reset-clock`, refused
  inside agent sessions, #67). The host also persists the **highest `issuedAt` it has ever
  accepted**, in the same durable write as the replay record, before the action executes. The
  reset sets the mark to the current time and a floor at the later of the reset
  time and that highest `issuedAt`: a command issued at or before the floor is refused. Every
  command the host ever executed is issued at or before it, including one dated up to 2 minutes
  ahead whose record the far-ahead mark pruned early, so none can run twice. If the floor is in
  the future, because the host once accepted a command dated ahead, new commands wait until then,
  and the host shows when;
* where the command arrives on a channel from `by` itself (folder trust, update), `nonce` is one
  the host gave that channel in `hello`;
* the host is fresh (§6.3) and holds every head the command names, syncing first if not. The one
  exception is `attest` under `selfAttest` (§6.3), whose purpose is to refresh a stale host: it
  keeps every other check here, its nonce and its rule on heads, but not this one.

| Action | Today | With person commands |
|---|---|---|
| Transfer (`SessionTransfers.Unsigner`) | `CommandMac` by a device present as `pwa`/`app`/`phone` | A person command for the source, `to` the target. There is no nonce, since the source never talks to `by`; single use and the 10-minute lifetime stand in. The signer is still neither the asking host nor `to`, and is still in presence |
| Folder-trust answer (`FolderTrustAnswers`) | `CommandMac` by the peer, kind `pwa`/`app`, plus the allow-list | A person command with the host's nonce, plus the allow-list (§9.2) |
| Remote update (`UpdateService`) | Peer kind `pwa`/`app` | A person command with the host's nonce |
| Confirming an approval with escaped characters (`session.approve:escaped`, #836; `EscapedApprovals`) | `SignedCommands.Verify`, a MAC under the app's DH key, plus the presence kind and the `permissions.escapedApprovalDevices` / folder-trust allow-list. Same-user code holding that key can confirm with no person present | A person command, so the presence proof is required. #836's bindings are kept as they are: `request`, session, question and `shown` (what the person read must equal what the host would send now), the 5-minute limit (shorter than the 10 above) and every check at delivery time. The allow-list is pinned to `(device.id, add_entry_hash)` (§9.2) |
| Forget or Remove a machine (#138) | Relay checks the hello kind | Under decision 7(a), Remove is a roster entry (§6). Under 7(b) the relay requires a person command for `forget` |

`CommandMac` signed commands are retired for these once a host is in `enforce`. Each person-only
press now asks for the presence check: Face ID, the passkey prompt, or Hello. That is deliberate,
and acceptable for actions this rare.

### 7.3 What stays on channel identity

Session ops (reading, replies, answers, launches, stops) still rest on the channel's device key,
with one exception: an **approval with escaped characters** (§7.2) is a person command. Every other
answer, including an ordinary Approve, stays here. Session ops are frequent, and a presence check on every reply would be unusable. So malware or a
prompt-injected agent on a machine with the Windows app can, as that app, read and drive
sessions on the account's other machines, as it already can on its own machine. This is a
residual, stated plainly here and in design.md §5.6 when it is updated. Presence-proven launches
with Allow-all (design.md §5.6) and a per-session approval of replies are possible later
narrowings, not part of #55.

### 7.4 Clients

A person's device applies the roster from its side. It lists, opens channels to, and sends
replies, answers, attachments and person commands only to **active rostered hosts**. A host that
is not in the roster is shown as "Not approved" with an Approve action, never as one of the
account's machines. Otherwise a stolen token could register a host called "Desk" and collect what
the person types into it.

### 7.5 The local pipe, the HA connector and the relay

The roster governs the relay path (and the LAN path of
[design.md §5.2](../design.md#52-transport-ladder) when it exists). The local pipe keeps its
same-user rules ([local-pipe.md](../protocol/local-pipe.md)). The HA connector acts in-process for
Home Assistant users (§9.5). Neither is a roster device, and neither can change the roster. The
relay's checks (§4.5) narrow what a token alone reaches when the relay is honest; hosts and
clients never depend on them.

### 7.6 The token

Relay tokens become bound to the device key, as design.md §5.4 plans: a token names the device id
it was issued to, and admission refuses it with another key. A stolen token then admits only the
key it was issued with, so it cannot add pending devices or flood the approval banner. A new device
flow can still mint a token for a new key; the roster, not the token, is the gate.

That must not break today's Windows app. The app does not sign in itself: it takes the host's token
over the pipe (`host.relay.account`, [local-pipe.md](../protocol/local-pipe.md)) and presents it with
**its own** device key (`RelayFleetLink`). A host-bound token would refuse it. So binding comes with
a way for an app to get its own token, and is enforced last:

1. **Exchange.** The relay gains `token-exchange`, a control message accepted only **on an
   already-admitted socket**: the requester has just proven the key its token is bound to, so a
   token alone, without that key, can never mint another. On its socket, the host forwards the app's
   public key and the app's proof of it, an HMAC over a relay nonce from ECDH with the relay's
   ephemeral, as admission does, which the app makes over the pipe. The relay issues a token for the
   same account bound to the app's key, and the host returns it over the pipe through a new
   `host.relay.token` op, limited to the app as `host.manage` is. The app never presents the host's
   token itself. A compromised host can mint tokens for keys of its choosing, but those keys are
   still only pending in the roster.
2. **Update clients, still unbound.** The relay keeps issuing **unbound** tokens while hosts and
   apps update and the app switches to the exchange. Issuing bound ones any earlier would break an
   old app: its host signs in, gets a token bound to the host's key, passes it over the pipe, and
   the relay refuses it with the app's key. "Accepting both" does not help, since that token is
   bound. The step ends when the relay's own admission log shows every app of the account using an
   exchanged token, which the owner checks.
3. **Issue bound, accept both.** Only then does the relay issue bound tokens, and it still accepts
   unbound ones until they expire. An app that still takes a host token after this (one restored
   from an old backup, say) is refused with a reason that says to update, rather than failing
   silently.
4. **Enforce.** Once every client the owner runs has updated (apps update within 30 minutes; the
   PWA and Apple clients sign in with their own key anyway) and unbound tokens have expired, the
   relay refuses unbound tokens.

Each step is a separate relay configuration change, and production steps are the owner's to
approve.

### 7.7 Remote management (`manage.*`, #821, #886)

#886 added remote restart and hook repair, and #821 specified more: Stop, config and token
operations under three grants, `manage.lifecycle`, `manage.config` and `manage.secrets`, with
command-bound config arguments, a token digest, and a fresh unlock for each, Stop included
(Q-O10). Grants are deferred (§3.2, `op` row), so **#55 does not deliver any of that**. What it
does deliver is the seam, and this section says which side of it each operation is on.

| Operation | Before #55 | When the roster is in `enforce` |
|---|---|---|
| Restart, hook repair (#886) | `permissions.remoteManage`, **on by default** (owner decision Q-O11, 6689873359), on a self-declared peer kind. Its safeguards stay: the off switch, at most 2 in 6 hours, waiting for quiet, owned hooks only, and the notice to other devices | A person command (§7.2) from an active person's device, with the host's nonce, in place of the declared kind. The default stays on and so do the safeguards. The command adds a presence proof; it does not make the operation opt-in |
| Stop (Q-O10) | Not remote. Stays at the machine | `needs-roster`: a person command with presence proven for that command, no reuse of an earlier unlock. Not part of #55 |
| Config (`manage.config`) | Not remote | `needs-roster`: a person command whose `choice` binds the exact key and value, so a command for one setting cannot carry another. Not part of #55 |
| Token (`manage.secrets`) | Not remote | `needs-roster`: a person command that binds the digest of the token it concerns. Not part of #55 |

Stop, config and token stay `needs-roster` until a named integration slice (not yet opened;
it depends on R6) gives them: the three grants as roster entries, a person command per operation
carrying its payload (config key and value, token digest) and a `request` the durable replay
record (§7.2) refuses a second time, and revocation, meaning a grant ends with the device's
removal at once and a grant not active in the roster is ignored. Until then no host exposes
them, and nothing here is a reason to expose them early.

Restart and repair move onto person commands in R6 with the other person-only actions, and only
in `enforce`. Before that they run as today, on a kind the peer declares. That is the residual
the owner accepted with the default, and it is not reduced by this design before R6.

## 8. Migration and rollout

### 8.1 Modes

A host and a client each have a roster mode, set locally (host: `security.roster`, changed only by
`agent-bridge-host config` or the app, both #67-guarded):

| Mode | Behaviour |
|---|---|
| `off` | Today's behaviour. The default until the cutover |
| `observe` | Syncs and verifies the roster, and logs and reports (`host.status`, app banner) every op it **would** refuse, and why, including stale and person-command failures, but refuses nothing |
| `enforce` | §7. With no roster, a host in `enforce` refuses every relay device except to say "approve this machine" |

### 8.2 The cutover for the owner's devices

Existing devices are **grandfathered by an explicit approval, never silently**. Decided (owner
decision 6(a)): every existing device enrolls with the mutual codes, once, as a new one would:

1. Release the relay, then hosts and clients with roster support in `off`, then `observe` (§8.3).
2. The owner starts the roster on a device that can approve (§5.1): under decision 3(a), the first
   presence-proven device available: an iPhone or iPad running the Apple client when one is
   available (preferred, §5.4), otherwise the Windows app on one machine once R4 has proven it.
3. For each other machine, its Windows app shows code 1 and the owner types it on the approving
   device; that device shows code 2 and the owner types it into the app, after checking the
   fingerprint. One exchange
   covers the app and the host there (§4.7). Then any other PWAs, and the Apple clients when they
   ship. Each machine starts its 72-hour probation, which only limits what it can approve. After it,
   the approving (genesis) device offers "Confirm" for each, so the approving devices among them can later cosign
   the removal of a lost senior device (§3.4).
4. Every host in `observe` now reports what it would refuse, and the relay, still in its own
   `observe` mode since step 2 (§4.5), lists which devices it would treat as pending. Until here no
   device has lost anything at the relay. After a quiet day, the owner switches each host to
   `enforce` (in the app's Settings, or with
   `agent-bridge-host config set security.roster enforce` at the machine). The owner also switches
   **every person's device** to `enforce` in its own Settings (each Windows app, the PWA, the Apple
   clients), since a client's mode is separate from its machine's: a client left in `observe`
   refuses nothing, and a dishonest relay could still show it a fake host called "Desk" and collect
   replies and attachments (§7.4). The Devices page lists any of the owner's devices still in `off`
   or `observe`. Then, once it lists no unapproved device the owner still uses, the owner switches
   the relay to `enforce` there ("Restrict unapproved devices"), which cannot be undone short of a
   reset.
5. A later release makes `enforce` the default for new installs of hosts and clients: a new
   machine serves its own apps until approved, a new client talks only to rostered hosts once it
   holds a roster, and a genesis on an account the relay has never seen another device of starts
   the relay in `enforce` too.

About six exchanges for today's fleet. The lighter alternative, decision 6(b), is a one-time
**bulk grandfather** on the genesis device. It lists the devices online at that moment, each with
its kind and its **descriptor fingerprint**, in groups of four. The id alone is not enough: it is
a hash of `dh` only, so a dishonest relay could keep a genuine device's id while substituting its
own `sign` key and presence declaration. The fingerprint is the first 128 bits of
`SHA-256(F("agent-bridge device descriptor v1", dh, sign, signAlg, presence, webauthn rpId,
credential id and key, attested, backupEligible, kind, platform))`, with empty fields for what a host lacks, so it
covers every field of the `add` that matters for a decision. In particular a relay cannot relabel
a desktop PWA as `android`, or change its backup-eligible flag, so that the approver records
presence it should not (§4.6). Each device computes and shows its own locally, from its own keys.
The owner ticks
each one after comparing the whole fingerprint with the one that device shows (a prefix is not
enough), and the genesis device signs `add` entries with `approval.method: "grandfather"` for
exactly the descriptor it showed. Nothing is pre-ticked, including ids from
`launch.remoteFolderTrustDevices`: the names beside them came from the relay and can be look-alikes
(#157). This path has no mutual check. A device that came from a stolen token, was online at that
moment and is named like one of the owner's is caught only by the fingerprint comparison. A grandfathered
machine also never checked which roster it joined, so each machine must still confirm the genesis
fingerprint locally (in the app or the CLI) before it holds the roster.

### 8.3 Order of releases

1. **Relay** (staging, then production **only with the owner's approval** of the `relay.yml`
   production job): roster storage and appends (§3.6), approved/pending admission with invite
   hints, nothing for pending devices, bound kinds in `presence`, the generation rules (§5.6), and
   `token-exchange` while it still issues only **unbound** tokens, and the `removed` close code.
   Issuing bound tokens waits until every app uses the exchange, and refusing unbound ones comes
   last (§7.6). Additive: an old client ignores new fields, and the
   relay keeps today's behaviour for an account with no roster, or whose relay mode is still
   `observe` (§4.5).
2. **Hosts** (Windows, then the Mac host), shipping in `off`: the verifier and state, gossip,
   freshness, person commands, `roster.*` ops, enrollment through the pipe and the CLI, `observe`
   and `enforce`.
3. **PWA** with its passkey: join, approve, Devices, remove, person commands. Under decision 3(a)
   it approves only when recorded with `presence: webauthn`, which in v1 needs a device-bound
   credential on Android, or on Windows once R4 has proven it; an iOS or iPadOS PWA never qualifies, since its passkeys are synced (§4.6).
4. **Windows app**: join, Devices, and the client rule (§7.4); approving and person commands only
   after R4's proof of presence. For this fleet it is likely the first device that can approve, so
   R4's proof comes before the cutover (R8).
5. **Apple clients**: join first, then approve. They must ship join before any host the owner uses
   them with is in `enforce`.
6. **The owner's cutover** (§8.2), per host.
7. **Default `enforce`** for new installs.

### 8.4 Old and new together

| Combination | Outcome |
|---|---|
| Old client, enforcing host | The old client's `ReadIntro` requires `name` and `kind`, so it fails before any gate runs, never reaching `not-approved`, on every XX handshake (message 2 is empty) and on IK when its key is not active (the reply is empty), and shows its usual failure to reach the machine. With an active key and the host's key still in its IK cache (`pwa/js/channels.js`, the app's `Machines`), it gets the full IK reply (§7.1) and keeps working over IK until its first XX fallback, which then fails. So a client must be updated before any host it talks to enforces. Every device §8.2 enrolls already has roster support, since joining needs it (§8.3 ships join first); this row is a copy left on an old version, such as a PWA tab not reloaded or an app not yet updated. Windows apps update within 30 minutes, and the PWA on reload |
| New client, old host | The client sees no heads in `hello`. In `observe` it warns; in `enforce` it refuses to send, saying "update this machine". Old hosts update with their app |
| New client, old relay | No roster store, so enrollment is unavailable and says why. Devices that hold a roster keep enforcing it and gossip |
| New host, old relay | The same: a host in `enforce` keeps its roster and syncs from peers. Freshness still works through peers and the pipe |
| agent-ha-bridge | Unaffected: nothing in it reads the relay, and the HA connector's contract is unchanged |

## 9. Effect on other work

### 9.1 Transfer (#70)

Transfers need a person command (§7.2) from an active, presence-proven person's device, and an
active host as the asker, `to` and the source. This closes the gap #70 records for a compromised
machine **only for devices with a presence proof**. A device with `presence: none` cannot sign
one under decision 3(a); option 3(b), which would change that, is not specified (§4.6). The "What this does not stop
yet" paragraphs of [v1-transfer.md](../protocol/v1-transfer.md) and
[host-transfer.md](../host-transfer.md) are rewritten in R6 to say exactly that.

### 9.2 Folder trust (#133, #157)

A folder-trust answer needs a person command with the host's nonce. `launch.remoteFolderTrustDevices`
stays as a per-machine choice of which approved devices may answer. Each choice records the device
id **and the entry hash of the add that made it active** when it was allowed. An id there that is
not active in the roster is ignored and shown as such, so removal revokes folder trust at once.
So is one whose active add is now a different one: a hostile senior that displaces the device
with a copy of its key (§3.3) keeps the id active under its own signing key, and same-user code
holding the device's DH key could otherwise answer folder trust under the copy's key, without the
original device's presence check. The person allows it again at the machine. `permissions.escapedApprovalDevices` is the same list
under the same rule: each entry is `(device.id, add_entry_hash)`, one that does not match the
active add is ignored and shown as such, and escaped-approval confirmation is a person command
(§7.2), so neither the list nor a key copy substitutes for presence. Settings shows
each device's roster name, kind, 16 characters of its id, by whom it was added and when this machine first saw it (never the signer's `at`), which
closes #157 item 4. Items 1 and 2 of #157 (tightening only after a restart, terminal writes past the
lock) are unaffected and still open.

### 9.3 Forget (#138)

Owner decision 7 was whether Forget on a host becomes Remove:
* **Remove revokes and forgets** (decided, 7(a)). It is a roster entry with a presence proof (§6),
  and the relay also drops the machine from `known`. A machine that is gone, perhaps with its disk,
  is revoked too. If it connects again it is listed as waiting for approval, and one exchange brings
  it back. agent-ha-bridge let a forgotten machine come back by itself; this is a deliberate
  difference, to be recorded in [parity.md](../parity.md).
* **Keep both**: Forget stays listing-only (parity), and the relay requires a person command for it.
  Remove is a separate action on the Devices page.

### 9.4 Remote update (#161)

Install and Check from a device need a person command with the host's nonce. The machine's own
Settings and the HA connector's Install button are local paths and are unaffected.

### 9.5 HA connector

Unchanged. It runs in the host for Home Assistant users and reaches the same services the relay path
does, guarded by HA credentials, as agent-ha-bridge was. It is not a roster device, it shows no
roster controls, and nothing it does changes the roster. On a machine with the connector on, HA
credentials stay a second way in, by design; R6 adds a note saying so to
[ha-connector.md](../ha-connector.md).

### 9.6 Apple clients (#260, #358)

They enroll like any other client. Each must implement:
* the signing key (Secure Enclave `P256.Signing`), created at first run beside the device key. On
  iPhone and iPad it uses `.userPresence`, so `presence: key`. On a Mac, `.userPresence` falls back
  to the login password in a dialog same-user code may reach, so the Mac app declares `key` only
  for a key made with `.biometryCurrentSet` and no password fallback (Touch ID only), and otherwise
  `none` until L1 proves the macOS prompts (§4.6). Without a Secure Enclave, a Keychain key,
  `presence: none`;
* strict parsing, the roster state of §3.5 (seniority, probation holds, voiding), gossip in `hello`,
  head attestations, and the shared vectors;
* **join**: the pending screen, both codes, the genesis fingerprint, the invite hint and both QR
  directions, and verifying the returned roster. This is needed before any host they talk to
  enforces;
* **approve**, **Devices** (remove, cascade, cosign) and **person commands** (transfer, folder trust,
  update), each behind Face ID or Touch ID, and on a Mac only when its key is Touch-ID-only;
* the client rule of §7.4, and the bound kind for what they show.

The iPhone and iPad client already connects as `phone` and the Mac app as `app`, which are the bound
kinds they get. The Mac host enrolls with the Mac app as in §4.7, or by its CLI.

## 10. Tests

Each slice adds its own. Together they cover:

**Acceptance (the issue)**
* A device of the account that is not in the roster is refused every op but `hello` and the
  write-only `roster.submit` on a host in `enforce`, and learns only that it is pending: `hello`
  says `approved: false` and nothing else, and `roster.submit` answers only `accepted` or
  `refused` (§7.1).
* An old client (no roster support) opening to a host in `enforce` fails the handshake on the
  empty intro over XX, whether or not its key is rostered, and over IK when its key is not active,
  and shows its usual failure to reach the machine. With an active key and the host's key in its
  IK cache it opens over IK and is served, until its first XX fallback, which fails.
* A device added with the mutual codes on the PWA (or an Apple client, or the Windows app after R4)
  is served by every host, including one that was offline during the approval.
* A removed device is refused by every host, including one that was offline at the removal.
* **Relay mode.** A genesis on an account with existing devices leaves the relay in `observe`: an
  app and a host not yet enrolled keep their presence, channels, pushes and blobs, and appear as
  pending on the Devices page. After a person's device switches the relay to `enforce`, they get
  only `pending: true`. A request to move back is refused; only a reset or the administration path
  does. A genesis on an account whose relay has admitted no other device starts in `enforce`.
* In `enforce`, a host's Noise message 2 carries an empty intro, as does its message 3 to a
  responder not in its roster: an unrostered initiator or responder never learns the machine's
  name or kind. An active device gets them in the `hello` answer.
* **IK.** In `enforce`, a host answering a Noise IK first message from a key not active in its
  roster replies with exactly `{}` and runs no handler, whether the first message asked an early
  `hello` or nothing early; that key's `hello` once open gets `approved: false` only. The same
  holds for a replay of an active device's first message received after its removal. So an
  unrostered device that learned the host's key over XX and opens with IK learns neither the
  machine's name nor its kind. An active device's IK opening gets the intro, with its early
  `hello` answered as one sent once open. A device with roster support opening IK to a key not
  active in its own roster sends `{}` and no early request. In `observe`, the reply is as today,
  and the host reports the opening it would have answered with `{}`.
* **What an active device still learns.** An active device whose only handshake with a host so
  far was XX in `enforce` (an empty message 2) opens its next channel to that host with IK, and
  sends it deflated frames: its approved `hello` carried `transport`. An unrostered device's
  `hello` carries no `transport`, and its message 2 holds no `ik`. Two other signals still tell an
  unrostered opener that the peer is a host, and are accepted in this phase: that it answers
  `hello` at all (`approved: false`, where an app's hub answers `unknown op hello`), and relay
  presence while the relay is still in `observe` (§8.2 step 4).

**Enrollment**
* A relay that substitutes keys on either leg: code 1 fails at A and code 2 fails at N. A grinding
  test shows a man in the middle that must commit first matches at the expected rate.
* **A relay acting as its own approver** (its own genesis, an honest exchange with a pending host):
  the code 2 the person reads off their real approver is refused at N, and N never holds the
  relay's roster. The same holds when N shows the QR and a device other than the scanner approves.
* The QR paths: a wrong or missing approver HMAC is refused at N, and a wrong joiner HMAC at A. A
  pending device never opens a channel; A opens one only to a matching invite hint.
* Lockouts at A and at N; an expired attempt; no log line holds a code, nonce or `q`.
* N refuses a roster whose newest entry is not its own keys, kind and presence, is signed by anyone
  but its peer, or carries another transcript. A machine shows the genesis fingerprint before
  code 2.
* Genesis is never made without `NEW`, and a host never trusts a roster it was not enrolled into.

**Tampering and parsing**
* One byte of a body changed; a signature by a key that is not the signer's; a signature over the
  raw body rather than `F("agent-bridge roster entry v1", …)`; an entry signed by a host, or by a
  device that is not active in the entry's parents' state (removed there, or never added); one that
  needs presence but lacks it, or whose `uv` has no UV flag, the wrong challenge, origin or rpId:
  refused. A probationary signer is not refused: its entry is held (below).
* Another account or generation; a wrong `seq`; a `prev` out of head order or unknown; a re-added
  removed id; a second genesis: refused. A valid removal that leaves no person's device is
  accepted (below), however an honest client never offers it.
* Negative parser vectors (shared by C#, JS and Swift): a duplicate member, an unknown field, a
  missing field, a field from another `op`, a fractional, exponent, negative or string `seq`, wrong
  case in a name or value, invalid UTF-8, a lone surrogate, base64 with padding or the wrong length,
  and an id in uppercase. Each is refused.
* Bulk grandfather: a relay that keeps a genuine device's id but substitutes its `sign` key,
  presence declaration, WebAuthn `rpId` or credential, or relabels its `platform` (a desktop PWA
  as `android`) changes the descriptor fingerprint, and the comparison fails.
* `approval` vectors: `code` without `transcript`, `qr` without `shown` or with another value,
  `grandfather` or `genesis` with a `transcript`, an extra member, or `genesis` on an `add`:
  refused. Each valid member set is accepted.
* An off-curve or malformed key; a high-S or non-minimal DER signature: refused. A second valid copy
  of an entry with another `sig` is the same entry, not a fork.

**Seniority, probation and forks**
* A `remove` of a device that is not junior to the signer, without `cosig`, is refused. With a
  cosig from a second device whose lineage of add entries, cut at the target's add, is disjoint
  from the first's, and both signers confirmed in the removal's parents' state, it stands.
  Cosigners that are parent and child, siblings (both added by one add left after the cut) or
  cousins are refused. Two devices the target approved and confirmed itself are accepted. A
  fabricated copy of a device's key, made by another signer, has its own lineage and does not
  stand in for the real device as a cosigner.
* **Unconfirmed devices cannot take over.** G approves A and B; within seconds A and B cosign
  removals of G and of every other device not junior to them: every one is invalid and refused,
  in every delivery order, whatever any clock says, because A and B are not confirmed. A
  removal by A alone of a device junior to A applies at once.
* **Confirmation cannot be manufactured.** G approves and confirms H (one mistake), then approves
  B on its own; H confirms B at once: refused, since H is not B's approver. H and B cannot cosign
  G's removal until G itself confirms B. The same when B was approved by a device junior to H.
* `confirm` vectors: by a device that is not the target's approver (a senior device included), by
  an unconfirmed device, of a device
  already confirmed there, or naming an add that is not active: refused. A void `confirm` (its
  signer removed concurrently) confirms nothing, and a cosigned removal that was valid when signed
  stays applied. A verifier that holds a `confirm` of a device ends that device's hold at once.
* A removal cosigned by two `presence: webauthn` devices carries two assertions, one per role, each
  verified under its own credential. One assertion, or two from one credential, is refused.
* A probationary device's add or rename is held until 72 hours of the verifier's persisted
  monotonic running time after it first accepted that device's `add`, whatever `at` says. Moving
  the wall clock forward a week, or back, changes nothing; a verifier switched off for a week holds
  for 72 running hours. A removal of that device applies at once.
* A probationary device's removal of a device junior to it is **not** held: it applies at once. A cosigned removal in which either signer is unconfirmed is not held either: it is invalid.
* A host back after six weeks offline receives the cosigned removal of a stolen genesis device,
  signed by two confirmed devices it sees as new: it applies the removal at once and stops serving the genesis
  device. It does not hold those two devices' adds either, since it holds their `confirm`s; it would hold the adds of a device it sees as new and unconfirmed, unless its own app
  reports earlier times.
* A device enrolled through an established, non-genesis approver adopts that approver's remaining
  probation for each device, so established devices are not held.
* A removed device signs an `add` on a head from before its removal: it is void in the merged state,
  and so is everything the device it added signed, transitively.
* Removals in the shared vectors (monotone, so the same result in every delivery order):
  * a host that applied B removing C, then receives a concurrent A removing B: C stays removed;
  * two branches where each device removes the other: both go;
  * the three-branch cycle of §3.5 (A removes B, B removes C, C and D remove A): A, B and C go,
    D stays;
  * the last two person's devices removing themselves concurrently: no person's device is left,
    and every host refuses relay devices until a reset;
  * a removed device's removal signed on an old head, of a device junior to it that was active
    there: it applies, and it cannot touch a device added later;
  * two independently approved devices, both confirmed, cosign a senior device's removal, and the
    person removes both: their concurrent adds are void, and the senior device's removal applies
    at once and stays applied (the stated residual);
  * **a voided merge cannot undo a removal.** A forced far fork quarantines cosigner P's add; P and
    K remove the stolen senior S; W's merge adopts that branch, so the removal applies. S then
    signs `remove(W)` on a head from before its removal. W is revoked, its merge is not void, the
    branch stays adopted and S stays removed, in every delivery order and in every intermediate
    state;
  * **a removed device's old-head removal does not reach past its removal.** S is removed by C1
    and C2, who then enroll replacements R1 and R2. S, on an old head, removes C1 and C2. C1 and C2
    are revoked; R1, R2 and everything they add stay active. With S as genesis, the generation
    continues through R1 and R2;
  * two adds of one id on concurrent branches by signers neither of which is senior to the other's
    device: the id is conflicted and inactive, whichever arrived first;
  * G adds A and H on sibling branches, A adds X, and H adds X's public key on its own branch: X
    is conflicted and the conflict names H; G removes H, which takes the copy, and X
    is active again, in every delivery order, with no new enrollment;
  * genesis adds A and H on concurrent branches; H later adds A's public key on its own branch,
    where A is absent: H's add is subordinate and A stays active, in both delivery orders;
  * a relay hides newer heads from an established approver, which enrolls N on a branch at a
    lower `seq` than H, enrolled earlier on another: N is not senior to H and cannot remove it
    alone, even after probation and a merge;
  * A and B add one key X on concurrent branches, C active where both were made; C removes A's add
    before learning of B's, and a removal of A concurrent with A's add then arrives: X stays
    revoked (C's removal bars B's add), in every delivery order. A removal of C concurrent with
    C's removal of A's add, delivered first, in between or last, changes nothing: every
    intermediate state keeps X inactive. When C was not active where B approved X, X is active
    through B and shown as such;
  * a fabrication through a device H added: removed H adds J on an old head, J adds a
    replacement device's
    real key and removes that add, the replacement's real approver having joined after H's saved
    head. Nothing is barred, and the real device is active once its hold ends;
  * H adds A's public key with H's own `sign` key on a branch where A is absent, and signs 64
    removals under it: they count against that fabricated signer, and the real A can still sign
    its own 64;
  * two established devices rename one junior device on sibling branches: the rename with the
    smaller entry hash wins in both delivery orders and at every verifier, and a later rename that
    descends from both supersedes it;
  * H adds A's public key on its own branch where A is absent (A and H concurrent) and renames
    that copy eight times: after the merge the copy is subordinate, A's name is unchanged, and A
    can still be renamed eight times;
  * the genesis approves and later confirms A and B itself, and is lost: A and B together remove
    it (their lineages meet only at the genesis, which the cut removes). Lost before confirming
    them: they cannot, and it is a reset;
  * the genesis device removes itself: it is revoked, its grants that are not ancestors of the
    removal are void, its merges are not, the genesis entry is not, and the removal applies;
  * a host holds a hostile approver's 64 sibling adds; another device removes it on a 65th: the
    65th is kept as evidence, never applied, and the removal applies;
  * a probationary device signs 5,000 over-budget adds and then removes itself: the self-removal
    brings no evidence and is refused at a host that lacks them; no host fails closed or stops
    serving other devices. A removal of it by another device brings at most 256 of its entries,
    and one needing more is refused and shown as unconfirmed on the remover's Devices page;
  * removals of one target forking from two incomparable points, each concurrent with a different
    add by the target, delivered in both orders: both are kept and both adds are void;
  * a removed H that makes `add(N)` with a replacement device's real key on an old head and then
    removes that add: H's add is void and its removal inert, and the replacement device stays
    active, whichever arrives first; the ids are not conflicted;
  * a probationary B makes a held add of C; the remover's device has synced that add before
    removing B and does not keep C: the removal takes C, C is revoked and everything C signed is
    void, even though the add is an ancestor of the removal. Kept, C stays held and takes effect
    when B's hold would have ended;
  * a `keep` list longer than 512, out of order or with a repeated hash; a kept add that is not
    an ancestor of the removal, not in the target's subtree, or signed by a device that is neither
    the target nor kept; a target `add` that is not that id's add, or not active in the parents'
    state (a subordinate add, or one of a conflicted id's adds: removing its approver takes it
    instead); a removal other than a self-removal signed by its target, or by a device of the
    target's subtree that it does not keep; a self-removal with a non-empty `keep` or a
    `cosigner`; a cosigned removal whose signers fail the
    cosign test, even for a target junior to `by` (there are no cosigned removals of rank 1); a
    `targets` or `voidGrants` member (not part of v1); a removal over 46 KiB or any other entry
    over 8 KiB of fixed content, such as one padded with JSON whitespace, at the relay, by gossip,
    at enrollment and through `roster.submit` alike: refused. A `remove` of exactly 46 KiB in its
    canonical wrapper fits one channel frame, and travels in one `roster.submit` part, one gossip
    message and one enrollment frame. An entry within the limits that arrives in a wrapper padded
    with whitespace, where it fits a frame, is accepted, since size is measured over fixed content,
    and whoever forwards it re-emits the wrapper canonically. An assertion whose signature is not
    minimal DER, as a relay could make one by re-encoding an honest entry, or is high-S, as anyone
    could make one from an honest low-S signature without the key: refused. A signer's client
    whose authenticator returns a high-S assertion signature normalizes it to low-S, and the entry
    verifies everywhere.
* Caps and budgets: a roster at its 4,096-entry limit (filled with renames by one device) still
  accepts a removal of that device. The ninth rename of one device, a signer's 65th add, merge or
  removal, and the 513th add of a generation are refused, counted across every branch and
  including quarantined and void entries. One approver that signs removals Rᵢ of a junior C and
  then removals Sᵢ of a junior B on top of each, from one head, stops at 64 removals.
* A removal or merge signed by a device whose add is quarantined at a verifier is valid there by
  its history state, and kept: the removal applies, and the merge adopts, when that branch is
  adopted. Neither is refused as signed by an inactive device.
* Quarantine is inherited: an established signer's quarantined rename on a far fork, followed by
  an add on top of it, leaves both quarantined and the added device inactive until a merge adopts
  the branch. A removal on that branch that is valid from unquarantined entries alone applies.
* After C1 and C2 remove the genesis G and enroll replacements R1 and R2, G removes itself (rank
  1) on the entry right after genesis: G is revoked; G's adds of C1 and C2 are ancestors of the
  removal of G that C1 and C2 signed, so the self-removal does not void them; R1, R2 and
  everything they add stay active, and the generation continues through them. The same when the
  self-removing device is a non-genesis senior device S that approved C1 and C2.
* **A removal by fabricated devices cannot outrank the person's.** G enrolls and confirms S, which
  is stolen. The person, on G, removes S (rank 1). From the head before that removal, S enrolls
  and confirms J, K and Y, and J and K cosign a removal of S (rank 2). J's, K's and Y's adds are
  S's grants concurrent with the person's removal, so void; J's and K's removal revokes no signer
  of G's, so it shields nothing: only G is active, in every delivery order.
* **Fabricated cosigners of a non-genesis device.** G enrolls and confirms D, which is stolen. D
  adds M (never confirmed), J and K, and confirms J and K; J and K cosign the removal of D (rank
  2), concurrently with the person's removal of D, which keeps nothing and so takes M, J and K
  with D. M, J and K are revoked,
  nothing they sign afterwards is active, and D is revoked, in every delivery order. The same when
  D signs all of it on an old head from before G's removal: then M, J and K are D's concurrent
  grants and void.
* **Fabricated ancestors, fork before the recovery.** G approves and confirms S, then approves C1
  and C2 directly. Stolen S adds and confirms J and K, and G confirms C1 and C2 on the relay's
  line after them. On a withheld branch from before the recovery, J and K cosign removals of C1
  and C2 (rank 2), and J enrolls and confirms Y. With the previous revision's separate removals
  (C1 and C2 remove S, rank 2, then J and K one by one, rank 1), the active set was {G, Y}: Y
  survived and F, which C1 enrolled after the recovery, was void. Now the recovery is one removal
  M of S, cosigned by C1 and C2 (rank 2), which takes J and K with S. J's and K's removal yields
  to M (they are in S's subtree; neither C1 nor C2 is in C1's or C2's), so it shields nothing and
  M shields F:
  the active set is {G, F}, with C1 and C2 revoked by the withheld removals, in every delivery
  order.
* **Fabricated ancestors, fork from the recovery.** The same, but J and K sign their removals on a
  fork from M itself. M takes them, so they are inactive in every state that holds it: their
  removals are invalid, C1 and C2 stay active, Y is invalid, and the active set is {G, C1, C2,
  F}.
* The same, but G alone removes S, taking J and K (rank 1): Y is void (J's removal revokes no signer of
  G's, so it cannot shield against M); J's and K's rank-2 removal outranks M, so what C1 did after
  M is void with C1. G is unaffected. With C2 as cosigner, the recovery ranks 2 and the outcome is
  {G, F} as above, which is why the client asks for one.
* **A remove(P) after syncing.** S approved P, P approved C1, and J and K are ancestors of the
  recovery M, a removal of S that C1 and C2 sign keeping P and C1, and so taking J and K. J and K,
  after syncing M, cosign a removal of P: invalid, since M takes them. On a fork from before M,
  after C1's add, the same removal takes P and C1: C1's approver P is junior to S, so it is the
  tie through P below. P and C1 are revoked, what C1 enrolls after M is void, C2 and what it
  enrolls survive, and anything J or K adds there is void.
* **A partial cascade with a fabricated pair.** Stolen D adds J and K on the main line; E, which D
  enrolled before the theft, joins heads and enrolls E2, whose add descends from J and K. The
  person's one removal of D keeps E and E2, and so takes J and K. On a fork from before it, D confirms J
  and K and adds Y, and J and K cosign a removal of D (rank 2). Y and D's confirms of J and K are
  D's concurrent grants and void; J's and K's removal revokes no signer of the person's, so it
  shields nothing; E and E2 stay active.
* **A quarantined descendant does not block the recovery.** At head P the roster holds G, S, and
  C1 and C2, which G approved and confirmed independently of S. The honest line advances 9
  entries; S then submits `add(J)` on P, which every verifier quarantines. C1 and C2 remove S,
  keeping nothing, on the smallest unquarantined candidate: the removal is valid although J is
  under no candidate, S is revoked, J is void as S's grant concurrent with it, and a later Tidy
  adopting J's branch leaves J void.
* **Two adds of one id.** S adds J; J adds X (`aJ`); S adds X's key itself (`aS`) on a parent
  from before J's add, within the window; J confirms `aJ` while it is still active on J's branch,
  and the heads are then joined: `aS` wins and `aJ` is subordinate. The same for K and X2. X's and
  X2's removal below forks from J's and K's branches after those confirmations and before the
  join, where `aJ` and X2's counterpart are active and confirmed. C1 and
  C2, which G approved, remove S (rank 2), keeping
  nothing, on a parent that holds both adds of each id: the removal takes `aS`, J, `aJ`, K and
  both adds of X2, whatever their state, so X and X2 have no active add in any state that holds
  it, and no other add of their ids by S's subtree applies there. X's and X2's rank-2 removal of
  C1 and C2 on a fork from before it yields to it: C1 and C2 are revoked, what they enrolled after
  removing S stands, and the active set is G and those devices, in every delivery order. The
  previous revision, which could name only one add per id, left `aJ`
  active under the thief's keys.
* **Two adds of one id, conflicted.** As above, but J and K each add X's key, neither senior to
  the other's add, so X is conflicted and neither add can be targeted. The removal of S takes
  both. S's later rank-1 removal of K on an old head changes nothing: X stays inactive.
* **Kept devices cannot shield the thief.** G approves S, then P; S approves E1 and E2, P approves
  C1, and G approves C2; all are confirmed. On a branch from before C1's add, E1 and E2 cosign the
  removal of P (rank 2: both are junior to P, and independent). Concurrently, C1 and C2 remove S,
  keeping E1 and E2, and C1 then enrolls F. The thief adds Y through S on top of the removal of
  P. That removal voids C1, P's grant concurrent with it, and neither removal yields. Under the
  previous rule 3 the person's removal revoked neither E1 nor E2, so the removal of P shielded Y,
  and the active set was {G, E1, E2, C2, Y}. Now E1 and E2 are in the subtree of S, the other
  removal's target, so the removal of P shields nothing: Y is void, as S's grant concurrent with
  the removal of S, and F is void with C1, whose approver P the person's own devices removed.
  The active set is {G, E1, E2, C2}, in every delivery order.
* **A thief-initiated recovery.** J, made and confirmed by stolen S and named "Dan's iPad", builds
  a removal of S keeping J and Y, which J added, and asks C2 to cosign. C2's client shows the
  keep list with each device's approver and when C2 first saw it, and asks when the person last
  had S. J, a signer from S's subtree, and Y, which C2 first saw after that time, each need a
  double confirmation; E, which S approved and C2 saw before that time, if kept too, needs one
  acknowledgement in the list. C2 refuses to sign until all are given. Cosigned
  anyway, the removal is valid, ranks 2 and keeps J and Y; the person then removes J, with C1 and
  C2 cosigning, which takes Y.
* **A copy cannot be kept through its subtree.** S approves J and E1 on concurrent branches, and
  E1 approves E2. On J's branch, where E2's add is absent, J signs a copy of E2's key carrying
  E2's `dh` and `sign`, and E2's device, shown only that branch, enrolls E3 there, whose signer is
  the copy. The heads are then joined: neither E1 nor J is senior to the other's add of E2, so E2
  is conflicted (J, whose approver S is senior to any copy J signs, could never displace a device
  S approved directly). Unticking E3 in the cascade does not untick the copy or J: the client
  cannot keep E3, and a removal keeping E3, the copy, or E2's genuine add (its id has two adds)
  is refused at every verifier. The removal of S, keeping E1, takes J, the copy, E2 and E3, which
  are enrolled again with new keys, and every device E2 enrolled through its genuine add.
* **A copy kept on a branch that cannot see the original.** G adds H, then A; A enrolls X; on a
  branch from before A's add, H signs a copy of X's key. G removes H on that branch keeping the
  copy, which its history shows as X's only add, so the removal is valid. Once the branches join,
  the kept copy may not displace A's add, made outside H's subtree, and A may not displace the
  copy, so X is conflicted: inactive, never active under H's keys, announced, and enrolled again
  with a new key. The keep set and everything the removal revokes are unchanged, in every
  delivery order.
* **A copy cannot un-keep a signer.** G approves C2, S and then P; P approves C1; S is stolen.
  C1 and C2 remove P keeping C1 (R), then remove S keeping nothing (M). On a saved branch from R
  that leaves out M, S adds Y; S also supplies W, a copy of C1's key on a parent from before P's
  add, with an adopting merge. W is S's grant concurrent with M and void, so it is in no conflict
  set; R still keeps C1, revokes no signer of M, and shields nothing. Y is void and the active set
  is {G, C1, C2}. Under the previous revision's late un-keeping, W made R revoke C1 and shield Y:
  {G, C2, Y}.
* **A removed device's copy reaches nothing.** A device removed by M0, outside S's subtree, signs
  an add of E's key on an old head, where E is kept by the person's removal M of S, and adopts it
  with its own merge. The add is the removed device's grant, void if concurrent with M0 and taken
  by M0 if an ancestor of it and not kept, so it is in no conflict set: E, M's signers and what
  they enrolled after M stay active, and M keeps exactly what it signed. If M0 kept it, which M0
  can only when its parents lack E's add, the kept copy may not displace E, E (kept by M) may not
  displace an add from outside S's subtree, the two conflict, and E is inactive until enrolled
  again (decision 9's kept-copy residual). E's keep, M's revocations and its shields are
  unchanged either way.
* **An outside add wins against a kept add.** G approves C2, S and then P; P approves C1. C1 and
  C2 remove P keeping C1 (R). Before the person's removal of S, S signs a copy of C1's key on a
  parent after S's add and before P's, so S is senior to C1 and P is not senior to the copy, and
  adopts it with a merge of its own, since the copy is quarantined at first. The
  copy may displace C1's add; C1's add, kept by R, may not displace the copy, which was made
  outside P's subtree (and under the plain rule could not either, P not being senior to it). So
  the copy wins and C1 is active under S's keys, announced as a displacement naming S, which S
  could have achieved by removing C1 directly. The person's removal of S, which C1's client signs
  on a parent from before the copy, as §3.3's choice of parent does, makes the copy S's grant
  concurrent with it and void: C1's genuine add is the only one left, and C1 is active again with
  its own keys.
* **The person picks the thief as cosigner.** The person starts removing S on C1 and is offered
  cosigners: C2, outside S's subtree, first and recommended; J, made and confirmed by S and named
  "Dan's iPad", last, with a warning. Picking J puts J in the keep list on C1's screen with S as
  its approver and when C1 first saw it, and C1 refuses to sign until the person confirms J twice; J's own
  client, the thief's, checks nothing. Confirmed anyway, the removal (C1 and J, keeping J) is
  valid and ranks 2, and J can enroll Y afterwards; the person then removes J, which takes Y.
* **A kept device's own adds.** The person removes S keeping E, which S approved before the
  theft. E enrolls F after the removal, and had enrolled F0 on a branch concurrent with it: F and
  F0 are active, since E is kept. S's grants concurrent with the removal are void.
* **A thief's keep list keeps nothing.** J and K, made and confirmed by stolen S, cosign a
  removal of S that keeps themselves and Y, which J added, on a fork from before the person's
  removal of S by C1 and C2, which G approved: valid where it was signed, but it revokes no signer
  of the person's removal, so it shields nothing, and J, K and Y are void as S's side. The active
  set is the person's devices.
* **Two self-removals.** D removes itself on two heads: each is a removal of D that D signed, so
  the self-removal rule protects nothing against either, and every grant D signed that is not an
  ancestor of both is void.
* **A removal of an add that step 4 subordinates.** C removes A's add of X; in step 4 that add is
  subordinate to a copy by H, senior to X. The removal still revokes A's add (step-1 voidness
  decides). If C was active in the copy's parents' state, the copy is barred and X is inactive;
  otherwise X is active under H's descriptor, announced as a displacement.
* **A device cannot cosign its own removal.** J signs a removal of D with D as `cosigner`:
  refused as invalid at every verifier and the relay, whatever the lineage test says. G's removal
  of D then takes M and J with D, as above.
* **Mutual equal-rank removals.** Confirmed A and B cosign the removal of C while confirmed C and
  D cosign the removal of A, on concurrent branches, each target not junior to its removers and
  none of the four in the subtree of either target. Neither yields, and each revokes one of the other's signers, so
  neither shields: A and C are revoked, what A and C did after either removal is void, and B's and
  D's work stands, each announced.
* **Step 4 does not feed back.** Id X has adds X1 by A and X2 by H, and neither wins, so X is
  conflicted. A's own add is then subordinate to a copy (step 4), so X1 is void. X stays
  conflicted, and X2 does not become active, until the copy is voided.
* **The tie through a device below S.** J and K, made and confirmed by stolen S on an old branch,
  cosign the removal of P, which S approved and which approved C1; C1 and C2 cosign the removal of
  S on the main line, and C1 then enrolls a fresh device F. P and C1 are revoked and F, J and K
  void; C2 and
  what C2 enrolls survive. A fresh device C2 enrolled after removing S is active.
* **A cascade that voids another id's winner.** Id Y has adds Y1 by A and Y2 by B, and Y1 wins.
  In step 4, A's own add is subordinate to a copy by a device senior to A, so it is void, and Y1
  with it. Y's conflict set is not recomputed: Y2 stays subordinate, and Y has no active device
  until the person removes the copier, which voids the copy, restores A, and with it Y1.
* **Wiping only when taken.** B adds C, then removes itself on a head from before C's add: C
  shows "not active" and keeps its keys and roster. The person's removal of B that keeps C then
  arrives and C is active again; in the other order, C never stops being active. A removal of C,
  or a removal of B that does not keep C and descends from C's add, takes C's add and makes C
  wipe, in either order. A removal of B concurrent with C's add voids C without taking it: C shows
  "not active".
* **A barred enrollment grants nothing.** A, B and C as in the bar example of §3.5: X, active only
  through B's add, signs `add(Y)` on B's old branch; C's removal bars B's add and A's is void. Y is
  void, since it was signed through a barred add and is not an ancestor of the barring removal.
* **Self-removal rule.** The person removes probationary B, keeping C, which B added; B then
  removes itself on a head from before C's add. C's add is an ancestor of the person's removal of
  B, which B did not sign, so B's self-removal does not void it: C and what it enrolls stay
  active; B's other grants are void.
* **Rank 1 against rank 1.** G removes H, keeping J; H, on an old head, removes J (H is senior to
  J). J is revoked, but G's removal of H ranks the same, neither yields, and H's removal revokes
  no signer of G's, so G's removal shields its descendants: J's work after it stands.
* **Rank 1 against rank 2.** H, revoked by C1's and C2's cosigned removal, had removed C1 on an
  old head and added Y above it: the cosigned removal is not shielded against (H's rank 1 is
  lower), and Y is void.
* **Yielding at rank 1.** G adds P, P adds Q, and G adds W after Q's add, so W is junior to Q. Q,
  on a branch concurrent with what follows, removes W. G removes P, taking Q (rank 1). Q's removal
  of W yields to G's (Q is in P's subtree; G is not in W's), so G's removal shields its
  descendants: W is revoked, but W's grants made after G's removal stand.
* **Revocation through a raw chain.** As above, but Q was added on a branch concurrent with G's
  removal, which the remover never saw: Q's add is P's concurrent grant, in the raw chain of G's
  removal of P, so Q counts as revoked by it, and the outcome is the same.
* **The window boundary.** A copy-add whose parent has exactly 8 unquarantined entries it has not
  seen is within the window and applied; with 9, it is quarantined.
* **Copies across ids.** X2 copies Y's key while Y2 copies X's key, each on a branch from before
  the other's genuine add: the global pass forms both conflict sets from step 1 at once, resolves
  them without waiting on each other, and every verifier reaches the same state in every delivery
  order.
* **The tie.** J and K, made and confirmed by stolen S on an old branch from before C1's add,
  cosign the removal of S there; C1 and C2 cosign the removal of S on the main line, and C1 then
  enrolls R1 while J enrolls Y. C1, C2, J, K, R1 and Y are all void, S is revoked, and with no
  other approver the generation needs a reset; every device announces it.
* G approves P and C2, P approves C1, and confirmed C1 and C2 remove G (rank 2); C1 then enrolls
  N. G, on an old head, removes P (rank 1), which takes C1: both are revoked, and N stays active, since the
  removal of G ranks higher.
* Stolen genesis S, on an old branch, adds and confirms J and K, which cosign removals of C1 and
  C2; S adds Y above those removals. C1 and C2 cosign the removal of S. J's and K's adds are S's
  grants concurrent with it, in its raw chain; their removal yields to it (they are in S's
  subtree): Y is void, C1 and C2 are revoked, their replacements stay, in every delivery order.
* With a partitioning relay, confirmed A and B cosign the removal of G while G removes both (rank
  1) on another branch, and A adds X after the removal of G: X stays active, and is
  announced (the residual of decision 9). The same with A or B unconfirmed: the removal of G is
  invalid, and X is void.
* A subtree that spreads 2,000 over-budget entries over many signers with two entries each brings
  at most 1,024 as evidence; a removal needing more is refused and shown as unconfirmed, and an
  import at the machine admits it.
* **Local adoption.** A host quarantined the branch where G approved and confirmed C1 and C2; their
  cosigned removal of stolen G is held there and their merges adopt nothing. `devices adopt` at the
  machine, shown the branch, adopts it, the removal applies, and the host stops serving G. Inside
  an agent session it is refused.
* **The evidence rule with a subtree.** T, on probation, adds J; J signs 64 adds on each of two
  branches; host X holds one set and host Y the other. G removes T, taking J, on a head synced
  from Y: X admits J's entries as evidence and applies the removal, and J is revoked. The same
  removal keeping J is refused at X and shown as unconfirmed, and the client offers the full
  cascade. A removal whose over-budget ancestors are signed by a device it keeps, or outside the
  target's subtree, is refused.
* **Removals are signed where every host already is.** With an honest relay, T adds J1 to J6; each
  signs about 192 entries within its budget; T gossips a different set to each of hosts H1 to H4
  and another to the relay. The person's device, holding attestations from all four, removes T:
  the client signs on the newest entry under every attested head and the relay's head that holds
  T's add and the remover's add, so the removal needs no evidence at any of the four, applies at
  each, and every entry of T and J1 to J6 not an ancestor of it is void. The same removal signed
  on the relay's head instead needs more than 1,024 evidence entries at each host and is refused:
  the client never does that. A fifth host the device has no attestation from, holding a sixth
  set, refuses it, shows as not having confirmed it, and admits it after `devices sync` there.
* T removes its own juniors with evidence until a host's 1,024 evidence entries are used: a later
  removal of T, signed on the common parent, needs none and applies there.
* **Copies.** With an honest relay, H (approved by G) signs on a parent 5 entries back a copy of
  the key of X, added 3 entries back by A, a device H is not senior to: the relay appends it, X is
  conflicted and the conflict names H. The same against a device added 20 entries back, signed on
  a parent 22 entries back: every verifier quarantines the copy. H then puts two sibling renames
  above it and signs a merge of only those heads, leaving out the branch with X's genuine add: the
  merge adopts the copy, and X is conflicted at every verifier holding both, in every delivery
  order. In each case G removes H, which takes the copy: it is revoked and X active again, with no
  new enrollment. A cosigned removal of H by two confirmed devices, signed on a parent from before
  the copy, does the same. H's attempt to copy G's key, or that of any device senior to it, is
  refused as a re-added id.
* **Reciprocal removal.** Confirmed C1 and C2, approved independently, cosign the removal of
  stolen senior H (rank 2). H, on a head from before it, removes C1 and C2 (rank 1) and then adds
  Y on top. H, C1 and C2 are all revoked; Y is void, since H's removals rank below the removal of
  H; replacements C1 and C2 enroll after removing H stay active, since that removal ranks above
  H's. Every delivery order gives the same state.
* **Removal through devices it approved.** Stolen confirmed S, on an old branch, adds and confirms
  J and K, which cosign removals of C1 and C2. C1 and C2 cosign the removal of S on the main line
  and enroll replacements. J's and K's adds are void (S's grants concurrent with its removal), so
  they are revoked through S's lineage; C1 and C2 are revoked; the replacements stay active; any
  add J or K signed afterwards is void, since their own adds are.
* **A cycle of copies.** Concurrent approvers A, B and C each add one id, over histories holding
  A and C, A and B, and B and C respectively, so A's add displaces B's, B's displaces C's and C's
  displaces A's: no add wins, the id is conflicted at every verifier holding all three, in every
  delivery order, and no verifier picks a signing key for it. With only two of the three, the one
  that wins pairwise is active.
* **Displacement.** G adds H, then G adds A on top of that, and A adds X, so H is senior to X. H
  signs a copy of X's key with its own add as parent, before A's: A is not senior to the copy and
  H is senior to X, so X's genuine add is subordinate. X stays active under H's `sign` key, X's own entries and
  subtree are void, every device announces "*H* replaced the enrollment of *X* that *A* approved",
  and X shows "Replaced by *H*" and keeps its keys. G removes H, which takes the copy: the copy
  is revoked, X is active again under its own descriptor, with its subtree.
* **The target cannot choose the removal's parent.** Stolen senior S signs `add(Y1)` to
  `add(Y5)` on a far fork, with renames above them to raise its `seq`, and gossips it everywhere;
  every verifier quarantines it. Confirmed C1 and C2, approved independently, remove S. The client
  signs on the smallest unquarantined entry holding S's add, their adds and their confirms; S's
  branch is under no candidate, so Y1 to Y5 are concurrent with the removal and void. The join
  merge carries no `adopt` and names no quarantined head, so it adopts nothing at any verifier,
  including one that quarantines something under it. The cascade shows Y1 to Y5, quarantined,
  ticked; the removal names none of them, and they are void by position. A later Tidy adopting
  S's branch leaves them void. A join that names a quarantined head,
  or a merge with `adopt` other than `true`: an honest client never signs one, and a parser
  refuses `adopt: false`.
* When the honest line was built on two of the target's applied adds before a signer's confirm,
  the client takes the smallest candidate, says that those two cannot be voided by position, and
  the removal takes the devices they added unless the person keeps them.
* A hostile device fills J's budget at one host with entries the relay never sees, before the
  relay's line arrives: that host refuses J's entries on the line and everything after them,
  goes stale, and recovers after `devices sync` at the machine.
* **Replay mark.** A host whose wall clock ran a week ahead and was corrected refuses every person
  command as expired and says so. After `devices reset-clock` at the machine, a new command is
  accepted, and a command issued before the reset is refused even if its record was pruned.
  Inside an agent session the reset is refused.
* An iPhone PWA whose Safari passkey reports BE set is recorded `presence: none`.
* **The window cannot be refreshed.** H submits a removal signed on a parent `P` 30 entries back,
  which is accepted, and then a copy-add on `P`: the copy is outside the window (30 unseen
  entries) and quarantined, though the removal's head is one entry away. The same with a
  quarantined grant on `P` first instead of the removal. An open head left 12 entries behind (a
  fork no one merged), extended without forking: outside the window, quarantined. An honest client
  holding both heads merges them before appending, and its entries are within it.
* A far fork holding `add(X)` and then `remove(Y)` by an established device: the removal applies,
  `add(X)` stays quarantined and X is not active. A far removal signed by X itself is held until a
  merge adopts that branch.
* A removal forking from outside the window is accepted and applied, whichever order the branches
  arrive in. A non-removal entry from outside the window, or one making a fifth head, is quarantined:
  kept, gossiped, not applied, and adopted only by a presence-signed `merge` with `adopt: true`. Two verifiers fed the
  same branches in opposite orders converge after that merge.
* An entry offered by an unrostered channel peer through anything but `roster.submit`: refused.
* **Removal forks.** A removal signed on a common parent that already has children at the relay:
  the relay appends it, no verifier quarantines it or reports misbehavior, it applies at once, and
  the remover's merge of it and the other heads joins it. Without that merge (a remover on
  probation), the next honest append merges first, and its entry is within the window.
* **Removal proofs.** A device whose copy would quarantine its remover's add receives a proof with
  the adopting merge and the entries leading to it, and confirms its removal. Without the merge
  (the host adopted the branch only locally), it stays at "not confirmed" and the host still
  refuses it; after a person's device signs the adopting merge, the proof confirms it.
* **Replay across a reset.** A host whose clock is correct accepts a transfer dated 60 seconds
  ahead and runs it; its clock then jumps a day forward, pruning the record, and is corrected; the
  reset 30 seconds after the transfer ran sets the floor at that transfer's `issuedAt`, and the
  asking host's replay is refused. A new command issued after the floor is accepted.
* **Restoring past the active-device cap.** 64 active; H disables ten by copies; G approves ten
  replacements; G removes H, which takes its copies: the ten genuine enrollments are restored and 73
  devices are active, in every delivery order. The next add is refused until removals bring the
  count below 64; removals are accepted throughout.
* **Partial cascade.** The person removes probationary B but keeps C, which B added: the removal
  of B keeps C, its parent holds C's add but none of B's other grants, C stays (taking effect when
  B's hold would have ended), and B's other adds are void. With everything ticked, it keeps
  nothing, takes C, and C is revoked.
* **Durable replay.** A transfer person command used once; the source host restarts, becomes fresh
  again, and the asking host replays the same command within its lifetime: refused. The same after
  the source's wall clock is moved back 30 minutes. A host whose replay record cannot be written
  does not run the action.
* **Client mode.** A dishonest relay advertises an unrostered host named "Desk": a client in
  `enforce` shows it only as "Not approved" and sends it nothing; a client in `observe` reports it
  and still talks to it, which is why the cutover switches every client to `enforce`.
  Through `roster.submit`: a valid chain from a device the host missed is accepted, and a removal
  in it applies even while the host's app is uninformed; the answer is only `accepted` or
  `refused`, identical whatever the host holds; the 257th entry that is not evidence for a removal
  in the submission, and a second submission within a minute from one key, are refused. One
  submission carrying a removal, 256 evidence entries from one signer of the target's subtree and
  its other missing ancestors is accepted and the removal applies. One of exactly 1,281 entries
  (the removal, 256 missing ancestors that are not evidence, and 256 evidence entries from each of
  four signers of the target's subtree) is accepted at a host none of whose 1,024 evidence entries
  is used, and refused as a whole with one entry more, or at a host that has already admitted
  any evidence. That submission spans many frames and is accepted only once its last part
  arrives; one with a part missing for 60 seconds, over 16 MiB of entry sizes, or holding a
  47 KiB removal or a 9 KiB add is refused as a whole, and nothing in it is applied. With
  evidence that is all renames and adds at the 8 KiB limit, the 1,281-entry proof is about
  10 MiB and accepted. With 162 of its evidence entries the target's subtree's over-budget
  removals, each padded to 46 KiB, the proof passes 16 MiB: it is refused through `roster.submit`, the host
  shows as not confirming the removal, and `devices sync` at the machine admits it. A flood of submissions from
  many other keys does not stop one holding a valid removal from being checked first.
* An approver whose head moved before its append asks the person again, and never reuses the old
  signature or assertion.
* The cascade offers exactly the devices in the removed device's approval subtree. Whatever is
  ticked, it is one removal of the device keeping the unticked ones, and spends one removal of
  each signer's budget. Unticking a device unticks its approvers up to the target. A device that
  approved others is removed with a cosignature when a second confirmed device is available.

**Person commands and kind spoofing**
* A transfer, folder-trust answer, Install or forget from an **active `app` channel without a
  presence-proven person command** (the compromised-machine case): refused.
* A device allowed for folder trust at a machine is displaced by a hostile senior's copy of its
  key: its id is still active, but under an add other than the one recorded when it was allowed,
  so folder-trust answers signed under the copy's key are refused, and Settings shows it as
  changed until the person allows it again at the machine.
* A Mac app whose signing key uses `.userPresence` declares `presence: none`; one with a
  `.biometryCurrentSet` key and no password fallback declares `key`. An iPhone client declares
  `key` with `.userPresence`.
* Enrollment: an approver's client records `presence: none`, not `webauthn`, for a synced
  credential (BE set), for a credential on `macos` or `linux`, and for one on `windows` before it
  ships R4's proof; it records `webauthn` for a device-bound credential on `ios`, `ipados` or
  `android`. A Mac app declares `key` only for a Touch-ID-only key.
* Verifiers on different versions reach the same verdict: an entry or person command is judged
  only by the recorded `presence` and `backupEligible`, never by `platform`. One from a `webauthn`
  device recorded with `backupEligible: true`, or with an assertion whose BE differs from the
  recorded one: refused.
* A person command from a `presence: none` device: refused under 3(a). Without the host's nonce,
  where one is required: refused. Replayed, expired, for another machine or action: refused.
* A rostered host's key connecting with `hello` kind `pwa`: refused everywhere a person is needed.
  An unrostered key with kind `pwa` or `app`: refused everywhere. A rostered `pwa` connecting as
  `host`: not used as a host-to-host peer.

**Freshness and removal while offline**
* A host that was offline at a removal learns it from the relay at reconnect. With a relay that
  withholds it, the host learns it from the first rostered peer or from its app over the pipe.
* A host with no independent attestation for 24 hours serves relay devices only `hello`, `roster.*`
  and attestations. The removed device's own attestation does not keep the host fresh for it, even
  when the host's copy lists it as the only person's device. A wall clock moved back a day, or a
  machine left off for a day, makes the host stale, not fresh.
* A host switched off for 48 hours that comes back with its wall clock moved back 48 hours is
  stale until a new attestation, as after any restart. A wall-clock jump of more than 5 minutes
  against the monotonic clock while running makes it stale too.
* With `selfAttest` on: an `attest` person command with the host's nonce and heads that include or
  descend from the last peer-attested heads keeps the host fresh, and makes a host that was already
  stale fresh again, and the host reports the setting.
  One without a presence proof, without the nonce, naming heads the host lacks, or naming heads
  older than the last peer-attested ones: refused.
* A host that holds a removal whose signers' adds are quarantined there, and so still serves the
  target, is not counted as confirming it: its attestation does not list the target's add as
  revoked, and the Devices page offers local adoption there.
* The remover's Devices page lists which machines have confirmed a removal. A machine kept in a
  simulated partition never confirms, and a roster imported at the machine ends the partition.
* A removal that names a device's id but revokes only a fabricated duplicate add of that id does
  not make the real device wipe anything: its own enrollment add is still active, and it says the
  removal is not confirmed.
* A removed device is told so, wipes its data and push subscriptions, and makes a new key to join
  again, but only after verifying a removal of itself. A relay `removed` close code or a host's
  refusal without one changes nothing it holds. An offline device that was removed gets the
  removal proof with the refusal (the entries from its heads to the removal), completes the wipe,
  and a key that was never approved gets no proof.

**Relay**
* A token presented with another key is refused. `token-exchange` is refused on a fresh socket
  with only a token, and works only on an admitted socket that proved the token's key. The app gets
  its own bound token through its host. While the relay still issues only unbound tokens, an old
  app that takes its host's token keeps working; the relay issues bound tokens only after every
  app uses the exchange, and an app that takes a bound host token then is refused with a reason.
* A `veto` with another account, generation or reset hash, a missing presence proof, a signature
  over the raw body or under another context label, an extra or missing member, or from a device not
  active in the current generation: refused.
* A reset vetoed by a current-generation device stays held; the administration override admits it;
  no machine moves without its local action. A reset genesis is held 72 hours, announced, and
  ended by a veto. A pending device receives no roster and no heads.
* Presence declarations: a WebAuthn registration with a valid attestation from a pinned root is recorded `attested: true`; one without is `attested: false`, and the approver says so.

**Interop**
* Shared vectors for `F`, entries, signatures (DER, low-S, for `sig`, `cosig` and WebAuthn assertions alike: a high-S copy of each is refused), entry hashes, head order, `C`, `T`, both
  codes (including leading zeros), the QR MACs and invite hint, head attestations, person commands,
  WebAuthn assertion checks, and the merged state of a forked fixture. They run in C#, the PWA and
  Swift, as the command MAC's vectors do.
* A name holding a hidden character: removed by the approver before signing; refused by a verifier
  if it is a control; shown escaped otherwise.
* Shared vectors for a name holding a tab and one holding a line feed (`"Dan's\tPhone"`,
  `"Dan's\nPhone"`): the approver removes the control before signing (`"Dan'sPhone"`), and a
  signed name still holding either is refused by every verifier.

## 11. Owner decisions

The owner decided all ten on 2026-11-04, each option (a). The rejected options are kept for the
record.

1. **Trust root.** **Decided by the owner (2026-11-04): (a).**
   (a) Approved, presence-proven person's devices (decision 3) sign roster entries with a new
   ECDSA key and a presence proof, under seniority, probation and confirmation (§3.4): a device's
   grants are held for its first 72 hours, and it can take part in removing a device that is not
   junior to it only once its approver has confirmed it. Hosts never sign. (b) One account root
   key on the first device. (c) Per-machine approval lists only.
   **Decided: (a)**, with seniority, probation and confirmation (decision 9) and the voiding rules of §3.5.
   Without them, one hostile device could remove every other. Under (a), the most senior device is
   the most powerful one, and if it is stolen and unlocked the answer is a reset.
2. **Hosts in the roster.** **Decided by the owner (2026-11-04): (a).**
   (a) Hosts are approved by the mutual codes like any device, and confirm the genesis fingerprint
   at the machine; clients refuse unapproved hosts. (b) Hosts are trusted on sign-in as today, and
   only person's devices are approved.
   **Decided: (a)**, since the mutual check of §4.2 stops a relay from enrolling a host into a
   roster of its own. Under (b), a stolen token registers a fake machine that collects what people
   type.
3. **Who can approve and do person-only actions.** **Decided by the owner (2026-11-04): (a).**
   (a) Only devices recorded with presence (§4.6): an iPhone or iPad running the Apple client
   (Secure Enclave, `key`); a Mac app only with a Touch-ID-only key, since `.userPresence` falls
   back to the login password; the Windows app, and PWAs in a Windows browser, once R4 proves that
   same-user code cannot satisfy Windows Hello or the app's mechanism; and PWAs on phones (`ios`,
   `ipados`, `android`) from R5. A PWA qualifies only with a **device-bound** credential: in v1 a
   **synced** passkey (iCloud Keychain, Google Password Manager), which can be asserted from any
   device in its sync account, never does. Safari passkeys on iOS and iPadOS are always iCloud
   Keychain passkeys, so an iOS or iPadOS PWA never qualifies; the presence-proven client there
   is the native Apple client. The approver's client decides at enrollment and records
   `key`, `webauthn` or `none`, and verifiers read only that, so hosts on different versions agree.
   Desktop PWAs on macOS and Linux and Mac apps without Touch ID are recorded `none` until L1.
   Devices recorded `none` can be approved and drive sessions, but cannot approve, sign roster
   entries or do person-only actions.
   (b) Also devices without presence. **Not specified by this note**: §3.3 requires presence for
   every entry, and letting devices without it approve would need its own design and security
   review before it could be chosen.
   (c) One code, compared rather than typed, with no presence requirement. This gives up the
   mutual check of §4.2 and the presence proof together, so a relay acting as an approver, or
   same-user malware, could approve devices.
   **Decided: (a).** The cutover then starts on the first presence-proven device. Genesis is the
   most senior device and the hardest to lose (§5.4), so prefer an iPhone or iPad running the Apple
   client when one exists: its prompt is the phone's own, and it runs no agents. The alternative,
   the Windows app on the desk machine once R4 has proven it (R4 before R8), keeps the most senior
   key on the machine where agents run, so a compromise there sits next to it, with only R4's proof
   between them. A phone PWA is not recommended as genesis, since any script in the Pages origin
   can ask for its assertions (§4.6).
   Under any option, presence and platform are the enrolling device's own claims, checked by
   attestation where the platform gives one (§3.1). They hold for devices that were not
   compromised when they enrolled.
4. **What an unapproved device of the account sees.** **Decided by the owner (2026-11-04): (a).**
   (a) Nothing beyond "pending approval": no roster, no heads. (b) A read-only list of machines.
   **Decided: (a).**
5. **Recovery when every approved person's device is lost.** **Decided by the owner (2026-11-04): (a).**
   (a) Reset to a new generation, held 72 hours at the relay with veto, and confirmed at each machine.
   A stolen senior device can veto every reset, so recovery from that needs the relay operator's
   out-of-band override (the owner, for this relay); machines still move only by local action.
   (b) As (a), plus an optional offline recovery key made at genesis.
   **Decided: (a).** A recovery key is one more secret that, with the GitHub account, takes the
   account. Total loss is unlikely if the person keeps at least three approving devices, two of them
   approved and confirmed directly by the genesis device (§5.4), and enrolls a fresh device straight after removing a
   stolen senior one (§5.4). Losing a senior device without two such confirmed,
   independently approved survivors is also a reset. A senior device **stolen with its presence
   check passable** can force one regardless, if every approver is junior to it: for the genesis
   device that is always the case (§3.5, the tie).
6. **Grandfathering the devices in use now.** **Decided by the owner (2026-11-04): (a).**
   (a) Enroll each with the mutual codes once (one exchange per machine, about six). Hosts go to
   `observe` first, then `enforce` per host and on every person's device, and the relay's
   restrictions on unapproved devices come last, when the owner switches them on (§4.5), so nothing
   is cut off mid-enrollment. (b) Bulk-approve the devices online at cutover after
   comparing whole descriptor fingerprints (covering `dh`, `sign`, `signAlg`, presence, `rpId`, the
   credential id and key, `attested`, `backupEligible`, kind and platform, not only the id), with
   nothing pre-ticked (not even `remoteFolderTrustDevices` ids, whose names can be
   look-alikes), and each machine confirming the genesis fingerprint locally. (b) has no mutual
   check.
   **Decided: (a).**
7. **Forget and Remove for a machine.** **Decided by the owner (2026-11-04): (a).**
   (a) One action, Remove, a presence-proven roster entry that revokes and forgets; a machine that
   returns waits for approval. (b) Keep Forget listing-only (behind a person command), with Remove
   separate.
   **Decided: (a).**
8. **Which kinds count as a person's.** **Decided by the owner (2026-11-04): (a).**
   (a) `app`, `pwa` and `phone` everywhere, so the native iPhone client can answer folder trust and
   press Install. (b) Keep folder trust and update to `app` and `pwa`.
   **Decided: (a)**, provided person-only actions need presence-proven person commands (§7.2).
   The kind alone does not show that a person acted.
9. **Seniority, probation and confirmation.** **Decided by the owner (2026-11-04): (a).**
   (a) A device removes only itself or devices junior to it: those whose add descends from its own
   add. Removing any other device needs two signers whose lineages of add entries, cut to leave out
   the target's add and every add senior to it, share nothing (not parent and child, not siblings
   or cousins), and who are both **confirmed**: each one's own approver signed a `confirm` of it,
   which the client offers only 72 hours after the add and the person confirms with presence. This
   is a
   validity rule, so no clock decides it. New devices also have 72 hours of probation, counted in
   each verifier's persisted monotonic running time: their adds and renames are held until it
   ends, or until the verifier holds a `confirm` of them. Their removals of devices junior to them
   apply at once, since removals are never held or undone, and merges are never held, since they
   only adopt. Removing a device offers to remove the devices it added, always as one removal that
   keeps the unticked ones. What (a) still allows:
   * a delayed takeover, by two hostile devices that the person's own devices approved
     independently and their approvers later confirmed, unnoticed, which can then remove the
     devices above them. That takes two approval mistakes and two confirmation mistakes, each
     announced on every device;
   * a hostile device, even on probation, removing at once devices junior to it (added after it),
     each removal announced;
   * a hostile approver disabling any device that is not senior to it, by adding a copy of its key
     on a fork and, if that is quarantined, adopting it with its own adopting merge (§3.3): a
     conflict makes the device inactive, and for a device junior to it the copy can displace the
     genuine enrollment, leaving the id active under the approver's own keys. It needs no
     dishonest relay, but never reaches a device senior to it. Both are announced naming it, and
     removing it, which takes its copies unless it keeps one it could not tell from the original,
     restores the devices outside its own subtree. A device its subtree enrolled and then copied, a device enrolled under a
     copy, and every device either of those enrolled, are taken with it, since no kept add may
     have a copy between it and the target in its lineage, and are enrolled again with new keys:
     work lost, not trust gained. A removal of it signed on a branch that cannot see a device's
     genuine add can keep its copy of that device, which that history shows as the only add; once
     the branches join the two conflict (a kept add does not displace, §3.3) and the device is
     inactive until enrolled again with a new key. Only if no device senior to it is left and the copies have disabled the cosigners its
     removal would need does it take a reset;
   * one-time revocations by removed devices, signed on old heads (§3.5). A removed device alone can
     revoke devices that were active and junior to it there. A confirmed pair approved independently
     can also revoke a device that is not junior to them, and with it that device's approval
     subtree except their own approvers, one such device per removal. They never gain trust, a revoked senior device comes
     back junior, and such a revocation never voids what descends from the removal that revoked
     the remover, when that removal shields against it (§3.5, precedence): when it ranks higher (a
     cosigned recovery outranks a removal by one device), or ranks the same and the revocation
     yields to it, or neither is signed from the other's target's subtree and the revocation revokes none of its signers. A stolen
     senior device whose thief passes its presence check can so revoke the two devices that
     removed it, itself or through devices it approved on an old branch; what they did afterwards
     stands against those revocations.
     **The person's recovery is one removal** of the stolen device that names only what the person
     keeps: it takes the rest of the device's approval subtree, whatever ids and states those adds
     have, and what the subtree added where the remover could not see it is void (§3.3), so no list
     can be incomplete. **Nothing the stolen device or the devices it made add survives the
     recovery, except what the person kept**, on any fork and in any delivery order, unless a
     device outside that subtree (one of the person's own, or a second compromised one) helps
     revoke one of the recovery's signers. Removals by the stolen device, by devices it made and
     by devices the person kept from its subtree cannot shield anything it adds: a removal signed
     by devices the other removal's target approved never shields against that removal at equal
     rank. §3.5, "What this guarantees", proves it. "Kept" means kept by the removal its signers
     signed, so a removal signed by a device the stolen device made (built by it, or with it
     picked as the cosigner) and keeping that device keeps it. Both signing clients therefore show
     the whole keep list, kept signers included, recommend cosigners outside the stolen device's
     subtree, and refuse to sign until the person confirms, twice, each signer from that subtree
     and each kept device the client first accepted after the person says they last had it (by
     the client's own record, never the signer's `at`), and acknowledges the
     older kept devices once (§6.1). Confirmations asked this often invite agreeing by habit,
     which no client rule removes; a device kept despite them can be removed like any other. Two cases do
     lose honest work, and are stated here rather than closed. When the recovery is signed by one
     senior device (rank 1), a confirmed pair the stolen device made can still, once, revoke a
     device junior to the stolen device that did not sign the recovery, and what that device did
     after the recovery is void, since a rank-1 removal cannot shield against their rank-2 one;
     the senior device itself is out of their reach. The client therefore asks for a cosignature
     on every recovery of a device that approved others. And what the cosigners do after a rank-2
     recovery is guaranteed to survive only when none of their approvers is the stolen device or
     junior to it: a pair it made can remove such an approver, and then neither removal yields
     (the tie, below). That is why the
     person enrolls a fresh device right after removing a stolen senior device, from an approver
     not junior to it where there is one (§5.4);
   * a reset forced by a stolen senior device whose thief passes its presence check: it approves
     and confirms two devices on an old branch, from before the person's cosigners were added, and
     has them cosign its own removal there. That voids every device it approved after that point,
     the cosigners included, while their removal of it voids the two it made; the two removals
     rank the same, the graph is symmetric, and neither side is kept (§3.5, "the tie"). Everything
     any of those devices enrolled is void too, on either branch, including replacements the
     cosigners enroll after their removal of it. Aimed at a device below it instead (one that
     approved a cosigner), the tie takes that device and its subtree and spares the other cosigner. Only
     approvers not junior to the stolen device are sure to survive; for the genesis device, to
     which every device is junior, that means a reset. No tie-break over the entries is safe, so
     this is not closed. It is announced;
   * work lost when the person's own devices act at cross purposes on concurrent branches: devices
     kept from a stolen device's subtree that remove the device through which a recovery's
     cosigner was approved void that cosigner and what it enrolled afterwards, and neither removal
     shields (§3.5, rule 3). Nothing the stolen device adds survives it;
   * the mirror image of the one-time revocations above, since the rule cannot tell the two apart (§3.5): with a relay that
     partitions the account, two confirmed hostile devices approved independently can cosign the
     removal of a senior device and keep the grants they make after it, even if that device
     removes them on another branch. The grants are announced; devices senior to the two can then
     remove them, and if none are left it is a reset;
   * a reset when a senior device is lost without two confirmed, independently approved survivors,
     including when it is lost within three days of approving them;
   * a removal refused for evidence at some hosts, because there a hostile target can make it
     need more evidence than the limits allow. The client signs every removal on a parent every
     attested host holds when one holds what the removal needs, and then only a host it has no
     attestation from (one offline since before the target was added, say) can need evidence
     (§3.3). When none does, because a signer's `confirm` or a kept device's add is newer than what
     some attested hosts hold, the removal's parent lies outside their common history, and the
     target's entries that precede that `confirm` on the relay's line can need evidence at those
     attested hosts too. Either way such a host shows as not having confirmed the removal, the
     client says which hosts lack the parent, and importing the roster at each machine admits it.
     The same holds for a host that is behind and can be reached only through `roster.submit`,
     when the target's subtree pads its over-budget removals so that the proof passes the 16 MiB
     a submission carries (§7.1).
   Seniority follows causality (one device's add is an ancestor of the other's), so a relay that
   hides heads cannot make a newer device outrank an older one.
   (b) Seniority without the two-signature removal: a lost senior device then always means a reset.
   (c) Neither: any approver removes any device, and one hostile device can remove all the others
   before the person notices.
   **Decided: (a).**
10. **Freshness.** **Decided by the owner (2026-11-04): (a).**
    (a) A host without an attestation of the current roster from another device for 24 hours
    (counted by the larger of monotonic and wall-clock elapsed time, so a clock change makes it
    stale sooner, never later; and stale at once after any restart, or a wall-clock jump of more
    than 5 minutes against the monotonic clock, until a new attestation) serves relay devices only
    `hello`, `roster.*` and attestations,
    with no single-device exception. The local setting `security.roster.selfAttest`, off by default
    and set only at the machine, re-opens exactly that exception for a headless host no other device
    contacts: it then accepts the requester's own presence-proven `attest` command, which can never
    name older heads than the last peer attestation, and a relay that hides an addition and then the
    requester's removal keeps that host serving the removed requester. The host says so while the
    setting is on. (b) The same, but a stale host refuses only person-only actions, so a removed
    device can go on driving sessions there while a dishonest relay withholds its removal. (c) No
    freshness check, so the withholding can last indefinitely. Under any option, a relay that
    partitions the account (keeping a group of machines and their apps from every device that holds
    the removal) keeps the removed device served inside that group until the partition ends. The
    person sees it as machines that never confirm the removal (§6.3).
    **Decided: (a)**, with `selfAttest` left off. Other hosts every 5 minutes, and the app on
    the same machine, keep a host fresh in normal use; turning `selfAttest` on is a per-machine
    choice to accept the weaker rule there.

## 12. Slices

Each is one PR, security-reviewed independently before merge. Every owner decision a slice depends
on below was made on 2026-11-04 (§11), so none is waiting for one.

| # | Slice | Depends on | Changes behaviour |
|---|---|---|---|
| R1 | Roster library: strict parser and negative vectors, entries and signing contexts, per-signer presence proofs, the acceptance rule and state (causal seniority over add entries with lineage-independent cosigners, probation holds for grants only on persisted monotonic time, the removal pass, single-target removals that take the target's approval subtree except a keep list, and their ranks, voiding (bars included), the self-removal rule, precedence between removals (rank, yielding by approval subtree, raw chains), key bars, conflicting adds by seniority, caps, per-signer budgets and the evidence rule with subtrees, quarantine and the window, merges that are never void with adoption only by `adopt: true`, the removal-parent choice, `confirm` and the confirmation rule for cosigned removals), the `approval` member sets, `veto`, person commands, head attestations, WebAuthn assertion checks, signing keys (TPM/DPAPI, WebCrypto), shared vectors (C#, JS); protocol spec `docs/protocol/devices.md` | decisions 1, 2, 9 | No |
| R2 | Relay: store and appends, the account relay mode (`observe` until a person's device switches it, one way; `enforce` at a new account's genesis), approved/pending admission with invite hints, nothing for pending devices, bound kinds in presence, generation rules with hold and veto, bound tokens with `token-exchange` (unbound until every app exchanges, then issue bound and accept both, then enforce last), pending limits, `removed` close code, administration reset for a squatted genesis. Staging; production only with the owner's approval | R1, decision 5 | Only for accounts with a roster |
| R3 | Host: state (with quarantine), gate in `ChannelHub` (XX and IK replies, early requests included), `transport` in an approved `hello` and the opener's use of it, gossip, `roster.submit` with per-key limits, freshness (monotonic and wall-clock), `selfAttest` and acknowledgements, person commands, modes `off`/`observe`/`enforce`, enrollment over the pipe and `devices join`/`reset`/`sync`/`adopt` with the genesis fingerprint | R1, decision 10 | Only when the mode is changed |
| R4 | Windows app: join (mutual codes, fingerprint), Devices, client rule; **proof** that its presence mechanism (Windows WebAuthn/Hello, or a TPM UI policy) cannot be satisfied by same-user code, including `SendInput`, UI Automation and the project's injector, and the same proof for Windows Hello as browsers reach it, after which its approvers record `webauthn` for device-bound Windows PWA credentials (§4.6). Only after that does it approve or sign person commands | R2, R3, decision 3 | Only after genesis |
| R5 | PWA: passkey registration (with attestation where offered, and the BE flag recorded so synced passkeys count only as §4.6 allows), join, approve (codes and both QR directions), Devices, confirm after 72 hours, remove, cascade, cosign, person commands, pending screen, client rule | R2, R3, decision 3 | As R4 |
| R6 | Person-only actions on person commands, with the durable replay store that replaces `CommandMemory`: transfer, folder trust, escaped-approval confirmation (with `escapedApprovalDevices` and the folder-trust list pinned to `(device.id, add_entry_hash)`), update, restart and repair (§7.7), forget/remove; one `RosterKinds.IsPerson`; usage peers and transfer hosts from the roster; docs for #70, #133, #138, #161 and the HA connector note | R3, R5, decisions 7, 8 | In `enforce` |
| R7 | Removal end to end: revocation, offline hosts, removed-device experience, Forget/Remove | R5, R6 | Yes |
| R8 | Owner cutover: runbook and live checks (the issue's "Validate live"), then `enforce` per host | R1-R7, decision 6 | On the owner's machines, with their go-ahead |
| R9 | `enforce` by default for new installs | R8 | Yes |
| A1 | Apple: join, then approve, Devices and person commands (contributor) | R1, R2 | For Apple clients |
| L1 | Later: recovery key if chosen, checkpoints, CPace in place of two codes if wanted, presence-proven Allow-all launches, and the presence proofs for `macos` and `linux` prompts (desktop PWAs, and a Mac app without Touch ID), plus synced passkeys in a later protocol version | | |
