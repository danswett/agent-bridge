import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { FleetRegistryView } from '../../js/fleet-registry-view.js';

// Slice 10 (docs/design/fleet-session-registry.md; #1407): the shared reader-behaviour vector, replayed by the PWA's
// reader. FleetRegistryVectorTests.cs replays the same file in C#; both must give the same snapshots.
const vectors = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/fleet-registry-reader.json', import.meta.url)));
const start = Date.parse(vectors.start);

// "@-20" in `at` and `lastSeen` is that many seconds from the reader's clock at the step.
const materialize = (node, now) => {
  if (Array.isArray(node)) { return node.map((n) => materialize(n, now)); }
  if (node && typeof node === 'object') {
    return Object.fromEntries(Object.entries(node).map(([k, v]) =>
      [k, (k === 'at' || k === 'lastSeen') && typeof v === 'string' && /^@-?\d+$/.test(v) ? new Date(now + Number(v.slice(1)) * 1000).toISOString() : materialize(v, now)]));
  }
  return node;
};

for (const scenario of vectors.scenarios) {
  test(`reader vector: ${scenario.name}`, () => {
    const clock = { wall: start, mono: 0 };
    const view = new FleetRegistryView({ now: () => clock.wall, mono: () => clock.mono });
    for (const step of scenario.steps) {
      if ('advanceSeconds' in step) { clock.wall += step.advanceSeconds * 1000; clock.mono += step.advanceSeconds * 1000; }
      else if ('get' in step) { view.applyGet(materialize(step.get, clock.wall)); }
      else if ('changed' in step) { view.applyChanged(materialize(step.changed, clock.wall)); }
      else {
        const got = view.snapshot().map((m) => ({ id: m.id, online: m.online, live: m.live, verified: m.verified,
          record: m.record ? { epoch: m.record.epoch, seq: m.record.seq } : null })).sort((a, b) => (a.id < b.id ? -1 : 1));
        assert.deepEqual(got, step.expect, step.note ?? scenario.name);
      }
    }
  });
}

test('the reader vector covers every behaviour the registry capabilities name', () => {
  assert.deepEqual([...new Set(vectors.scenarios.map((s) => s.capability))].sort(),
    ['registry-cache-expiry', 'registry-freshness', 'registry-order-and-replay', 'registry-removal-and-purge']);
});
