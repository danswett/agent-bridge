import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ALL, HostSession } from '../../js/host-session.js';
import { HostClient, HostError } from '../../js/host-client.js';
import { SessionsStore } from '../../js/sessions-store.js';
import { AnswerService } from '../../js/answer-service.js';
import { ReplyService } from '../../js/reply-service.js';
import { StopService } from '../../js/stop-service.js';
import { LaunchService } from '../../js/launch-service.js';
import { prepareAttachment } from '../../js/attachments.js';
import { DemoBlobStore, DemoConnection, DemoHost } from '../../js/demo.js';

const flush = () => new Promise((r) => setImmediate(r));

// A relay connection whose channel opens the test settles by hand.
function fakeConnection(presence) {
  const listeners = new Map();
  const opens = [];
  return {
    presence,
    ready: true,
    opens,
    emit(type, detail) { for (const fn of listeners.get(type) ?? []) { fn(detail); } },
    on(type, fn) { (listeners.get(type) ?? listeners.set(type, []).get(type)).push(fn); },
    openChannel(peerId) {
      let resolve, reject;
      const p = new Promise((res, rej) => { resolve = res; reject = rej; });
      const channel = { peerId, peerName: peerId, closed: false, close() { this.closed = true; } };
      opens.push({ peerId, channel, resolve: () => resolve(channel), reject });
      return p;
    },
  };
}

function fakeClient(channel) {
  return {
    channel,
    closed: false,
    on() {},
    close() { this.closed = true; },
    hello: async () => ({ protocol: 1, machine: { name: channel.peerId }, capabilities: ['sessions.read'] }),
    subscribe: async () => ({ revision: 1, count: 0 }),
  };
}

function session(presence) {
  const connection = fakeConnection(presence);
  const hosts = new HostSession({ connection, store: new SessionsStore(), storage: null, makeClient: (ch) => fakeClient(ch) });
  return { connection, hosts };
}

test('a hello that times out is retried, not taken for an old host', async () => {
  const connection = fakeConnection([{ id: 'a', kind: 'host' }]);
  const clients = [];
  const hosts = new HostSession({
    connection, store: new SessionsStore(), storage: null,
    makeClient: (ch) => {
      const c = fakeClient(ch);
      c.hello = async () => { throw Object.assign(new Error('hello: timed out'), { reason: 'timed out' }); };
      clients.push(c);
      return c;
    },
  });
  hosts.select('a');
  await flush();
  connection.opens[0].resolve();
  await flush();
  assert.equal(hosts.phase, 'failed');
  assert.equal(hosts.client, null);
  assert.equal(clients[0].closed, true);
});

test('a host that refuses hello is reported as not supporting sessions', async () => {
  const connection = fakeConnection([{ id: 'a', kind: 'host' }]);
  const hosts = new HostSession({
    connection, store: new SessionsStore(), storage: null,
    makeClient: (ch) => {
      const c = fakeClient(ch);
      c.hello = async () => { throw new HostError('hello', 'unknown op hello', { fromHost: true }); };
      return c;
    },
  });
  hosts.select('a');
  await flush();
  connection.opens[0].resolve();
  await flush();
  assert.equal(hosts.phase, 'unsupported');
  assert.match(hosts.error, /unknown op hello/);
});

test('switching machines while a handshake is pending opens the new machine', async () => {
  const { connection, hosts } = session([{ id: 'a', kind: 'host' }, { id: 'b', kind: 'host' }]);
  hosts.select('a');
  hosts.select('b');
  await flush();
  // Whether B starts at once or after A settles, A's late channel must not win.
  connection.opens[0].resolve();
  await flush();
  const forB = connection.opens.find((o) => o.peerId === 'b');
  assert.ok(forB, 'a channel to b was opened');
  forB.resolve();
  await flush();
  assert.equal(hosts.phase, 'open');
  assert.equal(hosts.client.channel.peerId, 'b');
  assert.equal(connection.opens[0].channel.closed, true, "a's channel is closed, not leaked");
});

test('going back to a machine while its first attempt is pending keeps only one channel', async () => {
  const { connection, hosts } = session([{ id: 'a', kind: 'host' }, { id: 'b', kind: 'host' }]);
  hosts.select('a');
  hosts.select('b');
  hosts.select('a');
  await flush();
  for (const o of [...connection.opens]) { o.resolve(); }
  await flush();
  for (const o of [...connection.opens]) { o.resolve(); }
  await flush();
  assert.equal(hosts.phase, 'open');
  assert.equal(hosts.client.channel.peerId, 'a');
  const open = connection.opens.filter((o) => !o.channel.closed);
  assert.equal(open.length, 1, `open channels: ${open.map((o) => o.peerId)}`);
  assert.equal(open[0].channel, hosts.client.channel);
});

