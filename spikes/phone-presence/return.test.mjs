import test, { mock } from 'node:test';
import assert from 'node:assert/strict';
import { createHash, generateKeyPairSync, sign } from 'node:crypto';
import { mkdtemp, readFile, writeFile, unlink, rmdir } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { Sessions, challenge, enrollmentChallenge, qrPayload } from './core.mjs';
import { fromRaw } from '../../pwa/js/roster/p256.js';
import { authenticationString } from './sas.mjs';
import { poll } from './poller.mjs';
import { confirmOwnerCode } from './owner.mjs';
import { resultText, writeResult } from './results.mjs';
import { handle, SessionMailbox } from './mailbox/worker.mjs';
test.before(() => mock.method(globalThis, 'fetch', () => { throw new Error('Real network forbidden.'); }));
test.after(() => mock.restoreAll());
const origin = 'https://proof.slipgates.com', code = 'abcd ef01 2345 6789', id = 'ab'.repeat(16);
function fixture(blobs = ['bad', 'good']) {
  let clock = 0, requests = 0; const writes = [], checked = [], refused = [];
  const session = { sessionId: id, expiry: 6000 };
  const options = { now: () => clock, monotonic: () => clock, sleep: async ms => { clock += ms; },
    read: async () => { requests++; return blobs; }, verify: async blob => { checked.push(blob); if (blob !== 'good') throw new Error('invalid'); return { facts: { UV: true }, code }; },
    confirm: async value => { assert.equal(value, code); return true; }, write: async report => writes.push(report), refuse: text => refused.push(text) };
  return { session, options, writes, checked, refused, requests: () => requests };
}
test('first valid wins among junk, consumes session, stops and replay refuses', async () => {
  const f = fixture(['bad', 'good', 'later']); await poll(f.session, f.options);
  assert.deepEqual(f.checked, ['bad', 'good']); assert.equal(f.requests(), 1); assert.equal(f.session.used, true);
  assert.equal(f.writes[0].outcome, 'PASS'); assert.equal(f.writes[0].invalidBlobs, 1);
  await assert.rejects(poll(f.session, f.options), /used|ended/);
});
test('all junk expires, each is refused once, no invalid facts are written', async () => {
  const f = fixture(['bad', 'junk']); await poll(f.session, f.options);
  assert.equal(f.requests(), 3); assert.deepEqual(f.checked, ['bad', 'junk']); assert.equal(f.refused.length, 2);
  assert.equal(f.writes[0].outcome, 'FAIL'); assert.equal(f.writes[0].facts, undefined);
});
test('confirmation absent or false fails despite signature and ends session', async () => {
  for (const confirm of [undefined, async () => false]) {
    const f = fixture(['good']); f.options.confirm = confirm; await poll(f.session, f.options);
    assert.equal(f.writes[0].outcome, 'FAIL'); assert.equal(f.writes[0].facts, undefined); assert.equal(f.session.ended, true);
  }
});
test('close and expiry during verification cannot accept or consume evidence', async () => {
  for (const close of [true, false]) {
    const f = fixture(['good']), abort = new AbortController(); f.options.signal = abort.signal;
    f.options.verify = async () => { if (close) abort.abort(); else await f.options.sleep(6000); return { facts: { UV: true }, code }; };
    await poll(f.session, f.options); assert.equal(f.writes[0].outcome, 'FAIL'); assert.equal(f.session.used, undefined);
  }
});
test('monotonic deadline survives wall-clock rollback during mailbox read', async () => {
  const f = fixture([]); f.options.now = () => -500000;
  f.options.read = async () => { await f.options.sleep(300000); return []; };
  await poll(f.session, f.options); assert.equal(f.writes[0].outcome, 'FAIL');
});
test('owner types exact phone code, not yes, and pending input times out', async () => {
  for (const input of [code, 'y', 'yes', '', code.toUpperCase()]) assert.equal(await confirmOwnerCode(code, 6000, { now: () => 0, ask: async () => input }), input === code);
  assert.equal(await confirmOwnerCode(code, 0, { now: () => 0, ask: async () => code }), false);
  assert.equal(await confirmOwnerCode(code, Date.now() + 20, { ask: () => new Promise(() => {}) }), false);
});
function vector(existingBundle) {
  const store = new Sessions({ origin, rpId: 'proof.slipgates.com', now: () => 0 }), create = store.createEnrollment();
  const generated = store.create('12'.repeat(16), Buffer.from(JSON.stringify({ account: 'spike', generation: '12'.repeat(16), fakeGenesis: true }))), get = existingBundle?.get ?? generated;
  const enrollment = existingBundle?.create ?? create;
  const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' }), jwk = publicKey.export({ format: 'jwk' });
  const hash = b => createHash('sha256').update(b).digest(), b64 = b => Buffer.from(b).toString('base64url'), credential = Buffer.from('DO-NOT-RECORD-CREDENTIAL-ID');
  const key = Buffer.concat([Buffer.from('a5010203262001215820', 'hex'), Buffer.from(jwk.x, 'base64url'), Buffer.from('225820', 'hex'), Buffer.from(jwk.y, 'base64url')]);
  const auth = Buffer.concat([hash('proof.slipgates.com'), Buffer.from([29]), Buffer.alloc(4)]), client = (type, c) => Buffer.from(JSON.stringify({ type, origin, challenge: b64(c) }));
  const attested = Buffer.concat([auth.subarray(0, 32), Buffer.from([93]), auth.subarray(33), Buffer.alloc(16), Buffer.from([0, credential.length]), credential, key]);
  const attestation = Buffer.concat([Buffer.from('a363666d74646e6f6e656761747453746d74a068617574684461746158', 'hex'), Buffer.from([attested.length]), attested]);
  const json = client('webauthn.get', challenge(get));
  const registration = { id: b64(credential), clientDataJSON: b64(client('webauthn.create', enrollmentChallenge(enrollment.sessionId))), attestationObject: b64(attestation), transports: ['internal'] };
  const assertion = { id: b64(credential), clientDataJSON: b64(json), authenticatorData: b64(auth), signature: b64(fromRaw(sign('sha256', Buffer.concat([auth, hash(json)]), { key: privateKey, dsaEncoding: 'ieee-p1363' }))) };
  return { bundle: { create: enrollment, get }, evidence: { registration, assertion, phone: { OS: 'Fixture OS', browser: 'Fixture browser', path: 'direct platform (self-reported)', authenticatorAttachment: 'platform' } }, spki: publicKey.export({ type: 'spki', format: 'der' }) };
}
test('actual signature verifies, SAS uses parsed key, refusals leave no shared enrollment', async () => {
  const { verifyEvidence } = await import('./pc.mjs');
  const v = vector(), original = structuredClone(v.bundle), bad = structuredClone(v.evidence); bad.assertion.signature = 'AA';
  await assert.rejects(verifyEvidence(v.bundle, JSON.stringify(bad), { now: () => 0 }));
  const verified = await verifyEvidence(v.bundle, JSON.stringify(v.evidence), { now: () => 0 });
  assert.equal(verified.code, await authenticationString(v.spki, challenge(v.bundle.get))); assert.equal(verified.facts.BE, true); assert.deepEqual(v.bundle, original);
  const other = vector(); await assert.rejects(verifyEvidence(other.bundle, JSON.stringify(v.evidence), { now: () => 0 }));
  const text = resultText({ outcome: 'PASS', invalidBlobs: 1, ...verified });
  for (const secret of [v.evidence.registration.id, v.evidence.assertion.signature, v.evidence.registration.attestationObject, 'DO-NOT-RECORD-CREDENTIAL-ID']) assert.equal(text.includes(secret), false);
  assert.match(text, /owner console confirmation/); assert.match(text, /UNSOLVED/); assert.match(text, /same-PC/);
});
test('results persist once, existing result never overwritten, no partial temp files', async () => {
  const dir = await mkdtemp(join(tmpdir(), 'phone-return-test-')), path = join(dir, 'RESULTS-PHONE.md');
  try {
    await writeFile(path, 'existing', { flag: 'wx' }); await assert.rejects(writeResult(path, { outcome: 'FAIL', invalidBlobs: 0 }));
    assert.equal(await readFile(path, 'utf8'), 'existing'); await unlink(path);
    await writeResult(path, { outcome: 'FAIL', invalidBlobs: 2 }); assert.match(await readFile(path, 'utf8'), /FAIL/); await unlink(path);
  } finally { await rmdir(dir); }
});
test('bounded fixed-origin mailbox reads forbid redirects and foreign URLs', async () => {
  const { readMailbox } = await import('./client.mjs');
  const local = async (url, options) => { assert.equal(url, 'https://proof-mailbox.slipgates.com/s/' + id); assert.equal(options.redirect, 'error'); return Response.json({ blobs: ['bad'] }); };
  assert.deepEqual(await readMailbox(id, { fetch: local }), ['bad']);
  await assert.rejects(readMailbox(id, { fetch: async () => new Response('x'.repeat(80001)) }), /size/);
  assert.deepEqual(await readMailbox(id, { fetch: async () => new Response(null, { status: 404 }) }), []);
  for (const error of [new TypeError('redirect'), new TypeError('network')]) {
    const f = fixture(); let calls = 0; f.options.read = async () => { calls++; throw error; };
    await assert.rejects(poll(f.session, f.options), error); assert.equal(calls, 1);
  }
  const f = fixture(); let reads = 0; f.options.read = async () => { if (++reads === 1) throw new DOMException('offline', 'NetworkError'); return ['good']; };
  assert.equal((await poll(f.session, f.options)).outcome, 'PASS'); assert.equal(reads, 2);
});
test('missing or tampered slot one refuses before any request display or WebAuthn', async () => {
  const { boot } = await import('./bootstrap.mjs');
  const v = vector(), raw = JSON.stringify({ request: v.bundle });
  const fingerprint = createHash('sha256').update(raw).digest('hex').slice(0, 16).match(/.{4}/g).join(' ');
  const url = new URL(qrPayload(origin, v.bundle.get)); url.hash = new URLSearchParams({ session: v.bundle.get.sessionId, fingerprint });
  const changed = structuredClone(v.bundle); changed.create.sessionId = 'ee'.repeat(16);
  for (const blobs of [[], [JSON.stringify({ request: changed })]]) {
    const elements = Object.fromEntries(['account', 'fingerprint', 'approval', 'approve', 'status'].map(id => [id, { textContent: '', hidden: true }]));
    const doc = { getElementById: id => elements[id] };
    await assert.rejects(boot(doc, url, {}, { now: () => 0, fetch: async () => Response.json({ blobs }) }), /request|fingerprint/);
    assert.equal(elements.account.textContent, ''); assert.equal(elements.fingerprint.textContent, ''); assert.equal(elements.approval.hidden, true);
  }
});
test('native seed key is distinct; lost network response retry deduplicates evidence in slot two', async () => {
  const { seedRequest, requestFingerprint } = await import('./client.mjs');
  const v = vector(), calls = []; let state, alarm;
  const storage = { get: async () => structuredClone(state), put: async (_, value) => { state = structuredClone(value); }, getAlarm: async () => alarm ?? null, setAlarm: async value => { alarm = value; }, transaction: async fn => fn() };
  const actor = new SessionMailbox({ storage, blockConcurrencyWhile: fn => fn() }, { now: () => 0 }), env = { MAILBOX: { idFromName: name => name, get: () => actor } };
  const local = async (url, options) => {
    calls.push(options); const response = await handle(new Request(url, { ...options, headers: { ...options.headers, Origin: origin } }), env);
    if (calls.length === 1) { assert.equal(options.headers.Origin, origin); assert.equal(options.headers['Idempotency-Key'], v.bundle.create.sessionId); }
    if (calls.length === 2) throw new DOMException('lost response', 'NetworkError'); return response;
  };
  const raw = await seedRequest(v.bundle, { fetch: local });
  assert.equal(raw, calls[0].body); assert.equal((await requestFingerprint(raw)).replaceAll(' ', ''), createHash('sha256').update(raw).digest('hex').slice(0, 16));
  const { postEvidence } = await import('./return.mjs'); await postEvidence(v.bundle.get.sessionId, v.evidence, { fetch: local });
  assert.deepEqual(calls.slice(1).map(o => o.body), [JSON.stringify(v.evidence), JSON.stringify(v.evidence)]);
  assert.equal(calls[1].headers['Idempotency-Key'], v.bundle.get.sessionId); assert.notEqual(calls[0].headers['Idempotency-Key'], calls[1].headers['Idempotency-Key']);
  assert.equal(state.posts.length, 2);
  assert.equal((await handle(new Request('https://proof-mailbox.slipgates.com/s/' + v.bundle.get.sessionId, { method: 'POST', headers: { Origin: origin, 'Content-Type': 'application/json', 'Idempotency-Key': 'ef'.repeat(16) }, body: JSON.stringify(v.evidence) }), env)).status, 201);
  assert.deepEqual((await (await handle(new Request('https://proof-mailbox.slipgates.com/s/' + v.bundle.get.sessionId), env)).json()).blobs, [raw, JSON.stringify(v.evidence), JSON.stringify(v.evidence)]);
  await assert.rejects(seedRequest({ ...v.bundle, create: v.bundle.get }, { fetch: local }), /id|key/); assert.equal(calls.length, 3);
});
test('valid full request fingerprint enables phone approval and automatic unchanged evidence', async () => {
  const { boot } = await import('./bootstrap.mjs'), v = vector(), raw = JSON.stringify({ request: v.bundle }), posted = [];
  const fingerprint = createHash('sha256').update(raw).digest('hex').slice(0, 16).match(/.{4}/g).join(' '), url = new URL(origin);
  url.hash = new URLSearchParams({ session: v.bundle.get.sessionId, fingerprint });
  const elements = Object.fromEntries(['account', 'fingerprint', 'approval', 'approve', 'status', 'qr', 'enrollment'].map(id => [id, { textContent: '', hidden: true, innerHTML: '' }]));
  const decode = s => Uint8Array.from(Buffer.from(s, 'base64url')).buffer, response = type => ({ rawId: decode(v.evidence.registration.id), authenticatorAttachment: 'platform', response: { clientDataJSON: decode(v.evidence[type].clientDataJSON), attestationObject: decode(v.evidence.registration.attestationObject), authenticatorData: decode(v.evidence.assertion.authenticatorData), signature: decode(v.evidence.assertion.signature), getTransports: () => ['internal'], getPublicKey: () => Uint8Array.from(v.spki).buffer } });
  const nav = { platform: 'Fixture OS', userAgent: 'Fixture browser', credentials: { create: async () => response('registration'), get: async () => response('assertion') } };
  const close = await boot({ getElementById: id => elements[id] }, url, nav, { now: () => 0, fetch: async (_, o) => { if (o.method === 'POST') { posted.push(JSON.parse(o.body)); return new Response(null, { status: 201 }); } return Response.json({ blobs: [raw] }); } });
  try {
    assert.equal(elements.approval.hidden, false); assert.equal(elements.fingerprint.textContent, fingerprint);
    await elements.approve.onclick(); assert.equal(posted.length, 1); assert.deepEqual(posted[0].registration, v.evidence.registration); assert.deepEqual(posted[0].assertion, v.evidence.assertion);
  } finally { close(); }
});
test('PC runner seeds a fresh request, verifies real ES256 and writes only after console match', async () => {
  const { runPC } = await import('./pc.mjs');
  let raw, v, clock = 0; const writes = [], prints = [];
  const options = { now: () => clock, monotonic: () => clock, sleep: async ms => { clock += ms; }, print: s => prints.push(s), refuse: () => {}, write: async r => writes.push(r),
    fetch: async (_, o) => { if (o.method === 'POST') { raw = o.body; v = vector(JSON.parse(raw).request); return new Response(null, { status: 201 }); } return Response.json({ blobs: [raw, JSON.stringify(v.evidence)] }); },
    confirm: async code => { assert.equal(code, await authenticationString(v.spki, challenge(v.bundle.get))); return true; } };
  const result = await runPC(options); assert.equal(result.outcome, 'PASS'); assert.equal(result.invalidBlobs, 0); assert.equal(writes.length, 1);
  assert.match(prints[0], /Genesis UNSOLVED/); assert.match(prints[0], /https:\/\/proof\.slipgates\.com\/\?pc=/);
});
