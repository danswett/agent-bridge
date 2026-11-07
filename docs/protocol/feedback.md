# Beta feedback upload

How a device gets a bundle of beta feedback (#738) to a private store (#739) where only the
feedback lead (#740) can read it. It builds on the blob store (`blobs.md`) but is a separate
container with separate rules, so the two never share retention or access.

## What the relay sees

Nothing of the content. The device seals the bundle to the **feedback public key** before it
leaves the device (an ephemeral X25519 key, HKDF and AES-256-GCM, with the key id and a random
bundle id as associated data; the sealed layout is specified with the client in #738) and pads
it to a blob bucket size. The private key is held only by the feedback lead (Key Vault or TPM),
never by the relay, the repository or a device. The relay treats the upload as opaque bytes.

## Messages

Device to relay, over the existing connection:

```json
{"t":"feedback-put","id":7,"size":65536}
```

`size` is the stored size: a bucket as in `blobs.md` (4096, powers of two to 1 MiB, then whole
MiB). Relay to device:

```json
{"t":"feedback-result","id":7,"url":"https://…","expires":"2026-11-07T01:00:00Z"}
{"t":"feedback-result","id":7,"error":"quota"}
```

The device then `PUT`s exactly `size` bytes to `url` (a path on the relay, `/v1/feedback/<ticket>`; the ticket is an HMAC over a random name, the size and the expiry). The relay checks the length and creates the blob itself. `ready` carries
`feedback: {"max": <bytes>}` when the relay takes feedback and `feedback: null` when it does not.

Errors: `disabled`, `bad-request`, `too-large` (over `max`), `bad-size` (not a bucket),
`rate-limited`, `quota`, `unavailable`. The upload answers 201, or 403 (bad or expired ticket), 400 (wrong length), 413 (more than ticketed), 409 (ticket already used), 503 (store failed).

## Limits and retention (defaults)

* 8 MiB per bundle; 3 requests per minute, 10 per day and 40 MiB per day per account.
* The ticket is for one random blob name and one size, and lasts 10 minutes. The relay never
  overwrites, so a second `PUT` is refused, and it takes no more or fewer bytes than ticketed, so
  the size limit is enforced by the relay, not left to storage.
* The relay's storage identity can only create blobs in this container: its role assignment
  carries a condition that removes read, list, tags and delete. A compromised relay can add
  garbage (within the limits above) but cannot read or delete feedback. Reading is the
  feedback lead's role (#740).
* The container has a 30-day time-based retention policy, so a stored bundle cannot be
  overwritten or deleted by the relay or any other data-plane identity until 30 days have
  passed. (A role condition cannot express "no overwrite", so storage enforces it.) The
  policy deploys unlocked, so an account owner could still remove it until an operator locks
  it; the lock step is in [relay-deploy.md](../relay-deploy.md#blob-store).
* A lifecycle rule deletes bundles 31 days after they were written (#731); the `blobs`
  container's one-day rule is scoped so it never touches them.

## Privacy

The blob name is random: no account, device or time is in it. The relay logs and counts
outcomes and sizes only, never an account, a device or a ticket. Quota is kept in memory per
account and forgotten on restart. The route is `PUT` only; there is no way to read feedback back through the relay. `Relay:Feedback:Backend=local` keeps bundles in memory for tests.
