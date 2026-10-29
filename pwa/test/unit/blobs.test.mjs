import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createHash, randomBytes } from 'node:crypto';
import { BlobFormat, BlobIntegrityError, blobIdOf, maxPlain, openBlob, resolveBlobUrl, sealBlob, storedSize, toBase64url } from '../../js/blobs.js';

const MIB = 1024 * 1024;
const key = () => new Uint8Array(randomBytes(32));
const id = () => new Uint8Array(randomBytes(16));

test('a sealed blob opens to the same bytes, whatever its size', async () => {
  for (const size of [1, BlobFormat.record, BlobFormat.record + 1, 300000]) {
    const file = new Uint8Array(randomBytes(size));
    const k = key();
    const i = id();
    const stored = await sealBlob(file, k, i);
    assert.equal(stored.length, storedSize(size));
    assert.deepEqual(await openBlob(stored, size, k, i), file);
  }
});

// The same vector is in tests/AgentBridge.Transport.Tests/BlobTests.cs: the host opens exactly
// what this seals.
test('the format is byte for byte the one the host opens', async () => {
  const k = Uint8Array.from({ length: 32 }, (_, i) => i);
  const i = Uint8Array.from({ length: 16 }, (_, n) => 0xa0 + n);
  const file = Uint8Array.from({ length: 70000 }, (_, n) => n % 251);
  const stored = await sealBlob(file, k, i);
  assert.equal(stored.length, 131072);
  assert.equal(createHash('sha256').update(stored).digest('hex'), '3e6ef14bdfc54c85c017cf13d7013ecb53c98890cc29d2e1fa247217db3a3a8c');
});

test('stored sizes are padded to the buckets the host and relay use', () => {
  assert.equal(storedSize(1), 4096);
  assert.equal(storedSize(5000), 8192);
  assert.equal(storedSize(16 * BlobFormat.record - 16 * BlobFormat.tag), MIB);
  assert.equal(storedSize(MIB), 2 * MIB);
  assert.equal(storedSize(4 * MIB + 1), 5 * MIB);
  const max = 201 * MIB;
  assert.ok(storedSize(maxPlain(max)) <= max && storedSize(maxPlain(max) + 1) > max);
  assert.ok(maxPlain(max) >= 200 * MIB);
});

test('a changed byte, swapped records, another blob\'s id or dirty padding are refused', async () => {
  const k = key();
  const i = id();
  const size = 3 * BlobFormat.record;
  const stored = await sealBlob(new Uint8Array(randomBytes(size)), k, i);
  const record = BlobFormat.record + BlobFormat.tag;

  const changed = stored.slice();
  changed[100] ^= 1;
  const swapped = stored.slice();
  swapped.set(stored.subarray(0, record), record);
  swapped.set(stored.subarray(record, 2 * record), 0);
  const dirty = (await sealBlob(new Uint8Array(1000), k, i)).slice();
  dirty[dirty.length - 1] = 9;

  await assert.rejects(openBlob(changed, size, k, i), BlobIntegrityError);
  await assert.rejects(openBlob(swapped, size, k, i), BlobIntegrityError);
  await assert.rejects(openBlob(stored, size, k, id()), BlobIntegrityError);
  await assert.rejects(openBlob(stored, 2 * BlobFormat.record, k, i), BlobIntegrityError);
  await assert.rejects(openBlob(dirty, 1000, k, i), BlobIntegrityError);
});

test('a ticket\'s first part is the blob id the records bind', () => {
  const raw = new Uint8Array(randomBytes(16));
  assert.deepEqual(blobIdOf(`${toBase64url(raw)}.body.mac`), raw);
  assert.equal(blobIdOf('short.body.mac'), null);
  assert.equal(blobIdOf(undefined), null);
});

test('a URL from the relay is only followed to Azure Storage over HTTPS or back to the relay', () => {
  const relay = 'https://relay.example';
  assert.equal(resolveBlobUrl(relay, 'https://acct.blob.core.windows.net/blobs/x?sig=1'), 'https://acct.blob.core.windows.net/blobs/x?sig=1');
  assert.equal(resolveBlobUrl(relay, '/v1/dev-blobs/x?sig=1'), 'https://relay.example/v1/dev-blobs/x?sig=1');
  assert.equal(resolveBlobUrl(relay, 'http://acct.blob.core.windows.net/blobs/x'), null);
  assert.equal(resolveBlobUrl(relay, 'https://acct.blob.core.windows.net:8443/x'), null);
  assert.equal(resolveBlobUrl(relay, 'https://evil.example/acct.blob.core.windows.net'), null);
  assert.equal(resolveBlobUrl(relay, 'https://user@acct.blob.core.windows.net/x'), null);
});
