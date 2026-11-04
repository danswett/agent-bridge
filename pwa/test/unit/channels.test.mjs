// Channels as the PWA opens them (#603): Noise IK to a host an earlier XX taught, with the first
// request answered in the handshake, and XX again when IK goes unanswered. The vectors are the
// ones the .NET implementation generates (docs/protocol/vectors/noise-ik-p256.json).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { IKHandshake, P256KeyPair, XXHandshake, hex, unhex, IK_PROTOCOL_NAME } from '../../js/noise-p256.js';
import { ChannelSet, KIND, HEADER, prologue } from '../../js/channels.js';
import { concat, fromUtf8, utf8 } from '../../js/bytes.js';
import { deviceIdOf } from '../../js/identity.js';

const EMPTY = new Uint8Array(0);
const vectors = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/noise-ik-p256.json', import.meta.url), 'utf8'));
const key = (k) => P256KeyPair.fromParameters(unhex(k.d), unhex(k.x), unhex(k.y));

test('the IK handshake and transport messages are the ones the .NET implementation generates', async () => {
  assert.equal(vectors.protocol, IK_PROTOCOL_NAME);
  const prologueBytes = unhex(vectors.prologue);
  const respStatic = await key(vectors.keys.respStatic);
  const ini = await IKHandshake.create(true, await key(vectors.keys.initStatic), prologueBytes, respStatic.public, await key(vectors.keys.initEphemeral));
  const res = await IKHandshake.create(false, respStatic, prologueBytes, null, await key(vectors.keys.respEphemeral));
  for (const m of vectors.handshake) {
    const [w, r] = m.from === 'i' ? [ini, res] : [res, ini];
    const message = await w.writeMessage(unhex(m.payload));
    assert.equal(hex(message), m.message);
    assert.equal(hex(await r.readMessage(message)), m.payload);
  }
  assert.ok(ini.isComplete && res.isComplete);
  assert.equal(hex(ini.handshakeHash), vectors.handshakeHash);
  assert.equal(hex(res.handshakeHash), vectors.handshakeHash);
  for (const m of vectors.transport) {
    const [w, r] = m.from === 'i' ? [ini, res] : [res, ini];
    const message = await w.send.encryptWithAd(EMPTY, unhex(m.payload));
    assert.equal(hex(message), m.message);
    assert.equal(hex(await r.receive.decryptWithAd(EMPTY, message)), m.payload);
  }
});

test('an IK opener refuses a host key off the curve before any ECDH', async () => {
  const mine = await P256KeyPair.generate();
  const off = (await P256KeyPair.generate()).public.slice();
  off[64] ^= 1;
  await assert.rejects(IKHandshake.create(true, mine, EMPTY, off), /not on P-256/);
});

