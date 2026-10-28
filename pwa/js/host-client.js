// A client of one Bridge Host over one transport: request/response plus the events a
// subscription delivers. Protocol: docs/protocol/v1-sessions.md.
//
// The transport is anything with `send(message) -> Promise`, `close()` and settable
// `onmessage(message)` / `onclose(error)` callbacks, carrying already-parsed JSON. In the
// app that is an end-to-end Noise channel (channels.js); in demo mode and the unit tests
// it is an in-process fake. Nothing here knows which.
import { Events, Ops, readHello, readSession } from './protocol.js';

export class HostError extends Error {
  constructor(op, message) {
    super(`${op}: ${message}`);
    this.op = op;
    this.reason = message;
  }
}

export class HostClient {
  #transport;
  #pending = new Map();
  #next = 0;
  #timeoutMs;
  #listeners = new Map();
  #lastSeq = null;
  // While a subscription's snapshot is arriving: the ids received since subscribing.
  #snapshot = null;
  #resubscribing = null;
  closed = false;
  synced = false;

  constructor(transport, { store, timeoutMs = 15000 } = {}) {
    this.#transport = transport;
    this.store = store;
    this.#timeoutMs = timeoutMs;
    transport.onmessage = (m) => this.#receive(m);
    transport.onclose = (e) => this.#closed(e);
  }

  on(type, fn) {
    if (!this.#listeners.has(type)) { this.#listeners.set(type, new Set()); }
    this.#listeners.get(type).add(fn);
    return () => this.#listeners.get(type)?.delete(fn);
  }

  #emit(type, detail) {
    for (const fn of this.#listeners.get(type) ?? []) {
      try { fn(detail); } catch (e) { console.error(e); }
    }
  }

  request(op, data = {}) {
    if (this.closed) { return Promise.reject(new HostError(op, 'the channel is closed')); }
    const id = ++this.#next;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        if (this.#pending.delete(id)) { reject(new HostError(op, 'timed out')); }
      }, this.#timeoutMs);
      this.#pending.set(id, { op, resolve, reject, timer });
      // A send that fails (the relay went away under the channel) fails this request at
      // once, rather than leaving it to time out: S5's sleeping-iPhone lesson.
      Promise.resolve().then(() => this.#transport.send({ id, op, data })).catch((e) => {
        const p = this.#pending.get(id);
        if (p) { this.#pending.delete(id); clearTimeout(p.timer); reject(new HostError(op, e.message)); }
      });
    });
  }

  async hello() { return readHello(await this.request(Ops.hello)); }

  // Every page of sessions.list. Not used by the live view, which subscribes instead (pages
  // can overlap or miss a session if anything changes between them), but kept for one-shot
  // readers.
  async listAll({ maxPages = 100 } = {}) {
    const sessions = [];
    let cursor, revision = null;
    for (let page = 0; page < maxPages; page++) {
      const r = await this.request(Ops.sessionsList, cursor ? { cursor } : {});
      revision = r?.revision ?? revision;
      for (const s of Array.isArray(r?.sessions) ? r.sessions : []) {
        const read = readSession(s);
        if (read) { sessions.push(read); }
      }
      if (typeof r?.next !== 'string' || r.next === '') { return { sessions, revision }; }
      cursor = r.next;
    }
    throw new HostError(Ops.sessionsList, 'too many pages');
  }

  // The full session with its tool history, or null once it has ended.
  async getSession(id) {
    const r = await this.request(Ops.sessionGet, { id });
    return r?.missing === true ? null : readSession(r);
  }

  // Starts (or restarts) the subscription. The host answers {revision, count}, then sends
  // one upsert per live session and `sessions.synced`. Upserts are shown as they arrive;
  // on synced, any session from before that the snapshot did not include is dropped.
  async subscribe() {
    this.#snapshot = new Set();
    this.#lastSeq = null;
    this.synced = false;
    return this.request(Ops.sessionsSubscribe);
  }

  close() {
    if (this.closed) { return; }
    try { this.#transport.close?.(); } finally { this.#closed(new Error('closed')); }
  }

  #closed(error) {
    if (this.closed) { return; }
    this.closed = true;
    for (const [, p] of this.#pending) { clearTimeout(p.timer); p.reject(new HostError(p.op, error?.message ?? 'the channel closed')); }
    this.#pending.clear();
    this.#emit('close', error);
  }

  #receive(m) {
    if (m === null || typeof m !== 'object' || Array.isArray(m)) { return; }
    if (Number.isInteger(m.re)) {
      const p = this.#pending.get(m.re);
      if (!p) { return; }
      this.#pending.delete(m.re);
      clearTimeout(p.timer);
      if (m.ok === true) { p.resolve(m.result); }
      else { p.reject(new HostError(p.op, typeof m.error === 'string' ? m.error : 'failed')); }
      return;
    }
    if (typeof m.ev === 'string') { this.#event(m); }
  }

  #event(m) {
    // Noise is ordered, so a gap means the host lost events, not the network. The only
    // way back to a known state is a fresh snapshot.
    let gap = false;
    if (Number.isInteger(m.seq)) {
      gap = this.#lastSeq !== null && m.seq !== this.#lastSeq + 1;
      this.#lastSeq = m.seq;
    }
    const data = m.data !== null && typeof m.data === 'object' ? m.data : {};
    switch (m.ev) {
      case Events.sessionUpsert: {
        const s = this.store?.upsert(data);
        if (s) { this.#snapshot?.add(s.id); }
        break;
      }
      case Events.sessionRemove:
        this.store?.remove(data.id);
        this.#snapshot?.delete(data.id);
        break;
      case Events.sessionsSynced:
        if (this.#snapshot) {
          if (this.store) {
            this.store.revision = Number.isFinite(data.revision) ? data.revision : this.store.revision;
            this.store.retain(this.#snapshot);
          }
          this.#snapshot = null;
          this.synced = true;
          this.#emit('synced', data);
        }
        break;
      default:
        // An event this client doesn't know, from a newer host. It still counted for seq.
        break;
    }
    this.#emit('event', m);
    if (gap) { this.#resubscribe(m.seq); }
  }

  #resubscribe(seq) {
    if (this.#resubscribing || this.closed) { return; }
    this.#emit('gap', { seq });
    this.#resubscribing = this.subscribe()
      .catch((e) => this.#emit('error', e))
      .finally(() => { this.#resubscribing = null; });
  }
}
