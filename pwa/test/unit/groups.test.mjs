import { test } from 'node:test';
import assert from 'node:assert/strict';
import { Groups, GroupNameError } from '../../js/groups.js';
import { SessionOrder, orderKey } from '../../js/session-order.js';

const KEY = 'agent-bridge.app.order';
class MemoryStorage {
  raw = null;
  writes = 0;
  getItem(key) { assert.equal(key, KEY); return this.raw; }
  setItem(key, value) { assert.equal(key, KEY); this.raw = value; this.writes++; }
}
const entry = (id, hostId = 'host') => ({
  hostId, session: { id, status: 'idle', updatedAt: '2026-11-11T12:00:00Z' },
});
const keyOf = ({ hostId, session }) => orderKey(hostId, session.id);

test('name validation trims without writing and shares typed errors with group edits', () => {
  const storage = new MemoryStorage();
  const order = new SessionOrder({ storage });
  const leads = order.createGroup('Leads');
  const other = order.createGroup('Other');
  const groups = new Groups(order);
  const before = storage.raw;
  const writes = storage.writes;
  assert.equal(groups.validateName('  New group  '), 'New group');
  assert.equal(groups.validateName(' leads ', leads), 'leads');
  assert.equal(groups.validateName('\u{1f600}'.repeat(40)), '\u{1f600}'.repeat(40));
  for (const name of ['', '   ', null, 'x'.repeat(41)]) {
    assert.throws(() => groups.validateName(name), { name: 'GroupNameError', code: 'length' });
  }
  assert.throws(() => groups.validateName(' leads '), { name: 'GroupNameError', code: 'duplicate' });
  assert.equal(storage.raw, before);
  assert.equal(storage.writes, writes);
  for (const edit of [() => order.createGroup('LEADS'), () => order.renameGroup(other, 'leads')]) {
    assert.throws(edit, (error) => error instanceof GroupNameError
      && error instanceof RangeError && error.code === 'duplicate');
  }
  assert.equal(storage.raw, before);
  assert.equal(storage.writes, writes);
});

test('counts use the current unpinned live rows without changing existing order records', () => {
  const storage = new MemoryStorage();
  const original = new SessionOrder({ storage });
  const rows = [entry('a'), entry('a', 'other-host'), entry('pinned'), entry('ended'), entry('ungrouped')];
  original.arrange(rows);
  const leads = original.createGroup('Leads');
  const empty = original.createGroup('Empty');
  for (const row of rows.slice(0, 4)) { original.setGroup(keyOf(row), leads); }
  original.setPinned(keyOf(rows[2]), true);
  original.observe({ type: 'remove', ...rows[3] });
  original.setGroupCollapsed(leads, true);
  original.setSort('machine');
  const order = new SessionOrder({ storage });
  const arranged = order.arrange(rows.filter((row) => row !== rows[3]));
  const before = storage.raw;
  const writes = storage.writes;
  const groups = new Groups(order);
  assert.deepEqual([...groups.counts(arranged.sessions)], [[leads, 2], [empty, 0]]);
  assert.deepEqual([...groups.counts([])], [[leads, 0], [empty, 0]]);
  assert.equal(groups.validateName('LEADS', leads), 'LEADS');
  assert.equal(order.sort, 'machine');
  assert.equal(order.isGroupCollapsed(leads), true);
  assert.equal(storage.raw, before);
  assert.equal(storage.writes, writes);
  assert.deepEqual(order.groups, original.groups);
  assert.equal(order.groupFor(keyOf(rows[3])), leads);
});

test('moving, removing and deleting still use the existing membership and preserve sessions', () => {
  const storage = new MemoryStorage();
  const order = new SessionOrder({ storage });
  const rows = [entry('a'), entry('b'), entry('c')];
  order.arrange(rows);
  const first = order.createGroup('First');
  const second = order.createGroup('Second');
  order.setGroup(keyOf(rows[0]), first);
  order.setGroup(keyOf(rows[1]), first);
  order.setGroup(keyOf(rows[0]), second);
  order.setGroup(keyOf(rows[0]), null);
  order.setPinned(keyOf(rows[1]), true);
  order.deleteGroup(first);
  const reloaded = new SessionOrder({ storage });
  const arranged = reloaded.arrange(rows);
  for (const row of rows) { assert.equal(reloaded.groupFor(keyOf(row)), null); }
  assert.deepEqual([...new Groups(reloaded).counts(arranged.sessions)], [[second, 0]]);
  assert.deepEqual(arranged.pinned.map(keyOf), [keyOf(rows[1])]);
  assert.deepEqual(new Set([...arranged.pinned, ...arranged.sessions].map(keyOf)), new Set(rows.map(keyOf)));
});

test('corrupt and missing storage stay empty without a write until an explicit edit', () => {
  for (const raw of [null, '{broken']) {
    const storage = new MemoryStorage();
    storage.raw = raw;
    const order = new SessionOrder({ storage });
    const groups = new Groups(order);
    assert.deepEqual([...groups.counts([])], []);
    assert.equal(groups.validateName(' Leads '), 'Leads');
    assert.equal(storage.raw, raw);
    assert.equal(storage.writes, 0);
    const id = order.createGroup(groups.validateName(' Leads '));
    assert.deepEqual(new SessionOrder({ storage }).groups, [{ id, name: 'Leads' }]);
  }
});
