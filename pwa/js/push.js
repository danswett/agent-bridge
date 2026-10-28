// Notifications (Web Push): turning them on and off, and registering this device with each
// machine it opens. Protocol: docs/protocol/v1-sessions.md, Notifications.
//
//   tap "Turn on" -> Notification.requestPermission()   (only from a tap: Safari's rule)
//                 -> pushManager.subscribe(relay's VAPID key, GET /v1/push/key)
//   each channel to a machine with "push" -> push.subscribe { endpoint, keys, app, device }
//   machine -> relay -> Apple/Google -> sw.js shows it; a tap opens ?host=&session=&question=
//
// The machine encrypts every notice to this browser's key, so the relay and the push
// service never read it. Turning off unsubscribes the browser, so any machine still holding
// the subscription is told it is gone (410) by the push service on its next push.
import { Capabilities, Ops } from './protocol.js';

const SETTING = 'agent-bridge.app.notify';

export function isIos(nav = globalThis.navigator) {
  const ua = nav?.userAgent ?? '';
  // iPadOS asks for desktop sites and says it is a Mac; only the touch points give it away.
  return /iPhone|iPad|iPod/.test(ua) || (/Macintosh/.test(ua) && (nav?.maxTouchPoints ?? 0) > 1);
}

export function isStandalone(win = globalThis) {
  return win.matchMedia?.('(display-mode: standalone)')?.matches === true || win.navigator?.standalone === true;
}

// What this device allows now: 'ok', 'install' (iPhone or iPad, not opened from the Home
// Screen, where iOS offers no push at all), 'unsupported' or 'denied'.
export function pushSupport(win = globalThis) {
  if (isIos(win.navigator) && !isStandalone(win)) { return 'install'; }
  if (!win.navigator?.serviceWorker || !('PushManager' in win) || !('Notification' in win)) { return 'unsupported'; }
  if (win.Notification.permission === 'denied') { return 'denied'; }
  return 'ok';
}

export function base64UrlToBytes(s) {
  const b64 = s.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - (s.length % 4)) % 4);
  return Uint8Array.from(atob(b64), (c) => c.charCodeAt(0));
}

function sameBytes(a, b) {
  if (!a || !b) { return false; }
  const x = new Uint8Array(a instanceof ArrayBuffer ? a : a.buffer ?? a);
  const y = new Uint8Array(b instanceof ArrayBuffer ? b : b.buffer ?? b);
  return x.length === y.length && x.every((v, i) => v === y[i]);
}

// The relay's VAPID public key: what a subscription is bound to.
export async function fetchVapidKey(base, fetchFn = globalThis.fetch) {
  const r = await fetchFn(`${base}/v1/push/key`, { cache: 'no-store' });
  if (r.status === 404) { throw new Error('This relay does not send notifications.'); }
  if (!r.ok) { throw new Error(`The relay answered ${r.status}.`); }
  const { key } = await r.json();
  if (typeof key !== 'string') { throw new Error('The relay sent no notification key.'); }
  return base64UrlToBytes(key);
}

// One per notification about a session: what sw.js shows it under, and what the app closes.
export const noticeTag = (hostId, sessionId) => `${hostId}:${sessionId}`;

export class PushRegistrar {
  #listeners = new Set();
  #key = null;
  #chain = Promise.resolve();
  // Goes up when signing out; every operation still in flight checks it before going on.
  #generation = 0;
  // 'on' or 'off' while turning notifications on or off, '' otherwise; error is the last
  // failure's message, '' after a success.
  busy = '';
  error = '';

  constructor({ win = globalThis, storage = globalThis.localStorage, registration, fetchKey, appUrl, device }) {
    Object.assign(this, { win, storage, registration, fetchKey, appUrl, device });
  }

  onChange(fn) { this.#listeners.add(fn); return () => this.#listeners.delete(fn); }

  #emit() {
    for (const fn of this.#listeners) {
      try { fn(this); } catch (e) { console.error(e); }
    }
  }

  get support() { return pushSupport(this.win); }

