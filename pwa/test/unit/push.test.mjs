import { test } from 'node:test';
import assert from 'node:assert/strict';
import { PushRegistrar, base64UrlToBytes, closeNotices, closeReadNotices, closeStaleNotices, fetchVapidKey, noticeTag, pushSupport } from '../../js/push.js';
import { SessionsStore } from '../../js/sessions-store.js';
import { Reading } from '../../js/reading.js';
import { deviceName } from '../../js/identity.js';
import { BRAND } from '../../js/brand.js';

const IPHONE = 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.4 Mobile/15E148 Safari/604.1';
const IPAD_AS_MAC = 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.4 Safari/605.1.15';
const ANDROID = 'Mozilla/5.0 (Linux; Android 15) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Mobile Safari/537.36';
const KEY = 'BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4';

function browser({ ua = ANDROID, standalone = false, touch = 0, permission = 'default', push = true } = {}) {
  const win = {
    navigator: { userAgent: ua, maxTouchPoints: touch, serviceWorker: {}, standalone },
    matchMedia: (q) => ({ matches: standalone && q === '(display-mode: standalone)' }),
    Notification: { permission, requestPermission: async () => win.Notification.answer ?? 'granted' },
  };
  if (push) { win.PushManager = function PushManager() {}; }
  return win;
}

test('iOS offers notifications only to the app opened from the Home Screen, and says so', () => {
  assert.equal(pushSupport(browser({ ua: IPHONE, push: false })), 'install');
  assert.equal(pushSupport(browser({ ua: IPAD_AS_MAC, touch: 5, push: false })), 'install');
  assert.equal(pushSupport(browser({ ua: IPHONE, standalone: true })), 'ok');
  // An iPhone too old for web push, on the Home Screen.
  assert.equal(pushSupport(browser({ ua: IPHONE, standalone: true, push: false })), 'unsupported');
  // A Mac is a Mac: no touch points.
  assert.equal(pushSupport(browser({ ua: IPAD_AS_MAC })), 'ok');
  assert.equal(pushSupport(browser({ permission: 'denied' })), 'denied');
  const noWorker = browser();
  delete noWorker.navigator.serviceWorker;
  assert.equal(pushSupport(noWorker), 'unsupported');
});

function storage() {
  const m = new Map();
  return { getItem: (k) => m.get(k) ?? null, setItem: (k, v) => m.set(k, String(v)), removeItem: (k) => m.delete(k) };
}

// A browser's push subscription and PushManager, as far as the app uses them.
function pushManager() {
  const pm = {
    current: null,
    subscribed: [],
    async getSubscription() { return pm.current; },
    async subscribe(options) {
      pm.subscribed.push(options);
      const n = pm.subscribed.length;
      const sub = {
        endpoint: `https://web.push.apple.com/sub-${n}`,
        options: { applicationServerKey: options.applicationServerKey.buffer.slice(0) },
        unsubscribed: false,
        async unsubscribe() { sub.unsubscribed = true; if (pm.current === sub) { pm.current = null; } return true; },
        toJSON() { return { endpoint: sub.endpoint, keys: { p256dh: `p256dh-${n}`, auth: `auth-${n}` } }; },
      };
      pm.current = sub;
      return sub;
    },
  };
  return pm;
}

function client(capabilities = ['sessions.read', 'push']) {
  return {
    capabilities,
    requests: [],
    async request(op, data) { this.requests.push({ op, data }); return op === 'push.subscribe' ? { subscribed: true, count: 1 } : { removed: true }; },
  };
}

function registrar(win = browser(), { key = KEY } = {}) {
  const pm = pushManager();
  const keys = [key];
  const r = new PushRegistrar({
    win,
    storage: storage(),
    registration: async () => ({ pushManager: pm }),
    fetchKey: async () => base64UrlToBytes(keys[0]),
    appUrl: 'https://danswett.github.io/agent-bridge/app/',
    device: 'iPhone · Agent Bridge',
  });
  return { r, pm, win, keys };
}

