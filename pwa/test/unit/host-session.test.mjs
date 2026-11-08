import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ALL, HostSession, OFFLINE_KEEP_MS } from '../../js/host-session.js';
import { HostClient, HostError } from '../../js/host-client.js';
import { SessionsStore } from '../../js/sessions-store.js';
import { AnswerService } from '../../js/answer-service.js';
import { ReplyService } from '../../js/reply-service.js';
import { StopService } from '../../js/stop-service.js';
import { LaunchService } from '../../js/launch-service.js';
import { prepareAttachment } from '../../js/attachments.js';
import { DemoBlobStore, DemoConnection, DemoHost } from '../../js/demo.js';
import { ChannelSet, KIND } from '../../js/channels.js';
import { P256KeyPair } from '../../js/noise-p256.js';
import { BridgeConnection, QUIET_AFTER_MS } from '../../js/connection.js';

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
    openChannel(peerId, { signal = null, early = null } = {}) {
      let resolve, reject;
      const p = new Promise((res, rej) => { resolve = res; reject = rej; });
      const channel = { peerId, peerName: peerId, closed: false, close() { this.closed = true; } };
      // As ChannelSet does: a handshake no longer wanted is closed, and its open fails.
      signal?.addEventListener('abort', () => { channel.close(); reject(new Error('no longer wanted')); });
      opens.push({ peerId, channel, signal, early, resolve: () => resolve(channel), reject });
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

test("a shown machine's channel asks hello early with the data its first hello sends, so the handshake's answer is the one used (#603)", async () => {
  const connection = fakeConnection([{ id: 'a', kind: 'host' }]);
  const asked = [];
  const hosts = new HostSession({
    connection, store: new SessionsStore(), storage: null,
    makeClient: (ch) => {
      const c = fakeClient(ch);
      c.hello = async (data) => { asked.push(data); return { protocol: 1, machine: { name: 'a' }, capabilities: ['sessions.read'] }; };
      return c;
    },
  });
  hosts.select('a');
  await flush();
  connection.opens[0].resolve();
  await flush();
  assert.equal(connection.opens[0].early.op, 'hello');
  assert.deepEqual(asked[0], connection.opens[0].early.data);
});

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
  assert.deepEqual([hosts.route('a'), hosts.staleOf('a')], [null, { reason: 'unsupported', since: null }], 'held as unsupported, never as reconnecting');
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

test('a machine that leaves presence keeps its sessions on screen, offline and stale, beside the other machine\'s, and so does one machine chosen', async () => {
  const connection = fakeConnection(hosts('a', 'b'));
  const s = new HostSession({ connection, storage: null, makeClient: snapshot({ a: [['s1', '2026-10-31T10:00:00.000Z']], b: [['s2', '2026-10-31T11:00:00.000Z']] }) });
  s.select(ALL);
  await flush();
  for (const o of connection.opens) { o.resolve(); }
  await flush();
  await flush();
  const changes = [];
  s.sessions.onChange((c) => changes.push(`${c.type}:${c.hostId}`));
  const b = s.link('b').client;
  connection.presence = hosts('a');
  connection.emit('presence');
  assert.deepEqual(s.sessions.list().map((e) => `${e.hostId}/${e.session.id}`), ['b/s2', 'a/s1']);
  assert.deepEqual(changes, [], 'nothing cleared, nothing removed');
  assert.equal(b.closed, true, 'its channel went with it');
  assert.deepEqual([s.link('b').phase, s.shownIds(), s.away()], ['waiting', ['a', 'b'], ['b']]);
  assert.equal(s.staleOf('b').reason, 'offline');
  assert.equal(s.staleOf('a'), null, 'the other machine is untouched');
  assert.equal(s.route('b'), null);
  assert.ok(s.route('a'));

  s.select('a');
  assert.equal(s.link('a').phase, 'open', 'the chosen machine keeps the channel it had');
  connection.presence = [];
  connection.emit('presence');
  assert.deepEqual([s.phase, s.sessions.list().map((e) => e.session.id), s.staleOf('a')?.reason], ['waiting', ['s1'], 'offline']);
});

// A client whose snapshot the test finishes by hand: subscribe() answers at once, and synced()
// sends sessions.synced with the sessions given, as HostClient does.
function heldSnapshot(clients) {
  return (ch, store) => {
    const listeners = new Map();
    const c = {
      ...fakeClient(ch),
      synced: false,
      on(type, fn) {
        if (!listeners.has(type)) { listeners.set(type, []); }
        listeners.get(type).push(fn);
      },
      subscribe: async () => { c.synced = false; return { revision: 1, count: 0 }; },
      snapshot(sessions) {
        for (const [id, updatedAt, question = null] of sessions) { store.upsert({ id, agent: 'copilot', status: question ? 'waiting' : 'idle', updatedAt, activity: {}, question }); }
        c.synced = true;
        store.retain(new Set(sessions.map(([id]) => id)));
        for (const fn of listeners.get('synced') ?? []) { fn({}); }
      },
    };
    clients.push(c);
    return c;
  };
}

test('a machine back from offline takes nothing until its first whole snapshot, which ends what ended meanwhile without duplicating the rest', async () => {
  const connection = fakeConnection(hosts('a', 'b'));
  const clients = [];
  const s = new HostSession({ connection, storage: null, makeClient: heldSnapshot(clients) });
  const opened = [];
  s.onOpen((id) => opened.push(id));
  s.select(ALL);
  await flush();
  for (const o of connection.opens) { o.resolve(); }
  await flush();
  await flush();
  const q = { id: 'q1', kind: 'form', message: 'Which?', fields: [] };
  assert.deepEqual([opened, s.route('a'), s.staleOf('a')?.reason], [[], null, 'confirming'], 'even a first channel waits for its snapshot');
  clients.find((c) => c.channel.peerId === 'a').snapshot([]);
  clients.find((c) => c.channel.peerId === 'b').snapshot([['kept', '2026-10-31T10:00:00.000Z'], ['ends', '2026-10-31T11:00:00.000Z', q]]);
  assert.deepEqual(opened, ['a', 'b']);

  connection.presence = hosts('a');
  connection.emit('presence');
  connection.presence = hosts('a', 'b');
  connection.emit('presence');
  await flush();
  connection.opens.at(-1).resolve();
  await flush();
  await flush();
  assert.equal(s.link('b').phase, 'open');
  assert.deepEqual(s.staleOf('b'), { reason: 'confirming', since: null });
  assert.equal(s.route('b'), null, 'open, but its snapshot has not finished');
  assert.deepEqual(opened, ['a', 'b'], 'nothing waiting is resent before the snapshot');
  assert.ok(s.sessions.get('b', 'ends')?.question, 'the question it showed before is still drawn, stale');

  const ended = [];
  s.sessions.onChange((c) => { if (c.type === 'synced') { ended.push(...c.removed.map((x) => x.id)); } });
  clients.at(-1).snapshot([['kept', '2026-10-31T12:00:00.000Z']]);
  assert.deepEqual(ended, ['ends']);
  assert.deepEqual(s.sessions.list().map((e) => `${e.hostId}/${e.session.id}`).sort(), ['b/kept']);
  assert.equal(s.staleOf('b'), null);
  assert.equal(s.route('b').client, clients.at(-1));
  assert.deepEqual(opened, ['a', 'b', 'b']);
});

// The real HostClient over a transport playing a host that answers hello, sessions.subscribe and
// session.answer at once; the test sends its events. subscribeOk(n): whether a channel's nth
// sessions.subscribe is taken.
function playedHost(transports, { subscribeOk = () => true } = {}) {
  return (ch, store) => {
    let subscribes = 0;
    const t = {
      sent: [],
      onmessage: null,
      onclose: null,
      close() { ch.close(); },
      send(m) {
        t.sent.push(m);
        const reply = (r) => setImmediate(() => t.onmessage?.(r));
        if (m.op === 'hello') { reply({ re: m.id, ok: true, result: { protocol: 1, machine: { name: ch.peerId }, capabilities: ['sessions.read', 'sessions.answer'] } }); }
        if (m.op === 'sessions.subscribe') {
          reply(subscribeOk(++subscribes) ? { re: m.id, ok: true, result: { revision: 1, count: 1 } } : { re: m.id, ok: false, error: 'not now' });
        }
        if (m.op === 'session.answer') { reply({ re: m.id, ok: true, result: { outcome: 'delivered' } }); }
        return Promise.resolve();
      },
      host(m) { t.onmessage(m); },
      ops(op) { return t.sent.filter((m) => m.op === op); },
    };
    transports.push(t);
    return new HostClient(t, { store });
  };
}

const asking = (id) => ({ id, agent: 'copilot', status: 'waiting', updatedAt: '2026-11-03T09:00:00.000Z', activity: {}, question: { id: 'q1', kind: 'form', message: 'Which?', fields: [] } });

async function playedMachine(options = {}) {
  const connection = fakeConnection(hosts('a'));
  const transports = [];
  const s = new HostSession({ connection, storage: null, makeClient: playedHost(transports, options), reopenMs: 5 });
  const opened = [];
  s.onOpen((id) => opened.push(id));
  s.select('a');
  await flush();
  connection.opens[0].resolve();
  for (let i = 0; i < 4; i++) { await flush(); }
  const t = transports[0];
  t.host({ ev: 'session.upsert', seq: 1, data: asking('s1') });
  t.host({ ev: 'session.upsert', seq: 2, data: asking('s2') });
  // whole: false leaves the first snapshot under way.
  if (options.whole !== false) { t.host({ ev: 'sessions.synced', seq: 3, data: { revision: 1 } }); }
  return { connection, transports, s, opened, t };
}

test('after a gap in a machine\'s events nothing is sent to it, an answer included, until the snapshot it asks for again is whole', async () => {
  const { s, opened, t } = await playedMachine();
  const answers = new AnswerService({ current: (h) => s.route(h), accepts: (h) => !!s.route(h), retryMs: 10 });
  s.onOpen((id) => answers.ready(id));
  assert.ok(s.route('a'), 'routed once its first snapshot is whole');
  assert.deepEqual(opened, ['a']);

  t.host({ ev: 'session.upsert', seq: 5, data: asking('s1') });
  assert.equal(s.route('a'), null, 'held from the gap on');
  assert.deepEqual(s.staleOf('a'), { reason: 'confirming', since: null });
  assert.equal(answers.submit({ hostId: 'a', sessionId: 's1', questionId: 'q1', answer: { text: 'yes' } }), null);
  await flush();
  assert.equal(t.ops('sessions.subscribe').length, 2, 'subscribed again on the same channel');
  t.host({ ev: 'session.upsert', seq: 6, data: asking('s1') });
  await flush();
  assert.equal(s.route('a'), null, 'half a snapshot confirms nothing');
  assert.deepEqual([t.ops('session.answer'), opened], [[], ['a']], 'nothing sent, and nothing waiting resent, before the snapshot is whole');

  t.host({ ev: 'sessions.synced', seq: 7, data: { revision: 2 } });
  assert.equal(s.staleOf('a'), null);
  assert.deepEqual(opened, ['a', 'a'], 'what waits to be resent goes once it is whole');
  assert.ok(s.route('a'), 'routed again');
  assert.equal(answers.submit({ hostId: 'a', sessionId: 's1', questionId: 'q1', answer: { text: 'yes' } }).state, 'sending');
  await until(() => answers.get('a', 's1', 'q1')?.state === 'done', 'the answer');
  assert.equal(t.ops('session.answer').length, 1);
});

test('a recovery snapshot that loses an event before its sessions.synced confirms nothing, resends nothing, drops nothing, and is asked for again', async () => {
  const { s, opened, t } = await playedMachine();
  t.host({ ev: 'session.upsert', seq: 5, data: asking('s1') });
  await flush();
  await flush();
  t.host({ ev: 'session.upsert', seq: 6, data: asking('s1') });
  // seq 7, s2's upsert, is lost.
  t.host({ ev: 'sessions.synced', seq: 8, data: { revision: 2 } });
  assert.deepEqual([s.route('a'), s.staleOf('a')?.reason, opened], [null, 'confirming', ['a']], 'held, and nothing waiting resent');
  assert.ok(s.sessions.get('a', 's2'), 'not taken for ended by half a snapshot');
  await flush();
  await flush();
  assert.equal(t.ops('sessions.subscribe').length, 3, 'asked for again');
  t.host({ ev: 'session.upsert', seq: 9, data: asking('s1') });
  t.host({ ev: 'session.upsert', seq: 10, data: asking('s2') });
  t.host({ ev: 'sessions.synced', seq: 11, data: { revision: 3 } });
  assert.ok(s.route('a'));
  assert.deepEqual(opened, ['a', 'a']);
});

test('a recovery snapshot that loses its first upsert is not taken as whole: nothing pruned, nothing resent, and it is asked for again', async () => {
  const { s, opened, t } = await playedMachine();
  t.host({ ev: 'session.upsert', seq: 5, data: asking('s1') });
  await flush();
  await flush();
  // seq 6, the new snapshot's first upsert (s1's), is lost; s2's and the end arrive.
  t.host({ ev: 'session.upsert', seq: 7, data: asking('s2') });
  t.host({ ev: 'sessions.synced', seq: 8, data: { revision: 2 } });
  assert.deepEqual([s.route('a'), opened], [null, ['a']]);
  assert.ok(s.sessions.get('a', 's1'), 'not pruned by a snapshot missing its upsert');
  await flush();
  await flush();
  assert.equal(t.ops('sessions.subscribe').length, 3);
});

test('a sessions.synced from the subscription before, still on its way when a gap asked again, does not end the new snapshot', async () => {
  const { s, opened, t } = await playedMachine({ whole: false });
  t.host({ ev: 'session.upsert', seq: 4, data: asking('s2') });
  // The first snapshot's end, sent before the host had the new subscribe: it answers that only
  // after stopping the first.
  t.host({ ev: 'sessions.synced', seq: 5, data: { revision: 1 } });
  assert.deepEqual([s.route('a'), opened], [null, []]);
  await flush();
  await flush();
  t.host({ ev: 'session.upsert', seq: 6, data: asking('s1') });
  assert.equal(s.route('a'), null, 'the new snapshot is under way');
  t.host({ ev: 'session.upsert', seq: 7, data: asking('s2') });
  t.host({ ev: 'sessions.synced', seq: 8, data: { revision: 2 } });
  assert.ok(s.route('a'));
  assert.deepEqual(opened, ['a']);
  assert.deepEqual(s.sessions.list().map((e) => e.session.id).sort(), ['s1', 's2']);
});

test('a gap whose resubscription the machine refuses closes the channel and opens a new one, rather than holding the machine for good', async () => {
  const { connection, transports, s, t } = await playedMachine({ subscribeOk: (n) => n === 1 });
  t.host({ ev: 'session.upsert', seq: 5, data: asking('s1') });
  assert.equal(s.route('a'), null);
  for (let i = 0; i < 4; i++) { await flush(); }
  assert.equal(connection.opens[0].channel.closed, true);
  await until(() => connection.opens.length === 2, 'a new channel');
  connection.opens[1].resolve();
  for (let i = 0; i < 4; i++) { await flush(); }
  transports[1].host({ ev: 'session.upsert', seq: 1, data: asking('s1') });
  transports[1].host({ ev: 'sessions.synced', seq: 2, data: { revision: 2 } });
  assert.equal(s.route('a')?.client.closed, false, 'routed on the new channel once its snapshot is whole');
});

test('an answer, reply, stop or launch is refused unsent for a machine that takes nothing now', () => {
  let stale = true;
  const sent = [];
  const client = { closed: false, answer: async (d) => { sent.push(d); return { outcome: 'delivered' }; } };
  const services = [
    new AnswerService({ current: () => ({ client, hostId: 'b' }), accepts: () => !stale }),
    new ReplyService({ current: () => ({ client, hostId: 'b' }), accepts: () => !stale }),
    new StopService({ current: () => ({ client, hostId: 'b' }), accepts: () => !stale }),
    new LaunchService({ current: () => ({ client, hostId: 'b' }), accepts: () => !stale }),
  ];
  assert.equal(services[0].submit({ hostId: 'b', sessionId: 's', questionId: 'q', answer: { text: 'yes' } }), null);
  assert.equal(services[1].submit({ hostId: 'b', sessionId: 's', text: 'hi' }), null);
  assert.equal(services[2].submit({ hostId: 'b', sessionId: 's' }), null);
  assert.equal(services[3].submit({ hostId: 'b', launch: { agent: 'copilot' } }), null);
  assert.deepEqual([services[0].get('b', 's', 'q'), sent], [null, []]);
  stale = false;
  assert.equal(services[0].submit({ hostId: 'b', sessionId: 's', questionId: 'q', answer: { text: 'yes' } }).state, 'sending');
});

test('an offline machine\'s sessions go after fifteen minutes; the one machine chosen stays chosen, empty', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const connection = fakeConnection(hosts('a', 'b'));
  const s = new HostSession({ connection, storage: null, makeClient: snapshot({ a: [['s1', '2026-10-31T10:00:00.000Z']], b: [['s2', '2026-10-31T11:00:00.000Z']] }) });
  s.select(ALL);
  await flush();
  for (const o of connection.opens) { o.resolve(); }
  await flush();
  await flush();
  connection.presence = hosts('a');
  connection.emit('presence');
  t.mock.timers.tick(15 * 60 * 1000 - 1);
  assert.equal(s.sessions.size, 2, 'kept up to the bound');
  t.mock.timers.tick(1);
  assert.deepEqual([s.sessions.list().map((e) => e.hostId), s.shownIds(), s.link('b')], [['a'], ['a'], null]);

  s.select('a');
  connection.presence = [];
  connection.emit('presence');
  t.mock.timers.tick(OFFLINE_KEEP_MS);
  assert.deepEqual([s.selection, s.sessions.size, s.phase], ['a', 0, 'waiting']);
  s.select(ALL);
  assert.deepEqual([s.shownIds(), s.link('a')], [[], null], 'gone past the bound, it is not kept with every machine');
});

