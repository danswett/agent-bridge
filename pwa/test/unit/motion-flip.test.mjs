import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as motion from '../../js/ui/motion.js';

function surface(check) {
  const frames = [], animations = [], position = { top: 100, origin: 0 };
  const doc = { documentElement: {}, defaultView: { requestAnimationFrame: (fn) => frames.push(fn) } };
  const child = { ownerDocument: doc, isConnected: true, getClientRects: () => [{}],
    getBoundingClientRect: () => ({ top: position.top, left: 0, width: 100, height: 40 }), getAnimations: () => [],
    animate: (keyframes, options) => { const a = { keyframes, options }; animations.push(a); return a; } };
  const container = { children: [child], ownerDocument: doc, isConnected: true,
    getBoundingClientRect: () => ({ top: position.origin, left: 0 }) };
  child.parentElement = container;
  const previous = globalThis.getComputedStyle;
  globalThis.getComputedStyle = () => ({ getPropertyValue: (key) => key.endsWith('-duration') ? '167ms' : 'linear' });
  try { check({ frames, animations, position, doc, container }); }
  finally { globalThis.getComputedStyle = previous; }
}

test('same-frame list mutations take effect immediately and coalesce into one token-timed neighbour shift', () => surface(({ frames, animations, position, doc, container }) => {
  motion.flip(container, () => { position.top += 40; });
  motion.flip(container, () => { position.top += 40; });
  assert.equal(position.top, 180, 'the real layout must never wait for animation');
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
  motion.flip(container, () => { position.top += 40; });
  doc.visibilityState = 'hidden'; visibility?.();
  doc.visibilityState = 'visible'; visibility?.();
  frames.shift()();
  assert.equal(animations.length, 1, 'returning from a hidden tab must not replay old positions');
  doc.visibilityState = 'hidden';
  const result = motion.flip(container, () => { position.top += 40; return position.top; });
  assert.equal(result, 260, 'hidden mutations still take effect immediately');
  assert.equal(frames.length, 0, 'a hidden update must not queue a stale frame');
}));

test('moving a list surface never slides its rows back across adjacent controls', () => surface(({ frames, animations, position, container }) => {
  motion.flip(container, () => { position.top += 100; position.origin += 100; });
  frames.shift()();
  assert.equal(animations.length, 0);
}));

test('a same-frame explicit exclusion overrides an earlier reorder', () => surface(({ frames, animations, position, container }) => {
  motion.flip(container, () => { position.top += 40; }, { entry: 'reorder' });
  motion.flip(container, () => { position.top += 40; }, { entry: () => null });
  frames.shift()();
  assert.equal(animations.length, 0);
}));
