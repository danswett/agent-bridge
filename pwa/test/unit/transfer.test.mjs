import { test } from 'node:test';
import assert from 'node:assert/strict';
import { P256KeyPair } from '../../js/noise-p256.js';
import { deviceIdOf } from '../../js/identity.js';
import { fromBase64url, toBase64url } from '../../js/blobs.js';
import { fromUtf8, utf8 } from '../../js/bytes.js';
import { TRANSFER, collectRemote, commandKey, macOf, signTransfer } from '../../js/transfer.js';
import { checkLaunch, readLaunchResult } from '../../js/launch.js';
import { HostClient } from '../../js/host-client.js';
import { LaunchService } from '../../js/launch-service.js';

const fromHex = (s) => Uint8Array.from(s.match(/../g).map((b) => parseInt(b, 16)));
// The keys and body tests/AgentBridge.Host.Tests/SignedCommandTests.cs uses: the same MAC on
// both sides is what lets a command from this app be carried out by a host.
const phone = () => P256KeyPair.fromParameters(
  fromHex('905900d4260fa339e62fdd5e34ca9db632ff099271432b0b957d44c389d4576e'),
  fromHex('accab67f642cf9f9c35c24175fb780e76d00f0a2782a5af26dba7ac634647b7f'),
  fromHex('73d0820dc17ec237ae270a993b2b7400d662284d1b921c83c1b839136163138f'));
const source = () => P256KeyPair.fromParameters(
  fromHex('5f659de036a5d83c281f6c80a44633fc4cae31a37a140c73c21bff1d917b3597'),
  fromHex('5d7f19384abde7014a17903abac0264f1add24f8639ab69fb9da7bbfaf24f910'),
  fromHex('48ceda706c4697a48859a8fd3cc690992b208a4dcdd4ae0b2c32f3c65cfcde4a'));

test('a command is signed exactly as the host checks it', async () => {
  const [me, machine] = await Promise.all([phone(), source()]);
  const deviceId = await deviceIdOf(me.public);
  const signed = await signTransfer({
    key: me, deviceId, machinePublic: machine.public,
    session: '0f3c4d5e-6a7b-4c8d-9e0f-a1b2c3d4e5f6', to: '00112233445566778899aabbccddeeff',
    now: 1793300000 * 1000, request: '7d0f3a2e-1c4b-4d5e-8f60-112233445566',
  });
  assert.equal(signed.mac, '_PJ9LEydaL4_cLc4dUQ-wkKfhFj1DfQi-9K7Bl_0fxs');
  assert.equal(signed.by, toBase64url(me.public));
  const body = JSON.parse(fromUtf8.decode(fromBase64url(signed.body)));
  assert.deepEqual(body, {
    v: 1, action: 'session.transfer', request: '7d0f3a2e-1c4b-4d5e-8f60-112233445566',
    principal: { kind: 'user', device: deviceId },
    target: { machine: await deviceIdOf(machine.public), session: '0f3c4d5e-6a7b-4c8d-9e0f-a1b2c3d4e5f6' },
    to: '00112233445566778899aabbccddeeff', issuedAt: 1793300000, expiresAt: 1793300300,
  });
});

test('only the machine a command is for can make its key, from its side of the exchange', async () => {
  const [me, machine, other] = await Promise.all([phone(), source(), P256KeyPair.generate()]);
  const [meId, machineId] = await Promise.all([deviceIdOf(me.public), deviceIdOf(machine.public)]);
  const body = utf8.encode('{"v":1}');
  const mine = await macOf(await commandKey(me, machine.public, meId, machineId), body);
  const theirs = await macOf(await commandKey(machine, me.public, meId, machineId), body);
  const stranger = await macOf(await commandKey(other, me.public, meId, machineId), body);
  assert.deepEqual(mine, theirs);
  assert.notDeepEqual(mine, stranger);
});

test('a transfer must open in a workspace here, and is not also a resume', () => {
  const transfer = { from: 'a'.repeat(32), session: 's', command: { body: 'b', by: 'k', mac: 'm' } };
  assert.match(checkLaunch({ agent: 'copilot', transfer }), /name the workspace it opens in/);
  assert.match(checkLaunch({ agent: 'copilot', transfer, resume: 'x' }), /not both/);
  assert.equal(checkLaunch({ agent: 'copilot', transfer, workspace: 'Bridge' }), null);
});

