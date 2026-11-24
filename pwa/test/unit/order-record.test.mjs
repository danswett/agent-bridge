// The sealed order record's plaintext (docs/protocol/order-records.md §3, §5, §6), replayed from the shared
// vectors in docs/protocol/vectors/order-records.json (#759, S2): what a reader accepts and refuses, the exact
// expansion to the merge form and the limits. What goes when a record is over (fit) is the next piece.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { canonical, fromMergeForm, LIMITS, OrderRecordError, parseOrder, toMergeForm } from '../../js/order-record.js';
import { mergeOrder } from '../../js/order-merge.js';

const v = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/order-records.json', import.meta.url), 'utf8')).order;
const LF = String.fromCharCode(10);
const pad = (n, w) => String(n).padStart(w, '0');
const sid = (i, series = 8) => `00000000-0000-4000-${series}000-${pad(i, 12)}`; // a session id the size of a real one

// The records of "limits": built by the rule the file's "about" gives.
function build({ rows, memberships, groups, tombstones }) {
  const o = { d: ['0'.repeat(31) + '1'], h: ['h0', 'h1', 'h2'].slice(0, Math.min(3, Math.max(rows, memberships ? 3 : 1))), v: 1, g: {}, m: {}, r: {}, x: { g: {}, m: {}, r: {} } };
  for (let n = 0; n < groups; n += 1) { o.g[`00000000-0000-4000-8000-${pad(n, 12)}`] = [900 + n, 0, `Group ${n}`]; }
  for (let i = 0; i < rows; i += 1) { o.r[`${i % 3}${LF}${sid(i)}`] = [i + 1, 0, `k${i.toString(36).padStart(3, '0')}`, i % 20 === 0 ? 1 : 0]; }
  for (let j = 0; j < memberships; j += 1) { o.m[`${(4 * j) % 3}${LF}${sid(4 * j)}`] = [300 + j, 0, `00000000-0000-4000-8000-${pad(j % groups, 12)}`]; }
  for (let t = 0; t < tombstones; t += 1) { o.x.r[`0${LF}${sid(t, 9)}`] = [2000 + t, 0, 20400]; }
  return canonical(o);
}
const refuses = (text, code, name) => assert.throws(() => parseOrder(text), (e) => e instanceof OrderRecordError && e.code === code, name);

test('every valid record reads, and writes back as the same bytes', () => {
  for (const c of v.valid) { assert.equal(canonical(parseOrder(c.text)), c.text, c.name); }
});

test('every refused record is refused for its reason', () => {
  for (const c of v.refused) { refuses(c.text, c.code, c.name); }
});

test('the compact form expands to the merge form of order-merge.js and back, exactly', () => {
  for (const c of v.merge) {
    const merged = toMergeForm(parseOrder(c.text));
    assert.deepEqual(merged, c.mergeForm, c.name);
    assert.equal(canonical(fromMergeForm(c.mergeForm, { nowDay: 0 })), c.text, `${c.name}: back, whatever order the merge form lists its devices and hosts`);
    // The merge ports read it as they read their own state (a tombstone's day is not theirs to keep yet).
    const plain = structuredClone(merged);
    for (const map of Object.values(plain)) { for (const r of Object.values(map)) { delete r.day; } }
    assert.deepEqual(mergeOrder(merged, merged), mergeOrder(plain, plain), c.name);
  }
});

test('the limits: 500 rows, 500 memberships, 32 groups, and the size, measured in canonical bytes', () => {
  for (const c of v.limits) {
    const text = build(c);
    if (c.ok) {
      assert.doesNotThrow(() => parseOrder(text), c.name);
      assert.ok(Buffer.byteLength(text) < 46000, `${c.name}: ${Buffer.byteLength(text)} bytes`);
      // The merge form of the same state would not fit: why the record is compact.
      assert.ok(JSON.stringify(toMergeForm(parseOrder(text))).length > LIMITS.bytes, 'the merge form is over 64 KiB');
      assert.ok(Buffer.byteLength(text) + 106 <= LIMITS.bytes, 'and its sealed object fits 64 KiB');
    } else { refuses(text, c.code, c.name); }
  }
  refuses(build({ rows: 1, memberships: 0, groups: 0, tombstones: 4000 }), 'too-large', 'a record whose sealed object would pass 65,536 bytes');
});

test('a deeply nested input is refused with a code, not a stack overflow', () => {
  refuses('['.repeat(30000) + ']'.repeat(30000), 'not-canonical', 'nesting');
});
