import { test } from 'node:test';
import assert from 'node:assert/strict';
import * as ticker from '../../js/ui/ticker.js';
import { watchPresses, enter } from '../../js/ui/motion.js';

const fire = (target, type, props = {}) => target.dispatchEvent(Object.assign(new Event(type), props));
const { whileVisible } = ticker;
function element() {
  const attributes = new Set();
  return {
    dataset: {}, value: '', classList: { add() {}, toggle() {} },
    addEventListener() {}, removeAttribute() {}, setAttribute() {}, replaceChildren() {},
    contains: () => false, querySelector: () => element(), querySelectorAll: () => [],
    toggleAttribute: (name, on) => on ? attributes.add(name) : attributes.delete(name),
    hasAttribute: (name) => attributes.has(name),
  };
}
function page() {
  return Object.assign(new EventTarget(), {
    visibilityState: 'visible', hidden: false, defaultView: new EventTarget(), documentElement: element(),
  });
}

test('pagehide alone suspends a ticker until a persisted visible pageshow, once, and stop removes lifecycle listeners', (t) => {
  t.mock.timers.enable({ apis: ['setInterval'] });
  const doc = page();
  let ticks = 0;
  const ticker = whileVisible(() => { ticks++; }, 250, doc);
  t.after(() => ticker.stop());
  t.mock.timers.tick(250);
  assert.equal(ticks, 1);
  fire(doc.defaultView, 'pagehide');
  t.mock.timers.tick(1000);
  assert.equal(ticks, 1, 'pagehide pauses even before visibilityState changes');
  fire(doc.defaultView, 'pageshow', { persisted: false });
  assert.equal(ticks, 1);
  doc.visibilityState = 'hidden';
  fire(doc.defaultView, 'pageshow', { persisted: true });
  assert.equal(ticks, 1, 'a cached page restored in the background stays paused');
  doc.visibilityState = 'visible';
  fire(doc, 'visibilitychange');
  fire(doc.defaultView, 'pageshow', { persisted: true });
  assert.equal(ticks, 2, 'return catches up once, not once per lifecycle event');
  t.mock.timers.tick(250);
  assert.equal(ticks, 3);
  fire(doc.defaultView, 'pagehide');
  ticker.stop();
  fire(doc.defaultView, 'pageshow', { persisted: true });
  t.mock.timers.tick(1000);
  assert.equal(ticks, 3);
  const next = whileVisible(() => { ticks++; }, 250, doc);
  t.after(() => next.stop());
  t.mock.timers.tick(250);
  assert.equal(ticks, 4, 'stopping the last hidden ticker must not leave the document stuck hidden');
});

test('pagehide alone pauses motion and blocks new motion until a persisted visible pageshow', (t) => {
  const doc = page();
  const animation = { playState: 'running', effect: { target: { isConnected: true } },
    pause() { this.playState = 'paused'; }, play() { this.playState = 'running'; } };
  doc.getAnimations = () => [animation];
  const previous = globalThis.getComputedStyle;
  globalThis.getComputedStyle = () => ({ getPropertyValue: (key) => key.endsWith('-duration') ? '100ms' : 'linear' });
  t.after(() => { globalThis.getComputedStyle = previous; });
  let starts = 0;
  const el = { isConnected: true, ownerDocument: doc, animate: () => { starts++; return {}; } };
  watchPresses(doc);
  fire(doc.defaultView, 'pagehide');
  assert.equal(doc.documentElement.hasAttribute('data-motion-hidden'), true);
  assert.equal(animation.playState, 'paused');
  assert.equal(enter(el, 'value'), null, 'pagehide-only updates must not start transient motion');
  assert.equal(starts, 0);
  fire(doc.defaultView, 'pageshow', { persisted: false });
  assert.equal(animation.playState, 'paused');
  doc.visibilityState = 'hidden';
  fire(doc.defaultView, 'pageshow', { persisted: true });
  assert.equal(animation.playState, 'paused');
  doc.visibilityState = 'visible';
  fire(doc, 'visibilitychange');
  assert.equal(doc.documentElement.hasAttribute('data-motion-hidden'), false);
  assert.equal(animation.playState, 'running');
  enter(el, 'value');
  assert.equal(starts, 1);
});
