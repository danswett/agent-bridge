// The device roster's encoding vectors (docs/protocol/vectors/roster.json), which the C# library
// generates (tests/AgentBridge.Roster.Tests/RosterVectorTests.cs), replayed through the PWA's port
// (js/roster): the same entries accepted, the same refused with the same code, and the same bytes
// for every framing, hash, challenge and enrollment value.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { RosterFormatError } from '../../js/roster/refusal.js';
import { base64Url, tryBase64Url, fromBase64Url, fromHex, hex } from '../../js/roster/codec.js';
import { frame, utf8 } from '../../js/roster/framing.js';
import { point, checkRosterSignature, verify, fromRaw } from '../../js/roster/p256.js';
import { parseEntry, parseHostDescriptor, hostKeyBinding, deviceId, fingerprint, signedBy, signedByCosigner, presenceChallenge, isUtc } from '../../js/roster/entry.js';
import { checkAssertion } from '../../js/roster/webauthn.js';
import * as enrollment from '../../js/roster/enrollment.js';

const vectors = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/roster.json', import.meta.url), 'utf8'));
const b64 = (text) => fromBase64Url(text, 'vector');
const key = (name) => b64(vectors.keys[name].public);

async function refusal(promiseOrFn) {
  try {
    await (typeof promiseOrFn === 'function' ? promiseOrFn() : promiseOrFn);
  } catch (e) {
    if (e instanceof RosterFormatError) return e.code;
    throw e;
  }
  return null;
}

const credential = (c) => ({ rpId: c.rpId, credentialId: b64(c.credentialId), key: b64(c.key), attested: c.attested, backupEligible: c.backupEligible });
const assertionOf = (a) => ({ authenticatorData: b64(a.authenticatorData), clientDataJSON: b64(a.clientDataJSON), signature: b64(a.signature) });

test('framing gives the bytes the library gives', () => {
  for (const v of vectors.framing) assert.equal(hex(frame(...v.parts.map(fromHex))), v.framed);
});

test('base64url has one spelling: padding, the standard alphabet and stray bits are refused', () => {
  for (const v of vectors.base64url.valid) {
    assert.equal(hex(tryBase64Url(v.text)), v.hex);
    assert.equal(base64Url(fromHex(v.hex)), v.text);
  }
  for (const text of vectors.base64url.refused) assert.equal(tryBase64Url(text), null, text);
});

test('points are uncompressed and on the curve, and a device id is the start of its key\'s hash', async () => {
  for (const text of vectors.points.valid) assert.equal(point(text, 'p').length, 65);
  for (const v of vectors.points.refused) assert.equal(await refusal(() => point(v.text, 'p')), v.code, v.text);
  for (const v of vectors.ids) assert.equal(await deviceId(b64(v.dh)), v.device);
  for (const k of Object.values(vectors.keys)) assert.equal(await deviceId(b64(k.public)), k.device);
  assert.equal(fingerprint(b64(vectors.fingerprint.genesis)), vectors.fingerprint.fingerprint);
});

test('a roster or assertion signature is minimal low-S DER', async () => {
  for (const h of vectors.der.valid) checkRosterSignature(fromHex(h), 'sig');
  for (const h of vectors.der.highS) assert.equal(await refusal(() => checkRosterSignature(fromHex(h), 'sig')), 'high-s');
  for (const h of vectors.der.refused) assert.equal(await refusal(() => checkRosterSignature(fromHex(h), 'sig')), 'der', h);
});

test('a raw WebCrypto signature becomes low-S DER that verifies', async () => {
  const pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign', 'verify']);
  const pub = new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey));
  const message = utf8('message');
  for (let i = 0; i < 8; i++) {
    const der = fromRaw(new Uint8Array(await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, pair.privateKey, message)));
    checkRosterSignature(der, 'sig');
    assert.ok(await verify(pub, message, der));
  }
});

test('what is signed and the presence challenges are the library\'s', async () => {
  const body = b64(vectors.signed.body);
  assert.equal(hex(signedBy(body)), vectors.signed.entry);
  assert.equal(hex(signedByCosigner(body)), vectors.signed.cosign);
  assert.equal(base64Url(await presenceChallenge('by', body)), vectors.signed.presenceBy);
  assert.equal(base64Url(await presenceChallenge('cosigner', body)), vectors.signed.presenceCosigner);
});

