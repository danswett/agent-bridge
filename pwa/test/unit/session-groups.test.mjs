import { test } from 'node:test';
import assert from 'node:assert/strict';
import { SessionOrder, orderKey } from '../../js/session-order.js';
import { groupOf, mergeOrder } from '../../js/order-merge.js';

const KEY = 'agent-bridge.app.order';
class MemoryStorage {
  raw = null;
  getItem() { return this.raw; }
  setItem(k, v) { assert.equal(k, KEY); this.raw = v; }
}
const key = (id, host = 'h1') => orderKey(host, id);
const entry = (id, hostId = 'h1') => ({ hostId, session: { id, updatedAt: '2026-11-10T12:00:00Z', activity: { responseTurn: 'before' } } });
const rows = [entry('a'), entry('b'), entry('c'), entry('d')];
const ids = (entries) => entries.map((e) => e.session.id);

test('create and rename trim names, keep empty groups in order and survive reload', () => {
  const storage = new MemoryStorage();
  const o = new SessionOrder({ storage });
  const team = o.createGroup(' Team ');
  const personal = o.createGroup('Personal');
  o.renameGroup(team, ' Work ');
  assert.deepEqual(new SessionOrder({ storage }).groups, [{ id: team, name: 'Work' }, { id: personal, name: 'Personal' }]);
  const copy = o.groups;
  copy[0].name = 'Changed outside';
  assert.equal(o.groups[0].name, 'Work');
  const before = JSON.parse(storage.raw).groupRecords;
  o.renameGroup(team, 'Work again');
  const merged = mergeOrder(JSON.parse(storage.raw).groupRecords, before);
  assert.equal(merged.groups[team].value, 'Work again');
});

test('group names are 1 to 40 characters, unique ignoring case, with at most 32 groups', () => {
  const o = new SessionOrder({ storage: new MemoryStorage() });
  const team = o.createGroup('Team');
  for (const name of ['', '   ', 'x'.repeat(41), ' team ']) {
    assert.throws(() => o.createGroup(name), RangeError);
  }
  const personal = o.createGroup('Personal');
  assert.throws(() => o.renameGroup(personal, 'TEAM'), RangeError);
  assert.throws(() => o.renameGroup(team, ''), RangeError);
  o.renameGroup(team, 'TEAM');
  for (let i = 2; i < 32; i++) { o.createGroup(`Group ${i}`); }
  assert.throws(() => o.createGroup('One too many'), RangeError);
  assert.equal(o.groups.length, 32);
  assert.throws(() => o.renameGroup('missing', 'Other'), RangeError);
});

test('membership spans machines, moves a row to the top once and removes it to Sessions', () => {
  const storage = new MemoryStorage();
  const o = new SessionOrder({ storage });
  const list = [...rows, entry('a', 'h2')];
  o.arrange(list);
  const first = o.createGroup('First');
  const second = o.createGroup('Second');
  o.setGroup(key('c'), first);
  o.setGroup(key('a', 'h2'), first);
  o.setGroup(key('c'), second);
  assert.equal(o.groupFor(key('c')), second);
  assert.equal(o.groupFor(key('a', 'h2')), first);
  assert.equal(o.groupFor(key('a')), null);
  const reloaded = new SessionOrder({ storage });
  assert.equal(reloaded.groupFor(key('c')), second);
  const before = ids(reloaded.arrange(rows).sessions);
  reloaded.setGroup(key('c'), second);
  assert.deepEqual(ids(reloaded.arrange(rows).sessions), before);
  reloaded.setGroup(key('c'), null);
  assert.equal(reloaded.groupFor(key('c')), null);
  assert.equal(ids(reloaded.arrange(rows).sessions)[0], 'c');
  assert.throws(() => o.setGroup(key('a'), 'missing'), RangeError);
});

test('deleting returns members to the top of Sessions in their order and keeps pins pinned', () => {
  const storage = new MemoryStorage();
  const o = new SessionOrder({ storage });
  o.arrange(rows);
  const group = o.createGroup('Team');
  o.setGroup(key('c'), group);
  o.setGroup(key('b'), group);
  o.setGroup(key('d'), group);
  o.setPinned(key('d'), true);
  const before = JSON.parse(storage.raw).groupRecords;
  o.deleteGroup(group);
  const arranged = o.arrange(rows);
  assert.deepEqual(ids(arranged.sessions), ['b', 'c', 'a']);
  assert.deepEqual(ids(arranged.pinned), ['d']);
  for (const e of rows) { assert.equal(o.groupFor(key(e.session.id)), null); }
  assert.deepEqual(new SessionOrder({ storage }).groups, []);
  const after = JSON.parse(storage.raw).groupRecords;
  assert.equal(after.groups[group].tombstone, true);
  assert.equal(groupOf(mergeOrder(before, after), key('c')), null);
});

