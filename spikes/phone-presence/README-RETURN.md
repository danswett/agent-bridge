# Brief 220 PR 2c: attended credential-possession SPIKE
1. In your own console on the approved fleet VM (not a live bridge host), run `node spikes\phone-presence\pc.mjs`, then open the printed PC URL.
2. Scan its QR with your phone; both screens show the request's four fingerprint groups.
3. Compare all four groups and account `spike`, then tap Approve on the phone.
4. Approve both passkey prompts, then TYPE the phone enrollment code in your PC console.
5. Send back the automatically written `RESULTS-PHONE.md`; no phone file/copy step.

Delete only the new `proof.slipgates.com` spike passkey in your phone's passkey manager.
On iOS: Passwords > Passkeys; on Android: your chosen credential manager > Passkeys.
Keep all other credentials; no live bridge profile/token/key/roster is touched.

NOT a trust root, device-provenance proof or confused-deputy mitigation; genesis is
UNSOLVED. None/self attestation permits PC software keys. Engineering owns F1
authenticated provenance and F2 a PC-independent trusted request display.
QR fingerprint covers the exact nonsecret request envelope, including session
nonces/expiry; the fake body separately uses the roster F assertion challenge.
The phone verifies slot one and its complete hash before displaying approval.
A malicious PC can show its own QR/fingerprint; native Origin is NOT authentication.
Junk posts can burn slots: failure/retry requires a NEW session. No Worker change.
Slot one uses the enrollment nonce key; evidence uses the distinct assertion id key.
An explicit NetworkError retries identical evidence once, deduplicated in slot two;
slot three remains available. TypeError/redirect/HTTP/cancel never retry.
Record "owner console confirmation": same-PC code able to write to that console
could fake it. Signature/flags are spike facts, not phone provenance or root policy.
Five-minute wall/monotonic deadline; Ctrl+C closes; existing results never overwrite.
Tests use injected fetch plus real-network ban, serial/BelowNormal/one CPU on VM7.
Helper must verify the mailbox README's mandatory Free-plan rate rule before deploy.
Only helper deployment proves binding/domain/CORS; only the attended owner run
reports actual credential facts/self-reported OS/browser/path. No run is claimed.
Static publication after exact-head Quality approval must include the browser
modules (`index`, `bootstrap`, `client`, `lifetime`, `return`, `phone`, `page`,
`screen`, `sas`, `qr`) and existing `pwa/js/roster` framing/codec/strict-json/p256/refusal.
