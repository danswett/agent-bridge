// The device roster's vetoes, person commands and head attestations
// (docs/protocol/vectors/roster-commands.json), which the C# library generates
// (tests/AgentBridge.Roster.Tests/RosterCommandTests.cs), replayed through the PWA's port
// (js/roster/commands.js): the same refusals and codes, the same attestation key, message and MAC,
// and an attestation and a signed command that never verify as each other.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { base64Url, fromBase64Url, hex, fromHex } from '../../js/roster/codec.js';
import { utf8 } from '../../js/roster/framing.js';
import { parseEntry } from '../../js/roster/entry.js';
import { RosterFormatError } from '../../js/roster/refusal.js';
import { parseStrict } from '../../js/roster/strict-json.js';
import { RosterVerifier } from '../../js/roster/verifier.js';
import {
  attestationKey, attestationMac, attestationMessage, checkPersonCommand, checkVeto, instant, instantOfMs,
  parsePersonCommand, parseVeto, revokedAdds, verifyAttestation,
} from '../../js/roster/commands.js';
import { commandKey, macOf } from '../../js/transfer.js';

const vectors = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/roster-commands.json', import.meta.url), 'utf8'));
const b64 = (text) => fromBase64Url(text, 'vector');

// The roster every case is checked against: each entry accepted at running time 0, checked an hour on.
async function roster() {
  let now = 0;
  const v = new RosterVerifier(vectors.account, vectors.generation, () => now);
  const offered = await v.offer(await Promise.all(vectors.entries.map((e) => parseEntry(utf8(e.wrapper)))));
  assert.deepEqual(offered.map((o) => o.verdict), vectors.entries.map(() => 'accepted'));
  now = 3600 * 1000;
  return v;
}

async function outcome(parse, check) {
  let parsed;
  try {
    parsed = parse();
  } catch (e) {
    if (e instanceof RosterFormatError) return { refusal: e.code };
    throw e;
  }
  return { code: await check(parsed) };
}

const expected = (c) => (c.refusal !== undefined ? { refusal: c.refusal } : { code: c.code });

for (const c of vectors.vetoes) {
  test(`veto ${c.name}: ${c.about}`, async () => {
    const v = await roster();
    assert.deepEqual(await outcome(() => parseVeto(utf8(c.wrapper)), (veto) => checkVeto(veto, v, b64(vectors.heldReset))), expected(c));
  });
}

for (const c of vectors.commands) {
  test(`person command ${c.name}: ${c.about}`, async () => {
    const v = await roster();
    const x = c.context;
    const context = {
      host: x.host,
      action: x.action,
      now: instant(x.now),
      floor: x.floor ? instant(x.floor) : null,
      nonce: x.nonce ? b64(x.nonce) : null,
      previous: x.previous ? x.previous.map(b64) : null,
    };
    assert.deepEqual(await outcome(() => parsePersonCommand(utf8(c.wrapper)), (command) => checkPersonCommand(command, v, context)), expected(c));
  });
}

test('a time in milliseconds is the instant its RFC 3339 form is', () => {
  assert.deepEqual(instantOfMs(Date.UTC(2026, 10, 4, 12, 0, 0, 123)), instant('2026-11-04T12:00:00.123Z'));
  assert.deepEqual(instant('2000-02-29T00:00:00Z'), { seconds: 951782400, nanos: 0 });
});

const a = vectors.attestation;

async function sharedSecret() {
  const pub = b64(a.attesterPublic);
  const jwk = { kty: 'EC', crv: 'P-256', d: a.attesterPrivate, x: base64Url(pub.subarray(1, 33)), y: base64Url(pub.subarray(33)) };
  const mine = await crypto.subtle.importKey('jwk', jwk, { name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
  const theirs = await crypto.subtle.importKey('raw', b64(a.hostPublic), { name: 'ECDH', namedCurve: 'P-256' }, false, []);
  return new Uint8Array(await crypto.subtle.deriveBits({ name: 'ECDH', public: theirs }, mine, 256));
}

test('an attestation has the key, message and MAC the library gives, and names what its host revoked', async () => {
  const key = await attestationKey(await sharedSecret(), a.attester, a.host);
  assert.equal(hex(key), a.key);
  const message = attestationMessage(a.account, a.generation, a.heads.map(b64), a.host, b64(a.nonce), a.revoked.map(b64));
  assert.equal(hex(message), a.message);
  assert.equal(base64Url(await attestationMac(key, message)), a.mac);
  assert.ok(await verifyAttestation(key, message, b64(a.mac)));
  assert.deepEqual(revokedAdds(await roster()).map(base64Url), a.revoked);
  assert.ok(!(await verifyAttestation(key, attestationMessage(a.account, a.generation, a.heads.map(b64), a.host, b64(a.nonce), []), b64(a.mac))));
});

test('the attestation key is the signed-command key between the same two devices', async () => {
  const z = await sharedSecret();
  const commandSide = await commandKey({ dh: async () => z }, b64(a.hostPublic), a.attester, a.host);
  assert.equal(hex(commandSide), a.key);
});

test('an attestation never verifies as a signed command, nor a signed command as an attestation', async () => {
  const key = fromHex(a.key);
  const message = fromHex(a.message);
  // As a command, its MAC would match, but its body is not a command's JSON.
  assert.equal(base64Url(await macOf(key, message)), a.mac);
  assert.throws(() => parseStrict(message, 'command'), (e) => e instanceof RosterFormatError && e.code === 'json');
  // A signed command's MAC under the same key does not verify the attestation.
  assert.equal(base64Url(await macOf(key, b64(a.command.body))), a.command.mac);
  assert.ok(!(await verifyAttestation(key, message, b64(a.command.mac))));
});
test('two offers of the same genesis at once commit it once', async () => {
  const genesis = await parseEntry(utf8(vectors.entries[0].wrapper));
  const v = new RosterVerifier(vectors.account, vectors.generation, () => 0);
  const [first, second] = await Promise.all([v.offer([genesis]), v.offer([genesis])]);
  assert.equal(first[0].verdict, 'accepted');
  assert.equal(second[0].verdict, 'duplicate');
  assert.equal(v.graph.count, 1);
  assert.equal(v.state.active.size, 1);
  assert.equal(v.state.conflicted.size, 0);
});

test('a first entry and the rest offered at once are all accepted, in order', async () => {
  const parsed = await Promise.all(vectors.entries.map((e) => parseEntry(utf8(e.wrapper))));
  const v = new RosterVerifier(vectors.account, vectors.generation, () => 0);
  const [head, ...rest] = parsed;
  const [a, b] = await Promise.all([v.offer([head]), v.offer(rest)]);
  assert.deepEqual([...a, ...b].map((o) => o.verdict), vectors.entries.map(() => 'accepted'));
});
