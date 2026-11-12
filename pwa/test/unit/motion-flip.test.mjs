import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as motion from '../../js/ui/motion.js';

test('same-frame list mutations take effect immediately and coalesce into one token-timed neighbour shift', () => {
  const frames = [];
  const animations = [];
  const doc = { documentElement: {}, defaultView: { requestAnimationFrame: (fn) => frames.push(fn) } };
  const css = { getPropertyValue: (key) => key.endsWith('-duration') ? '167ms' : 'linear' };
  const previous = globalThis.getComputedStyle;
  globalThis.getComputedStyle = () => css;
  try {
    let top = 100;
    const child = {
      isConnected: true, ownerDocument: doc, getClientRects: () => [{}],
      getBoundingClientRect: () => ({ top, left: 0, width: 100, height: 40 }),
      getAnimations: () => [],
      animate: (keyframes, options) => { const a = { keyframes, options }; animations.push(a); return a; },
    };
    const container = { children: [child], ownerDocument: doc, isConnected: true, getBoundingClientRect: () => ({ top: 0, left: 0 }) };
    child.parentElement = container;
    motion.flip(container, () => { top += 40; });
    motion.flip(container, () => { top += 40; });
    assert.equal(top, 180, 'the real layout must never wait for animation');
    assert.equal(frames.length, 1, 'a burst must share one frame');
    frames.shift()();
    assert.equal(animations.length, 1);
    assert.equal(animations[0].id, 'ab-motion-shift');
    assert.equal(animations[0].options.duration, 167);
    assert.equal(animations[0].keyframes[0].transform, 'translate(0px, -80px)');
    assert.equal(animations[0].keyframes.at(-1).transform, 'none');
    let visibility;
    doc.addEventListener = (_, fn) => { visibility = fn; };
    doc.removeEventListener = () => { visibility = null; };
    motion.flip(container, () => { top += 40; });
    doc.visibilityState = 'hidden'; visibility?.();
    doc.visibilityState = 'visible'; visibility?.();
    frames.shift()();
    assert.equal(animations.length, 1, 'returning from a hidden tab must not replay old positions');
    doc.visibilityState = 'hidden';
    const result = motion.flip(container, () => { top += 40; return top; });
    assert.equal(result, 260, 'hidden mutations still take effect immediately');
    assert.equal(frames.length, 0, 'a hidden update must not queue a stale frame');
  } finally { globalThis.getComputedStyle = previous; }
});

test('moving a list surface never slides its rows back across adjacent controls', () => {
  const frames = [];
  let top = 0, moves = 0;
  const doc = { documentElement: {}, defaultView: { requestAnimationFrame: (fn) => frames.push(fn) } };
  const box = () => ({ top, left: 0, width: 100, height: 40 });
  const child = { ownerDocument: doc, isConnected: true, getClientRects: () => [{}],
    getBoundingClientRect: box, getAnimations: () => [], animate: () => { moves++; return {}; } };
  const container = { children: [child], ownerDocument: doc, isConnected: true, getBoundingClientRect: box };
  child.parentElement = container;
  const previous = globalThis.getComputedStyle;
  globalThis.getComputedStyle = () => ({ getPropertyValue: (key) => key.endsWith('-duration') ? '167ms' : 'linear' });
  try {
    motion.flip(container, () => { top += 100; });
    frames.shift()();
    assert.equal(moves, 0);
  } finally { globalThis.getComputedStyle = previous; }
});

test('a same-frame explicit exclusion overrides an earlier reorder', () => {
  const frames = [];
  let top = 100, moves = 0;
  const doc = { documentElement: {}, defaultView: { requestAnimationFrame: (fn) => frames.push(fn) } };
  const child = { ownerDocument: doc, isConnected: true, getClientRects: () => [{}],
    getBoundingClientRect: () => ({ top, left: 0 }), getAnimations: () => [],
    animate: () => { moves++; return {}; } };
  const container = { children: [child], ownerDocument: doc, isConnected: true,
    getBoundingClientRect: () => ({ top: 0, left: 0 }) };
  child.parentElement = container;
  const previous = globalThis.getComputedStyle;
  globalThis.getComputedStyle = () => ({ getPropertyValue: (key) => key.endsWith('-duration') ? '167ms' : 'linear' });
  try {
    motion.flip(container, () => { top += 40; }, { entry: 'reorder' });
    motion.flip(container, () => { top += 40; }, { entry: () => null });
    frames.shift()();
    assert.equal(moves, 0);
  } finally { globalThis.getComputedStyle = previous; }
});
