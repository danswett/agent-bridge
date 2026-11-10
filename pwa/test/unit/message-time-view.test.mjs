// The ticker for the times on messages (#1619): it wakes only when a label could change, and
// never while hidden. Fake timers and a fake page stand in for the clock.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { timeTicker } from '../../js/ui/message-time-view.js';

function setup(iso, nowMs) {
  const t = { textContent: '' };
  const root = { querySelectorAll: () => [{ dataset: { at: iso }, firstElementChild: t, setAttribute() {} }] };
  const timers = [];
  const listeners = {};
  const doc = { visibilityState: 'visible', addEventListener: (n, f) => { listeners[n] = f; }, removeEventListener() {} };
  const clock = { ms: nowMs };
  const ticker = timeTicker(root, {
    now: () => clock.ms,
    setTimer: (f, ms) => { const x = { ms, live: true }; x.f = () => { x.live = false; f(); }; timers.push(x); return timers.length - 1; },
    clearTimer: (i) => { if (i !== null && timers[i]) { timers[i].live = false; } },
    doc,
  });
  const live = () => timers.filter((x) => x.live);
  return { t, ticker, live, doc, listeners, clock };
}

const SENT = '2026-11-09T19:00:00Z';
const T0 = Date.parse(SENT);

test('under a minute it wakes each second, then on the minute, then stops at a clock time', () => {
  const s = setup(SENT, T0 + 5000);
  s.ticker.refresh();
  assert.equal(s.t.textContent, 'just now');
  assert.deepEqual(s.live().map((x) => x.ms), [1000]);
  s.clock.ms = T0 + 12000;
  s.live()[0].f();
  assert.equal(s.t.textContent, '12s ago');
  s.clock.ms = T0 + 61000;
  s.live()[0].f();
  assert.equal(s.t.textContent, '1 min ago');
  assert.equal(s.live()[0].ms, 59000);
  s.clock.ms = T0 + 3600000;
  s.live()[0].f();
  assert.equal(s.live().length, 0);
});

test('it does not run while hidden and catches up when shown', () => {
  const s = setup(SENT, T0 + 30000);
  s.doc.visibilityState = 'hidden';
  s.ticker.refresh();
  assert.equal(s.t.textContent, '');
  assert.equal(s.live().length, 0);
  s.doc.visibilityState = 'visible';
  s.listeners.visibilitychange();
  assert.equal(s.t.textContent, '30s ago');
  assert.equal(s.live().length, 1);
});
