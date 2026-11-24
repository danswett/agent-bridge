import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, generateKeyPairSync } from 'node:crypto';
import { Approval, plan, challenge } from './phone.mjs';
import { qrMatrix, qrSvg } from './qr.mjs';
import { mountPhone } from './screen.mjs';
import { authenticationString } from './sas.mjs';
import { renderSession } from './page.mjs';
import { frame, utf8 } from '../../pwa/js/roster/framing.js';
import { der, readDer } from '../../pwa/js/roster/p256.js';

const now = 1000, origin = 'https://proof.slipgates.com';
const context = (id, generation = '01'.repeat(16)) => {
  const bytes = Buffer.from(JSON.stringify({ account: 'spike', generation }));
  return { account: 'spike', generation, body: bytes.toString('base64url'), genesisHash: createHash('sha256').update(bytes).digest('hex'), sessionId: id.repeat(16), expiry: now + 300000 };
};
const bundle = () => ({ create: { account: 'spike', enrollment: true, sessionId: 'cd'.repeat(16), expiry: now + 300000 }, get: context('ef') });
const fingerprint = context('ef').genesisHash.slice(0, 16).match(/.{4}/g).join(' ');
const fragment = '#' + new URLSearchParams({ session: 'ef'.repeat(16), fingerprint });
const ready = () => plan(bundle(), fragment, now, origin);
const spki = generateKeyPairSync('ec', { namedCurve: 'prime256v1' }).publicKey.export({ type: 'spki', format: 'der' });
const credential = () => ({ rawId: new Uint8Array([1, 2]).buffer, response: { clientDataJSON: new Uint8Array([3]).buffer, attestationObject: new Uint8Array([4]).buffer, authenticatorData: new Uint8Array([5]).buffer, signature: der(1n, 0xffffffff00000000ffffffffffffffffbce6faada7179e84f3b9cac2fc632550n).buffer, getTransports: () => ['internal'], getPublicKey: () => Uint8Array.from(spki).buffer }, authenticatorAttachment: 'platform' });
function browser(calls = []) {
  return { userAgent: 'Synthetic phone browser', platform: 'Synthetic phone OS', credentials: {
    create: async o => { calls.push(['create', o]); return credential(); },
    get: async o => { calls.push(['get', o]); return credential(); },
  } };
}
test('QR matrix matches independent qrcode 1.5.4 version 5 L mask 0 vector', () => {
  const url = origin + '/#session=' + 'cd'.repeat(16) + '&fingerprint=abab+abab+abab+abab';
  const matrix = qrMatrix(url);
  assert.equal(matrix.length, 37);
  assert.equal(createHash('sha256').update(Uint8Array.from(matrix.flat())).digest('hex'), '7d4f04ff62578340f935109fbf4e02e2ffc35cda0e86effbff6d5182005eb82c');
  assert.match(qrSvg(url), /viewBox="0 0 45 45"/);
  assert.throws(() => qrMatrix('x'.repeat(107)), /capacity/);
});
test('browser challenges use roster F body framing and an independent enrollment nonce', async () => {
  const c = context('cd');
  const expected = createHash('sha256').update(frame(utf8('agent-bridge presence v1'), utf8('by'), Buffer.from(c.body, 'base64url'))).digest();
  assert.deepEqual(Buffer.from(await challenge(c)), expected);
  assert.deepEqual(await challenge(c), await challenge(context('ef')));
  const enrollment = bundle().create;
  assert.deepEqual(Buffer.from(await challenge(enrollment)), createHash('sha256').update(frame(utf8('agent-bridge phone enrollment spike v1'), Buffer.from(enrollment.sessionId, 'hex'))).digest());
  await assert.rejects(challenge({ ...c, genesisHash: 'ab'.repeat(32) }));
});
test('enrollment string binds the actual DER-SPKI key and assertion challenge', async () => {
  const c = await challenge(context('ef'));
  const expected = createHash('sha256').update(Buffer.concat([spki, c])).digest('hex').slice(0, 16).match(/.{4}/g).join(' ');
  assert.equal(await authenticationString(spki, c), expected);
  assert.notEqual(await authenticationString(spki, await challenge(context('ef', '02'.repeat(16)))), expected);
  const other = generateKeyPairSync('ec', { namedCurve: 'prime256v1' }).publicKey.export({ type: 'spki', format: 'der' });
  assert.notEqual(await authenticationString(other, c), expected);
  await assert.rejects(authenticationString(new Uint8Array(1), c), /key/);
});
test('phone plan refuses swapped fingerprint, id, account, origin and expired sessions', () => {
  assert.equal(ready().fingerprint, fingerprint);
  for (const change of ['id', 'fingerprint', 'account', 'origin', 'expiry', 'generation', 'same session', 'extra QR field', 'circular enrollment', 'duplicate body']) {
    const b = bundle();
    let f = fragment, o = origin, clock = now;
    if (change === 'id') f = f.replace('efef', 'ffff');
    if (change === 'fingerprint') f = f.replace(fingerprint.slice(0, 4), 'ffff');
    if (change === 'account') b.get.account = 'other';
    if (change === 'origin') o = 'https://wrong.example';
    if (change === 'expiry') clock = b.get.expiry;
    if (change === 'generation') b.get.generation = 2;
    if (change === 'same session') b.create.sessionId = b.get.sessionId;
    if (change === 'extra QR field') f += '&secret=x';
    if (change === 'circular enrollment') b.create.genesisHash = b.get.genesisHash;
    if (change === 'duplicate body') b.get.body = Buffer.from('{"account":"spike","account":"other"}').toString('base64url');
    assert.throws(() => plan(b, f, clock, o));
  }
});
test('comparison precedes sheets and create then get bind different challenges with required UV', async () => {
  const calls = [], approval = new Approval(ready(), () => now);
  await assert.rejects(approval.run(browser(calls), false), /compare/);
  assert.equal(calls.length, 0);
  const codes = [], evidence = await approval.run(browser(calls), true, code => codes.push(code));
  assert.deepEqual(calls.map(c => c[0]), ['create', 'get']);
  assert.equal(calls[0][1].publicKey.rp.id, 'proof.slipgates.com');
  assert.equal(calls[0][1].publicKey.authenticatorSelection.userVerification, 'required');
  assert.equal(calls[1][1].publicKey.userVerification, 'required');
  assert.notDeepEqual(calls[0][1].publicKey.challenge, calls[1][1].publicKey.challenge);
  assert.equal(evidence.registration.id, 'AQI'); assert.equal(evidence.assertion.id, 'AQI');
  assert.deepEqual(readDer(Buffer.from(evidence.assertion.signature, 'base64url')), { r: 1n, s: 1n });
  assert.equal(evidence.phone.path, 'direct platform passkey on phone (self-reported)');
  assert.equal(codes[0], await authenticationString(spki, await challenge(context('ef'))));
});
test('second click, cancellation, post-close and expiry after creation never return evidence', async () => {
  const a = new Approval(ready(), () => now), calls = [];
  const nav = browser(calls); let release, started;
  const reached = new Promise(resolve => { started = resolve; });
  nav.credentials.create = () => new Promise(resolve => { release = resolve; started(); });
  const pending = a.run(nav, true, () => {});
  await assert.rejects(a.run(nav, true, () => {}), /started/);
  await reached;
  a.close(); release(credential());
  await assert.rejects(pending, /closed/); assert.equal(calls.length, 0);
  await assert.rejects(a.run(browser(), true, () => {}), /started|closed/);
  let clock = now;
  const expired = new Approval(ready(), () => clock), n = browser();
  n.credentials.create = async () => { clock = now + 300000; return credential(); };
  await assert.rejects(expired.run(n, true, () => {}), /expired/);
  let wall = now, mono = 0;
  const rollback = new Approval(ready(), () => wall, () => mono), r = browser();
  r.credentials.create = async () => { wall = now - 500000; mono = 300000; return credential(); };
  await assert.rejects(rollback.run(r, true, () => {}), /expired/);
});
test('phone screen displays fingerprint and account before any sheet and closes without return', async () => {
  const elements = Object.fromEntries(['fingerprint', 'account', 'enrollment', 'approve', 'status'].map(id => [id, { textContent: '', disabled: true }]));
  const doc = { getElementById: id => elements[id] }, nav = browser();
  let returned = 0;
  nav.credentials.create = async () => {
    assert.equal(elements.fingerprint.textContent, fingerprint);
    assert.equal(elements.account.textContent, 'Account: spike (test only)');
    return credential();
  };
  nav.credentials.get = async () => { assert.match(elements.enrollment.textContent, /^[a-f0-9]{4}( [a-f0-9]{4}){3}$/); return credential(); };
  const close = mountPhone(doc, ready(), nav, () => { returned++; }, () => now);
  close(); await elements.approve.onclick();
  assert.equal(returned, 0); assert.match(elements.status.textContent, /Refused/);
  mountPhone(doc, ready(), nav, () => { returned++; }, () => now);
  await elements.approve.onclick(); assert.equal(returned, 1);
  assert.equal(elements.enrollment.textContent, await authenticationString(spki, await challenge(context('ef'))));
});
test('PC renders only the fixed-origin QR and never invokes a passkey sheet', () => {
  const elements = Object.fromEntries(['fingerprint', 'account', 'enrollment', 'approve', 'status', 'qr'].map(id => [id, { textContent: '', innerHTML: '', disabled: true }]));
  const calls = [];
  renderSession({ getElementById: id => elements[id] }, bundle(), '', origin, browser(calls), undefined, () => now);
  assert.match(elements.qr.innerHTML, /<svg/); assert.equal(elements.fingerprint.textContent, fingerprint);
  assert.equal(elements.approve.hidden, true); assert.equal(calls.length, 0);
});
test('missing browser public-key export refuses before assertion and never returns', async () => {
  const nav = browser(), calls = [];
  nav.credentials.create = async () => { const c = credential(); c.response.getPublicKey = () => null; return c; };
  nav.credentials.get = async () => { calls.push('get'); return credential(); };
  await assert.rejects(new Approval(ready(), () => now).run(nav, true, () => {}), /key/);
  assert.equal(calls.length, 0);
  const bad = browser();
  bad.credentials.get = async () => { const c = credential(); c.response.signature = new Uint8Array([6]).buffer; return c; };
  await assert.rejects(new Approval(ready(), () => now).run(bad, true, () => {}), /DER/);
});
test('unconfigured automatic return or enrollment display never opens a sheet', async () => {
  const calls = [], nav = browser(calls);
  await assert.rejects(new Approval(ready(), () => now).run(nav, true), /display/);
  const elements = Object.fromEntries(['fingerprint', 'account', 'enrollment', 'approve', 'status'].map(id => [id, { textContent: '', disabled: true }]));
  mountPhone({ getElementById: id => elements[id] }, ready(), nav, undefined, () => now);
  await elements.approve.onclick();
  assert.equal(calls.length, 0); assert.match(elements.status.textContent, /not configured/);
});
