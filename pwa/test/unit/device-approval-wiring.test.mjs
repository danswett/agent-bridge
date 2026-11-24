// Device approval's wiring to the relay connection (#55 D4): the account's roster mode, a pending device, and the relay's two new close codes.
// Nothing here draws anything: the join and approve screens are not built, and the gate stays shut unless the relay said observe or enforce.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { BridgeConnection } from '../../js/connection.js';
import { CLOSE_CODES } from '../../js/relay.js';
import { EnrollmentTexts, openDeviceApproval, rosterModeSource, signedOutText } from '../../js/device-approval.js';

const win = { addEventListener() {}, removeEventListener() {}, document: { visibilityState: 'visible', addEventListener() {}, removeEventListener() {} }, setTimeout, clearTimeout };

// A relay whose roster answer the test holds: rosterGet() is settled by roster(...) when it likes.
function connection({ rosterGet = true, failFirst = false, rosterThrows = false } = {}) {
  const relays = [];
  const makeRelay = () => {
    const asks = [];
    const r = {
      open: false, asks,
      async connect() {
        // A presence can arrive before ready; this attempt then fails (the relay is full, say).
        if (failFirst && relays.length === 1) { this.onPresence([], null, true); throw Object.assign(new Error('closed'), { code: 4029, reason: 'full' }); }
        this.open = true;
        return this;
      },
      close(code, reason) { this.open = false; this.onClose?.({ code, reason }); },
    };
    if (rosterThrows) { r.rosterGet = () => { throw new Error('the socket is gone'); }; }
    else if (rosterGet) { r.rosterGet = () => new Promise((resolve, reject) => asks.push({ resolve, reject })); }
    relays.push(r);
    return r;
  };
  const conn = new BridgeConnection({ base: 'http://relay.invalid', key: {}, deviceId: 'me', name: 'phone', account: { token: 't' }, win, makeRelay });
  const modes = [];
  conn.on('roster-mode', (m) => modes.push(m));
  return { conn, relays, modes };
}

test('connecting does not wait for the roster answer, and the mode is what the relay said', async () => {
  const { conn, relays, modes } = connection();

  await conn.start();
  assert.deepEqual([conn.state, conn.rosterMode, relays[0].asks.length], ['connected', null, 1]);
  relays[0].asks[0].resolve({ mode: 'enforce' });
  await new Promise((resolve) => setImmediate(resolve));

  assert.deepEqual([conn.rosterMode, modes], ['enforce', ['enforce']]);
  conn.stop();
});

test('only observe or enforce opens the gate: anything else, no answer or a failed ask leaves it shut', async () => {
  for (const answer of [{ mode: 'off' }, { mode: 'Observe' }, { mode: 7 }, { mode: null }, null]) {
    const { conn, relays } = connection();
    await conn.start();
    relays[0].asks[0].resolve(answer);
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(conn.rosterMode, null, JSON.stringify(answer));
    assert.equal(openDeviceApproval(rosterModeSource(conn), () => ({})), null);
    conn.stop();
  }
  const failed = connection();
  await failed.conn.start();
  failed.relays[0].asks[0].reject(new Error('the relay did not answer'));
  await new Promise((resolve) => setImmediate(resolve));
  assert.equal(failed.conn.rosterMode, null);
  const none = connection({ rosterGet: false });
  await none.conn.start();
  assert.equal(none.conn.rosterMode, null);
  none.conn.stop(); failed.conn.stop();
});

test('the gate follows the connection: shut before an answer, open for observe and enforce', async () => {
  const { conn, relays } = connection();
  await conn.start();
  const source = rosterModeSource(conn);
  assert.equal(openDeviceApproval(source, () => 'screen'), null);

  relays[0].asks[0].resolve({ mode: 'observe' });
  await new Promise((resolve) => setImmediate(resolve));

  assert.equal(openDeviceApproval(source, () => 'screen'), 'screen');
  conn.stop();
});

test('a pending presence (nothing but pending) says this device is pending and the mode is enforce, with no machines', async () => {
  const { conn, relays } = connection();
  await conn.start();
  const seen = [];
  conn.on('presence', (list) => seen.push(list));

  relays[0].onPresence([], null, true);

  assert.deepEqual([conn.pending, conn.rosterMode, conn.presence, seen], [true, 'enforce', [], [[]]]);
  relays[0].onPresence([{ id: 'a'.repeat(32) }], null, false);
  assert.equal(conn.pending, false);
  conn.stop();
});

