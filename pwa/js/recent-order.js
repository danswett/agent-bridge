import { orderKey } from './session-order.js';

const groups = ['pinned', 'sessions'];
const key = (e) => orderKey(e.hostId, e.session.id);

export class RecentOrder {
  #sort = null;
  #shown = { pinned: [], sessions: [] };
  #questions = new Map();
  #last = -Infinity;
  constructor({ now = () => Date.now() } = {}) { this.now = now; }
  wait = null;
  moved = new Set();

  arrange(target, { sort, held = false } = {}) {
    this.wait = null;
    this.moved = new Set();
    const previous = this.#questions;
    this.#questions = new Map(groups.flatMap((g) => target[g].map((e) => [key(e), e.session.question?.id ?? null])));
    const reset = this.#sort !== sort;
    this.#sort = sort;
    let shown = target;
    if (sort === 'recent' && !reset) {
      shown = { ...target };
      for (const g of groups) {
        const entries = new Map(target[g].map((e) => [key(e), e]));
        const bypass = target[g].filter((e) => !this.#shown[g].includes(key(e))
          || (e.session.question?.id && previous.get(key(e)) !== e.session.question.id));
        for (const e of bypass) { this.moved.add(key(e)); }
        shown[g] = [...bypass, ...this.#shown[g].filter((k) => entries.has(k) && !this.moved.has(k)).map((k) => entries.get(k))];
      }
      const pending = groups.some((g) => shown[g].some((e, i) => key(e) !== key(target[g][i])));
      const remaining = this.#last + 5000 - this.now();
      if (pending && !held && remaining <= 0 && !this.moved.size) {
        shown = target;
        this.moved = new Set(groups.flatMap((g) => target[g].filter((e, i) => key(e) !== this.#shown[g][i]).map(key)));
        this.#last = this.now();
      } else if (pending && !held) { this.wait = Math.max(0, remaining); }
    } else { this.#last = -Infinity; }
    this.#shown = Object.fromEntries(groups.map((g) => [g, shown[g].map(key)]));
    return shown;
  }
}
