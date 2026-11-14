import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import { SessionOrder, orderKey } from '../../js/session-order.js';
import { RecentOrder } from '../../js/recent-order.js';
import { ReadDwell, DWELL_MS } from '../../js/read-dwell.js';

const document = { activeElement: null, addEventListener() {} };

class Element {
  children = [];
  dataset = {};
  get ownerDocument() { return document; }
  get firstChild() { return this.children[0] ?? null; }
  get nextSibling() { return this.parent?.children[this.parent.children.indexOf(this) + 1] ?? null; }
  append(child) { this.insertBefore(child, null); }
  insertBefore(child, next) {
    child.remove();
    this.children.splice(next ? this.children.indexOf(next) : this.children.length, 0, child);
    child.parent = this;
  }
  remove() {
    if (this.parent) { this.parent.children.splice(this.parent.children.indexOf(this), 1); this.parent = null; }
  }
  contains() { return false; }
  closest() { return null; }
  querySelectorAll() { return this.children; }
  getClientRects() { return []; }
  after() {}
  addEventListener() {}
  setAttribute() {}
}

class Card {
  element = new Element();
  view = new Element();
  els = { open: new Element() };
  constructor(session) { this.session = session; }
  update(session) { this.session = session; }
  setSelected(value) { this.selected = value; }
  setUnread(value) { this.unread = value; }
  setPinned() {}
  setStale() {}
}

// Execute the real view with inert rendering dependencies, without a browser or a copied draw loop.
const source = readFileSync(new URL('../../js/ui/views.js', import.meta.url), 'utf8')
  .replace(/^import .*;\r?$/gm, '').replace(/^export /gm, '');
const SessionListView = vm.runInNewContext(`${source}\nSessionListView`, {
  h: () => new Element(), SessionCard: Card, RowMenu: class {}, SessionOrder, RecentOrder, ReadDwell,
  whileVisible() {}, document, queueMicrotask, AbortController,
  flip: (_container, mutate) => mutate(),
  setTimeout: (...args) => globalThis.setTimeout(...args),
  clearTimeout: (handle) => globalThis.clearTimeout(handle),
}, { filename: 'views.js' });

const tile = vm.runInNewContext(`${source}\nmachineTile`, {
  h: (tag, props, ...children) => ({ tag, props, children: children.flat(Infinity).filter((c) => c != null), style: {} }),
  icon: () => null,
}, { filename: 'views.js' });
const elements = (node, tag) => typeof node !== 'object' || node === null ? []
  : [...(node.tag === tag ? [node] : []), ...(node.children ?? []).flatMap((child) => elements(child, tag))];
const runningTile = (sessions) => tile({ name: 'Test machine', raw: { sessions: '3 sessions' }, sessions, more: [], expanded: true });

test('the Fleet Running disclosure lists the existing session titles without interpreting them', () => {
  const titles = ['Build service', '<ready> & tests', ''];
  const rows = elements(runningTile(titles.map((title, i) => ({ id: `running-${i}`, title }))), 'li');
  assert.deepEqual(rows.map((row) => row.children.join('')), ['Build service', '<ready> & tests', 'Untitled session']);
});

for (const [name, sessions] of [['missing', undefined], ['empty', []]]) {
  test(`the Fleet Running disclosure invents no list or rows when session data is ${name}`, () => {
    const drawn = runningTile(sessions);
    assert.equal(elements(drawn, 'ul').length, 0);
    assert.equal(elements(drawn, 'li').length, 0);
  });
}

for (const pinned of [false, true]) {
  test(`same-turn redraws do not restart the selected ${pinned ? 'pinned' : 'unpinned'} row's read dwell`, async (t) => {
    t.mock.timers.enable({ apis: ['setTimeout'] });
    const order = new SessionOrder({ storage: { getItem: () => null, setItem() {} } });
    const key = orderKey('h', 's');
    let session = { id: 's', title: 'Session', updatedAt: '2026-11-10T00:00:00Z', activity: { responseTurn: 'seen' } };
    let changed;
    const sessions = { list: () => [{ hostId: 'h', session }], onChange: (fn) => { changed = fn; } };
    const view = new SessionListView(new Element(), sessions, { order, awake: () => true, timers: globalThis });
    if (pinned) { order.setPinned(key, true); }
    view.render();
    const card = view.select(key);
    await Promise.resolve();
    const upsert = async (change) => {
      session = { ...session, ...change, activity: { ...session.activity, ...change.activity } };
      changed({ type: 'upsert', hostId: 'h', id: 's' });
      await Promise.resolve();
    };
    await upsert({ activity: { responseTurn: 'new-reply' } });
    t.mock.timers.tick(DWELL_MS / 2);
    await upsert({ updatedAt: '2026-11-10T00:00:01Z', status: 'working', activity: { currentTool: { name: 'test' } } });
    t.mock.timers.tick(DWELL_MS / 2 - 1);
    assert.equal(card.unread, true);
    t.mock.timers.tick(1);
    assert.equal(card.unread, false, 'the original one-second dwell must finish despite the same-turn redraw');
    assert.equal(order.unread(key, 'new-reply'), false);

    await upsert({ activity: { responseTurn: 'unseen-reply' } });
    t.mock.timers.tick(DWELL_MS / 2);
    view.select(null);
    await Promise.resolve();
    t.mock.timers.tick(DWELL_MS);
    assert.equal(order.unread(key, 'unseen-reply'), true, 'leaving the selected session still cancels its dwell');
  });
}