test('what the machine supports is kept from hello, and forgotten when another machine is chosen', async () => {
  const connection = fakeConnection([{ id: 'a', kind: 'host' }, { id: 'b', kind: 'host' }]);
  const hosts = new HostSession({
    connection, store: new SessionsStore(), storage: null,
    makeClient: (ch) => {
      const c = fakeClient(ch);
      c.hello = async () => ({ protocol: 1, machine: { name: ch.peerId }, capabilities: ch.peerId === 'a' ? ['sessions.read', 'sessions.answer'] : ['sessions.read'] });
      return c;
    },
  });
  hosts.select('a');
  await flush();
  connection.opens[0].resolve();
  await flush();
  await flush();
  assert.equal(hosts.phase, 'open');
  assert.deepEqual(hosts.capabilities, ['sessions.read', 'sessions.answer']);
  hosts.select('b');
  assert.deepEqual(hosts.capabilities, []);
  await flush();
  connection.opens[1].resolve();
  await flush();
  await flush();
  assert.deepEqual(hosts.capabilities, ['sessions.read']);
  hosts.forget();
  assert.deepEqual(hosts.capabilities, []);
});

test('the machine being viewed is asked for its hello again, and a change in what it offers or why it is read-only is heard', async () => {
  const connection = fakeConnection([{ id: 'a', kind: 'host' }]);
  let said = { protocol: 1, machine: { name: 'a' }, capabilities: ['sessions.read', 'sessions.answer'] };
  const hosts = new HostSession({
    connection, store: new SessionsStore(), storage: null,
    makeClient: (ch) => ({ ...fakeClient(ch), hello: async () => said }),
  });
  hosts.select('a');
  await flush();
  connection.opens[0].resolve();
  await flush();
  assert.equal(hosts.phase, 'open');
  let heard = 0;
  hosts.onChange(() => heard++);

  await hosts.refresh();
  assert.equal(heard, 0);

  said = { protocol: 1, machine: { name: 'a', readOnly: 'agent-ha-bridge is running for this user' }, capabilities: ['sessions.read'] };
  await hosts.refresh();
  assert.equal(heard, 1);
  assert.equal(hosts.machine.readOnly, 'agent-ha-bridge is running for this user');
  assert.deepEqual(hosts.capabilities, ['sessions.read']);
});

// ---- every machine at once ----

const hosts = (...ids) => ids.map((id) => ({ id, name: `Machine ${id}`, kind: 'host' }));
// What each machine's snapshot holds: its id's sessions, updated at the times given.
const snapshot = (sessions) => (ch, store) => ({
  ...fakeClient(ch),
  synced: true,
  subscribe: async () => {
    for (const [id, updatedAt] of sessions[ch.peerId] ?? []) { store.upsert({ id, agent: 'copilot', status: 'idle', updatedAt, activity: {} }); }
    return { revision: 1, count: 0 };
  },
});
const memory = (saved = {}) => ({ saved, getItem: (k) => saved[k] ?? null, setItem: (k, v) => { saved[k] = v; }, removeItem: (k) => { delete saved[k]; } });

test('with nothing remembered every machine is shown, each over a channel of its own, and their sessions are one list, newest first', async () => {
  const connection = fakeConnection(hosts('a', 'b', 'me'));
  const s = new HostSession({
    connection, me: 'me', storage: memory(),
    makeClient: snapshot({ a: [['s1', '2026-10-31T10:00:00.000Z'], ['s2', '2026-10-31T12:00:00.000Z']], b: [['s1', '2026-10-31T11:00:00.000Z']] }),
  });
  s.restoreTarget(null);
  assert.equal(s.selection, ALL);
  await flush();
  assert.deepEqual(connection.opens.map((o) => o.peerId), ['a', 'b'], 'never a channel to this device');
  for (const o of connection.opens) { o.resolve(); }
  await flush();
  await flush();
  assert.deepEqual(s.links().map((l) => [l.id, l.phase]), [['a', 'open'], ['b', 'open']]);
  // The same session id on two machines is two sessions.
  assert.deepEqual(s.sessions.list().map((e) => `${e.hostId}/${e.session.id}`), ['a/s2', 'b/s1', 'a/s1']);
  assert.equal(s.sessions.size, 3);
});

