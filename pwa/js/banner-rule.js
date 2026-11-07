// The banner rule (ui.md §7, #1064): only a lasting state gets a banner. One that is wanted shows
// only once it has stood for BANNER_GRACE_MS, so a state that comes and goes inside that never
// draws one, and once shown it stays at least BANNER_MIN_MS, so it never flickers away as soon as
// it has been read. A state that goes while it waits shows nothing at all.
export const BANNER_GRACE_MS = 3000;
export const BANNER_MIN_MS = 10000;

export class LastingBanner {
  #shownAt = null;
  #timer = null;
  #want = false;

  // show(), hide(): what draws and takes away the banner. now, timers: the clock and the timers,
  // for tests to take over.
  constructor({ show, hide, graceMs = BANNER_GRACE_MS, minMs = BANNER_MIN_MS, now = () => Date.now(), timers = globalThis }) {
    Object.assign(this, { show, hide, graceMs, minMs, now, timers });
  }

  get shown() { return this.#shownAt !== null; }

  // Whether the state the banner stands for holds now.
  set(wanted) {
    this.#want = !!wanted;
    if (this.#want === this.shown && this.#timer === null) { return; }
    if (this.#want) {
      // Back while it waited to go: it stays.
      if (this.shown) { this.#clear(); return; }
      if (this.#timer === null) { this.#timer = this.timers.setTimeout(() => this.#settle(), this.graceMs); }
      return;
    }
    this.#clear();
    if (!this.shown) { return; }
    const left = this.#shownAt + this.minMs - this.now();
    if (left <= 0) { this.#settle(); return; }
    this.#timer = this.timers.setTimeout(() => this.#settle(), left);
  }

  #clear() {
    if (this.#timer !== null) { this.timers.clearTimeout(this.#timer); }
    this.#timer = null;
  }

  #settle() {
    this.#timer = null;
    if (this.#want && !this.shown) {
      this.#shownAt = this.now();
      this.show();
    } else if (!this.#want && this.shown) {
      this.#shownAt = null;
      this.hide();
    }
  }
}
