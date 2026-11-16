import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadBars } from '../../js/load-bars.js';

const sample = (extra = {}) => ({
  state: 'fresh', ageMs: 2000, staleAfterMs: 15000,
  cpu: { percent: 37.5, cores: 8 },
  memory: { totalBytes: 32 * 1024 ** 3, availableBytes: 8 * 1024 ** 3, committedPercent: 92, usedPercent: 75 },
  ...extra,
});
const noFigures = (v) => {
  assert.equal(v.cpu.percent, null);
  assert.equal(v.memory.percent, null);
  assert.equal(v.cpu.text, 'no figure');
  assert.equal(v.memory.text, 'no figure');
};

test('memory uses only the full-precision host percentage, including real zero and one hundred', () => {
  for (const [usedPercent, availableBytes] of [[25.3456789012345, 25], [0, 100], [100, 0]]) {
    const v = loadBars(sample({ memory: { totalBytes: 100, availableBytes, committedPercent: 92, usedPercent } }));
    assert.equal(v.memory.percent, usedPercent);
    assert.equal(v.cpu.percent, 37.5);
  }
});

test('a missing or invalid memory figure never becomes a byte ratio, committed figure or zero', () => {
  for (const usedPercent of [undefined, null, NaN, Infinity, -Infinity, -0.1, 100.1, '0']) {
    const memory = { totalBytes: 100, availableBytes: 25, committedPercent: 92,
      ...(usedPercent === undefined ? {} : { usedPercent }) };
    const v = loadBars(sample({ memory }));
    assert.equal(v.memory.percent, null);
    assert.equal(v.state, 'fresh');
    assert.equal(v.cpu.percent, 37.5);
  }
});

test('host freshness and raw CPU survive a conflicting age limit and absent memory figure', () => {
  for (const state of ['fresh', 'partial']) {
    const v = loadBars(sample({ state, ageMs: 20000, cpu: { percent: 37.543 }, memory: undefined }));
    assert.equal(v.state, state);
    assert.equal(v.word, state === 'fresh' ? 'Fresh' : 'Partial');
    assert.equal(v.ageMs, 20000);
    assert.equal(v.cpu.percent, 37.543);
    assert.equal(v.memory.percent, null);
  }
});

test('bars show the exact whole-machine CPU and used memory share, not committed memory', () => {
  const v = loadBars(sample());
  assert.equal(v.state, 'fresh');
  assert.equal(v.word, 'Fresh');
  assert.equal(v.cpu.label, 'CPU (whole machine)');
  assert.equal(v.cpu.percent, 37.5);
  assert.equal(v.cpu.text, '37.5%');
  assert.equal(v.memory.label, 'Memory used');
  assert.equal(v.memory.percent, 75);
  assert.equal(v.memory.text, '75%');
  assert.equal(v.ageMs, 2000);
  assert.equal(loadBars(sample({ cpu: { percent: 37.543 } })).cpu.text, '37.543%');
});

test('a partial sample keeps measured memory but a missing CPU is no figure, never zero', () => {
  const v = loadBars(sample({ state: 'partial', cpu: undefined }));
  assert.equal(v.state, 'partial');
  assert.equal(v.cpu.percent, null);
  assert.equal(v.cpu.text, 'no figure');
  assert.equal(v.memory.percent, 75);
  assert.notEqual(v.icon, loadBars(sample()).icon);
  assert.equal(loadBars(sample({ memory: { availableBytes: 1024 } })).memory.percent, null);
});

test('measured zero CPU and completely free memory remain real zero figures', () => {
  const v = loadBars(sample({ cpu: { percent: 0 }, memory: { totalBytes: 100, availableBytes: 100, usedPercent: 0 } }));
  assert.equal(v.cpu.percent, 0);
  assert.equal(v.cpu.text, '0%');
  assert.equal(v.memory.percent, 0);
  assert.equal(v.memory.text, '0%');
});

test('only the host stale state hides every live figure; age alone never reclassifies', () => {
  const v = loadBars(sample({ state: 'stale' }));
  assert.equal(v.state, 'stale');
  noFigures(v);
  for (const extra of [{ ageMs: 15000 }, { ageMs: 15001 }, { ageMs: 50000, staleAfterMs: 60000 }]) {
    const f = loadBars(sample(extra));
    assert.equal(f.state, 'fresh');
    assert.equal(f.cpu.percent, 37.5);
  }
});

test('unavailable and offline never repeat figures from the last sample', () => {
  const unavailable = loadBars(sample({ state: 'unavailable' }));
  const offline = loadBars(sample(), { online: false });
  assert.equal(unavailable.word, 'Unavailable');
  assert.equal(offline.word, 'Offline');
  assert.notEqual(offline.icon, unavailable.icon);
  noFigures(unavailable);
  noFigures(offline);
  noFigures(loadBars(null));
});

test('unknown states and invalid figures cannot look like measured load', () => {
  noFigures(loadBars(sample({ state: 'future' })));
  const v = loadBars(sample({ cpu: { percent: NaN }, memory: { totalBytes: 100, availableBytes: 101 } }));
  assert.equal(v.state, 'fresh');
  assert.equal(v.cpu.percent, null);
  assert.equal(v.memory.percent, null);
});
