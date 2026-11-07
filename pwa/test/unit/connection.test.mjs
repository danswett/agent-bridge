import { test } from 'node:test';
import assert from 'node:assert/strict';
import { BridgeConnection, PROBE_AFTER_MS, PROBE_TIMEOUT_MS, QUIET_AFTER_MS } from '../../js/connection.js';
import { CONNECTION_CHANGED } from '../../js/channels.js';
import { AnswerService } from '../../js/answer-service.js';

const flush = async () => { for (let i = 0; i < 5; i++) { await Promise.resolve(); } };

// A page whose visibility the test turns, and relays that connect at once and record closing.
function page() {
  const listeners = [];
  const doc = {
    visibilityState: 'visible',
    addEventListener(type, fn) { if (type === 'visibilitychange') { listeners.push(fn); } },
    removeEventListener(type, fn) { const i = listeners.indexOf(fn); if (i >= 0) { listeners.splice(i, 1); } },
  };
  const win = { document: doc, addEventListener() {}, removeEventListener() {} };
  const show = (visible) => { doc.visibilityState = visible ? 'visible' : 'hidden'; for (const fn of [...listeners]) { fn(); } };
  return { win, show };
}

function connected({ busy = () => false } = {}) {
  const { win, show } = page();
  const relays = [];
  const makeRelay = () => {
    const r = {
      open: false,
      closed: null,
      onClose: null,
      async connect() { this.open = true; return this; },
      close(code, reason) { this.open = false; this.closed = { code, reason }; this.onClose?.({ code, reason }); },
    };
    relays.push(r);
    return r;
  };
  const conn = new BridgeConnection({ base: 'http://relay.invalid', key: {}, deviceId: 'me', name: 'phone', account: { token: 't' }, win, busy, makeRelay });
  const lost = [];
  conn.on('lost', (e) => lost.push(e));
  return { conn, relays, show, lost };
}

test('out of sight for 15 s, the app closes its relay connection, stays closed, and connects again at once when back', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { conn, relays, show, lost } = connected();
  await conn.start();
  assert.equal(conn.state, 'connected');

  show(false);
  t.mock.timers.tick(QUIET_AFTER_MS - 1);
  assert.equal(relays[0].closed, null, 'not before 15 s');
  t.mock.timers.tick(1);
  await flush();
  assert.equal(conn.state, 'resting');
  assert.deepEqual(relays[0].closed, { code: 1000, reason: 'resting' });
  assert.equal(conn.ready, false);
  assert.deepEqual(lost.map((e) => e.resting), [true], 'every channel goes, as on a drop, said to be a rest');

  // No reconnecting while out of sight, however long.
  t.mock.timers.tick(10 * 60 * 1000);
  await flush();
  assert.equal(relays.length, 1);

  show(true);
  await flush();
  assert.equal(relays.length, 2, 'connected again at once, with no backoff');
  assert.equal(conn.state, 'connected');
  conn.stop();
});

test('a rest is said before its channels go, so the machines shown still know which of them were current', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { conn, show } = connected();
  await conn.start();
  const order = [];
  const attach = conn.channels.attach.bind(conn.channels);
  conn.channels.attach = (r) => { order.push(r ? 'attached' : 'detached'); return attach(r); };
  conn.on('lost', (e) => order.push(e.resting ? 'rest' : 'drop'));

  show(false);
  t.mock.timers.tick(QUIET_AFTER_MS);
  await flush();

  assert.deepEqual(order, ['rest', 'detached']);
  conn.stop();
});

test('a quick look at another app keeps the connection as it was', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { conn, relays, show } = connected();
  await conn.start();
  show(false);
  t.mock.timers.tick(QUIET_AFTER_MS - 5000);
  show(true);
  t.mock.timers.tick(QUIET_AFTER_MS);
  await flush();
  assert.equal(relays.length, 1);
  assert.equal(relays[0].closed, null);
  assert.equal(conn.state, 'connected');
  conn.stop();
});

test('with an answer or reply on its way, the connection stays until it is done, then rests', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  let sending = true;
  const { conn, relays, show } = connected({ busy: () => sending });
  await conn.start();
  show(false);
  t.mock.timers.tick(QUIET_AFTER_MS);
  await flush();
  assert.equal(relays[0].closed, null, 'not while something is on its way');
  assert.equal(conn.state, 'connected');

  sending = false;
  t.mock.timers.tick(QUIET_AFTER_MS);
  await flush();
  assert.equal(conn.state, 'resting');
  assert.notEqual(relays[0].closed, null);
  conn.stop();
});

test('put away while the relay is still admitting it, the attempt is let go, and coming back connects afresh', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { win, show } = page();
  const relays = [];
  const makeRelay = () => {
    const first = relays.length === 0;
    const r = {
      open: false,
      closed: null,
      onClose: null,
      // The first is never admitted; the next is at once.
      connect() { return first ? new Promise(() => {}) : Promise.resolve().then(() => { this.open = true; return this; }); },
      close(code, reason) { this.open = false; this.closed = { code, reason }; this.onClose?.({ code, reason }); },
    };
    relays.push(r);
    return r;
  };
  const conn = new BridgeConnection({ base: 'http://relay.invalid', key: {}, deviceId: 'me', name: 'phone', account: { token: 't' }, win, busy: () => false, makeRelay });
  conn.start();
  await flush();
  assert.equal(conn.state, 'connecting');

  show(false);
  t.mock.timers.tick(QUIET_AFTER_MS);
  await flush();
  assert.equal(conn.state, 'resting');
  assert.deepEqual(relays[0].closed, { code: 1000, reason: 'resting' });

  show(true);
  await flush();
  assert.equal(relays.length, 2, 'a new attempt, not the one left waiting');
  assert.equal(conn.state, 'connected');
  conn.stop();
});

