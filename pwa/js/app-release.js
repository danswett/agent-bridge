// Release-state model for #1246. The caller owns registration and explicit reloads.
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
    this.lastCheck = -Infinity;
    this.takeoverTimer = null;
    this.stalled = false;
    this.installError = '';
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

  auto() {
    if (Date.now() - this.lastCheck < 5000) { return; }
    return this.check({ automatic: true });
  }

  async check({ automatic = false } = {}) {
    if (this.checking || this.disposed) { return; }
    const before = this.state;
    let latest;
    let failed = false;
    this.checking = true;
    this.lastCheck = Date.now();
    if (!automatic) { this.#set({ ...before, phase: 'checking', error: '' }); }
    try {
      const read = async () => {
        const response = await this.fetchVersion('version.json', { cache: 'no-store' });
        if (!response.ok) { throw new Error('Release request failed'); }
        return metadata(await response.json());
      };
      // Metadata failure must not stop the browser's independent installation retry.
      [latest] = await Promise.all([read(), Promise.resolve().then(() => {
        if (!this.disposed) { return this.registration?.update(); }
      })]);
      if (this.disposed) { return; }
      this.#watch(this.registration?.installing ?? this.registration?.waiting);
    } catch {
      failed = true;
    } finally {
      this.checking = false;
    }
    if (this.disposed) { return; }
    if (failed) {
      if (automatic) { this.#refresh(); return; }
      this.#set({ ...before, phase: this.#phase(before.latest), error: this.installError || "Couldn't check for updates." });
    } else if (automatic && latest.version === this.version && !this.worker && !this.activated && !this.installError) {
      if (before.latest?.version === this.version && !before.error) { return; }
      // A routine current result is silent; clear an older "newer version" line if rolled back.
      this.#set({ phase: 'idle', latest: null, error: '' });
    } else {
      this.#set({ phase: this.#phase(latest), latest, error: this.installError });
    }
  }

  installFailed(sentence) {
    if (this.disposed) { return; }
    this.installError = sentence;
    this.#refresh();
  }

  tookOver() {
    if (this.disposed) { return; }
    this.activated = true;
    this.#clearTakeover();
    this.#refresh();
  }

  dispose() {
    this.disposed = true;
    this.registration?.removeEventListener('updatefound', this.found);
    this.#watch(null);
  }

  #watch(worker) {
    // An already-active worker can be the old release; only its observed takeover proves readiness.
    if (worker === this.worker) { return; }
    this.#clearTakeover();
    this.worker?.removeEventListener('statechange', this.changed);
    this.worker = worker && INSTALLING.has(worker.state) ? worker : null;
    this.worker?.addEventListener('statechange', this.changed);
    this.#refresh();
  }

  #phase(latest) {
    // Takeover proves an update is ready, not that it is the latest published id.
    if (this.activated) { return 'ready'; }
    if (this.installError) { return 'failed'; }
    if (this.stalled) { return 'stalled'; }
    if (!latest) { return 'idle'; }
    if (latest.version === this.version) { return 'current'; }
    return this.worker && INSTALLING.has(this.worker.state) ? 'installing' : 'behind';
  }

  #refresh() {
    if (!this.worker || !['installed', 'activating'].includes(this.worker.state) || this.activated) {
      this.#clearTakeover();
    } else if (!this.disposed && this.takeoverTimer === null && !this.stalled) {
      this.takeoverTimer = setTimeout(() => {
        this.takeoverTimer = null;
        this.stalled = true;
        this.#refresh();
      }, 10000);
    }
    const phase = this.#phase(this.state.latest);
    if (!this.disposed && (!this.checking || ['ready', 'stalled', 'failed'].includes(phase))) {
      this.#set({ ...this.state, phase, error: this.installError });
    }
  }

  #clearTakeover() {
    clearTimeout(this.takeoverTimer);
    this.takeoverTimer = null;
    this.stalled = false;
  }

  #set(state) {
    if (state.phase === this.state.phase && state.latest === this.state.latest && state.error === this.state.error) { return; }
    this.state = state;
    this.onChange(state);
  }
}
