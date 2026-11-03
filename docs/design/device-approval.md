# Device approval and the signed roster (#55)

**Status: proposed design, not built.** It needs the owner's decisions (§11) and an
independent security review before any slice starts. It refines design.md
[§5.4](../design.md#54-accounts-devices-and-the-signed-roster). Where the two disagree,
this note is the newer reasoning; §5.4 will be updated when the decisions are made.

The aim: a device that signs in with the account's GitHub login can do nothing until a
person approves it, on a device that is already trusted, by matching a code. Every host
then acts only for approved devices, and person-only checks use the kind bound at
approval rather than what a device says about itself.

## 1. Where things stand

| Today | Where | Consequence |
|---|---|---|
| The relay admits any key that proves possession and holds a valid account token, and takes `kind` and `name` from the device's own `hello` | `RelayHub.AdmitAsync` | Anything holding the token can connect a fresh key as `pwa` |
| A host completes a Noise handshake with any device in the account's presence and serves it the full `SessionService` handler | `ChannelHub.OnDataAsync`, host `Program.cs` | Any device of the account can read and drive every session (#186 review) |
| Person-only checks read the relay's (self-declared) kind | `SessionTransfers.Unsigner` (`pwa`, `app`, `phone`), `FolderTrustAnswers` and `UpdateService` (`pwa`, `app`), relay `ForgetAsync` (`app`, `pwa`, `phone`) | A compromised host can reconnect as `pwa` and authorize transfers, answer folder trust where it is allowed, press Install, and forget machines (#138 review, #70 comment) |
| The three host checks list person kinds separately, and they differ | as above | The native iPhone client (`phone`, #260) can sign transfers but cannot answer folder trust or press Install |
| Folder trust has a per-machine allow-list of device ids (`launch.remoteFolderTrustDevices`) | `LaunchConfig`, Settings | The list shows the relay's names, so look-alikes are possible (#157 item 4) |
| Device keys are P-256 **ECDH-only**: a TPM key, a non-extractable WebCrypto key, a Secure Enclave key-agreement key | `DeviceKeys`, `pwa/js/noise-p256.js`, the Apple clients | No device can make a signature that a third party can verify. Signed commands are designated-verifier MACs from static-static ECDH (`CommandMac`), which only the signer and one named machine can check |
| The account token is a bearer token, not bound to the device key | `Tokens.Issue` | A stolen token admits any key |

The last two rows shape the design: a roster that every host checks on its own needs a
signature anyone can verify, and the token cannot be the gate.

## 2. Threat model

In addition to design.md [§5.1](../design.md#51-threat-model):

| Party | Assumption | What it must not be able to do |
|---|---|---|
| **The account's GitHub sign-in** (a stolen token, a phished device flow, a leaked relay session token) | Can admit any number of new keys to the relay, with any `kind` and `name` | Read or drive a session, pass a person-only check, appear as an approved machine, or approve itself |
| **The relay** (operator, storage, a compromised deployment) | Untrusted for authorization. It stores and serves the roster, routes frames, and can drop, delay, reorder, replay or withhold anything | Forge or alter a roster entry, forge an approval, make a verifier accept an older roster, or substitute keys during enrollment without the codes differing. It **can** deny service, hide new entries for a while (§6.2), and show a pending device a misleading name |
| **A compromised host** (an agent or malware running as the user on a rostered machine) | Holds that machine's host key and the account token. If the same OS user also runs the Windows app there, it can use the app's key too: TPM keys cannot be copied off, but can be used while the attacker is on the machine | Pass a person-only check with the **host** key, add or remove devices with it, or take sessions from other machines without a person's press. What it can do with a co-located app key is bounded by §4.6 (user verification) and is the residual risk named there |
| **A pending (unapproved) device** | Has completed sign-in and proved its key | See machines, sessions, usage or names; receive pushes or blobs; reach any host op except learning it is pending |
| **A removed device** | Was approved; still holds its keys and maybe a valid token | Anything a pending device cannot, from the moment each host learns of the removal (§6.2) |
| **The person at an approved device** | Trusted | They are the only source of approval, by matching a code |
| **The local pipe and the HA connector** | Unchanged: same-OS-user trust, and Home Assistant credentials respectively | Out of scope for the roster; see §9.5 |

Non-goals: protecting against the person approving an attacker's device on purpose,
against malware that waits for the person to approve something themselves, and
availability against the relay.

## 3. The signed roster

### 3.1 Who signs it

**Every approved person's device holds a second key, a P-256 ECDSA signing key**, beside its
ECDH device key, created on the device and never exported:

| Client | Signing key storage |
|---|---|
| Windows app | TPM via the Platform Crypto Provider (`ECDSA_P256`, more widely supported than the `ECDH_P256` S6 needs), DPAPI fallback, or a key bound to Windows Hello (§4.6) |
| PWA | WebCrypto `ECDSA` P-256, `extractable: false`, in the same IndexedDB as the device key |
| iPhone, iPad, Mac app | Secure Enclave `SecureEnclave.P256.Signing`, data-protection Keychain fallback |

**Any approved person's device can append an entry** (multi-approver chain, owner
decision 1). **Hosts never sign** and are never approvers, so a compromised host's own key
cannot add, remove or rename anything. The chain is rooted in a genesis entry made by the
first person's device and signed by it.

Considered and not recommended:

* *Pairwise MACs per host*, extending today's signed commands. They cannot reach a host that
  is offline at approval time or joins later, so every approval would have to be repeated on
  every host.
* *A single account root key* held by the first device. Losing that one device, or a
  browser evicting the PWA's IndexedDB, loses the account; every approval needs that device.
* *Per-host approval lists only*, like `launch.remoteFolderTrustDevices`. They cannot be
  approved from a phone away from the machine, and N devices times M machines approvals do
  not scale. The folder-trust list stays as an extra, per-machine narrowing on top (§9.2).

### 3.2 Entries

An entry is sent as exact bytes (`body`, base64url, no padding) with a detached signature,
as signed commands are, so no end has to serialize JSON identically:

```json
{ "body": "eyJ2IjoxLC…", "sig": "MEUCIQ…" }
```

`body` decodes to:

```json
{
  "v": 1,
  "account": "<account id>",
  "generation": "<32 hex, random, fixed by genesis>",
  "seq": 7,
  "prev": "<base64url SHA-256 of entry 6's body bytes>",
  "op": "add",
  "by": "<signer's device id>",
  "at": "2026-11-02T22:14:05Z",
  "device": {
    "id": "<32 hex: first 16 bytes of SHA-256 of dh>",
    "dh": "<65-byte uncompressed P-256 ECDH key, base64url>",
    "sign": "<65-byte uncompressed P-256 ECDSA key, base64url; absent for a host>",
    "kind": "phone",
    "platform": "ios",
    "name": "Travel iPhone"
  },
  "approval": { "method": "code", "transcript": "<base64url SHA-256 of the enrollment transcript>" }
}
```

| Field | Rules |
|---|---|
| `v` | 1. A verifier refuses any other version; a new version is a deliberate protocol change |
| `account` | The account id the relay issued. A roster entry from another account never verifies |
| `generation` | Chosen at genesis. A reset (§5.5) is a new generation |
| `seq`, `prev` | `seq` is 0 at genesis and one more than its parent otherwise; `prev` is the SHA-256 of the parent's **body bytes** (not of the signature, so a re-encoded ECDSA signature cannot make a second entry with the same content) |
| `op` | `genesis`, `add`, `remove`, `rename`. Grants (`set grants` in §5.4) are left for later |
| `by` | The signer's device id. It must be an active person's device in the state the parent leaves; for `genesis`, the device the entry adds |
| `at` | When the signer says it signed. Shown to people ("added by Desk app at 14:05"); never used to order or expire anything |
| `device.kind` | **The bound kind**: `host`, `app`, `pwa`, `phone`. `test` is never rostered |
| `device.platform` | `windows`, `macos`, `linux`, `ios`, `ipados`, `web`. Display only: authorization reads `kind` |
| `device.name` | At most 64 characters, control and bidirectional-override characters removed. Proposed by the new device, editable by the approver, and signed with the rest |
| `approval` | How the person approved: `code`, `qr`, `grandfather` (§8.2) or `genesis`, and the enrollment transcript hash, so an entry can be traced to the exchange that produced it |

Kinds: on the wire the kind is the authorization class, and it stays the set every check
already uses. An iPhone or iPad is `phone`, a Mac app is `app`, a Mac host is `host`; the
platform says which. Apple clients therefore add platforms (`ios`, `ipados`, `macos`), not
kinds. Adding a class later means adding it to one set (`RosterKinds.IsPerson`) in each
implementation, not to several.

`add` carries `device`; `remove` carries `device.id` only; `rename` carries `device.id` and
`device.name`.

The signature is ECDSA P-256 with SHA-256 over the body bytes, encoded as DER (WebCrypto's
raw `r‖s` is converted). A verifier checks every public key in an entry is on the curve
before using it, the requirement S2 added after WebKitGTK accepted an off-curve point.

### 3.3 Validity

A verifier accepts an entry only if all of these hold, and otherwise refuses the whole
roster as offered, keeping what it had:

* `v`, `account`, `generation` match; `seq` and `prev` extend the verifier's chain;
* the signature verifies under `by`'s `sign` key, and `by` is an active person's device
  (`app`, `pwa`, `phone`) in the state before this entry;
* `add`: the id is the hash of `dh`; the id was never in this generation before (a removed key
  is never re-added; a re-approved device makes a new key pair); a person's device has `sign`
  and a host has none;
* `remove`: the id is active; it is not the last active person's device (removing that is a
  reset, §5.5);
* `rename`: the id is active;
* `genesis`: only at `seq` 0, adding a person's device that is `by` itself;
* the chain has at most 4,096 entries and 64 active devices. Compaction by a signed
  checkpoint is left for when an account comes near that.

### 3.4 Versioning and rollback

Each verifier keeps a **pin**: `(generation, seq, hash of that entry's body)`, the newest
entry it has accepted, written before it acts on the new state.

* **Extension only.** An offered roster is accepted only if it contains the pinned entry at
  the pinned `seq` and continues from it. A shorter roster, or one that does not contain the
  pin, is a rollback and is refused; the verifier keeps its own copy and says so (host log,
  app and PWA banner).
* **Fork.** Two valid entries with one parent can only exist if the relay showed two
  approvers the same head at once (it serializes honest appends with conditional writes,
  §3.5). A fork is a hard error, as §5.4 says, and fails closed: the verifier stays on its
  pinned branch, also applies every validly signed `remove` it has seen on any branch, and
  refuses further `add`s until a person resolves it. Resolution is a later slice (a signed
  entry naming the abandoned head) or a reset.
* **Generations.** A verifier never moves to a new generation because something remote says
  so; only a local action does (§5.5).
* **Downgrade.** Once a host or client has a pin, nothing remote can turn enforcement off:
  not a relay without roster support, a relay that says there is no roster, a peer that
  claims an older protocol version, or a missing capability. Only a local setting does (§8.1).

### 3.5 Where it lives

* **The relay is an untrusted store.** It keeps one append-only chain per account, beside
  the machine roster (`docs/protocol/roster.md`) and under the same naming and ETag rules,
  serves it, and accepts an append only if it extends the current head (so two honest
  approvers cannot fork it; the second re-reads, re-verifies and signs again on the new head
  without asking the person again, unless the new head removed it or added the same device).
  The relay verifies signatures too, for its own decisions (§4.5), but nobody relies on that.
* **Every host and every person's device keeps its own verified copy and pin**: the host in
  its profile (private folder, as `PrivateFolder` writes), the Windows and Mac app in their
  profiles, the PWA in IndexedDB, the Apple clients in their container with the pin in the
  Keychain.
* **Gossip.** Every channel between two rostered devices starts by exchanging heads
  (`roster.head` in the channel's `hello`), and the one behind fetches the missing entries from
  the other (`roster.sync`). Hosts already open channels to each other (`UsagePeers` every
  5 minutes, transfers), so a host learns new entries from any up-to-date peer even if the
  relay withholds them.

### 3.6 How a host verifies

On every roster it is offered (relay message at connect and on change, `roster.sync` from a
peer, the chain an approver hands it at enrollment), a host runs §3.3 from its pin forward,
writes the new pin, and then:
* closes channels from devices that are no longer active;
* drops their push subscriptions (`push.*`) and anything else it holds per device;
* recomputes which devices may do person-only ops.

A host that cannot verify (store unreadable, corrupt file) keeps enforcing its last good copy;
unreadable is not absence.

## 4. Enrollment

### 4.1 What the person sees

1. The new device signs in. It shows **"Waiting for approval"**, a six-digit code, and on a
   desktop a QR code too. Nothing else: no machines, no sessions.
2. On any approved person's device (the Windows app, the PWA, a Mac or iPhone app), a banner
   reads "A new device wants to join: *Kitchen laptop*, Windows app, signed in 2 min ago".
   The person opens it, **types the code** shown on the new device (or scans its QR), and may
   edit the name.
3. The approver confirms "Add *Kitchen laptop* (Windows app)?" with user verification where
   the platform has it (§4.6). The new device turns into a normal one within a second or two.

The other direction is supported for a phone: the desktop app shows **"Add a phone"** with a
QR code, and the phone scans it (§4.3). Either way, the person proves the two screens belong
together; nothing is typed that would be secret later.

### 4.2 The code exchange

Run over a Noise `XX` channel between the approver A and the pending device N, opened by A
through the relay (a pending device is reachable only this way, §4.5). After the handshake,
each side knows the other's static key and the handshake hash `h`, which binds both keys,
both ids (the prologue) and the channel. A man in the middle (the relay) would have two
channels with two different `h`.

A plain six-digit code derived from `h` would be weak: a man in the middle completes one leg
first and then tries about 10⁶ ephemerals on the other until the codes collide. So the code
also takes nonces in a commit-then-reveal order, the agent-ha-bridge fleet-pairing protocol
(`docs/fleet-pairing.md` there) with its HA steps replaced by the channel:

1. **N → A: commit.** N picks a 32-byte nonce `nN` and sends
   `C = SHA-256("agent-bridge enroll commit v1" ‖ h ‖ nN)` with its proposed `kind`,
   `platform` and `name`. A person's device also sends its `sign` key and a signature by it
   over `h`, which proves it holds that key on this channel; a host has no `sign` key.
2. **A → N: offer.** A picks `nA` and sends it. A has not seen `nN`.
3. **N → A: reveal.** N sends `nN`. A checks it against `C` and abandons the attempt otherwise.
4. **Both** compute `T = SHA-256("agent-bridge enroll v1" ‖ account ‖ h ‖ nA ‖ nN)` and the
   code `SAS = (first 4 bytes of SHA-256("sas" ‖ T) as a big-endian integer) mod 10⁶`, six
   digits. N shows it.
5. **The person types it at A.** A compares it with its own in fixed time. On a match, A
   signs the `add` entry (`approval.transcript = T`), appends it at the relay, and sends N the
   chain from genesis through that entry over the channel.
6. **N verifies** the chain (§3.3), that the newest entry adds exactly its own `dh` and `sign`
   with the kind it asked for, that the signer is A (the peer whose code the person typed),
   and that `approval.transcript` is its own `T`. It pins the chain and connects again as an
   approved device.

Why each piece is there:
* **Commit before offer**: a man in the middle must fix its nonce on one leg before it learns
  the other's, so it gets one guess in 10⁶ per attempt, and each attempt needs a person to
  type a code.
* **Typing, not comparing**: comparing invites tapping Approve without reading. Typing forces
  the comparison (owner decision 3).
* **N checks the entry against `T`**: otherwise a relay could finish an exchange with N itself
  and hand N someone else's roster; N would then serve, or as a host trust, a roster the person
  never chose.
* **No secret is disclosed.** Only public keys, nonces and a signed entry cross; no private
  key leaves its device and there is no shared secret to rotate. A record of the exchange is
  useless later.

### 4.3 The QR path

On a desktop approver, "Add a phone" shows a QR code carrying A's device id and a fresh
128-bit secret `q`. The phone scans it, opens a channel to that id, checks the peer's static
key hashes to it, and sends `HMAC-SHA256(q, h)` with its commit. The high-entropy `q` replaces
the code in steps 1-5: A knows the device on this channel is the one that scanned, and the
phone knows it reached the approver in the QR. The person still confirms the name and kind at
A. `q` expires with the attempt and is never stored.

When the new device shows a QR (a desktop being added from a phone), it carries N's id, a hash
of its `sign` key and `q`, and A checks both before the same confirmation.

### 4.4 Limits, refusals and lockout

* One enrollment at a time per approver and per pending device; an attempt expires after
  **5 minutes**.
* **Three wrong codes** for one pending device end its attempts for **15 minutes**, and three
  in an hour on one approver lock that approver's enrollment for 15 minutes. Both are counted
  by the approver, which is trusted; the relay's own limits (below) only bound load.
* Refused, with the reason shown at both ends: a wrong code, an expired attempt, a device that
  is already approved, a key that was removed before ("this device was removed; sign out and in
  again to make a new key"), a kind the approver does not recognize, a roster at the limit.
* The relay limits each account to **8 pending devices** and **20 enrollment attempts an
  hour**, and drops a pending device's socket after an hour without approval.
* Logs on every side record the attempt (random tag), the outcome and the device ids, never
  the code, nonces or `q`.
* A pending device's name, kind and platform are its own words until approved. The approver
  shows them as claims ("says it is…"), with the first 8 characters of its id and when it
  signed in, and says "Only approve a device you are holding now."

### 4.5 What the relay does for a pending device

The relay verifies the roster too (signatures are public) and uses it as defense in depth,
never as the gate:
* admission tags each socket **approved** or **pending** by whether its key is active in the
  roster;
* `presence` and `known` report the **bound** kind and name for approved devices;
* a pending device's `presence` lists nobody and says `pending: true`; it receives no `known`;
* its frames are routed only on channels a person's device opened to it;
* it may not use push or the blob store, or `forget`;
* other devices see it in a separate `pending` list in `presence`, for the banner;
* a removed device is refused at admission with `removed` (a new close code), so it can say
  so (§6.4).

### 4.6 User verification on approval

Approving is the most powerful thing a device does. Where the platform offers it, signing a
roster entry asks the person to verify (owner decision 3):
* **Windows app**: the key itself must demand the gesture; a `UserConsentVerifier` prompt in
  the app alone does not stop malware calling the key directly. R4 picks one of: a Windows
  Hello (Passport) key credential, which is RSA-2048, so `device.sign` would then carry its
  algorithm (`ES256` or `RS256`); or a TPM ECDSA key with an NCrypt UI policy. Without Hello,
  a TPM key and a confirmation dialog;
* **Apple**: a Secure Enclave key with `.userPresence` access control (Face ID, Touch ID or
  passcode);
* **PWA**: WebAuthn user verification is a later slice; until then the PWA's approval is a
  typed code and a confirmation.

Without user verification, malware on a machine with the Windows app can approve its own
device with the app's key while the person is not looking. That is the residual risk of a
compromised machine running the app, and the reason every roster change is announced (§6.3).

### 4.7 A machine with an app and a host

Usually a machine has both, and they are enrolled together with one code. The app is the
enrollee; it reads the host's ECDH public key over the local pipe (an op limited to the app,
as `host.manage` is, docs/protocol/local-pipe.md) and asks for both. The approver signs two
entries (`app` and `host`) after the one code, and the app hands the chain to the host over
the pipe, where the host pins it. A host without an app (a Linux or Mac host run by hand)
enrolls itself with `agent-bridge-host devices join`. It shows the code in the terminal, and
the #67 guard refuses it inside an agent session.

## 5. Bootstrap and recovery

### 5.1 The first device

Genesis is an explicit choice on a person's device, **"Start this account's device list"**,
never inferred from finding no roster: the relay can hide one. When the relay says there is a
roster, genesis is not offered. When it says there is none, the device says that if other
devices were ever approved, this one should be approved from one of them instead, and starts
a roster only when the person types `NEW` (the word agent-ha-bridge's pairing uses).

A genesis made by an attacker holding the token is a roster of its own: no host trusts it,
because a host trusts a roster only through enrollment (§4.7, §5.2) or a local action. So a
race to genesis gains an attacker nothing on hosts the owner has already enrolled, and the
cutover (§8.2) has the owner make genesis before anything enforces.

### 5.2 How a host comes to trust a roster

Only by enrollment (the chain the approver hands it, bound to the code the person typed) or by
a local action at the machine (§5.5). Never by first use of whatever the relay serves.

### 5.3 Clients

A person's device trusts the roster it was enrolled into. The genesis device trusts its own.

### 5.4 Losing a device

Any approved person's device can approve, so losing one is a non-event as long as another is
left: the person removes the lost one from another (§6). With the Windows app on each of the
owner's machines and the PWA on a phone, losing every person's device at once is unlikely.

### 5.5 Losing every person's device: reset

A **reset** makes a new generation on a new person's device (typed `RESET`). Every device must
then be enrolled into it again. A host moves to the new generation only by a local action at
the machine:
* the Windows or Mac app there, over the pipe, after the person approves the new roster's
  genesis device by its code, or
* `agent-bridge-host devices reset`, interactively, refused inside agent sessions (#67).

Until then it keeps enforcing the old generation, refusing everyone. That is fail-closed, and
a machine the owner cannot reach stays unusable remotely until they can.

An optional **recovery key** (owner decision 5) would avoid the trip: at genesis, a person's
device makes an extra ECDSA key, shows its private half once as 24 words and a QR code to keep
offline, and enters its public half as a `recovery` approver. Typed into a new device, it signs
that device's `add`. The cost is a long-lived secret that, with the GitHub account, is enough to
take the account.

## 6. Removal

### 6.1 The person

Any approved person's device has a **Devices** page listing the roster: name, kind, platform,
the first 8 characters of the id, when and by whom it was added, online now or last seen.
**Remove** takes two presses (the first arms it for 5 seconds, "Press again to remove", as
Forget does). It signs a `remove`, appends it, and sends it to every connected host and device
directly as well as through the relay. The last person's device cannot be removed (§3.3).

A device can remove itself ("Remove this device"), which also signs it out.

### 6.2 Revocation reaching every host

* **Online hosts** get the entry from the relay at once and from the remover directly. They close
  the device's channels, drop its push subscriptions, and refuse it from then on.
* **Offline hosts** get it when they reconnect: from the relay's roster at admission, and from the
  first rostered peer they open a channel with or that opens one with them (§3.5 gossip).
* **The relay** refuses the removed key at admission (§4.5), so an honest relay cuts it off
  everywhere at once.
* **The window**: a host that was offline at removal and reconnects through a relay that
  withholds the entry can still serve the removed device, until any up-to-date peer talks to it.
  Its own app, other hosts' usage exchange (every 5 minutes) and every person's device that
  opens it all carry the head. That needs a dishonest relay **and** a removed device's keys,
  and it ends at the first honest contact. Commands also carry the signer's `rosterHead`
  (design §5.5), so a host that sees a newer head than its own syncs before acting.

### 6.3 Every change is announced

Every `add`, `remove` and `rename` is shown on every person's device ("*Kitchen laptop* was
added by *Desk app* at 14:05 PT") and logged by every host. An approval a person did not make
is then visible on their other devices at once, and can be undone in two presses.

### 6.4 What a removed device sees

At its next connection the relay refuses it with `removed`, and a host it still reaches refuses
it in `hello`. It says "This device was removed from *login*'s devices by *Desk app* on Nov 2.",
deletes what it kept about the account (sessions, usage, notifications, cached roster) and its
push subscriptions, and offers "Sign in again", which makes a **new key** and starts enrollment
again. A removed host stops serving relay devices, says so in the app and the host log, and keeps
serving its own machine's apps over the pipe.

## 7. Enforcement

### 7.1 Hosts

The host's `ChannelHub` gets a roster gate. For a channel from a device that is not active in
the host's verified roster, the handshake completes (so the device can learn why), and then:

| Op | Unrostered device |
|---|---|
| `hello` | Answered with `approved: false`, the host's roster head and nothing else: no name of a session, no capability list, no machine details beyond what presence already showed |
| `roster.head`, `roster.sync` | Answered, so the device can tell whether it is behind |
| Everything else (`sessions.*`, `answer`, `reply`, `launch.*`, `stop`, `usage.*`, `update.*`, `push.*`, `blob`, `session.transfer`) | Refused with `not-approved` |

Recommended: **nothing beyond "pending approval"** (owner decision 4). A read-only view would
give a stolen token the sessions' text, which can hold anything an agent read.

For an approved device, every person-only check reads the **bound** kind from the host's own
roster, through one definition (`RosterKinds.IsPerson`: `app`, `pwa`, `phone`):

| Check | Today | With the roster |
|---|---|---|
| Transfer signer (`SessionTransfers.Unsigner`) | in presence, presence kind in `pwa`/`app`/`phone`, not the asker or `to` | active in the roster as a person's device, and still in presence (the person is pressing now), not the asker or `to`. The asker and `to` must be rostered hosts |
| Folder-trust answer (`FolderTrustAnswers.NotAPerson`) | presence kind `pwa`/`app`, plus the machine's allow-list | rostered person's device, plus the allow-list (§9.2) |
| Remote update (`UpdateService`) | presence kind `pwa`/`app` | rostered person's device |
| Host-to-host peers (`UsagePeers`, the transfer target and source) | presence kind and intro say `host` | rostered `host` |
| Every other op | any device of the account | any rostered device; hosts only for host-to-host ops |

The Noise intro's `kind` and presence's `kind` stay on the wire for old peers and for display,
and are never read for a decision once a roster is pinned.

### 7.2 Clients

A person's device applies the same rule from its side. It lists, opens channels to, and sends
replies, answers, attachments and signed commands only to **rostered hosts**. A host that is not
in the roster is shown as "Not approved" with an Approve action, never as one of the account's
machines. Otherwise a stolen token could register a host called "Desk" and collect what the
person types into it.

### 7.3 The local pipe and the HA connector

The roster governs the relay path (and the LAN path of design §5.2 when it exists). The local
pipe keeps its same-user rules (docs/protocol/local-pipe.md). The HA connector acts in-process
for Home Assistant users (§9.5). Neither is a roster device and neither can change the roster.

### 7.4 The relay

Its checks (§4.5) narrow what a token alone reaches when the relay is honest. Hosts and clients
never depend on them.

## 8. Migration and rollout

### 8.1 Modes

A host and a client each have a roster mode, set locally (host: `security.roster`, changed only
by `agent-bridge-host config` or the app, both #67-guarded):

| Mode | Behaviour |
|---|---|
| `off` | Today's behaviour. The default until the cutover. |
| `observe` | Syncs and verifies the roster, and logs and reports (`host.status`, app banner) every op it **would** refuse and why, but refuses nothing |
| `enforce` | §7. With no pinned roster, a host in `enforce` refuses every relay device except to say "approve this machine" |

### 8.2 The cutover for the owner's devices

Existing devices are **grandfathered by an explicit approval, never silently**. Recommended
(owner decision 6): every existing device is enrolled with a code, once, the same way a new one
would be:

1. Release the relay, then hosts and clients with roster support in `off`, then `observe` (§8.3).
2. The owner starts the roster on one person's device (the Windows app on DEV-VM1, say).
3. For each other machine, its Windows app shows a code and the owner types it on an approved
   device. One code covers the app and the host there (§4.7). Then each phone PWA, and the Apple
   clients when they ship.
4. Every host in `observe` now reports whether anything it serves is unrostered. When the owner
   has seen a quiet day, they switch each host to `enforce` (from the app's Settings or
   `agent-bridge-host config set security.roster enforce` at the machine).
5. A later release makes `enforce` the default for new installs: a new machine serves its own
   apps until approved.

About six codes for today's fleet. A lighter alternative is a one-time **bulk grandfather** on
the genesis device: it lists the devices online at that moment, each with its kind and the first
8 characters of its id; the owner ticks each after checking the id shown on that device, and the
genesis device signs `add` entries with `approval.method: "grandfather"`. A device that came only
from a stolen token, online at that moment and named like one of the owner's, would be caught
only by that id check. Ids from `launch.remoteFolderTrustDevices` (approved at a machine
already) are pre-ticked, nothing else is.

### 8.3 Order of releases

1. **Relay** (staging, then production **only with the owner's approval** of the `relay.yml`
   production job): roster storage, the `roster`, `roster-append` and `pending` control messages,
   approved/pending admission, bound kinds in `presence`. Additive: an old client ignores new
   fields, and a relay keeps today's behaviour for an account with no roster.
2. **Hosts** (Windows, then the Mac host), shipping in `off`: the verifier, the pin, gossip,
   `roster.*` ops, enrollment through the pipe and the CLI, `observe` and `enforce`.
3. **Windows app and PWA**: the Devices page, approve (code and QR), join, genesis, remove, the
   pending screen, and the client-side rule of §7.2 (only in `observe`/`enforce`).
4. **Apple clients**: join first, approve next (§9.6). They must ship join before any host the
   owner uses them with is in `enforce`.
5. **The owner's cutover** (§8.2), per host.
6. **Default `enforce`** for new installs.

### 8.4 Old and new together

| Combination | Outcome |
|---|---|
| Old client, enforcing host | Refused with `not-approved`; the client shows the error text. Windows apps update within 30 minutes and the PWA on reload, so this is brief |
| New client, old host | The client sees no roster head in `hello`. In `observe` it warns; in `enforce` it refuses to send to it, saying "update this machine". Old hosts update with their app |
| New client, old relay | No roster store: enrollment is unavailable and says why. Devices with a pinned roster go on enforcing it and sync by gossip |
| New host, old relay | Same: a host in `enforce` keeps its pin and syncs from peers |
| agent-ha-bridge | Unaffected: nothing in it reads the relay, and the HA connector's contract is unchanged |

## 9. Effect on other work

### 9.1 Transfer (#70)

`Unsigner` requires the signer to be an active rostered person's device and still in presence;
the asking target and the named `to` must be rostered hosts. The "What this does not stop yet"
paragraphs of `docs/protocol/v1-transfer.md` and `docs/host-transfer.md` go. Commands stay
MACs: they already name one verifier. Moving them to ECDSA signatures is possible later and not
needed.

### 9.2 Folder trust (#133, #157)

`NotAPerson` reads the bound kind. `launch.remoteFolderTrustDevices` stays, a per-machine choice
of which approved devices may answer, and an id there that is not active in the roster is
ignored (and shown as such), so removal revokes folder trust at once. Settings shows each
device's roster name, kind, the first 8 characters of its id, and when and by whom it was
added, which closes #157 item 4. Items 1 and 2 of #157 (tightening only after a restart, terminal
writes past the lock) are unaffected and still open.

### 9.3 Forget (#138)

The relay checks the bound kind for `forget` rather than `hello`'s (§4.5). Removing a host
(§6) also drops it from the machine roster's `known`. Owner decision 7 is whether Forget on a
host becomes Remove:
* **Remove revokes and forgets** (recommended): a machine that is gone, perhaps with its disk, is
  revoked too. If it connects again it is listed as waiting for approval, and one code brings it
  back. agent-ha-bridge let a forgotten machine come back by itself; this is a deliberate
  difference, to be recorded in parity.md.
* **Keep both**: Forget stays listing-only (parity), Remove is a separate action on the Devices
  page.

### 9.4 Remote update (#161)

`UpdateService` reads the bound kind; nothing else changes. The machine's own Settings and the
HA connector's Install button are local paths and unaffected.

### 9.5 HA connector

Unchanged. It runs in the host for Home Assistant users and reaches the same services the relay
path does, guarded by HA credentials, as agent-ha-bridge was. It is not a roster device, it
shows no roster controls, and nothing it does changes the roster. On a machine with the
connector on, HA credentials stay a second way in, by design; the note in ha-connector.md says
so.

### 9.6 Apple clients (#260, #358)

They enroll like any other client. Each must implement:
* the ECDSA signing key (Secure Enclave `P256.Signing` with `.userPresence`, Keychain fallback),
  created at first run beside the device key;
* the roster verifier, the pin (in the Keychain), gossip in `hello`, and the shared test vectors;
* **join** (pending screen, six-digit code, QR scan for "Add a phone", the commit/reveal of §4.2,
  verifying the returned chain), before any host they talk to enforces;
* **approve** and **Devices** (type or scan a code, confirm with Face ID or Touch ID, remove in
  two presses), soon after; the Mac app is a natural approver for its own machine;
* the client rule of §7.2, and reading the bound kind for what they show.

The iPhone and iPad client already connects as `phone` and the Mac app as `app`, which are the
bound kinds they get. The Mac host enrolls with the Mac app as in §4.7, or by its CLI.

## 10. Tests

Each slice adds its own; together they cover:

**Acceptance (the issue)**
* A device of the account that is not in the roster is refused every session op on a host in
  `enforce`, and sees only "pending approval".
* A device added by typing its code on the Windows app, the PWA, or (later) an Apple client is
  served at once, by every host, including one that was offline during the approval.
* A removed device is refused by every host, including one that was offline at the removal.

**Tampering**
* A roster with one byte of a body changed, a signature from a key that is not the signer's, an
  entry signed by a host, by a removed device, or by a pending device: refused, and the old copy
  kept.
* An entry from another account or generation, a `seq` gap, a `prev` that does not match, a
  re-added removed id, a removal of the last person's device, a second genesis: refused.
* An off-curve or malformed `dh` or `sign` key: refused before use.
* A re-encoded (high-S or re-DER'd) signature does not make a second, distinct entry.

**Replay**
* An old `add` sent again: refused (its `seq`/`prev` place it).
* An enrollment message from one attempt replayed into another: refused (`h` and nonces).
* A signed command replayed after its signer was removed: refused.

**Downgrade and rollback**
* The relay serves an older head, a shorter chain, no roster at all, or says "none": a host and a
  client with a pin keep enforcing their own and say so.
* A peer's `hello` without a roster head or with an older protocol version: no change of mode.
* A fork (two valid entries on one parent): hard error, removes from both branches applied,
  adds refused.

**Kind spoofing**
* A rostered host's key connecting with `hello` kind `pwa`: refused at transfer signing, folder
  trust, update and (relay) forget.
* An unrostered key with kind `pwa` or `app`: refused everywhere.
* A rostered `pwa` connecting as `host`: not used as a host-to-host peer.

**Enrollment**
* A relay that substitutes keys on either leg: the codes differ and the approver refuses.
  A grinding test shows a man in the middle that must commit first matches at the expected rate.
* Three wrong codes lock the pending device for 15 minutes; an expired attempt is refused; no
  log line holds a code, nonce or `q`.
* N refuses a chain whose newest entry is not its own keys and kind, is signed by anyone but
  its code peer, or carries another transcript.
* Genesis is never made without `NEW`, and a host never trusts a roster it was not enrolled into.

**Removal while offline**
* A host offline at removal learns it from the relay at reconnect; with a relay that withholds
  it, it learns from the first rostered peer, and refuses the removed device from then on.
* A removed device is told so, wipes its data and push subscriptions, and makes a new key to
  join again.

**Interop**
* Shared vectors for entries, signatures, the chain hash, the code and the QR MAC, run by C#,
  the PWA and Swift, as the command MAC's are.

## 11. Decisions for the owner

1. **Trust root.**
   (a) Any approved person's device signs roster entries with a new, non-exportable ECDSA key;
   hosts never sign. (b) One account root key on the first device. (c) Per-machine approval
   lists only.
   **Recommended: (a).**
2. **Hosts in the roster.**
   (a) Hosts are approved by a code like any device, and clients refuse unapproved hosts.
   (b) Hosts are trusted on sign-in as today, and only person's devices are approved.
   **Recommended: (a)**: otherwise a stolen token registers a fake machine that collects what
   people type.
3. **How an approval is made.**
   (a) Type the six-digit code shown on the new device, or scan a QR, plus Windows Hello, Face ID
   or Touch ID where available. (b) The same without user verification. (c) Compare the codes
   and tap.
   **Recommended: (a).**
4. **What an unapproved device of the account sees.**
   (a) Nothing beyond "pending approval". (b) A read-only list of machines.
   **Recommended: (a).**
5. **Recovery when every approved person's device is lost.**
   (a) Reset to a new roster, confirmed at each machine. (b) (a), plus an optional offline
   recovery key made at genesis.
   **Recommended: (a)**: with the app on each machine as an approver, total loss is unlikely,
   and a recovery key is one more secret that, with the GitHub account, takes the account.
6. **Grandfathering the devices in use now.**
   (a) Enroll each with a code once (one per machine, about six), hosts in `observe` first, then
   `enforce` per host. (b) Bulk-approve the devices online at cutover after checking their ids.
   **Recommended: (a).**
7. **Forget and Remove for a machine.**
   (a) One action, Remove, which revokes and forgets; a machine that returns waits for approval.
   (b) Keep Forget listing-only, with Remove separate.
   **Recommended: (a).**
8. **Which kinds count as a person's.**
   (a) `app`, `pwa` and `phone` everywhere, so the native iPhone client can answer folder trust
   and press Install. (b) Keep folder trust and update to `app` and `pwa`.
   **Recommended: (a)**, once kinds are bound.

## 12. Slices

Each is one PR, security-reviewed independently before merge.

| # | Slice | Depends on | Changes behaviour |
|---|---|---|---|
| R1 | Roster library: entries, ECDSA signing keys (TPM/DPAPI, WebCrypto), chain verification, pin store, shared vectors (C#, JS); protocol spec `docs/protocol/devices.md` | decisions 1, 2 | No |
| R2 | Relay: store and serve the chain, appends, approved/pending admission, bound kinds in presence, pending limits, `removed` close code. Staging; production only with the owner's approval | R1 | Only for accounts with a roster |
| R3 | Host: verifier, pin, gossip in `hello`, `roster.*` ops, modes `off`/`observe`/`enforce`, the gate in `ChannelHub`, enrollment over the pipe and `devices join`/`reset` | R1 | Only when the mode is changed |
| R4 | Enrollment protocol and the Windows app: genesis, join, approve (code), Devices page, remove, announcements, Windows Hello signing | R2, R3, decision 3 | Only after genesis |
| R5 | PWA: join, approve (code, QR scan), Devices, remove, pending screen, client rule | R2, R4 | As R4 |
| R6 | Bound kinds: one `RosterKinds.IsPerson`; transfer, folder trust, update and usage peers read the roster; the client rule in the app; docs for #70, #133, #138, #161 | R3, decision 8 | In `enforce` |
| R7 | Removal end to end: revocation, offline hosts, removed-device experience, Forget/Remove | R4, R5, decision 7 | Yes |
| R8 | Owner cutover: runbook and live checks (the issue's "Validate live"), then `enforce` per host | R1-R7, decision 6 | On the owner's machines, with their go-ahead |
| R9 | `enforce` by default for new installs | R8 | Yes |
| A1 | Apple: join, then approve and Devices (contributor) | R1, R2 | For Apple clients |
| L1 | Later: fork resolution, recovery key if chosen, checkpoints, WebAuthn user verification in the PWA | | |