test('turning notifications on asks first, subscribes with the relay key and registers with the machine shown', async () => {
  const { r, pm, win } = registrar();
  let asked = 0;
  win.Notification.requestPermission = () => { asked++; return new Promise((res) => setTimeout(() => { win.Notification.permission = 'granted'; res('granted'); }, 5)); };
  const host = client();

  const done = r.enable(host, host.capabilities);
  // Asked for at once, inside the tap: anything awaited first would lose Safari's prompt.
  assert.equal(asked, 1);
  assert.equal(r.busy, 'on');
  await done;

  assert.equal(r.enabled, true);
  assert.equal(r.busy, '');
  assert.deepEqual(pm.subscribed.map((o) => o.userVisibleOnly), [true]);
  assert.deepEqual([...pm.subscribed[0].applicationServerKey], [...base64UrlToBytes(KEY)]);
  assert.deepEqual(host.requests, [{
    op: 'push.subscribe',
    data: { endpoint: 'https://web.push.apple.com/sub-1', keys: { p256dh: 'p256dh-1', auth: 'auth-1' }, app: 'https://danswett.github.io/agent-bridge/app/', device: 'iPhone · Agent Bridge' },
  }]);
});

test('a refused permission leaves notifications off and says why', async () => {
  const { r, pm, win } = registrar();
  win.Notification.answer = 'denied';
  await assert.rejects(r.enable(), /blocked/);
  assert.equal(r.enabled, false);
  assert.match(r.error, /Allow them in Settings/);
  assert.equal(pm.subscribed.length, 0);
});

test('every machine opened while notifications are on is registered, and only one that offers push', async () => {
  const { r, pm, win } = registrar();
  win.Notification.permission = 'granted';
  const before = client();
  assert.equal(await r.register(before, before.capabilities), false, 'off until turned on');
  await r.enable();

  const a = client();
  const old = client(['sessions.read']);
  assert.equal(await r.register(a, a.capabilities), true);
  assert.equal(await r.register(old, old.capabilities), false);
  assert.equal(a.requests.length, 1);
  assert.equal(old.requests.length, 0);
  assert.equal(pm.subscribed.length, 1, 'the same browser subscription for every machine');

  // The permission was taken away in Settings: nothing more is registered.
  win.Notification.permission = 'denied';
  assert.equal(await r.register(client(), ['push']), false);
});

test('a subscription made with an old relay key is replaced before registering, and one the browser dropped is remade', async () => {
  const { r, pm, win } = registrar();
  win.Notification.permission = 'granted';
  await r.enable();
  const first = pm.current;

  pm.current = null;
  const host = client();
  await r.register(host, host.capabilities);
  assert.equal(host.requests[0].data.endpoint, 'https://web.push.apple.com/sub-2');

  // The relay's key changed (a new deployment): the old subscription would be refused.
  const stale = pm.current;
  const other = registrar(win, { key: 'BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8' });
  other.pm.current = stale;
  other.r.storage.setItem('agent-bridge.app.notify', 'on');
  const third = client();
  await other.r.register(third, third.capabilities);
  assert.equal(stale.unsubscribed, true);
  assert.equal(third.requests[0].data.endpoint, 'https://web.push.apple.com/sub-1');
  assert.equal(other.pm.subscribed.length, 1);
  assert.equal(first.unsubscribed, false, 'the dropped one was the browser\'s doing');
});

test('turning notifications off tells the machine shown and unsubscribes the browser, so every other machine hears 410', async () => {
  const { r, pm, win } = registrar();
  win.Notification.permission = 'granted';
  await r.enable();
  const sub = pm.current;
  const host = client();

  await r.disable(host);

  assert.equal(r.enabled, false);
  assert.deepEqual(host.requests, [{ op: 'push.unsubscribe', data: { endpoint: sub.endpoint } }]);
  assert.equal(sub.unsubscribed, true);
  assert.equal(await r.register(client(), ['push']), false);
});

test('signing out drops the subscription: the account\'s machines must not go on notifying this device', async () => {
  const { r, pm, win } = registrar();
  win.Notification.permission = 'granted';
  await r.enable();
  const sub = pm.current;
  await r.forget();
  assert.equal(sub.unsubscribed, true);
  assert.equal(r.enabled, false);
});

