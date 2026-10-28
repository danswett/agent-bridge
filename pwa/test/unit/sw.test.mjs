// The service worker's push and notificationclick handlers, run against a stubbed worker
// scope. Playwright's Chromium test (test/push.spec.mjs) delivers a real push to the real
// worker; this pins every case, including WebKit's, which a browser can't be made to show.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const pwa = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const scope = 'https://danswett.github.io/agent-bridge/app/';
const MACHINE = '53d1997f0a1b2c3d4e5f60718293a4b5';

function worker({ clients = [] } = {}) {
  const handlers = {};
  const shown = [];
  const opened = [];
  const self = {
    registration: { scope, showNotification: async (title, options) => { shown.push({ title, ...options }); } },
    clients: { matchAll: async () => clients, openWindow: async (u) => { opened.push(u); }, claim() {} },
    addEventListener: (type, fn) => { handlers[type] = fn; },
    skipWaiting() {},
  };
  vm.runInNewContext(readFileSync(path.join(pwa, 'sw.js'), 'utf8'), { self, location: new URL(scope), URL, caches: {}, fetch });
  const fire = async (type, event) => {
    let done;
    handlers[type]({ ...event, waitUntil: (p) => { done = p; } });
    await done;
  };
  return { fire, shown, opened };
}

// Objects made inside the worker's realm have its prototypes; compare their contents.
const plain = (x) => JSON.parse(JSON.stringify(x));

const push = (payload) => ({ data: payload === undefined ? null : { json: () => (typeof payload === 'string' ? JSON.parse(payload) : payload) } });

// What a host sends (docs/protocol/v1-sessions.md, The payload).
const notice = (kind, extra = {}) => ({
  web_push: 8030,
  notification: {
    title: 'Pick the session store',
    body: 'DEV · waiting on you\nWhich database?',
    navigate: `${scope}?host=${MACHINE}&session=s1&question=q1`,
    tag: `${MACHINE}:s1`,
    lang: 'en',
    ...(kind === 'resolved' ? { silent: true } : {}),
    data: { v: 1, kind, machine: MACHINE, session: 's1', question: kind === 'finished' ? undefined : 'q1', at: '2026-10-28T04:22:43.182Z' },
    ...extra,
  },
});

test('a question shows its title and body under the session\'s tag, alerting again, with where a tap goes', async () => {
  const sw = worker();
  await sw.fire('push', push(notice('question')));
  const [n] = sw.shown;
  assert.equal(n.title, 'Pick the session store');
  assert.equal(n.body, 'DEV · waiting on you\nWhich database?');
  assert.equal(n.tag, `${MACHINE}:s1`);
  assert.equal(n.renotify, true);
  assert.equal(n.silent, undefined);
  assert.deepEqual(plain(n.data), { url: `${scope}?host=${MACHINE}&session=s1&question=q1`, kind: 'question', machine: MACHINE, session: 's1', question: 'q1' });
});

test('an answered question replaces its notification quietly, and a finished turn alerts', async () => {
  const sw = worker();
  await sw.fire('push', push(notice('resolved', { body: 'DEV · no longer waiting' })));
  await sw.fire('push', push(notice('finished', { body: 'DEV · finished\nDone.' })));
  assert.deepEqual(sw.shown.map((n) => [n.data.kind, n.tag, n.silent ?? false, n.renotify]),
    [['resolved', `${MACHINE}:s1`, true, false], ['finished', `${MACHINE}:s1`, false, true]]);
});

test('a kind from a newer machine is shown as a question', async () => {
  const sw = worker();
  await sw.fire('push', push(notice('reminder')));
  assert.equal(sw.shown[0].data.kind, 'question');
  assert.equal(sw.shown[0].renotify, true);
});

test('a tap can never leave the app, whatever the payload says', async () => {
  const sw = worker();
  for (const navigate of ['https://evil.example/phish', 'https://danswett.github.io/other-app/', 'javascript:alert(1)', '//evil.example/x']) {
    await sw.fire('push', push(notice('question', { navigate })));
  }
  assert.deepEqual(sw.shown.map((n) => n.data.url), [scope, scope, scope, scope]);
});

