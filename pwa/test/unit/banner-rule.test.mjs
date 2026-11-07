import { test } from 'node:test';
import assert from 'node:assert/strict';
import { LastingBanner, BANNER_GRACE_MS, BANNER_MIN_MS } from '../../js/banner-rule.js';

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

function banner() {
  const c = clock();
  const log = [];
  const b = new LastingBanner({ ...c, show: () => log.push(`show@${c.now()}`), hide: () => log.push(`hide@${c.now()}`) });
  return { c, log, b };
}

test('a banner for a state that holds shows only after the grace', () => {
  const { c, log, b } = banner();
  b.set(true);
  c.tick(BANNER_GRACE_MS - 1);
  assert.deepEqual(log, []);
  c.tick(1);
  assert.deepEqual(log, [`show@${BANNER_GRACE_MS}`]);
});

test('a state that goes inside the grace never draws a banner', () => {
  const { c, log, b } = banner();
  b.set(true);
  c.tick(2000);
  b.set(false);
  c.tick(20000);
  assert.deepEqual(log, []);
});

test('a banner whose state goes stays until it has been up ten seconds', () => {
  const { c, log, b } = banner();
  b.set(true);
  c.tick(BANNER_GRACE_MS);
  c.tick(1000);
  b.set(false);
  c.tick(BANNER_MIN_MS - 1000 - 1);
  assert.deepEqual(log, [`show@${BANNER_GRACE_MS}`]);
  c.tick(1);
  assert.deepEqual(log, [`show@${BANNER_GRACE_MS}`, `hide@${BANNER_GRACE_MS + BANNER_MIN_MS}`]);
});

test('a banner whose state goes after ten seconds is taken away at once', () => {
  const { c, log, b } = banner();
  b.set(true);
  c.tick(BANNER_GRACE_MS + BANNER_MIN_MS + 5000);
  b.set(false);
  assert.deepEqual(log, [`show@${BANNER_GRACE_MS}`, `hide@${BANNER_GRACE_MS + BANNER_MIN_MS + 5000}`]);
});

test('a state that comes back while its banner waits to go keeps it, shown once', () => {
  const { c, log, b } = banner();
  b.set(true);
  c.tick(BANNER_GRACE_MS + 1000);
  b.set(false);
  c.tick(2000);
  b.set(true);
  c.tick(60000);
  assert.deepEqual(log, [`show@${BANNER_GRACE_MS}`]);
});
