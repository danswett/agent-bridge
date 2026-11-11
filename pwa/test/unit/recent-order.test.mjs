import { test } from 'node:test';
import assert from 'node:assert/strict';
import { RecentOrder } from '../../js/recent-order.js';
import { SessionOrder } from '../../js/session-order.js';

const row = (id, question = null) => ({ hostId: 'host', session: { id, question } });
const list = (...ids) => ({ pinned: [], sessions: ids.map((id) => row(id)), ended: [] });
const ids = (r) => r.sessions.map((e) => e.session.id);

test('activity moves together no sooner than five seconds after the last batch', () => {
  let now = 0;
  const recent = new RecentOrder({ now: () => now });
  const draw = (target, held = false) => ids(recent.arrange(target, { sort: 'recent', held }));
  assert.deepEqual(draw(list('a', 'b', 'c')), ['a', 'b', 'c']);
  assert.deepEqual(draw(list('c', 'a', 'b')), ['c', 'a', 'b']);
  now = 1000;
  assert.deepEqual(draw(list('b', 'a', 'c')), ['c', 'a', 'b']);
  assert.equal(recent.wait, 4000);
  now = 4999;
  assert.deepEqual(draw(list('a', 'b', 'c')), ['c', 'a', 'b']);
  now = 5000;
  assert.deepEqual(draw(list('a', 'b', 'c')), ['a', 'b', 'c']);
  now = 20000;
  assert.deepEqual(draw(list('b', 'c', 'a')), ['b', 'c', 'a']);
});

test('holds collect changes; a new session or question alone bypasses within its group', () => {
  let now = 0;
  const recent = new RecentOrder({ now: () => now });
  const draw = (target, held = true) => ids(recent.arrange(target, { sort: 'recent', held }));
  draw(list('a', 'b', 'c'));
  now = 10000;
  assert.deepEqual(draw(list('b', 'a', 'c')), ['a', 'b', 'c']);
  assert.equal(recent.wait, null);
  assert.deepEqual(draw(list('b', 'new', 'a', 'c')), ['new', 'a', 'b', 'c']);
  const target = list('b', 'c', 'new', 'a');
  target.sessions[1] = row('c', { id: 'question' });
  assert.deepEqual(draw(target), ['c', 'new', 'a', 'b']);
  assert.deepEqual(draw(target), ['c', 'new', 'a', 'b']);
  assert.deepEqual(draw(target, false), ['b', 'c', 'new', 'a']);
  now += 1;
  assert.deepEqual(draw(list('a', 'b', 'c')), ['b', 'c', 'a'], 'ended rows leave immediately');
});

test('pins stay separate, and explicit sorts reset the shown order without activity batching', () => {
  const recent = new RecentOrder();
  const target = { pinned: [row('p'), row('q')], sessions: [row('a')], ended: [] };
  recent.arrange(target, { sort: 'recent' });
  const changed = { ...target, pinned: [row('q'), row('p')] };
  assert.deepEqual(recent.arrange(changed, { sort: 'recent', held: true }).pinned.map((e) => e.session.id), ['p', 'q']);
  for (const sort of ['yours', 'machine', 'recent']) {
    assert.deepEqual(recent.arrange(changed, { sort, held: true }), changed);
  }
});

test('ending a hold before the five-second mark waits only the remaining time', () => {
  let now = 0;
  const recent = new RecentOrder({ now: () => now });
  const draw = (target, held = false) => ids(recent.arrange(target, { sort: 'recent', held }));
  draw(list('a', 'b'));
  draw(list('b', 'a'));
  now = 1000;
  assert.deepEqual(draw(list('a', 'b'), true), ['b', 'a']);
  now = 2000;
  assert.deepEqual(draw(list('a', 'b')), ['b', 'a']);
  assert.equal(recent.wait, 3000);
  now = 5000;
  assert.deepEqual(draw(list('a', 'b')), ['a', 'b']);
});

test('blocked storage uses the same held Most recent order', () => {
  const order = new SessionOrder({ storage: null });
  const recent = new RecentOrder();
  const a = { hostId: 'host', session: { id: 'a', activityAt: '2026-11-01T00:00:02Z' } };
  const b = { hostId: 'host', session: { id: 'b', activityAt: '2026-11-01T00:00:01Z' } };
  const draw = () => ids(recent.arrange(order.arrange([a, b]), { sort: order.sort, held: true }));
  assert.deepEqual(draw(), ['a', 'b']);
  b.session.activityAt = '2026-11-01T00:00:03Z';
  assert.deepEqual(draw(), ['a', 'b']);
});

test('a bypass at hold release schedules eligible ordinary moves without another host event', () => {
  const recent = new RecentOrder();
  const draw = (target, held) => ids(recent.arrange(target, { sort: 'recent', held }));
  draw(list('a', 'b'), true);
  draw(list('b', 'a'), true);
  const target = list('new', 'b', 'a');
  assert.deepEqual(draw(target, false), ['new', 'a', 'b']);
  assert.equal(recent.wait, 0);
  assert.deepEqual(draw(target, false), ['new', 'b', 'a']);
});
