import { test } from 'node:test';
import assert from 'node:assert/strict';
import { assertLoopingAnimation } from '../animation-assertions.mjs';
import { watchPresses } from '../../js/ui/motion.js';

test('a paused animation-less stub fails the indicator presence assertion', () => {
  const stub = { animationName: 'none', animationDuration: '0s',
    animationIterationCount: '1', animationPlayState: 'paused' };
  for (const name of ['glyph-turn', 'spin']) {
    assert.throws(() => assertLoopingAnimation(stub, name), /the expected animation must exist/);
    assert.throws(() => assertLoopingAnimation({ ...stub, animationName: name,
      animationDuration: '1s', animationIterationCount: 'infinite', animationNames: [] }, name),
    /the browser must instantiate the animation/);
  }
});

function surface() {
  const doc = new EventTarget();
  doc.visibilityState = 'visible';
  doc.defaultView = new EventTarget();
  doc.documentElement = { toggleAttribute: (_, hidden) => { doc.motionHidden = hidden; } };
  const target = { isConnected: true };
  const animation = { effect: { target }, playState: 'running',
    pause() { this.playState = 'paused'; }, play() { this.playState = 'running'; } };
  doc.getAnimations = () => target.isConnected ? [animation] : [];
  watchPresses(doc);
  const show = (visible) => {
    doc.visibilityState = visible ? 'visible' : 'hidden';
    doc.dispatchEvent(new Event('visibilitychange'));
  };
  return { doc, target, animation, show };
}

test('motion resumes after pagehide and a visible visibilitychange without persisted pageshow', () => {
  const { doc, animation, show } = surface();
  doc.defaultView.dispatchEvent(new Event('pagehide'));
  assert.equal(animation.playState, 'paused');
  assert.equal(doc.motionHidden, true);
  doc.defaultView.dispatchEvent(new Event('pageshow'));
  assert.equal(animation.playState, 'paused', 'a non-persisted pageshow does not release pagehide');
  show(true);
  assert.equal(doc.motionHidden, false);
  assert.equal(animation.playState, 'running');
});

test('motion detached and reconnected while hidden resumes on return', () => {
  const { target, animation, show } = surface();
  show(false);
  target.isConnected = false;
  show(false);
  target.isConnected = true;
  show(true);
  assert.equal(animation.playState, 'running');
});

test('motion is not stranded if its target is detached when visibility returns', () => {
  const { target, animation, show } = surface();
  show(false);
  target.isConnected = false;
  show(true);
  target.isConnected = true;
  assert.equal(animation.playState, 'running');
});

test('motion cancelled while hidden is not restarted on return', () => {
  const { animation, show } = surface();
  show(false);
  animation.playState = 'idle';
  show(true);
  assert.equal(animation.playState, 'idle');
});
