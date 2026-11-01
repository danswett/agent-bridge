// The connected host's live sessions, as the views see them. Fed by HostClient; it knows
// nothing about channels. Every change is announced to listeners as { type, id? }.
import { readSession } from './protocol.js';

export class SessionsStore {
  #sessions = new Map();
  #listeners = new Set();
  revision = null;
  // What holds the host's discovery ({ reason, agents }), or null.
  held = null;

  onChange(fn) {
    this.#listeners.add(fn);
    return () => this.#listeners.delete(fn);
  }

  #emit(change) {
    for (const fn of this.#listeners) {
      try { fn(change); } catch (e) { console.error(e); }
    }
  }

  get size() { return this.#sessions.size; }
  get(id) { return this.#sessions.get(id) ?? null; }
  has(id) { return this.#sessions.has(id); }

  // Most recently updated first: the Most recent sort. What the list shows by default is the
  // person's order (session-order.js). Times are in one fixed format, so they compare as strings.
  list() {
    return [...this.#sessions.values()].sort((a, b) =>
      a.updatedAt < b.updatedAt ? 1 : a.updatedAt > b.updatedAt ? -1 : a.id < b.id ? -1 : a.id > b.id ? 1 : 0);
  }

  // Returns the session as stored, or null when the payload had no usable id.
  upsert(raw) {
    const s = readSession(raw);
    if (!s) { return null; }
    this.#sessions.set(s.id, s);
    this.#emit({ type: 'upsert', id: s.id });
    return s;
  }

  // The change carries the session as it last was: the list keeps it in its Ended group
  // (session-order.js).
  remove(id) {
    const session = typeof id === 'string' ? this.#sessions.get(id) : undefined;
    if (!session) { return false; }
    this.#sessions.delete(id);
    this.#emit({ type: 'remove', id, session });
    return true;
  }

  // Drops every session not in `ids`: what a completed snapshot means. Those dropped have
  // ended, and go with the change as `removed`.
  retain(ids) {
    const removed = [];
    for (const [id, s] of [...this.#sessions]) {
      if (!ids.has(id)) { this.#sessions.delete(id); removed.push(s); }
    }
    this.#emit({ type: 'synced', changed: removed.length > 0, removed });
  }

  // Announced only when it changes.
  setHeld(held) {
    const was = this.held;
    if ((was?.reason ?? null) === (held?.reason ?? null) && (was?.agents ?? []).join() === (held?.agents ?? []).join()) { return; }
    this.held = held ?? null;
    this.#emit({ type: 'held' });
  }

  clear() {
    this.#sessions.clear();
    this.revision = null;
    this.held = null;
    this.#emit({ type: 'clear' });
  }
}
