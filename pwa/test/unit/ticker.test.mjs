import { test } from 'node:test';
import assert from 'node:assert/strict';
import { whileVisible } from '../../js/ui/ticker.js';

test('a clock ticks only while the page is in sight, and catches up once as it comes back', (t) => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  const listeners = [];
  const doc = { visibilityState: 'visible', addEventListener: (type, fn) => listeners.push(fn), removeEventListener: (type, fn) => listeners.splice(listeners.indexOf(fn), 1) };
  const show = (visible) => { doc.visibilityState = visible ? 'visible' : 'hidden'; for (const fn of [...listeners]) { fn(); } };
  let ticks = 0;
  const ticker = whileVisible(() => { ticks++; }, 15000, doc);

  t.mock.timers.tick(30000);
  assert.equal(ticks, 2);

  show(false);
  t.mock.timers.tick(10 * 60 * 1000);
  assert.equal(ticks, 2, 'nothing while hidden');

  show(true);
  assert.equal(ticks, 3, 'once at once on return');
  t.mock.timers.tick(15000);
  assert.equal(ticks, 4);

  ticker.stop();
  t.mock.timers.tick(60000);
  show(false);
  show(true);
  assert.equal(ticks, 4);
});