test('the relay key is fetched from the relay, and a relay without push says so', async () => {
  const calls = [];
  const fetchFn = async (url, init) => { calls.push([url, init.cache]); return { ok: true, status: 200, json: async () => ({ key: KEY }) }; };
  assert.equal((await fetchVapidKey('https://relay.example', fetchFn)).length, 65);
  assert.deepEqual(calls, [['https://relay.example/v1/push/key', 'no-store']]);
  await assert.rejects(fetchVapidKey('https://relay.example', async () => ({ ok: false, status: 404 })), /does not send notifications/);
});

function notification(data) {
  return { data, closed: false, close() { this.closed = true; } };
}

test('question notifications this app sees answered are closed; others stay', async () => {
  const store = new SessionsStore();
  store.upsert({ id: 's1', status: 'waiting', updatedAt: 't', question: { id: 'q2', kind: 'form', message: 'm' } });
  store.upsert({ id: 's2', status: 'waiting', updatedAt: 't', question: { id: 'q9', kind: 'form', message: 'm' } });
  const shown = [
    notification({ kind: 'question', machine: 'm1', session: 's1', question: 'q1' }), // superseded
    notification({ kind: 'question', machine: 'm1', session: 's2', question: 'q9' }), // still asked
    notification({ kind: 'question', machine: 'm1', session: 'gone', question: 'q3' }), // session ended
    notification({ kind: 'finished', machine: 'm1', session: 'gone' }), // not a question
    notification({ kind: 'question', machine: 'm2', session: 's1', question: 'q1' }), // another machine
  ];
  await closeStaleNotices({ getNotifications: async () => shown }, 'm1', store);
  assert.deepEqual(shown.map((n) => n.closed), [true, false, true, false, false]);

  const tagged = [notification({}), notification({})];
  let asked;
  await closeNotices({ getNotifications: async (filter) => { asked = filter; return tagged; } }, 'm1', 's1');
  assert.deepEqual(asked, { tag: noticeTag('m1', 's1') });
  assert.equal(noticeTag('m1', 's1'), 'm1:s1');
  assert.ok(tagged.every((n) => n.closed));
});

// ---- the session being read (#1190) ----

function readingPage({ visible = true, keepaliveMs = 30 } = {}) {
  const doc = { visibilityState: visible ? 'visible' : 'hidden', fire: [], addEventListener(type, fn) { if (type === 'visibilitychange') { this.fire.push(fn); } }, removeEventListener(type, fn) { this.fire = this.fire.filter((f) => f !== fn); } };
  const inputs = [];
  const listening = new Set();
  const win = {
    addEventListener(type, fn, options) { listening.add(fn); if (options?.passive === true) { inputs.push(fn); } },
    removeEventListener(type, fn) { listening.delete(fn); const i = inputs.indexOf(fn); if (i >= 0) { inputs.splice(i, 1); } },
  };
  const changes = [];
  const focus = [];
  const closed = [];
  const clock = { t: 1_000_000 };
  const said = { refreshes: 0, idle: null };
  const hosts = {
    setFocus: ({ idle, ...f }) => { said.idle = idle; if (f.question === null) { delete f.question; } focus.push(f); },
    refreshFocus: () => { said.refreshes += 1; },
    sessions: { onChange: (fn) => { changes.push(fn); return () => { changes.splice(changes.indexOf(fn), 1); }; } },
  };
  const reading = new Reading({ hosts, doc, win, delayMs: 20, keepaliveMs, now: () => clock.t, close: async (h, s, q) => { closed.push(q ? `${h}/${s}/${q}` : `${h}/${s}`); } });
  const initial = focus.splice(0);
  const flip = (state) => { doc.visibilityState = state; for (const fn of doc.fire) { fn(); } };
  const input = (advanceMs = 300) => { clock.t += advanceMs; for (const fn of inputs) { fn(); } };
  const settle = () => new Promise((r) => { setTimeout(r, 60); });
  return { reading, focus, initial, said, clock, input, closed, changes, flip, settle, doc, win, listening, hosts };
}


test('the page says what it shows as soon as it starts, even with no session open', () => {
  assert.deepEqual(readingPage().initial, [{ visible: true, hostId: null, id: null }]);
  assert.deepEqual(readingPage({ visible: false }).initial, [{ visible: false, hostId: null, id: null }]);
});

test('the age of the last input is read as it is sent, and is unknown, not 0, until there has been input', () => {
  const { reading, said, clock, input } = readingPage();
  assert.equal(reading.idleMs(), null);
  assert.equal(said.idle(), null);
  input();
  clock.t += 5000;
  assert.equal(said.idle(), 5000);
  clock.t += 2500;
  assert.equal(said.idle(), 7500);
});

