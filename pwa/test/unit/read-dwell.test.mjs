// When a session counts as read (session-order-roaming.md section 8, #1584).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ReadDwell, DWELL_MS } from '../../js/read-dwell.js';

function clock() {
  let next = 1;
  const due = new Map();
  let now = 0;
  return {
    setTimeout(fn, ms) { due.set(next, { fn, at: now + ms }); return next++; },
    clearTimeout(id) { due.delete(id); },
    advance(ms) {
      now += ms;
      for (const [id, t] of [...due]) { if (t.at <= now) { due.delete(id); t.fn(); } }
    },
  };
}

function make() {
  const timers = clock();
  const marked = [];
  return { timers, marked, dwell: new ReadDwell({ mark: (k, at) => marked.push([k, at]), timers }) };
}

test('a session shown for the whole dwell is read once, not before', () => {
  const { timers, marked, dwell } = make();
  dwell.watch('a', 't1', true);
  timers.advance(DWELL_MS - 1);
  assert.deepEqual(marked, []);
  timers.advance(1);
  assert.deepEqual(marked, [['a', 't1']]);
});

test('watching again with the same news does not restart the dwell', () => {
  const { timers, marked, dwell } = make();
  dwell.watch('a', 't1', true);
  timers.advance(600);
  dwell.watch('a', 't1', true);
  timers.advance(400);
  assert.equal(marked.length, 1);
});

test('news arriving during the dwell restarts it and marks the newer news', () => {
  const { timers, marked, dwell } = make();
  dwell.watch('a', 't1', true);
  timers.advance(600);
  dwell.watch('a', 't2', true);
  timers.advance(600);
  assert.deepEqual(marked, []);
  timers.advance(400);
  assert.deepEqual(marked, [['a', 't2']]);
});

test('losing focus, visibility or the open session cancels the dwell', () => {
  const { timers, marked, dwell } = make();
  dwell.watch('a', 't1', true);
  timers.advance(900);
  dwell.watch('a', 't1', false);
  timers.advance(5000);
  assert.deepEqual(marked, []);
  dwell.watch('a', 't1', true);
  timers.advance(500);
  dwell.cancel();
  timers.advance(5000);
  assert.deepEqual(marked, []);
});

test('a session with no news time is never marked', () => {
  const { timers, marked, dwell } = make();
  dwell.watch('a', undefined, true);
  timers.advance(5000);
  assert.deepEqual(marked, []);
});
