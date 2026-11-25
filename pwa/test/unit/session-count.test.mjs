// #4547: the sessions count matches the fleet roster, which lists live sessions only.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { countSessions, countLine } from '../../js/session-count.js';

const rows = (host, n) => Array.from({ length: n }, () => ({ hostId: host }));
const STALE = { a: null, b: { reason: 'offline', since: null }, c: { reason: 'reconnecting', since: null }, d: { reason: 'confirming', since: null, grace: true } };
const staleOf = (id) => STALE[id] ?? null;

test('rows kept for an offline machine or an unconfirmed channel are not counted as live', () => {
  // The owner's case in miniature: 3 live on a, 2 kept for offline b, 1 on reconnecting c, 4 in the grace on d.
  const counts = countSessions([...rows('a', 3), ...rows('b', 2), ...rows('c', 1), ...rows('d', 4)], staleOf);
  assert.deepEqual(counts, { live: 7, machines: 2, offline: 2, unconfirmed: 1 });
});

test('107 rows of which 68 are live count 68 live', () => {
  const live = ['a', 'a', 'a', 'a'];
  const entries = [];
  for (let i = 0; i < 68; i += 1) { entries.push({ hostId: live[i % 4] }); }
  for (let i = 0; i < 39; i += 1) { entries.push({ hostId: i < 25 ? 'b' : 'c' }); }
  assert.equal(entries.length, 107);
  assert.deepEqual(countSessions(entries, staleOf), { live: 68, machines: 1, offline: 25, unconfirmed: 14 });
});

test('a machine with every row counted says nothing more than before', () => {
  const counts = countSessions([...rows('a', 3)], staleOf);
  assert.equal(countLine(counts, { several: true }), '3 live on 1 machine');
  assert.equal(countLine(counts, { one: 'Demo machine' }), '3 live on Demo machine');
  assert.equal(countLine(counts), '3 live');
});

test('rows kept apart are named on the line, never added to the live number', () => {
  const counts = countSessions([...rows('a', 3), ...rows('b', 2), ...rows('c', 1)], staleOf);
  assert.equal(countLine(counts, { several: true }), '3 live on 1 machine · 2 offline · 1 not confirmed');
});

test('only kept rows read 0 live, and no rows at all read nothing', () => {
  assert.equal(countLine(countSessions(rows('b', 5), staleOf), { several: true }), '0 live · 5 offline');
  assert.equal(countLine(countSessions([], staleOf), { several: true }), '');
});