// A host as ChannelHub answers: XX, and, where it takes it, IK with hello answered early. Frames
// cross in order, as the relay forwards them, and each sender is noted to count round trips.
async function pair({ takesIk = true, early = ['hello'], ikPatienceMs = 5000 } = {}) {
  const hostKey = await P256KeyPair.generate();
  const hostId = await deviceIdOf(hostKey.public);
  const myKey = await P256KeyPair.generate();
  const myId = await deviceIdOf(myKey.public);
  const set = new ChannelSet({ key: myKey, deviceId: myId, name: 'Phone', ikPatienceMs });
  const host = { id: hostId, takesIk, early, requests: [], senders: [] };
  const handshakes = new Map();
  let toHost = Promise.resolve(), toMe = Promise.resolve();
  const answer = (op) => ({ protocol: 1, machine: { id: hostId, name: 'Host' }, capabilities: [], op });
  const deliver = (payload) => {
    host.senders.push('host');
    toMe = toMe.then(() => set.relay.onData(hostId, payload));
  };
  const handle = async (payload) => {
    const kind = payload[0], cid = payload.slice(1, HEADER), body = payload.slice(HEADER), k = hex(cid);
    const frame = (kind2, b) => deliver(concat(new Uint8Array([kind2]), cid, b));
    const intro = (extra = {}) => utf8.encode(JSON.stringify({ name: 'Host', kind: 'host', ...(host.takesIk ? { ik: host.early } : {}), ...extra }));
    if (kind === KIND.msg1) {
      const hs = await XXHandshake.create(false, hostKey, prologue(myId, hostId, cid));
      await hs.readMessage(body);
      handshakes.set(k, hs);
      frame(KIND.msg2, await hs.writeMessage(intro()));
    } else if (kind === KIND.msg3) {
      await handshakes.get(k).readMessage(body);
    } else if (kind === KIND.ik1 && host.takesIk) {
      const hs = await IKHandshake.create(false, hostKey, prologue(myId, hostId, cid));
      const opening = JSON.parse(fromUtf8.decode(await hs.readMessage(body)));
      handshakes.set(k, hs);
      const asked = opening.early;
      if (asked) { host.requests.push({ early: true, op: asked.op }); }
      frame(KIND.ik2, await hs.writeMessage(intro(asked && host.early.includes(asked.op) ? { early: { ok: true, result: answer(asked.op) } } : {})));
    } else if (kind === KIND.transport) {
      const hs = handshakes.get(k);
      const m = JSON.parse(fromUtf8.decode(await hs.receive.decryptWithAd(EMPTY, body)));
      host.requests.push({ early: false, op: m.op });
      frame(KIND.transport, await hs.send.encryptWithAd(EMPTY, utf8.encode(JSON.stringify({ re: m.id, ok: true, result: answer(m.op) }))));
    }
  };
  const relay = {
    open: true,
    onData: null,
    send(to, payload) {
      assert.equal(to, hostId);
      host.senders.push('me');
      toHost = toHost.then(() => handle(payload));
    },
  };
  set.attach(relay);
  return { set, host };
}

// One request and its response over a channel, as HostClient makes them.
function ask(ch, op) {
  return new Promise((resolve, reject) => {
    ch.onmessage = (m) => resolve(m);
    ch.send({ id: 1, op, data: {} }).catch(reject);
  });
}

const roundTrips = (senders) => Math.ceil(senders.filter((s, i) => s !== senders[i - 1]).length / 2);

test('a host known from an earlier channel is opened with IK and answers hello in the handshake: one round trip, not two', async () => {
  const { set, host } = await pair();
  const early = { op: 'hello', data: {} };

  const cold = await set.open(host.id, { early });
  const coldReply = await ask(cold, 'hello');
  const coldTrips = roundTrips(host.senders);
  cold.close();
  host.senders.length = 0;
  host.requests.length = 0;
  const warm = await set.open(host.id, { early });
  const warmReply = await ask(warm, 'hello');

  assert.ok(cold.hs instanceof XXHandshake);
  assert.equal(coldTrips, 2);
  assert.ok(warm.hs instanceof IKHandshake);
  assert.equal(roundTrips(host.senders), 1);
  assert.deepEqual(host.requests, [{ early: true, op: 'hello' }]);
  assert.equal(warmReply.re, 1);
  assert.equal(warmReply.ok, true);
  assert.deepEqual(warmReply.result, coldReply.result);
  assert.equal(warm.peerName, 'Host');
});

test('a first request other than the one the handshake carried is sent, not answered from it', async () => {
  const { set, host } = await pair();
  await set.open(host.id).then((ch) => ch.close());
  host.requests.length = 0;

  const ch = await set.open(host.id, { early: { op: 'hello', data: {} } });
  const reply = await ask(ch, 'sessions.list');

  assert.equal(reply.result.op, 'sessions.list');
  assert.deepEqual(host.requests, [{ early: true, op: 'hello' }, { early: false, op: 'sessions.list' }]);
});

test('an IK opening the host leaves unanswered opens with XX instead and forgets the key', async () => {
  const { set, host } = await pair({ ikPatienceMs: 100 });
  await set.open(host.id).then((ch) => ch.close());
  assert.ok(set.known.has(host.id));
  // As an older host, put back: it ignores IK's first message.
  host.takesIk = false;

  const ch = await set.open(host.id, { early: { op: 'hello', data: {} } });

  assert.ok(ch.hs instanceof XXHandshake);
  assert.equal((await ask(ch, 'hello')).ok, true);
  assert.equal(set.known.has(host.id), false);
});