test('every valid entry parses to its op, hash, size and canonical wrapper, and its signatures verify', async () => {
  const pwaCredential = credential(vectors.assertions[0].credential);
  for (const v of vectors.entries.valid) {
    const entry = await parseEntry(utf8(v.wrapper));
    assert.equal(entry.body.op, v.op, v.name);
    assert.equal(base64Url(entry.hash), v.hash, v.name);
    assert.equal(entry.size, v.size, v.name);
    assert.equal(entry.canonical, v.canonical ?? v.wrapper, v.name);
    assert.ok(await verify(key(v.signer), signedBy(entry.bodyBytes), entry.sig), v.name);
    if (v.cosigner) assert.ok(await verify(key(v.cosigner), signedByCosigner(entry.bodyBytes), entry.cosig), v.name);
    if (entry.uvBy) await checkAssertion(entry.uvBy, pwaCredential, await presenceChallenge('by', entry.bodyBytes));
    if (entry.uvCosigner) await checkAssertion(entry.uvCosigner, pwaCredential, await presenceChallenge('cosigner', entry.bodyBytes));
  }
});

test('every refused entry is refused with the library\'s code', async () => {
  const wrong = [];
  for (const v of vectors.entries.refused) {
    const bytes = v.wrapperHex ? fromHex(v.wrapperHex) : utf8(v.wrapper);
    const code = await refusal(parseEntry(bytes));
    if (code !== v.code) wrong.push(`${v.name}: expected ${v.code}, got ${code}`);
  }
  assert.deepEqual(wrong, []);
});

const hostVector = vectors.entries.valid.find((v) => v.name === 'add-host-v2');
const vectorBody = (v) => JSON.parse(new TextDecoder().decode(b64(JSON.parse(v.wrapper).body)));
const hostBody = vectorBody(hostVector);
const descriptorBytes = (device) => utf8(JSON.stringify(device));

test('the C# host-v2 descriptor round trips without an entry signature or signer authority', async () => {
  const parsed = await parseHostDescriptor(descriptorBytes(hostBody.device), hostBody.account, hostBody.generation);
  const entry = await parseEntry(utf8(hostVector.wrapper));
  assert.deepEqual(parsed, entry.body.device);
  assert.equal(parsed.sign, null);
  assert.equal(parsed.presence, null);
});

test('standalone host descriptors replay every shared C# host-proof refusal', async () => {
  for (const v of vectors.entries.refused.filter((v) => v.name.startsWith('host-proof-') || v.name.startsWith('host-sign-'))) {
    const body = vectorBody(v);
    assert.equal(await refusal(parseHostDescriptor(descriptorBytes(body.device), body.account, body.generation)), v.code, v.name);
  }
});

test('wrong account, wrong generation, tampered dh and tampered proof each fail the binding', async (t) => {
  const other = vectorBody(vectors.entries.valid.find((v) => v.name === 'add-grandfathered')).device;
  for (const [name, account, generation, device] of [
    ['wrong account', 'another-account', hostBody.generation, hostBody.device],
    ['wrong generation', hostBody.account, '0'.repeat(32), hostBody.device],
    ['tampered dh', hostBody.account, hostBody.generation, { ...hostBody.device, id: other.id, dh: other.dh }],
    ['tampered proof', hostBody.account, hostBody.generation, { ...hostBody.device,
      hostSignProof: vectorBody(vectors.entries.refused.find((v) => v.name === 'host-proof-by-another-key')).device.hostSignProof }],
  ]) {
    await t.test(name, async () => {
      assert.equal(await refusal(parseHostDescriptor(descriptorBytes(device), account, generation)), 'host-proof');
    });
  }
});

test('standalone descriptor parsing keeps strict JSON, host-only schema and context checks', async () => {
  const bytes = descriptorBytes(hostBody.device);
  assert.equal(await refusal(parseHostDescriptor(utf8(new TextDecoder().decode(bytes).replace('"id":', '"kind":"host","id":')),
    hostBody.account, hostBody.generation)), 'duplicate-member');
  assert.equal(await refusal(parseHostDescriptor(descriptorBytes({ ...hostBody.device, sign: hostBody.device.hostSign }),
    hostBody.account, hostBody.generation)), 'unknown-member');
  assert.equal(await refusal(parseHostDescriptor(descriptorBytes({ ...hostBody.device, kind: 'pwa' }),
    hostBody.account, hostBody.generation)), 'version');
  assert.equal(await refusal(parseHostDescriptor(bytes, '', hostBody.generation)), 'account');
  assert.equal(await refusal(parseHostDescriptor(bytes, hostBody.account, 'not-a-generation')), 'id');
});

