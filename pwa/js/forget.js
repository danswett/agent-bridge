// Forgetting one of the account's machines that is offline (#50, docs/protocol/roster.md).
// Two presses, as End session takes: the first arms that machine's Forget for a few seconds,
// the second asks the relay. The relay takes a forget only from a person's device (this app
// is one) and only for a machine that is not connected. A machine that comes back leaves the
// offline list, and its arm goes with it.

export const CONFIRM_MS = 5000;

const REASONS = {
  online: 'It is back online, so it was not forgotten.',
  'not-found': 'The relay no longer lists it.',
  'rate-limited': 'Too many machines forgotten just now. Try again in a minute.',
  'not-allowed': "The relay refused: only a person's device can forget a machine.",
  unsupported: 'The relay cannot forget machines yet.',
  disabled: 'The relay cannot forget machines yet.',
  unavailable: 'The relay cannot reach its list of machines just now. Try again in a minute.',
  unreachable: 'The relay did not answer. Try again when connected.',
};

// The machines gone from the offline list without coming online: forgotten (from this device
// or another), or past the relay's cap. Only between two lists from a relay with a roster: a
// relay without one (null) says nothing about what was forgotten.
export function vanished(before, after, online) {
  if (!before || !after) { return []; }
  const still = new Set(after.map((k) => k.id));
  const here = new Set(online);
  return before.map((k) => k.id).filter((id) => !still.has(id) && !here.has(id));
}

export function forgetReason(code) {
  return REASONS[code] ?? `The relay refused: ${code}.`;
}

export class ForgetControl {
  armed = null;
  busy = null;
  #armedUntil = 0;
  #errors = new Map();
  #timer = null;
  #listeners = new Set();

  // onForgotten(id): once the relay has forgotten it, for what this app keeps about it.
  constructor({ connection, onForgotten = () => {}, confirmMs = CONFIRM_MS, timers = globalThis, now = () => Date.now() }) {
    Object.assign(this, { connection, onForgotten, confirmMs, timers, now });
  }

  onChange(fn) { this.#listeners.add(fn); return () => this.#listeners.delete(fn); }

  #emit() { for (const fn of this.#listeners) { try { fn(); } catch (e) { console.error(e); } } }

  // { armed, busy, error } for one machine's row.
  stateOf(id) {
    return { armed: this.armed === id, busy: this.busy === id, error: this.#errors.get(id) ?? null };
  }

  async press(id) {
    if (this.busy) { return; }
    // A background tab's timers are held back, so the one that lapses an arm may not have run:
    // a press after the deadline arms again whatever it says.
    if (this.armed && this.now() >= this.#armedUntil) { this.#disarm(); }
    if (this.armed !== id) {
      this.#disarm();
      this.armed = id;
      this.#armedUntil = this.now() + this.confirmMs;
      this.#errors.delete(id);
      this.#timer = this.timers.setTimeout(() => { if (this.armed === id) { this.armed = null; this.#emit(); } }, this.confirmMs);
      this.#emit();
      return;
    }
    this.#disarm();
    this.busy = id;
    this.#emit();
    let why;
    try { why = await this.connection.forget(id); } catch { why = 'unreachable'; }
    this.busy = null;
    if (why) { this.#errors.set(id, forgetReason(why)); } else {
      this.#errors.delete(id);
      try { this.onForgotten(id); } catch (e) { console.error(e); }
    }
    this.#emit();
  }

  // The machines offline now: an arm or a refusal for one no longer listed (back online, or
  // forgotten from another device) goes.
  reconcile(offlineIds) {
    const ids = new Set(offlineIds);
    let changed = false;
    if (this.armed && !ids.has(this.armed)) { this.#disarm(); changed = true; }
    for (const id of [...this.#errors.keys()]) {
      if (!ids.has(id)) { this.#errors.delete(id); changed = true; }
    }
    if (changed) { this.#emit(); }
  }

  clear() {
    this.#disarm();
    this.#errors.clear();
    this.#emit();
  }

  #disarm() {
    this.timers.clearTimeout(this.#timer);
    this.#timer = null;
    this.armed = null;
  }
}
