import { test } from 'node:test';
import assert from 'node:assert/strict';
import { BridgeConnection, QUIET_AFTER_MS } from '../../js/connection.js';

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
