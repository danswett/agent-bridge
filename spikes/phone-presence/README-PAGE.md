# Brief 220 PR 2a: offline phone page, QR and enrollment comparison

**Credential-possession SPIKE, not a trust root, device-provenance proof or
confused-deputy mitigation.** None/self attestation permits a software key on
the PC; malware can show its own QR and matching fingerprint. Engineering owns
unresolved F1 authenticated provenance and F2 a PC-independent trusted display.
These modules have no network, hosting or deployment code.
`index.html` loads the session from the URL and posts evidence through the automatic
return callback to the evidence mailbox (PR 2b/2c; see README-RETURN.md). No owner run or
real phone credential facts are claimed, and nothing here is deployed.
`renderSession` accepts the short-lived create/get contexts from the future loader;
the root view shows the PC QR, the fragment view shows the phone approval screen.
No sheet opens without fingerprint comparison and a configured return callback.
Registration uses a nonce independent of genesis; `get` uses the exact roster
F body challenge, 32-hex generation and normalized low-S DER. Both require UV
and rpId `proof.slipgates.com`, with wall/monotonic expiry. This page asserts a
pre-supplied fake body: final key-inclusive genesis assembly/exchange is UNSOLVED.

Enrollment SAS is the first 8 bytes of SHA-256(canonical P-256 DER-SPKI public key
|| 32-byte assertion challenge), rendered as four lowercase four-hex groups.
The PC must compute it from the credential key it actually parsed/verified, never
from an unverified key/code supplied alongside the assertion. Person comparison
is mandatory: owner TYPES the phone code in his own PC console, else **FAIL**.
Record "owner console confirmation": same-PC code able to write to that console
could fake it. This does not solve enrollment provenance or malicious PC displays.
OS/browser/path reports are self-reported, not authenticated phone provenance.

`node --test --test-concurrency=1 spikes\phone-presence\phone.test.mjs` is offline.
Run on the approved VM, BelowNormal, one CPU. Reuses existing roster helpers; no third-party runtime package.
The original fixed-version 5-L/mask-0 QR encoder was cross-checked against
temporary **qrcode 1.5.4** on URL, capacity, short and UTF-8 vectors; it is not
vendored or shipped. The temporary package is removed after validation.
PR 2b owns the separate Durable Object mailbox, fixed return URL, poller, fail-closed
person comparison and automatic RESULTS-PHONE.md. No file return, copy/paste,
Cloudflare authentication, provisioning, DNS changes or deploy belongs to this cut.
