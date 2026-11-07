import { test } from 'node:test';
import assert from 'node:assert/strict';
import { FeedbackDiagnostics, isSensitiveFeedbackText, sealFeedbackEnvelope, sendFeedbackBundle, serializeFeedbackBundle } from '../../js/feedback.js';
import { toBase64url } from '../../js/blobs.js';

const decodeBase64url = (value) => Uint8Array.from(atob(value.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(value.length / 4) * 4, '=')), (c) => c.charCodeAt(0));

async function openFeedbackEnvelope(envelope, privateKey) {
  assert.deepEqual(envelope.slice(0, 4), new TextEncoder().encode('SGBF'));
  assert.equal(envelope[4], 1);
  const keyIdLength = envelope[5];
  const keyId = new TextDecoder().decode(envelope.slice(6, 6 + keyIdLength));
  let at = 6 + keyIdLength;
  const id = envelope.slice(at, at + 16); at += 16;
  const ephemeralPublic = envelope.slice(at, at + 32); at += 32;
  const salt = envelope.slice(at, at + 32); at += 32;
  const nonce = envelope.slice(at, at + 12); at += 12;
  const ciphertextLength = new DataView(envelope.buffer, envelope.byteOffset, envelope.byteLength).getUint32(at, false); at += 4;
  const ciphertext = envelope.slice(at, at + ciphertextLength);
  const ephemeral = await crypto.subtle.importKey('raw', ephemeralPublic, { name: 'X25519' }, false, []);
  const shared = await crypto.subtle.deriveBits({ name: 'X25519', public: ephemeral }, privateKey, 256);
  const material = await crypto.subtle.importKey('raw', shared, 'HKDF', false, ['deriveKey']);
  const prefix = new TextEncoder().encode(`slipgates feedback envelope v1${keyId}`);
  const context = new Uint8Array(prefix.length + id.length);
  context.set(prefix);
  context.set(id, prefix.length);
  const key = await crypto.subtle.deriveKey(
    { name: 'HKDF', hash: 'SHA-256', salt, info: context },
    material,
    { name: 'AES-GCM', length: 256 },
    false,
    ['decrypt'],
  );
  const plain = new Uint8Array(await crypto.subtle.decrypt(
    { name: 'AES-GCM', iv: nonce, additionalData: context, tagLength: 128 },
    key,
    ciphertext,
  ));
  return { keyId, id, plain, padding: envelope.slice(at + ciphertextLength) };
}

test('diagnostics retain only bounded allowlisted events, log codes, and version fields', () => {
  let now = Date.parse('2026-01-01T00:00:00.000Z');
  const diagnostics = new FeedbackDiagnostics({ now: () => now++ });
  for (let i = 0; i < 105; i++) { diagnostics.recordEvent('host.sessions.upsert'); }
  diagnostics.recordEvent('host.sessions.upsert: machine-id session-title');
  diagnostics.recordLog('relay-message-error');
  diagnostics.recordLog('Authorization: Bearer very-secret-token');
  const bundle = diagnostics.snapshot({
    appVersion: 'abc123',
    protocolVersion: 1,
    hostVersions: ['1.2.3', 'machine name containing private data', '1.2.3'],
    note: '',
  });

  assert.equal(bundle.events.length, 100);
  assert.equal(bundle.events[0].at, new Date(Date.parse('2026-01-01T00:00:00.005Z')).toISOString());
  assert.deepEqual(bundle.logs.map((x) => x.code), ['relay-message-error']);
  assert.deepEqual(bundle.versions.hosts, ['1.2.3']);
  assert.equal(bundle.versions.app, 'abc123');
  assert.equal(bundle.versions.protocol, 1);
  assert.equal(bundle.note, undefined);
  assert.doesNotMatch(serializeFeedbackBundle(bundle), /machine-id|session-title|very-secret-token/);
});

test('feedback notes containing common credentials are blocked before upload', () => {
  for (const note of [
    'Bearer abcdefghijklmnop',
    'password=hunter2',
    'ghp_abcdefghijklmnopqrstuvwxyz123456',
    'eyJhbGciOiJIUzI1NiJ9.eyJ1c2VyIjoiYSJ9.signaturepart',
  ]) { assert.equal(isSensitiveFeedbackText(note), true); }
  assert.equal(isSensitiveFeedbackText('The page stopped after I pressed refresh.'), false);
  assert.throws(() => new FeedbackDiagnostics().snapshot({ note: 'x'.repeat(1001) }), /at most 1000/);
});

test('X25519/HKDF/AES-GCM envelope decrypts and has zero padding to a relay bucket', async () => {
  const recipient = await crypto.subtle.generateKey({ name: 'X25519' }, false, ['deriveBits']);
  const recipientPublic = new Uint8Array(await crypto.subtle.exportKey('raw', recipient.publicKey));
  const bundleId = toBase64url(crypto.getRandomValues(new Uint8Array(16)));
  const plain = new TextEncoder().encode('{"schema":"slipgates-feedback.v1"}');
  const envelope = await sealFeedbackEnvelope(plain, {
    keyId: 'feedback-2026-01',
    publicKey: toBase64url(recipientPublic),
    bundleId,
  });

  assert.equal(envelope.length, 4096);
  const opened = await openFeedbackEnvelope(envelope, recipient.privateKey);
  assert.equal(opened.keyId, 'feedback-2026-01');
  assert.deepEqual(opened.id, decodeBase64url(bundleId));
  assert.ok(opened.padding.every((b) => b === 0));
  assert.deepEqual(opened.plain, plain);
});

test('sending uses the sealed byte size and relay-issued URL; unsafe notes never request a URL', async () => {
  const recipient = await crypto.subtle.generateKey({ name: 'X25519' }, false, ['deriveBits']);
  const publicKey = toBase64url(new Uint8Array(await crypto.subtle.exportKey('raw', recipient.publicKey)));
  const diagnostics = new FeedbackDiagnostics();
  const bundle = diagnostics.snapshot({ appVersion: 'test', protocolVersion: 1 });
  const requests = [];
  const uploads = [];
  const relay = {
    open: true,
    feedbackMaxBytes: 4096,
    async feedbackPut(size) { requests.push(size); return { url: 'https://blob.example/once' }; },
  };
  const result = await sendFeedbackBundle({
    relay,
    bundle,
    keyId: 'test-key',
    publicKey,
    upload: async (url, body) => uploads.push({ url, body }),
  });
  assert.equal(result.storedSize, 4096);
  assert.deepEqual(requests, [4096]);
  assert.equal(uploads[0].url, 'https://blob.example/once');
  assert.equal(uploads[0].body.length, 4096);
  const uploaded = await openFeedbackEnvelope(uploads[0].body, recipient.privateKey);
  assert.deepEqual(uploaded.plain, new TextEncoder().encode(serializeFeedbackBundle(bundle)));

  await assert.rejects(sendFeedbackBundle({
    relay,
    bundle: { ...bundle, note: 'password=secret123' },
    keyId: 'test-key',
    publicKey,
    upload: async () => assert.fail('unsafe note must not upload'),
  }), /credential-like text/);
  assert.deepEqual(requests, [4096]);
});
