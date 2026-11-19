import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';
import * as auth from '../../js/auth-state.js';
import { readSession } from '../../js/protocol.js';

const source = readFileSync(new URL('../../js/ui/sign-in-view.js', import.meta.url), 'utf8')
  .replace(/^import .*;\r?$/gm, '').replace(/^export /gm, '');

function setup(t) {
  const doc = { activeElement: null };
  class Element {
    children = [];
    dataset = {};
    attrs = {};
    className = '';
    constructor(tag, props, children) {
      this.tag = tag;
      this.ownerDocument = doc;
      Object.assign(this, props);
      this.className = props?.class ?? '';
      this.replaceChildren(...children.flat().filter((c) => c != null));
    }
    classList = {
      contains: (name) => this.className.split(' ').includes(name),
      toggle: (name, force) => {
        const names = new Set(this.className.split(' ').filter(Boolean));
        if (force ?? !names.has(name)) { names.add(name); } else { names.delete(name); }
        this.className = [...names].join(' ');
      },
    };
    get firstChild() { return this.children[0]; }
    get childElementCount() { return this.children.length; }
    get nextSibling() { return this.parent?.children[this.parent.children.indexOf(this) + 1]; }
    get isConnected() { return doc.body === this || !!this.parent?.isConnected; }
    setAttribute(k, v) { this.attrs[k] = v; }
    addEventListener(k, fn) { this[`on${k}`] = fn; }
    append(...children) { for (const c of children) { this.insertBefore(c, null); } }
    replaceChildren(...children) { for (const c of this.children) { if (c instanceof Element) { c.parent = null; } } this.children = []; this.append(...children); }
    insertBefore(c, before) {
      if (c instanceof Element) { c.remove(); c.parent = this; }
      this.children.splice(before == null ? this.children.length : this.children.indexOf(before), 0, c);
    }
    remove() { if (this.parent) { this.parent.children.splice(this.parent.children.indexOf(this), 1); this.parent = null; } }
    contains(c) { return this === c || this.children.some((n) => n instanceof Element && n.contains(c)); }
    closest(tag) { return this.tag === tag ? this : this.parent?.closest(tag); }
    showModal() { this.open = true; }
    close() { this.open = false; }
    focus() { doc.activeElement = this; }
  }
  const h = (tag, props, ...children) => new Element(tag, props, children);
  doc.body = h('body');
  const container = h('div');
  doc.body.append(container);
  const find = (name, root = doc.body) => root.classList.contains(name) ? root
    : root.children.filter((c) => c instanceof Element).map((c) => find(name, c)).find(Boolean);
  let now = Date.parse('2026-11-05T00:45:00Z');
  let changed;
  const session = readSession({ id: 's', agent: 'copilot', title: 'Session', status: 'idle',
    attention: { kind: 'signInNeeded', agent: 'copilot', signIn: 'phone' } });
  const link = { store: { list: () => [session] }, machine: {}, capabilities: ['sessions.signin'] };
  const hosts = {
    sessions: { onChange(fn) { changed = fn; return () => {}; } },
    onChange: () => () => {}, shownIds: () => ['m'], link: () => link, nameOf: () => 'Machine',
    route: () => ({ client: { request: async () => ({ outcome: 'started', flow: 'f' }) } }),
  };
  const View = vm.runInNewContext(`${source}\nSignInBanners`, {
    ...auth, h, glyph: () => h('span'), document: doc, queueMicrotask,
    whileVisible: () => ({ stop() {} }), setTimeout: () => 1, clearTimeout() {},
  });
  const view = new View(container, hosts, { now: () => now });
  t.after(() => view.stop());
  const sendCode = async (uri = 'https://github.com/login/device') => {
    changed({ type: 'signin', hostId: 'm', ev: 'signin.code', data: {
      flow: 'f', agent: 'copilot', userCode: 'WDJB-MJHT', verificationUri: uri,
      expiresAt: new Date(now + 60000).toISOString(),
    } });
    await Promise.resolve();
  };
  return { find, doc, view, sendCode, expire() { now += 60000; view.render(); } };
}

for (const state of ['expired', 'unshowable']) {
  test(`${state} code fills Cancel, keeps Try again secondary, and retry restores the code-shown rule`, async (t) => {
    const s = setup(t);
    await s.find('sign-in-start').onclick({ detail: 0 });
    await s.sendCode();
    assert.equal(s.find('sign-in-copy').classList.contains('secondary'), false);
    const cancel = s.find('sign-in-cancel');
    assert.equal(cancel.classList.contains('secondary'), true);
    s.find('sign-in-copy').focus();
    if (state === 'expired') { s.expire(); } else { await s.sendCode('https://evil.example/device'); }
    assert.ok(s.find(state === 'expired' ? 'sign-in-expired' : 'sign-in-refused'));
    assert.equal(s.find('sign-in-code'), undefined);
    assert.equal(cancel.classList.contains('secondary'), false, 'Cancel is filled when no code can be shown');
    assert.equal(s.find('sign-in-retry').classList.contains('secondary'), true);
    assert.equal(s.doc.activeElement === cancel, true, 'focus returns to Cancel when the code controls disappear');
    await s.find('sign-in-retry').onclick({ detail: 0 });
    assert.equal(cancel.classList.contains('secondary'), true, 'starting keeps its existing emphasis');
    await s.sendCode();
    assert.equal(s.find('sign-in-copy').classList.contains('secondary'), false);
    assert.equal(cancel.classList.contains('secondary'), true);
  });
}

test('Enter defaults to Cancel in an expired sheet without taking Try again own Enter', async (t) => {
  const s = setup(t);
  await s.find('sign-in-start').onclick({ detail: 0 });
  await s.sendCode();
  s.expire();
  const dialog = s.find('sign-in-sheet');
  const retry = s.find('sign-in-retry');
  let prevented = false;
  const enter = (target) => dialog.onkeydown?.({ key: 'Enter', target, preventDefault() { prevented = true; } });
  retry.focus();
  enter(retry);
  assert.equal(prevented, false);
  assert.equal(dialog.open, true);
  dialog.focus();
  enter(dialog);
  assert.equal(dialog.open, false, 'Enter on the sheet cancels the expired sign-in');
  assert.equal(prevented, true);
});
