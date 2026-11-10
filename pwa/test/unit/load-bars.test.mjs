import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadBars } from '../../js/load-bars.js';

const sample = (extra = {}) => ({
  state: 'fresh', ageMs: 2000, staleAfterMs: 15000,
  cpu: { percent: 37.5, cores: 8 },
  memory: { totalBytes: 32 * 1024 ** 3, availableBytes: 8 * 1024 ** 3, committedPercent: 92 },
  ...extra,
});
const noFigures = (v) => {
  assert.equal(v.cpu.percent, null);
  assert.equal(v.memory.percent, null);
  assert.equal(v.cpu.text, 'no figure');
  assert.equal(v.memory.text, 'no figure');
};

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
  const v = loadBars(sample({ cpu: { percent: 0 }, memory: { totalBytes: 100, availableBytes: 100 } }));
  assert.equal(v.cpu.percent, 0);
  assert.equal(v.cpu.text, '0%');
  assert.equal(v.memory.percent, 0);
  assert.equal(v.memory.text, '0%');
});

test('host stale state or an age beyond the record limit hides every live figure', () => {
  for (const extra of [{ state: 'stale' }, { ageMs: 15001 }]) {
    const v = loadBars(sample(extra));
    assert.equal(v.state, 'stale');
    noFigures(v);
  }
  assert.equal(loadBars(sample({ ageMs: 15000 })).state, 'fresh');
  assert.equal(loadBars(sample({ ageMs: 50000, staleAfterMs: 60000 })).state, 'fresh');
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
  assert.equal(v.state, 'partial');
  noFigures(v);
});
