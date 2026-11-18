import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import * as ticker from '../../js/ui/ticker.js';
import { watchPresses, enter } from '../../js/ui/motion.js';
import { LaunchFollow } from '../../js/launch-follow.js';
import { ASK, ALLOW_ALL, MAX_PROMPT } from '../../js/launch.js';

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
  fire(doc, 'visibilitychange');
  t.mock.timers.tick(1000);
  assert.equal(ticks, 1, 'a visible visibilityState must not override pagehide');
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
  fire(doc, 'visibilitychange');
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

const launchSource = readFileSync(new URL('../../js/ui/launch-view.js', import.meta.url), 'utf8')
  .replace(/^import .*;\r?$/gm, '').replace(/^export /gm, '');
for (const away of [1000, 16000]) {
  test(`pagehide suspends launch probes and jumps; persisted pageshow ${away < 15000 ? 'resumes' : 'expires'} the original jump deadline`, (t) => {
    t.mock.timers.enable({ apis: ['setInterval', 'Date'], now: 0 });
    const doc = page();
    const state = { reads: 0, jumps: 0, entry: null };
    const LaunchPanel = vm.runInNewContext(`${launchSource}\nLaunchPanel`, {
      h: (_tag, props) => Object.assign(element(), props), document: doc,
      LaunchFollow, ASK, ALLOW_ALL, MAX_PROMPT, AbortController, TextEncoder, Date, ...ticker,
      setTimeout, clearTimeout,
      whileVisible: (fn, ms) => whileVisible(fn, ms, doc),
    });
    const panel = new LaunchPanel(element(), {
      service: { watch: (_id, fn) => { state.settle = fn; return () => {}; }, get: () => state.entry },
      jump: () => { state.jumps++; return false; },
    });
    t.after(() => panel.clear());
    panel.setTarget({ hostId: 'target', load: () => new Promise(() => {}) });
    panel.setOpen(true);
    panel.probeOthers([{ hostId: 'other', load: async () => { state.reads++; return { trust: [] }; } }]);
    state.entry = { state: 'done', outcome: 'started', hostId: 'target', request: 'request', detail: { session: 'session' } };
    state.settle(state.entry);
    assert.equal(state.jumps, 1);
    fire(doc.defaultView, 'pagehide');
    t.mock.timers.tick(away);
    assert.equal(state.jumps, 1, 'no jump callback while pagehidden');
    fire(doc.defaultView, 'pageshow', { persisted: true });
    assert.equal(state.jumps, away < 15000 ? 2 : 1);
    assert.equal(state.reads, 2, 'probes refresh once on return');
    fire(doc.defaultView, 'pageshow', { persisted: true });
    assert.equal(state.reads, 2);
    t.mock.timers.tick(14001);
    assert.equal(state.jumps, away < 15000 ? 2 : 1, 'return never grants another 15 seconds');
  });
}
