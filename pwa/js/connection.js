// The app's relay connection over time. A phone kills the socket whenever it locks or the
// app goes to the background (1006), and the app must come back by itself when it is next
// in front (spikes/s5-web-push RESULTS.md, finding 5). So:
// - after any drop, reconnect with backoff (1 s doubling to 30 s)
// - reconnect at once when the page becomes visible again, is shown from the bfcache, or
//   the network returns
// - stop for good when the relay refuses the account (sign in again) or another window has
//   taken this device over (two would only keep replacing each other)
import { CLOSE_CODES, Relay } from './relay.js';
import { ChannelSet } from './channels.js';

export class BridgeConnection {
  #listeners = new Map();
  #attempt = null;
  #retryTimer = null;
  #backoff = 1000;
  #wake = null;
  relay = null;
  presence = [];
  // The account's machines that are offline (relay.js); null while the relay keeps no roster.
  known = null;
  // 'idle' | 'connecting' | 'connected' | 'reconnecting' | 'replaced' | 'signed-out' | 'stopped'
  state = 'idle';
  detail = '';
  retryAt = 0;

  constructor({ base, key, deviceId, name, account, win = globalThis }) {
    Object.assign(this, { base, key, deviceId, name, account, win });
    this.channels = new ChannelSet({ key, deviceId, name });
    this.channels.onLog = (line) => this.#emit('log', line);
  }

  on(type, fn) {
    if (!this.#listeners.has(type)) { this.#listeners.set(type, new Set()); }
    this.#listeners.get(type).add(fn);
    return () => this.#listeners.get(type)?.delete(fn);
  }

  #emit(type, detail) {
    for (const fn of this.#listeners.get(type) ?? []) {
      try { fn(detail); } catch (e) { console.error(e); }
    }
  }

  #setState(state, detail = '') {
    this.state = state;
    this.detail = detail;
    this.#emit('state', { state, detail });
  }

  get ready() { return !!this.relay?.open; }

  start() {
    if (!this.#wake) {
      this.#wake = () => {
        if (this.win.document?.visibilityState !== 'hidden') { this.#backoff = 1000; this.#connect(); }
      };
      this.win.document?.addEventListener?.('visibilitychange', this.#wake);
      this.win.addEventListener?.('pageshow', this.#wake);
      this.win.addEventListener?.('online', this.#wake);
    }
    // Explicitly restarting (a "Use here" after another window took over) clears the stop.
    if (this.state === 'replaced' || this.state === 'stopped') { this.state = 'idle'; }
    return this.#connect();
  }

  stop() {
    clearTimeout(this.#retryTimer);
    if (this.#wake) {
      this.win.document?.removeEventListener?.('visibilitychange', this.#wake);
      this.win.removeEventListener?.('pageshow', this.#wake);
      this.win.removeEventListener?.('online', this.#wake);
      this.#wake = null;
    }
    const r = this.relay;
    this.relay = null;
    this.known = null;
    this.channels.attach(null);
    r?.close();
    this.#setState('stopped');
  }

  openChannel(peerId, options) { return this.channels.open(peerId, options); }

  // Forgets one of the account's offline machines: null once done, or why not ('unsupported'
  // from a relay without a roster).
  forget(machineId) {
    if (!this.relay?.open) { return Promise.reject(new Error('not connected to the relay')); }
    if (this.known === null) { return Promise.resolve('unsupported'); }
    return this.relay.forget(machineId);
  }

  #connect() {
    if (['signed-out', 'replaced', 'stopped'].includes(this.state)) { return Promise.resolve(); }
    if (this.#attempt) { return this.#attempt; }
    if (this.relay?.open) { return Promise.resolve(); }
    clearTimeout(this.#retryTimer);
    if (this.state !== 'reconnecting') { this.#setState('connecting'); }
    const r = new Relay({ base: this.base, key: this.key, deviceId: this.deviceId, name: this.name, kind: 'pwa' });
    r.onLog = (line) => this.#emit('log', line);
    r.onPresence = (list, known) => {
      // Presence can arrive before 'ready' (S5 finding 2), while this.relay is still null.
      if (this.relay && this.relay !== r) { return; }
      this.presence = list;
      this.known = known;
      this.#emit('presence', list);
    };
    r.onClose = (e) => this.#dropped(r, e);
    this.#attempt = r.connect(this.account.token).then(() => {
      this.relay = r;
      this.#backoff = 1000;
      this.channels.attach(r);
      this.#setState('connected', r.login ?? '');
      this.#emit('ready', r);
    }, (e) => this.#dropped(null, e)).finally(() => { this.#attempt = null; });
    return this.#attempt;
  }

  #dropped(r, e) {
    if (r && this.relay !== r) { return; }
    if (r) {
      this.relay = null;
      this.channels.attach(null);
      this.#emit('lost', e);
    }
    if (this.state === 'stopped') { return; }
    if (e.code === CLOSE_CODES.authFailed || e.code === CLOSE_CODES.proofFailed) {
      this.#setState('signed-out', 'The relay no longer accepts this sign-in.');
      this.#emit('signed-out', e);
      return;
    }
    if (e.code === CLOSE_CODES.replaced) {
      this.#setState('replaced', 'This app is open in another window or tab.');
      return;
    }
    const delay = this.#backoff;
    this.#backoff = Math.min(this.#backoff * 2, 30000);
    this.retryAt = Date.now() + delay;
    this.#setState('reconnecting', `${e.code ?? ''} ${e.reason ?? ''}`.trim());
    this.#retryTimer = setTimeout(() => this.#connect(), delay);
  }
}
