# Offline credential-possession SPIKE (brief 220 PR 1)

No trust root, separate-device proof, confused-deputy mitigation or real phone
result is claimed. `none`/self attestation also accepts software keys on the PC.
A malware-controlled QR plus matching PC fingerprint defeats that comparison.
Engineering owns F1 (trusted attestation/second channel for device provenance) and
F2 (PC-independent request display); both remain unsolved, not code in this PR.
No network, hosting, roster wiring, custody or live processes are used.

Run `node --test --test-concurrency=1 spikes\phone-presence\core.test.mjs` on the
approved VM, BelowNormal, one CPU. Existing PWA roster helpers are reused unchanged.
First `createEnrollment` produces a random 128-bit nonce session; registration has
no genesis challenge. Construct the body after enrollment; `create(generation,
bodyBytes)` then makes its assertion session. Generation is 32 lowercase hex.
The body/account/generation/hash are checked; challenge is the exact roster
SHA-256(F("agent-bridge presence v1","by",bodyBytes)), not a custom JSON challenge.
Only the synthetic interop test builds/signs a v3 genesis, with the device sign key.
Session expiry uses Unix **and monotonic** deadlines; expiry is terminal. Close on
cancellation. State is memory-only; no body can acquire a second session in it.
Assertions require strict minimal low-S DER. `none` and ES256 packed self-attestation
are supported; certificates/extensions refuse, and neither supported fmt proves provenance.
SignCount is recorded, never used to refuse: devices.md/device-approval.md are silent.
BE/BS/UV/AAGUID/fmt/counter/id length/transports are recorded without policy approval.
QR is HTTPS URL plus id/fingerprint only. Full request delivery remains a later cut.

No real credential is created here. After any later phone run:
Find the proof.slipgates.com spike entry in the phone's passkey manager.
Delete only that test entry; stop the spike to discard its in-memory public-key/session state.
