import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readShowDetail, saveShowDetail } from '../../js/show-detail.js';

const KEY = 'agent-bridge.app.showDetail';
const memory = (value = null) => ({
  getItem(key) { assert.equal(key, KEY); return value; },
  setItem(key, next) { assert.equal(key, KEY); value = String(next); },
});

test('detail defaults on, reads saved off, and keeps either choice', () => {
  const storage = memory();
  assert.equal(readShowDetail(storage), true);
  assert.equal(saveShowDetail(false, storage), true);
  assert.equal(readShowDetail(storage), false);
  assert.equal(saveShowDetail(true, storage), true);
  assert.equal(readShowDetail(storage), true);
});

test('invalid saved values default on and invalid choices are not kept', () => {
  for (const value of ['off', '0', '', '{}']) { assert.equal(readShowDetail(memory(value)), true); }
  assert.equal(saveShowDetail('false', memory()), false);
});

test('unavailable storage defaults on and reports that a choice was not kept', () => {
  const blocked = { getItem() { throw new Error('blocked'); }, setItem() { throw new Error('full'); } };
  for (const storage of [null, blocked]) {
    assert.equal(readShowDetail(storage), true);
    assert.equal(saveShowDetail(false, storage), false);
  }
});
