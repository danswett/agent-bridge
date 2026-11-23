// Step 8 of the code exchange (docs/protocol/devices.md "The exchange state machines"): the joiner accepts the newest entry of the roster it
// has already verified only if it is an add of exactly this device, by the approver the person confirmed, carrying this exchange's
// transcript, and only after code 2 matched. The PWA's port of EnrollmentJoiner.Accepts. The entries are the parsed shape of
// js/roster/entry.js (`{ body: { op, by, device, approval } }`); signatures and the roster itself are the verifier's job, not this check's.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fromHex } from '../../js/roster/codec.js';
import { EnrollmentLockout, EnrollmentJoiner, macMatches } from '../../js/enrollment-exchange.js';

const V = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/roster.json', import.meta.url), 'utf8')).enrollment.exchanges[0];
const APPROVER = 'bb'.repeat(16);
const bytes = (n, fill) => new Uint8Array(n).fill(fill);
const own = () => ({
  id: 'aa'.repeat(16), dh: bytes(65, 4), sign: bytes(65, 5), signAlg: 'p256-sha256-der', presence: 'key', webauthn: null, kind: 'pwa', platform: 'windows',
  name: 'Kitchen laptop', hostSign: null, hostSignAlg: null, hostSignProof: null, capSign: null, capSignAlg: null, capSignProof: null,
});
const add = (over = {}, device = own()) => ({ body: { op: 'add', by: APPROVER, device, approval: { method: 'code', transcript: fromHex(V.transcript), shown: null }, ...over } });

// A joiner that has taken the offer and had code 2 typed right.
async function matched(c = { t: 1_000_000 }) {
  const j = await EnrollmentJoiner.create(V.account, fromHex(V.h), new EnrollmentLockout('ended-attempts', () => c.t), () => c.t, { nonce: fromHex(V.nN) });
  j.commit();
  await j.onOffer(fromHex(V.nA));
  assert.equal(j.enterCode2(V.code2), 'match');
  return j;
}

test('the joiner accepts an add of exactly its own device by the approver it confirmed, carrying this exchange\'s transcript', async () => {
  assert.equal((await matched()).accepts(add(), own(), APPROVER), true);
});

test('it accepts nothing before code 2 matched or after the attempt ran out of time', async () => {
  const c = { t: 1_000_000 };
  const j = await EnrollmentJoiner.create(V.account, fromHex(V.h), new EnrollmentLockout('ended-attempts', () => c.t), () => c.t, { nonce: fromHex(V.nN) });
  assert.equal(j.accepts(add(), own(), APPROVER), false);
  j.commit();
  await j.onOffer(fromHex(V.nA));
  assert.equal(j.accepts(add(), own(), APPROVER), false);
  j.enterCode2(V.code2);
  assert.equal(j.accepts(add(), own(), APPROVER), true);
  c.t += 5 * 60_000;
  assert.equal(j.accepts(add(), own(), APPROVER), false);
});

test('it refuses an entry that is not an add, is by another signer, or is approved some other way or with another transcript', async () => {
  const j = await matched();
  const other = fromHex(V.transcript);
  other[31] ^= 1;
  const refused = [
    add({ op: 'remove' }), add({ op: 'rename' }), add({ by: 'cc'.repeat(16) }), add({ device: null }), add({ approval: null }),
    add({ approval: { method: 'qr', transcript: fromHex(V.transcript), shown: 'approver' } }),
    add({ approval: { method: 'grandfather', transcript: null, shown: null } }),
    add({ approval: { method: 'code', transcript: other, shown: null } }),
    add({ approval: { method: 'code', transcript: fromHex(V.transcript).subarray(0, 31), shown: null } }),
    add({ approval: { method: 'code', transcript: null, shown: null } }),
  ];
  for (const entry of refused) assert.equal(j.accepts(entry, own(), APPROVER), false, JSON.stringify(entry.body.op) + JSON.stringify(entry.body.approval?.method));
  assert.equal(j.accepts(add(), own(), 'cc'.repeat(16)), false);
  assert.equal(j.accepts(null, own(), APPROVER), false);
  assert.equal(j.accepts({}, own(), APPROVER), false);
});

test('it refuses an add that differs from its own device in any field, key or credential', async () => {
  const j = await matched();
  const changes = [
    { id: 'dd'.repeat(16) }, { kind: 'app' }, { platform: 'android' }, { name: 'Kitchen laptop ' }, { presence: 'none' }, { signAlg: 'x' },
    { dh: bytes(65, 9) }, { dh: null }, { dh: Array.from(bytes(65, 4)) }, { sign: bytes(65, 9) }, { sign: null }, { hostSign: bytes(65, 1) },
    { hostSignAlg: 'p256-sha256-der' }, { hostSignProof: bytes(64, 1) }, { capSign: bytes(65, 1) }, { capSignAlg: 'p256-sha256-der' }, { capSignProof: bytes(64, 1) }, { webauthn: { rpId: 'x.example', credentialId: bytes(16, 1), key: bytes(65, 2), attested: false, backupEligible: false } },
  ];
  for (const change of changes) assert.equal(j.accepts(add({}, { ...own(), ...change }), own(), APPROVER), false, Object.keys(change)[0]);
  const cred = { rpId: 'x.example', credentialId: bytes(16, 1), key: bytes(65, 2), attested: true, backupEligible: false };
  const withCred = async () => ({ ...own(), webauthn: cred });
  assert.equal(j.accepts(add({}, { ...own(), webauthn: { ...cred } }), await withCred(), APPROVER), true);
  for (const change of [{ rpId: 'y.example' }, { credentialId: bytes(16, 2) }, { key: bytes(65, 3) }, { attested: false }, { backupEligible: true }]) {
    assert.equal(j.accepts(add({}, { ...own(), webauthn: { ...cred, ...change } }), await withCred(), APPROVER), false, Object.keys(change)[0]);
  }
});

test('it refuses when it is not told who the approver is or what its own device is, and an own key the entry lacks', async () => {
  const j = await matched();
  for (const approver of [undefined, null, '', 7]) assert.equal(j.accepts(add({ by: approver }), own(), approver), false);
  for (const mine of [null, undefined, 'x']) assert.equal(j.accepts(add(), mine, APPROVER), false);
  assert.equal(j.accepts(add({}, { ...own(), sign: null }), own(), APPROVER), false);
});

test('an add that arrives after the attempt ran out of time is refused and counts as an ended attempt', async () => {
  const c = { t: 1_000_000 };
  const lock = new EnrollmentLockout('ended-attempts', () => c.t);
  const j = await EnrollmentJoiner.create(V.account, fromHex(V.h), lock, () => c.t, { nonce: fromHex(V.nN) });
  j.commit();
  await j.onOffer(fromHex(V.nA));
  j.enterCode2(V.code2);
  c.t += 5 * 60_000;
  assert.equal(j.accepts(add(), own(), APPROVER), false);
  for (let i = 0; i < 2; i++) lock.recordEnded('attempt');
  assert.equal(lock.isBlocked('attempt'), true, 'that expiry was the first of three ended attempts');
});

test('transcripts are compared without stopping at the first difference, and only equal byte strings match', () => {
  const t = bytes(32, 7);
  assert.equal(macMatches(t, bytes(32, 7)), true);
  assert.equal(macMatches(t, bytes(31, 7)), false);
  const last = bytes(32, 7);
  last[31] = 8;
  assert.equal(macMatches(t, last), false);
  assert.equal(macMatches(t, null), false);
});