  // On, as the person left it, and still allowed by the browser.
  get enabled() { return this.#setting() === 'on' && this.win.Notification?.permission === 'granted'; }

  // Must run in a tap's handler: Safari shows the permission prompt only then, so the
  // prompt is asked for before anything is awaited. Then registers with `client` if given.
  enable(client = null, capabilities = []) {
    let asked;
    try { asked = this.win.Notification.requestPermission(); } catch (e) { asked = Promise.reject(e); }
    // Signing out while the prompt is up must win: nothing is subscribed or switched on after.
    const generation = this.#generation;
    this.busy = 'on';
    this.error = '';
    this.#emit();
    return this.#serial(async () => {
      try {
        const permission = await asked;
        if (permission !== 'granted') {
          throw new Error(permission === 'denied'
            ? 'Notifications are blocked for Agent Bridge. Allow them in Settings, then try again.'
            : 'Notifications were not allowed.');
        }
        if (generation !== this.#generation || !await this.#subscription(true, generation)) { return; }
        if (generation !== this.#generation) { return; }
        this.#write('on');
        if (client) { await this.#register(client, capabilities); }
      } catch (e) {
        this.error = e.message;
        throw e;
      } finally {
        this.busy = '';
        this.#emit();
      }
    });
  }

  // Turns notifications off for this device: tells the machine connected now, then drops
  // the browser's subscription, which every other machine learns of at its next push.
  disable(client = null) {
    this.#write('off');
    this.busy = 'off';
    this.error = '';
    this.#emit();
    return this.#serial(async () => {
      try {
        const sub = await this.#subscription(false).catch(() => null);
        if (client) {
          await client.request(Ops.pushUnsubscribe, sub ? { endpoint: sub.endpoint } : {}).catch(() => {});
        }
        await sub?.unsubscribe().catch(() => {});
      } finally {
        this.busy = '';
        this.#emit();
      }
    });
  }

  // Sent on every channel to a machine that offers push while notifications are on: a
  // machine that dropped the subscription, or a browser that renewed it, is caught up.
  // Resolves to whether it registered.
  register(client, capabilities) { return this.#serial(() => this.#register(client, capabilities)); }

  // One at a time, in the order asked: a registration still waiting for the relay's key
  // when notifications are turned off must finish before the turning off does, or it would
  // subscribe again afterwards and leave this device notified with the switch showing off.
  #serial(fn) {
    const run = this.#chain.then(fn, fn);
    this.#chain = run.catch(() => {});
    return run;
  }

  async #register(client, capabilities) {
    if (!this.enabled || !capabilities.includes(Capabilities.push)) { return false; }
    const sub = await this.#subscription(true, this.#generation);
    if (!sub) { return false; }
    const json = sub.toJSON();
    await client.request(Ops.pushSubscribe, {
      endpoint: json.endpoint,
      keys: { p256dh: json.keys?.p256dh, auth: json.keys?.auth },
      app: this.appUrl,
      device: this.device,
    });
    return true;
  }

  // Signing out: this account's machines must not keep notifying this device. Not queued
  // behind a registration, which may wait on the relay for as long as it likes while the
  // page is about to go: it unsubscribes now, and anything still in flight is told to stand
  // down (#generation) and undo what it makes.
  async forget() {
    this.#write(null);
    this.#generation++;
    const sub = await this.#subscription(false).catch(() => null);
    await sub?.unsubscribe().catch(() => {});
    this.#emit();
  }

  // The browser's subscription, made (or remade, if the relay's key has changed since) when
  // `create` is set; null when signing out overtook the making of it.
  async #subscription(create, generation = this.#generation) {
    const reg = await this.registration();
    let sub = await reg.pushManager.getSubscription();
    if (!create) { return sub; }
    this.#key ??= await this.fetchKey();
    if (generation !== this.#generation) { return null; }
    if (sub && !sameBytes(sub.options?.applicationServerKey, this.#key)) {
      await sub.unsubscribe().catch(() => {});
      sub = null;
    }
    sub ??= await reg.pushManager.subscribe({ userVisibleOnly: true, applicationServerKey: this.#key });
    if (generation !== this.#generation) {
      await sub.unsubscribe().catch(() => {});
      return null;
    }
    return sub;
  }

  #setting() { try { return this.storage?.getItem(SETTING) ?? null; } catch { return null; } }

  #write(v) {
    try {
      if (v === null) { this.storage?.removeItem(SETTING); } else { this.storage?.setItem(SETTING, v); }
    } catch { /* a nicety */ }
  }
}

// Closes this machine's question notifications that are no longer true: the session has
// gone, or is not asking that question any more. Called with the machine's complete list.
export async function closeStaleNotices(registration, hostId, store) {
  const shown = await registration?.getNotifications?.() ?? [];
  for (const n of shown) {
    const d = n.data ?? {};
    if (d.machine !== hostId || d.kind !== 'question') { continue; }
    const s = store.get(d.session);
    if (!s?.question || s.question.id !== d.question) { n.close(); }
  }
}

// Closes every notification about one session: answered here, so there is nothing to say.
export async function closeNotices(registration, hostId, sessionId) {
  const shown = await registration?.getNotifications?.({ tag: noticeTag(hostId, sessionId) }) ?? [];
  for (const n of shown) { n.close(); }
}