test('a machine back within fifteen minutes keeps its sessions past the bound', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const connection = fakeConnection(hosts('a', 'b'));
  const s = new HostSession({ connection, storage: null, makeClient: snapshot({ a: [['s1', '2026-10-31T10:00:00.000Z']], b: [['s2', '2026-10-31T11:00:00.000Z']] }) });
  s.select(ALL);
  await flush();
  for (const o of connection.opens) { o.resolve(); }
  await flush();
  await flush();
  connection.presence = hosts('a');
  connection.emit('presence');
  t.mock.timers.tick(20000);
  connection.presence = hosts('a', 'b');
  connection.emit('presence');
  t.mock.timers.tick(OFFLINE_KEEP_MS);
  assert.ok(s.sessions.get('b', 's2'));
});

test('a forgotten machine\'s sessions go at once, and signing out takes every offline machine\'s', async () => {
  const connection = fakeConnection(hosts('a', 'b', 'c'));
  const s = new HostSession({ connection, storage: null, makeClient: snapshot({ a: [['s1', '2026-10-31T10:00:00.000Z']], b: [['s2', '2026-10-31T11:00:00.000Z']], c: [['s3', '2026-10-31T12:00:00.000Z']] }) });
  s.select(ALL);
  await flush();
  for (const o of connection.opens) { o.resolve(); }
  await flush();
  await flush();
  connection.presence = hosts('a');
  connection.emit('presence');
  s.dropMachine('a');
  assert.ok(s.link('a'), 'one online is never dropped as forgotten');
  s.dropMachine('b');
  assert.deepEqual([s.link('b'), s.sessions.list().map((e) => e.hostId).sort()], [null, ['a', 'c']]);
  s.forget();
  assert.deepEqual([s.sessions.size, s.link('c')], [0, null]);
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

test('a machine no longer shown gives up its handshake and its place among those under way at once', async () => {
  const connection = fakeConnection(hosts('m0', 'm1', 'm2', 'm3'));
  const s = new HostSession({ connection, storage: null, makeClient: (ch) => fakeClient(ch) });
  s.select(ALL);
  await flush();
  assert.deepEqual(connection.opens.map((o) => o.peerId), ['m0', 'm1', 'm2']);
  s.select('m3');
  await flush();
  assert.deepEqual(connection.opens.map((o) => o.peerId), ['m0', 'm1', 'm2', 'm3'], 'not held up behind handshakes nobody wants');
  assert.deepEqual(connection.opens.slice(0, 3).map((o) => o.signal.aborted), [true, true, true]);
  connection.opens[3].resolve();
  await flush();
  await flush();
  assert.equal(s.phase, 'open');
});

test('a machine found not to support sessions is tried again once its channel closes, as it may have been updated', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const connection = fakeConnection(hosts('a'));
  let supports = false;
  const closers = [];
  const s = new HostSession({
    connection, storage: null, reopenMs: 1000,
    makeClient: (ch) => ({
      ...fakeClient(ch),
      on: (type, fn) => { if (type === 'close') { closers.push(fn); } },
      hello: async () => ({ protocol: 1, machine: { name: 'a' }, capabilities: supports ? ['sessions.read'] : [] }),
    }),
  });
  s.select('a');
  await flush();
  connection.opens[0].resolve();
  for (let i = 0; i < 4; i++) { await flush(); }
  assert.equal(s.phase, 'unsupported');
  supports = true;
  closers[0](new Error('the host closed the channel'));
  assert.equal(s.phase, 'waiting');
  t.mock.timers.tick(1000);
  await flush();
  connection.opens[1].resolve();
  for (let i = 0; i < 4; i++) { await flush(); }
  assert.equal(s.phase, 'open');
});

test('a notification shows its machine without changing what was picked in the Machines panel', () => {
  const connection = fakeConnection(hosts('a', 'b'));
  const storage = memory();
  const s = new HostSession({ connection, storage, makeClient: (ch) => fakeClient(ch) });
  s.select(ALL);
  s.visit('gone');
  assert.equal(s.selection, 'gone');
  assert.equal(storage.saved['agent-bridge.app.lastHost'], ALL);
});

test('a handshake given up tells the host at once and fails, rather than waiting out its timeout', async () => {
  const sent = [];
  const set = new ChannelSet({ key: await P256KeyPair.generate(), deviceId: 'me', name: 'phone' });
  set.attach({ open: true, send: (peer, payload) => sent.push([peer, payload[0]]) });
  const abort = new AbortController();
  const opening = set.open('host-a', { signal: abort.signal, timeoutMs: 60000 });
  await assert.doesNotReject(until(() => sent.length > 0, 'the first handshake message'));
  abort.abort();
  await assert.rejects(opening, /closed/);
  assert.deepEqual(sent, [['host-a', KIND.msg1], ['host-a', KIND.close]]);
  assert.equal(set.channels.size, 0);
});

test('picking the machine a notification is showing remembers it', () => {
  const connection = fakeConnection(hosts('a', 'b'));
  const storage = memory();
  const s = new HostSession({ connection, storage, makeClient: (ch) => fakeClient(ch) });
  s.select('a');
  s.visit('b');
  s.visit('b');
  assert.equal(storage.saved['agent-bridge.app.lastHost'], 'a', 'a second notification for it is no pick either');
  s.select('b');
  assert.equal(storage.saved['agent-bridge.app.lastHost'], 'b');
});

test('a channel that was open and closes is reopened a second later without counting as a failed handshake', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const connection = fakeConnection(hosts('a'));
  const closers = [];
  const s = new HostSession({
    connection, storage: null, retryMs: 5000, reopenMs: 1000,
    makeClient: (ch) => ({ ...fakeClient(ch), on: (type, fn) => { if (type === 'close') { closers.push(fn); } } }),
  });
  s.select('a');
  await flush();
  connection.opens[0].resolve();
  for (let i = 0; i < 4; i++) { await flush(); }
  assert.equal(s.phase, 'open');
  closers[0](new Error('the host restarted'));
  t.mock.timers.tick(1000);
  await flush();
  assert.equal(connection.opens.length, 2);
  // The host is still restarting: the first failure waits the first retry, not twice it.
  connection.opens[1].reject(new Error('the handshake timed out'));
  for (let i = 0; i < 4; i++) { await flush(); }
  t.mock.timers.tick(4999);
  await flush();
  assert.equal(connection.opens.length, 2);
  t.mock.timers.tick(1);
  await flush();
  assert.equal(connection.opens.length, 3);
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
  await until(() => s.links().every((l) => s.route(l.id)), 'both machines to open');
  const current = (hostId) => s.route(hostId);
  // As SessionsPage gives them: one gate, the route.
  const accepts = (hostId) => !!s.route(hostId);
  const services = { answers: new AnswerService({ current, accepts, retryMs: 10 }), replies: new ReplyService({ current, accepts, retryMs: 10, blobs: () => blobs }), stops: new StopService({ current, accepts, retryMs: 10 }), launches: new LaunchService({ current, accepts, retryMs: 10 }) };
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

test('nothing new is taken for a machine that is away, while the other goes on, and its answer goes once it is back', async () => {
  const { one, two, connection, hosts, answers } = await twoMachines();
  connection.setOnline('two', false);
  assert.equal(answers.submit({ hostId: 'two', sessionId: 'same-id', questionId: 'q1', answer: { fields: { answer: 'a' } } }), null, 'nothing new for a machine away');
  answers.submit({ hostId: 'one', sessionId: 'same-id', questionId: 'q1', answer: { fields: { answer: 'b' } } });
  await until(() => answers.get('one', 'same-id', 'q1')?.state === 'done', 'the answer to one');
  assert.deepEqual(two.answerLog, []);
  connection.setOnline('two', true);
  await until(() => hosts.route('two'), 'two to be back');
  answers.submit({ hostId: 'two', sessionId: 'same-id', questionId: 'q1', answer: { fields: { answer: 'a' } } });
  await until(() => answers.get('two', 'same-id', 'q1')?.state === 'done', 'the answer to two');
  assert.deepEqual([one.answerLog.length, two.answerLog.length], [1, 1]);
  assert.deepEqual(two.typed.map((x) => x.said), ['answer: "a"']);
});

test('a machine still in presence whose channel drops takes nothing new until a new channel\'s snapshot is whole, and an answer sent before the drop is resent then, typed once', async () => {
  const { two, hosts, answers, replies } = await twoMachines();
  two.holdResponses();
  answers.submit({ hostId: 'two', sessionId: 'same-id', questionId: 'q1', answer: { fields: { answer: 'a' } } });
  await until(() => two.answerLog.length === 1, 'the answer to reach two');
  two.holdSnapshot();
  two.disconnect();
  await until(() => answers.get('two', 'same-id', 'q1')?.state === 'waiting', 'the answer to wait');
  assert.equal(hosts.staleOf('two')?.reason === 'offline', false, 'still in presence');
  assert.equal(replies.submit({ hostId: 'two', sessionId: 'same-id', text: 'hi' }), null);
  await until(() => hosts.link('two').phase === 'open', 'a new channel to two');
  assert.deepEqual([hosts.route('two'), hosts.staleOf('two')?.reason, two.answerLog.length], [null, 'confirming', 1]);
  assert.equal(replies.submit({ hostId: 'two', sessionId: 'same-id', text: 'hi' }), null, 'nor before its snapshot is whole');
  two.releaseSnapshot();
  await until(() => two.answerLog.length === 2, 'the answer to be resent');
  assert.equal(two.answerLog[1].request, two.answerLog[0].request, 'the same request, so the host types it at most once');
  two.releaseResponses();
  await until(() => answers.get('two', 'same-id', 'q1')?.state === 'done', 'its outcome');
  assert.deepEqual([answers.get('two', 'same-id', 'q1').outcome, two.typed.length], ['delivered', 1]);
});

// The demo host's timings are a host's (v1-sessions.md): each copy of a request counts them from
// its own arrival, a stage reached before it came as 0.
test('the demo machine times a repeated reply from the repeat\'s own arrival', async () => {
  const t0 = new Date().toISOString();
  const host = new DemoHost({ sessions: [{ id: 's1', agent: 'copilot', title: 'T', status: 'idle', statusSince: t0, startedAt: t0, updatedAt: t0, processId: 1, activity: {} }] }, { answerDelayMs: 40 });
  const got = [];
  const channel = { deliver: (m) => got.push(m) };
  const results = () => got.filter((m) => m.ok === true).map((m) => m.result);
  const send = (id) => host.receive(channel, { id, op: 'session.reply', data: { id: 's1', request: 'r1', text: 'hi', progress: true } });
  send('1');
  await until(() => results().length === 1, 'the first outcome');
  assert.equal(results()[0].outcome, 'delivered');
  assert.ok(results()[0].timings.typed >= 30, `the first copy waited for its typing (${results()[0].timings.typed} ms)`);
  send('2');
  await until(() => results().length === 2, 'the repeat\'s outcome');
  assert.deepEqual(results()[1], { outcome: 'delivered', timings: { typed: 0, recorded: 0, confirmed: 0 } });
});

test('a machine asked to say when its hello changes is asked for it again when it says so, on the same channel', async () => {
  const connection = fakeConnection([{ id: 'a', kind: 'host' }]);
  const asked = [];
  const events = [];
  const hosts = new HostSession({
    connection, store: new SessionsStore(), storage: null,
    makeClient: (ch) => {
      const c = fakeClient(ch);
      c.on = (type, fn) => { if (type === 'event') { events.push(fn); } };
      c.hello = async (options) => {
        asked.push(options);
        return { protocol: 1, machine: { name: 'a', readOnly: asked.length > 1 ? 'agent-ha-bridge is running for this user' : null }, capabilities: ['sessions.read', 'hello.changed'] };
      };
      return c;
    },
  });
  hosts.select('a');
  await flush();
  connection.opens[0].resolve();
  await flush();
  assert.equal(hosts.phase, 'open');
  assert.deepEqual(asked, [{ changes: true }]);

  for (const fn of events) { fn({ ev: 'session.upsert', seq: 1, data: {} }); }
  await flush();
  assert.equal(asked.length, 1, 'only hello.changed asks again');
  for (const fn of events) { fn({ ev: 'hello.changed', seq: 2, data: {} }); }
  await flush();
  assert.deepEqual(asked, [{ changes: true }, { changes: true }]);
  assert.equal(hosts.link('a').machine.readOnly, 'agent-ha-bridge is running for this user');
  assert.equal(connection.opens.length, 1);
});

test('a relay connection put to rest says so on each machine shown, rather than that it dropped', async () => {
  const { connection, hosts } = session([{ id: 'a', kind: 'host' }]);
  hosts.select('a');
  await flush();
  connection.opens[0].resolve();
  await flush();
  assert.equal(hosts.phase, 'open');
  connection.emit('lost', { code: 1000, reason: 'resting', resting: true });
  assert.equal(hosts.link('a').stale, true);
  assert.equal(hosts.link('a').error, 'Paused while the app is in the background.');
});

test('a rest leaves each machine whose list was current confirming, not reconnecting, until its list is whole again, and a real drop says reconnecting', async () => {
  const connection = fakeConnection([{ id: 'a', kind: 'host' }, { id: 'b', kind: 'host' }]);
  const hosts = new HostSession({ connection, store: new SessionsStore(), storage: null, makeClient: (ch) => Object.assign(fakeClient(ch), { synced: true }) });
  hosts.select(ALL);
  await flush();
  // b's channel never opens: it is reconnecting when the app rests, and stays so.
  connection.opens.find((o) => o.peerId === 'a').resolve();
  await flush();
  assert.notEqual(hosts.route('a'), null);
  connection.emit('lost', { code: 1000, reason: 'resting', resting: true });
  assert.deepEqual(hosts.staleOf('a'), { reason: 'confirming', since: null, rested: true });
  assert.deepEqual(hosts.staleOf('b'), { reason: 'reconnecting', since: null });
  connection.emit('state', { state: 'connecting' });
  assert.equal(hosts.staleOf('a').rested, true, 'coming back is not trouble');

  connection.emit('ready');
  await flush();
  connection.opens.filter((o) => o.peerId === 'a').at(-1).resolve();
  await flush();
  assert.equal(hosts.staleOf('a'), null, 'its whole list is in');
  assert.equal(hosts.link('a').rested, false);

  connection.emit('lost', { code: 1006, reason: '' });
  assert.deepEqual(hosts.staleOf('a'), { reason: 'reconnecting', since: null });
});

// The relay connection itself, put to rest by the page going out of sight: its channels close the
// host clients at once (as ChannelSet's do), which the machines shown must not take for a drop.
test('the app put to rest through its relay connection leaves each machine whose list was current confirming, though its channels close at once', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const visibility = [];
  const doc = { visibilityState: 'visible', addEventListener(type, fn) { if (type === 'visibilitychange') { visibility.push(fn); } }, removeEventListener() {} };
  const relays = [];
  const connection = new BridgeConnection({
    base: 'http://relay.invalid', key: {}, deviceId: 'me', name: 'phone', account: { token: 't' },
    win: { document: doc, addEventListener() {}, removeEventListener() {} },
    makeRelay: () => {
      const r = { open: false, async connect() { this.open = true; return this; }, close() { this.open = false; this.onClose?.({ code: 1000, reason: 'resting' }); } };
      relays.push(r);
      return r;
    },
  });
  const channels = [];
  connection.channels = {
    attach(r) { if (!r) { for (const ch of channels.splice(0)) { ch.close(); } } },
    open(peerId) {
      const ch = { peerId, peerName: peerId, closed: false, onclose: null, close() { if (!this.closed) { this.closed = true; this.onclose?.(); } } };
      channels.push(ch);
      return Promise.resolve(ch);
    },
  };
  const hosts = new HostSession({
    connection, store: new SessionsStore(), storage: null,
    makeClient: (ch) => {
      const closing = [];
      const c = Object.assign(fakeClient(ch), { synced: true, on(type, fn) { if (type === 'close') { closing.push(fn); } }, close() { ch.close(); } });
      ch.onclose = () => { for (const fn of closing) { fn(); } };
      return c;
    },
  });
  await connection.start();
  relays[0].onPresence([{ id: 'a', kind: 'host' }], null);
  hosts.select('a');
  await flush();
  assert.notEqual(hosts.route('a'), null);

  doc.visibilityState = 'hidden';
  for (const fn of visibility) { fn(); }
  t.mock.timers.tick(QUIET_AFTER_MS);

  assert.equal(connection.state, 'resting');
  assert.equal(channels.length, 0, 'its channels went');
  assert.deepEqual(hosts.staleOf('a'), { reason: 'confirming', since: null, rested: true });
  connection.stop();
});

test('back from a rest, the relay failing to come back turns its machines to reconnecting', async () => {
  const connection = fakeConnection([{ id: 'a', kind: 'host' }]);
  const hosts = new HostSession({ connection, store: new SessionsStore(), storage: null, makeClient: (ch) => Object.assign(fakeClient(ch), { synced: true }) });
  let changes = 0;
  hosts.onChange(() => { changes++; });
  hosts.select('a');
  await flush();
  connection.opens[0].resolve();
  await flush();
  connection.emit('lost', { code: 1000, reason: 'resting', resting: true });
  assert.equal(hosts.staleOf('a').rested, true);
  const before = changes;
  connection.emit('state', { state: 'reconnecting' });
  assert.deepEqual(hosts.staleOf('a'), { reason: 'reconnecting', since: null });
  assert.equal(hosts.link('a').error, 'The relay connection dropped; reconnecting.', 'no longer blamed on the background');
  assert.ok(changes > before, 'whoever draws it hears so');
});

test('a hello that changes while the channel is still subscribing is asked for again once it is open', async () => {
  const connection = fakeConnection([{ id: 'a', kind: 'host' }]);
  const asked = [];
  let events = [];
  let subscribed;
  const hosts = new HostSession({
    connection, store: new SessionsStore(), storage: null,
    makeClient: (ch) => {
      const c = fakeClient(ch);
      c.on = (type, fn) => { if (type === 'event') { events.push(fn); } };
      c.hello = async (options) => { asked.push(options); return { protocol: 1, machine: { name: 'a' }, capabilities: ['sessions.read', 'hello.changed'] }; };
      c.subscribe = () => new Promise((resolve) => { subscribed = resolve; });
      return c;
    },
  });
  hosts.select('a');
  await flush();
  connection.opens[0].resolve();
  await flush();
  assert.equal(asked.length, 1);
  for (const fn of events) { fn({ ev: 'hello.changed', seq: 1, data: {} }); }
  await flush();
  assert.equal(asked.length, 1, 'not while it is still being set up');
  subscribed({ revision: 1, count: 0 });
  await flush();
  assert.equal(hosts.phase, 'open');
  assert.equal(asked.length, 2, 'asked once it is open: the host says so only once');
  events = [];
});

test("the page's focus (#1190) is told to a machine's channel as it is made, and to every open one when it changes, the open session only to its own machine", async () => {
  const connection = fakeConnection([{ id: 'a', kind: 'host' }, { id: 'b', kind: 'host' }]);
  const told = [];
  const refreshed = [];
  const hosts = new HostSession({
    connection, store: new SessionsStore(), storage: null,
    makeClient: (ch) => Object.assign(fakeClient(ch), {
      setFocus: ({ idle, ...f }) => told.push([ch.peerId, { ...f, idle: idle?.() }]),
      refreshFocus: () => refreshed.push(ch.peerId),
    }),
  });
  hosts.setFocus({ visible: true, hostId: 'b', id: 's1', idle: () => 4200 });
  hosts.select(ALL);
  await flush();
  connection.opens[0].resolve();
  await flush();
  assert.deepEqual(told, [['a', { visible: true, open: null, idle: 4200 }]]);
  connection.opens[1].resolve();
  await flush();
  assert.deepEqual(told.at(-1), ['b', { visible: true, open: 's1', idle: 4200 }]);
  told.length = 0;
  hosts.setFocus({ visible: false, hostId: 'a', id: 's9', idle: () => 4200 });
  assert.deepEqual(told, [['a', { visible: false, open: 's9', idle: 4200 }], ['b', { visible: false, open: null, idle: 4200 }]]);
  hosts.refreshFocus();
  assert.deepEqual(refreshed, ['a', 'b']);
});