test('an empty, unreadable or odd push still shows a notification, as iOS requires', async () => {
  const sw = worker();
  await sw.fire('push', push(undefined));
  await sw.fire('push', push('not json'));
  await sw.fire('push', push({ web_push: 8030, notification: { title: 42, body: null, tag: '', data: 'x' } }));
  assert.equal(sw.shown.length, 3);
  for (const n of sw.shown) {
    assert.equal(n.title, 'Agent Bridge');
    assert.equal(n.body, 'A session needs you.');
    assert.equal(n.tag, undefined, 'no tag, so no renotify either (which needs one)');
    assert.equal(n.renotify, undefined);
    assert.equal(n.data.url, scope);
  }
});

// A worker whose fetch and cache are recorded, for the caching tests.
function cachingWorker() {
  const handlers = {};
  const fetched = [];
  const added = [];
  const self = {
    registration: { scope },
    clients: { claim() {} },
    addEventListener: (type, fn) => { handlers[type] = fn; },
    skipWaiting() {},
  };
  const cache = { addAll: async (reqs) => { added.push(...reqs); }, put: async () => {} };
  const caches = { open: async () => cache, keys: async () => [], match: async () => undefined };
  const fetchStub = async (input, init) => { fetched.push({ input, init }); return { ok: true, clone() { return this; } }; };
  class Request { constructor(url, init) { Object.assign(this, { url, ...init }); } }
  vm.runInNewContext(readFileSync(path.join(pwa, 'sw.js'), 'utf8'), { self, location: new URL(scope), URL, caches, fetch: fetchStub, Request });
  return { handlers, fetched, added };
}

test('installing fills the shell from the network, never from a browser copy that may predate the deploy', async () => {
  const sw = cachingWorker();
  let done;
  sw.handlers.install({ waitUntil: (p) => { done = p; } });
  await done;
  assert.ok(sw.added.length > 0);
  assert.ok(sw.added.every((r) => r.cache === 'reload'), 'every shell file bypasses the HTTP cache');
});

test('every app file is checked with the server on each load, so a deploy shows on the next open', async () => {
  const sw = cachingWorker();
  let responded;
  sw.handlers.fetch({ request: { method: 'GET', url: `${scope}js/main.js` }, respondWith: (p) => { responded = p; } });
  await responded;
  assert.deepEqual(plain(sw.fetched), [{ input: `${scope}js/main.js`, init: { cache: 'no-cache', credentials: 'same-origin' } }]);
});

function tapped(data) {
  return { notification: { data, closed: false, close() { this.closed = true; } } };
}

test('a tap opens the app at the notice\'s link when it is not open', async () => {
  const sw = worker();
  const e = tapped({ url: `${scope}?host=${MACHINE}&session=s1&question=q1` });
  await sw.fire('notificationclick', e);
  assert.equal(e.notification.closed, true);
  assert.deepEqual(sw.opened, [`${scope}?host=${MACHINE}&session=s1&question=q1`]);
});

test('a tap brings an open app forward and tells it where to go, keeping its connection', async () => {
  const messages = [];
  let focused = 0;
  const elsewhere = { url: 'https://danswett.github.io/other-app/', postMessage: () => assert.fail('not this app'), focus: async () => assert.fail('not this app') };
  const app = { url: `${scope}?x=1`, postMessage: (m) => messages.push(m), focus: async () => { focused++; } };
  const sw = worker({ clients: [elsewhere, app] });
  await sw.fire('notificationclick', tapped({ url: `${scope}?host=${MACHINE}&session=s1&question=q1` }));
  assert.deepEqual(plain(messages), [{ type: 'agent-bridge.open', url: `${scope}?host=${MACHINE}&session=s1&question=q1` }]);
  assert.equal(focused, 1);
  assert.deepEqual(sw.opened, []);

  // Data that was tampered with still keeps the tap in the app.
  await sw.fire('notificationclick', tapped({ url: 'https://evil.example/' }));
  assert.equal(messages[1].url, scope);
});