test('the first input says the person is active at once, a burst of input says nothing more, and a keepalive follows only while input continues', async () => {
  const { said, clock, input, settle } = readingPage();
  input();
  assert.equal(said.refreshes, 1);
  for (let i = 0; i < 40; i++) { input(300); }
  assert.equal(said.refreshes, 1, 'a burst of input is not said again');
  await settle();
  assert.ok(said.refreshes >= 2, 'kept alive while input goes on');
  // Input stops: the next tick finds the person idle, and nothing goes after it.
  clock.t += 120_000;
  await settle();
  const before = said.refreshes;
  await settle();
  await settle();
  assert.equal(said.refreshes, before, 'nothing while idle');
  input();
  assert.equal(said.refreshes, before + 1, 'input after being idle says so at once');
  clock.t += 120_000;
});

test('nothing about input is said while the page is hidden, and the keepalive stops', async () => {
  const { focus, said, input, flip, settle } = readingPage();
  input();
  flip('hidden');
  assert.deepEqual(focus, [{ visible: false, hostId: null, id: null }]);
  const before = said.refreshes;
  input();
  await settle();
  await settle();
  assert.equal(said.refreshes, before);
  assert.equal(focus.length, 1, 'hidden is said once');
});

test("opening a session while the page is in sight tells the machines and closes that session's notices, no other's", async () => {
  const { reading, focus, closed } = readingPage();
  reading.open({ hostId: 'm1', id: 's1' });
  reading.open({ hostId: 'm1', id: 's1' });
  await Promise.resolve();
  assert.deepEqual(focus, [{ visible: true, hostId: 'm1', id: 's1' }]);
  assert.deepEqual(closed, ['m1/s1']);
  reading.open(null);
  await Promise.resolve();
  assert.deepEqual(focus.at(-1), { visible: true, hostId: null, id: null });
  assert.deepEqual(closed, ['m1/s1'], 'the list has no session to close notices of');
});


test('a hidden page closes no notices and says it is hidden; coming back to the open session closes them', async () => {
  const { reading, focus, closed, flip } = readingPage({ visible: false });
  reading.open({ hostId: 'm1', id: 's1' });
  await Promise.resolve();
  assert.deepEqual(focus, [{ visible: false, hostId: 'm1', id: 's1' }]);
  assert.deepEqual(closed, []);
  flip('visible');
  await Promise.resolve();
  assert.deepEqual(focus.at(-1), { visible: true, hostId: 'm1', id: 's1' });
  assert.deepEqual(closed, ['m1/s1']);
  flip('hidden');
  await Promise.resolve();
  assert.deepEqual(focus.at(-1), { visible: false, hostId: 'm1', id: 's1' });
  assert.deepEqual(closed, ['m1/s1']);
});

test('the question its card shows is said with the open session, and said again when it changes', async () => {
  const { reading, focus, closed } = readingPage();
  reading.open({ hostId: 'm1', id: 's1', question: 'q1' });
  reading.open({ hostId: 'm1', id: 's1', question: 'q1' });
  reading.open({ hostId: 'm1', id: 's1', question: 'q2' });
  reading.open({ hostId: 'm1', id: 's1', question: null });
  await Promise.resolve();
  assert.deepEqual(focus, [
    { visible: true, hostId: 'm1', id: 's1', question: 'q1' },
    { visible: true, hostId: 'm1', id: 's1', question: 'q2' },
    { visible: true, hostId: 'm1', id: 's1' },
  ]);
  assert.deepEqual(closed, ['m1/s1/q1', 'm1/s1/q2', 'm1/s1']);
});

test('only a notice of the question on screen is closed with a session being read; its other notices go, and a question not shown stays', async () => {
  const tagged = () => [
    notification({ kind: 'question', machine: 'm1', session: 's1', question: 'q1' }),
    notification({ kind: 'question', machine: 'm1', session: 's1', question: 'q2' }),
    notification({ kind: 'finished', machine: 'm1', session: 's1' }),
  ];
  let shown = tagged();
  const registration = { getNotifications: async (filter) => { assert.deepEqual(filter, { tag: noticeTag('m1', 's1') }); return shown; } };
  await closeReadNotices(registration, 'm1', 's1', 'q2');
  assert.deepEqual(shown.map((n) => n.closed), [false, true, true]);
  shown = tagged();
  await closeReadNotices(registration, 'm1', 's1', null);
  assert.deepEqual(shown.map((n) => n.closed), [false, false, true], 'no question on screen: no question notice is read');
});

