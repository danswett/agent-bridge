// A client of one Bridge Host over one transport: request/response plus the events a
// subscription delivers. Protocol: docs/protocol/v1-sessions.md.
//
// The transport is anything with `send(message) -> Promise`, `close()` and settable
// `onmessage(message)` / `onclose(error)` callbacks, carrying already-parsed JSON. In the
// app that is an end-to-end Noise channel (channels.js); in demo mode and the unit tests
// it is an in-process fake. Nothing here knows which.
import { Events, Ops, readAnswerResult, readAttachmentState, readDiscoveryHeld, readHello, readSession, readStopResult } from './protocol.js';
import { USAGE, readUsage } from './usage.js';
import { UPDATE, readUpdateState } from './update.js';
import { LAUNCH, TRUST, readLaunchOptions, readLaunchResult, readTrustResult } from './launch.js';

export class HostError extends Error {
  // fromHost: the host answered with ok:false. Otherwise the request never got an answer
  // (timeout, closed channel, failed send), which says nothing about what the host supports.
  constructor(op, message, { fromHost = false } = {}) {
    super(`${op}: ${message}`);
    this.op = op;
    this.reason = message;
    this.fromHost = fromHost;
  }
}

export class HostClient {
  #transport;
  #pending = new Map();
  #next = 0;
  #timeoutMs;
  #answerTimeoutMs;
  #replyTimeoutMs;
  #stopTimeoutMs;
  #launchTimeoutMs;
  #transferTimeoutMs;
  #listeners = new Map();
  #lastSeq = null;
  // While a subscription's snapshot is arriving: the ids received since subscribing.
  #snapshot = null;
  #resubscribing = null;
  #usageSubscribed = false;
  #updateSubscribed = false;
  closed = false;
  synced = false;

