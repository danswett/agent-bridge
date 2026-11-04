// How long the relay connection waits before it tries again (#578), as the host and the Windows
// app do (ReconnectBackoff.cs). Full jitter: a uniform wait between 0 and min(30 s, 1 s * 2^n).
// Without it every device retried at the same 1, 2, 4... s marks after a deploy closed them all
// with 1012 at once: a reconnect storm the relay brought on itself, which also tripped its
// per-IP connect limit for devices behind one NAT.

export const BASE_MS = 1000;
export const CAP_MS = 30000;
export const RESTARTING = 1012;
// After a 1012 the first retry is spread over this window, or over the relay's own hint.
export const RESTART_SPREAD_MS = 5000;
export const MIN_SPREAD_S = 1;
export const MAX_SPREAD_S = 60;

// min(cap, base * 2^attempt).
export function ceilingMs(attempt) {
  return attempt >= 32 ? CAP_MS : Math.min(CAP_MS, BASE_MS * 2 ** Math.max(0, attempt));
}

// A 1012's reason "restarting;spread=N" lets the relay widen the spread without a client
// release. The relay is not trusted, so anything else is ignored and N is clamped to 1-60 s.
export function spreadFromMs(reason) {
  if (typeof reason !== 'string' || reason.length === 0 || reason.length > 123) { return null; }
  const m = /^restarting;spread=([0-9]{1,9})$/.exec(reason);
  if (!m) { return null; }
  return Math.min(MAX_SPREAD_S, Math.max(MIN_SPREAD_S, Number(m[1]))) * 1000;
}

export class ReconnectBackoff {
  #random;
  attempt = 0;

  // random: uniform in [0, 1); injectable so a test can pin the schedule.
  constructor(random = Math.random) { this.#random = random; }

  // Only after a successful connection, never after a failed try.
  reset() { this.attempt = 0; }

  // The wait in ms before the next try. code and reason are how the last connection ended.
  next(code, reason) {
    let ceiling = ceilingMs(this.attempt);
    if (code === RESTARTING) { ceiling = Math.max(ceiling, spreadFromMs(reason) ?? RESTART_SPREAD_MS); }
    this.attempt += 1;
    let r = this.#random();
    // A source outside [0, 1) must not stretch the wait past its ceiling or make it negative.
    if (!(r >= 0) || r >= 1) { r = 0; }
    return Math.floor(ceiling * r);
  }
}
