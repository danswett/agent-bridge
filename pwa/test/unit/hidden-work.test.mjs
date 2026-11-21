import { test } from 'node:test';
import assert from 'node:assert/strict';
import { assertLoopingAnimation } from '../animation-assertions.mjs';
import { leave, watchPresses } from '../../js/ui/motion.js';
import { indicatorStyle, readFont } from '../hidden-work-fixture.mjs';

test('the hidden-work fixture loads the shipped sans font', async () => {
  const font = await readFont('/design/fonts/SlipgatesSans.woff2');
  assert.equal(font.subarray(0, 4).toString(), 'wOF2', 'the fixture must serve a real WOFF2 asset');
});

test('the hidden-work fixture rejects a missing font instead of using a fallback', async () => {
  await assert.rejects(readFont('/design/fonts/Missing.woff2'), { code: 'ENOENT' });
});

test('the indicator observes a pseudo-element animation rather than passing with an empty list', (t) => {
  const descriptor = Object.getOwnPropertyDescriptor(globalThis, 'getComputedStyle');
  Object.defineProperty(globalThis, 'getComputedStyle', { configurable: true, value: (_, pseudo) => {
    assert.equal(pseudo, '::after');
    return { animationName: 'spin', animationDuration: '1s',
      animationIterationCount: 'infinite', animationPlayState: 'running' };
  } });
  t.after(() => {
    if (descriptor) { Object.defineProperty(globalThis, 'getComputedStyle', descriptor); }
    else { delete globalThis.getComputedStyle; }
  });
  const el = { getAnimations: (options) => options?.subtree ? [{ animationName: 'spin' }] : [] };
  const style = indicatorStyle(el, '::after');
  assertLoopingAnimation(style, 'spin');
  assert.ok(style.animationNames.length > 0, 'at least one animation must be observed');
});

test('a paused animation-less stub fails the indicator presence assertion', () => {
  const stub = { animationName: 'none', animationDuration: '0s',
    animationIterationCount: '1', animationPlayState: 'paused' };
  for (const name of ['glyph-turn', 'spin']) {
    assert.throws(() => assertLoopingAnimation(stub, name), /the expected animation must exist/);
    assert.throws(() => assertLoopingAnimation({ ...stub, animationName: name,
      animationDuration: '1s', animationIterationCount: 'infinite', animationNames: [] }, name),
    /at least one browser animation must be observed/);
  }
});

function surface(t) {
  const doc = new EventTarget();
  doc.visibilityState = 'visible';
  doc.defaultView = new EventTarget();
  const listeners = [];
  for (const target of [doc, doc.defaultView]) {
    target.addEventListener = (type, listener, options) => {
      EventTarget.prototype.addEventListener.call(target, type, listener, options);
      listeners.push(() => target.removeEventListener(type, listener, options));
    };
  }
  t.after(() => { for (const remove of listeners) { remove(); } });
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

test('motion resumes after pagehide and a visible visibilitychange without persisted pageshow', (t) => {
  const { doc, animation, show } = surface(t);
  doc.defaultView.dispatchEvent(new Event('pagehide'));
  assert.equal(animation.playState, 'paused');
  assert.equal(doc.motionHidden, true);
  doc.defaultView.dispatchEvent(new Event('pageshow'));
  assert.equal(animation.playState, 'paused', 'a non-persisted pageshow does not release pagehide');
  show(true);
  assert.equal(doc.motionHidden, false);
  assert.equal(animation.playState, 'running');
});

test('motion detached and reconnected while hidden resumes on return', (t) => {
  const { target, animation, show } = surface(t);
  show(false);
  target.isConnected = false;
  show(false);
  target.isConnected = true;
  show(true);
  assert.equal(animation.playState, 'running');
});

test('detached paused motion stays paused until reconnected and visible, including a second hide', (t) => {
  const { target, animation, show } = surface(t);
  show(false);
  target.isConnected = false;
  show(true);
  assert.equal(animation.playState, 'paused', 'a detached target must not resume');
  show(false);
  target.isConnected = true;
  assert.equal(animation.playState, 'paused', 'hidden reattachment must not run motion');
  show(true);
  assert.equal(animation.playState, 'running');
});

test('motion cancelled while hidden is not restarted on return', (t) => {
  const { animation, show } = surface(t);
  show(false);
  animation.playState = 'idle';
  show(true);
  assert.equal(animation.playState, 'idle');
});

test('a swept leaving animation is cancelled and released while hidden, never resumed after reattachment', (t) => {
  const { doc, animation, show } = surface(t);
  const descriptor = Object.getOwnPropertyDescriptor(globalThis, 'getComputedStyle');
  Object.defineProperty(globalThis, 'getComputedStyle', { configurable: true, value: () => ({
    position: 'fixed', getPropertyValue: (key) => key.endsWith('-duration') ? '60000ms' : '',
  }) });
  t.after(() => {
    if (descriptor) { Object.defineProperty(globalThis, 'getComputedStyle', descriptor); }
    else { delete globalThis.getComputedStyle; }
  });
  Object.assign(doc.defaultView, { innerWidth: 100, innerHeight: 100 });
  const host = { ownerDocument: doc, isConnected: false, style: { setProperty() {} },
    setAttribute() {}, attachShadow: () => ({ append() {} }),
    remove() { this.isConnected = false; }, animate: () => animation };
  doc.body = { attachShadow() {}, append: (el) => { el.isConnected = true; } };
  doc.createElement = () => host;
  animation.effect.target = host;
  animation.finished = new Promise(() => {});
  animation.cancel = () => { animation.playState = 'idle'; };
  doc.getAnimations = () => host.isConnected ? [animation] : [];
  const source = { ownerDocument: doc, isConnected: true,
    getBoundingClientRect: () => ({ left: 0, top: 0, right: 50, bottom: 50, width: 50, height: 50 }) };
  assert.equal(leave(source, 'listItem'), animation);
  show(false);
  assert.equal(host.isConnected, false, 'hiding must sweep the leaving copy');
  assert.equal(animation.playState, 'idle', 'discarded WAAPI motion must be cancelled');
  let reads = 0;
  Object.defineProperty(animation, 'playState', { configurable: true,
    get() { reads++; return 'idle'; } });
  show(true);
  assert.equal(reads, 0, 'the paused Set must release the swept animation before returning');
  Object.defineProperty(animation, 'playState', { configurable: true, writable: true, value: 'idle' });
  doc.body.append(host);
  show(true);
  assert.equal(animation.playState, 'idle', 'reattaching a discarded copy must not resurrect its fade');
});