test('the choice is remembered: a machine, or every machine; a link shows its machine alone only when one machine was chosen', () => {
  const connection = fakeConnection(hosts('a', 'b'));
  const storage = memory();
  const make = () => new HostSession({ connection, storage, makeClient: (ch) => fakeClient(ch) });
  const first = make();
  first.select('a');
  assert.equal(storage.saved['agent-bridge.app.lastHost'], 'a');
  const again = make();
  again.restoreTarget(null);
  assert.equal(again.selection, 'a');
  again.restoreTarget('b');
  assert.equal(again.selection, 'b', 'a link wins over one machine remembered');
  again.select(ALL);
  assert.equal(storage.saved['agent-bridge.app.lastHost'], ALL);
  const third = make();
  third.restoreTarget('b');
  assert.equal(third.selection, ALL, 'a link to one of them keeps every machine shown');
  third.forget();
  assert.equal(storage.saved['agent-bridge.app.lastHost'], undefined);
});

test('at most three handshakes are under way at once; the rest wait their turn', async () => {
  const ids = Array.from({ length: 10 }, (_, i) => `m${i}`);
  const connection = fakeConnection(hosts(...ids));
  const s = new HostSession({ connection, storage: null, makeClient: (ch) => fakeClient(ch) });
  s.select(ALL);
  await flush();
  assert.deepEqual(connection.opens.map((o) => o.peerId), ['m0', 'm1', 'm2']);
  connection.opens[1].resolve();
  await flush();
  await flush();
  assert.deepEqual(connection.opens.map((o) => o.peerId), ['m0', 'm1', 'm2', 'm3']);
  for (let i = 0; i < 20 && connection.opens.some((o) => !o.done); i++) {
    for (const o of connection.opens) { if (!o.done) { o.done = true; o.resolve(); } }
    await flush();
    await flush();
  }
  assert.equal(connection.opens.length, 10);
  assert.ok(s.links().every((l) => l.phase === 'open'));
});

test('a machine that can\'t be reached is tried again on its own, waiting longer each time, and the others stay open', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const connection = fakeConnection(hosts('a', 'b'));
  const s = new HostSession({ connection, storage: null, makeClient: (ch) => fakeClient(ch), retryMs: 5000 });
  s.select(ALL);
  await flush();
  const settle = async () => {
    for (const o of connection.opens) {
      if (o.done) { continue; }
      o.done = true;
      if (o.peerId === 'b') { o.reject(new Error('the handshake timed out')); } else { o.resolve(); }
    }
    for (let i = 0; i < 4; i++) { await flush(); }
  };
  await settle();
  assert.equal(s.link('a').phase, 'open');
  assert.deepEqual([s.link('b').phase, s.link('b').error], ['failed', 'the handshake timed out']);
  const tries = () => connection.opens.filter((o) => o.peerId === 'b').length;
  t.mock.timers.tick(4999);
  await flush();
  assert.equal(tries(), 1);
  t.mock.timers.tick(1);
  await settle();
  assert.equal(tries(), 2);
  // Twice as long the second time.
  t.mock.timers.tick(9999);
  await flush();
  assert.equal(tries(), 2);
  t.mock.timers.tick(1);
  await settle();
  assert.equal(tries(), 3);
  assert.equal(s.link('a').phase, 'open');
  assert.equal(connection.opens.filter((o) => o.peerId === 'a').length, 1);
});

test('a machine going offline leaves with its sessions when every machine is shown; one machine chosen keeps its last list', async () => {
  const connection = fakeConnection(hosts('a', 'b'));
  const s = new HostSession({ connection, storage: null, makeClient: snapshot({ a: [['s1', '2026-10-31T10:00:00.000Z']], b: [['s2', '2026-10-31T11:00:00.000Z']] }) });
  s.select(ALL);
  await flush();
  for (const o of connection.opens) { o.resolve(); }
  await flush();
  await flush();
  const gone = [];
  s.sessions.onChange((c) => gone.push(`${c.type}:${c.hostId}`));
  const b = s.link('b').client;
  connection.presence = hosts('a');
  connection.emit('presence');
  assert.deepEqual(s.sessions.list().map((e) => e.hostId), ['a']);
  assert.deepEqual(gone, ['clear:b']);
  assert.equal(b.closed, true);
  assert.equal(s.link('b'), null);

  s.select('a');
  assert.equal(s.link('a').phase, 'open', 'the chosen machine keeps the channel it had');
  connection.presence = [];
  connection.emit('presence');
  assert.deepEqual([s.phase, s.sessions.list().map((e) => e.session.id)], ['waiting', ['s1']]);
});

