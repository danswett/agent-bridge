// The sessions list's order, kept per device (#164, ui.md §6.1).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { SessionOrder, orderKey } from '../../js/session-order.js';
import { SessionsStore } from '../../js/sessions-store.js';

class MemoryStorage {
  map = new Map();
  getItem(k) { return this.map.has(k) ? this.map.get(k) : null; }
  setItem(k, v) { this.map.set(k, String(v)); }
  removeItem(k) { this.map.delete(k); }
}

const at = (n) => `2026-11-01T12:00:${String(n).padStart(2, '0')}.000Z`;
const entry = (id, n, hostId = 'h1', title = id) => ({ hostId, session: { id, title, updatedAt: at(n) } });
const ids = (list) => list.map((e) => e.session.id);
const shown = (o, entries, opts) => {
  const r = o.arrange(entries, opts);
  return { pinned: ids(r.pinned), sessions: ids(r.sessions), ended: r.ended.map((e) => e.session.id) };
};
const key = (id, hostId = 'h1') => orderKey(hostId, id);

test('upserts never reorder rows in your order: three sessions updating in turn stay where they are', () => {
  const o = new SessionOrder({ storage: new MemoryStorage() });
  let list = [entry('a', 3), entry('b', 2), entry('c', 1)];
  assert.deepEqual(shown(o, list).sessions, ['a', 'b', 'c']);
  for (const [id, n] of [['c', 10], ['b', 11], ['a', 12], ['c', 13]]) {
    list = list.map((e) => (e.session.id === id ? entry(id, n) : e));
    assert.deepEqual(shown(o, list).sessions, ['a', 'b', 'c'], `after ${id} updated`);
  }
});

test('a new session lands at the top once, then stays put as others update', () => {
  const o = new SessionOrder({ storage: new MemoryStorage() });
  let list = [entry('a', 3), entry('b', 2)];
  shown(o, list);
  list = [...list, entry('new', 1)];
  assert.deepEqual(shown(o, list).sessions, ['new', 'a', 'b']);
  list = list.map((e) => (e.session.id === 'b' ? entry('b', 20) : e));
  assert.deepEqual(shown(o, list).sessions, ['new', 'a', 'b']);
  assert.ok(o.move(key('new'), 1));
  list = list.map((e) => (e.session.id === 'new' ? entry('new', 30) : e));
  assert.deepEqual(shown(o, list).sessions, ['a', 'new', 'b']);
});

test('sessions arriving together for the first time are placed newest first', () => {
  const o = new SessionOrder({ storage: new MemoryStorage() });
  assert.deepEqual(shown(o, [entry('old', 1), entry('newest', 9), entry('mid', 5)]).sessions, ['newest', 'mid', 'old']);
});

test('a machine\'s snapshot, drawn as it arrives, is placed newest first once the machine has sent it all', () => {
  const o = new SessionOrder({ storage: new MemoryStorage() });
  let settled = false;
  const opts = { settled: () => settled };
  const list = [];
  for (const e of [entry('old', 1), entry('mid', 5), entry('newest', 9)]) {
    list.push(e);
    assert.deepEqual(shown(o, list, opts).sessions, [...list].sort((a, b) => (a.session.updatedAt < b.session.updatedAt ? 1 : -1)).map((x) => x.session.id));
  }
  settled = true;
  assert.deepEqual(shown(o, list, opts).sessions, ['newest', 'mid', 'old']);
  list[0] = entry('old', 20);
  assert.deepEqual(shown(o, list, opts).sessions, ['newest', 'mid', 'old']);
});

test('moves persist across a reload of the page', () => {
  const storage = new MemoryStorage();
  const list = [entry('a', 3), entry('b', 2), entry('c', 1)];
  const o = new SessionOrder({ storage });
  shown(o, list);
  assert.ok(o.move(key('a'), 1));
  assert.ok(o.place(key('c'), key('b')));
  assert.deepEqual(shown(o, list).sessions, ['c', 'b', 'a']);
  const again = new SessionOrder({ storage });
  assert.deepEqual(shown(again, list).sessions, ['c', 'b', 'a']);
});

test('a row cannot move past the end of its group, and moves only among rows on screen', () => {
  const o = new SessionOrder({ storage: new MemoryStorage() });
  shown(o, [entry('a', 3), entry('b', 2, 'h2'), entry('c', 1)]);
  assert.equal(o.canMove(key('a'), -1), false);
  assert.equal(o.move(key('a'), -1), false);
  // Machine h2 goes from view: c moves past a in one step, and b keeps its place among them.
  shown(o, [entry('a', 3), entry('c', 1)]);
  assert.ok(o.move(key('c'), -1));
  assert.deepEqual(shown(o, [entry('a', 3), entry('b', 2, 'h2'), entry('c', 1)]).sessions, ['c', 'a', 'b']);
});

test('pin and unpin: pinned rows sit above the rest, and an unpinned one goes back to the top of the rest', () => {
  const storage = new MemoryStorage();
  const o = new SessionOrder({ storage });
  const list = [entry('a', 3), entry('b', 2), entry('c', 1)];
  shown(o, list);
  o.setPinned(key('c'), true);
  o.setPinned(key('b'), true);
  assert.deepEqual(shown(o, list), { pinned: ['c', 'b'], sessions: ['a'], ended: [] });
  assert.equal(o.isPinned(key('c')), true);
  // A pin survives a reload.
  assert.deepEqual(shown(new SessionOrder({ storage }), list).pinned, ['c', 'b']);
  // Moves stay within the pinned group.
  assert.equal(o.canMove(key('b'), 1), false);
  assert.ok(o.move(key('b'), -1));
  assert.deepEqual(shown(o, list).pinned, ['b', 'c']);
  o.setPinned(key('c'), false);
  assert.deepEqual(shown(o, list), { pinned: ['b'], sessions: ['c', 'a'], ended: [] });
});

