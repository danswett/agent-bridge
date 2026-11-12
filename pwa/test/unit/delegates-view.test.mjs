import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import * as attribution from '../../js/attribution.js';
import { clock, localTime } from '../../js/ui/time.js';

class Element {
  children = [];
  dataset = {};
  attrs = {};
  textContent = '';
  hidden = false;
  onscreen = true;
  constructor(tag, props, children = []) {
    this.tag = tag;
    Object.assign(this, props);
    this.children = children.flat().filter(Boolean);
  }
  setAttribute(k, v) { this.attrs[k] = v; }
  removeAttribute(k) { delete this.attrs[k]; }
  replaceChildren(...children) { this.children = children; }
  getClientRects() { return this.onscreen && !this.hidden ? [{}] : []; }
  contains() { return false; }
}

const source = readFileSync(new URL('../../js/ui/delegates-view.js', import.meta.url), 'utf8')
  .replace(/^import .*;\r?$/gm, '').replace(/^export /gm, '');
const START = Date.parse('2026-11-12T18:00:00Z');
const agent = { id: 'delegate', name: 'Run tests', status: 'working', startedAt: new Date(START).toISOString(), line: 'Last known line' };
const session = { subagents: [agent] };
const flush = async () => { await Promise.resolve(); await Promise.resolve(); };

function setup(t, hidden = false) {
  let now = START + 60000;
  let next = 0;
  let fired = 0;
  const pending = new Map();
  const listeners = new Set();
  const doc = {
    hidden, activeElement: null,
    addEventListener(type, fn) { assert.equal(type, 'visibilitychange'); listeners.add(fn); },
    removeEventListener(type, fn) { listeners.delete(fn); },
  };
  const h = (tag, props, ...children) => new Element(tag, props, children);
  const Detail = vm.runInNewContext(`${source}\nDelegateDetail`, {
    ...attribution, clock, localTime, document: doc,
    Date: class extends Date { static now() { return now; } },
    h, icon: () => h('svg'), glyph: () => h('span'), setGlyph() {},
    renderMarkdown: (text) => h('p', { textContent: text }),
    setTimeout(fn, ms) { const id = ++next; pending.set(id, { fn, due: now + ms }); return id; },
    clearTimeout(id) { pending.delete(id); },
  });
  let calls = 0;
  let fetch = async () => ({ ...agent, response: 'Last fetched words' });
  const view = new Detail({ fetchDelegate: () => { calls++; return fetch(); } });
  t.after(() => view.dispose());
  const tick = (ms) => {
    const until = now + ms;
    for (;;) {
      const due = [...pending].filter(([, timer]) => timer.due <= until).sort((a, b) => a[1].due - b[1].due)[0];
      if (!due) { break; }
      pending.delete(due[0]); now = due[1].due; fired++; due[1].fn();
    }
    now = until;
  };
  const show = (visible) => { doc.hidden = !visible; for (const fn of [...listeners]) { fn(); } };
  return { view, pending, listeners, tick, show, get calls() { return calls; }, get fired() { return fired; }, set fetch(fn) { fetch = fn; } };
}

test('a delegate selected while hidden starts no timer and fetches once immediately on return', async (t) => {
  const s = setup(t, true);
  s.view.select(agent.id, session);
  assert.equal(s.pending.size, 0);
  s.tick(600000);
  assert.equal(s.fired, 0);
  assert.equal(s.calls, 0);
  s.show(true);
  assert.equal(s.calls, 1);
  await flush();
  assert.equal(s.pending.size, 1);
  s.tick(9999);
  assert.equal(s.calls, 1);
  s.tick(1);
  assert.equal(s.calls, 2);
});

test('return keeps last words and updates age before its single refresh resolves, including failure', async (t) => {
  const s = setup(t);
  s.view.select(agent.id, session);
  s.tick(0);
  await flush();
  const words = s.view.els.latest.children[0];
  const status = () => s.view.els.props.children.find((row) => row.dataset.fact === 'status').children[1].children[0];
  assert.equal(status(), 'Working · 1m');
  s.show(false);
  assert.equal(s.pending.size, 0);
  const fired = s.fired;
  s.tick(600000);
  assert.equal(s.fired, fired, 'hidden time fires no callbacks, not even re-arming callbacks');
  assert.equal(s.calls, 1);
  let reject;
  s.fetch = () => new Promise((resolve, fail) => { reject = fail; });
  s.show(true);
  assert.equal(s.calls, 2, 'the refresh starts in the visibility event, without a timer');
  assert.equal(status(), 'Working · 11m', 'age catches up before the network response');
  assert.equal(s.view.els.latest.children[0], words, 'the existing content node stays in place');
  assert.equal(s.view.els.latestBlock.hidden, false);
  assert.equal(s.view.els.status.hidden, true, 'no loading state replaces the last data');
  reject(new Error('reconnecting'));
  await flush();
  assert.equal(s.view.els.latest.children[0], words);
  assert.equal(s.view.els.status.className, 'note warn');
  assert.match(s.view.els.status.textContent, /reconnecting/);
  assert.equal(s.pending.size, 1);
});

test('a fetch settling after hide neither follows queued changes nor arms a poll', async (t) => {
  const s = setup(t);
  let resolve;
  s.fetch = () => new Promise((done) => { resolve = done; });
  s.view.select(agent.id, session);
  s.tick(0);
  s.view.update({ subagents: [{ ...agent, line: 'Changed while loading' }] });
  s.tick(2000);
  s.show(false);
  resolve({ ...agent, response: 'Fetched words' });
  await flush();
  assert.equal(s.calls, 1);
  assert.equal(s.pending.size, 0);
  s.tick(600000);
  assert.equal(s.calls, 1);
  s.show(true);
  assert.equal(s.calls, 2);
});

test('a queued reload cancels a later change timer so hide leaves no orphan callback', async (t) => {
  const s = setup(t);
  let resolve;
  s.fetch = () => new Promise((done) => { resolve = done; });
  s.view.select(agent.id, session);
  s.tick(0);
  s.view.update({ subagents: [{ ...agent, line: 'First change' }] });
  s.tick(2000);
  s.view.update({ subagents: [{ ...agent, line: 'Second change' }] });
  s.fetch = async () => agent;
  resolve(agent);
  await flush();
  assert.equal(s.calls, 2);
  assert.equal(s.pending.size, 1, 'only the resumed poll owns a timer');
  s.show(false);
  assert.equal(s.pending.size, 0);
  const fired = s.fired;
  s.tick(600000);
  assert.equal(s.fired, fired);
});

test('an offscreen delegate leaves no polling timer and catches up when its pane is shown', async (t) => {
  const s = setup(t);
  s.view.select(agent.id, session);
  s.view.element.onscreen = false;
  s.tick(0);
  assert.equal(s.pending.size, 0);
  s.tick(600000);
  assert.equal(s.calls, 0);
  s.view.element.onscreen = true;
  s.view.shown();
  s.tick(0);
  await flush();
  assert.equal(s.calls, 1);
  assert.equal(s.pending.size, 1);
});

test('disposing removes visibility work and a hidden in-flight fetch cannot restart it', async (t) => {
  const s = setup(t);
  let resolve;
  s.fetch = () => new Promise((done) => { resolve = done; });
  s.view.select(agent.id, session);
  s.tick(0);
  s.show(false);
  s.view.dispose();
  resolve(agent);
  await flush();
  assert.equal(s.listeners.size, 0);
  s.show(true);
  s.tick(600000);
  assert.equal(s.calls, 1);
  assert.equal(s.pending.size, 0);
});
