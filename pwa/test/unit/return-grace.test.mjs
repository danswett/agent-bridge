import { test } from 'node:test';
import assert from 'node:assert/strict';
import { ReturnGrace, HELD_MS, CUE_MS, GRACE_MS } from '../../js/return-grace.js';

function clock() {
  let t = 0;
  let next = 1;
  const pending = new Map();
  return {
    now: () => t,
    timers: {
      setTimeout: (fn, ms) => { pending.set(next, { fn, at: t + ms }); return next++; },
      clearTimeout: (id) => { pending.delete(id); },
    },
    tick(ms) {
      const end = t + ms;
      for (;;) {
        const due = [...pending.entries()].filter(([, p]) => p.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
        if (!due) { break; }
        pending.delete(due[0]);
        t = due[1].at;
        due[1].fn();
      }
      t = end;
    },
  };
}

function target() {
  const handlers = new Map();
  return {
    addEventListener: (n, f) => handlers.set(n, [...(handlers.get(n) ?? []), f]),
    removeEventListener: (n, f) => handlers.set(n, (handlers.get(n) ?? []).filter((x) => x !== f)),
    fire: (n, e = {}) => { for (const f of handlers.get(n) ?? []) { f(e); } },
  };
}

test('a return holds for three seconds and then is over', () => {
  const c = clock();
  const g = new ReturnGrace(c);
  assert.equal(g.active, false);
  g.start();
  assert.equal(g.active, true);
  c.tick(GRACE_MS - 1);
  assert.equal(g.active, true);
  c.tick(1);
  assert.equal(g.active, false);
});

test('a held control is drawn only after 600 ms and the slot cue comes after 1 s', () => {
  const c = clock();
  const g = new ReturnGrace(c);
  let changes = 0;
  g.onChange(() => { changes += 1; });
  g.start();
  assert.equal(g.drawn, false);
  c.tick(HELD_MS - 1);
  assert.equal(g.drawn, false);
  c.tick(1);
  assert.equal(g.drawn, true);
  c.tick(CUE_MS - HELD_MS);
  assert.equal(changes, 3);
});

test('a second event inside the grace leaves it running from its first start, and one after it starts a new grace', () => {
  const c = clock();
  const g = new ReturnGrace(c);
  g.start();
  c.tick(800);
  g.start();
  assert.equal(g.drawn, true);
  c.tick(2200);
  assert.equal(g.active, false);
  g.start();
  assert.equal(g.active, true);
  assert.equal(g.drawn, false);
});

test('the page shown again, restored from the cache, or the network back is a return; hidden and a plain pageshow are not', () => {
  const c = clock();
  const doc = target();
  const win = target();
  doc.visibilityState = 'hidden';
  win.document = doc;
  const g = new ReturnGrace(c);
  const stop = g.watch(win);
  doc.fire('visibilitychange');
  win.fire('pageshow', { persisted: false });
  assert.equal(g.active, false);
  doc.visibilityState = 'visible';
  doc.fire('visibilitychange');
  assert.equal(g.active, true);
  g.end();
  win.fire('pageshow', { persisted: true });
  assert.equal(g.active, true);
  g.end();
  win.fire('online');
  assert.equal(g.active, true);
  g.end();
  stop();
  win.fire('online');
  assert.equal(g.active, false);
});
