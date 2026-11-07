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
// Entry states: 'sending' (a request is out), 'waiting' (no channel, reason 'not connected', and
// resent when there is one; or no outcome on a channel still open, resent after retryMs; or
// 'still being delivered'), 'failed' (the host answered the op with an error, not an outcome;
// resendable as is), 'done' (outcome and reason set).
//
// While it is on its way, a host that offers sessions.progress says how far it has got, which
// the entry keeps as its `stage`: 'received' (the host has it), 'typed' (the keys have gone),
// or 'queued' (typed into a working session, which takes it at its next step). It only moves
// forward, across resends of the same request: a repeat replays the stages reached, and what
// has been typed stays typed. Null from a host that doesn't say (#600).
//
// `marks` ('reply' or 'answer') keeps this device's timing marks for each delivery, from the
// tap to the outcome (timing.js); null keeps none.
//
// `current(hostId)` says whether that host is connected, and its HostClient: { client, hostId }
// or null. Each entry goes to the host it was given for and to no other, so with several
// machines shown at once an answer can never reach a session of the same id on another one.
// Call ready() whenever a new client for a host is up, and resends go out on it.
//
// ReplyService (reply-service.js) and StopService (stop-service.js) are this with three things
// swapped: the op it sends, the request it builds, and which outcomes are final. A settled
// entry's `detail` is the whole result, for ops whose result says more than outcome and reason.

import { ProgressStages } from './protocol.js';
import { finished, marked, tapped } from './timing.js';

const NO_OUTCOME = 'no outcome came back from the machine in time';
const MAX_BUSY_RESENDS = 5;
const STAGE_ORDER = { received: 1, typed: 2, queued: 2 };

// Marks 'sent' for a request once the transport has taken it (HostClient's onSent): not as the call
// returns, which counted a send the transport then refused, and the time it queued as the host's.
export function sent(entry) {
  return () => marked(entry.request, 'sent');
}

export class AnswerService {
  #entries = new Map();
  #listeners = new Map();
  #any = new Set();

  // send(client, data, entry, progress, stage): the op, which may call progress(anything) as it
  // goes, and passes stage to the client as its onProgress for the host's progress frames. It
  // marks 'sent' (timing.js) once the request itself has gone, after anything sent ahead of it.
  // payload(entry): its request data. final(entry): whether a settled outcome stands, so that
  // giving the answer again sends nothing.
  constructor({
    current,
    uuid = () => globalThis.crypto.randomUUID(),
    now = () => Date.now(),
    retryMs = 3000,
    giveUpMs = 9 * 60 * 1000,
    // How long one entry may go on being resent, when some ops take longer than others.
    giveUpFor = null,
    send = (client, data, entry, progress, stage) => client.answer(data, { onProgress: stage, onSent: sent(entry) }),
    payload = (e) => ({ id: e.sessionId, question: e.questionId, request: e.request, ...e.answer }),
    final = (e) => !['busy', 'refused'].includes(e.outcome),
    // Whether busy on a resend means the first copy is still being delivered. A launch's host
    // returns the first copy's outcome to a resend, whatever it is, so busy there is the answer.
    resendOnBusy = true,
    // Whether anything may be given for a host now. A machine whose list may be out of date (it
    // went offline, #269) takes nothing new: an answer, reply, stop or launch given from what it
    // showed before it went could act on a question or session that has gone since.
    accepts = () => true,
    marks = null,
  }) {
    Object.assign(this, { current, uuid, now, retryMs, giveUpMs, send, payload, final, resendOnBusy, accepts, marks });
    this.giveUpFor = giveUpFor ?? (() => this.giveUpMs);
  }

  static key(hostId, sessionId, questionId) { return JSON.stringify([hostId, sessionId, questionId]); }

