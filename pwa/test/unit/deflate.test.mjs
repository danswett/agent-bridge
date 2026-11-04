// Per-message deflate in the PWA's channels (#603), as src/AgentBridge.Transport/MessageDeflate.cs
// does it: raw DEFLATE after a marker byte, each message on its own, only to a host whose
// introduction said it inflates.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ChannelSet, DEFLATES, HEADER, KIND, MARKER, pack, prologue, unpack } from '../../js/channels.js';
import { P256KeyPair, XXHandshake, hex } from '../../js/noise-p256.js';
import { concat, fromUtf8, utf8 } from '../../js/bytes.js';
import { deviceIdOf } from '../../js/identity.js';

const EMPTY = new Uint8Array(0);
const sessions = (n) => JSON.stringify({
  id: 4,
  op: 'echo',
  data: {
    sessions: Array.from({ length: n }, (_, i) => ({
      id: `${i.toString(16).padStart(8, '0')}-0000-4000-8000-000000000000`,
      agent: 'copilot',
      status: i % 3 === 0 ? 'working' : 'idle',
      name: `Session ${i}`,
      activity: { response: 'Done. The tests pass and the change is pushed.', tool: 'powershell' },
    })),
  },
});

test('this runtime deflates', () => {
  assert.equal(DEFLATES, true);
});

test('a message deflates to less than half and inflates back byte for byte', async () => {
  const plain = utf8.encode(sessions(80));
  const packed = await pack(plain);
  assert.equal(packed[0], MARKER);
  assert.ok(packed.length * 2 < plain.length, `${plain.length} bytes packed to ${packed.length}`);
  assert.deepEqual(await unpack(packed), plain);
});

test('a small message, or one that does not shrink, goes as it is', async () => {
  const small = utf8.encode('{"id":1,"op":"hello","data":{}}');
  const noise = crypto.getRandomValues(new Uint8Array(4096));
  noise[0] = '{'.charCodeAt(0);
  assert.equal(await pack(small), small);
  assert.equal(await pack(noise), noise);
  assert.equal(await unpack(small), small);
});

// The same bytes MessageDeflate's tests read: one stored block of raw DEFLATE, no zlib header.
test('a deflated message is raw DEFLATE with no zlib header', async () => {
  const stored = concat(new Uint8Array([MARKER, 0x01, 0x05, 0x00, 0xfa, 0xff]), utf8.encode('hello'));
  assert.equal(fromUtf8.decode(await unpack(stored, 100)), 'hello');
});

test('a message that inflates past what a sender may send, or is corrupt, is refused', async () => {
  const bomb = await pack(new Uint8Array(10 * 1024 * 1024).fill(0x7b));
  assert.ok(bomb.length < 65519);
  await assert.rejects(unpack(bomb), /inflates to more than/);
  await assert.rejects(unpack(new Uint8Array([MARKER, 0xff, 0xff, 0xff, 0xff])));
});

// A host as ChannelHub answers XX, saying it inflates or not; it notes the plaintext of each
// transport message it is sent.
async function pair({ hostInflates }) {
  const hostKey = await P256KeyPair.generate();
  const hostId = await deviceIdOf(hostKey.public);
  const myKey = await P256KeyPair.generate();
  const myId = await deviceIdOf(myKey.public);
  const set = new ChannelSet({ key: myKey, deviceId: myId, name: 'Phone' });
  const host = { id: hostId, received: [], intro: null };
  const handshakes = new Map();
  let chain = Promise.resolve();
  const handle = async (payload) => {
    const kind = payload[0], cid = payload.slice(1, HEADER), body = payload.slice(HEADER), k = hex(cid);
    if (kind === KIND.msg1) {
      const hs = await XXHandshake.create(false, hostKey, prologue(myId, hostId, cid));
      await hs.readMessage(body);
      handshakes.set(k, hs);
      const intro = utf8.encode(JSON.stringify({ name: 'Host', kind: 'host', ...(hostInflates ? { deflate: true } : {}) }));
      const reply = concat(new Uint8Array([KIND.msg2]), cid, await hs.writeMessage(intro));
      chain = chain.then(() => set.relay.onData(hostId, reply));
    } else if (kind === KIND.msg3) {
      host.intro = JSON.parse(fromUtf8.decode(await handshakes.get(k).readMessage(body)));
    } else if (kind === KIND.transport) {
      host.received.push(await handshakes.get(k).receive.decryptWithAd(EMPTY, body));
    }
  };
  let toHost = Promise.resolve();
  set.attach({ open: true, onData: null, send(to, payload) { toHost = toHost.then(() => handle(payload)); } });
  return { set, host, settled: () => toHost };
}

test('a message too large to send as JSON is refused before it is sent, though it would deflate to fit, and the channel goes on', async () => {
  const { set, host, settled } = await pair({ hostInflates: true });
  const ch = await set.open(host.id);

  await assert.rejects(ch.send({ id: 1, op: 'echo', data: { text: 'a'.repeat(70000) } }), /does not fit/);
  await ch.send({ id: 2, op: 'echo', data: {} });
  await settled();

  assert.equal(host.received.length, 1);
  assert.deepEqual(JSON.parse(fromUtf8.decode(await unpack(host.received[0]))), { id: 2, op: 'echo', data: {} });
});

for (const hostInflates of [true, false]) {
  test(`a large message goes ${hostInflates ? 'deflated to a host that said it inflates' : 'as JSON to a host that did not say it inflates'}`, async () => {
    const { set, host, settled } = await pair({ hostInflates });
    const ch = await set.open(host.id);
    const message = JSON.parse(sessions(60));

    await ch.send(message);
    await settled();

    assert.equal(host.intro.deflate, true, 'the PWA says it inflates');
    const [received] = host.received;
    assert.equal(received[0], hostInflates ? MARKER : '{'.charCodeAt(0));
    assert.deepEqual(JSON.parse(fromUtf8.decode(await unpack(received))), message);
  });
}