test('the relay dropping keeps every machine\'s sessions on screen, and each machine is opened again when it is back', async () => {
  const connection = fakeConnection(hosts('a', 'b'));
  const s = new HostSession({ connection, storage: null, makeClient: snapshot({ a: [['s1', '2026-10-31T10:00:00.000Z']], b: [['s2', '2026-10-31T11:00:00.000Z']] }) });
  s.select(ALL);
  await flush();
  for (const o of connection.opens) { o.resolve(); }
  await flush();
  await flush();
  connection.ready = false;
  connection.emit('lost');
  assert.deepEqual(s.links().map((l) => [l.phase, l.error]), [['waiting', 'The relay connection dropped; reconnecting.'], ['waiting', 'The relay connection dropped; reconnecting.']]);
  assert.equal(s.sessions.size, 2);
  const opened = [];
  s.onOpen((id) => opened.push(id));
  connection.ready = true;
  connection.emit('ready');
  await flush();
  for (const o of connection.opens.slice(2)) { o.resolve(); }
  await flush();
  await flush();
  assert.deepEqual(opened.sort(), ['a', 'b']);
  assert.ok(s.links().every((l) => l.phase === 'open'));
});

test('a notification for a machine that turns out to be offline shows that machine alone, without changing what is remembered', () => {
  const connection = fakeConnection([]);
  const storage = memory({ 'agent-bridge.app.lastHost': ALL });
  const s = new HostSession({ connection, storage, makeClient: (ch) => fakeClient(ch) });
  s.restoreTarget('gone');
  assert.equal(s.selection, ALL, 'shown with every machine until presence says otherwise');
  connection.presence = hosts('a');
  connection.emit('presence');
  assert.deepEqual([s.selection, s.phase], ['gone', 'waiting']);
  assert.equal(storage.saved['agent-bridge.app.lastHost'], ALL);

  const online = new HostSession({ connection, storage, makeClient: (ch) => fakeClient(ch) });
  online.restoreTarget('a');
  connection.emit('presence');
  assert.equal(online.selection, ALL, 'one that is online stays among the others');
});

test('a channel that closes while it is being set up is retried once, after the first wait, not twice as long', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const connection = fakeConnection(hosts('a'));
  const s = new HostSession({
    connection, storage: null, retryMs: 5000,
    makeClient: (ch) => {
      const closers = [];
      const c = { ...fakeClient(ch), on: (type, fn) => { if (type === 'close') { closers.push(fn); } } };
      c.hello = async () => {
        for (const fn of closers) { fn(new Error('closed')); }
        throw new HostError('hello', 'the channel closed');
      };
      return c;
    },
  });
  s.select('a');
  await flush();
  connection.opens[0].resolve();
  for (let i = 0; i < 4; i++) { await flush(); }
  assert.equal(s.phase, 'failed');
  t.mock.timers.tick(4999);
  await flush();
  assert.equal(connection.opens.length, 1);
  t.mock.timers.tick(1);
  await flush();
  assert.equal(connection.opens.length, 2);
});

// ---- two machines, each card's actions to its own ----

const delay = (ms) => new Promise((r) => { setTimeout(r, ms); });
async function until(check, what) {
  for (let i = 0; i < 200; i++) { if (check()) { return; } await delay(5); }
  assert.fail(`timed out waiting for ${what}`);
}

