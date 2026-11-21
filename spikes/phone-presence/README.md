# Offline phone-presence spike (brief 220, PR 1)

Scope: challenge, QR **payload only**, registration/assertion verifier and synthetic tests.
No browser, network, hosting, relay, roster, signing custody or live bridge is used.
Phone page, QR rendering, owner instructions and automated RESULTS-PHONE.md are PR 2,
after the Chief supplies the Pages repository for https://proof.slipgates.com.
No real phone assertion or credential facts have been observed here.

Run `node --test --test-concurrency=1 spikes\phone-presence\core.test.mjs` on VM7,
with the Node process BelowNormal and affinity 1 (one CPU); no installs are needed.
The named replay/post-close/second-pending tests are the PC-side attack script.
Any unexpected acceptance fails the suite: REFUSE, not a phone-presence verdict.

`Sessions.create` makes a random 128-bit id and five-minute expiry; `open` also
accepts a supplied context for deterministic tests. Close on cancel/window close.
State is memory-only, with ended ids retained to prevent reuse until process exit.
Registration and assertion each consume a distinct session; only a verified
assertion demonstrates credential possession. Registration `fmt=none` is NOT attested.
ES256 `none` and `packed` self-attestation are supported; certificate attestation
and authenticator extensions refuse explicitly, rather than claiming trust.
BE/BS/UV, AAGUID, fmt, signCount, id length and reported transports are returned;
transport reports and AAGUID are not independently trusted device/OS provenance.
Synced counters need not increase; device-bound nonzero counters must increase.
The vectors use ephemeral synthetic private keys, never saved or exported.
Refusals throw a plain sentence and commit no credential, evidence or consumed state.

Challenge bytes are SHA-256 of UTF-8 `JSON.stringify` of the ordered array:
`["Slipgates/WebAuthn/v1","roster-genesis-presence",account,generation,genesisHash,sessionId,expiry]`.
Hashes/ids are lowercase hex; generation is a positive integer, expiry Unix milliseconds.
QR payload is an HTTPS URL with fragment fields `session` and `fingerprint` only;
fingerprint is the first 16 genesis-hash hex characters in four groups of four.
No challenge, account, expiry, key or credential id is carried in the QR.

No real credential is created by PR 1. After PR 2's phone run:
Open the phone's password/passkey manager and find the proof.slipgates.com spike entry.
Delete only that test passkey, not any Slipgates production or unrelated credential.
Stop the spike (Ctrl+C) to discard its in-memory session and public-key state.