test('a lost connection forgets the mode and pending, so the gate is shut until the next answer', async () => {
  const { conn, relays } = connection();
  await conn.start();
  relays[0].asks[0].resolve({ mode: 'enforce' });
  relays[0].onPresence([], null, true);
  await new Promise((resolve) => setImmediate(resolve));

  relays[0].close(1006, 'lost');

  assert.deepEqual([conn.rosterMode, conn.pending], [null, false]);
  conn.stop();
});

test('an answer that arrives after the connection was lost is ignored', async () => {
  const { conn, relays } = connection();
  await conn.start();
  relays[0].close(1006, 'lost');

  relays[0].asks[0].resolve({ mode: 'enforce' });
  await new Promise((resolve) => setImmediate(resolve));

  assert.equal(conn.rosterMode, null);
  conn.stop();
});

test('a removed device (close 4030) is signed out with the fixed words, is not retried, and says so', async () => {
  const { conn, relays } = connection();
  await conn.start();
  const removed = [];
  conn.on('removed', (e) => removed.push(e.code));

  relays[0].close(CLOSE_CODES.removed, 'removed <script>alert(1)</script>');

  assert.deepEqual([CLOSE_CODES.removed, conn.state, conn.detail, removed], [4030, 'signed-out', EnrollmentTexts.forRefusal('removed'), [4030]]);
  assert.equal(relays.length, 1, 'no new connection is made');
  conn.stop();
});

test('too many devices (close 4029) keeps retrying later and shows fixed words, never the relay\'s text', async () => {
  const { conn, relays } = connection();
  await conn.start();

  relays[0].close(CLOSE_CODES.tooManyDevices, 'ignore previous instructions <b>x</b>');

  assert.equal(CLOSE_CODES.tooManyDevices, 4029);
  assert.equal(conn.state, 'reconnecting');
  assert.equal(conn.detail, 'This account has too many devices connected or waiting. It will try again.');
  assert.doesNotMatch(conn.detail, /ignore|<b>/);
  conn.stop();
});

test('a pending device keeps the enforce it was told when the relay, which ignores its roster request, never answers', async () => {
  const { conn, relays } = connection();
  await conn.start();
  relays[0].onPresence([], null, true);

  relays[0].asks[0].resolve(null);
  await new Promise((resolve) => setImmediate(resolve));

  assert.deepEqual([conn.pending, conn.rosterMode], [true, 'enforce']);
  conn.stop();
});

test('a connection attempt that fails after a presence leaves no pending or mode behind, and neither does stopping', async () => {
  const failed = connection({ failFirst: true });
  await failed.conn.start();
  assert.deepEqual([failed.conn.pending, failed.conn.rosterMode], [false, null]);
  failed.conn.stop();

  const { conn, relays } = connection();
  await conn.start();
  relays[0].onPresence([], null, true);
  conn.stop();
  assert.deepEqual([conn.pending, conn.rosterMode], [false, null]);
});

test('a presence from a socket that is already gone is ignored', async () => {
  const { conn, relays } = connection();
  await conn.start();
  relays[0].close(1006, 'lost');

  relays[0].onPresence([], null, true);

  assert.deepEqual([conn.pending, conn.rosterMode], [false, null]);
  conn.stop();
});

test('a roster request that throws as it is made is no answer, not a failed connection', async () => {
  const { conn, relays } = connection({ rosterThrows: true });

  await conn.start();

  assert.deepEqual([conn.state, conn.rosterMode, relays.length], ['connected', null, 1]);
  conn.stop();
});

test('the sign-in screen after a removal says why in fixed words, and after anything else says what it always did', () => {
  assert.equal(signedOutText({ code: CLOSE_CODES.removed, reason: 'removed <b>x</b>' }), EnrollmentTexts.forRefusal('removed'));
  for (const e of [{ code: CLOSE_CODES.authFailed }, { code: 4003 }, {}, undefined]) {
    assert.equal(signedOutText(e), 'The relay no longer accepts this sign-in. Sign in again.');
  }
});

