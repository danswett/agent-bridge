// The connected host's live sessions, as the views see them. Fed by HostClient; it knows
// nothing about channels. Every change is announced to listeners as { type, id? }.
import { readSession } from './protocol.js';

// When a session last did something that matters to the person (#628): a prompt, a finished turn,
// a question or approval asked or answered, a change of status. A tool call, a reasoning or output
// tick, a background count, a title, model or usage change is not one. Tick-driven updatedAt
// moved a card on every tool call; this moves it only for these.
export function mattered(prev, next) {
  return prev.status !== next.status
    || (prev.question?.id ?? null) !== (next.question?.id ?? null)
    || prev.activity.userMessage !== next.activity.userMessage
    || prev.activity.turn !== next.activity.turn
    || prev.activity.turnCount !== next.activity.turnCount;
}

// Newest activity first; ties by id, then machine, so the order is stable. Times are in one fixed
// format, so they compare as strings. a, b: { hostId, session }.
export function byActivity(a, b) {
  const x = a.session, y = b.session;
  const tx = x.activityAt ?? x.updatedAt, ty = y.activityAt ?? y.updatedAt;
  if (tx !== ty) { return tx < ty ? 1 : -1; }
  if (x.id !== y.id) { return x.id < y.id ? -1 : 1; }
  return (a.hostId ?? '') < (b.hostId ?? '') ? -1 : (a.hostId ?? '') > (b.hostId ?? '') ? 1 : 0;
}

export class SessionsStore {
  #sessions = new Map();
  // Per session id, kept across a reconnect (clear) so it never moves back.
  #activityAt = new Map();
  #listeners = new Set();
  revision = null;
  // What holds the host's discovery ({ reason, agents }), or null.
  held = null;
  // The run of the host the list came from (hello's machine.instance), and whether the list is its
  // whole list at revision: what a reconnect to that run resumes from (sessions.subscribe's since).
  instance = null;
  whole = false;

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

  // sessions.subscribe's since for that run of the host, or null when the list is not whole from it.
  resumeFrom(instance) {
    return this.whole && typeof instance === 'string' && instance === this.instance && Number.isSafeInteger(this.revision) ? { instance, revision: this.revision } : null;
  }
  get(id) { return this.#sessions.get(id) ?? null; }
  has(id) { return this.#sessions.has(id); }

  // Latest activity first: the Most recent sort. What the list shows by default is the
  // person's order (session-order.js).
  list() {
    return [...this.#sessions.values()].map((session) => ({ hostId: '', session })).sort(byActivity).map((e) => e.session);
  }

  // Returns the session as stored (with activityAt), or null when the payload had no usable id.
  // snapshot: the session came in a snapshot, which is not an event: a first-seen session starts
  // at its updatedAt, and a known one keeps its activityAt.
  upsert(raw, { snapshot = false } = {}) {
    const s = readSession(raw);
    if (!s) { return null; }
    const prev = this.#sessions.get(s.id);
    const was = this.#activityAt.get(s.id);
    let at = was ?? s.updatedAt;
    if (was !== undefined && !snapshot && prev && mattered(prev, s) && s.updatedAt > at) { at = s.updatedAt; }
    this.#activityAt.set(s.id, at);
    // Not enumerable: a session compares and copies as the host's fields alone.
    Object.defineProperty(s, 'activityAt', { value: at });
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
    this.#activityAt.delete(id);
    this.#emit({ type: 'remove', id, session });
    return true;
  }

  // Drops every session not in `ids`: what a completed snapshot means. Those dropped have
  // ended, and go with the change as `removed`.
  retain(ids) {
    const removed = [];
    for (const [id, s] of [...this.#sessions]) {
      if (!ids.has(id)) { this.#sessions.delete(id); this.#activityAt.delete(id); removed.push(s); }
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

  // A device flow's signin.code or signin.done (#800), passed on to whoever shows it and never kept:
  // a code lives only as long as the flow on screen, and never in the list.
  signIn(ev, data) { this.#emit({ type: 'signin', ev, data }); }

  clear() {
    this.#sessions.clear();
    this.revision = null;
    this.held = null;
    this.instance = null;
    this.whole = false;
    this.#emit({ type: 'clear' });
  }
}
