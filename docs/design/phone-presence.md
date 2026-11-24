# Phone credential-possession SPIKE (brief 220; corrected design 219)

Scope: https://github.com/danswett/agent-bridge/issues/3450 . Engineering owns this
correction. This spike is **not a trust root, separate-device proof, or a
confused-deputy mitigation**. Nothing is wired into the app, host, relay or roster.
The `agent.lite` trust gate is instead authenticated same-account relay host presence,
with no separate peers pin (owner, Nov 19, 22:05 PT; merged in
[#3384](https://github.com/danswett/agent-bridge/pull/3384)).
`fmt=none` and self-attestation authenticate no device provenance: software ES256
keys on the PC pass the same UP/UV/signature checks. A malware-shown QR and matching
PC fingerprint can approve the attacker's request. The PC reference is not trusted.

Required follow-ups, owned by Engineering: **F1** authenticated device provenance
(trusted attestation or a second channel); **F2** a PC-independent trusted display
of the request. Neither is implemented or solved by this spike or by typed codes.
First enroll on a domain-separated random session nonce, without a genesis hash.
Only after registration provides the id/key can the final genesis body and its
fingerprint be constructed; a fresh second session asserts over that exact body.
The interoperable assertion challenge is SHA-256(F("agent-bridge presence v1","by",
bodyBytes)); F uses four-byte big-endian byte lengths. Generation is 32 lowercase hex.
Assertion signatures are strict minimal low-S DER; the genesis signature uses the
device's `sign` key, **not capSign**. Only a synthetic v3 test creates a roster entry.

Session id/expiry are local freshness state, not extra fields in the roster body.
One body gets one assertion session per process; both Unix and monotonic deadlines
must remain live, and observed expiry is terminal. Single use prevents same-process
replay. Restart/persistent replay, production enrollment and real genesis creation
remain unsolved. Protocol docs specify no signCount acceptance rule: record only.
BE/BS/AAGUID/fmt/transports and OS/browser reports are facts, not a P1/P2 policy verdict;
the existing v1 Roster verifier rejects BE-eligible presence. No real phone run exists.
Later static request exchange must supply the full request as well as the return
blob; QR-only id/fingerprint cannot convey the challenge. Hosting/deploy is person-only.