// Two demo machines, each with a session of the same id asking a question, shown together.
async function twoMachines() {
  const t = new Date().toISOString();
  const session = (title) => ({
    id: 'same-id', agent: 'copilot', title, status: 'waiting', statusSince: t, startedAt: t, updatedAt: t, processId: 1, activity: {},
    question: { id: 'q1', kind: 'form', message: 'Which?', askedAt: t, fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a' }, { value: 'b' }] }] },
  });
  const blobs = new DemoBlobStore();
  const one = new DemoHost({ sessions: [session('on one')] }, { machineId: 'one', machineName: 'One', answerDelayMs: 0 });
  const two = new DemoHost({ sessions: [session('on two')] }, { machineId: 'two', machineName: 'Two', answerDelayMs: 0 });
  for (const m of [one, two]) { m.blobStore = blobs; }
  const connection = new DemoConnection([one, two]);
  const s = new HostSession({ connection, storage: null });
  s.select(ALL);
  await until(() => s.links().every((l) => l.phase === 'open' && l.client.synced), 'both machines to open');
  const current = (hostId) => s.route(hostId);
  const services = { answers: new AnswerService({ current, retryMs: 10 }), replies: new ReplyService({ current, retryMs: 10, blobs: () => blobs }), stops: new StopService({ current, retryMs: 10 }), launches: new LaunchService({ current, retryMs: 10 }) };
  s.onOpen((id) => { for (const x of Object.values(services)) { x.ready(id); } });
  return { one, two, blobs, connection, hosts: s, ...services };
}

test('with two machines shown, an answer goes to the machine its card is on, never to a session of the same id on the other', async () => {
  const { one, two, answers } = await twoMachines();
  answers.submit({ hostId: 'two', sessionId: 'same-id', questionId: 'q1', answer: { fields: { answer: 'b' } } });
  await until(() => answers.get('two', 'same-id', 'q1')?.state === 'done', 'the answer');
  assert.equal(answers.get('two', 'same-id', 'q1').outcome, 'delivered');
  assert.deepEqual(two.typed.map((x) => x.said), ['answer: "b"']);
  assert.deepEqual([one.answerLog, one.typed], [[], []]);
});

test('with two machines shown, a reply and its files, the large one through the blob store, go to the machine its card is on', async () => {
  const { one, two, blobs, replies } = await twoMachines();
  two.upsert({ ...two.get('same-id'), question: null, status: 'idle' });
  const bytes = (n) => Uint8Array.from({ length: n }, (_, i) => (i * 7) & 0xff);
  const named = (b, name, type) => Object.assign(new Blob([b], { type }), { name });
  const small = await prepareAttachment(named(bytes(1000), 'notes.txt', 'text/plain'), { largeMax: 100 * 1024 * 1024 });
  const large = await prepareAttachment(named(bytes(400000), 'photo.png', 'image/png'), { largeMax: 100 * 1024 * 1024 });
  replies.submit({ hostId: 'two', sessionId: 'same-id', text: 'see these', attachments: [small, large] });
  await until(() => replies.get('two', 'same-id')?.state === 'done', 'the reply');
  assert.equal(replies.get('two', 'same-id').outcome, 'delivered');
  assert.deepEqual(blobs.log.filter((l) => l.op === 'put').map((l) => l.to), ['two']);
  assert.deepEqual(two.typedReplies.map((r) => r.files.map((f) => f.name)), [['notes.txt', 'photo.png']]);
  assert.deepEqual([one.replyLog, one.attachmentLog], [[], []]);
});

test('with two machines shown, End session and a launch go to the machine they are pressed for', async () => {
  const { one, two, stops, launches } = await twoMachines();
  stops.submit({ hostId: 'one', sessionId: 'same-id' });
  await until(() => stops.get('one', 'same-id')?.state === 'done', 'the stop');
  assert.equal(stops.get('one', 'same-id').outcome, 'confirm', 'a session waiting on you takes a second press');
  assert.equal(one.stopLog.length, 1);
  launches.submit({ hostId: 'two', launch: { agent: 'copilot', workspace: 'notes', permissions: 'ask' } });
  await until(() => launches.get('two')?.state === 'done', 'the launch');
  assert.equal(launches.get('two').outcome, 'started');
  assert.deepEqual([one.launchLog.length, two.launchLog.length, two.stopLog.length], [0, 1, 0]);
});

test('an answer for a machine that is away waits for that machine, and goes to it when it is back while the other goes on', async () => {
  const { one, two, connection, answers } = await twoMachines();
  connection.setOnline('two', false);
  answers.submit({ hostId: 'two', sessionId: 'same-id', questionId: 'q1', answer: { fields: { answer: 'a' } } });
  answers.submit({ hostId: 'one', sessionId: 'same-id', questionId: 'q1', answer: { fields: { answer: 'b' } } });
  await until(() => answers.get('one', 'same-id', 'q1')?.state === 'done', 'the answer to one');
  assert.equal(answers.get('two', 'same-id', 'q1').state, 'waiting');
  assert.deepEqual(two.answerLog, []);
  connection.setOnline('two', true);
  await until(() => answers.get('two', 'same-id', 'q1')?.state === 'done', 'the answer to two');
  assert.deepEqual([one.answerLog.length, two.answerLog.length], [1, 1]);
  assert.deepEqual(two.typed.map((x) => x.said), ['answer: "a"']);
});
