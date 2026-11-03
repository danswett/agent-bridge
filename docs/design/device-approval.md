# Device approval and the signed roster (#55)

**Status: proposed design, not built.** It needs the owner's decisions (§11) and an
independent security review before any slice starts. It refines design.md
[§5.4](../design.md#54-accounts-devices-and-the-signed-roster). Where the two disagree,
this note is the newer reasoning; design.md §5.4 will be updated when the decisions are made.
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
| **The relay** (operator, storage, a compromised deployment; it mints tokens) | Untrusted for authorization. It stores and serves the roster, routes frames, and can drop, delay, reorder, replay or withhold anything, and act as any number of devices of its own | Forge or alter a roster entry, forge an approval, make a verifier forget an entry it holds, substitute keys in an enrollment or pose as an approver to a new device without the codes failing, or keep a removed device served beyond the freshness window (§6.3). It **can** deny service and show a pending device a misleading name |
| **A compromised machine** (an agent or malware running as the user where a host and the Windows app run) | Holds the host key and the account token, has the use (not a copy) of the app's TPM keys, and can inject input | With the host key: pass a person-only check, add or remove devices, or take another machine's sessions without a person's press. With the app's keys: approve devices or pass person-only checks. The app does either only with a presence proof, which R4 must show same-user code cannot produce (§4.6). It **can**, as that app, read and drive sessions on the account's other machines, as it already can on its own: the residual named in §7.3 |
| **A pending (unapproved) device** | Has completed sign-in and proved its key | Learn anything but that it is pending: no roster, machines, sessions, usage or names; no pushes or blobs |
| **A newly approved device that turns out to be hostile** (approved by mistake or by a compromised approver) | Holds an approved key | Remove the devices that were there before it, or add devices, during its probation; remove a more senior device on its own signature (§3.4) |
| **A removed device** | Was approved; still holds its keys and maybe a valid token | Anything a pending device cannot, once a host learns of the removal, which a host does within the freshness window or stops serving relay devices (§6.3) |
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
| `key` | `sign` itself refuses to sign until the person verifies, enforced outside the app's process | Apple: Secure Enclave `SecureEnclave.P256.Signing` with `.userPresence`. Windows: only once R4 proves its mechanism (§4.6) |
| `webauthn` | Each presence-proven signature also carries a WebAuthn assertion with user verification from a platform authenticator, over the same message; the credential's public key is in the entry | PWA (R5). Windows may use this route too (§4.6) |
| `none` | No presence proof | Any device on a software key (DPAPI, a Keychain key without the Secure Enclave), and the Windows app until R4 |

A device declares `presence` when it enrolls, while it is still known to be in the person's
hands; nothing can raise it later without a new enrollment. Under owner decision 3(a),
only devices with `key` or `webauthn` can approve or do person-only actions.

**Approved, non-probationary person's devices sign entries** (multi-approver roster, owner
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
{ "body": "eyJ2IjoxLC…", "sig": "MEUCIQ…", "cosig": "MEQCIF…", "uv": { "…": "…" } }
```

| Field | |
|---|---|
| `body` | The entry's JSON, UTF-8, base64url without padding. Signers and verifiers use these exact bytes; nobody re-serializes |
| `sig` | `by`'s signature over `F("agent-bridge roster entry v1", bodyBytes)` (framing `F` below), never over the raw body, so a roster signature can never be taken for any other signature the same key makes |
| `cosig` | Only where §3.4 needs a second signer: `cosigner`'s signature over `F("agent-bridge roster cosign v1", bodyBytes)` |
| `uv` | Only for a signer (or cosigner) whose `presence` is `webauthn`, on an entry that needs presence (§3.3): the WebAuthn assertion (`authenticatorData`, `clientDataJSON`, `signature`, base64url) whose challenge is `SHA-256(F("agent-bridge presence v1", bodyBytes))` |

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
    "webauthn": { "rpId": "danswett.github.io", "credentialId": "<base64url>", "key": "<base64url, 65 bytes>" },
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
| `op` | `genesis`, `add`, `remove`, `rename`, `merge`. Grants (`set grants` in design.md §5.4) are left for later |
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
| `device.webauthn` | With `presence: webauthn` only: the relying party id, the credential id, and the credential's P-256 public key as a SEC1 point (converted from COSE at registration) |
| `device.kind` | **The bound kind**: `host`, `app`, `pwa`, `phone`. `test` is never rostered |
| `device.platform` | `windows`, `macos`, `linux`, `ios`, `ipados`, `web`. Display only |
| `device.name` | At most 64 Unicode scalar values and 256 UTF-8 bytes. The approver removes every character in the project's hidden-character set (`NeedsYou.HasHidden`, `pwa/js/needs-you.js` `HIDDEN`) before signing. A verifier never alters a signed name: it refuses one over the limits or holding a C0 or C1 control, and shows any other hidden character as a visible escape, one per scalar value (`\uXXXX`, four uppercase hex digits, up to U+FFFF; `\u{X…}` above; an unpaired surrogate as its own `\uXXXX`), as `NeedsYou.Reveal` does. It never refuses those, since unassigned code points differ between platforms' Unicode versions |
| `approval` | `code`, `qr`, `grandfather` (§8.2) or `genesis`, and the enrollment transcript hash `T` (§4.2) |

The exact field set per `op`:

| `op` | Fields besides `v`, `op`, `account`, `generation`, `seq`, `by`, `at` |
|---|---|
| `genesis` | `device` (a person's), `approval` (`method: genesis`, no `transcript`); no `prev` |
| `add` | `prev`, `device`, `approval` |
| `remove` | `prev`, `target` (a device id), optional `cosigner` |
| `rename` | `prev`, `target`, `name` |
| `merge` | `prev` (two to four) |

**Strict parsing** (all three implementations, with shared negative vectors in R1). The body
must be I-JSON (RFC 7493): valid UTF-8, no duplicate member names, no lone surrogates. Member
names and enumerated values are case-sensitive. Each object has exactly the fields the table
allows: an unknown or missing field refuses the entry. `seq` is an integer in `[0, 2^31)`,
written without a fraction, exponent or sign. Ids are exactly 32 lowercase hex characters, and
binary fields are base64url without padding that decode to exactly the stated lengths.

**Entry hash and identity.** The entry hash is `SHA-256(bodyBytes)` over the decoded bytes,
written as base64url without padding wherever it appears (`prev`, pins, `rosterHead`, logs). An
entry's identity is its entry hash alone. Two copies of one body with different signatures are
the same entry: the first valid copy is kept and the second ignored, never treated as a fork.

Kinds stay the authorization classes every check already uses. An iPhone or iPad is `phone`,
a Mac app is `app`, a Mac host is `host`, and `platform` says which. Adding a class later means
adding it to one set (`RosterKinds.IsPerson`) in each implementation.

### 3.3 Validity of one entry

An entry is evaluated in the **state of its parents** (§3.5 computes that state). It is valid
only if:

* it parses strictly (§3.2) and `account` and `generation` match;
* `sig` verifies under `by`'s `sign` key (and `cosig` under `cosigner`'s);
* `by` (and `cosigner`) are active person's devices in that state; for `genesis`, `by` is the
  device it adds. Probation is not a validity rule: it delays when a verifier applies an entry
  (§3.4);
* when the op needs presence (`genesis`, `add`, `remove`, `rename`; not `merge`), each signer's
  presence is proven: `presence: key`, or `presence: webauthn` with a valid `uv` (§4.6). Under
  decision 3(a) a signer with `presence: none` cannot make these entries at all;
* `add`: `device.id` matches `dh`; the id was never added in this generation (a removed key is
  never re-added, and a re-approved device makes a new key pair); a person's device has `sign`,
  `signAlg` and `presence`, and a host has none of them;
* `remove`: the target is active, and the seniority rule of §3.4 holds; it is not the last
  active person's device (removing that is a reset, §5.5);
* `rename`: the target is active and is `by` itself or junior to it;
* `merge`: its parents are two to four distinct heads, in head order;
* at most 4,096 entries and 64 active devices. Compaction by a signed checkpoint is left for
  when an account comes near that.

### 3.4 Seniority and probation

A new device that turns out to be hostile must not be able to undo the devices that were
there before it. So:

* **Seniority.** A device's rank is the `(seq, entry hash)` of the entry that added it;
  smaller is senior, and the genesis device is the most senior. A device may `remove` **itself
  or any device junior to it**. Removing a more senior device needs a `cosig` from a second
  active, non-probationary person's device. Both signers must be junior to the target, and
  both need presence. A lost senior device can therefore be removed by any two of the person's
  other devices, while one hostile device cannot remove anything above it.
* **Probation.** A device added in the last **72 hours** can remove only itself. It cannot add,
  remove others, rename others or cosign. The 72 hours run on each verifier's own clock from when
  *it* first accepted the `add`; `at` is never used.
  * A verifier that receives an entry signed by a device still on probation by its clock does
    not refuse the roster. It **holds** that entry, and the effects of everything signed by
    devices that entry added, until the probation ends by its clock.
  * Entries from other signers apply as usual. Removals are always safe to apply early, so a
    removal of the probationary device itself applies at once and voids what it held (§3.5).
  * An honest client does not let a probationary device sign, and an honest relay refuses to
    append such an entry by its own clock.
  * Holds only delay effects, never change which entries exist. Every verifier therefore
    reaches the same state once its holds expire.
* **Cascade.** Removing a device shows the devices it added, junior to the remover, ticked by
  default, so one confirmation can remove them too: one `remove` per device, in one batch.
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

1. `e` came from the relay, from an active rostered device on a channel (gossip, §3.6), or from
   the approver during this verifier's own enrollment (§4.2);
2. every parent of `e` is already in `S`, and `e` is valid in their state (§3.3);
3. if a parent of `e` already has a child in `S`, `e` starts a **fork**. That is accepted only if
   the parent is at most **8 entries** behind one of the verifier's heads and `S` would then have
   at most 4 heads. A fork from further back is refused and reported as relay misbehavior: it
   is how a removed device would bring back an entry it signed on an old head.

**The state** of a set of heads is computed deterministically from their common history:

* Walk the entries in `(seq, entry hash)` order. Each `add` makes a device active, and each valid
  `remove` removes one.
* **Conflicting removals** on concurrent branches (X removes Y on one, and Y, alone or as
  cosigner, removes X on the other) are settled by seniority. The removal whose most senior
  signer is more senior stands; the other is void.
* **Voiding.** An entry signed by a device that a standing removal on another branch removes is
  void, unless it is an ancestor of that removal: the device acted concurrently with being
  removed. So is anything a void entry added, and everything signed by those devices, transitively.
  A removed device's concurrent additions therefore never survive a merge.
* Probation holds (§3.4) are applied last.

A `merge` entry joins heads into one and carries no decision: its state is exactly that
computation. So until one is signed, every verifier already applies the merged state and
reports `rosterHead` as all its heads in head order. Any active, non-probationary person's
device may sign a merge. A device whose `sign` key is presence-gated (`presence: key`) cannot
sign silently: it adds the merge the next time the person confirms anything on it, and the
Devices page offers "Tidy the device list". A `presence: webauthn` device signs merges with
`sign` alone, without a `uv`, because a merge needs no presence. Every device shows "the device
list forked" while a fork is open, since an honest relay never causes one.

**Generations.** A verifier never moves to another generation because something remote says
so; only a local action does (§5.5).

**Downgrade.** Once a host or client holds a roster, nothing remote turns enforcement off:
not a relay without roster support, a relay that says there is none, a peer claiming an older
protocol, or a missing capability. Only a local setting does (§8.1).

### 3.6 Where it lives

* **The relay is an untrusted store.** It keeps one set of entries per account, beside the
  machine roster ([roster.md](../protocol/roster.md)) under the same naming and ETag rules, and
  serves it to approved devices only. It appends an entry only if it is valid by §3.5 against
  the relay's own copy. Honest approvers therefore extend its single head, and a second approver
  that finds the head moved re-reads, re-verifies and signs again on the new head without asking
  the person again (its decision was about a key, not a position). An exception: if the new head
  removed it, or already added the same device, it stops.
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
7. **A signs** the `add` (`approval.transcript = T`) with its presence proof, appends it at the
   relay, and sends N the roster from genesis through that entry.
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
never as the gate:
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
| Apple | Secure Enclave key with `.userPresence` (Face ID, Touch ID or passcode, in the system's own UI) | `presence: key` |
| PWA | A WebAuthn platform credential (`userVerification: "required"`), registered at enrollment; each proof is an assertion whose challenge is the hash of what is signed. Any host verifies it: `rpIdHash`, the UV flag in the signed `authenticatorData`, `type: webauthn.get`, the origin, the challenge, and the signature under the credential key in the roster | `presence: webauthn`, from R5 |
| Windows app | R4 must pick one mechanism and **prove** that same-user code cannot satisfy it. It must hold against `SendInput`, UI Automation and this project's console injector, running unelevated at the same integrity level. The candidates are the Windows WebAuthn API (`webauthn.dll`, Windows Hello, which makes ES256 credentials, unlike `KeyCredentialManager`'s RSA keys) and a TPM key with an NCrypt UI protection policy or PIN | `presence: none` until that proof; then `webauthn` or `key` |

What a presence proof does **not** cover: malware that waits for the person's own approval and
swaps what is approved. The confirmation screen shows the device's name, kind and 16-character
id, and the signature covers the entry, but on a compromised approver the screen can lie. Two
cases remain:
* **Synced passkeys.** A PWA credential that the platform syncs (iCloud Keychain, Google
  Password Manager) exists on the person's other devices of that platform. Each still needs the
  person's verification. The roster records the credential, not which device used it.
* **A script in the PWA's origin.** Any script on the Pages origin (a repository, Actions or
  Pages compromise, a dependency, or XSS in rendered agent output) can ask for an assertion. The
  system's prompt then appears, but it cannot say what is being approved. The PWA's
  content-security policy and the existing rule that agent output is never rendered as HTML are
  what keep such a script out.

Under owner decision 3(a), only `presence: key` or `webauthn` devices approve or do person-only
actions. Under 3(b), `none` devices may approve too, but nothing they add can add `host` or
`app` devices or leave probation until a presence-proven device confirms it. That bounds them,
but a script in the Pages origin or same-user malware could still approve a device on its own.

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
approved from one of them instead. Under decision 3(a) the first device is the PWA with its
passkey (R5), or an Apple client, or the Windows app once R4 has proven its presence.

### 5.2 How a host comes to trust a roster

Only by enrollment, where the roster comes from the approver whose code 2 the person typed at
the machine and whose genesis fingerprint the machine showed, or by a local action at the machine
(§5.5). Never by first use of whatever the relay serves. So an attacker's genesis, made with a
stolen token or by the relay, is never trusted by a machine the owner has not typed its code 2 into.

### 5.3 Clients

A person's device trusts the roster it was enrolled into, by the same mutual check. The genesis
device trusts its own.

### 5.4 Losing a device

Remove it from another (§6). A lost senior device needs two of the person's other devices
(§3.4). With the PWA and an Apple or Windows device that can approve, losing one is routine.

### 5.5 Losing every person's device: reset

A **reset** makes a new generation on a new person's device (typed `RESET`), and every device
enrolls again into it. A host moves to the new generation only by a local action at the machine:
* the app there, over the pipe, after a mutual enrollment with the new genesis device; or
* `agent-bridge-host devices reset`, interactively, refused inside agent sessions (#67).

Until then it keeps the old generation, which refuses everyone when nobody in it is left. That is
fail-closed: a machine the owner cannot reach stays unusable remotely until they can.

An optional **recovery key** (owner decision 5) would avoid the trip. At genesis, a person's
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
* A squatted first genesis (one made with a stolen token before the owner's) never reaches a
  machine (§5.2). The owner operates the relay, so it is removed with a relay administration
  action ([relay-deploy.md](../relay-deploy.md) will document it in R2), which no verifier relies on.

This binds an honest relay. A dishonest one can store anything, and no verifier holding a roster
follows it.

## 6. Removal

### 6.1 The person

A person's device that can approve has a **Devices** page listing the roster: name, kind,
platform, 16 hex characters of the id in groups of four, when and by whom each device was added,
whether it is on probation, and whether it is online now or when it was last seen. It also shows
the genesis fingerprint. **Remove** takes two presses (the first arms it for 5 seconds, "Press
again to remove", as Forget does), then the presence check. It offers the cascade (§3.4), signs
the `remove` entries, appends them, and sends them to every connected host and device directly
as well as through the relay. A more senior device asks for a second device's cosignature: the
other device shows "*Desk app* asks to remove *Old laptop*, which is senior to it" and needs its
own presence check. The last person's device cannot be removed.

A device can remove itself ("Remove this device"), which also signs it out.

### 6.2 Revocation reaching every host

* **Online hosts** get the entry from the relay at once and from the remover directly. They close
  the device's channels, drop its push subscriptions, and refuse it from then on.
* **Offline hosts** get it when they reconnect, from the relay and from the first rostered peer
  they exchange heads with (§3.6).
* **The relay** refuses the removed key at admission, so an honest relay cuts it off everywhere at
  once.

### 6.3 Freshness: a withholding relay is bounded

A relay that withholds a removal from a host, and routes only the removed device to it, could
otherwise keep it served indefinitely. So a host requires **fresh** evidence of the account's
current roster from someone other than the device asking:

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
  heads the host has. The time is the host's own clock from receipt.
* **Stale.** A stale host serves relay devices nothing but `hello`, `roster.*` and attestations
  (owner decision 10), and says so in `host.status`, the app and the log.
* **The one exception** is an account whose roster has exactly one active person's device. No
  removal can be withheld from it (the last person's device cannot be removed), so that device's
  own attestation counts.

What remains: within the 24 hours after the last independent attestation, a host that only the
removed device reaches can still serve it. That needs a dishonest relay **and** the removed
device's keys, and ends at the first honest contact or at the window.

### 6.4 Every change is announced

Every `add`, `remove`, `rename` and open fork is shown on every person's device ("*Kitchen
laptop* was added by *Desk app* at 14:05 PT") and logged by every host. Probation (§3.4) gives the
person 72 hours to notice a device they did not approve before it can act, and seniority means
it cannot remove the devices that would notice.

### 6.5 What a removed device sees

At its next connection the relay refuses it with `removed`, and a host it still reaches refuses it
in `hello`. It says "This device was removed from *login*'s devices by *Desk app* on Nov 2.",
deletes what it kept about the account (sessions, usage, notifications, its roster copy) and its
push subscriptions, and offers "Sign in again", which makes a **new key** and starts enrollment
again. A removed host stops serving relay devices, says so in the app and the host log, and keeps
serving its own machine's apps over the pipe.

## 7. Enforcement

### 7.1 Hosts: who gets a channel's ops

The host's `ChannelHub` gets a roster gate. For a channel from a device that is not active in
the host's roster, the handshake completes so the device can learn why, and then:

| Op | Unrostered device |
|---|---|
| `hello` | Answered with `approved: false` and nothing else: no heads, no roster, no session, capability or machine detail |
| Everything else, including `roster.*` | Refused with `not-approved` |

Recommended: **nothing beyond "pending approval"** (owner decision 4). A pending device gets its
roster from its approver during enrollment (§4.2).

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
* the body parses strictly, and `sig` and the presence proof verify (`presence: none` never
  passes under decision 3(a));
* `by` is an active, non-probationary person's device, its kind read from the roster;
* `target.machine` is this host, and the action and its target match the op;
* the lifetime is at most 10 minutes, it was not issued more than 2 minutes in the host's future,
  and it has not expired;
* `request` was never used before (the existing replay store);
* where the command arrives on a channel from `by` itself (folder trust, update), `nonce` is one
  the host gave that channel in `hello`;
* the host is fresh (§6.3) and holds every head the command names, syncing first if not.

| Action | Today | With person commands |
|---|---|---|
| Transfer (`SessionTransfers.Unsigner`) | `CommandMac` by a device present as `pwa`/`app`/`phone` | A person command for the source, `to` the target. There is no nonce, since the source never talks to `by`; single use and the 10-minute lifetime stand in. The signer is still neither the asking host nor `to`, and is still in presence |
| Folder-trust answer (`FolderTrustAnswers`) | `CommandMac` by the peer, kind `pwa`/`app`, plus the allow-list | A person command with the host's nonce, plus the allow-list (§9.2) |
| Remote update (`UpdateService`) | Peer kind `pwa`/`app` | A person command with the host's nonce |
| Forget or Remove a machine (#138) | Relay checks the hello kind | Under decision 7(a), Remove is a roster entry (§6). Under 7(b) the relay requires a person command for `forget` |

`CommandMac` signed commands are retired for these once a host is in `enforce`. Each person-only
press now asks for the presence check: Face ID, the passkey prompt, or Hello. That is deliberate,
and acceptable for actions this rare.

### 7.3 What stays on channel identity

Session ops (reading, replies, answers, launches, stops) still rest on the channel's device key.
They are frequent, and a presence check on every reply would be unusable. So malware or a
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

Relay tokens are bound to the device key, as design.md §5.4 plans. A token names the device id it
was issued to (sign-in proves the key, as admission does), and admission refuses a token
presented with another key. A stolen token then admits only the key it was issued with, so it
does not add pending devices or flood the approval banner. A new device flow can still mint a
token for a new key, and the roster, not the token, is the gate.

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

Existing devices are **grandfathered by an explicit approval, never silently**. Recommended (owner
decision 6): every existing device enrolls with the mutual codes, once, as a new one would:

1. Release the relay, then hosts and clients with roster support in `off`, then `observe` (§8.3).
2. The owner starts the roster on a device that can approve (§5.1): the phone PWA with its
   passkey, under decision 3(a).
3. For each machine, its Windows app shows code 1 and the owner types it on the phone; the phone
   shows code 2 and the owner types it into the app, after checking the fingerprint. One exchange
   covers the app and the host there (§4.7). Then any other PWAs, and the Apple clients when they
   ship. Each machine starts its 72-hour probation, which only limits what it can approve.
4. Every host in `observe` now reports what it would refuse. After a quiet day, the owner switches
   each host to `enforce` (in the app's Settings, or with
   `agent-bridge-host config set security.roster enforce` at the machine).
5. A later release makes `enforce` the default for new installs: a new machine serves its own apps
   until approved.

About six exchanges for today's fleet. The lighter alternative, decision 6(b), is a one-time
**bulk grandfather** on the genesis device. It lists the devices online at that moment, each with
its kind and its **whole id** in groups of four. The owner ticks each one after comparing the whole
id with the one that device shows locally (a prefix is not enough), and the genesis device signs
`add` entries with `approval.method: "grandfather"`. Nothing is pre-ticked, including ids from
`launch.remoteFolderTrustDevices`: the names beside them came from the relay and can be look-alikes
(#157). This path has no mutual check. A device that came from a stolen token, was online at that
moment and is named like one of the owner's is caught only by the id comparison. A grandfathered
machine also never checked which roster it joined, so each machine must still confirm the genesis
fingerprint locally (in the app or the CLI) before it holds the roster.

### 8.3 Order of releases

1. **Relay** (staging, then production **only with the owner's approval** of the `relay.yml`
   production job): roster storage and appends (§3.6), approved/pending admission with invite
   hints, nothing for pending devices, bound kinds in `presence`, the generation rules (§5.6), token
   binding (§7.6), the `removed` close code. Additive: an old client ignores new fields, and the
   relay keeps today's behaviour for an account with no roster.
2. **Hosts** (Windows, then the Mac host), shipping in `off`: the verifier and state, gossip,
   freshness, person commands, `roster.*` ops, enrollment through the pipe and the CLI, `observe`
   and `enforce`.
3. **PWA** with its passkey: join, approve, Devices, remove, person commands. Under decision 3(a),
   this is the first device that can approve.
4. **Windows app**: join, Devices, and the client rule (§7.4); approving and person commands only
   after R4's proof of presence.
5. **Apple clients**: join first, then approve. They must ship join before any host the owner uses
   them with is in `enforce`.
6. **The owner's cutover** (§8.2), per host.
7. **Default `enforce`** for new installs.

### 8.4 Old and new together

| Combination | Outcome |
|---|---|
| Old client, enforcing host | Refused with `not-approved`; the client shows the error text. Windows apps update within 30 minutes, and the PWA on reload |
| New client, old host | The client sees no heads in `hello`. In `observe` it warns; in `enforce` it refuses to send, saying "update this machine". Old hosts update with their app |
| New client, old relay | No roster store, so enrollment is unavailable and says why. Devices that hold a roster keep enforcing it and gossip |
| New host, old relay | The same: a host in `enforce` keeps its roster and syncs from peers. Freshness still works through peers and the pipe |
| agent-ha-bridge | Unaffected: nothing in it reads the relay, and the HA connector's contract is unchanged |

## 9. Effect on other work

### 9.1 Transfer (#70)

Transfers need a person command (§7.2) from an active, presence-proven person's device, and an
active host as the asker, `to` and the source. This closes the gap #70 records for a compromised
machine **only for devices with a presence proof**. A device with `presence: none` cannot sign
one under decision 3(a), and under 3(b) it is the residual of §4.6. The "What this does not stop
yet" paragraphs of [v1-transfer.md](../protocol/v1-transfer.md) and
[host-transfer.md](../host-transfer.md) are rewritten in R6 to say exactly that.

### 9.2 Folder trust (#133, #157)

A folder-trust answer needs a person command with the host's nonce. `launch.remoteFolderTrustDevices`
stays as a per-machine choice of which approved devices may answer. An id there that is not active
in the roster is ignored and shown as such, so removal revokes folder trust at once. Settings shows
each device's roster name, kind, 16 characters of its id, and when and by whom it was added, which
closes #157 item 4. Items 1 and 2 of #157 (tightening only after a restart, terminal writes past the
lock) are unaffected and still open.

### 9.3 Forget (#138)

Owner decision 7 is whether Forget on a host becomes Remove:
* **Remove revokes and forgets** (recommended). It is a roster entry with a presence proof (§6),
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
* the signing key (Secure Enclave `P256.Signing` with `.userPresence`, so `presence: key`), created
  at first run beside the device key. Without a Secure Enclave, a Keychain key, `presence: none`;
* strict parsing, the roster state of §3.5 (seniority, probation holds, voiding), gossip in `hello`,
  head attestations, and the shared vectors;
* **join**: the pending screen, both codes, the genesis fingerprint, the invite hint and both QR
  directions, and verifying the returned roster. This is needed before any host they talk to
  enforces;
* **approve**, **Devices** (remove, cascade, cosign) and **person commands** (transfer, folder trust,
  update), each behind Face ID or Touch ID;
* the client rule of §7.4, and the bound kind for what they show.

The iPhone and iPad client already connects as `phone` and the Mac app as `app`, which are the bound
kinds they get. The Mac host enrolls with the Mac app as in §4.7, or by its CLI.

## 10. Tests

Each slice adds its own. Together they cover:

**Acceptance (the issue)**
* A device of the account that is not in the roster is refused every op but `hello` on a host in
  `enforce`, and learns only that it is pending.
* A device added with the mutual codes on the PWA (or an Apple client, or the Windows app after R4)
  is served by every host, including one that was offline during the approval.
* A removed device is refused by every host, including one that was offline at the removal.

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
  raw body rather than `F("agent-bridge roster entry v1", …)`; an entry signed by a host, a removed,
  pending or probationary device; one that needs presence but lacks it, or whose `uv` has no UV
  flag, the wrong challenge, origin or rpId: refused.
* Another account or generation; a wrong `seq`; a `prev` out of head order or unknown; a re-added
  removed id; removing the last person's device; a second genesis: refused.
* Negative parser vectors (shared by C#, JS and Swift): a duplicate member, an unknown field, a
  missing field, a field from another `op`, a fractional, exponent, negative or string `seq`, wrong
  case in a name or value, invalid UTF-8, a lone surrogate, base64 with padding or the wrong length,
  and an id in uppercase. Each is refused.
* An off-curve or malformed key; a high-S or non-minimal DER signature: refused. A second valid copy
  of an entry with another `sig` is the same entry, not a fork.

**Seniority, probation and forks**
* A junior device's `remove` of a senior one without `cosig` is refused; with a valid cosig from a
  second junior device it stands.
* A device on probation cannot add, remove others, rename others or cosign. A verifier holds such an
  entry until 72 hours after it first accepted that device's `add`, by its own clock, whatever `at`
  says, and applies a removal of that device at once.
* A removed device signs an `add` on a head from before its removal: it is void in the merged state,
  and so is everything the device it added signed, transitively.
* Conflicting removals on two branches: the more senior signer's stands.
* A fork more than 8 entries behind every head, or a fifth head: refused.
* An entry offered by an unrostered channel peer: refused. A merge whose state differs from the
  computed one: impossible by construction (it carries no state).
* The cascade offers exactly the devices the removed device added that are junior to the remover.

**Person commands and kind spoofing**
* A transfer, folder-trust answer, Install or forget from an **active `app` channel without a
  presence-proven person command** (the compromised-machine case): refused.
* A person command from a `presence: none` device: refused under 3(a). Without the host's nonce,
  where one is required: refused. Replayed, expired, for another machine or action: refused.
* A rostered host's key connecting with `hello` kind `pwa`: refused everywhere a person is needed.
  An unrostered key with kind `pwa` or `app`: refused everywhere. A rostered `pwa` connecting as
  `host`: not used as a host-to-host peer.

**Freshness and removal while offline**
* A host that was offline at a removal learns it from the relay at reconnect. With a relay that
  withholds it, the host learns it from the first rostered peer or from its app over the pipe.
* A host with no independent attestation for 24 hours serves relay devices only `hello`, `roster.*`
  and attestations. The removed device's own attestation does not keep the host fresh for it.
* A removed device is told so, wipes its data and push subscriptions, and makes a new key to join
  again.

**Relay**
* A token presented with another key is refused. A reset genesis is held 72 hours, announced, and
  ended by a veto. A pending device receives no roster and no heads.

**Interop**
* Shared vectors for `F`, entries, signatures (DER, low-S), entry hashes, head order, `C`, `T`, both
  codes (including leading zeros), the QR MACs and invite hint, head attestations, person commands,
  WebAuthn assertion checks, and the merged state of a forked fixture. They run in C#, the PWA and
  Swift, as the command MAC's vectors do.
* A name holding a hidden character: removed by the approver before signing; refused by a verifier
  if it is a control; shown escaped otherwise.

## 11. Decisions for the owner

1. **Trust root.**
   (a) Approved, non-probationary person's devices sign roster entries with a new ECDSA key and a
   presence proof, under seniority (§3.4); hosts never sign. (b) One account root key on the first
   device. (c) Per-machine approval lists only.
   **Recommended: (a)**, with seniority and probation (decision 9) and the merge voiding of §3.5.
   Without them, one hostile device could remove every other. Under (a), the most senior device is
   the most powerful one, and if it is stolen and unlocked the answer is a reset.
2. **Hosts in the roster.**
   (a) Hosts are approved by the mutual codes like any device, and confirm the genesis fingerprint
   at the machine; clients refuse unapproved hosts. (b) Hosts are trusted on sign-in as today, and
   only person's devices are approved.
   **Recommended: (a)**, since the mutual check of §4.2 stops a relay from enrolling a host into a
   roster of its own. Under (b), a stolen token registers a fake machine that collects what people
   type.
3. **Who can approve and do person-only actions.**
   (a) Only presence-proven devices: Apple with the Secure Enclave, the PWA with a passkey (R5), and
   the Windows app once R4 proves its presence cannot be satisfied by same-user code. Until then
   the Windows app can be approved and drive sessions, but cannot approve or do person-only actions.
   (b) Also devices without presence (the PWA without a passkey, an unproven Windows app), whose
   additions stay on probation and cannot add `host` or `app` until a presence-proven device
   confirms them. Any script in the PWA's origin, or same-user malware on a Windows machine, could
   then approve a device or press a person-only action on its own.
   (c) One code, compared rather than typed, with no presence requirement.
   **Recommended: (a).** The cutover then starts on the phone PWA with its passkey (R5 before R8).
4. **What an unapproved device of the account sees.**
   (a) Nothing beyond "pending approval": no roster, no heads. (b) A read-only list of machines.
   **Recommended: (a).**
5. **Recovery when every approved person's device is lost.**
   (a) Reset to a new generation, held 72 hours at the relay with veto, and confirmed at each machine.
   (b) As (a), plus an optional offline recovery key made at genesis.
   **Recommended: (a).** A recovery key is one more secret that, with the GitHub account, takes the
   account. With two approving devices and cosigned removal of seniors, total loss is unlikely.
6. **Grandfathering the devices in use now.**
   (a) Enroll each with the mutual codes once (one exchange per machine, about six). Hosts go to
   `observe` first, then `enforce` per host. (b) Bulk-approve the devices online at cutover after
   comparing whole ids, with nothing pre-ticked (not even `remoteFolderTrustDevices` ids, whose names
   can be look-alikes) and each machine confirming the fingerprint locally. (b) has no mutual check.
   **Recommended: (a).**
7. **Forget and Remove for a machine.**
   (a) One action, Remove, a presence-proven roster entry that revokes and forgets; a machine that
   returns waits for approval. (b) Keep Forget listing-only (behind a person command), with Remove
   separate.
   **Recommended: (a).**
8. **Which kinds count as a person's.**
   (a) `app`, `pwa` and `phone` everywhere, so the native iPhone client can answer folder trust and
   press Install. (b) Keep folder trust and update to `app` and `pwa`.
   **Recommended: (a)**, provided person-only actions need presence-proven person commands (§7.2).
   The kind alone does not show that a person acted.
9. **Seniority and probation.**
   (a) A device removes only itself or junior devices; removing a senior device needs two junior
   devices' signatures. New devices have 72 hours of probation (by each verifier's clock). Removing a
   device offers to remove the devices it added. (b) Seniority without the two-signature removal: a
   lost senior device then always means a reset. (c) Neither: any approver removes any device, and
   one hostile device can remove all the others before the person notices.
   **Recommended: (a).**
10. **Freshness.**
    (a) A host without an attestation of the current roster from another device for 24 hours serves
    relay devices only `hello` and roster sync. (b) The same, but a stale host refuses only
    person-only actions. A removed device can then go on driving sessions there while a dishonest
    relay withholds its removal. (c) No freshness check: that can last indefinitely.
    **Recommended: (a).** Other hosts every 5 minutes, and the app on the same machine, keep a host
    fresh in normal use.

## 12. Slices

Each is one PR, security-reviewed independently before merge.

| # | Slice | Depends on | Changes behaviour |
|---|---|---|---|
| R1 | Roster library: strict parser and negative vectors, entries and signing contexts, the acceptance rule and state (seniority, probation holds, voiding, merges), person commands, head attestations, WebAuthn assertion checks, signing keys (TPM/DPAPI, WebCrypto), shared vectors (C#, JS); protocol spec `docs/protocol/devices.md` | decisions 1, 2, 9 | No |
| R2 | Relay: store and appends, approved/pending admission with invite hints, nothing for pending devices, bound kinds in presence, generation rules with hold and veto, token binding, pending limits, `removed` close code, administration reset for a squatted genesis. Staging; production only with the owner's approval | R1, decision 5 | Only for accounts with a roster |
| R3 | Host: state, gate in `ChannelHub`, gossip, freshness, person commands, modes `off`/`observe`/`enforce`, enrollment over the pipe and `devices join`/`reset` with the genesis fingerprint | R1, decision 10 | Only when the mode is changed |
| R4 | Windows app: join (mutual codes, fingerprint), Devices, client rule; **proof** that its presence mechanism (Windows WebAuthn/Hello, or a TPM UI policy) cannot be satisfied by same-user code, including `SendInput`, UI Automation and the project's injector. Only after that does it approve or sign person commands | R2, R3, decision 3 | Only after genesis |
| R5 | PWA: passkey registration, join, approve (codes and both QR directions), Devices, remove, cascade, cosign, person commands, pending screen, client rule | R2, R3, decision 3 | As R4 |
| R6 | Person-only actions on person commands: transfer, folder trust, update, forget/remove; one `RosterKinds.IsPerson`; usage peers and transfer hosts from the roster; docs for #70, #133, #138, #161 and the HA connector note | R3, R5, decisions 7, 8 | In `enforce` |
| R7 | Removal end to end: revocation, offline hosts, removed-device experience, Forget/Remove | R5, R6 | Yes |
| R8 | Owner cutover: runbook and live checks (the issue's "Validate live"), then `enforce` per host | R1-R7, decision 6 | On the owner's machines, with their go-ahead |
| R9 | `enforce` by default for new installs | R8 | Yes |
| A1 | Apple: join, then approve, Devices and person commands (contributor) | R1, R2 | For Apple clients |
| L1 | Later: recovery key if chosen, checkpoints, CPace in place of two codes if wanted, presence-proven Allow-all launches | | |