  // Typing and confirming an answer can take a host a while, so session.answer gets longer
  // than other requests before it counts as lost (and is resent with the same request). A
  // reply to a working session is confirmed only once the agent takes it, which a host
  // waits up to a minute for, so session.reply gets longer still.
  constructor(transport, { store, timeoutMs = 15000, answerTimeoutMs = Math.max(timeoutMs, 30000), replyTimeoutMs = Math.max(answerTimeoutMs, 90000), stopTimeoutMs = Math.max(answerTimeoutMs, 45000), launchTimeoutMs = Math.max(answerTimeoutMs, 120000), transferTimeoutMs = Math.max(launchTimeoutMs, 12 * 60000) } = {}) {
    this.#transport = transport;
    this.store = store;
    this.#timeoutMs = timeoutMs;
    this.#answerTimeoutMs = answerTimeoutMs;
    this.#replyTimeoutMs = replyTimeoutMs;
    this.#stopTimeoutMs = stopTimeoutMs;
    this.#launchTimeoutMs = launchTimeoutMs;
    this.#transferTimeoutMs = transferTimeoutMs;
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

  request(op, data = {}, { timeoutMs = this.#timeoutMs } = {}) {
    if (this.closed) { return Promise.reject(new HostError(op, 'the channel is closed')); }
    const id = ++this.#next;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        if (this.#pending.delete(id)) { reject(new HostError(op, 'timed out')); }
      }, timeoutMs);
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

  // Answers a question: data is session.answer's { id, question, request, fields | text }.
  // Resolves to { outcome, reason }; rejects only when no outcome arrived (HostError).
  async answer(data, { timeoutMs = this.#answerTimeoutMs } = {}) {
    return readAnswerResult(await this.request(Ops.sessionAnswer, data, { timeoutMs }));
  }

  // Sends a session a prompt: data is session.reply's { id, request, text, attachments? }.
  // Resolves to { outcome, reason }; rejects only when no outcome arrived (HostError).
  async reply(data, { timeoutMs = this.#replyTimeoutMs } = {}) {
    return readAnswerResult(await this.request(Ops.sessionReply, data, { timeoutMs }));
  }

  // Sends a file ahead of a reply (attachments.js does the chunking). Each resolves to
  // { received, complete }, or rejects: a HostError fromHost says why the host turned it down.
  async attachmentStart(data) { return readAttachmentState(await this.request(Ops.attachmentStart, data)); }

  async attachmentChunk(data) { return readAttachmentState(await this.request(Ops.attachmentChunk, data)); }

  async attachmentDiscard(attachment) { return (await this.request(Ops.attachmentDiscard, { attachment }))?.discarded === true; }

  // Ends a session: data is session.stop's { id, request, confirm? }. Resolves to { outcome,
  // reason, confirm, confirmSeconds, forced }; rejects only when no outcome arrived. A host
  // gives a session up to 12 s to exit on /exit, then terminates it, so this waits longer
  // than an answer.
  async stop(data, { timeoutMs = this.#stopTimeoutMs } = {}) {
    return readStopResult(await this.request(Ops.sessionStop, data, { timeoutMs }));
  }

// launch.options: what session.launch can name on this machine now.
  async launchOptions() { return readLaunchOptions(await this.request(LAUNCH.options)); }

  // Launches or resumes a session: data is session.launch's settings with a request. A host
  // answers once the session has registered, which it waits up to a minute for, after making a
  // worktree if the workspace is isolated, so this waits longer than anything else.
  // A launch that brings a session from another machine first waits for it to be packed, sent
  // and unpacked, which the machine allows ten minutes for: the launch's own wait would give up,
  // and resend, long before.
  async launch(data, { timeoutMs = data?.transfer ? this.#transferTimeoutMs : this.#launchTimeoutMs } = {}) {
    return readLaunchResult(await this.request(LAUNCH.launch, data, { timeoutMs }));
  }

  // Answers a launched agent's folder-trust question: { question, choice, command }, the command
  // signed by this device for that choice. The machine answers once it has seen the session start
  // or the agent close, which it waits up to a minute for.
  async answerTrust(data, { timeoutMs = this.#launchTimeoutMs } = {}) {
    return readTrustResult(await this.request(TRUST.answer, data, { timeoutMs }));
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

  // usage.get: the machine's latest usage report (docs/protocol/v1-usage.md).
  async usage() { return readUsage(await this.request(USAGE.get)); }

  // usage.subscribe: the current report, then a 'usage' event with each new one.
  async subscribeUsage() {
    this.#usageSubscribed = true;
    return readUsage(await this.request(USAGE.subscribe));
  }

  // update.get: what the machine's updater is doing (docs/protocol/v1-update.md).
  async updateState() { return readUpdateState(await this.request(UPDATE.get)); }

  // update.subscribe: the state now, then an 'update' event with each change.
  async subscribeUpdate() {
    this.#updateSubscribed = true;
    return readUpdateState(await this.request(UPDATE.subscribe));
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
    // Closed means this client's machine is no longer shown. A frame still being decrypted
    // when the user switched must not put the old machine's sessions back in the store.
    if (this.closed) { return; }
    if (m === null || typeof m !== 'object' || Array.isArray(m)) { return; }
    if (Number.isInteger(m.re)) {
      const p = this.#pending.get(m.re);
      if (!p) { return; }
      this.#pending.delete(m.re);
      clearTimeout(p.timer);
      if (m.ok === true) { p.resolve(m.result); }
      else { p.reject(new HostError(p.op, typeof m.error === 'string' ? m.error : 'failed', { fromHost: true })); }
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
          const snapshot = this.#snapshot;
          // Synced before the store tells anyone: a view drawn from that news reads the live
          // count and the hold only once this is true, and nothing redraws it after.
          this.#snapshot = null;
          this.synced = true;
          if (this.store) {
            this.store.revision = Number.isFinite(data.revision) ? data.revision : this.store.revision;
            this.store.setHeld(readDiscoveryHeld(data.discoveryHeld));
            this.store.retain(snapshot);
          }
          this.#emit('synced', data);
        }
        break;
      case Events.discoveryChanged:
        this.store?.setHeld(readDiscoveryHeld(data.discoveryHeld));
        break;
      case USAGE.update:
        this.#emit('usage', readUsage(data));
        break;
      case UPDATE.event:
        this.#emit('update', readUpdateState(data));
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
    // An update lost in the gap is replaced by the current report.
    if (this.#usageSubscribed) { this.subscribeUsage().then((r) => this.#emit('usage', r), (e) => this.#emit('error', e)); }
    if (this.#updateSubscribed) { this.subscribeUpdate().then((r) => this.#emit('update', r), (e) => this.#emit('error', e)); }
    this.#resubscribing = this.subscribe()
      .catch((e) => this.#emit('error', e))
      .finally(() => { this.#resubscribing = null; });
  }
}