async function descriptorForAccount(account) {
  const pair = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, true, ['sign', 'verify']);
  const hostSign = new Uint8Array(await crypto.subtle.exportKey('raw', pair.publicKey));
  const proof = await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, pair.privateKey,
    hostKeyBinding(account, hostBody.generation, b64(hostBody.device.dh)));
  return descriptorBytes({ ...hostBody.device, hostSign: base64Url(hostSign),
    hostSignProof: base64Url(fromRaw(new Uint8Array(proof))) });
}

test('standalone descriptors refuse unpaired account surrogates instead of accepting replacement aliases', async (t) => {
  for (const [name, account] of [
    ['lone high', '\ud800'], ['lone low', '\udfff'],
    ['embedded high', 'a\udbffz'], ['embedded low', 'a\udc00z'],
    ['two highs', '\ud800\udbff'], ['reversed pair', '\udc00\ud800'],
    ['pair then lone high', '\ud800\udc00\ud800'],
  ]) {
    await t.test(name, async () => {
      const replacement = new TextDecoder().decode(utf8(account));
      const bytes = await descriptorForAccount(replacement);
      assert.equal((await parseHostDescriptor(bytes, replacement, hostBody.generation)).kind, 'host');
      assert.equal(await refusal(parseHostDescriptor(bytes, account, hostBody.generation)), 'account');
    });
  }
});

test('standalone descriptors preserve BMP scalars, replacement characters and valid surrogate pairs', async () => {
  for (const account of ['account', 'a\ud7ff\ue000\ufffdz', 'a\ud800\udc00z', 'a\udbff\udfffz', '\ud83d\ude00'.repeat(32)]) {
    const bytes = await descriptorForAccount(account);
    assert.equal((await parseHostDescriptor(bytes, account, hostBody.generation)).kind, 'host');
  }
});

test('a presence assertion passes or fails each check as the library\'s does', async () => {
  const wrong = [];
  for (const v of vectors.assertions) {
    const code = await refusal(checkAssertion(assertionOf(v.assertion), credential(v.credential), b64(v.challenge)));
    if (code !== (v.code ?? null)) wrong.push(`${v.name}: expected ${v.code}, got ${code}`);
  }
  assert.deepEqual(wrong, []);
});

test('the enrollment values, codes with their leading zeros, typed codes and QR MACs are the library\'s', async () => {
  for (const v of vectors.enrollment.exchanges) {
    const [h, nA, nN] = [v.h, v.nA, v.nN].map(fromHex);
    assert.equal(hex(await enrollment.commit(h, nN)), v.commit);
    assert.equal(hex(enrollment.keyProof(h)), v.keyProofMessage);
    const t = await enrollment.transcript(v.account, h, nA, nN);
    assert.equal(hex(t), v.transcript);
    assert.equal(await enrollment.code1(t), v.code1);
    assert.equal(await enrollment.code2(t), v.code2);
    assert.ok(enrollment.matches(v.code1, enrollment.shown(v.code1)));
  }
  assert.equal(vectors.enrollment.exchanges[0].code1[0], '0');
  for (const v of vectors.enrollment.typed) assert.equal(enrollment.typed(v.input), v.typed ?? null, JSON.stringify(v.input));
  for (const v of vectors.enrollment.qr) {
    const [q, h] = [v.q, v.h].map(fromHex);
    assert.equal(await enrollment.inviteHint(q), v.hint);
    assert.equal(hex(await enrollment.qrJoiner(q, h)), v.joiner);
    assert.equal(hex(await enrollment.qrApprover(q, h)), v.approver);
  }
});

test('at is UTC to the second on a real date', () => {
  for (const at of ['2026-11-03T17:25:10Z', '2024-02-29T00:00:00Z', '2026-11-03T17:25:10.123456789Z']) assert.ok(isUtc(at), at);
  for (const at of ['2025-02-29T00:00:00Z', '2026-11-03T24:00:00Z', '0000-01-01T00:00:00Z', '2026-11-03 17:25:10Z', '+2026-11-03T17:25:10Z']) assert.ok(!isUtc(at), at);
});

