import assert from 'node:assert/strict';

export function assertLoopingAnimation(style, name) {
  assert.equal(style.animationName, name, 'the expected animation must exist');
  assert.ok(parseFloat(style.animationDuration) > 0, 'the animation must have a duration');
  assert.equal(style.animationIterationCount, 'infinite', 'the indicator must loop');
  assert.ok(style.animationNames.length > 0, 'at least one browser animation must be observed');
  assert.ok(style.animationNames.includes(name), 'the browser must instantiate the animation');
}
