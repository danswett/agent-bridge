import { test } from 'node:test';
import assert from 'node:assert/strict';
import { isPageHidden, whileVisible } from '../../js/ui/ticker.js';

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

test('a visible visibilitychange releases pagehide once without a persisted pageshow', (t) => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  const doc = new EventTarget();
  doc.visibilityState = 'visible';
  doc.defaultView = new EventTarget();
  let ticks = 0;
  const ticker = whileVisible(() => ticks++, 15000, doc);
  doc.defaultView.dispatchEvent(new Event('pagehide'));
  t.mock.timers.tick(15000);
  assert.equal(ticks, 0);
  assert.equal(isPageHidden(doc), true);
  doc.visibilityState = 'hidden';
  doc.dispatchEvent(new Event('visibilitychange'));
  assert.equal(isPageHidden(doc), true, 'a hidden visibilitychange cannot release pagehide');
  doc.visibilityState = 'visible';
  doc.dispatchEvent(new Event('visibilitychange'));
  assert.equal(isPageHidden(doc), false);
  assert.equal(ticks, 1);
  doc.dispatchEvent(new Event('visibilitychange'));
  assert.equal(ticks, 1, 'catch up only once');
  t.mock.timers.tick(15000);
  assert.equal(ticks, 2);
  ticker.stop();
});
