import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { PANES, PANES_KEY, PaneWidths, settle, limits, pick } from '../../js/pane-widths.js';

const tokens = JSON.parse(readFileSync(new URL('../../../design/tokens.json', import.meta.url), 'utf8'));
const memory = (seed) => {
  const m = new Map(seed ? [[PANES_KEY, seed]] : []);
  return { getItem: (k) => m.get(k) ?? null, setItem: (k, v) => m.set(k, v), removeItem: (k) => m.delete(k), m };
};

test('the pane sizes are the layout.pwa tokens', () => {
  for (const [name, value] of Object.entries(PANES)) { assert.equal(value, tokens.layout.pwa[name], name); }
});

test('two panes keep the list at its default of 320 where the session keeps 440', () => {
  assert.deepEqual(settle({ total: 760 }), { list: 320, inspector: 0 });
});

test('three panes at their defaults leave the session the rest', () => {
  assert.deepEqual(settle({ total: 1280, inspectorShown: true }), { list: 320, inspector: 336 });
});

test('the inspector gives way first, down to its minimum, and then the list, the kept widths untouched', () => {
  assert.deepEqual(settle({ total: 1100, list: 400, inspector: 500, inspectorShown: true }), { list: 380, inspector: 280 });
  assert.deepEqual(settle({ total: 1200, list: 400, inspector: 500, inspectorShown: true }), { list: 400, inspector: 360 });
});

test('the list gives way only once the inspector is at its minimum', () => {
  assert.deepEqual(settle({ total: 1000, list: 480, inspector: 560, inspectorShown: true }), { list: 280, inspector: 280 });
});

test('the widths come back when the window widens', () => {
  const kept = { list: 400, inspector: 500, inspectorShown: true };
  assert.equal(settle({ total: 1000, ...kept }).inspector, 280);
  assert.deepEqual(settle({ total: 1600, ...kept }), { list: 400, inspector: 500 });
});

test('a kept width past a limit is held to the limit', () => {
  assert.deepEqual(settle({ total: 1800, list: 900, inspector: 10, inspectorShown: true }), { list: 480, inspector: 280 });
});

test('the list divider may take the inspector down to its minimum', () => {
  assert.deepEqual(limits('list', { total: 1280, inspectorShown: true }), { min: 264, max: 480 });
  assert.equal(limits('list', { total: 1000, inspectorShown: true }).max, 280);
  assert.equal(limits('list', { total: 900 }).max, 460);
});

test('the inspector divider may not take the width the list keeps', () => {
  assert.equal(limits('inspector', { total: 1280, list: 480, inspectorShown: true }).max, 360);
  assert.equal(limits('inspector', { total: 1800, inspectorShown: true }).max, 560);
});

test('a width asked for is rounded and held to its limits', () => {
  const state = { total: 1440, inspectorShown: true };
  assert.equal(pick('list', 100, state), 264);
  assert.equal(pick('list', 700, state), 480);
  assert.equal(pick('inspector', 400.4, state), 400);
});

test('widths kept are read back, held to their limits, and a reset forgets one', () => {
  const s = memory(JSON.stringify({ list: 400, inspector: 9999 }));
  const w = new PaneWidths({ storage: s });
  assert.equal(w.list, 400);
  assert.equal(w.inspector, 560);
  w.reset('inspector');
  assert.equal(w.inspector, 336);
  assert.deepEqual(JSON.parse(s.m.get(PANES_KEY)), { list: 400 });
  w.reset('list');
  assert.equal(s.m.has(PANES_KEY), false);
});

test('a width held in memory during a drag can be put back without saving', () => {
  const s = memory(JSON.stringify({ list: 400 }));
  const w = new PaneWidths({ storage: s });
  w.set('list', 300, false);
  w.set('inspector', 500, false);
  w.restore('list', 400);
  w.restore('inspector', undefined);
  assert.equal(w.list, 400);
  assert.equal(w.inspector, 336);
  assert.deepEqual(JSON.parse(s.m.get(PANES_KEY)), { list: 400 });
});

test('storage that is blocked or holds rubbish leaves the defaults and does not throw', () => {
  const blocked = { getItem() { throw new Error('blocked'); }, setItem() { throw new Error('blocked'); }, removeItem() { throw new Error('blocked'); } };
  const w = new PaneWidths({ storage: blocked });
  w.set('list', 400);
  assert.equal(w.list, 400);
  assert.equal(new PaneWidths({ storage: memory('{{') }).list, 320);
  assert.equal(new PaneWidths({ storage: null }).inspector, 336);
});
