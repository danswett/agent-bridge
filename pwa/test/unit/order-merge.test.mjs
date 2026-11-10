// The order and read-marker merge (order-merge.js, #1583; design section 5 of
// docs/design/session-order-roaming.md). Every required vector, each checked in both orders.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  emptyOrder, groupOf, mergeMarkers, mergeOrder, mergeRegisters, nextCounter, orderedRows,
} from '../../js/order-merge.js';

const reg = (value, counter, deviceTag) => ({ value, counter, deviceTag });
const tomb = (counter, deviceTag) => ({ tombstone: true, counter, deviceTag });
const order = (parts) => ({ ...emptyOrder(), ...parts });
const plain = (o) => JSON.parse(JSON.stringify(o));

// Merges both ways, requires one answer, and that merging again changes nothing.
function merged(a, b) {
  const ab = mergeOrder(a, b);
  assert.deepEqual(plain(mergeOrder(b, a)), plain(ab));
  assert.deepEqual(plain(mergeOrder(ab, a)), plain(ab));
  assert.deepEqual(plain(mergeOrder(ab, ab)), plain(ab));
  return ab;
}

test('a concurrent move of one row is won by the higher counter', () => {
  const a = order({ rows: { r1: reg({ key: 'm' }, 5, 'aa') } });
  const b = order({ rows: { r1: reg({ key: 'c' }, 6, '01') } });
  assert.equal(merged(a, b).rows.r1.value.key, 'c');
});

test('equal counters go to the higher deviceTag, compared as bytes', () => {
  const a = order({ rows: { r1: reg({ key: 'a' }, 4, '0a') } });
  const b = order({ rows: { r1: reg({ key: 'b' }, 4, '9f') } });
  assert.equal(merged(a, b).rows.r1.value.key, 'b');
  assert.equal(merged(order({ rows: { r1: reg({ key: 'a' }, 4, new Uint8Array([0x01, 0x00])) } }), order({ rows: { r1: reg({ key: 'b' }, 4, '00ff') } })).rows.r1.value.key, 'a');
});

test('a stale write arriving after a newer one changes nothing', () => {
  const newer = order({ rows: { r1: reg({ key: 'x' }, 9, '01') } });
  const stale = order({ rows: { r1: reg({ key: 'y' }, 3, 'ff') } });
  assert.equal(merged(newer, stale).rows.r1.value.key, 'x');
});

test('inserts at one position keep both rows, ordered by key then deviceTag', () => {
  const a = order({ rows: { r1: reg({ key: 'm' }, 1, 'bb'), r3: reg({ key: 'z' }, 1, 'bb') } });
  const b = order({ rows: { r2: reg({ key: 'm' }, 1, 'aa'), r4: reg({ key: 'a' }, 1, 'aa') } });
  assert.deepEqual(orderedRows(merged(a, b)).map((r) => r.row), ['r4', 'r2', 'r1', 'r3']);
});

test('identical registers on two rows still sort one way', () => {
  const a = order({ rows: { b: reg({ key: 'm' }, 1, 'aa'), a: reg({ key: 'm' }, 1, 'aa') } });
  assert.deepEqual(orderedRows(a).map((r) => r.row), ['a', 'b']);
});

test('a delete beats an older move and loses to a newer one', () => {
  const move = order({ rows: { r1: reg({ key: 'k' }, 5, '01') } });
  assert.equal(orderedRows(merged(move, order({ rows: { r1: tomb(6, '01') } }))).length, 0);
  assert.equal(orderedRows(merged(move, order({ rows: { r1: tomb(4, 'ff') } }))).length, 1);
});

test('a tombstone beats a value at an equal counter and tag', () => {
  assert.equal(mergeRegisters(reg({ key: 'k' }, 2, '01'), tomb(2, '01')).tombstone, true);
  assert.equal(mergeRegisters(tomb(2, '01'), reg({ key: 'k' }, 2, '01')).tombstone, true);
});

test('a group rename and a delete: the later one wins, either way round', () => {
  const base = { groups: { g: reg('Work', 1, '01') }, membership: { r1: reg('g', 1, '01') } };
  const rename = order({ groups: { g: reg('Office', 3, '02') } });
  const del = order({ groups: { g: tomb(2, '03') } });
  const renameLater = merged(order(base), merged(rename, del));
  assert.equal(groupOf(renameLater, 'r1'), 'g');
  assert.equal(renameLater.groups.g.value, 'Office');
  const deleteLater = merged(order(base), merged(order({ groups: { g: reg('Office', 2, '02') } }), order({ groups: { g: tomb(3, '03') } })));
  assert.equal(groupOf(deleteLater, 'r1'), null);
});

test('a deleted group sends its sessions back to Sessions', () => {
  const a = order({ groups: { g: reg('Work', 1, '01') }, membership: { r1: reg('g', 2, '01'), r2: reg(null, 2, '01') } });
  assert.equal(groupOf(a, 'r1'), 'g');
  const gone = merged(a, order({ groups: { g: tomb(5, '02') } }));
  assert.equal(groupOf(gone, 'r1'), null);
  assert.equal(groupOf(gone, 'r2'), null);
  assert.equal(groupOf(gone, 'unknown'), null);
});

test('a pin travels with its row register', () => {
  const a = order({ rows: { r1: reg({ key: 'a', pinned: false }, 1, '01') } });
  const b = order({ rows: { r1: reg({ key: 'a', pinned: true }, 2, '01') } });
  assert.equal(orderedRows(merged(a, b))[0].pinned, true);
});

test('a device counts above everything it has seen', () => {
  assert.equal(nextCounter(emptyOrder()), 1);
  assert.equal(nextCounter(order({ rows: { r: reg({}, 4, '01') }, groups: { g: tomb(9, '01') } })), 10);
});

test('merging does not change its inputs and ignores malformed registers and prototype keys', () => {
  const a = order({ rows: { r1: reg({ key: 'a' }, 1, '01'), bad: { value: 1, counter: -1, deviceTag: '01' } } });
  const before = plain(a);
  const evil = JSON.parse('{"rows":{"__proto__":{"value":{"key":"x"},"counter":1,"deviceTag":"01"},"odd":{"counter":1,"deviceTag":"zz"}}}');
  const out = mergeOrder(a, evil);
  assert.deepEqual(plain(a), before);
  assert.deepEqual(Object.keys(out.rows).sort(), ['__proto__', 'r1']);
  assert.equal({}.key, undefined);
});

test('a read marker from two devices keeps the higher turn, either way round', () => {
  assert.deepEqual({ ...mergeMarkers({ s1: 7 }, { s1: 4, s2: 2 }) }, { s1: 7, s2: 2 });
  assert.deepEqual({ ...mergeMarkers({ s1: 4, s2: 2 }, { s1: 7 }) }, { s1: 7, s2: 2 });
});

test('a read marker never moves backward and is idempotent', () => {
  const once = mergeMarkers({ s1: 9 }, { s1: 3 });
  assert.equal(once.s1, 9);
  assert.deepEqual({ ...mergeMarkers(once, once) }, { s1: 9 });
});

test('a marker for a session with no row is kept; bad turns are ignored', () => {
  const out = mergeMarkers({ ghost: 3, neg: -1, frac: 1.5, str: '4' }, {});
  assert.deepEqual({ ...out }, { ghost: 3 });
});
