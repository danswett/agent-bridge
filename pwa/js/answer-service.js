// Sending answers, and getting each one to exactly one outcome. One per session and
// question, kept in memory for as long as the app is open:
//
//   submit() -> a fresh request UUID -> session.answer -> outcome
//                    | no response (timeout, dropped channel, reconnect)
//                    v
//               resent with the SAME request, which the host answers with the first
//               outcome instead of typing again
//
// The request is what makes a resend safe, so an answer keeps its request until it has an
// outcome, across reconnects, and gets a new one only when the person answers again (after
// busy or refused). A host remembers requests for at least 10 minutes; after
// `giveUpMs` from its first send without an outcome the answer is reported as unconfirmed
// instead of being sent again, since a host that has forgotten it could type it twice.
// One never sent at all (given while disconnected) can't have been typed, so it waits for
// a connection however long that takes.
//
// Entry states: 'sending' (a request is out), 'waiting' (no channel; resent when there is
// one), 'failed' (the host answered the op with an error, not an outcome; resendable as
// is), 'done' (outcome and reason set).
//
// `current()` says which host is connected and its HostClient, or null. Call ready()
// whenever a new client for a host is up, and resends go out on it.
//
// ReplyService (reply-service.js) is this with three things swapped: the op it sends, the
// request it builds, and which outcomes are final.

const NO_OUTCOME = 'no outcome came back from the machine in time';
const MAX_BUSY_RESENDS = 5;

export class AnswerService {
  #entries = new Map();
  #listeners = new Map();
  #any = new Set();

  // send(client, data): the op. payload(entry): its request data. final(entry): whether a
  // settled outcome stands, so that giving the answer again sends nothing.
  constructor({
    current,
    uuid = () => globalThis.crypto.randomUUID(),
    now = () => Date.now(),
    retryMs = 3000,
    giveUpMs = 9 * 60 * 1000,
    send = (client, data) => client.answer(data),
    payload = (e) => ({ id: e.sessionId, question: e.questionId, request: e.request, ...e.answer }),
    final = (e) => !['busy', 'refused'].includes(e.outcome),
  }) {
    Object.assign(this, { current, uuid, now, retryMs, giveUpMs, send, payload, final });
  }

  static key(hostId, sessionId, questionId) { return JSON.stringify([hostId, sessionId, questionId]); }

  get(hostId, sessionId, questionId) { return this.#snapshot(this.#entries.get(AnswerService.key(hostId, sessionId, questionId))); }

  // Calls fn(entry) whenever the answer to this question changes.
  watch(hostId, sessionId, questionId, fn) {
    const key = AnswerService.key(hostId, sessionId, questionId);
    if (!this.#listeners.has(key)) { this.#listeners.set(key, new Set()); }
    this.#listeners.get(key).add(fn);
    return () => this.#listeners.get(key)?.delete(fn);
  }

  onAny(fn) { this.#any.add(fn); return () => this.#any.delete(fn); }

  // Sends an answer: { fields } or { text }. While one is on its way it is returned as is:
  // a second tap never sends twice.
  submit({ hostId, sessionId, questionId, answer }) {
    const key = AnswerService.key(hostId, sessionId, questionId);
    const existing = this.#entries.get(key);
    if (existing && (existing.state === 'sending' || existing.state === 'waiting')) { return this.#snapshot(existing); }
    if (existing && existing.state === 'done' && this.final(existing)) { return this.#snapshot(existing); }
    const entry = {
      key, hostId, sessionId, questionId, answer,
      request: this.uuid(), state: 'sending', outcome: null, reason: null,
      firstSentAt: null, attempts: 0, busyResends: 0, timer: null, token: 0,
    };
    this.#entries.set(key, entry);
    this.#attempt(entry);
    return this.#snapshot(entry);
  }

  // After 'failed': the same request again, so the host still types it at most once.
  resend(hostId, sessionId, questionId) {
    const entry = this.#entries.get(AnswerService.key(hostId, sessionId, questionId));
    if (!entry || entry.state !== 'failed') { return null; }
    this.#attempt(entry);
    return this.#snapshot(entry);
  }

  // A client for a host is up: anything waiting to be resent to that host goes now.
  ready() {
    for (const entry of this.#entries.values()) {
      if (entry.state === 'waiting') { this.#attempt(entry); }
    }
  }

  // Everything tied to the signed-in account goes with it.
  clear() {
    for (const entry of this.#entries.values()) { clearTimeout(entry.timer); entry.token++; }
    this.#entries.clear();
  }

  #snapshot(entry) {
    if (!entry) { return null; }
    const { key, hostId, sessionId, questionId, answer, request, state, outcome, reason, attempts } = entry;
    return { key, hostId, sessionId, questionId, answer, request, state, outcome, reason, attempts };
  }

  #set(entry, change) {
    Object.assign(entry, change);
    const snap = this.#snapshot(entry);
    for (const fn of [...(this.#listeners.get(entry.key) ?? []), ...this.#any]) {
      try { fn(snap); } catch (e) { console.error(e); }
    }
  }

  #wait(entry, reason) {
    clearTimeout(entry.timer);
    entry.timer = null;
    this.#set(entry, { state: 'waiting', reason });
  }

  async #attempt(entry) {
    clearTimeout(entry.timer);
    entry.timer = null;
    const token = ++entry.token;
    if (entry.firstSentAt !== null && this.now() - entry.firstSentAt > this.giveUpMs) {
      this.#set(entry, { state: 'done', outcome: 'unconfirmed', reason: NO_OUTCOME });
      return;
    }
    const cur = this.current();
    if (!cur?.client || cur.client.closed || cur.hostId !== entry.hostId) {
      this.#wait(entry, 'not connected');
      return;
    }
    const client = cur.client;
    entry.attempts++;
    entry.firstSentAt ??= this.now();
    this.#set(entry, { state: 'sending', reason: null });
    const data = this.payload(entry);
    let result;
    try {
      result = await this.send(client, data);
    } catch (e) {
      if (token !== entry.token) { return; }
      if (e?.fromHost === true) {
        this.#set(entry, { state: 'failed', reason: e.reason ?? e.message });
        return;
      }
      // No outcome: the response was lost or the channel went. If another channel is
      // already up (the reconnect beat this rejection), resend on it now; otherwise later.
      const now = this.current();
      if (now?.client && !now.client.closed && now.client !== client && now.hostId === entry.hostId) { this.#attempt(entry); return; }
      this.#wait(entry, e?.message ?? 'no response');
      if (!client.closed) { entry.timer = setTimeout(() => this.#attempt(entry), this.retryMs); }
      return;
    }
    if (token !== entry.token) { return; }
    // A resend can reach the host while it is still delivering the first copy, which it
    // reports as busy. That busy is our own answer, so it is not one to show: ask again
    // with the same request until the first copy's outcome comes back.
    // The contract doesn't say how a host answers a repeat of a request it is still working
    // on, so this is bounded: after a few, the copy that may have been typed is reported as
    // unconfirmed, which is the truth.
    if (result.outcome === 'busy' && entry.attempts > 1) {
      if (++entry.busyResends > MAX_BUSY_RESENDS) {
        this.#set(entry, { state: 'done', outcome: 'unconfirmed', reason: result.reason ?? NO_OUTCOME });
        return;
      }
      this.#wait(entry, 'still being delivered');
      entry.timer = setTimeout(() => this.#attempt(entry), this.retryMs);
      return;
    }
    this.#set(entry, { state: 'done', outcome: result.outcome, reason: result.reason });
  }
}
