import { test } from 'node:test';
import assert from 'node:assert/strict';
import { PushRegistrar, base64UrlToBytes, closeNotices, closeStaleNotices, fetchVapidKey, noticeTag, pushSupport } from '../../js/push.js';
import { SessionsStore } from '../../js/sessions-store.js';

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
