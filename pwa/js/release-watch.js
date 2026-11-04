// Whether new releases of the app keep failing to install (#650). A release installs whole or
// not at all (sw.js): one file of its shell missing from a deploy, and its worker goes, leaving
// the app on the release it has. The browser tries again at every start, quietly, so a person
// could stay on an old release for good without knowing. After a few failures in a row, the
// app says so; a release that installs clears the count.
export const STUCK_AFTER = 3;
// Remembered across starts, each start trying once, with the release in control as they were
// counted: a release can install with no page open to see it (the browser checks for one when a
// push arrives, and finishes an install begun before the app was closed), and a count kept under
// one release is no count under another.
const FAILURES = 'agent-bridge.app.installFailures';

// localStorage, or null where the browser blocks it: even reading the property can throw.
function localStorageOrNull() {
  try { return globalThis.localStorage ?? null; } catch { return null; }
}

export class ReleaseWatch {
  constructor({ storage = localStorageOrNull(), onChange = () => {} } = {}) {
    this.storage = storage;
    this.onChange = onChange;
    this.watched = new WeakSet();
    // The release in control (sw.js's cache for it, from the worker's record), once known:
    // undefined until then, null for a worker that keeps no record. Failures seen before it is
    // known are counted here, and kept once it is.
    this.release = undefined;
    this.failures = 0;
  }

  get stuck() { return this.failures >= STUCK_AFTER; }

  // The release in control is known: the count kept under it, if that is the one kept, and any
  // failures seen meanwhile.
  under(release) {
    this.release = release;
    let kept = null;
    try { kept = JSON.parse(this.storage?.getItem(FAILURES) ?? 'null'); } catch { /* unreadable */ }
    const n = Math.floor(Number(kept?.failures));
    this.#count((kept?.release === release && n > 0 ? n : 0) + this.failures);
  }

  // A worker installing a release (the registration's installing worker). One that goes from
  // installing straight to redundant failed; one that got as far as installed did not, even
  // when a later release replaces it.
  watch(worker) {
    if (!worker || this.watched.has(worker)) { return; }
    this.watched.add(worker);
    let was = worker.state;
    worker.addEventListener('statechange', () => {
      if (worker.state === 'installed' || worker.state === 'activated') {
        this.installed();
      } else if (worker.state === 'redundant' && was === 'installing') {
        this.#count(this.failures + 1);
      }
      was = worker.state;
    });
  }

  // A release installed: what was kept goes too, even before the release in control is known.
  installed() {
    this.#forget();
    this.#count(0);
  }

  // A release took over: nothing failed, and which release is in control is learnt again
  // (under), from its record.
  tookOver() {
    this.#forget();
    this.release = undefined;
    this.#count(0);
  }

  #forget() {
    try { this.storage?.removeItem(FAILURES); } catch { /* storage blocked: nothing was kept */ }
  }

  #count(n) {
    this.failures = n;
    if (this.release !== undefined) {
      try {
        if (n) { this.storage?.setItem(FAILURES, JSON.stringify({ release: this.release, failures: n })); } else { this.storage?.removeItem(FAILURES); }
      } catch { /* storage blocked or full: the count lasts as long as this page */ }
    }
    this.onChange(this.stuck);
  }
}
