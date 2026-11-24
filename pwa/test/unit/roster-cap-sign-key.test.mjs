// The person's capSign key in the PWA (js/roster/cap-sign-key.js): non-extractable WebCrypto in
// IndexedDB, made once, never re-made locally while the roster names one (devices.md, "Custody").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { checkRosterSignature, isPoint, verify } from '../../js/roster/p256.js';
import { utf8 } from '../../js/roster/framing.js';
import { CAP_SIGN_RECORD, openCapSignKey } from '../../js/roster/cap-sign-key.js';
import { generateSigningKey, loadSigningKey } from '../../js/roster/signing-key.js';

function memoryStore() {
  const map = new Map();
  return {
    map,
    get: async (k) => (map.has(k) ? structuredClone(map.get(k)) : undefined),
    put: async (k, v) => { map.set(k, structuredClone(v)); },
    delete: async (k) => { map.delete(k); },
  };
}

const noCapSign = { message: 'no-capsign', code: 'no-capsign' };

test('the first need creates the capSign key and a second call returns the same public key', async () => {
  const store = memoryStore();
  const [made, raced] = await Promise.all([openCapSignKey(store), openCapSignKey(store)]);
  const again = await openCapSignKey(store, { enrolled: made.public });
  assert.ok(isPoint(made.public));
  assert.deepEqual(raced.public, made.public);
  assert.deepEqual(again.public, made.public);
  assert.equal(made.algorithm, 'p256-sha256-der');
  assert.equal(made.tier, 'webcrypto');
});

test('a capSign signature is low-S DER that verifies with its public key', async () => {
  const key = await openCapSignKey(memoryStore());
  for (let i = 0; i < 32; i++) {
    const message = utf8(`capSign ${i}`);
    const der = await key.sign(message);
    checkRosterSignature(der, 'sig');
    assert.ok(await verify(key.public, message, der));
  }
});

test('the capSign private key is non-extractable and never in serialized state', async () => {
  const store = memoryStore();
  const key = await openCapSignKey(store);
  const { privateKey } = await store.get(CAP_SIGN_RECORD);
  assert.equal(privateKey.extractable, false);
  assert.deepEqual(privateKey.usages, ['sign']);
  await assert.rejects(crypto.subtle.exportKey('pkcs8', privateKey));
  await assert.rejects(crypto.subtle.exportKey('jwk', privateKey));
  assert.ok(!Object.values(key).some((v) => v instanceof CryptoKey));
  assert.deepEqual(Object.keys(JSON.parse(JSON.stringify(key))).sort(), ['algorithm', 'public', 'tier']);
});

test('the capSign key is distinct from the generic roster signing key', async () => {
  const store = memoryStore();
  const sign = await loadSigningKey(store);
  const cap = await openCapSignKey(store);
  assert.notDeepEqual(cap.public, sign.public);
});

test('a cleared origin that the roster still names reports no capSign and creates nothing', async () => {
  const store = memoryStore();
  const made = await openCapSignKey(memoryStore());
  await assert.rejects(openCapSignKey(store, { enrolled: made.public }), noCapSign);
  assert.equal(store.map.size, 0);
});

test('a damaged or extractable record is a plain failure and is never regenerated', async () => {
  const store = memoryStore();
  await openCapSignKey(store);
  const kept = store.map.get(CAP_SIGN_RECORD);
  store.map.set(CAP_SIGN_RECORD, { ...kept, public: new Uint8Array(65) });
  await assert.rejects(openCapSignKey(store), noCapSign);
  const loose = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign']);
  store.map.set(CAP_SIGN_RECORD, { ...kept, privateKey: loose.privateKey });
  await assert.rejects(openCapSignKey(store), noCapSign);
  assert.equal(store.map.get(CAP_SIGN_RECORD).privateKey, loose.privateKey);
});

test('a key that differs from the enrolled one is refused', async () => {
  const store = memoryStore();
  await openCapSignKey(store);
  const other = await generateSigningKey();
  await assert.rejects(openCapSignKey(store, { enrolled: other.public }), noCapSign);
});
