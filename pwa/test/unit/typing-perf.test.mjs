import { test } from 'node:test';
import assert from 'node:assert/strict';
import { runInNewContext } from 'node:vm';
import { installTypingMeter, summarizeTyping } from '../typing-perf.mjs';

test('typing percentiles use nearest rank without discarding the slowest keystroke', () => {
  const samples = [120, ...Array.from({ length: 19 }, (_, i) => i + 1)];
  const result = summarizeTyping(samples, 30);
  assert.equal(result.sampleCount, 20);
  assert.equal(result.p50, 10);
  assert.equal(result.p95, 19);
  assert.equal(result.max, 120);
  assert.deepEqual(result.breaches, ['max']);
  assert.equal(samples[0], 120);
});

test('the strict under-100-ms budget flags equality, tails and frame hangs independently', () => {
  assert.deepEqual(summarizeTyping([99.99], 99.99).breaches, []);
  assert.deepEqual(summarizeTyping([100], 20).breaches, ['p95', 'max']);
  assert.deepEqual(summarizeTyping([20], 100).breaches, ['frameGap']);
});

test('missing or invalid measurements never become a passing baseline', () => {
  for (const samples of [[], [NaN], [Infinity], [-1], ['20']]) {
    assert.throws(() => summarizeTyping(samples, 20), /Invalid typing measurements/);
  }
  for (const frameGap of [NaN, Infinity, -1, undefined]) {
    assert.throws(() => summarizeTyping([20], frameGap), /Invalid typing measurements/);
  }
});

test('the meter charges synchronous input work through two frames and restores its instrumentation', () => {
  for (const epochTimestamp of [false, true]) {
    let now = 10;
    let id = 0;
    const callbacks = new Map();
    const handlers = new Map();
    const storage = { setItem: () => 'stored' };
    const original = storage.setItem;
    const input = {
      closest: () => ({ querySelector: () => ({}) }),
      addEventListener: (name, handler) => handlers.set(name, handler),
      removeEventListener: (name) => handlers.delete(name),
    };
    const context = {
      input, window: {}, Storage: { prototype: storage },
      document: { getElementsByTagName: () => [] },
      performance: { now: () => now, timeOrigin: 1700000000000 },
      requestAnimationFrame: (fn) => { callbacks.set(++id, fn); return id; },
      cancelAnimationFrame: (key) => callbacks.delete(key),
      MutationObserver: class { observe() {} disconnect() {} takeRecords() { return []; } },
    };
    const frame = (at) => {
      now = at;
      const batch = [...callbacks.values()];
      callbacks.clear();
      for (const fn of batch) { fn(at); }
    };
    runInNewContext(`(${installTypingMeter.toString()})(input)`, context);
    handlers.get('keydown')({ key: 'a', timeStamp: epochTimestamp ? 1700000000010 : 10 });
    now = 160;
    handlers.get('input')({ isTrusted: true });
    assert.equal(storage.setItem('synthetic', 'value'), 'stored');
    frame(170);
    assert.equal(context.window.__typingPerf.samples.length, 0);
    frame(185);
    const result = context.window.__typingPerf.finish();
    assert.equal(result.samples[0], 175);
    assert.equal(result.worstFrameMs, 160);
    assert.equal(result.trustedInputs, 1);
    assert.equal(result.storageWrites, 1);
    assert.equal(storage.setItem, original);
    assert.equal(handlers.size, 0);
    assert.equal(callbacks.size, 0);
  }
});
