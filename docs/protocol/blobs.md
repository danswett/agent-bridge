# Blobs: large files through the relay's blob store

How a file too large for a channel goes from one device to another: sealed by the sender
with a key of its own, uploaded straight to storage, fetched straight from it by the one
device it is for, opened and checked there, then deleted. The relay only issues tickets
and short-lived URLs; it never sees the bytes, and storage sees only ciphertext. Design
§4.6. Implemented by `AgentBridge.Transport` (`BlobFormat`, `BlobHttp`, `BlobTransfer`,
`RelayClient.Blob*Async`), `AgentBridge.Relay` (`BlobService`), the host's
`AttachmentStore` and the PWA's `js/blobs.js`.

Used now for **reply attachments over 256 KiB** (`sessions.attach.blob` in
[v1-sessions.md](v1-sessions.md#attachments)). Session transfer and cross-machine resume
will use the same format and relay messages, in both directions; nothing here is specific
to attachments.

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
([v1-sessions.md](v1-sessions.md#attachmentstart)). The relay sees the ticket go up and
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
allows. Session transfers will default to 25 MB, configurable up to 200 MB (design §9),
within `MaxBytes`.

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
