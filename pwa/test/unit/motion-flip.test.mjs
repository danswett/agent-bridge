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
    const container = { children: [child], ownerDocument: doc, isConnected: true };
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
  } finally { globalThis.getComputedStyle = previous; }
});
