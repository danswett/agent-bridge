# Feedback upload: encrypted, write-only, private (#739)

Status: **draft for review. Design only; no code.** Part of #739, for the Send feedback client
(#738) and the inbox (#740). Security-sensitive: it touches the relay, keys and storage, so it
needs an independent security review before anything is built.

This note extends design §4.6 (blobs) and §5.10 (observability and privacy). It does not change
either. The privacy design (#732) and the Send feedback UX (#733) own the bundle contents and the
consent flow. Where they have not decided something, it is listed under
[Open questions](#open-questions) with a recommended default, not assumed.

## What blocks #739

`status:blocked` means "acceptance awaits an explicit prerequisite recorded in #170". #739 has no
comment naming one. From the issues, the prerequisites are:

1. **#732 (privacy design, open).** It fixes the feedback public key, the retention, the quota and
   the consent contract. #732 states that nothing in the upload (F3), inbox (F4) or telemetry (F7)
   is built before its independent security review. This is the real block.
2. **The feedback key holder.** The private half must live only in the feedback team's tool
   (#740, #746). Nobody holds it yet.
3. **Retention.** The owner's 30 days is "proposed, confirmed in #732" (#731 item 5).

The owner's 2026-11-06 4:32 PM decision (invite the first 5 testers now; feedback goes through a
private channel the owner names until #738/#739 land) **does not unblock #739**. It only removes the
schedule pressure: #738/#739 now gate the wider release, not these 5. The interim channel (email or
Teams) sits outside this repository. Bundles sent that way are readable by whoever runs the mailbox,
so the guide (#744) should say "describe the problem, don't attach logs" until the sealed upload
exists. That is a recommendation; the owner names the channel.

This note can be drafted and reviewed now. Nothing below is built before #732's security review.

## Principles (inherited, not new)

- The relay and Storage see **ciphertext only**. Sealing happens on the device.
- Content never reaches this public repository, a log line, a metric label or a URL.
- The relay stays blind (§5.3, §5.10): no account or device id (hashed or not) in metrics, no client
  IPs in logs, log retention 7 days.
- Reuse the blob store machinery (user delegation SAS signed by the relay's managed identity,
  `Blobs.cs`), but a **separate container** with its own lifecycle rule and no device read path.

## Endpoint: "give me an upload URL"

Same transport as the existing blob control frames (`blob-put` / `blob-result`): an authenticated
frame on the device's relay connection, so the account is known without sending any new identity.
New frames: `feedback-put` and `feedback-result`. An HTTPS route is the alternative if a client must
send with no relay session (Q6).

Request (device to relay):

```json
{ "t": "feedback-put", "id": "<client correlation id>", "v": 1, "size": 262144 }
```

- `size` is the **padded ciphertext size** in bytes: a size bucket (`BlobFormat.IsBucket`: powers of
  two up to 1 MiB, then whole MiB) no larger than the cap.
- No name, type, hash, kind or app version. The relay learns account, time and bucket.

Response, success:

```json
{ "t": "feedback-result", "id": "<same>", "url": "<single-blob write SAS>",
  "expires": "<UTC, about 10 minutes>", "ref": "<opaque random receipt>" }
```

- `url` is a **create-only** SAS for one random blob name in the private `feedback` container. It
  grants no list, read, delete or overwrite. The client PUTs the ciphertext with
  `x-ms-blob-type: BlockBlob` and `Content-Type: application/octet-stream`, with **no metadata or
  tags**.
- `ref` is a random string the relay does not index to the account. The client shows "Sent" only
  after Storage returns 201, and may display `ref` so a tester can quote it. It lets the team match
  a conversation, not identify a person.

Response, refusal: `{ "t": "feedback-result", "id": "<same>", "error": "<code>", "retryAfter": <seconds, optional> }`

| Code | Meaning | Client state |
|---|---|---|
| `too-large` | Over the cap | Failed: drop optional items and retry |
| `bad-size` | Not a size bucket | Client bug; do not retry |
| `quota` | Per-account daily or stored quota used | Queued; retry after `retryAfter` |
| `rate-limited` | Too many requests | Queued; back off with `retryAfter` |
| `disabled` | Feature or container off (kill switch) | Failed, not queued: "Feedback isn't available right now" |
| `unavailable` | Storage or identity problem | Queued with backoff |
| `bad-request` | Malformed frame | Client bug; do not retry |

The request carries no content, so a refusal reveals nothing and never echoes input.

If the PUT fails after a URL was granted (network, 403 on expiry), ask for a new URL and resend the
same sealed bundle. The abandoned blob name is removed by lifecycle deletion.

## Sealed-bundle envelope

What goes **in** the bundle (journal tail, versions, redacted config, crash records, optional
screenshot and text, opt-in session turns) is #732/#738's decision. This section fixes only the
envelope, so the relay, Storage and inbox can be built without knowing the contents.

```
magic "ABFB" | format version (u8) | key id (16 bytes) | encapsulated key | records...
```

- **Seal:** hybrid public-key encryption (HPKE, RFC 9180) to the feedback public key, with a fresh
  content key per upload, so one bundle never exposes another. Suggested suite, to confirm at
  review: DHKEM(X25519), HKDF-SHA256, AES-256-GCM.
- **Body:** the 64 KiB AES-256-GCM record stream of §4.6 (nonce = record counter; AAD binds the key
  id, record index and a final-record flag), so truncation and splicing are detected and the
  streaming code is reused.
- **Inside the seal:** a JSON manifest first (`schema` version, `created` rounded to the minute, app,
  host and agent versions, channel, OS and architecture, the items with names and sizes, whether the
  opt-in session turns are included), then the items as separate parts. Nothing descriptive appears
  in the blob name, headers or metadata.
- **Padding:** to the §4.6 size buckets, so the size reveals little.
- **Key id:** names the public key used. It is not secret and identifies the feedback key, not the
  tester. It lets the inbox rotate keys.
- **No sender identity** in the envelope. For a reply the tester types a contact into the text,
  which they see in the preview (Q7).
- **Versioning:** the format byte and manifest `schema` are both checked. The inbox refuses versions
  it does not know and keeps the blob until retention deletes it, for a later tool.
- **Public key distribution:** compiled into the apps with its key id and covered by release
  signing. The relay never sees or serves it. Rotation ships with a release, and the inbox keeps
  accepting the previous key id until old bundles expire.

## Quota, rate and retention

All are server settings (as §4.6 says of blob limits). Recommended defaults, for owner review:

| Setting | Default | Why |
|---|---|---|
| Bundle size | 5 MiB padded | Small; not a free file host |
| Per account per day | 5 uploads, 25 MiB | A tester files a few reports a day at most |
| Per account stored | 20 bundles, 25 MiB, until deleted | Bounds a runaway client |
| Global per day | 500 uploads, 2 GiB, with an alert | Backstop; the cohort is 5 people |
| Rate | 3 requests per minute per account | Stops a retry loop |
| URL lifetime | 10 minutes | Size is known, so shorter than blob URLs |
| **Retention** | **30 days** by container lifecycle rule (#731 item 5, confirmed in #732) | The owner's proposed value |

- The lifecycle rule deletes 30 days after creation. No versioning, soft delete, backups or
  snapshots, as for blobs.
- The inbox (#740) downloads, decrypts, files the summary privately and **deletes the blob**, so most
  bundles live minutes. Lifecycle is the backstop.
- Quota counters hold a count and bytes per account, never a reference to a bundle.

## Access control (write-only)

- Devices: create-only SAS. No read, list or delete.
- Relay identity: signs write SAS only, and has **no read role** on the feedback container.
- Inbox identity (#740): a separate managed identity with read and delete on that container only.
  The private decryption key is **not** in the relay's Key Vault; it stays with the feedback team's
  tool (#732).
- The container has no public access. CORS allows only product origins and `PUT`.
- A SAS URL or `ref` never goes into a log, metric or issue.

## Observability and privacy rules (§5.10)

- New metrics use only the allowed labels (region, node, outcome, client kind, OS, version):
  URLs issued, quota refusals, bytes, lifecycle deletions. No account or device id, even hashed.
- Logs: the ephemeral connection id only. No `ref`, URL or size finer than the bucket.
- CI leak test in the style of S4: upload a marked bundle through the real path against the storage
  emulator and assert that no stored byte, blob name, metadata, tag, URL or log line contains the
  marker, a file name, a MIME type or a device id.
- Tests that the SAS cannot read, list, overwrite or delete, cannot write a second blob, and that
  quota and rate refusals disclose nothing about the account.
- What the relay does learn: account, time and padded size, as for blobs. The privacy notice
  (slipgates.com/privacy, #732) must say so.
- Telemetry (#741) is a separate opt-in tier and must not share these endpoints or counters.

## What the client (#738) needs

1. The feedback public key and key id, compiled in, and the envelope code (seal and record stream).
2. A local **queue of already-sealed bundles**, so the disk holds ciphertext. Retry on reconnect
   with backoff, honouring `retryAfter`; give up after the retention window and tell the tester.
3. UI states mapped from the table: gathering, ready, sending, sent, queued (offline, `quota`,
   `rate-limited`, `unavailable`), failed (`too-large`, `disabled`). "Sent" only after Storage's 201.
4. A preview that is exactly what is sealed: preview and seal read the same in-memory manifest.
5. Cancel deletes the queued bundle.

## Open questions

None has an owner decision. Each has a recommended default.

| # | Question | Recommended default |
|---|---|---|
| Q1 | Who holds the feedback private key, and where? | The Customer Feedback Lead's tool only (#746), an offline backup held by the owner, never in the relay's or inbox's Key Vault |
| Q2 | Retention | 30 days, plus deletion on inbox filing |
| Q3 | Seal suite and key rotation | HPKE X25519 / HKDF-SHA256 / AES-256-GCM; rotate per release train; old key id accepted 30 days |
| Q4 | Size cap and quotas | The table above |
| Q5 | Are opt-in session turns in the same bundle? | Yes, as a separate part, off by default, as #738 says |
| Q6 | Must a tester send with no relay session? | No for the first 5: signed-in only, offline reports queue. A wider beta needs an unauthenticated route with abuse controls |
| Q7 | How does the team reply? | A contact the tester types, visible in the preview; the bundle carries no identity |
| Q8 | Separate account or container? | A separate container with its own identity and lifecycle for the beta; a separate account for a wider one |
| Q9 | Which region stores feedback? | One region, since the inbox is one place |
| Q10 | The interim email/Teams channel, its owner and retention | The owner names it; not part of this design |
| Q11 | Can a tester ask for deletion? | Yes through the team, by quoting `ref`; no relay index, to keep the relay blind |

## Slices once unblocked (after #732's security review)

1. Relay: `feedback-put`, container, quotas, metrics, leak and permission tests.
2. Protocol: envelope seal and open, with test vectors.
3. Infra: container, lifecycle rule and identities (`infra/`, `docs/relay-deploy.md`).
4. Client queue and states (#738).
5. Inbox (#740).
