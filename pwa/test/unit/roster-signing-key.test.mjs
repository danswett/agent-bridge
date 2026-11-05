// This device's roster signing key (js/roster/signing-key.js): non-extractable, kept as IndexedDB
// keeps it (a structured clone of the CryptoKey), and signing as the roster requires.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { checkRosterSignature, isPoint, readDer, verify, isLowS } from '../../js/roster/p256.js';
import { signedBy } from '../../js/roster/entry.js';
import { utf8 } from '../../js/roster/framing.js';
import { SIGNING_RECORD, forgetSigningKey, loadSigningKey } from '../../js/roster/signing-key.js';

// What IndexedDB does with a record: a structured clone in, another out.
function memoryStore() {
  const map = new Map();
  return {
    map,
    get: async (k) => (map.has(k) ? structuredClone(map.get(k)) : undefined),
    put: async (k, v) => { map.set(k, structuredClone(v)); },
    delete: async (k) => { map.delete(k); },
  };
}

test('a signing key is made once, kept, and comes back as the same key', async () => {
  const store = memoryStore();
  const made = await loadSigningKey(store);
  const again = await loadSigningKey(store);
  assert.ok(isPoint(made.public));
  assert.deepEqual(again.public, made.public);
  assert.equal(store.map.size, 1);
  const message = signedBy(utf8('{"v":1}'));
  assert.ok(await verify(made.public, message, await again.sign(message)));
});

test('its private key cannot be read out, and it signs nothing but roster signatures', async () => {
  const key = await loadSigningKey(memoryStore());
  assert.equal(key.privateKey.extractable, false);
  assert.deepEqual(key.privateKey.usages, ['sign']);
  await assert.rejects(crypto.subtle.exportKey('pkcs8', key.privateKey));
  await assert.rejects(crypto.subtle.exportKey('jwk', key.privateKey));
});

test('every signature is minimal low-S DER that verifies, however WebCrypto drew its S', async () => {
  const key = await loadSigningKey(memoryStore());
  for (let i = 0; i < 64; i++) {
    const message = signedBy(utf8(`{"n":${i}}`));
    const der = await key.sign(message);
    checkRosterSignature(der, 'sig');
    assert.ok(isLowS(readDer(der).s));
    assert.ok(await verify(key.public, message, der), `signature ${i}`);
  }
});

test('forgetting the device forgets its signing key', async () => {
  const store = memoryStore();
  const first = await loadSigningKey(store);
  await forgetSigningKey(store);
  assert.equal(store.map.has(SIGNING_RECORD), false);
  assert.notDeepEqual((await loadSigningKey(store)).public, first.public);
});
