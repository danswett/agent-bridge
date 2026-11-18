import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createHash, createPrivateKey, generateKeyPairSync, sign } from 'node:crypto';
import { FleetRegistryReader, registrySignedBytes } from '../../js/fleet-registry-reader.js';
import { RosterVerifier } from '../../js/roster/verifier.js';
import { parseEntry } from '../../js/roster/entry.js';
import { base64Url } from '../../js/roster/codec.js';
import { frame, utf8 } from '../../js/roster/framing.js';
import { fromRaw } from '../../js/roster/p256.js';
import { parseStrict } from '../../js/roster/strict-json.js';

const vectors = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/roster-state.json', import.meta.url)));
const scenario = vectors.scenarios.find((s) => s.name === 'host-sign');
const oracle = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/fleet-registry-signing.json', import.meta.url)));
const canonical = (v) => JSON.stringify(v, (_, x) => x && !Array.isArray(x) && typeof x === 'object'
  ? Object.fromEntries(Object.keys(x).filter((k) => k !== 'sig').sort().map((k) => [k, x[k]])) : x);

async function setup() {
  const roster = new RosterVerifier(vectors.account, vectors.generation, () => 0, { acceptHostSign: true });
  for (const e of scenario.entries) assert.equal((await roster.offer([await parseEntry(utf8(e.wrapper))]))[0].verdict, 'accepted');
  const point = roster.graph.at(roster.state.activeAdd(scenario.devices.H)).body.device.hostSign;
  const key = createPrivateKey({ format: 'jwk', key: { kty: 'EC', crv: 'P-256',
    x: base64Url(point.slice(1, 33)), y: base64Url(point.slice(33)),
    d: createHash('sha256').update('agent-bridge roster test key H sign').digest('base64url') } });
  let now = Date.parse('2026-11-12T18:00:00Z');
  const options = { account: vectors.account, generation: vectors.generation, roster: () => roster, now: () => now };
  const reader = new FleetRegistryReader(options);
  const make = (changes = {}, signer = key) => {
    const record = { ...structuredClone(oracle.record), machine: scenario.devices.H, ...changes };
    delete record.sig;
    record.sig = base64Url(fromRaw(sign('sha256', frame(utf8(oracle.context), utf8(canonical(record))), { key: signer, dsaEncoding: 'ieee-p1363' })));
    return { machine: record.machine, online: true, record };
  };
  const read = (f, r = reader) => r.read([utf8(JSON.stringify(f))]);
  return { roster, reader, options, make, read, key, advance: (ms) => { now += ms; } };
}

test('only an active host add in the pinned account and generation verifies a record', async () => {
  const s = await setup();
  assert.equal((await s.read(s.make()))[0].verified, true);
  for (const changes of [{ account: 'other' }, { generation: 'other' }, { roster: () => null }]) {
    assert.deepEqual(await s.read(s.make(), new FleetRegistryReader({ ...s.options, ...changes })), []);
  }
  assert.deepEqual(await s.read({ ...s.make(), machine: scenario.devices.G }), []);
  s.roster.acceptHostSign = false;
  assert.deepEqual(await s.read(s.make(), new FleetRegistryReader(s.options)), []);
});

test('tampering, missing signatures, wrong keys and malformed signed schemas are dropped', async () => {
  const s = await setup();
  const bad = s.make(); bad.record.sessions[0].node = 'agent_bridge_0000000000000002';
  const unsigned = s.make(); delete unsigned.record.sig;
  const wrongKey = generateKeyPairSync('ec', { namedCurve: 'prime256v1' }).privateKey;
  for (const f of [bad, unsigned, { machine: 'host-test', online: true, record: oracle.record },
    s.make({}, wrongKey),
    ...oracle.refusedSignatures.map((sig) => ({ ...s.make(), record: { ...s.make().record, sig } })),
    s.make({ extra: 'not allowed' }), s.make({ at: [oracle.record.at] }),
    s.make({ sessions: [{ ...oracle.record.sessions[0], state: 'unknown' }] })]) {
    assert.deepEqual(await s.read(f), []);
  }
  const duplicate = JSON.stringify(s.make()).replace('"seq":2', '"seq":2,"seq":2');
  assert.deepEqual(await s.reader.read([utf8(duplicate)]), []);
  assert.equal((await s.read(s.make())).length, 1);
});

test('authority changing during WebCrypto verification discards the in-flight record', async () => {
  const s = await setup();
  let calls = 0;
  const reader = new FleetRegistryReader({ ...s.options, roster: () => calls++ === 0 ? s.roster : null });
  assert.deepEqual(await s.read(s.make(), reader), []);
  const offline = { ...s.make(), online: false };
  assert.deepEqual(await s.read(offline), [{ machine: offline.machine, online: false, sessions: offline.record.sessions, truncated: true, verified: true }]);
});

test('canonical signing bytes match the existing C1 shared oracle', () => {
  for (const record of [oracle.record, oracle.equivalent]) {
    assert.equal(Buffer.from(registrySignedBytes(parseStrict(utf8(JSON.stringify(record)), 'record'))).toString('hex'), oracle.signedHex);
  }
});

test('freshness uses the fake reader clock in both directions and never trusts relay liveness', async () => {
  const s = await setup();
  s.advance(60000);
  assert.equal((await s.read(s.make()))[0].verified, true);
  s.advance(1);
  assert.deepEqual(await s.read(s.make(), new FleetRegistryReader(s.options)), []);
  s.advance(-120002);
  assert.deepEqual(await s.read(s.make(), new FleetRegistryReader(s.options)), []);
});

test('concurrent reads cannot accept a replay; restart needs a later signed time', async () => {
  const s = await setup();
  const results = await Promise.all([s.read(s.make()), s.read(s.make())]);
  assert.equal(results.flat().length, 1);
  assert.deepEqual(await s.read(s.make({ seq: 1 })), []);
  assert.deepEqual(await s.read(s.make({ epoch: '1111111111111111', seq: 1 })), []);
  assert.equal((await s.read(s.make({ epoch: '1111111111111111', seq: 1, at: '2026-11-12T18:00:01Z' }))).length, 1);
  assert.deepEqual(await s.read(s.make({ seq: 3 })), []);
});

test('sequence ordering retains all signed int64 bits rather than rounded JavaScript numbers', async () => {
  const s = await setup();
  for (const [seq, count] of [['9007199254740992', 1], ['9007199254740993', 1], ['9007199254740992', 0]]) {
    const text = canonical(s.make().record).replace('"seq":2', `"seq":${seq}`);
    const sig = base64Url(fromRaw(sign('sha256', frame(utf8(oracle.context), utf8(text)), { key: s.key, dsaEncoding: 'ieee-p1363' })));
    const bytes = utf8(`{"machine":"${scenario.devices.H}","record":${text.slice(0, -1)},"sig":"${sig}"}}`);
    const result = await s.reader.read([bytes]);
    assert.equal(result.length, count);
  }
});