test('a stopped Reading has let go of every listener and says nothing more', async () => {
  const { reading, focus, said, input, flip, settle, doc, listening, changes, closed } = readingPage();
  reading.open({ hostId: 'm1', id: 's1' });
  input();
  assert.ok(listening.size > 0 && doc.fire.length === 1 && changes.length === 1);
  reading.stop();
  await Promise.resolve();
  assert.equal(listening.size, 0, 'no window listener left');
  assert.equal(doc.fire.length, 0, 'no visibility listener left');
  assert.equal(changes.length, 0, 'no sessions listener left');
  const [sent, refreshed, shut] = [focus.length, said.refreshes, closed.length];
  reading.open({ hostId: 'm1', id: 's2' });
  flip('hidden');
  flip('visible');
  await settle();
  await settle();
  assert.deepEqual([focus.length, said.refreshes, closed.length], [sent, refreshed, shut]);
});

test('a Reading made for a later sign-in is the only one listening once the earlier is stopped', () => {
  const first = readingPage();
  const alone = first.listening.size;
  first.reading.stop();
  // The same page again, as begin() makes it: a second Reading on the same window and document.
  const again = new Reading({ hosts: first.hosts, doc: first.doc, win: first.win, close: async () => {} });
  assert.equal(first.listening.size, alone, 'the listeners are the second Reading\'s alone');
  assert.equal(first.doc.fire.length, 1);
  assert.equal(first.changes.length, 1);
  again.stop();
  assert.equal(first.listening.size, 0);
});

test("a change to the open session closes its notices a moment later; another session's change, or one while hidden, closes nothing", async () => {
  const { reading, closed, changes, flip, settle } = readingPage();
  reading.open({ hostId: 'm1', id: 's1' });
  await settle();
  closed.length = 0;
  const change = (hostId, id) => { for (const fn of changes) { fn({ type: 'upsert', hostId, id }); } };
  change('m1', 's2');
  change('m2', 's1');
  await settle();
  assert.deepEqual(closed, []);
  for (let i = 0; i < 5; i++) { change('m1', 's1'); }
  await Promise.resolve();
  assert.deepEqual(closed, [], 'not before the push that came with it has been shown');
  await settle();
  assert.deepEqual(closed, ['m1/s1'], 'once, however many changes came');
  flip('hidden');
  closed.length = 0;
  change('m1', 's1');
  await settle();
  assert.deepEqual(closed, []);
});

