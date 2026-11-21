import test from 'node:test';
import assert from 'node:assert/strict';
import { createHash, generateKeyPairSync, sign } from 'node:crypto';
import { challenge, qrPayload, Sessions } from './core.mjs';

const hash = b => createHash('sha256').update(b).digest();
const b64 = b => Buffer.from(b).toString('base64url');
const context = { account: 'spike', generation: 1, genesisHash: 'ab'.repeat(32), sessionId: 'cd'.repeat(16), expiry: 300000 };
function cbor(value) {
  const head = (major, n) => n < 24 ? Buffer.from([major * 32 + n]) : Buffer.from([major * 32 + 24, n]);
  if (Number.isInteger(value)) return head(value < 0 ? 1 : 0, value < 0 ? -1 - value : value);
  if (Buffer.isBuffer(value)) return Buffer.concat([head(2, value.length), value]);
  if (typeof value === 'string') return Buffer.concat([head(3, Buffer.byteLength(value)), Buffer.from(value)]);
  return Buffer.concat([head(5, value.size), ...[...value].flatMap(([k, v]) => [cbor(k), cbor(v)])]);
}
function vector(flags = 0x1d, fmt = 'none') {
  const { publicKey, privateKey } = generateKeyPairSync('ec', { namedCurve: 'prime256v1' });
  const jwk = publicKey.export({ format: 'jwk' });
  const id = Buffer.from([1, 2, 3]);
  const key = cbor(new Map([[1, 2], [3, -7], [-1, 1], [-2, Buffer.from(jwk.x, 'base64url')], [-3, Buffer.from(jwk.y, 'base64url')]]));
  const auth = Buffer.concat([hash('phone.example'), Buffer.from([flags]), Buffer.alloc(4)]);
  const client = (type, ctx = context) => Buffer.from(JSON.stringify({ type, challenge: b64(challenge(ctx)), origin: 'https://phone.example', crossOrigin: false }));
  const registrationData = Buffer.concat([auth.subarray(0, 32), Buffer.from([flags | 64]), auth.subarray(33), Buffer.alloc(16), Buffer.from([0, id.length]), id, key]);
  const stmt = fmt === 'packed' ? new Map([['alg', -7], ['sig', sign('sha256', Buffer.concat([registrationData, hash(client('webauthn.create'))]), privateKey)]]) : new Map();
  const registration = { id: b64(id), clientDataJSON: b64(client('webauthn.create')), attestationObject: b64(cbor(new Map([['fmt', fmt], ['attStmt', stmt], ['authData', registrationData]]))), transports: ['internal'] };
  function assertion(ctx = context, data = auth, json = client('webauthn.get', ctx)) {
    return { id: b64(id), clientDataJSON: b64(json), authenticatorData: b64(data), signature: b64(sign('sha256', Buffer.concat([data, hash(json)]), privateKey)) };
  }
  return { registration, assertion, auth, client };
}
function prepared() {
  const v = vector(), store = new Sessions({ origin: 'https://phone.example', rpId: 'phone.example', now: () => 0 });
  store.open(context);
  const facts = store.register(context.sessionId, 'spike', v.registration);
  const ctx = { ...context, sessionId: 'ef'.repeat(16) };
  store.open(ctx);
  return { store, v, ctx, facts };
}
test('challenge is domain separated and changes with every bound field', () => {
  const expected = hash(JSON.stringify(['Slipgates/WebAuthn/v1', 'roster-genesis-presence', 'spike', 1, context.genesisHash, context.sessionId, context.expiry]));
  assert.deepEqual(challenge(context), expected);
  for (const [key, value] of Object.entries({ account: 'other', generation: 2, genesisHash: 'aa'.repeat(32), sessionId: 'aa'.repeat(16), expiry: 299999 })) {
    assert.notDeepEqual(challenge({ ...context, [key]: value }), expected);
  }
});
test('QR contains only the approved URL, session id and four fingerprint groups', () => {
  const url = new URL(qrPayload('https://phone.example/approve', context));
  assert.deepEqual([...url.searchParams.keys()], []);
  assert.deepEqual([...new URLSearchParams(url.hash.slice(1)).keys()], ['session', 'fingerprint']);
  assert.equal(new URLSearchParams(url.hash.slice(1)).get('fingerprint'), 'abab abab abab abab');
  assert.throws(() => qrPayload('http://phone.example', context));
  assert.throws(() => qrPayload('https://phone.example/?secret=x', context));
});
test('independent ES256 vector registers and verifies with exact recorded facts', () => {
  const { store, v, ctx, facts } = prepared();
  assert.equal(facts.aaguid, '00000000-0000-0000-0000-000000000000');
  assert.deepEqual([facts.BE, facts.BS, facts.UV, facts.fmt, facts.signCount, facts.credentialIdLength], [true, true, true, 'none', 0, 3]);
  assert.equal(store.verify(ctx.sessionId, 'spike', v.assertion(ctx)).UV, true);
});
for (const attack of ['wrong challenge', 'wrong rpIdHash', 'wrong origin', 'UV not set', 'UP not set', 'bad signature', 'different account', 'BS without BE', 'backup eligibility changed', 'cross origin', 'invalid crossOrigin']) {
  test(`refuses ${attack} without partial state`, () => {
    const { store, v, ctx } = prepared(), data = Buffer.from(v.auth);
    let json = v.client('webauthn.get', ctx), account = 'spike';
    if (attack === 'wrong challenge') json = v.client('webauthn.get', context);
    if (attack === 'wrong rpIdHash') data[0] ^= 1;
    if (attack === 'wrong origin') json = Buffer.from(json.toString().replace('https://phone.example', 'https://wrong.example'));
    if (attack === 'cross origin') json = Buffer.from(json.toString().replace('"crossOrigin":false', '"crossOrigin":true'));
    if (attack === 'invalid crossOrigin') json = Buffer.from(json.toString().replace('"crossOrigin":false', '"crossOrigin":"false"'));
    if (attack === 'UV not set') data[32] &= ~4;
    if (attack === 'UP not set') data[32] &= ~1;
    if (attack === 'BS without BE') data[32] &= ~8;
    if (attack === 'backup eligibility changed') data[32] &= ~24;
    if (attack === 'different account') account = 'other';
    const input = v.assertion(ctx, data, json);
    if (attack === 'bad signature') input.signature = b64(Buffer.alloc(70));
    const before = store.snapshot();
    assert.throws(() => store.verify(ctx.sessionId, account, input));
    assert.deepEqual(store.snapshot(), before);
  });
}
test('expired sessions and post-close assertions refuse without state changes', () => {
  const { store, v, ctx } = prepared();
  store.now = () => 300000;
  assert.throws(() => store.verify(ctx.sessionId, 'spike', v.assertion(ctx)), /expired/);
  store.close(ctx.sessionId);
  const before = store.snapshot();
  assert.throws(() => store.verify(ctx.sessionId, 'spike', v.assertion(ctx)), /session/);
  assert.deepEqual(store.snapshot(), before);
});
test('replay, reused session and second assertion all refuse', () => {
  const { store, v, ctx } = prepared(), input = v.assertion(ctx);
  store.verify(ctx.sessionId, 'spike', input);
  const before = store.snapshot();
  assert.throws(() => store.verify(ctx.sessionId, 'spike', input), /session/);
  assert.throws(() => store.open(ctx), /session/);
  assert.deepEqual(store.snapshot(), before);
});
test('a second pending request cannot accept the first request answer', () => {
  const { store, v, ctx } = prepared(), second = { ...ctx, sessionId: '12'.repeat(16) };
  store.open(second);
  const before = store.snapshot();
  assert.throws(() => store.verify(second.sessionId, 'spike', v.assertion(ctx)), /challenge/);
  assert.deepEqual(store.snapshot(), before);
  assert.equal(store.verify(ctx.sessionId, 'spike', v.assertion(ctx)).UV, true);
});
test('invalid registration and unregistered keys leave no partial credentials', () => {
  const v = vector(), store = new Sessions({ origin: 'https://phone.example', rpId: 'phone.example', now: () => 0 });
  store.open(context);
  const before = store.snapshot();
  assert.throws(() => store.register(context.sessionId, 'spike', { ...v.registration, attestationObject: b64(Buffer.from([0xa1])) }));
  assert.throws(() => store.verify(context.sessionId, 'spike', v.assertion()), /credential/);
  assert.deepEqual(store.snapshot(), before);
});
test('malformed and duplicate attestation CBOR refuse without partial state', () => {
  const v = vector(), store = new Sessions({ origin: 'https://phone.example', rpId: 'phone.example', now: () => 0 });
  store.open(context);
  for (const bytes of [Buffer.from([0xbf]), Buffer.from([0xa2, 0x61, 0x78, 0, 0x61, 0x78, 0]), Buffer.from([0xa1, 0x61, 0x78, 0xa1])]) {
    const before = store.snapshot();
    assert.throws(() => store.register(context.sessionId, 'spike', { ...v.registration, attestationObject: b64(bytes) }));
    assert.deepEqual(store.snapshot(), before);
  }
});
test('generated sessions expire in five minutes and caller mutation cannot alter binding', () => {
  const store = new Sessions({ origin: 'https://phone.example', rpId: 'phone.example', now: () => 10 });
  const c = store.create(1, context.genesisHash);
  assert.match(c.sessionId, /^[0-9a-f]{32}$/); assert.equal(c.expiry, 300010);
  c.account = 'other';
  assert.equal(store.snapshot().sessions[0][1].account, 'spike');
  assert.throws(() => store.open({ ...context, expiry: 300011 }), /expiry/);
});
test('packed self-attestation verifies, but corrupted attestation and unsupported extensions refuse', () => {
  for (const [flags, fmt, corrupt, accepted] of [[0x1d, 'packed', false, true], [0x1d, 'packed', true, false], [0x9d, 'none', false, false], [0x1d, 'unknown', false, false]]) {
    const v = vector(flags, fmt), store = new Sessions({ origin: 'https://phone.example', rpId: 'phone.example', now: () => 0 });
    store.open(context);
    if (corrupt) { const bytes = Buffer.from(v.registration.attestationObject, 'base64url'); bytes[bytes.indexOf(Buffer.from([0x30])) + 5] ^= 1; v.registration.attestationObject = b64(bytes); }
    const before = store.snapshot();
    if (accepted) assert.equal(store.register(context.sessionId, 'spike', v.registration).fmt, 'packed');
    else { assert.throws(() => store.register(context.sessionId, 'spike', v.registration)); assert.deepEqual(store.snapshot(), before); }
  }
});
test('device-bound nonzero signCount cannot repeat or decrease while synced counters may', () => {
  for (const flags of [5, 0x1d]) {
    const v = vector(flags), store = new Sessions({ origin: 'https://phone.example', rpId: 'phone.example', now: () => 0 });
    store.open(context); store.register(context.sessionId, 'spike', v.registration);
    const ctx = { ...context, sessionId: '34'.repeat(16) }, next = { ...context, sessionId: '56'.repeat(16) };
    const data = Buffer.from(v.auth); data.writeUInt32BE(2, 33);
    store.open(ctx); assert.equal(store.verify(ctx.sessionId, 'spike', v.assertion(ctx, data)).signCount, 2);
    store.open(next);
    for (const count of [2, 1, 0]) {
      data.writeUInt32BE(count, 33);
      const before = store.snapshot();
      if (flags === 5) { assert.throws(() => store.verify(next.sessionId, 'spike', v.assertion(next, data)), /signCount/); assert.deepEqual(store.snapshot(), before); }
    }
    if (flags !== 5) assert.equal(store.verify(next.sessionId, 'spike', v.assertion(next, data)).signCount, 0);
    else { data.writeUInt32BE(3, 33); assert.equal(store.verify(next.sessionId, 'spike', v.assertion(next, data)).signCount, 3); }
  }
});
