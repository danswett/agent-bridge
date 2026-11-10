// The app's relay connection over time. A phone kills the socket whenever it locks or the
// app goes to the background (1006), and the app must come back by itself when it is next
// in front (spikes/s5-web-push RESULTS.md, finding 5). So:
// - after any drop, reconnect with jittered backoff (backoff.js: up to 1 s doubling to 30 s,
//   and a relay restart's 1012 spread over 0-5 s or the relay's hint)
// - reconnect at once when the page becomes visible again, is shown from the bfcache, or
//   the network returns
// - stop for good when the relay refuses the account (sign in again) or another window keeps
//   taking this device over (two would only keep replacing each other); a first takeover is
//   taken back once, silently (#1218)
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
// A phone that changes networks, or whose radio sleeps, can keep a socket that reads open while
// nothing gets through either way. The relay closes it only at its pong timeout (30 s after its
// next ping) and a reply waits 90 s for its answer, so a reply sat at Sending for a minute or more,
// then went at once after Reconnecting (#1128). So once a message has gone out on a channel and
// nothing at all has come back for PROBE_AFTER_MS, the relay is asked whether it is still there
// (Relay.probe), and without an answer in PROBE_TIMEOUT_MS the socket is given up and another
// opened at once. Whatever was on its way is sent again on it with its same request, which a host
// carries out once however often it comes (answer-service.js).
export const PROBE_AFTER_MS = 5000;
export const PROBE_TIMEOUT_MS = 3000;
const UNRESPONSIVE = Object.freeze({ code: 1000, reason: 'unresponsive' });

// A window replaced by another one takes the connection back once, on its own. Replaced again
// within this long of that take-back, it stops for good (two windows in front would otherwise
// replace each other for ever) and says so, with Use here (#1218).
export const REPLACED_GUARD_MS = 60000;

// Why the connection is stopped for good because another window took it. An installed app has
// no tabs, so it says window only; the person need not know about sockets.
export const REPLACED_WORDING = Object.freeze({
  browser: 'This app is open in another window or tab.',
  installed: 'This app is open in another window.',
});

export class BridgeConnection {
  #listeners = new Map();
  #attempt = null;
  // The relay of the attempt under way, until it is admitted: closed if the app rests meanwhile.
  #connecting = null;
  #retryTimer = null;
  #backoff = new ReconnectBackoff();
  #wake = null;
  #rest = null;
  #restTimer = null;
  // When this window last took the connection back after being replaced (ms), kept across the
  // take-back: a second replacement within REPLACED_GUARD_MS of it is final. Null before one.
  #tookBackAt = null;
  // Replaced while out of sight: 'back' or 'final' (the guard, as it stood when it was replaced).
  // It rests (silently) without the usual wake-and-connect, and takes the connection back once,
  // or stops for good, when it is next in front.
  #yielded = null;
  // Set while a message sent on the current relay awaits any frame back, then a probe of it.
  #watchTimer = null;
  #probing = null;
  relay = null;
  presence = [];
  // The account's machines that are offline (relay.js); null while the relay keeps no roster.
  known = null;
  // 'idle' | 'connecting' | 'connected' | 'reconnecting' | 'resting' | 'replaced' | 'signed-out' | 'stopped'
  // replaced: stopped for good because another window keeps taking the connection, until Use here.
  state = 'idle';
  detail = '';
  retryAt = 0;