test('turning notifications off while a registration waits for the relay key leaves this device unsubscribed', async () => {
  const win = browser({ permission: 'granted' });
  const pm = pushManager();
  let release;
  const keyArrives = new Promise((res) => { release = res; });
  const store = storage();
  store.setItem('agent-bridge.app.notify', 'on');
  const r = new PushRegistrar({
    win,
    storage: store,
    registration: async () => ({ pushManager: pm }),
    fetchKey: async () => { await keyArrives; return base64UrlToBytes(KEY); },
    appUrl: 'https://danswett.github.io/agent-bridge/app/',
    device: 'iPhone · Agent Bridge',
  });
  const host = client();

  // A channel opened: registering, which waits for the relay's key. Then the tap on Off.
  const registering = r.register(host, host.capabilities);
  await new Promise((res) => { setImmediate(res); });
  const turningOff = r.disable(host);
  release();
  await Promise.all([registering, turningOff]);

  assert.equal(r.enabled, false);
  assert.equal(pm.current, null, 'no browser subscription left');
  assert.deepEqual(host.requests.map((q) => q.op), ['push.subscribe', 'push.unsubscribe'], 'the machine heard off last');
  assert.equal(host.requests[1].data.endpoint, host.requests[0].data.endpoint);
});
test('signing out unsubscribes at once, even with a registration stuck waiting for the relay key, which then stands down', async () => {
  const win = browser({ permission: 'granted' });
  const pm = pushManager();
  const existing = await pm.subscribe({ userVisibleOnly: true, applicationServerKey: base64UrlToBytes(KEY) });
  let release;
  const keyArrives = new Promise((res) => { release = res; });
  const store = storage();
  store.setItem('agent-bridge.app.notify', 'on');
  const r = new PushRegistrar({
    win,
    storage: store,
    registration: async () => ({ pushManager: pm }),
    fetchKey: async () => { await keyArrives; return base64UrlToBytes(KEY); },
    appUrl: 'https://danswett.github.io/agent-bridge/app/',
    device: 'iPhone · Agent Bridge',
  });
  const host = client();
  const registering = r.register(host, host.capabilities);
  await new Promise((res) => { setImmediate(res); });

  // The page navigates away soon after; the key never comes before it does.
  await r.forget();
  assert.equal(existing.unsubscribed, true, 'unsubscribed before the key arrived');

  release();
  assert.equal(await registering, false);
  assert.equal(pm.current, null, 'nothing made again afterwards');
  assert.equal(pm.subscribed.length, 1);
  assert.deepEqual(host.requests, []);
});
test('signing out while the permission prompt is up leaves notifications off and nothing subscribed', async () => {
  const { r, pm, win } = registrar();
  let grant;
  win.Notification.requestPermission = () => new Promise((res) => { grant = () => { win.Notification.permission = 'granted'; res('granted'); }; });
  const host = client();

  const enabling = r.enable(host, host.capabilities);
  await r.forget();
  grant();
  await enabling;

  assert.equal(r.enabled, false);
  assert.equal(pm.current, null);
  assert.equal(pm.subscribed.length, 0);
  assert.deepEqual(host.requests, []);
});
test('a machine slow to answer its registration holds up no other machine\'s', async () => {
  const { r, win } = registrar();
  win.Notification.permission = 'granted';
  await r.enable();
  let answer;
  const slow = { ...client(), requests: [], request(op, data) { this.requests.push({ op, data }); return new Promise((res) => { answer = res; }); } };
  const quick = client();
  const slowDone = r.register(slow, slow.capabilities);
  assert.equal(await r.register(quick, quick.capabilities), true);
  assert.equal(quick.requests.length, 1);
  answer({ subscribed: true, count: 1 });
  assert.equal(await slowDone, true);
});

test('a machine\'s question notifications stay when its list stops being current while they are looked up', async () => {
  const store = new SessionsStore();
  store.upsert({ id: 's1', status: 'waiting', updatedAt: 't', question: { id: 'q1', kind: 'form', message: 'm' } });
  const shown = [notification({ kind: 'question', machine: 'm1', session: 's1', question: 'q1' })];
  let current = true;
  // Another machine is picked while the browser looks the notifications up: this one's list empties.
  const lookup = async () => { store.clear(); current = false; return shown; };
  await closeStaleNotices({ getNotifications: lookup }, 'm1', store, () => current);
  assert.equal(shown[0].closed, false);
});

test('an app updated to the new name keeps notifications on and its browser subscription, and registers the same endpoint under the new device name', async () => {
  const win = browser({ ua: IPHONE, standalone: true, permission: 'granted' });
  const pm = pushManager();
  const store = storage();
  const app = (device) => new PushRegistrar({
    win, storage: store, registration: async () => ({ pushManager: pm }), fetchKey: async () => base64UrlToBytes(KEY),
    appUrl: 'https://danswett.github.io/agent-bridge/app/', device,
  });
  // Before the update, under the old name.
  await app('iPhone · Agent Bridge').enable();
  // After it: the same browser, storage and subscription, with the name the app now gives.
  assert.notEqual(deviceName(IPHONE), 'iPhone · Agent Bridge', 'the device name changed with the rename');
  const updated = app(deviceName(IPHONE));
  assert.equal(updated.enabled, true, 'still on, without asking again');
  const host = client();
  assert.equal(await updated.register(host, host.capabilities), true);
  assert.equal(pm.subscribed.length, 1, 'no second browser subscription');
  assert.deepEqual(host.requests.map((q) => [q.op, q.data.endpoint, q.data.device]),
    [['push.subscribe', 'https://web.push.apple.com/sub-1', `iPhone · ${BRAND.name}`]]);
});