test('a row dragged among the pinned ones is pinned, and one dragged out is unpinned', () => {
  const o = new SessionOrder({ storage: new MemoryStorage() });
  const list = [entry('a', 3), entry('b', 2), entry('c', 1)];
  shown(o, list);
  o.setPinned(key('a'), true);
  shown(o, list);
  assert.ok(o.place(key('c'), key('a'), true));
  assert.deepEqual(shown(o, list), { pinned: ['a', 'c'], sessions: ['b'], ended: [] });
  assert.ok(o.place(key('a'), key('b'), true));
  assert.deepEqual(shown(o, list), { pinned: ['c'], sessions: ['b', 'a'], ended: [] });
});

test('most recent and machine sorts, and pins above both', () => {
  const o = new SessionOrder({ storage: new MemoryStorage() });
  let list = [entry('a', 3, 'h2'), entry('b', 2, 'h1'), entry('c', 1, 'h2')];
  shown(o, list);
  list = list.map((e) => (e.session.id === 'c' ? entry('c', 9, 'h2') : e));
  o.setSort('recent');
  assert.deepEqual(shown(o, list).sessions, ['c', 'a', 'b']);
  const names = { h1: 'Alpha', h2: 'Beta' };
  o.setSort('machine');
  assert.deepEqual(shown(o, list, { nameOf: (h) => names[h] }).sessions, ['b', 'a', 'c']);
  o.setPinned(key('c', 'h2'), true);
  assert.deepEqual(shown(o, list, { nameOf: (h) => names[h] }), { pinned: ['c'], sessions: ['b', 'a'], ended: [] });
  // Rows move only in your order.
  assert.equal(o.canMove(key('a', 'h2'), -1), false);
});

test('storage that cannot be read or written falls back to most recent', () => {
  const blocked = { getItem() { throw new Error('SecurityError'); }, setItem() { throw new Error('SecurityError'); } };
  const o = new SessionOrder({ storage: blocked });
  assert.equal(o.usable, false);
  assert.equal(o.sort, 'recent');
  const list = [entry('a', 1), entry('b', 2)];
  assert.deepEqual(shown(o, list).sessions, ['b', 'a']);
  assert.deepEqual(shown(o, [entry('a', 5), entry('b', 2)]).sessions, ['a', 'b']);

  // Readable, but full: the first write fails, and the list stops pretending.
  const full = new MemoryStorage();
  full.setItem = () => { throw new Error('QuotaExceededError'); };
  const o2 = new SessionOrder({ storage: full });
  assert.equal(o2.sort, 'yours');
  shown(o2, list);
  assert.equal(o2.usable, false);
  assert.equal(o2.sort, 'recent');
});

test('a row has news once its session updates after it was last open, and not on first sight', () => {
  const o = new SessionOrder({ storage: new MemoryStorage() });
  shown(o, [entry('a', 1)]);
  assert.equal(o.unread(key('a'), at(1)), false);
  assert.equal(o.unread(key('a'), at(2)), true);
  o.markSeen(key('a'), at(2));
  assert.equal(o.unread(key('a'), at(2)), false);
  // Never back in time.
  o.markSeen(key('a'), at(1));
  assert.equal(o.unread(key('a'), at(2)), false);
});

test('a session the host removes ends up in the ended group, and one only gone from view does not', () => {
  const store = new SessionsStore();
  const o = new SessionOrder({ storage: new MemoryStorage(), now: () => Date.parse(at(40)) });
  store.onChange((c) => o.observe({ ...c, hostId: 'h1' }));
  store.upsert({ id: 'a', title: 'A', updatedAt: at(1) });
  store.upsert({ id: 'b', title: 'B', updatedAt: at(2) });
  store.upsert({ id: 'c', title: 'C', updatedAt: at(3) });
  const live = () => store.list().map((s) => ({ hostId: 'h1', session: s }));
  shown(o, live());
  o.setPinned(key('a'), true);
  store.remove('a');
  store.retain(new Set(['c']));
  const r = o.arrange(live());
  assert.deepEqual(ids(r.sessions), ['c']);
  assert.deepEqual(r.ended.map((e) => [e.session.title, e.endedAt]), [['B', at(40)], ['A', at(40)]]);
  assert.equal(o.isPinned(key('a')), false);
  // A machine not shown: its ended sessions aren't either.
  assert.deepEqual(o.arrange(live(), { shows: () => false }).ended, []);
  // Clearing a machine's store (it went offline) ends nothing.
  store.clear();
  assert.equal(o.arrange([]).ended.length, 2);
  o.clearEnded();
  assert.equal(o.arrange([]).ended.length, 0);
});

test('a machine\'s complete list forgets rows of its sessions that ended while nobody looked', () => {
  const storage = new MemoryStorage();
  const o = new SessionOrder({ storage });
  shown(o, [entry('a', 3), entry('b', 2), entry('x', 1, 'h2')]);
  o.synced('h1', new Set(['b']));
  const kept = JSON.parse(storage.getItem('agent-bridge.app.order'));
  assert.deepEqual(kept.order, [key('b'), key('x', 'h2')]);
  assert.deepEqual(Object.keys(kept.seen).sort(), [key('b'), key('x', 'h2')].sort());
});