  get(hostId, sessionId, questionId) { return this.#snapshot(this.#entries.get(AnswerService.key(hostId, sessionId, questionId))); }

  // Whether any is on its way now: a request out (files going first), or one about to be resent
  // on its own, its response lost or the machine still delivering the first copy. One waiting for
  // a channel to its machine has no timer, and waits as well with the app at rest.
  get busy() {
    for (const entry of this.#entries.values()) {
      if (entry.state === 'sending' || (entry.state === 'waiting' && entry.timer !== null)) { return true; }
    }
    return false;
  }

  // Calls fn(entry) whenever the answer to this question changes.
  watch(hostId, sessionId, questionId, fn) {
    const key = AnswerService.key(hostId, sessionId, questionId);
    if (!this.#listeners.has(key)) { this.#listeners.set(key, new Set()); }
    this.#listeners.get(key).add(fn);
    return () => this.#listeners.get(key)?.delete(fn);
  }

  onAny(fn) { this.#any.add(fn); return () => this.#any.delete(fn); }

  // Sends an answer: { fields } or { text }. While one is on its way it is returned as is:
  // a second tap never sends twice. Null, with nothing sent, for a host that takes nothing now.
  // request: the UUID it goes as, when something was already signed for it (an escaped approval's
  // confirmation names its answer's request, #803); otherwise a fresh one.
  submit({ hostId, sessionId, questionId, answer, request = null }) {
    const key = AnswerService.key(hostId, sessionId, questionId);
    const existing = this.#entries.get(key);
    if (existing && (existing.state === 'sending' || existing.state === 'waiting')) { return this.#snapshot(existing); }
    if (existing && existing.state === 'done' && this.final(existing)) { return this.#snapshot(existing); }
    if (!this.accepts(hostId)) { return null; }
    const entry = {
      key, hostId, sessionId, questionId, answer,
      request: request ?? this.uuid(), state: 'sending', outcome: null, reason: null, progress: null, stage: null,
      firstSentAt: null, attempts: 0, busyResends: 0, timer: null, token: 0,
    };
    this.#entries.set(key, entry);
    if (this.marks) { tapped(this.marks, entry.request); }
    this.#attempt(entry);
    return this.#snapshot(entry);
  }

  // After 'failed': the same request again, so the host still types it at most once.
  resend(hostId, sessionId, questionId) {
    const entry = this.#entries.get(AnswerService.key(hostId, sessionId, questionId));
    if (!entry || entry.state !== 'failed' || !this.accepts(hostId)) { return null; }
    this.#attempt(entry);
    return this.#snapshot(entry);
  }

  // A client for a host is up: anything waiting to be resent to that host goes now. Without
  // a host, every host's. And an answer the host reported typed but unconfirmed (a stage) is
  // asked about again, with the same request, so one the session recorded late, or the person
  // submitted at the machine, comes back delivered (#467). The host never types a request
  // twice, so this only reads; it stops once the host may have forgotten the request.
  ready(hostId = null) {
    for (const entry of this.#entries.values()) {
      if (hostId !== null && entry.hostId !== hostId) { continue; }
      if (entry.state === 'waiting') { this.#attempt(entry); } else if (this.#checkable(entry)) { this.#recheck(entry); }
    }
  }

  #checkable(entry) {
    return entry.state === 'done' && entry.outcome === 'unconfirmed' && entry.detail?.stage != null
      && entry.firstSentAt !== null && this.now() - entry.firstSentAt <= this.giveUpFor(entry);
  }

  // The settled entry is shown as it is meanwhile, and changes only if the host says more.
  // Only the latest ask may change it: one still out on an older channel, answered after a
  // newer one, would otherwise put back what the newer one replaced.
  async #recheck(entry) {
    const cur = this.current(entry.hostId);
    if (!cur?.client || cur.client.closed || cur.hostId !== entry.hostId) { return; }
    const token = ++entry.token;
    let result;
    try { result = await this.send(cur.client, this.payload(entry), this.#snapshot(entry), () => {}); } catch { return; }
    if (token !== entry.token || entry.state !== 'done' || entry.outcome !== 'unconfirmed') { return; }
    // A new reason at the same stage is news too (a late completion's error replacing a
    // mismatch), so it is shown rather than the evidence it replaced.
    if (result.outcome === 'delivered' || (result.outcome === 'unconfirmed' && (result.stage !== entry.detail?.stage || result.reason !== entry.reason))) {
      this.#set(entry, { outcome: result.outcome, reason: result.reason, detail: result });
    }
  }

  // Everything tied to the signed-in account goes with it.
  clear() {
    for (const entry of this.#entries.values()) { clearTimeout(entry.timer); entry.token++; }
    this.#entries.clear();
  }

  #snapshot(entry) {
    if (!entry) { return null; }
    const { key, hostId, sessionId, questionId, answer, request, state, outcome, reason, attempts, progress, stage, detail = null } = entry;
    return { key, hostId, sessionId, questionId, answer, request, state, outcome, reason, attempts, progress, stage, detail };
  }

  #set(entry, change) {
    Object.assign(entry, change);
    if (change.state === 'done' && this.marks) { finished(entry.request, entry.outcome, entry.detail?.timings ?? null); }
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
    if (entry.firstSentAt !== null && this.now() - entry.firstSentAt > this.giveUpFor(entry)) {
      this.#set(entry, { state: 'done', outcome: 'unconfirmed', reason: NO_OUTCOME });
      return;
    }
    const cur = this.current(entry.hostId);
    if (!cur?.client || cur.client.closed || cur.hostId !== entry.hostId) {
      this.#wait(entry, 'not connected');
      return;
    }
    const client = cur.client;
    entry.attempts++;
    entry.firstSentAt ??= this.now();
    this.#set(entry, { state: 'sending', reason: null, progress: null });
    const data = this.payload(entry);
    // What the op has done so far, for one that takes a while (a reply's files going first).
    const progress = (p) => { if (token === entry.token && entry.state === 'sending') { this.#set(entry, { progress: p }); } };
    // How far the host has got with it (sessions.progress). A stage from an attempt given up
    // on, one this app doesn't know (from a newer host), or one behind one already reached,
    // changes nothing.
    const stage = ({ stage: s, working }) => {
      if (token !== entry.token || entry.state !== 'sending' || !ProgressStages.includes(s)) { return; }
      const next = s === 'typed' && working ? 'queued' : s;
      // Only forward: typed and queued share a rank, so frames alternating between them move nothing.
      if ((STAGE_ORDER[next] ?? 0) <= (STAGE_ORDER[entry.stage] ?? 0)) { return; }
      // Marked only when the stage moves on: a repeat replaying the stages so far, or a host
      // saying the same again, adds nothing to the timing record.
      if (this.marks) { marked(entry.request, next); }
      this.#set(entry, { stage: next });
    };
    let result;
    try {
      result = await this.send(client, data, this.#snapshot(entry), progress, stage);
    } catch (e) {
      if (token !== entry.token) { return; }
      if (e?.fromHost === true) {
        this.#set(entry, { state: 'failed', reason: e.reason ?? e.message });
        return;
      }
      // No outcome: the response was lost or the channel went. If another channel is
      // already up (the reconnect beat this rejection), resend on it now; otherwise later.
      const now = this.current(entry.hostId);
      if (now?.client && !now.client.closed && now.client !== client && now.hostId === entry.hostId) { this.#attempt(entry); return; }
      // A channel that went is no connection, as when there was none to send on: the receipt says
      // it waits for the machine (ui.md §6.5). Still connected, it is a lost response, retried.
      this.#wait(entry, client.closed ? 'not connected' : e?.message ?? 'no response');
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
    if (result.outcome === 'busy' && entry.attempts > 1 && this.resendOnBusy) {
      if (++entry.busyResends > MAX_BUSY_RESENDS) {
        this.#set(entry, { state: 'done', outcome: 'unconfirmed', reason: result.reason ?? NO_OUTCOME });
        return;
      }
      this.#wait(entry, 'still being delivered');
      entry.timer = setTimeout(() => this.#attempt(entry), this.retryMs);
      return;
    }
    this.#set(entry, { state: 'done', outcome: result.outcome, reason: result.reason, detail: result });
  }
}
