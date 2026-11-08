import { test } from 'node:test';
import assert from 'node:assert/strict';
import { BridgeConnection, QUIET_AFTER_MS, REPLACED_GUARD_MS, REPLACED_WORDING } from '../../js/connection.js';

const flush = async () => { for (let i = 0; i < 5; i++) { await Promise.resolve(); } };

// A page whose visibility the test turns, and relays that connect at once and record closing.
function page(extra = {}) {
  const listeners = [];
  const doc = {
    visibilityState: 'visible',
    addEventListener(type, fn) { if (type === 'visibilitychange') { listeners.push(fn); } },
    removeEventListener(type, fn) { const i = listeners.indexOf(fn); if (i >= 0) { listeners.splice(i, 1); } },
  };
  const win = { document: doc, addEventListener() {}, removeEventListener() {}, ...extra };
  const show = (visible) => { doc.visibilityState = visible ? 'visible' : 'hidden'; for (const fn of [...listeners]) { fn(); } };
  return { win, show };
}

// refuse(n): an error the nth relay (from 0) is refused with while connecting, if any.
function connected({ busy = () => false, winExtra = {}, now = undefined, refuse = () => null } = {}) {
  const { win, show } = page(winExtra);
  const relays = [];
  const makeRelay = () => {
    const n = relays.length;
    const r = {
      open: false,
      closed: null,
      onClose: null,
      async connect() { const e = refuse(n); if (e) { throw e; } this.open = true; return this; },
      close(code, reason) { this.open = false; this.closed = { code, reason }; this.onClose?.({ code, reason }); },
    };
    relays.push(r);
    return r;
  };
  const conn = new BridgeConnection({ base: 'http://relay.invalid', key: {}, deviceId: 'me', name: 'phone', account: { token: 't' }, win, busy, makeRelay, ...(now ? { now } : {}) });
  const lost = [];
  conn.on('lost', (e) => lost.push(e));
  const states = [];
  conn.on('state', ({ state }) => states.push(state));
  return { conn, relays, show, lost, states };
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

// The relay closing the open connection because another window of this device took it (4009).
const replace = (relay) => { relay.open = false; relay.onClose({ code: 4009, reason: 'replaced' }); };
const refusedAs = (code) => Object.assign(new Error(`the relay closed the connection (${code})`), { code, reason: '' });

test('replaced while in sight, the window takes the connection back once, and says nothing', async () => {
  const { conn, relays, states } = connected();
  await conn.start();
  states.length = 0;

  replace(relays[0]);
  await flush();

  assert.equal(relays.length, 2, 'one take-back');
  assert.equal(conn.state, 'connected');
  assert.ok(!states.includes('replaced'), 'no banner, no paused strip');
  conn.stop();
});

test('replaced again within a minute of the take-back, the window stops for good, in plain words, and Use here restarts it', async () => {
  const clock = { t: 5000 };
  const { conn, relays, show } = connected({ now: () => clock.t });
  await conn.start();
  replace(relays[0]);
  await flush();
  clock.t += REPLACED_GUARD_MS - 1;

  replace(relays[1]);
  await flush();

  assert.equal(conn.state, 'replaced');
  assert.equal(conn.detail, REPLACED_WORDING.browser);
  assert.equal(relays.length, 2, 'no third connection, however it is woken');
  show(false);
  show(true);
  await flush();
  assert.equal(conn.state, 'replaced');
  assert.equal(relays.length, 2);

  conn.start();
  await flush();
  assert.equal(conn.state, 'connected');
  assert.equal(relays.length, 3, 'Use here connects');
  conn.stop();
});

test('replaced a minute or more after the take-back, the window takes the connection back again', async () => {
  const clock = { t: 5000 };
  const { conn, relays, states } = connected({ now: () => clock.t });
  await conn.start();
  replace(relays[0]);
  await flush();
  clock.t += REPLACED_GUARD_MS;

  replace(relays[1]);
  await flush();

  assert.equal(relays.length, 3);
  assert.equal(conn.state, 'connected');
  assert.ok(!states.includes('replaced'));
  conn.stop();
});

test('replaced out of sight, the window stays stopped without a word, and takes the connection back once when it is next in front', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const { conn, relays, show, states } = connected();
  await conn.start();
  show(false);
  states.length = 0;

  replace(relays[0]);
  await flush();
  t.mock.timers.tick(10 * 60 * 1000);
  await flush();
  assert.equal(relays.length, 1, 'not while out of sight, however long');
  assert.ok(!states.includes('replaced'), 'nothing said');
  assert.equal(conn.ready, false);

  show(true);
  await flush();
  assert.equal(relays.length, 2, 'one take-back');
  assert.equal(conn.state, 'connected');
  assert.ok(!states.includes('replaced'));
  conn.stop();
});

test('replaced out of sight within a minute of an earlier take-back, the window stops for good when next in front', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  const clock = { t: 5000 };
  const { conn, relays, show } = connected({ now: () => clock.t });
  await conn.start();
  replace(relays[0]);
  await flush();
  show(false);
  clock.t += 1000;
  replace(relays[1]);
  await flush();
  assert.equal(relays.length, 2);

  // However long it is out of sight: it was replaced within the guard, and that is what counts.
  clock.t += 10 * REPLACED_GUARD_MS;
  show(true);
  await flush();

  assert.equal(conn.state, 'replaced');
  assert.equal(relays.length, 2, 'the guard holds across the hours out of sight');
  conn.stop();
});

test('the wording leaves out tabs for an installed app, and keeps them in a browser tab', async () => {
  const said = async (winExtra) => {
    const { conn, relays } = connected({ winExtra });
    await conn.start();
    replace(relays[0]);
    await flush();
    replace(relays[1]);
    await flush();
    const { state, detail } = conn;
    conn.stop();
    return { state, detail };
  };

  const tab = await said({ matchMedia: () => ({ matches: false }) });
  const standalone = await said({ matchMedia: (q) => ({ matches: q === '(display-mode: standalone)' }) });
  const home = await said({ navigator: { standalone: true } });

  assert.deepEqual(tab, { state: 'replaced', detail: 'This app is open in another window or tab.' });
  assert.deepEqual(standalone, { state: 'replaced', detail: 'This app is open in another window.' });
  assert.deepEqual(home, standalone);
});

test('replaced while it was still connecting is never left stuck: the first attempt takes back once, the take-back stops for good', async () => {
  const first = connected({ refuse: (n) => (n === 0 ? refusedAs(4009) : null) });
  await first.conn.start();
  assert.equal(first.relays.length, 2, 'taken back once');
  assert.equal(first.conn.state, 'connected');
  first.conn.stop();

  const again = connected({ refuse: (n) => (n === 1 ? refusedAs(4009) : null) });
  await again.conn.start();
  replace(again.relays[0]);
  await flush();
  assert.equal(again.conn.state, 'replaced', 'the take-back was itself replaced');
  assert.equal(again.relays.length, 2);

  again.conn.start();
  await flush();
  assert.equal(again.conn.state, 'connected', 'Use here is not refused');
  again.conn.stop();
});
