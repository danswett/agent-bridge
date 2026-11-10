// How long a turn takes from this app (#1600): the moment the person sends a reply to the moment the
// session settles again, as one number. timing.js times the delivery (tap to the machine's
// outcome); this times what the person waits for, the agent's whole turn.
//
// Only the duration, settlement time and app's name are kept: no text, machine or session id. The ids
// below key a turn still running and are dropped the moment it settles, expires or its session goes.
// Kept in this page until the minute upload to the relay's aggregate store (#1603).
// Where it runs: in the page that sent the reply. If the page closes, turns in
// flight are lost and nothing else: no state lives on any machine.

export const APP = 'PWA';

const KEEP = 100;
// A turn that never settles in view (the page was away, the session ended) is not a measurement.
const EXPIRE_MS = 60 * 60 * 1000;

export class TurnClock {
  #running = new Map();
  #done = [];
  #now;

  constructor({ now = () => Date.now() } = {}) { this.#now = now; }

  #key(hostId, sessionId) { return JSON.stringify([hostId, sessionId]); }

  // The person sent a reply to this session. A second send before the first settles keeps the
  // first's start: the turn is still the one the person has been waiting on.
  sent(hostId, sessionId) {
    const key = this.#key(hostId, sessionId);
    const now = this.#now();
    const turn = this.#running.get(key);
    if (!turn || now - turn.start > EXPIRE_MS) { this.#running.set(key, { start: now, working: false }); }
  }

  // The session's status as last seen. Working starts the clock's second half; the first status
  // after that which is not working is the reply settling.
  seen(hostId, sessionId, status) {
    const key = this.#key(hostId, sessionId);
    const turn = this.#running.get(key);
    if (!turn) { return; }
    const now = this.#now();
    const ms = now - turn.start;
    if (ms > EXPIRE_MS) { this.#running.delete(key); return; }
    if (status === 'working') { turn.working = true; return; }
    if (!turn.working) { return; }
    this.#running.delete(key);
    this.#done.push({ app: APP, ms: Math.max(0, Math.round(ms)), at: Math.floor(now / 1000) });
    while (this.#done.length > KEEP) { this.#done.shift(); }
  }

  // The session is gone: its turn will never settle.
  dropped(hostId, sessionId) { this.#running.delete(this.#key(hostId, sessionId)); }

  // Settled turns, oldest first: [{ app, ms, at }].
  recent() { return this.#done.map((t) => ({ ...t })); }

  // Settled turns, handed over once: what a reporter takes and does not take again.
  drain() { const out = this.recent(); this.#done.length = 0; return out; }
}
