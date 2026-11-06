// The service worker's push and notificationclick handlers, run against a stubbed worker
// scope. Playwright's Chromium test (test/push.spec.mjs) delivers a real push to the real
// worker; this pins every case, including WebKit's, which a browser can't be made to show.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';
import { BRAND } from '../../js/brand.js';

const pwa = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const scope = 'https://danswett.github.io/agent-bridge/app/';
const MACHINE = '53d1997f0a1b2c3d4e5f60718293a4b5';

function worker({ clients = [] } = {}) {
  const handlers = {};
  const shown = [];
  const opened = [];
  // What is in the tray: shown, and not closed since; one per tag, as the browser keeps it.
  const open = [];
  const events = [];
  const self = {
    registration: {
      scope,
      showNotification: async (title, options) => {
        // Per the Notifications spec: a silent notification cannot renotify.
        if (options.silent && options.renotify) { throw new TypeError('silent and renotify are mutually exclusive'); }
        const n = { title, ...options, close: () => { events.push('close'); open.splice(open.indexOf(n), 1); } };
        await new Promise((r) => setImmediate(r));
        events.push('show');
        shown.push({ title, ...options });
        const at = options.tag === undefined ? -1 : open.findIndex((o) => o.tag === options.tag);
        if (at >= 0) { open.splice(at, 1); }
        open.push(n);
      },
      // A browser answers this asynchronously too: other pushes' work can land in between.
      getNotifications: async ({ tag } = {}) => {
        await new Promise((r) => setImmediate(r));
        return open.filter((o) => tag === undefined || o.tag === tag);
      },
    },
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
  // Dispatched and left running, as a browser does with pushes that arrive close together.
  const start = (type, event) => {
    let done;
    handlers[type]({ ...event, waitUntil: (p) => { done = p; } });
    return done;
  };
  return { fire, start, shown, opened, open, events };
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

// A push as the host sends it once it sends a class (#913).
const classed = (kind, cls, extra = {}) => {
  const base = notice(kind).notification;
  return { web_push: 8030, notification: { ...base, ...(kind === 'finished' && cls === 'done' ? { silent: true } : {}), ...extra, data: { ...base.data, ...(cls === undefined ? {} : { class: cls }) } } };
};

test('a done notification is silent and never alerts again, whatever the push says of itself', async () => {
  const sw = worker();
  await sw.fire('push', push(classed('finished', 'done', { silent: false })));
  await sw.fire('push', push(classed('finished', 'done', { silent: undefined })));
  assert.deepEqual(sw.shown.map((n) => [n.tag, n.silent, n.renotify]), [[`${MACHINE}:s1`, true, false], [`${MACHINE}:s1`, true, false]]);
  assert.equal(sw.open.length, 1, 'replaced in place, not stacked');
});

test('a push whose kind and class disagree takes the quieter reading: a finished that needs you never alerts as finished', async () => {
  const sw = worker();
  await sw.fire('push', push(classed('finished', 'needsYou', { silent: false })));
  await sw.fire('push', push(classed('question', 'done', { silent: false })));
  assert.deepEqual(sw.shown.map((n) => [n.silent, n.renotify]), [[true, false], [true, false]]);
});

test('a silent finished push shows rather than throwing on silent with renotify', async () => {
  const sw = worker();
  await sw.fire('push', push(notice('finished', { silent: true })));
  assert.equal(sw.shown.length, 1);
  assert.equal(sw.shown[0].silent, true);
  assert.equal(sw.shown[0].renotify, false);
});

test('an FYI leaves nothing in the tray, though it is shown first so the push is not left showing nothing', async () => {
  const sw = worker();
  await sw.fire('push', push(classed('question', 'fyi')));
  assert.equal(sw.shown.length, 1);
  assert.equal(sw.shown[0].silent, true);
  assert.deepEqual(sw.open, []);
});

test('an FYI takes the session\'s earlier notification away with it', async () => {
  const sw = worker();
  await sw.fire('push', push(classed('question', 'needsYou')));
  await sw.fire('push', push(classed('question', 'fyi')));
  assert.deepEqual(sw.open, []);
});

test('a question that needs you alerts again, and replaces the session\'s earlier notification', async () => {
  const sw = worker();
  await sw.fire('push', push(classed('finished', 'done')));
  await sw.fire('push', push(classed('question', 'needsYou')));
  assert.equal(sw.shown[1].renotify, true);
  assert.equal(sw.shown[1].silent, undefined);
  assert.deepEqual(sw.open.map((n) => n.data.kind), ['question']);
});

test('a question answered on any device is withdrawn: its notification is closed, and other sessions\' stay', async () => {
  const sw = worker();
  await sw.fire('push', push(classed('question', 'needsYou')));
  await sw.fire('push', push(classed('question', 'needsYou', { tag: `${MACHINE}:s2` })));
  await sw.fire('push', push(notice('resolved', { body: 'DEV · no longer waiting' })));
  assert.deepEqual(sw.open.map((n) => n.tag), [`${MACHINE}:s2`]);
});

test('a withdrawal still under way never closes a newer question that arrived for the same session', async () => {
  const sw = worker();
  await Promise.all([
    sw.start('push', push(notice('resolved', { body: 'DEV · no longer waiting' }))),
    sw.start('push', push(classed('question', 'needsYou', { body: 'DEV · waiting on you\nWhich port?' }))),
  ]);
  assert.deepEqual(sw.open.map((n) => [n.tag, n.data.kind, n.body]), [[`${MACHINE}:s1`, 'question', 'DEV · waiting on you\nWhich port?']]);
  assert.deepEqual(sw.events, ['show', 'close', 'show']);
});

test('an answered question with no tag to withdraw it by is shown and closed under one of its own', async () => {
  const sw = worker();
  await sw.fire('push', push(notice('resolved', { tag: '' })));
  assert.equal(sw.shown.length, 1);
  assert.deepEqual(sw.open, []);
});

test('a withdrawn notice says the session\'s own line while it is up, never a placeholder, and is closed only after it is shown', async () => {
  const sw = worker();
  await sw.fire('push', push(classed('question', 'fyi', { body: 'DEV · finished a side task' })));
  await sw.fire('push', push(notice('resolved', { body: 'DEV · no longer waiting' })));
  await sw.fire('push', push(notice('resolved', { body: undefined })));
  assert.deepEqual(sw.shown.map((n) => [n.body, n.silent, n.renotify]), [
    ['DEV · finished a side task', true, false],
    ['DEV · no longer waiting', true, false],
    ['Pick the session store', true, false],
  ]);
  assert.deepEqual(sw.events, ['show', 'close', 'show', 'close', 'show', 'close']);
});

test('a class from a newer machine, or of the wrong type, reads as none: the notice is as before', async () => {
  const sw = worker();
  for (const cls of ['urgent', 7, null, { a: 1 }]) {
    await sw.fire('push', push(classed('question', cls === null ? undefined : cls)));
  }
  assert.equal(sw.shown.length, 4);
  for (const n of sw.shown) {
    assert.equal(n.renotify, true);
    assert.equal(n.silent, undefined);
  }
  assert.equal(sw.open.length, 1);
});

test('every notification carries the app icon and the mark as its badge, for Android\'s status bar', async () => {
  const sw = worker();
  await sw.fire('push', push(notice('question')));
  await sw.fire('push', push(undefined));
  assert.deepEqual(sw.shown.map((n) => [n.icon, n.badge]), [['icons/icon-192.png', 'icons/badge-96.png'], ['icons/icon-192.png', 'icons/badge-96.png']]);
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
    assert.equal(n.title, BRAND.name);
    assert.equal(n.body, 'A session needs you.');
    assert.equal(n.tag, undefined, 'no tag, so no renotify either (which needs one)');
    assert.equal(n.renotify, undefined);
    assert.equal(n.data.url, scope);
  }
});

// A worker whose fetches are recorded, over caches held in memory: { name: { url: body } }, in
// the order the browser made them (caches.keys() keeps it).
function cachingWorker(stored = {}, { offline = false } = {}) {
  const handlers = {};
  const fetched = [];
  const added = [];
  const self = {
    registration: { scope },
    clients: { claim() {} },
    addEventListener: (type, fn) => { handlers[type] = fn; },
    skipWaiting() {},
  };
  const store = new Map(Object.entries(stored).map(([name, files]) => [name, new Map(Object.entries(files).map(([u, body]) => [u, { ok: true, body, json: async () => JSON.parse(body) }]))]));
  const key = (r) => new URL(typeof r === 'string' ? r : r.url, scope).href;
  const bare = (u) => u.split('?')[0];
  const open = (name) => {
    if (!store.has(name)) { store.set(name, new Map()); }
    const files = store.get(name);
    return {
      addAll: async (reqs) => {
        if (offline) { throw new TypeError('Failed to fetch'); }
        added.push(...reqs);
        for (const r of reqs) { files.set(key(r), { ok: true, body: 'network' }); }
      },
      put: async (r, res) => { files.set(key(r), res); },
      match: async (r) => files.get(key(r)),
    };
  };
  const caches = {
    open: async (name) => open(name),
    keys: async () => [...store.keys()],
    has: async (name) => store.has(name),
    delete: async (name) => store.delete(name),
    match: async (r, { ignoreSearch } = {}) => [...store.values()].map((f) => [...f].find(([u]) => (ignoreSearch ? bare(u) === bare(key(r)) : u === key(r)))?.[1]).find(Boolean),
  };
  const fetchStub = async (input, init) => { fetched.push({ input, init }); return { ok: true, body: 'network', clone() { return { ok: true, body: 'network' }; } }; };
  class Request { constructor(url, init) { Object.assign(this, { url, ...init }); } }
  class Response { constructor(body) { this.ok = true; this.body = body; } async json() { return JSON.parse(this.body); } }
  vm.runInNewContext(readFileSync(path.join(pwa, 'sw.js'), 'utf8'), { self, location: new URL(scope), URL, caches, fetch: fetchStub, Request, Response });
  // Fires a fetch for the URL; resolves to what the page was given, once the worker is done.
  const get = async (url, mode = 'cors') => {
    let responded;
    const extended = [];
    handlers.fetch({ request: { method: 'GET', url, mode }, respondWith: (p) => { responded = p; }, waitUntil: (p) => { extended.push(p); } });
    const res = await responded;
    while (extended.length) { await extended.shift(); }
    return res;
  };
  const kept = (name) => Object.fromEntries([...(store.get(name) ?? [])].map(([u, r]) => [u, r.body]));
  const fire = async (type) => {
    let done;
    handlers[type]({ waitUntil: (p) => { done = p; } });
    await done;
  };
  return { handlers, fetched, added, get, kept, fire, names: () => [...store.keys()].filter((n) => n !== 'agent-bridge-app') };
}

const CURRENT = 'agent-bridge-app-dev';

test('installing fills the shell from the network, never from a browser copy that may predate the deploy', async () => {
  const sw = cachingWorker();
  let done;
  sw.handlers.install({ waitUntil: (p) => { done = p; } });
  await done;
  assert.ok(sw.added.length > 0);
  assert.ok(sw.added.every((r) => r.cache === 'reload'), 'every shell file bypasses the HTTP cache');
});

test('a release\'s own files come from its cache, with nothing asked of the network, so a warm start waits on nothing', async () => {
  const sw = cachingWorker({ [CURRENT]: { [`${scope}app.css`]: 'kept', [scope]: 'page' } });
  assert.equal((await sw.get(`${scope}app.css`)).body, 'kept');
  // The page, at whatever address the app was opened.
  assert.equal((await sw.get(`${scope}?demo=1&host=${MACHINE}`, 'navigate')).body, 'page');
  assert.deepEqual(sw.fetched, []);
});

test('a file that is not the release\'s own is served as kept, and fetched again for next time', async () => {
  const sw = cachingWorker({ [CURRENT]: { [`${scope}js/ui/glyph.js`]: 'kept' } });
  assert.equal((await sw.get(`${scope}js/ui/glyph.js`)).body, 'kept');
  assert.deepEqual(plain(sw.fetched), [{ input: `${scope}js/ui/glyph.js`, init: { cache: 'no-cache', credentials: 'same-origin' } }]);
  assert.equal(sw.kept(CURRENT)[`${scope}js/ui/glyph.js`], 'network');
});

test('a page still open on the release before is given that release\'s files, never another\'s', async () => {
  const old = 'agent-bridge-app-0123456789abcdef';
  const sw = cachingWorker({ [old]: { [`${scope}bundle/demo-OLD.js`]: 'old chunk' }, [CURRENT]: {} });
  assert.equal((await sw.get(`${scope}bundle/demo-OLD.js`)).body, 'old chunk');
  assert.deepEqual(sw.fetched, []);
});

test('a new release keeps the one before it for pages still open on it, and drops older ones, never another app\'s', async () => {
  const sw = cachingWorker({ 'agent-bridge-app-a': {}, 's4-shell': {}, 'agent-bridge-app-b': {}, [CURRENT]: {} });
  await sw.fire('activate');
  assert.deepEqual(sw.names(), ['s4-shell', 'agent-bridge-app-b', CURRENT]);
});

test('after a rollback, the release kept is the one that was in control, not the one made last', async () => {
  // A, then B, then A again: this release replaces A, so pages still open on A keep their files.
  const record = { [`${scope}releases.json`]: JSON.stringify({ active: 'agent-bridge-app-a', before: 'agent-bridge-app-b' }) };
  const sw = cachingWorker({ 'agent-bridge-app-a': {}, 'agent-bridge-app-b': {}, [CURRENT]: {}, 'agent-bridge-app': record });
  await sw.fire('activate');
  assert.deepEqual(sw.names(), ['agent-bridge-app-a', CURRENT]);
  // A worker that changed only itself is the same release, so the one before stays the same.
  const again = cachingWorker({ 'agent-bridge-app-a': {}, [CURRENT]: {}, 'agent-bridge-app-z': {}, 'agent-bridge-app': { [`${scope}releases.json`]: JSON.stringify({ active: CURRENT, before: 'agent-bridge-app-a' }) } });
  await again.fire('activate');
  assert.deepEqual(again.names(), ['agent-bridge-app-a', CURRENT]);
});

test('an install that fails leaves a release already kept as it was, and drops one it only began', async () => {
  const kept = cachingWorker({ [CURRENT]: { [`${scope}app.css`]: 'kept' } }, { offline: true });
  await assert.rejects(kept.fire('install'));
  assert.equal(kept.kept(CURRENT)[`${scope}app.css`], 'kept', 'the worker in control still has its files');
  const begun = cachingWorker({}, { offline: true });
  await assert.rejects(begun.fire('install'));
  assert.deepEqual(begun.names(), []);
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
