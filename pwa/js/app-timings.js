// Best-effort aggregate upload. The caller ticks on the page's lifetime; no queue survives it.
export class AppTimings {
  #last = null;
  #busy = false;

  constructor({ fetch = (...args) => globalThis.fetch(...args), now = () => performance.now(), log = (line) => console.debug(line) } = {}) {
    Object.assign(this, { fetch, now, log });
  }

  async post(base, token, drain) {
    if (!base || !token) { drain(); return; }
    const now = this.now();
    if (this.#busy || (this.#last !== null && now - this.#last < 60000)) { return; }
    const samples = drain().slice(0, 50).map((s) => ({
      app: 'PWA', route: 'send-to-settled', ms: Math.min(600000, Math.max(0, Math.round(s.ms))), at: s.at,
    }));
    let body = JSON.stringify({ samples });
    while (new TextEncoder().encode(body).length > 4096) { samples.pop(); body = JSON.stringify({ samples }); }
    if (!samples.length) { return; }
    this.#last = now;
    this.#busy = true;
    try {
      const response = await this.fetch(`${base.replace(/\/$/, '')}/v1/stats/app-timings`, {
        method: 'POST', headers: { Authorization: `Bearer ${token}`, 'Content-Type': 'application/json' },
        body, credentials: 'omit', redirect: 'error', cache: 'no-store', signal: AbortSignal.timeout(10000),
      });
      if (response.status !== 202) { this.log(`app timings: HTTP ${response.status}; batch dropped`); }
    } catch (e) {
      if (!(e instanceof TypeError) && !['AbortError', 'TimeoutError'].includes(e?.name)) { throw e; }
      this.log('app timings: request failed; batch dropped');
    } finally { this.#busy = false; }
  }
}