  // quietAfterMs: how long the app is out of sight before it rests (null: never). busy(): whether
  // anything is on its way that resting would cut off.
  constructor({
    base, key, deviceId, name, account, win = globalThis, quietAfterMs = QUIET_AFTER_MS, busy = () => false,
    makeRelay = (options) => new Relay(options), now = () => Date.now(), probeAfterMs = PROBE_AFTER_MS, probeTimeoutMs = PROBE_TIMEOUT_MS,
  }) {
    Object.assign(this, { base, key, deviceId, name, account, win, quietAfterMs, busy, makeRelay, now, probeAfterMs, probeTimeoutMs });
    this.channels = new ChannelSet({ key, deviceId, name });
    this.channels.onLog = (line) => this.#emit('log', line);
    this.channels.onSent = () => this.#sent();
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
        if (this.state === 'resting') {
          this.state = 'idle';
          if (this.#yielded) { this.#takeBack(this.#yielded === 'final'); return; }
        }
        this.#backoff.reset();
        this.#connect();
      };
      this.#rest = () => { if (this.#hidden()) { this.#restLater(); } };
      this.win.document?.addEventListener?.('visibilitychange', this.#wake);
      this.win.document?.addEventListener?.('visibilitychange', this.#rest);
      this.win.addEventListener?.('pageshow', this.#wake);
      this.win.addEventListener?.('online', this.#wake);
    }
    // Explicitly restarting (a "Use here" after another window took over) clears the stop, and
    // the guard: the person has chosen this window.
    if (this.state === 'replaced' || this.state === 'stopped' || this.#yielded) { this.state = 'idle'; }
    this.#yielded = null;
    this.#tookBackAt = null;
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
    this.#yielded = null;
    this.#unwatch();
    const c = this.#connecting;
    this.#connecting = null;
    this.#attempt = null;
    c?.close();
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
    this.#unwatch();
    // Put away while the relay was still admitting it: that attempt goes too, so the return
    // connects afresh rather than waiting on an admission that may never come.
    const c = this.#connecting;
    this.#connecting = null;
    this.#attempt = null;
    c?.close(1000, 'resting');
    const r = this.relay;
    this.relay = null;
    this.#setState('resting', 'The app is in the background.');
    if (r) {
      // Said before the channels go: detaching them closes every host client at once, and the
      // machines shown would read as dropped (reconnecting, out of the app badge) before they hear
      // it was a rest, with nothing left to tell which of them were current.
      this.#emit('lost', { code: 1000, reason: 'resting', resting: true });
      this.channels.attach(null);
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
    r.onReceive = () => { if (this.relay === r) { this.#unwatch(); } };
    this.#connecting = r;
    const attempt = r.connect(this.account.token).then(() => {
      // Put away while it was connecting (#sleep let it go), or stopped: it never takes over.
      if (this.#connecting !== r || this.state === 'resting') { r.close(1000, 'resting'); return; }
      this.#connecting = null;
      this.relay = r;
      this.#backoff.reset();
      this.channels.attach(r);
      this.#setState('connected', r.login ?? '');
      this.#emit('ready', r);
    }, (e) => {
      if (this.#connecting !== r) { return; }
      this.#connecting = null;
      this.#dropped(null, e);
    }).finally(() => { if (this.#attempt === attempt) { this.#attempt = null; } });
    this.#attempt = attempt;
    return attempt;
  }

  // A message went out on a channel: unless a frame of any kind comes back within probeAfterMs,
  // the relay is probed. One watch at a time, from the first message nothing has answered yet.
  #sent() {
    const r = this.relay;
    if (!r || this.#watchTimer || this.#probing === r || typeof r.probe !== 'function') { return; }
    this.#watchTimer = setTimeout(() => { this.#watchTimer = null; this.#probe(r); }, this.probeAfterMs);
  }

  #unwatch() {
    clearTimeout(this.#watchTimer);
    this.#watchTimer = null;
  }

  async #probe(r) {
    if (this.relay !== r) { return; }
    this.#probing = r;
    this.#emit('log', `relay silent ${this.probeAfterMs} ms after a send; probing it`);
    let alive = false;
    try { alive = await r.probe(this.probeTimeoutMs); } catch { alive = false; }
    if (this.#probing === r) { this.#probing = null; }
    if (alive || this.relay !== r) { return; }
    // Its close event may come only once the browser gives up on the socket, or never: the
    // connection is treated as gone first, so its close, whenever it comes, is ignored (#dropped).
    this.#dropped(r, UNRESPONSIVE, { now: true });
    r.close(UNRESPONSIVE.code, UNRESPONSIVE.reason);
  }

  #dropped(r, e, { now = false } = {}) {
    if (r && this.relay !== r) { return; }
    if (r) {
      this.#unwatch();
      // So that the next stall is on record (#1128): why it went, and how long it had been quiet.
      const since = r.lastReceivedAt ? `${Date.now() - r.lastReceivedAt} ms since its last frame` : 'no frame received';
      this.#emit('log', `relay connection lost (${`${e.code ?? ''} ${e.reason ?? ''}`.trim()}), ${since}`);
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
      this.#replaced();
      return;
    }
    // Given up for silence: the network is likely there by another route, so no waiting.
    const delay = now ? 0 : this.#backoff.next(e.code, e.reason);
    this.retryAt = Date.now() + delay;
    this.#setState('reconnecting', `${e.code ?? ''} ${e.reason ?? ''}`.trim());
    this.#retryTimer = setTimeout(() => this.#connect(), delay);
  }

  // Another window took the connection. In sight, this one takes it back at once; out of sight,
  // it rests without a word and does the same when next in front (#wake). Whether the guard
  // holds is settled now, when it was replaced, not hours later when it is looked at again.
  #replaced() {
    const final = this.#tookBackAt !== null && this.now() - this.#tookBackAt < REPLACED_GUARD_MS;
    if (this.#hidden()) {
      this.#yielded = final ? 'final' : 'back';
      this.#setState('resting', 'Another window has the connection.');
      return;
    }
    this.#takeBack(final);
  }

  // Takes the connection back once, unless it was replaced again within REPLACED_GUARD_MS of a
  // take-back: two windows in sight would otherwise replace each other for ever, so this one
  // stops for good (final).
  #takeBack(final) {
    this.#yielded = null;
    if (final) {
      this.#setState('replaced', this.#installed() ? REPLACED_WORDING.installed : REPLACED_WORDING.browser);
      return;
    }
    this.#tookBackAt = this.now();
    // Not announced: no banner, no paused strip. The attempt that was refused (when it was the
    // connecting that was replaced) is finished with, or #connect would join it and stay stopped.
    this.state = 'idle';
    this.#attempt = null;
    this.#backoff.reset();
    this.#connect();
  }

  // An installed app has no tabs to speak of.
  #installed() {
    try {
      return this.win.navigator?.standalone === true || this.win.matchMedia?.('(display-mode: standalone)')?.matches === true;
    } catch { return false; }
  }
}
