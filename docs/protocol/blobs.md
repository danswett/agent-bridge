# Blobs: large files through the relay's blob store

How a file too large for a channel goes from one device to another: sealed by the sender
with a key of its own, uploaded straight to storage, fetched straight from it by the one
device it is for, opened and checked there, then deleted. The relay only issues tickets
and short-lived URLs; it never sees the bytes, and storage sees only ciphertext. Design
§4.6. Implemented by `AgentBridge.Transport` (`BlobFormat`, `BlobHttp`, `BlobTransfer`,
`RelayClient.Blob*Async`), `AgentBridge.Relay` (`BlobService`), the host's
`AttachmentStore` and the PWA's `js/blobs.js`.

Used for **reply attachments over 256 KiB** (`sessions.attach.blob` in
[v1-sessions.md](v1-sessions.md#attachments)), from a device to a host, and for **session
transfer** ([v1-transfer.md](v1-transfer.md)), from one host to another: the same format and
relay messages, with nothing specific to either.

## Paths

| Path | When | Who carries the bytes |
|---|---|---|
| In the channel, `attachment.chunk` | Files up to **256 KiB** | The relay's bulk lane, inside the Noise channel |
| Direct, `attachment.chunk` | Files up to **100 MiB**, from an app on the host's own machine over its pipe (`sessions.attach.direct`, [local-pipe.md](local-pipe.md)) | The pipe. No relay |
| Blob store | Files over 256 KiB, up to **100 MiB**, from a device through the relay (`sessions.attach.blob`) | Azure Blob Storage, straight from and to the devices |

A client keeps small files in the channel even where the blob store is offered: it costs
nothing stored, needs no second connection, and works with a relay that has no blob store.

## Format (v1)

The sender picks a fresh 256-bit **key** per blob and seals the file as a stream of
records:

* **Records**: 64 KiB of plaintext each (the last one shorter, never empty), each sealed
  with AES-256-GCM and followed by its 16-byte tag.
* **Nonce**: 12 bytes, four zero bytes then the record index as a big-endian uint64.
* **Associated data**: the ASCII bytes `agent-bridge blob v1`, the blob's 16-byte **id**,
  the record index as a big-endian uint64, and one byte, `1` for the last record and `0`
  otherwise. Reordering, truncation, splicing records from another blob and extending past
  the last record all fail to open.
* **Padding**: zeros after the last record, up to a **bucket**: at least 4,096 bytes, then
  powers of two up to 1 MiB, then whole MiB. Storage and the relay learn the bucket, not the
  file's size.

The receiver knows the file's size from the message that named the blob, so it knows the
exact stored length; it requires that many bytes, every record to open, and the padding to
be zeros, then checks the SHA-256 of the whole file. Anything else is refused and the blob
is deleted.

Test vector (asserted by both implementations): key bytes `0x00..0x1F`, id bytes
`0xA0..0xAF`, a 70,000-byte file whose byte `i` is `i % 251` → 131,072 stored bytes with
SHA-256 `3e6ef14bdfc54c85c017cf13d7013ecb53c98890cc29d2e1fa247217db3a3a8c`.

## Relay messages

Text frames on the device's existing relay WebSocket, beside `send`, `presence` and `push`.
The relay knows the account and device from admission; nothing in a message says who
is asking. Every request carries a client `id`, echoed in its `blob-result`.

`ready` says whether the relay has a blob store: `"blobs": { "max": 210763776 }`, the
largest stored size it takes, or `"blobs": null`. A client with no `blobs` sends no blob
messages.

### `blob-put`

`{ "t": "blob-put", "id": 7, "to": "<recipient device id>", "size": 5242880 }`: the stored
size, a bucket. Result:

```json
{ "t": "blob-result", "id": 7, "blob": "<ticket>", "url": "https://…", "expires": "2026-10-28T17:30:00+00:00" }
```

The **ticket** names the blob from then on: `<name>.<body>.<mac>`. `<name>` is 16 random
bytes, base64url (22 characters), and is the id the format binds; the body says who sent
it, to whom, its stored size and when it expires; the MAC binds all that to the account
with a key only the relay has, so any relay replica can check it and no client can forge or
alter one. A ticket is at most 512 characters.

`url` is where to **Put Blob** the stored bytes once (`x-ms-blob-type: BlockBlob`,
`content-type: application/octet-stream`). It can create the blob and nothing else (no
overwrite, read or list), over HTTPS, until `expires` (30 minutes).

### `blob-get`

`{ "t": "blob-get", "id": 8, "blob": "<ticket>" }`. Only the device the blob was put for
may get it. Result: `{ "t": "blob-result", "id": 8, "url": "https://…", "size": 5242880,
"expires": "…" }`, a URL that can read that one blob, until `expires`. The relay checks the
stored size against the ticket first: a write URL cannot cap what is uploaded, so a blob
of any other size is deleted and refused (`size-mismatch`).

### `blob-delete`

`{ "t": "blob-delete", "id": 9, "blob": "<ticket>" }`, by its sender or its recipient.
Result: `{ "t": "blob-result", "id": 9, "deleted": true }` (`false` if it was already gone).
A recipient deletes a blob as soon as it has checked it, and one it cannot use.

### Errors

`{ "t": "blob-result", "id": 7, "error": "<code>" }`:

| Code | Meaning |
|---|---|
| `disabled` | This relay has no blob store |
| `bad-request` | A field is missing or malformed |
| `too-large` | Over `ready.blobs.max` |
| `bad-size` | Not a bucket size |
| `quota` | The account has put its daily allowance (2 GiB per rolling 24 hours by default) |
| `rate-limited` | More than 30 puts in a minute by the account |
| `not-found` | No such blob, or one this device may not see: another account's and another device's tickets get exactly this, so a refusal tells them nothing |
| `expired` | The ticket's lifetime (24 hours) is over |
| `size-mismatch` | What was uploaded is not the size ticketed; the blob was deleted |
| `unavailable` | Storage could not be reached; try again |

## What a message naming a blob carries

The blob's ticket and **key** travel only inside the end-to-end channel, with the file's
name, size, type and SHA-256, in the op that uses it. For reply attachments that is
`attachment.start` with `blob: { "id": "<ticket>", "key": "<base64url, 32 bytes>" }`
([v1-sessions.md](v1-sessions.md#attachmentstart)); for a session transfer, `session.transfer`'s
result, between the two hosts ([v1-transfer.md](v1-transfer.md#sessiontransfer)). The relay sees the ticket go up and
come back, never the key.

## Limits and lifetime

These are relay settings (`Relay:Blobs:*`), not protocol constants:

| Setting | Default |
|---|---|
| `MaxBytes`, largest stored blob | 201 MiB: a 200 MiB session transfer, sealed and padded |
| `MaxBytesPerAccountPerDay` | 2 GiB per rolling 24 hours, counted when a blob is put |
| `MaxPutsPerMinute` | 30 per account |
| `Lifetime`, how long a ticket can be read with | 24 hours |
| `UrlLifetime`, how long one URL works | 30 minutes |

A reply attachment is at most **100 MiB** (`AttachmentLimits.BlobBytes`) whatever the relay
allows. A session transfer is at most `launch.transfer.maxBytes` on each host, **25 MiB** by
default and up to **200 MiB** (design §9), within `MaxBytes`. The receiving host deletes a
transfer's blob as soon as it has fetched it, whatever the outcome.

Storage deletes every blob a day after it was written, whatever happens (a lifecycle
rule), so one nobody fetched or deleted lives one to two days at most. Rate and quota
counters are kept in memory per relay replica; with more than one replica an account's
home node will hold them (design §5.8).

## Security

* **End to end.** Plaintext exists only at the two ends; the key only inside the Noise
  channel. Storage and the relay see ciphertext padded to a bucket, its time and its
  sender's and recipient's device ids.
* **Scoped.** Tickets are bound to the account; only the recipient device can read a blob,
  and only its sender or recipient can delete it. URLs are for one blob, one direction,
  minutes.
* **No secrets in config.** The relay signs URLs with a user delegation key it gets from
  Entra as its managed identity. The storage account has shared keys turned off, so no
  account key or service SAS can exist; nothing secret is configured.
* **Where a URL may point.** Clients accept a URL only on `https://*.blob.core.windows.net`
  (default port, no user info), or on the relay's own origin for a dev relay with the
  in-memory store. Anything else is refused before any request is made.
* **No PII.** The relay logs and counts only operations, outcomes and sizes; never names,
  tickets, URLs or account ids.

## Received images: host-to-PWA contract (#2750, proposed)

**Spec only; not implemented or enabled by this section.** Upload bounds in #2749
and #2750's device-to-host work do not supply a received-image reference or read
path. This reverse path reuses the sealed format and relay messages above; it
does not change upload limits, `attachment.*`, session transfer or HA behavior.
No HA request, public image endpoint or plaintext relay proxy is involved.

### Reference and discovery

A future host offers `sessions.images` only to a currently pinned, authorized
person device, with `sessions.read`, a usable blob relay and this read path
implemented. `sessions.attach.blob` alone never implies image reads. Without
the new capability, clients send no image-read op and keep their text/file chips.
Pinning off is not permission to offer it. Service, connector, host and agent
principals get neither references nor retrieval, even if they can read text.

Inside the encrypted session channel, optional `images` on a history `Prompt`
or response `Part` is an ordered array of these exact v1 reference objects:

```json
{
  "ref": "4c1d7e2a-9b3f-4e6a-8c5d-0f1e2d3c4b5a",
  "type": "image/png",
  "size": 70000,
  "sha256": "<canonical unpadded base64url of 32 digest bytes>",
  "expiresAt": "<RFC 3339 UTC timestamp, as protocol times>"
}
```

`activity.userImages` and `activity.responseImages` carry the same objects for
the latest prompt/response, bound to `turn`/`responseTurn` respectively. No
turn identity means no image references. All existing history/activity byte
budgets still apply. At most 10 references per turn, across prompt and parts;
`imagesMore` on that turn (or `userImagesMore`/`responseImagesMore` on activity)
counts omitted images, never silent truncation.

`ref` is a fresh random lowercase UUID, opaque, not a path, URL, content hash,
attachment-upload id or bearer grant. Its immutable binding is account, serving
host, session, turn/part, exact image bytes and requesting person device. Repeat
pages for that device reuse it until expiry; another device gets another ref.
`size` is an integer plaintext byte count; `sha256` covers exactly those bytes.
Only `image/png`, `image/jpeg` and `image/webp` are allowed in v1. The host checks
the file format, not a filename extension. SVG, HTML, video and other types are
not rendered. No name, local path, inline data, storage URL, ticket or key is in
the reference. New references are strictly parsed: duplicate/unknown members,
bad types/encodings or out-of-bound values make that image unavailable, without
discarding the surrounding conversation.

### Retrieval and authority

1. The person client sends `session.image.get` through the same authenticated,
   encrypted host channel: `data: { "id": "<session UUID>", "ref": "<ref UUID>" }`.
   Exactly these fields are allowed. Nothing supplied selects a path, recipient,
   account, URL or decryption key.
2. Before reading bytes, the host rechecks the current pinned person lease,
   account/channel peer, session-read permission and exact unexpired binding.
   It serves only a source already bound to that conversation, never a path
   parsed from transcript text, a symlink escape or a client-supplied path.
   A changed source invalidates the reference; it is not silently rebound.
3. The host seals those immutable bytes with a fresh key and ticket, using
   `blob-put` addressed to the authenticated requesting device, then uploads.
   Recheck authorization and expiry before releasing the result; cancel and
   delete the upload if either changed. Success `data` is exactly
   `{ "image": <the reference>, "blob": { "id": "<ticket>", "key": "<32 bytes, canonical base64url>" } }`.
   The returned reference must equal the requested one in every field.
   The key travels only in this Noise response, never a relay control frame;
   the opaque ticket also appears in the relay's existing get/delete messages.
4. The PWA issues `blob-get` on its own admitted relay connection. The relay's
   existing account/recipient checks apply; another device's ticket is
   `not-found`. Validate the URL against the rules above and the build's CSP;
   reject redirects, omit credentials and referrers on the storage GET.
   No host sign-in token, cookie or Noise key is forwarded to storage.
5. Before allocating/decoding, enforce the plaintext/stored bounds and require
   the relay size and streamed body length to equal `storedSize(image.size)`.
   Open every authenticated record, validate finality/padding and SHA-256, then
   check raster type/dimensions. No partial or unverified image is displayed.
   Render only a memory object URL, never the storage URL or transcript HTML.
   Delete the ticket after verification or terminal failure; release the key.

This is a read, not a person-command signature, approval, launch or upload grant.
It adds no credential, enrollment or authorization bypass. The relay's URL is
a ciphertext capability: revocation cannot recall a ticket, URL, key or bytes
already received. Host gates prevent new grants; clients cancel and discard
pending results on lease/channel/account changes. Best-effort deletion limits
remaining access, but a still-admitted recipient can renew read URLs until the
ticket expires; an issued URL can last a further URL lifetime. These existing
relay bounds, not the 15-minute UI deadline, bound this residual when deletion
fails. No immediate cryptographic revocation is promised.

### Bounds, state and privacy

An image is 1 through **8,388,608 bytes (8 MiB)**, with stored size also within
`ready.blobs.max`. Both ends enforce the smaller bound. Decode at most
16,777,216 pixels, with neither dimension above 8,192; check dimensions before
full decode. The PWA keeps at most two fetches and 32 MiB of received plaintext
per tab, evicting/revoking old object URLs before exceeding that budget.

References expire at most **15 minutes after first issue**, never slide on
read/reconnect and never outlive their source. `expiresAt` is the host's deadline,
not the storage URL's. Check it before upload, release, fetch and display; reject
at equality. The host uses elapsed time as well as wall time so clock rollback
cannot extend a live ref. PWA timers drop displayed bytes at expiry.
Storage tickets/URLs retain the relay bounds above (24 hours/30 minutes by
default), but confer no permission to extend the image reference. A reconnect
may rediscover a fresh reference only after normal authorization/source checks.
The host tracks issued tickets and attempts deletion at ref expiry/revocation.
At most one ticket is retained per ref: a retry deletes its predecessor before
replacement, or fails unavailable if cleanup cannot be confirmed. In-flight
duplicates are `busy`; at most two uploads per device and 64 per host run at once.
Pending uploads are cancelled at expiry and cleaned up even after caller timeout.

The serving host holds at most 64 live bindings and 64 MiB of private source
snapshots; a full budget refuses new retrieval/discovery rather than evicting
live bindings silently. Bindings and copies are ephemeral and cleared on
expiry, sign-out or host restart; restart makes old refs unavailable. They run
on whichever host owns that session, not a designated fleet machine. If it
leaves, its images are unavailable; other hosts and text conversations continue.
Relay ciphertext remains in shared storage until deletion/lifecycle cleanup;
no new durable service or machine-specific state store is introduced.

No received bytes, base64, keys, tickets, signed URLs, hashes, refs, filenames,
paths, session/account/device ids or raw exceptions enter logs, traces, metrics,
crash breadcrumbs or feedback bundles. Emit only operation, fixed outcome code
and bounded counts/sizes. No image content or retrieval result goes to push,
localStorage, IndexedDB, Cache Storage or the service worker cache; storage GETs
use `cache: "no-store"`. Sign-out/account change cancels requests, clears memory
and revokes object URLs; late completions cannot repopulate another account.

### Failure behavior

Host failures use the normal failed-request envelope with fixed `error` codes:
`bad-request` for malformed input; `unavailable` for unknown, expired, wrong
session/device/account, unauthorized or changed-source references (same shape,
no existence oracle); `unsupported` for absent capability/type; `too-large` for
byte/pixel limits; `busy` for resource limits; `unavailable` for relay/storage
failure. No raw exception, path or URL is returned.

The PWA preserves text/order and shows an explicit image-unavailable placeholder.
Malformed metadata, bad URL/redirect, length/hash/tag/padding/type/decode failure
is terminal for that attempt: discard bytes/key and best-effort delete the blob,
never render or silently fall back to HA/local paths. Expiry or relay
`not-found`/`expired` requires rediscovery, not replay of the same ticket.
Offline, timeout or relay `unavailable` permits an explicit user retry while
the ref is valid; no background retry loop. Each retrieval has a 60-second
deadline and cancellation on navigation. Cleanup failure records only a fixed
code, does not mask the original failure, and leaves lifecycle expiry as backstop.

### Tests-first implementation cuts (separate authorization required)

Every cut includes tests, fixtures and docs in **fewer than 400 whole changed
lines**; split again before the cap. Each starts with runnable failing tests,
then implementation and focused GREEN evidence. None is claimed implemented here.

1. **Wire types/vectors (target 220 lines).** RED: valid reference/request/result,
   boundary sizes/times, duplicate/extra fields and malformed metadata. GREEN:
   shared C# shapes/strict validation and golden vectors; no capability enabled.
2. **Host binding source (target 300).** After 1. RED: exact session/turn/device
   binding, path/symlink rejection, changed source, 15-minute equality/rollback,
   restart and capacity. GREEN: bounded private immutable snapshots/ref lookup.
3. **Host read grant (target 320).** After 2. RED: pinning-off, agent/service/host,
   cross-account/device/session, revoked mid-upload, disabled relay and timeout;
   valid transfer addressed only to channel peer. GREEN: gated op/capability,
   fresh sealing/upload, fixed failures, duplicate/resource bounds and expired/
   orphan ticket cleanup; no image discovery yet.
4. **Conversation discovery (target 260).** After 3. RED: activity/history same
   binding/order, absent turn, byte/count caps and text-only peer gets no refs.
   GREEN: optional reference arrays/counts; no arbitrary transcript path reads.
5. **PWA verified retrieval (target 320).** After 1 and 3. RED: shared vectors,
   unauthorized ticket, hostile URL/redirect, short/long stream, record tamper,
   padding/hash mismatch, expired ref and oversized raster. GREEN: bounded
   relay GET/decrypt/verify pipeline; no DOM display yet.
6. **PWA display/lifetime (target 300).** After 4 and 5. RED: placeholder/retry,
   two-fetch/memory caps, object-URL revocation, sign-out during fetch, expiry,
   stale completion and no persistent/service-worker cache. GREEN: raster-only
   rendering with cancellation and account-generation guards.
7. **Privacy/HA-off integration (target 280).** After 6. RED: disposable host/PWA
   receives a real small image with HA absent; wrong-device retrieval fails;
   tampered image never renders; captured logs/telemetry contain no injected
   content/metadata/secret canaries. GREEN: close integration gaps only, retain
   upload/text regression tests. Quality obtains two distinct reader families.

Beta feedback uses its own container and rules: see [feedback.md](feedback.md).