test('pinning and dragging into Pinned retain membership across reload and unpin', () => {
  const storage = new MemoryStorage();
  const o = new SessionOrder({ storage });
  o.arrange(rows);
  const group = o.createGroup('Team');
  o.setGroup(key('c'), group);
  o.setPinned(key('c'), true);
  o.setGroup(key('b'), group);
  o.arrange(rows);
  assert.ok(o.place(key('b'), key('c')));
  const again = new SessionOrder({ storage });
  assert.equal(again.groupFor(key('b')), group);
  assert.equal(again.groupFor(key('c')), group);
  again.setPinned(key('c'), false);
  assert.equal(again.groupFor(key('c')), group);
});

test('ended and snapshot-removed sessions retain membership for a resume, not an Ended subgroup', () => {
  const storage = new MemoryStorage();
  const o = new SessionOrder({ storage });
  o.arrange(rows);
  const group = o.createGroup('Team');
  o.setGroup(key('b'), group);
  o.setGroup(key('c'), group);
  o.observe({ type: 'remove', ...entry('b') });
  o.synced('h1', new Set(['a', 'd']));
  const result = o.arrange([rows[0], rows[3]]);
  assert.deepEqual(ids(result.ended), ['b']);
  assert.equal(o.groupFor(key('b')), group);
  assert.equal(o.groupFor(key('c')), group);
  const again = new SessionOrder({ storage });
  assert.equal(again.groupFor(key('c')), group);
  assert.equal(ids(again.arrange(rows).sessions)[0], 'b');
  assert.equal(again.groupFor(key('b')), group);
});

test('membership expires 30 days after last listed, not after creation, and offline hosts keep it meanwhile', () => {
  const day = 86400000;
  let now = day;
  const storage = new MemoryStorage();
  let o = new SessionOrder({ storage, now: () => now });
  o.arrange(rows);
  const group = o.createGroup('Team');
  o.setGroup(key('a'), group);
  now += 29 * day;
  o.arrange(rows);
  o.synced('h1', new Set());
  now += 29 * day;
  o = new SessionOrder({ storage, now: () => now });
  o.arrange([]);
  assert.equal(o.groupFor(key('a')), group);
  now += 2 * day;
  o.arrange([]);
  assert.equal(o.groupFor(key('a')), null);
  assert.equal(new SessionOrder({ storage }).groupFor(key('a')), null);
  assert.equal(o.groups.length, 1);
});

test('a group write to full storage falls back to Most recent and blocked storage never claims Your order', () => {
  const storage = new MemoryStorage();
  const o = new SessionOrder({ storage });
  o.arrange(rows);
  storage.setItem = () => { throw new Error('QuotaExceededError'); };
  o.createGroup('Team');
  assert.equal(o.usable, false);
  assert.equal(o.sort, 'recent');
  const blocked = new SessionOrder({ storage: { getItem() { throw new Error('SecurityError'); } } });
  assert.equal(blocked.sort, 'recent');
});

test('a session returning after the retention window does not revive expired membership', () => {
  const day = 86400000;
  const storage = new MemoryStorage();
  const o = new SessionOrder({ storage, now: () => day });
  o.arrange(rows);
  const group = o.createGroup('Team');
  o.setGroup(key('a'), group);
  const again = new SessionOrder({ storage, now: () => 32 * day });
  again.arrange(rows);
  assert.equal(again.groupFor(key('a')), null);
});

test('malformed group names do not break a legacy order or later group edits', () => {
  const storage = new MemoryStorage();
  storage.raw = JSON.stringify({
    order: [key('b'), key('a')], sort: 'machine',
    groupRecords: { groups: { broken: { value: 42, counter: 1, deviceTag: 'aa' } } },
  });
  const o = new SessionOrder({ storage });
  assert.deepEqual(o.groups, []);
  const id = o.createGroup('Team');
  assert.equal(o.groups[0].id, id);
  assert.equal(o.sort, 'machine');
  assert.deepEqual(JSON.parse(storage.raw).order, [key('b'), key('a')]);
});

