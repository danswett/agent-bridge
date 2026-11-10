// Release-state model for #1277. The caller owns registration and explicit reloads.
const SHA = /^(?:[0-9a-f]{40}|dev)$/;
const INSTALLING = new Set(['installing', 'installed', 'activating']);

function metadata(value) {
  if (!value || typeof value.version !== 'string' || !SHA.test(value.version)
    || typeof value.builtAt !== 'string' || (value.builtAt && (
      !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(?:\.\d+)?(?:Z|[+-]\d\d:\d\d)$/.test(value.builtAt)
      || Number.isNaN(new Date(value.builtAt).getTime())))) {
    throw new Error('Invalid release metadata');
  }
  return { version: value.version, builtAt: value.builtAt };
}

export class AppRelease {
  constructor({ version, fetchVersion = globalThis.fetch, onChange = () => {} } = {}) {
    this.version = version;
    this.fetchVersion = fetchVersion;
    this.onChange = onChange;
    this.state = { phase: 'idle', latest: null, error: '' };
    this.checking = false;
    this.disposed = false;
    this.registration = null;
    this.worker = null;
    this.activated = false;
    this.found = () => this.#watch(this.registration.installing);
    this.changed = () => {
      if (this.worker.state === 'activated') { this.tookOver(); }
      else { this.#refresh(); }
    };
  }

  attach(registration) {
    if (this.disposed) { return; }
    this.registration?.removeEventListener('updatefound', this.found);
    this.#watch(null);
    this.registration = registration;
    registration?.addEventListener('updatefound', this.found);
    this.#watch(registration?.installing ?? registration?.waiting);
  }

  async check() {
    if (this.checking || this.disposed) { return; }
    const before = this.state;
    let latest;
    let failed = false;
    this.checking = true;
    this.#set({ ...before, phase: 'checking', error: '' });
    try {
      const response = await this.fetchVersion('version.json', { cache: 'no-store' });
      if (!response.ok) { throw new Error('Release request failed'); }
      latest = metadata(await response.json());
      if (this.disposed) { return; }
      await this.registration?.update();
      if (this.disposed) { return; }
      this.#watch(this.registration?.installing ?? this.registration?.waiting);
    } catch {
      failed = true;
    } finally {
      this.checking = false;
    }
    if (this.disposed) { return; }
    if (failed) {
      this.#set({ ...before, error: "Couldn't check for updates." });
    } else {
      this.#set({ phase: this.#phase(latest), latest, error: '' });
    }
  }

  tookOver() {
    if (this.disposed) { return; }
    this.activated = true;
    this.#refresh();
  }

  dispose() {
    this.registration?.removeEventListener('updatefound', this.found);
    this.#watch(null);
    this.disposed = true;
  }

  #watch(worker) {
    // An already-active worker can be the old release; only its observed takeover proves readiness.
    if (worker === this.worker) { return; }
    this.worker?.removeEventListener('statechange', this.changed);
    this.worker = worker && INSTALLING.has(worker.state) ? worker : null;
    this.worker?.addEventListener('statechange', this.changed);
    this.#refresh();
  }

  #phase(latest) {
    // Takeover proves an update is ready, not that it is the latest published id.
    if (this.activated) { return 'ready'; }
    if (!latest) { return 'idle'; }
    if (latest.version === this.version) { return 'current'; }
    return this.worker && INSTALLING.has(this.worker.state) ? 'installing' : 'behind';
  }

  #refresh() {
    if (!this.checking && !this.disposed) {
      this.#set({ ...this.state, phase: this.#phase(this.state.latest) });
    }
  }

  #set(state) {
    if (state.phase === this.state.phase && state.latest === this.state.latest && state.error === this.state.error) { return; }
    this.state = state;
    this.onChange(state);
  }
}
