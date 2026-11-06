// Text size, kept on this device (ui.md §4.4).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { applyTextSize, readTextSize, saveTextSize } from '../../js/text-size.js';

class MemoryStorage {
  map = new Map();
  getItem(k) { return this.map.has(k) ? this.map.get(k) : null; }
  setItem(k, v) { this.map.set(k, String(v)); }
  removeItem(k) { this.map.delete(k); }
}
const broken = { getItem() { throw new Error('blocked'); }, setItem() { throw new Error('full'); }, removeItem() { throw new Error('blocked'); } };
const fakeRoot = () => ({ attrs: new Map(), setAttribute(k, v) { this.attrs.set(k, v); }, removeAttribute(k) { this.attrs.delete(k); } });

test('a choice is kept, and Default keeps nothing', () => {
  const s = new MemoryStorage();
  assert.equal(readTextSize(s), 'default');
  assert.equal(saveTextSize('smaller', s), true);
  assert.equal(readTextSize(s), 'smaller');
  assert.equal(saveTextSize('default', s), true);
  assert.equal(s.map.size, 0);
  assert.equal(readTextSize(s), 'default');
});

test('anything stored that is not a size reads as Default, and is never saved', () => {
  const s = new MemoryStorage();
  s.setItem('agent-bridge.app.textSize', 'huge');
  assert.equal(readTextSize(s), 'default');
  assert.equal(saveTextSize('huge', s), false);
  assert.equal(s.getItem('agent-bridge.app.textSize'), 'huge');
});

test('storage that cannot be used leaves Default, without throwing', () => {
  assert.equal(readTextSize(broken), 'default');
  assert.equal(readTextSize(null), 'default');
  assert.equal(saveTextSize('larger', broken), false);
  assert.equal(saveTextSize('larger', null), false);
});

test('Smaller and Larger mark the page; Default leaves it as it was', () => {
  const root = fakeRoot();
  applyTextSize('larger', root);
  assert.equal(root.attrs.get('data-text-size'), 'larger');
  applyTextSize('default', root);
  assert.equal(root.attrs.has('data-text-size'), false);
  applyTextSize('bogus', root);
  assert.equal(root.attrs.has('data-text-size'), false);
});
