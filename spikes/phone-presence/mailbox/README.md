# Brief 220 PR 2b: opaque mailbox and fixed automatic POST

Isolated credential-possession SPIKE transport, not a trust root, device-provenance
proof or confused-deputy mitigation. None/self attestation permits a PC software
key; a malicious PC can show its own QR/fingerprint. Engineering owns unresolved
F1 authenticated provenance and F2 a PC-independent trusted request display.

`worker.mjs` is plain JS with pure `handle(request, env)` and a per-session
Durable Object bound as `MAILBOX`. POST/GET/OPTIONS `/s/<32 lowercase hex>` only;
exact Pages Origin, JSON content type, declared/streamed body limit 4096 bytes,
fixed CORS, no-store, no logging. `blockConcurrencyWhile` serializes append:
three opaque posts maximum, fourth 409, never overwrite; GET keeps stored order
and never consumes. The first post starts a five-minute timestamp/alarm lifetime;
later posts never extend it. At five minutes the alarm removes payloads/keys;
only expiry metadata remains until first-post + one hour, when `deleteAll` and
`deleteAlarm` remove ALL state, including the tombstone. Late requests also clear
retired state. Expired reads/posts return 404; PC expiry forbids old evidence.
POST requires a 32-lowercase-hex `Idempotency-Key`: identical key/blob retries
acknowledge 201 without a second stored copy; changed bytes under that key get
409, even when three slots are already full. Keys are not secrets/authentication.

`../return.mjs` is the shared fixed mailbox constant and phone POST helper.
Neither QR nor session data carries a mailbox URL. The phone uses its session id
as a stable idempotency key. Only explicit `DOMException` `NetworkError` retries
once. Fetch `TypeError` cannot distinguish network/redirect/CORS, so never retries;
nor do HTTP failures or cancellation. Native seed uses a DIFFERENT key from the
phone and never retries (a failure requires a new session). Origin is not authentication.
Transport can lose/refuse/tamper
with bytes, never provide enrollment trust. The later PC verifier must check
signature, exact challenge and session; no invalid blob is evidence.

Run `node --test --test-concurrency=1 spikes\phone-presence\mailbox\worker.test.mjs`
on an approved VM, BelowNormal, one CPU. Tests use fake DO storage and injected
fetch only. No Wrangler install/deploy, network service, credentials or live
app/relay/profile/key/roster is used. No third-party dependency or workflow file.

**NO DEPLOY WITHOUT RATE LIMITING.** The helper MUST apply a Cloudflare
rate-limiting rule on `proof-mailbox.slipgates.com`: match POST, limit
**10 POST requests per minute per IP**, block excess with 429 for 60 seconds.
The helper verifies that rule before deployment; it is not optional or implemented
by this code. Signed ids are not required for this temporary spike.
Helper ONLY, after exact-head Quality receipts and the required rate rule: deploy from this reviewed head
with `wrangler deploy` in this directory; its DO migration/binding is `MAILBOX`;
attach custom domain `proof-mailbox.slipgates.com`, never the Pages domain.
Leave request observability/logging disabled; never commit provisioning secrets.
Only helper deployment proves real Cloudflare binding/domain/CORS behavior.

PR 2c supplies PC polling, owner-console typed code confirmation, automatic
RESULTS-PHONE.md and page bootstrap after corrected core/page land on main.
No manual phone-to-PC file step. Console confirmation is not root acceptance:
same-PC code able to write to that console could fake it. No owner phone run,
real flags/results or end-to-end acceptance is claimed by this transport cut.
