// The app's relay connection over time. A phone kills the socket whenever it locks or the
// app goes to the background (1006), and the app must come back by itself when it is next
// in front (spikes/s5-web-push RESULTS.md, finding 5). So:
// - after any drop, reconnect with jittered backoff (backoff.js: up to 1 s doubling to 30 s,
//   and a relay restart's 1012 spread over 0-5 s or the relay's hint)
// - reconnect at once when the page becomes visible again, is shown from the bfcache, or
//   the network returns
// - stop for good when the relay refuses the account (sign in again) or another window has
//   taken this device over (two would only keep replacing each other)
// - rest while the app is out of sight (#610): after quietAfterMs hidden, the socket closes, and
//   with it every channel, subscription and look at a machine. An open socket held the phone's
//   radio in its high-power state, pinging every 20 s and taking every session's changes, until
//   the OS got round to suspending the page; meanwhile a machine's notifications (Web Push)
//   say what needs the person. Back in front, it connects at once, and each machine shown is
//   subscribed again (host-session.js), held as confirming until its list is whole (#427). Not
//   while anything is on its way (busy): an answer or reply being sent, a request awaiting its
//   response; those finish first.
import { CLOSE_CODES, Relay } from './relay.js';
import { ChannelSet } from './channels.js';
import { ReconnectBackoff } from './backoff.js';

// Long enough that switching to another app and back, to copy something, say, keeps everything
// as it was; short enough that a phone put away stops talking within seconds.
export const QUIET_AFTER_MS = 15000;

export class BridgeConnection {
  #listeners = new Map();
  #attempt = null;
  #retryTimer = null;
  #backoff = new ReconnectBackoff();
  #wake = null;
  #rest = null;
  #restTimer = null;
  relay = null;
  presence = [];
  // The account's machines that are offline (relay.js); null while the relay keeps no roster.
  known = null;
  // 'idle' | 'connecting' | 'connected' | 'reconnecting' | 'resting' | 'replaced' | 'signed-out' | 'stopped'
  state = 'idle';
  detail = '';
  retryAt = 0;

  // quietAfterMs: how long the app is out of sight before it rests (null: never). busy(): whether
  // anything is on its way that resting would cut off.
  constructor({
    base, key, deviceId, name, account, win = globalThis, quietAfterMs = QUIET_AFTER_MS, busy = () => false,
    makeRelay = (options) => new Relay(options),
  }) {
    Object.assign(this, { base, key, deviceId, name, account, win, quietAfterMs, busy, makeRelay });
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
        if (this.#hidden()) { return; }
        clearTimeout(this.#restTimer);
        this.#restTimer = null;
        if (this.state === 'resting') { this.state = 'idle'; }
        this.#backoff.reset();
        this.#connect();
      };
      this.#rest = () => { if (this.#hidden()) { this.#restLater(); } };
      this.win.document?.addEventListener?.('visibilitychange', this.#wake);
      this.win.document?.addEventListener?.('visibilitychange', this.#rest);
      this.win.addEventListener?.('pageshow', this.#wake);
      this.win.addEventListener?.('online', this.#wake);
    }
    // Explicitly restarting (a "Use here" after another window took over) clears the stop.
    if (this.state === 'replaced' || this.state === 'stopped') { this.state = 'idle'; }
    // Started out of sight (opened behind another tab): it rests as it would once put away.
    if (this.#hidden()) { this.#restLater(); }
    return this.#connect();
  }

  stop() {
    clearTimeout(this.#retryTimer);
    if (this.#wake) {
      this.win.document?.removeEventListener?.('visibilitychange', this.#wake);
      this.win.document?.removeEventListener?.('visibilitychange', this.#rest);
      this.win.removeEventListener?.('pageshow', this.#wake);
      this.win.removeEventListener?.('online', this.#wake);
      this.#wake = null;
      this.#rest = null;
    }
    clearTimeout(this.#restTimer);
    this.#restTimer = null;
    const r = this.relay;
    this.relay = null;
    this.known = null;
    this.channels.attach(null);
    r?.close();
    this.#setState('stopped');
  }

  openChannel(peerId, options) { return this.channels.open(peerId, options); }

  #hidden() { return this.win.document?.visibilityState === 'hidden'; }

  // Rests once the app has been out of sight for quietAfterMs; while something is on its way,
  // looks again as long after.
  #restLater() {
    if (this.quietAfterMs === null || this.#restTimer) { return; }
    this.#restTimer = setTimeout(() => {
      this.#restTimer = null;
      if (!this.#hidden() || ['signed-out', 'replaced', 'stopped', 'resting'].includes(this.state)) { return; }
      let busy = false;
      try { busy = this.busy() === true; } catch (e) { console.error(e); }
      if (busy) { this.#restLater(); return; }
      this.#sleep();
    }, this.quietAfterMs);
  }

  // Closes the socket, and stays closed until the app is back in sight. Listeners hear 'lost' as
  // they do for a drop, so every channel goes and is opened again on return.
  #sleep() {
    clearTimeout(this.#retryTimer);
    const r = this.relay;
    this.relay = null;
    this.#setState('resting', 'The app is in the background.');
    if (r) {
      this.channels.attach(null);
      this.#emit('lost', { code: 1000, reason: 'resting', resting: true });
      r.close(1000, 'resting');
    }
  }

  // Forgets one of the account's offline machines: null once done, or why not ('unsupported'
  // from a relay without a roster).
  forget(machineId) {
    if (!this.relay?.open) { return Promise.reject(new Error('not connected to the relay')); }
    if (this.known === null) { return Promise.resolve('unsupported'); }
    return this.relay.forget(machineId);
  }

  #connect() {
    if (['signed-out', 'replaced', 'stopped', 'resting'].includes(this.state)) { return Promise.resolve(); }
    if (this.#attempt) { return this.#attempt; }
    if (this.relay?.open) { return Promise.resolve(); }
    clearTimeout(this.#retryTimer);
    if (this.state !== 'reconnecting') { this.#setState('connecting'); }
    const r = this.makeRelay({ base: this.base, key: this.key, deviceId: this.deviceId, name: this.name, kind: 'pwa' });
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
      // Put away while it was connecting: it rests as soon as it is up.
      if (this.state === 'resting') { r.close(1000, 'resting'); return; }
      this.relay = r;
      this.#backoff.reset();
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
    if (this.state === 'stopped' || this.state === 'resting') { return; }
    if (e.code === CLOSE_CODES.authFailed || e.code === CLOSE_CODES.proofFailed) {
      this.#setState('signed-out', 'The relay no longer accepts this sign-in.');
      this.#emit('signed-out', e);
      return;
    }
    if (e.code === CLOSE_CODES.replaced) {
      this.#setState('replaced', 'This app is open in another window or tab.');
      return;
    }
    const delay = this.#backoff.next(e.code, e.reason);
    this.retryAt = Date.now() + delay;
    this.#setState('reconnecting', `${e.code ?? ''} ${e.reason ?? ''}`.trim());
    this.#retryTimer = setTimeout(() => this.#connect(), delay);
  }
}
