# Brief 220 PR 2a: offline phone page, QR and enrollment comparison

These browser modules have **no network, hosting or deployment code**.
`index.html` is deliberately inert until PR 2b supplies session loading and the
automatic return callback. No owner run or real phone credential facts are claimed.
`renderSession` accepts the short-lived create/get contexts from the future loader;
the root view shows the PC QR, the fragment view shows the phone approval screen.
No sheet opens without fingerprint comparison and a configured return callback.
Phone `create` and `get` require UV and use rpId `proof.slipgates.com`.
The enrollment code appears on the phone before `get`; keep it visible afterwards.

Enrollment SAS is the first 8 bytes of SHA-256(canonical P-256 DER-SPKI public key
|| 32-byte assertion challenge), rendered as four lowercase four-hex groups.
The PC must compute it from the credential key it actually parsed/verified, never
from an unverified key/code supplied alongside the assertion. Person comparison
is mandatory: no match or no confirmation means **FAIL**. This mitigates first-use
key substitution through the untrusted mailbox; it is a spike fact, not root acceptance.
OS/browser/path reports are self-reported, not authenticated phone provenance.

`node --test --test-concurrency=1 spikes\phone-presence\phone.test.mjs` is offline.
Run on the approved VM, BelowNormal, one CPU. Runtime dependencies: none.
The original fixed-version 5-L/mask-0 QR encoder was cross-checked against
temporary **qrcode 1.5.4** on URL, capacity, short and UTF-8 vectors; it is not
vendored or shipped. The temporary package is removed after validation.
PR 2b owns the separate Durable Object mailbox, fixed return URL, poller, fail-closed
person comparison and automatic RESULTS-PHONE.md. No file return, copy/paste,
Cloudflare authentication, provisioning, DNS changes or deploy belongs to this cut.
