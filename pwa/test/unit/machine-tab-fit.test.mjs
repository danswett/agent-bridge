// Which machine tabs show and which wait under "N more" (machine-tab-fit.js, #351): the rules of
// the Windows app's MachineTabFit.Split (MachineTabsTests.cs).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { splitTabs } from '../../js/ui/machine-tab-fit.js';

test('tabs that all fit all show, in their order, and More has nothing', () => {
  assert.deepEqual(splitTabs([40, 100, 100], 0, 248, 60, 4), { shown: [0, 1, 2], more: [], chosenWidth: null });
});

test('tabs that do not fit wait under More in their order, and what shows leaves room for More', () => {
  const widths = [40, 100, 100, 100];
  const split = splitTabs(widths, 0, 260, 60);
  assert.deepEqual(split.shown, [0, 1]);
  assert.deepEqual(split.more, [2, 3]);
  assert.ok(split.shown.reduce((a, i) => a + widths[i], 0) + 60 <= 260);
});

test('the chosen tab shows in its own place however far along it is, and the others keep their order', () => {
  const split = splitTabs([40, 100, 100, 100], 3, 260, 60);
  assert.deepEqual(split.shown, [0, 3]);
  assert.deepEqual(split.more, [1, 2]);
  assert.equal(split.chosenWidth, null);
});

test('the chosen tab wider than the room is cut to what More leaves, so More stays on the bar', () => {
  assert.deepEqual(splitTabs([46, 154, 120], 1, 247, 134, 0), { shown: [1], more: [0, 2], chosenWidth: 113 });
});

test('the space between tabs counts toward whether they fit', () => {
  assert.deepEqual(splitTabs([100, 100], 0, 200, 40).more, []);
  assert.deepEqual(splitTabs([100, 100], 0, 200, 40, 4).more, [1]);
});
