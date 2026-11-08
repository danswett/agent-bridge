// A client of one Bridge Host over one transport: request/response plus the events a
// subscription delivers. Protocol: docs/protocol/v1-sessions.md.
//
// The transport is anything with `send(message) -> Promise`, `close()` and settable
// `onmessage(message)` / `onclose(error)` callbacks, carrying already-parsed JSON. In the
// app that is an end-to-end Noise channel (channels.js); in demo mode and the unit tests
// it is an in-process fake. Nothing here knows which.
import { Capabilities, Events, Ops, readAnswerResult, readAttachmentState, readDiscoveryHeld, readHello, readProgress, readSession, readStopResult, readSubagent } from './protocol.js';
import { PatchError, applyPatch, readPatch } from './session-patch.js';
import { USAGE, readUsage } from './usage.js';
import { UPDATE, readUpdateState } from './update.js';
import { LAUNCH, TRUST, readLaunchOptions, readLaunchResult, readTrustResult } from './launch.js';
import { SIGN_IN } from './auth-state.js';
import { Limits, hostJsonBytes } from './answers.js';
import { checkReply } from './replies.js';

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
  // The sessions.subscribe whose response is awaited (its request id), and, from that response
  // on, the ids its snapshot has delivered. A host stops one subscription before answering the
  // next, so events before the response are an earlier subscription's: nothing before it counts
  // towards the snapshot or completes it (#427), or an earlier sessions.synced still on its way
  // would end the new snapshot with half its sessions.
  #subscribing = null;
  #snapshot = null;
  // What the subscription asks for: patches (session.patch), and the run of the host (hello's
  // machine.instance) a reconnect resumes the list of. A subscribe again after a gap asks the same.
  #patch = false;
  #instance = null;
  // Whether the latest answered subscribe resumed (only changed sessions follow, and sessions.synced
  // names the live ones), and whether the store's revision may move with each patch and removal:
  // from the sessions.synced that made the list whole, until a gap.
  #resumed = false;
  #tracking = false;
  // Each session as this channel last gave it, the JSON as received and the seq it came with: what
  // a session.patch applies to. A new subscription starts afresh, since its patches are on its own
  // events.
  #raw = new Map();
  // Whether anything was dropped since the latest subscribe was answered: a snapshot with a session
  // it could not read is not the whole list, and nothing resumes from it.
  #dropped = false;
  // Whether the latest subscribe gave since: a synced naming the live sessions then, its answer not
  // saying it resumed, can't be told for a full snapshot's, which would drop every session it didn't
  // send again.
  #askedSince = false;
  // sessions.focus: the state the page last gave, the one last sent on this channel, when, and
  // the wait to send the latest after the gap.
  #focusWant = null;
  #focusSent = null;
  #focusAt = 0;
  #focusTimer = null;
  #focusForce = false;
  #focusIdle = null;
  #focusGapMs;
  #inputGapMs;
  #usageSubscribed = false;
  #usageShared = false;
  #updateSubscribed = false;
  closed = false;
  synced = false;
  // What the host offered in its latest hello on this channel: a reply or an answer asks for
  // progress only from a host that offers it (#598), so an older one gets the request it always
  // has.
  capabilities = [];

  // Typing and confirming an answer can take a host a while, so session.answer gets longer
  // than other requests before it counts as lost (and is resent with the same request). A
  // reply to a working session is confirmed only once the agent takes it, which a host
  // waits up to a minute for, so session.reply gets longer still.
  constructor(transport, { store, focusGapMs = 1000, inputGapMs = 2000, timeoutMs = 15000, answerTimeoutMs = Math.max(timeoutMs, 30000), replyTimeoutMs = Math.max(answerTimeoutMs, 90000), stopTimeoutMs = Math.max(answerTimeoutMs, 45000), launchTimeoutMs = Math.max(answerTimeoutMs, 120000), transferTimeoutMs = Math.max(launchTimeoutMs, 12 * 60000) } = {}) {
    this.#transport = transport;
    this.store = store;
    this.#timeoutMs = timeoutMs;
    this.#focusGapMs = focusGapMs;
    this.#inputGapMs = Math.max(inputGapMs, focusGapMs);
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

  request(op, data = {}, options = {}) { return this.#request(op, data, options).done; }

  // The request's id too, known as soon as it is made. onProgress hears the request's interim
  // progress frames, read (readProgress), in the order they come. onSent runs once the transport
  // has taken the request, and never for one it refused: the transport sends a microtask after the
  // call, so a mark taken as the call returns counted a failed send, and the queue's time, as the
  // host's.
  #request(op, data = {}, { timeoutMs = this.#timeoutMs, onProgress = null, onSent = null } = {}) {
    if (this.closed) { return { id: null, done: Promise.reject(new HostError(op, 'the channel is closed')) }; }
    const id = ++this.#next;
    const done = new Promise((resolve, reject) => {
      const timer = setTimeout(() => {
        if (this.#pending.delete(id)) { reject(new HostError(op, 'timed out')); }
      }, timeoutMs);
      this.#pending.set(id, { op, resolve, reject, timer, onProgress });
      // A send that fails (the relay went away under the channel) fails this request at
      // once, rather than leaving it to time out: S5's sleeping-iPhone lesson.
      Promise.resolve().then(() => this.#transport.send({ id, op, data })).then(() => {
        if (onSent) { try { onSent(); } catch (e) { console.error(e); } }
      }, (e) => {
        const p = this.#pending.get(id);
        if (p) { this.#pending.delete(id); clearTimeout(p.timer); reject(new HostError(op, e.message)); }
      });
    });
    return { id, done };
  }

  // changes: asks a host that offers hello.changed to say when what hello says changes, for as
  // long as this channel is open (v1-sessions.md, hello); one that doesn't ignores it.
  async hello({ changes = false } = {}) {
    const hello = readHello(await this.request(Ops.hello, changes ? { changes: true } : {}));
    this.capabilities = hello.capabilities;
    return hello;
  }

  // A session.answer's or session.reply's data, asking for progress where the host offers it and
  // the request still fits its limit with the flag (fits): the host counts "progress": true
  // against it, and one that fits only without the flag goes without it, saying Sending until its
  // outcome, rather than be refused for asking how it went. A host without progress gets nothing
  // new, and a request is never checked against bytes it won't carry (#616 review), as the app's
  // Deliveries.AskProgress does.
  #withProgress(data, onProgress, fits) {
    if (!onProgress || !this.capabilities.includes(Capabilities.sessionsProgress)) { return data; }
    const flagged = { ...data, progress: true };
    return fits(flagged) ? flagged : data;
  }

  // Requests awaiting their response.
  get pending() { return this.#pending.size; }

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

  // One sub-agent in full, with its own response, reasoning and history (session.get with
  // agent), or null once it or its session has gone. A host before sub-agents ignores agent
  // and sends the whole session, which is never taken for the delegate's.
  async getSubagent(id, agent) {
    const r = await this.request(Ops.sessionGet, { id, agent });
    if (r?.missing === true) { return null; }
    const a = readSubagent(r);
    if (!a || a.id !== agent) { throw new HostError(Ops.sessionGet, 'this machine does not report delegates in detail'); }
    return a;
  }

  // Answers a question: data is session.answer's { id, question, request, fields | text }.
  // Resolves to { outcome, reason, timings? }; rejects only when no outcome arrived (HostError).
  // onProgress({ stage, working }) hears how far the host has got, where it says
  // (sessions.progress); a host that doesn't sends only the outcome. onSent: once it has gone.
  async answer(data, { timeoutMs = this.#answerTimeoutMs, onProgress = null, onSent = null } = {}) {
    return readAnswerResult(await this.request(Ops.sessionAnswer, this.#withProgress(data, onProgress, (d) => hostJsonBytes(d) <= Limits.request), { timeoutMs, onProgress, onSent }));
  }

  // Sends a session a prompt: data is session.reply's { id, request, text, attachments? }.
  // Resolves to { outcome, reason, timings? }; rejects only when no outcome arrived (HostError).
  // onProgress and onSent as for answer().
  async reply(data, { timeoutMs = this.#replyTimeoutMs, onProgress = null, onSent = null } = {}) {
    return readAnswerResult(await this.request(Ops.sessionReply, this.#withProgress(data, onProgress, (d) => checkReply(d) === null), { timeoutMs, onProgress, onSent }));
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
  // patch: ask for live changes as session.patch, of a host that offers sessions.patch; then a
  // list the store holds whole from that instance of the host is resumed rather than sent again.
  async subscribe({ patch = this.#patch, instance = this.#instance } = {}) {
    this.#patch = patch === true;
    this.#instance = typeof instance === 'string' ? instance : null;
    this.#snapshot = null;
    this.synced = false;
    this.#tracking = false;
    const since = this.#patch ? this.store?.resumeFrom?.(this.#instance) ?? null : null;
    this.#askedSince = since !== null;
    const data = this.#patch ? { patch: true, ...(since ? { since } : {}) } : {};
    const { id, done } = this.#request(Ops.sessionsSubscribe, data);
    this.#subscribing = id;
    try {
      const result = await done;
      // Said again after every subscribe, as the protocol asks: a new channel has been told nothing.
      this.#queueFocus(true);
      return result;
    } finally { if (this.#subscribing === id) { this.#subscribing = null; } }
  }

  // sessions.focus (#1190): what this page shows, for a host that offers it, so it does not send
  // a notification for what is being read. visible: the page is on screen; open: the id of the
  // session shown on this machine, if any. Said as it changes and after each subscribe, never
  // more than once in focusGapMs (the host refuses more than 20 in 10 s on a channel), and only
  // the latest state goes. A failure is ignored: the host then notifies as it always has.
  // idle: a function giving the ms since the person's last input in this page, read as each
  // request is made (an age, so the clocks of phone and host need not agree), or null when no
  // input has been seen: then idleMs is left out rather than say 0 (it reads as "not active").
  setFocus({ visible, open = null, idle = null } = {}) {
    this.#focusIdle = typeof idle === 'function' ? idle : null;
    const want = { visible: visible === true, ...(typeof open === 'string' && open ? { open } : {}) };
    if (this.#focusWant && JSON.stringify(want) === JSON.stringify(this.#focusWant)) { return; }
    this.#focusWant = want;
    this.#queueFocus();
  }

  // Says the focus again as it stands, with a fresh idleMs: the person is active again, or still
  // is. Held to one in inputGapMs, however much input there is, and coalesced with any waiting.
  refreshFocus() {
    if (this.#focusWant) { this.#queueFocus(true, this.#inputGapMs); }
  }

  #queueFocus(force = false, gapMs = this.#focusGapMs) {
    if (force) { this.#focusForce = true; }
    if (!this.#focusWant || this.closed || this.#focusTimer !== null || !this.capabilities.includes(Capabilities.sessionsFocus)) { return; }
    const wait = this.#focusAt + gapMs - Date.now();
    if (wait <= 0) { this.#sendFocus(); return; }
    this.#focusTimer = setTimeout(() => { this.#focusTimer = null; this.#sendFocus(); }, wait);
  }

  #sendFocus() {
    const want = this.#focusWant;
    const force = this.#focusForce;
    this.#focusForce = false;
    const key = JSON.stringify(want);
    if (!want || this.closed || !this.capabilities.includes(Capabilities.sessionsFocus) || (!force && key === this.#focusSent)) { return; }
    this.#focusSent = key;
    this.#focusAt = Date.now();
    let idleMs;
    try { idleMs = this.#focusIdle?.(); } catch { idleMs = null; }
    const data = Number.isFinite(idleMs) && idleMs >= 0 ? { ...want, idleMs: Math.floor(idleMs) } : want;
    try { this.#request(Ops.sessionsFocus, data).done.catch(() => {}); } catch { /* never into the page */ }
  }

  // usage.get: the machine's latest usage report (docs/protocol/v1-usage.md). shared: with the
  // readings it keeps from the account's other machines, where it offers usage.shared.
  async usage({ shared = false } = {}) { return readUsage(await this.request(USAGE.get, shared ? { shared: true } : {})); }

  // usage.subscribe: the current report, then a 'usage' event with each new one.
  async subscribeUsage({ shared = false } = {}) {
    this.#usageSubscribed = true;
    this.#usageShared = shared;
    return readUsage(await this.request(USAGE.subscribe, shared ? { shared: true } : {}));
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
    clearTimeout(this.#focusTimer);
    this.#focusTimer = null;
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
      // An interim frame (re and progress, no ok) says how far the request has got, and never
      // settles it: the response still follows. One that isn't an object is passed over.
      if (!('ok' in m) && 'progress' in m) {
        const progress = readProgress(m.progress);
        if (progress && p.onProgress) {
          try { p.onProgress(progress); } catch (e) { console.error(e); }
        }
        return;
      }
      this.#pending.delete(m.re);
      clearTimeout(p.timer);
      // Here, in the order of the channel, not when the awaiting code next runs: every event
      // after this response is the new subscription's, and none before it.
      if (m.re === this.#subscribing) {
        this.#subscribing = null;
        if (m.ok === true) {
          this.#snapshot = new Set();
          this.#resumed = m.result?.resumed === true;
          this.#raw.clear();
          this.#dropped = false;
        }
      }
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
      // A channel numbers its events from 1 and a later subscribe carries on from where it is, so
      // the next is always one more than the last, and the first is 1. Taking whatever came first,
      // on a channel or after subscribing again, as the baseline missed events lost just then.
      gap = m.seq !== (this.#lastSeq ?? 0) + 1;
      // An event numbered again or back (a host that is broken) may carry an older state than the
      // one held: the list is not whole after it, and is never resumed from.
      if (gap && m.seq <= (this.#lastSeq ?? 0)) { this.#lost(); }
      this.#lastSeq = m.seq;
    } else {
      // An event without a number (a host that is broken) says nothing of what was lost before it:
      // the revision stops moving, and the list is never resumed from.
      this.#tracking = false;
      this.#lost();
    }
    // Before anything of this event is applied or told: the snapshot under way is no longer the
    // whole of anything, so nothing may complete it, not even this event when it is the
    // sessions.synced. Told after, a listener confirmed the list, and resent what waited,
    // before hearing it was not whole (#427).
    if (gap) {
      // What came before the gap all arrived, so the store's revision still holds; nothing after
      // it moves it until a snapshot is whole again.
      this.#tracking = false;
      this.#resubscribe(m.seq);
    }
    const data = m.data !== null && typeof m.data === 'object' ? m.data : {};
    switch (m.ev) {
      case Events.sessionUpsert: {
        const s = this.store?.upsert(data);
        if (s) {
          this.#snapshot?.add(s.id);
          this.#raw.set(s.id, { json: data, seq: m.seq });
        } else { this.#lost(); }
        break;
      }
      case Events.sessionPatch:
        this.#patched(data, m.seq);
        break;
      case Events.sessionRemove:
        if (typeof data.id !== 'string') { this.#lost(); break; }
        this.store?.remove(data.id);
        this.#snapshot?.delete(data.id);
        this.#raw.delete(data.id);
        this.#advance(data.revision);
        break;
      case Events.sessionsSynced:
        if (this.#snapshot) {
          const snapshot = this.#snapshot;
          const keep = this.#kept(snapshot, data.live);
          // Not the whole of anything: subscribed again, without resuming, before anyone is told.
          if (!keep) {
            this.#snapshot = null;
            this.#lost();
            this.#resubscribe(m.seq);
            break;
          }
          // Synced before the store tells anyone: a view drawn from that news reads the live
          // count and the hold only once this is true, and nothing redraws it after.
          this.#snapshot = null;
          this.synced = true;
          this.#tracking = true;
          if (this.store) {
            this.store.revision = Number.isFinite(data.revision) ? data.revision : this.store.revision;
            this.store.instance = this.#instance;
            // A revision it can't read can't be resumed from: the one kept from before may be another
            // run's, which this one could reach with changes it never sent.
            this.store.whole = !this.#dropped && Number.isSafeInteger(data.revision);
            this.store.setHeld(readDiscoveryHeld(data.discoveryHeld));
            this.store.retain(keep);
          }
          this.#emit('synced', data);
        }
        break;
      case Events.discoveryChanged:
        this.store?.setHeld(readDiscoveryHeld(data.discoveryHeld));
        break;
      // A device flow this channel is in (#800): its code, then how it ended. Only to its view.
      case SIGN_IN.code:
      case SIGN_IN.done:
        this.store?.signIn(m.ev, data);
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
  }

  // A session.patch, applied to the session as this channel last gave it, then read as an upsert.
  // One that cannot be read or applied is dropped whole, and the list is no longer the host's, so
  // it is subscribed again in full, as after a gap: before anything of it is shown.
  #patched(data, seq) {
    const patch = readPatch(data);
    let applied = null;
    if (patch) {
      const held = this.#raw.get(patch.id);
      try { applied = applyPatch(held?.json ?? null, held?.seq ?? null, patch); }
      catch (e) { if (!(e instanceof PatchError)) { throw e; } }
    }
    const s = applied ? (this.store ? this.store.upsert(applied) : readSession(applied)) : null;
    if (!s) {
      this.#tracking = false;
      this.#lost();
      this.#resubscribe(seq);
      return;
    }
    this.#raw.set(s.id, { json: applied, seq });
    this.#snapshot?.add(s.id);
    this.#advance(patch.revision);
  }

  // The ids a completed snapshot leaves on the list, or null when it can't be whole. A full one is
  // the sessions it sent. A resumed one is every id in live, each sent since the response or held
  // from before; one neither, or a live that cannot be read, means the list can't be whole. So
  // does anything dropped since resuming was asked for, which the host could not know to resend.
  #kept(snapshot, live) {
    if (live !== undefined && live !== null && !(Array.isArray(live) && live.every((id) => typeof id === 'string'))) { return null; }
    if (!this.#resumed) { return this.#askedSince && Array.isArray(live) ? null : snapshot; }
    if (!Array.isArray(live) || this.store?.whole !== true) { return null; }
    return live.every((id) => snapshot.has(id) || this.store.has(id)) ? new Set(live) : null;
  }

  // The store's list is no longer the host's whole list: nothing resumes from it until a full
  // snapshot makes it whole again.
  #lost() {
    this.#dropped = true;
    if (this.store) { this.store.whole = false; }
  }

  #advance(revision) {
    if (this.#tracking && this.store && Number.isSafeInteger(revision)) { this.store.revision = revision; }
  }

  // 'gap' carries resync, which settles true once the host has taken the new subscription and
  // false if it never did: a listener holding the list until the new snapshot is whole (#427)
  // learns that it will never be on this channel. A subscribe still awaiting its response covers
  // the gap (its snapshot is everything after that response); one already answered doesn't, as
  // its snapshot is what the gap broke.
  #resubscribe(seq) {
    if (this.#subscribing !== null || this.closed) { return; }
    // An update lost in the gap is replaced by the current report.
    if (this.#usageSubscribed) { this.subscribeUsage({ shared: this.#usageShared }).then((r) => this.#emit('usage', r), (e) => this.#emit('error', e)); }
    if (this.#updateSubscribed) { this.subscribeUpdate().then((r) => this.#emit('update', r), (e) => this.#emit('error', e)); }
    const resync = this.subscribe().then(() => true, (e) => { this.#emit('error', e); return false; });
    this.#emit('gap', { seq, resync });
  }
}
