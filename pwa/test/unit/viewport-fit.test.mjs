import { test } from 'node:test';
import assert from 'node:assert/strict';
import { measureViewport } from '../../js/viewport-fit.js';

const phone = { innerWidth: 390, screen: { width: 390, height: 844 } };

test('measureViewport: reports the gap under the page on a phone', () => {
  assert.deepEqual(measureViewport({ ...phone, innerHeight: 797, visualViewport: { height: 797 } }), { short: 47, keyboardUp: false });
});

test('measureViewport: the keyboard zeroes the gap', () => {
  assert.deepEqual(measureViewport({ ...phone, innerHeight: 797, visualViewport: { height: 500 } }), { short: 0, keyboardUp: true });
});

test('measureViewport: a windowed iPad gap is clamped, not applied', () => {
  const ipad = { innerWidth: 700, innerHeight: 800, screen: { width: 820, height: 1180 }, visualViewport: { height: 800 } };
  assert.equal(measureViewport(ipad).short, 60);
});
