import { test } from 'node:test';
import assert from 'node:assert/strict';
import { TurnClock } from '../../js/turn-clock.js';

function clock() {
  let t = 1000;
  const c = new TurnClock({ now: () => t });
  return { c, at: (ms) => { t = ms; } };
}

test('a turn is timed from the send to the session settling', () => {
  const { c, at } = clock();
  c.sent('h', 's');
  at(1500); c.seen('h', 's', 'working');
  at(5200); c.seen('h', 's', 'idle');
  assert.deepEqual(c.recent(), [{ app: 'PWA', ms: 4200, at: 5 }]);
});

test('a status seen before the session starts working does not settle the turn', () => {
  const { c, at } = clock();
  c.sent('h', 's');
  at(1200); c.seen('h', 's', 'idle');
  assert.deepEqual(c.recent(), []);
  at(2000); c.seen('h', 's', 'working');
  at(3000); c.seen('h', 's', 'waiting');
  assert.deepEqual(c.recent(), [{ app: 'PWA', ms: 2000, at: 3 }]);
});

test('a second send before the turn settles keeps the first start', () => {
  const { c, at } = clock();
  c.sent('h', 's');
  at(2000); c.sent('h', 's'); c.seen('h', 's', 'working');
  at(4000); c.seen('h', 's', 'idle');
  assert.deepEqual(c.recent(), [{ app: 'PWA', ms: 3000, at: 4 }]);
});

test('a session nobody sent to, or one that went away, adds nothing', () => {
  const { c, at } = clock();
  c.seen('h', 'other', 'working'); c.seen('h', 'other', 'idle');
  c.sent('h', 's'); c.seen('h', 's', 'working'); c.dropped('h', 's');
  at(3000); c.seen('h', 's', 'idle');
  assert.deepEqual(c.recent(), []);
});

test('a turn left open for over an hour is not a measurement', () => {
  const { c, at } = clock();
  c.sent('h', 's');
  at(1000 + 61 * 60 * 1000); c.seen('h', 's', 'working'); c.seen('h', 's', 'idle');
  assert.deepEqual(c.recent(), []);
});

test('a measurement holds only the app, duration and time, and drain hands it over once', () => {
  const { c, at } = clock();
  c.sent('machine-1', 'session-1'); c.seen('machine-1', 'session-1', 'working');
  at(1700); c.seen('machine-1', 'session-1', 'idle');
  const out = c.drain();
  assert.deepEqual(Object.keys(out[0]).sort(), ['app', 'at', 'ms']);
  assert.deepEqual(c.drain(), []);
});

test('a send after a stale unsettled turn starts a fresh measurement', () => {
  const { c, at } = clock();
  c.sent('h', 's');
  at(1000 + 2 * 3600 * 1000); c.sent('h', 's');
  at(1000 + 2 * 3600 * 1000 + 100); c.seen('h', 's', 'working');
  at(1000 + 2 * 3600 * 1000 + 900); c.seen('h', 's', 'idle');
  assert.deepEqual(c.recent(), [{ app: 'PWA', ms: 900, at: 7201 }]);
});