test('new groups expand and collapse stays on this device without entering roaming records', () => {
  const storage = new MemoryStorage();
  const o = new SessionOrder({ storage });
  const id = o.createGroup('Team');
  assert.equal(o.isGroupCollapsed(id), false);
  const before = JSON.parse(storage.raw).groupRecords;
  o.setGroupCollapsed(id, true);
  assert.equal(new SessionOrder({ storage }).isGroupCollapsed(id), true);
  assert.deepEqual(JSON.parse(storage.raw).groupRecords, before);
  assert.equal(new SessionOrder({ storage: new MemoryStorage() }).isGroupCollapsed(id), false);
  o.deleteGroup(id);
  assert.equal(o.isGroupCollapsed(id), false);
});

test('collapsed groups keep needs-you rows visible and exclude pinned and ended members', () => {
  const o = new SessionOrder({ storage: new MemoryStorage() });
  const list = rows.map((e, i) => ({ ...e, session: { ...e.session, status: i < 2 ? 'waiting' : 'idle' } }));
  o.arrange(list);
  const id = o.createGroup('Team');
  for (const e of list) { o.setGroup(key(e.session.id), id); }
  o.setPinned(key('b'), true);
  o.observe({ type: 'remove', ...list[3] });
  o.setGroupCollapsed(id, true);
  const arranged = o.arrange(list.slice(0, 3));
  const { groups, sessions } = o.groupRows(arranged.sessions);
  assert.equal(groups[0].count, 2);
  assert.equal(groups[0].needs, 1);
  assert.equal(groups[0].expanded, false);
  assert.deepEqual(ids(groups[0].visible), ['a']);
  assert.deepEqual(sessions, []);
});

test('search and Needs you temporarily expand matching groups without changing collapse', () => {
  const storage = new MemoryStorage();
  const o = new SessionOrder({ storage });
  o.arrange(rows);
  const id = o.createGroup('Team');
  o.setGroup(key('a'), id);
  o.setGroup(key('b'), id);
  o.setGroupCollapsed(id, true);
  const entries = o.arrange(rows).sessions;
  const search = o.groupRows(entries, { matches: (e) => e.session.id === 'a', filtered: true });
  assert.equal(search.groups[0].expanded, true);
  assert.deepEqual(ids(search.groups[0].visible), ['a']);
  const waiting = entries.map((e) => ({ ...e, session: { ...e.session, status: e.session.id === 'a' ? 'waiting' : 'idle' } }));
  const needs = o.groupRows(waiting, { needsOnly: true });
  assert.equal(needs.groups[0].expanded, true);
  assert.deepEqual(ids(needs.groups[0].visible), ['a']);
  assert.equal(new SessionOrder({ storage }).isGroupCollapsed(id), true);
  assert.equal(o.groupRows(entries).groups[0].expanded, false);
});

test('empty groups show only without narrowing and collapsed unread marks exclude pins', () => {
  const o = new SessionOrder({ storage: new MemoryStorage() });
  o.arrange(rows);
  const id = o.createGroup('Team');
  o.createGroup('Empty');
  o.setGroup(key('a'), id);
  o.setGroup(key('b'), id);
  o.setPinned(key('b'), true);
  o.setGroupCollapsed(id, true);
  const updated = rows.map((e) => ({ ...e, session: { ...e.session, updatedAt: '2026-11-11T12:00:00Z', activity: { responseTurn: 'after' } } }));
  let entries = o.arrange(updated).sessions;
  assert.equal(o.groupRows(entries).groups[0].unread, true);
  assert.equal(o.groupRows(entries).groups[0].unreadCount, 1);
  o.markSeen(key('a'), updated[0].session.activity.responseTurn);
  assert.equal(o.groupRows(entries).groups[0].unread, false);
  assert.equal(o.groupRows(entries).groups[0].unreadCount, 0);
  assert.equal(o.groupRows(entries).groups.length, 2);
  assert.equal(o.groupRows(entries, { showEmpty: false }).groups.length, 1);
  assert.equal(o.groupRows(entries, { filtered: true, matches: () => false }).groups.length, 0);
});