// Relays that can be probed (#1128): each probe is noted, and answers only when the test says,
// or says no once its deadline passes.
function probed() {
  const { win } = page();
  const relays = [];
  const makeRelay = () => {
    const r = {
      open: false,
      closed: null,
      onClose: null,
      onReceive: null,
      lastReceivedAt: 0,
      probes: [],
      async connect() { this.open = true; return this; },
      close(code, reason) { this.open = false; this.closed = { code, reason }; this.onClose?.({ code, reason }); },
      probe(ms) {
        return new Promise((resolve) => {
          const timer = setTimeout(() => resolve(false), ms);
          this.probes.push({ ms, answer: () => { clearTimeout(timer); resolve(true); } });
        });
      },
    };
    relays.push(r);
    return r;
  };
  const conn = new BridgeConnection({ base: 'http://relay.invalid', key: {}, deviceId: 'me', name: 'phone', account: { token: 't' }, win, makeRelay });
  const logs = [];
  conn.on('log', (line) => logs.push(line));
  return { conn, relays, logs };
}

test('a message unanswered for 5 s has the relay probed, and an answer keeps the connection', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { conn, relays } = probed();
  await conn.start();

  conn.channels.onSent();
  t.mock.timers.tick(PROBE_AFTER_MS - 1);
  assert.equal(relays[0].probes.length, 0, 'not before 5 s');
  t.mock.timers.tick(1);
  await flush();
  assert.deepEqual(relays[0].probes.map((p) => p.ms), [PROBE_TIMEOUT_MS]);

  relays[0].probes[0].answer();
  await flush();
  t.mock.timers.tick(60000);
  await flush();
  assert.equal(relays.length, 1);
  assert.equal(relays[0].closed, null);
  assert.equal(conn.state, 'connected');
  conn.stop();
});

test('a probe unanswered within its deadline gives the socket up and connects again at once, saying why', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { conn, relays, logs } = probed();
  await conn.start();

  conn.channels.onSent();
  t.mock.timers.tick(PROBE_AFTER_MS);
  await flush();
  t.mock.timers.tick(PROBE_TIMEOUT_MS);
  await flush();
  assert.deepEqual(relays[0].closed, { code: 1000, reason: 'unresponsive' });
  assert.equal(conn.state, 'reconnecting');
  assert.ok(logs.some((l) => l.startsWith('relay connection lost (1000 unresponsive), ')), logs.join('\n'));

  t.mock.timers.tick(1);
  await flush();
  assert.equal(relays.length, 2, 'with no backoff');
  assert.equal(conn.state, 'connected');
  conn.stop();
});

test('a message answered within 5 s has nothing probed', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { conn, relays } = probed();
  await conn.start();

  conn.channels.onSent();
  t.mock.timers.tick(1000);
  relays[0].onReceive();
  t.mock.timers.tick(60000);
  await flush();
  assert.equal(relays[0].probes.length, 0);
  assert.equal(relays[0].closed, null);
  conn.stop();
});

test('an answer on a socket given up for silence goes again once, with its same request, on the next connection', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { conn, relays } = probed();
  await conn.start();
  const sends = [];
  let client = null;
  // A channel to the machine per connection: the first never hears back, the next answers at once.
  const open = (answers) => {
    const c = {
      closed: false,
      reject: null,
      answer(data) {
        sends.push({ client: c, request: data.request });
        conn.channels.onSent();
        return answers ? Promise.resolve({ outcome: 'delivered', reason: null }) : new Promise((_, reject) => { c.reject = reject; });
      },
    };
    client = c;
    return c;
  };
  let n = 0;
  const service = new AnswerService({ current: () => (client ? { client, hostId: 'h' } : null), uuid: () => `req-${++n}` });
  open(false);
  conn.on('lost', () => {
    client.closed = true;
    client.reject?.(Object.assign(new Error(`session.answer: ${CONNECTION_CHANGED}`), { reason: CONNECTION_CHANGED }));
  });
  conn.on('ready', () => { open(true); service.ready('h'); });

  service.submit({ hostId: 'h', sessionId: 's', questionId: 'q', answer: { text: 'yes' } });
  await flush();
  t.mock.timers.tick(PROBE_AFTER_MS);
  await flush();
  t.mock.timers.tick(PROBE_TIMEOUT_MS);
  await flush();
  t.mock.timers.tick(1);
  await flush();
  relays[1].onReceive();
  t.mock.timers.tick(10 * 60 * 1000);
  await flush();

  assert.equal(relays.length, 2);
  assert.equal(sends.length, 2, 'once on each connection, never twice on either');
  assert.notEqual(sends[0].client, sends[1].client);
  assert.equal(sends[0].request, sends[1].request, 'the same request, which the machine carries out once');
  assert.equal(service.get('h', 's', 'q').outcome, 'delivered');
  conn.stop();
});