test('what a transfer started as is read from the outcome', () => {
  const r = readLaunchResult({ outcome: 'started', session: 'n', launched: { agent: 'copilot', resume: 'n', transfer: { from: 'f', session: 's', bytes: 1234 } } });
  assert.deepEqual(r.launched.transfer, { from: 'f', session: 's', bytes: 1234 });
  assert.equal(readLaunchResult({ outcome: 'started', launched: { agent: 'copilot' } }).launched.transfer, null);
});

// Hosts behind scripted channels: hello, then launch.options, each from the table.
function fakeConnection(hosts, presence) {
  const opened = [];
  const closed = [];
  return {
    presence,
    opened,
    closed,
    async openChannel(id) {
      opened.push(id);
      const h = hosts[id];
      if (!h || h.offline) { throw new Error('the handshake timed out'); }
      return { id, hs: { remoteStatic: h.key }, close() { closed.push(id); } };
    },
    client(channel) {
      const h = hosts[channel.id];
      return {
        hello: async () => ({ machine: { name: h.name }, capabilities: h.capabilities }),
        launchOptions: async () => ({ resumable: h.resumable }),
        close: () => channel.close(),
      };
    },
  };
}

test("the other machines' sessions come from those that hand them on, with each one's key", async () => {
  const key = new Uint8Array(65).fill(4);
  const session = { id: 's1', agent: 'copilot', launcher: 'copilot', title: 'One' };
  const hosts = {
    target: { name: 'Target', capabilities: [TRANSFER.capability, TRANSFER.serve], key, resumable: [session] },
    studio: { name: 'Studio PC', capabilities: [TRANSFER.serve], key, resumable: [session] },
    old: { name: 'Old', capabilities: ['sessions.launch'], key, resumable: [session] },
    empty: { name: 'Empty', capabilities: [TRANSFER.serve], key, resumable: [] },
    asleep: { offline: true },
  };
  const presence = ['me', 'target', 'studio', 'old', 'empty', 'asleep'].map((id) => ({ id, name: id, kind: id === 'me' ? 'pwa' : 'host' }));
  const c = fakeConnection(hosts, presence);
  const out = await collectRemote({ connection: c, targetId: 'target', me: 'me', makeClient: (ch) => c.client(ch) });
  assert.deepEqual(out.map((m) => [m.hostId, m.machineName, m.resumable.map((r) => r.id)]), [['studio', 'Studio PC', ['s1']]]);
  assert.deepEqual(out[0].key, key);
  assert.deepEqual(c.opened, ['studio', 'old', 'empty', 'asleep']);
  assert.deepEqual(c.closed.sort(), ['empty', 'old', 'studio']);
});

test('a launch that brings a session over waits for the transfer, not just for a start', async () => {
  const sent = [];
  const t = { send(m) { sent.push(m); return Promise.resolve(); }, close() {}, onmessage: null, onclose: null };
  const client = new HostClient(t, { timeoutMs: 10, answerTimeoutMs: 10, launchTimeoutMs: 20, transferTimeoutMs: 200 });
  const plain = client.launch({ request: 'r1', workspace: 'w' });
  const brought = client.launch({ request: 'r2', workspace: 'w', transfer: { from: 'f', session: 's' } });
  await assert.rejects(plain);
  await new Promise((r) => setTimeout(r, 60));
  t.onmessage({ re: 2, ok: true, result: { outcome: 'started', session: 'n' } });
  assert.equal((await brought).outcome, 'started');
});
test('a transfer launch is resent for longer than a plain one before it is called unconfirmed', async () => {
  const wait = (ms) => new Promise((r) => { setTimeout(r, ms); });
  async function afterTenMinutes(launch) {
    let now = 0;
    const sent = [];
    // Every response lost, as a timeout would leave it.
    const client = { closed: false, launch(data) { sent.push(data); return Promise.reject(new Error('the request timed out')); } };
    const service = new LaunchService({ current: () => ({ client, hostId: 'h' }), uuid: () => 'r1', retryMs: 5, now: () => now });
    service.submit({ hostId: 'h', launch });
    await wait(20);
    now = 10 * 60000;
    await wait(40);
    const result = { entry: service.get('h'), sent: sent.length };
    service.clear();
    return result;
  }
  const plain = await afterTenMinutes({ agent: 'copilot', workspace: 'w' });
  assert.equal(plain.entry.outcome, 'unconfirmed');
  const brought = await afterTenMinutes({ agent: 'copilot', workspace: 'w', transfer: { from: 'f', session: 's' } });
  assert.notEqual(brought.entry.state, 'done');
  assert.ok(brought.sent > plain.sent);
});