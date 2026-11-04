// Whether new releases of the app keep failing to install (#650). A release installs whole or
// not at all (sw.js): one file of its shell missing from a deploy, and its worker goes, leaving
// the app on the release it has. The browser tries again at every start, quietly, so a person
// could stay on an old release for good without knowing. After a few failures in a row, the
// app says so; a release that installs clears the count.
export const STUCK_AFTER = 3;
// Remembered across starts, each start trying once, with the release in control as they were
// counted: a release can install with no page open to see it (the browser checks for one when a
// push arrives, and finishes an install begun before the app was closed), and a count kept under
// the release before is no count under the one after.
const FAILURES = 'agent-bridge.app.installFailures';

export class ReleaseWatch {
  // release: the release in control (sw.js's cache for it, from the worker's record), or null.
  constructor({ storage = globalThis.localStorage, release = null, onChange = () => {} } = {}) {
    this.storage = storage;
    this.release = release;
    this.onChange = onChange;
    this.watched = new WeakSet();
    let kept = null;
    try { kept = JSON.parse(storage.getItem(FAILURES)); } catch { /* none kept, or unreadable */ }
    const n = Math.floor(Number(kept?.failures));
    this.failures = kept?.release === release && n > 0 ? n : 0;
  }

  get stuck() { return this.failures >= STUCK_AFTER; }

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

  // A release installed, or took over.
  installed() {
    if (this.failures) { this.#count(0); }
  }

  #count(n) {
    this.failures = n;
    try {
      if (n) { this.storage.setItem(FAILURES, JSON.stringify({ release: this.release, failures: n })); } else { this.storage.removeItem(FAILURES); }
    } catch { /* storage blocked or full: the count lasts as long as this page */ }
    this.onChange(this.stuck);
  }
}
