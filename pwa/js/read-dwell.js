// When a session counts as read (docs/design/session-order-roaming.md §8 rule 1): it is open,
// visible and focused, and what it shows has been on screen for about a second. News that lands
// during the second restarts it; leaving the screen cancels it. No DOM and no clock of its own:
// the timers are handed in, so tests advance them and never sleep. Reading only calls mark();
// it never answers a question, and never moves a row (Most recent sorts by activity, not reads).
export const DWELL_MS = 1000;

export class ReadDwell {
  #pending = null;

  // mark(key, updatedAt): what to do when the dwell is up. timers: read at call time, so a test's
  // fake clock that was installed after this was made still counts.
  constructor({ mark, ms = DWELL_MS, timers = null }) {
    Object.assign(this, { mark, ms, timers });
  }

  // Called on every draw and on focus or visibility changes. on: whether the session is open,
  // visible and focused right now; updatedAt: the newest news it shows.
  watch(key, updatedAt, on) {
    const p = this.#pending;
    if (!on || typeof updatedAt !== 'string') { this.cancel(); return; }
    if (p && p.key === key && p.updatedAt === updatedAt) { return; }
    this.cancel();
    const t = this.timers ?? globalThis;
    const handle = t.setTimeout(() => {
      this.#pending = null;
      this.mark(key, updatedAt);
    }, this.ms);
    this.#pending = { key, updatedAt, handle, t };
  }

  cancel() {
    const p = this.#pending;
    if (!p) { return; }
    this.#pending = null;
    p.t.clearTimeout(p.handle);
  }
}
