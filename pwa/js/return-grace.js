// Coming back to the app (ui.md "Coming back to the app (#950)"): for a short while after the app
// is shown again, or the network returns, what was last seen stays on screen as it was, while the
// connections catch up. This keeps that clock and nothing else: the screens ask it what to draw.
//
//   0 ms        the return: the hold is on and the screens look as last seen.
//   HELD_MS     a control held since the return is drawn disabled (until then it is only aria-disabled).
//   CUE_MS      a list still not whole says "Updating…" in the title row's slot.
//   GRACE_MS    the away states (offline, reconnecting) show in place, and a strip for the client's own link.
export const HELD_MS = 600;
export const CUE_MS = 1000;
export const GRACE_MS = 3000;

export class ReturnGrace {
  #listeners = new Set();
  #timers = [];
  #since = null;
  #setTimeout;
  #clearTimeout;

  // now: the clock; timers: { setTimeout, clearTimeout }, both for tests to take over.
  constructor({ now = () => Date.now(), timers = globalThis } = {}) {
    this.now = now;
    this.#setTimeout = (fn, ms) => timers.setTimeout(fn, ms);
    this.#clearTimeout = (t) => timers.clearTimeout(t);
  }

  onChange(fn) { this.#listeners.add(fn); return () => this.#listeners.delete(fn); }

  // When the return that is being waited out began, or null once it is over.
  get since() { return this.#since; }
  get active() { return this.#since !== null; }
  // How long since the return, or null when none is on.
  get age() { return this.#since === null ? null : this.now() - this.#since; }
  // Whether a held control is drawn disabled now (it has been held HELD_MS) rather than only aria-disabled.
  get drawn() { return this.active && this.age >= HELD_MS; }

  // Listens for the events that are a return: the page shown again, restored from the back/forward
  // cache, or the network back. Returns what stops it.
  watch(win = globalThis) {
    const doc = win.document;
    const onVisible = () => { if (doc.visibilityState === 'visible') { this.start(); } };
    const onShow = (e) => { if (e.persisted) { this.start(); } };
    const onOnline = () => this.start();
    doc.addEventListener('visibilitychange', onVisible);
    win.addEventListener('pageshow', onShow);
    win.addEventListener('online', onOnline);
    return () => {
      doc.removeEventListener('visibilitychange', onVisible);
      win.removeEventListener('pageshow', onShow);
      win.removeEventListener('online', onOnline);
    };
  }

  // A return begins. One already on is left alone: another event in its 3 s (a visibility flip, then
  // the network back) must not snap a drawn control back to quiet or push the away states later.
  start() {
    if (this.#since !== null) { return; }
    this.#cancel();
    this.#since = this.now();
    for (const ms of [HELD_MS, CUE_MS]) { this.#timers.push(this.#setTimeout(() => this.#emit(), ms)); }
    this.#timers.push(this.#setTimeout(() => this.end(), GRACE_MS));
    this.#emit();
  }

  end() {
    if (this.#since === null) { return; }
    this.#cancel();
    this.#since = null;
    this.#emit();
  }

  #cancel() {
    for (const t of this.#timers) { this.#clearTimeout(t); }
    this.#timers = [];
  }

  #emit() { for (const fn of [...this.#listeners]) { fn(); } }
}
