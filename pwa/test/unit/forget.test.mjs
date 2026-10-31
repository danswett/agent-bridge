// Forget for an offline machine (forget.js): two presses, an arm that lapses or goes when the
// machine comes back, and the relay's refusals said as a person reads them.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ForgetControl, CONFIRM_MS, forgetReason, vanished } from '../../js/forget.js';
import { BridgeConnection } from '../../js/connection.js';

function fakeTimers() {
  const pending = new Map();
  let next = 0;
  return {
    setTimeout: (fn, ms) => { const id = ++next; pending.set(id, { fn, ms }); return id; },
    clearTimeout: (id) => { pending.delete(id); },
    fire() { for (const [id, t] of [...pending]) { pending.delete(id); t.fn(); } },
    pending,
  };
}

function control({ outcome = null } = {}) {
  const asked = [];
  const forgotten = [];
  const timers = fakeTimers();
  const c = new ForgetControl({
    connection: { forget: async (id) => { asked.push(id); if (outcome instanceof Error) { throw outcome; } return outcome; } },
    onForgotten: (id) => forgotten.push(id),
    timers,
  });
  return { c, asked, forgotten, timers };
}

test('the first press arms for a few seconds and asks nothing; the second asks the relay for that machine', async () => {
  const { c, asked, forgotten, timers } = control();
  await c.press('a');
  assert.deepEqual(c.stateOf('a'), { armed: true, busy: false, error: null });
  assert.deepEqual(asked, []);
  assert.equal([...timers.pending.values()][0].ms, CONFIRM_MS);

  await c.press('a');

  assert.deepEqual(asked, ['a']);
  assert.deepEqual(forgotten, ['a']);
  assert.deepEqual(c.stateOf('a'), { armed: false, busy: false, error: null });
});

test('an arm lapses, and the next press arms again rather than forgetting', async () => {
  const { c, asked, timers } = control();
  await c.press('a');
  timers.fire();
  assert.equal(c.stateOf('a').armed, false);

  await c.press('a');

  assert.deepEqual(asked, []);
  assert.equal(c.stateOf('a').armed, true);
});

test('pressing another machine moves the arm to it, so one press there never forgets the first', async () => {
  const { c, asked } = control();
  await c.press('a');
  await c.press('b');
  assert.equal(c.stateOf('a').armed, false);
  assert.equal(c.stateOf('b').armed, true);
  await c.press('a');
  assert.deepEqual(asked, []);
});

test('a machine that comes back takes its arm and any refusal with it', async () => {
  const { c, asked } = control({ outcome: 'rate-limited' });
  await c.press('b');
  await c.press('b');
  assert.equal(c.stateOf('b').error, 'Too many machines forgotten just now. Try again in a minute.');
  await c.press('a');

  c.reconcile(['c']);

  assert.deepEqual(c.stateOf('a'), { armed: false, busy: false, error: null });
  assert.equal(c.stateOf('b').error, null);
  await c.press('a');
  assert.deepEqual(asked, ['b']);
});

test('a refusal is said and the machine is kept; a relay that cannot be reached reads as such', async () => {
  for (const [outcome, said] of [
    ['online', 'It is back online, so it was not forgotten.'],
    ['not-allowed', "The relay refused: only a person's device can forget a machine."],
    ['unsupported', 'The relay cannot forget machines yet.'],
    [new Error('the relay did not answer'), 'The relay did not answer. Try again when connected.'],
  ]) {
    const { c, forgotten } = control({ outcome });
    await c.press('a');
    await c.press('a');
    assert.equal(c.stateOf('a').error, said);
    assert.deepEqual(forgotten, []);
  }
  assert.equal(forgetReason('something-new'), 'The relay refused: something-new.');
});

test('a press while one is in flight does nothing', async () => {
  let release;
  const asked = [];
  const c = new ForgetControl({ connection: { forget: (id) => { asked.push(id); return new Promise((r) => { release = r; }); } }, timers: fakeTimers() });
  await c.press('a');
  const done = c.press('a');
  assert.equal(c.stateOf('a').busy, true);
  await c.press('a');
  await c.press('b');
  release(null);
  await done;
  assert.deepEqual(asked, ['a']);
  assert.equal(c.stateOf('b').armed, false);
});

test('a machine that leaves the offline list without coming online has been forgotten, wherever it was', () => {
  const k = (id) => ({ id, name: id, kind: 'host', lastSeen: '2026-10-31T00:00:00Z' });

  assert.deepEqual(vanished([k('a'), k('b'), k('c')], [k('c')], ['b']), ['a']);
  // A relay without a roster, before or after (a rollback, a reconnect to an older one), says nothing.
  assert.deepEqual(vanished(null, [k('c')], []), []);
  assert.deepEqual(vanished([k('a')], null, []), []);
});

test('a connection to a relay without a roster does not ask it to forget', async () => {
  const conn = new BridgeConnection({ base: 'http://relay.invalid', key: {}, deviceId: 'me', name: 'phone', account: { token: 't' } });
  const sent = [];
  conn.relay = { open: true, forget: async (id) => { sent.push(id); return null; } };

  conn.known = null;
  assert.equal(await conn.forget('a'), 'unsupported');
  conn.known = [];
  assert.equal(await conn.forget('a'), null);
  assert.deepEqual(sent, ['a']);

  conn.relay = null;
  await assert.rejects(conn.forget('a'), /not connected/);
});
