import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

// Spec-only model; verified stands for enrolled-key/signature and replay verification, not a wire flag.
function select(records, now) {
  const live = records.filter(r => r.live);
  const result = result => ({ result });
  if (!live.length || live.some(r => r.verified !== true)) return result('refused');
  let missing = false;
  const integer = n => Number.isSafeInteger(n) && n >= 0;
  for (const record of live) {
    for (const field of ['at', 'epoch', 'seq']) {
      if (record[field] == null) { missing = true; continue; }
      if (!integer(record[field])) return result('refused');
      if (field === 'at' && Math.abs(record.at - now) > 60000) return result('refused');
    }
    const previous = record.previous;
    if (previous) {
      if (!integer(previous.epoch) || !integer(previous.seq)) return result('refused');
      if (integer(record.epoch) && integer(record.seq) &&
          (record.epoch < previous.epoch || record.epoch === previous.epoch && record.seq <= previous.seq))
        return result('refused');
    }
  }
  if (missing || new Set(live.map(r => r.machine)).size !== 1) return result('ambiguous');
  const ranked = [...live].sort((a, b) => b.epoch - a.epoch || b.seq - a.seq);
  if (ranked.length > 1 && ranked[0].epoch === ranked[1].epoch && ranked[0].seq === ranked[1].seq)
    return result('ambiguous');
  return { result: 'found', id: ranked[0].id };
}

test('shared order vectors pin the spec-only newest-wins rule', () => {
  const file = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/fleet-registry-order.json', import.meta.url), 'utf8'));
  assert.ok(file.cases.length);
  assert.equal(new Set(file.cases.map(c => c.name)).size, file.cases.length);
  for (const vector of file.cases) {
    assert.ok(vector.records.length, vector.name);
    assert.ok(['found', 'ambiguous', 'refused'].includes(vector.expected.result), vector.name);
    for (const records of [vector.records, [...vector.records].reverse()])
      assert.deepEqual(select(records, file.readerAt), vector.expected, vector.name);
  }
});
