# DESIGN 219 - phone/QR presence proof for the first roster genesis

Replaces the same-PC Windows Hello harness, brief 211. Owner decision 17:49 PT,
https://github.com/danswett/agent-bridge/issues/3450 . Author: Engineering.
Reads with brief 220 (the smallest first build).

## WHY (facts)
- Three attended runs on DSWETT-HOME failed 0x80090027 (NTE_INVALID_PARAMETER) before any prompt. The owner's passkeys there are Microsoft Password Manager SYNCED passkeys (AAGUID d3452668-01fd-4c12-926c-83a4204853aa). Neither run says anything about the TPM itself; the same-PC path is parked, not disproved.
- The goal is unchanged: a genesis (first signed roster entry) needs a presence proof that same-user code on the PC cannot produce (device-approval.md 3.1, decision 3(a); presence 'none' cannot sign). A typed value is not presence.

## WHAT THE PHONE PROVES, AND WHAT IT DOES NOT
- Proves: a human unlocked a SEPARATE device (biometric or PIN on the phone, user verification flag) and approved THIS request; malware on the PC cannot press, type or inject into the phone's prompt (SendInput, UI Automation, console injector, silent assertion are all out of reach by construction).
- Does not prove, by itself: that the key is non-exportable. A synced passkey (BE flag set) can be used by whoever controls the owner's cloud account. Against same-PC malware that is far stronger than today, but it is a weaker root than a device-bound key, and devices.md/device-approval.md currently say a WebAuthn credential qualifies as presence only if device-bound (BE clear), and that iOS passkeys never qualify.
- So this needs ONE explicit owner policy call, which Engineering records and does not decide: (P1) accept a synced phone passkey as the genesis presence root, declared as a distinct honest tier; or (P2) require BE clear, which means an Android device-bound credential or a hardware key (the hybrid prompt can also select a security key), and iOS stays out. Brief 220 records BE/BS/AAGUID/attestation and does not assume either; Chief asks the owner after the spike reports the real flags of the owner's phone.

## FLOW (smallest honest design)
1. The Windows app (or the spike) shows a QR. The QR carries only: relay URL, a one-time session id (128-bit random), and the PC's candidate genesis fingerprint (four groups of four). Never a secret, never a key.
2. The owner scans it on the phone. The phone opens the PWA approval screen (or the OS passkey sheet in the spike). The screen shows the fingerprint and the account id in words, and asks the owner to compare it with the PC screen (the existing short-code habit from device approval).
3. The phone makes a WebAuthn assertion (create on first use, get after). Challenge = SHA-256 over a domain-separated string: purpose 'roster-genesis-presence', account id, generation, genesis hash, session id, expiry. rpId is the product origin; origin and rpId are checked.
4. The assertion returns through the relay (opaque blob, relay cannot forge or alter it) to the PC. The host/app verifies: signature with the credential public key recorded at creation, rpIdHash, UP and UV set, flags BE/BS as recorded, signCount rules (not required to increase for synced; record anyway), challenge equals the recomputed value for this exact genesis, session not used, not expired.
5. Only then does the PC sign the genesis with the existing K1 genesis builder and K2 capSign custody and attach the presence assertion as the genesis's presence evidence verified by the existing Roster verifier. No Roster edits in the spike.

## FAILURE AND REPLAY
- Session id single use, 5 minute expiry, bound to the genesis hash; any mismatch, expiry, reuse, second assertion, wrong rpId, UV not set, wrong account: refuse with a plain sentence and leave NO partial state. Cancelling the phone prompt leaves nothing.
- Replay: an assertion for one challenge never verifies for another (test it). A QR photographed by a bystander is useless without the owner's biometric, and the fingerprint comparison catches a swapped QR.
- Confused deputy (the real remaining same-PC attack): malware on the PC starts its OWN request at a moment the owner expects a prompt. Mitigation is the human fingerprint comparison on the phone screen against the PC screen; therefore the first build must use a phone screen that SHOWS the fingerprint (the PWA), not only the OS passkey sheet, which shows only the rpId. A plain OS-sheet-only spike is a feasibility check, never an acceptance.
- Relay is untrusted: it only carries bytes; it learns session id and fingerprint, nothing secret.

## REUSED FROM THE R4 THREAT MODEL (brief 211)
- The attack list and its 'any success means REFUSE' rule, narrowed: SendInput, UI Automation, console injector, silent assertion and post-close signing are tested ONLY against what the PC can touch (the PC side prompt, the QR window); the phone prompt is out of reach by design and is not attacked.
- Replay, post-close signing and wrong-challenge tests carry over unchanged.
- Presence assertions verified by the existing Roster verifier, windows-approver rule, results file layout (RESULTS-*.md with exact flags), the no-load/no-live-process rule, own test credential deleted afterwards.
- What does NOT carry over: Windows Hello, webauthn.dll platform attachment, TPM key, the DSWETT-HOME run location. Nothing runs on DSWETT-HOME for this; the owner's phone is the only owner-side step.

## WHAT THE RELAY AND HOST NEED
- Relay: a short-lived opaque mailbox keyed by session id (post one blob, read one blob, single use, 5 minute TTL), no account data. Check whether the existing relay account-log/mailbox endpoints already allow this before adding any; the spike may use any throwaway transport (even a file or a local loopback) so the relay is NOT a blocker for the first build.
- Host/app: challenge builder, assertion verifier (ES256, CBOR/authData parse, rpIdHash, flags), session store with single use. The PWA already has roster verify/parse and the genesis builder but no navigator.credentials code and no roster-append client (checked 16:13); the PWA approval screen is new.
- Owner approval on the phone: scan, read the fingerprint, compare, biometric. Nothing to type.

## ORDER
- 220 (spike, no relay, no roster change) proves the cross-device assertion path and records the real flags of the owner's phone.
- Then Chief asks the owner the P1 or P2 policy question with those flags.
- Then relay mailbox + PWA approval screen + verifier wiring (a Phase 2 brief I write after the spike result), then 186b is retargeted to the PC app showing the QR.
