import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { whileVisible } from '../../js/ui/ticker.js';

const source = readFileSync(new URL('../../js/ui/stop-view.js', import.meta.url), 'utf8')
  .replace(/^import .*;\r?$/gm, '').replace(/^export /gm, '');

test('a stop confirmation countdown pauses while hidden and catches up on return', (t) => {
  t.mock.timers.enable({ apis: ['setInterval', 'setTimeout'] });
  let now = 0;
  let statusWrites = 0;
  let listener = null;
  const doc = {
    visibilityState: 'visible',
    addEventListener: (_type, fn) => { listener = fn; },
    removeEventListener: () => { listener = null; },
    show(visible) {
      doc.visibilityState = visible ? 'visible' : 'hidden';
      listener?.();
    },
  };
  const h = (tag, props = {}, ...children) => {
    props ??= {};
    const node = {
      tag, children, dataset: {}, attributes: {}, classList: { add() {} },
      setAttribute(name, value) { this.attributes[name] = value; },
      removeAttribute(name) { delete this.attributes[name]; },
    };
    if (props.class === 'answer-status stop-status') {
      let text = '';
      Object.defineProperty(node, 'textContent', {
        get: () => text,
        set(value) { text = value; statusWrites++; },
      });
    }
    Object.assign(node, props);
    return node;
  };
  const icon = () => ({});
  const EndSession = vm.runInNewContext(`${source}\nEndSession`, {
    h, icon, UPDATING: 'Updating', holdOf: () => ({ text: null, updating: false, drawn: false }),
    LINGER_READ_MS: 5000,
    whileVisible: (fn, ms) => whileVisible(fn, ms, doc),
    setInterval: globalThis.setInterval,
    clearInterval: globalThis.clearInterval,
    setTimeout: globalThis.setTimeout,
    clearTimeout: globalThis.clearTimeout,
  });
  const entry = {
    state: 'done', outcome: 'confirm', request: 'r1', reason: 'still working',
    detail: { confirm: 'token', confirmSeconds: 10 },
  };
  let watched;
  const service = {
    watch: (_hostId, _sessionId, fn) => { watched = fn; return () => {}; },
    get: () => entry,
  };
  const view = new EndSession({ stopping: { service, hostId: 'host', sessionId: 'session' }, now: () => now });
  watched(entry);
  const initialWrites = statusWrites;

  doc.show(false);
  now += 10_000;
  t.mock.timers.tick(10_000);
  assert.equal(statusWrites, initialWrites, 'hidden time does not redraw the countdown');

  doc.show(true);
  assert.match(view.els.status.textContent, /NOT confirmed/);
  assert.equal(statusWrites, initialWrites + 1, 'the expired countdown is drawn once on return');
  t.mock.timers.tick(1000);
  assert.equal(statusWrites, initialWrites + 1, 'an expired countdown leaves no timer behind');
  view.destroy();
});
