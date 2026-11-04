// The Needs you lane on screen (ui.md §6.1, #165), above the sessions list and apart from it:
// rows never move for it. What it lists and offers comes from ../needs-you.js; this draws it.
// Entries are kept by session and updated in place, so a button with focus keeps it while the
// lane redraws around it.
import { h } from './dom.js';
import { glyph } from './glyph.js';
import { needsYou, AppBadge } from '../needs-you.js';
import { tapState, prepareAnswer } from '../answers.js';
import { BUSY_PAUSE_MS, answerControls, answerState, describeAnswer } from './question-view.js';
import { ReceiptClock, ReceiptTiming, drawReceipt, drawn, holdCheck, receiptLive } from './receipt.js';
import { duration, localTime } from './time.js';
import { whileVisible } from './ticker.js';
import { renderMarkdown } from '../markdown.js';

// An entry whose question the machine cleared while its answer was still on its way (typed, before
// the machine confirmed it) stays, apart from the count, until its outcome has shown: a delivered
// one for its check, any other for long enough to read, and never past the answer's give-up (the
// answer service's 9 minutes). Dropped with its question, the ring went and the outcome never came.
// The outcome can also come first, the machine's update clearing the question after it: the entry
// then stays for what is left of that same time, as it would the other way round (outcomeUntil).
const LINGER_READ_MS = 5000;
const LINGER_MAX_MS = 9 * 60 * 1000;
const inFlight = (entry) => entry?.state === 'sending' || entry?.state === 'waiting';

export class NeedsYouLane {
  #entries = new Map();
  #scheduled = false;

  // container: the lane's element, with a heading (.needs-you-head) and a list (.needs-you-list).
  // sessions: { list() -> [{ hostId, session }], onChange } (HostSession.sessions).
  // answering(hostId) -> { service, capabilities } | null, as the cards have it.
  // machine(hostId) -> { name }. labelled(): whether entries name their machine (several shown).
  // open(hostId, id): shows that session's card. badge: where the count goes (AppBadge).
  constructor(container, sessions, { answering = () => null, machine = () => ({ name: '' }), labelled = () => false, open = () => {}, badge = new AppBadge() } = {}) {
    Object.assign(this, { container, sessions, answering, machine, labelled, open, badge });
    this.head = container.querySelector('.needs-you-head');
    this.list = container.querySelector('.needs-you-list');
    sessions.onChange(() => this.schedule());
    this.ticker = whileVisible(() => this.refreshTimes(), 15000);
    this.render();
  }

  // Questions waiting, not entries kept only to show an answer's outcome.
  get count() { let n = 0; for (const v of this.#entries.values()) { if (!v.lingering) { n++; } } return n; }

  schedule() {
    if (this.#scheduled) { return; }
    this.#scheduled = true;
    queueMicrotask(() => { this.#scheduled = false; this.render(); });
  }

  render() {
    const lane = needsYou(this.sessions.list(), {
      capabilities: (hostId) => {
        const ctx = this.answering(hostId);
        return ctx?.service ? ctx.capabilities ?? [] : [];
      },
      nameOf: (hostId) => this.machine(hostId).name,
      labelled: this.labelled(),
      current: (hostId) => !this.answering(hostId)?.stale,
    });
    const live = new Set();
    const want = [];
    const before = [...this.list.children];
    for (const e of lane) {
      live.add(e.key);
      let view = this.#entries.get(e.key);
      // A new question is a new entry: what became of an answer to the last one is not this one's.
      if (view && (view.questionId !== e.questionId || view.mode !== e.mode || !view.els.prompt !== (e.prompt === null))) { this.#drop(view); view = null; }
      if (!view) {
        view = this.#build(e);
        this.#entries.set(e.key, view);
      }
      if (view.lingering) { this.#unlinger(view); this.#show(view, view.service.get(e.hostId, e.sessionId, e.questionId)); }
      this.#fill(view, e);
      want.push(view.el);
    }
    const lingering = [];
    for (const [key, view] of this.#entries) {
      if (live.has(key)) { continue; }
      if (this.#lingers(view)) { lingering.push(view); } else { this.#drop(view); this.#entries.delete(key); }
    }
    // Where it was, so nothing around it moves for its question going.
    for (const view of lingering.sort((a, b) => before.indexOf(a.el) - before.indexOf(b.el))) {
      const at = before.indexOf(view.el);
      want.splice(at < 0 ? want.length : Math.min(at, want.length), 0, view.el);
    }
    let prev = null;
    for (const el of want) {
      const at = prev ? prev.nextSibling : this.list.firstChild;
      if (at !== el) { this.list.insertBefore(el, at); }
      prev = el;
    }
    while (prev ? prev.nextSibling : this.list.firstChild) { (prev ? prev.nextSibling : this.list.firstChild).remove(); }
    this.head.textContent = `Needs you \u00b7 ${lane.length}`;
    this.container.hidden = lane.length === 0 && lingering.length === 0;
    this.badge.set(lane.length);
    this.onRender?.(lane.length);
  }

  refreshTimes(now = Date.now()) {
    for (const view of this.#entries.values()) { this.#age(view, now); }
  }

  #build(e) {
    const view = { key: e.key, questionId: e.questionId, mode: e.mode, entry: e, unwatch: null, pause: null };
    const els = view.els = {};
    els.title = h('button', { type: 'button', class: 'needs-title', onclick: () => this.open(view.entry.hostId, view.entry.sessionId) });
    els.line = h('p', { class: 'needs-line' });
    // An approval answered here shows its prompt whole: everything Approve once allows, drawn as its
    // card draws an approval's message (#220).
    els.prompt = e.prompt !== null ? h('div', { class: 'md md-approval needs-prompt', dir: 'ltr' }) : null;
    els.machine = h('span', { class: 'needs-machine' });
    els.agent = h('span', { class: 'needs-agent' });
    els.delegate = h('span', { class: 'needs-delegate' });
    els.age = h('time', { class: 'needs-age' });
    els.meta = h('p', { class: 'needs-meta' });
    const parts = [h('span', { class: 'needs-mark' }, glyph('needs', { size: 16 })), els.title, els.prompt ?? els.line, els.meta];
    if (e.mode === 'approve') {
      els.approve = h('button', { type: 'button', class: 'button needs-approve', dataset: { value: e.decisions.approve.value }, 'aria-pressed': 'false', onclick: () => this.#decide(view, 'approve') }, 'Approve once');
      els.deny = h('button', { type: 'button', class: 'button secondary needs-deny', dataset: { value: e.decisions.deny.value }, 'aria-pressed': 'false', onclick: () => this.#decide(view, 'deny') }, 'Deny');
      // The machine failed the op (an error, not an outcome): the card's Try again, the same request.
      els.resend = h('button', { type: 'button', class: 'button secondary needs-resend', hidden: true, onclick: () => { const x = view.entry; view.service?.resend(x.hostId, x.sessionId, x.questionId); } }, 'Try again');
      // The receipt's words, and apart from them the line a screen reader hears (receipt.js): its
      // announcements are the request's, so an answer the card shows too is said once, not twice.
      els.status = h('p', { class: 'needs-status', hidden: true });
      els.live = receiptLive();
      parts.push(h('div', { class: 'needs-actions' }, els.approve, els.deny, els.resend), els.status, els.live);
    } else if (e.mode === 'atMachine') {
      parts.push(h('p', { class: 'needs-at-machine' }, 'Answer at the machine'));
    }
    view.el = h('li', { class: 'needs-entry', dataset: { key: e.key, hostId: e.hostId, id: e.sessionId, mode: e.mode, kind: e.kind } }, parts);
    if (e.mode === 'approve') {
      const ctx = this.answering(e.hostId);
      if (ctx?.service) {
        view.service = ctx.service;
        // The lane's line keeps the receipt's quiet window, dwell and stalls, as the card's does.
        view.receipt = new ReceiptClock({
          describe: (entry, { stalled }) => describeAnswer(entry, this.machine(view.entry.hostId).name, { question: view.entry.question, stalled }),
          draw: (d) => {
            drawReceipt(els.status, 'needs-status', d);
            // From the tap, its line is there (empty unless something's wrong), so the entry grows
            // once, as the answer goes, not as an exception's words come.
            els.status.hidden = !d;
          },
          say: (text) => { els.live.textContent = text; },
          heard: () => drawn(els.live),
          delivered: () => holdCheck([els.approve, els.deny].find((b) => b.getAttribute('aria-pressed') === 'true')),
          follow: (fn) => ctx.service.watch(e.hostId, e.sessionId, e.questionId, fn),
        });
        view.unwatch = ctx.service.watch(e.hostId, e.sessionId, e.questionId, (entry) => this.#show(view, entry));
        this.#show(view, ctx.service.get(e.hostId, e.sessionId, e.questionId));
      }
    }
    return view;
  }

  #fill(view, e) {
    view.entry = e;
    const { els } = view;
    els.title.textContent = e.title;
    els.line.textContent = e.line;
    els.line.hidden = !e.line;
    if (els.prompt && view.prompt !== e.prompt) {
      view.prompt = e.prompt;
      els.prompt.replaceChildren(renderMarkdown(e.prompt ?? '', document, { approval: true }));
    }
    els.machine.textContent = e.machine;
    els.agent.textContent = e.agent;
    els.delegate.textContent = e.delegate ? `${e.delegate} asks` : '';
    els.age.dateTime = e.askedAt;
    els.age.title = localTime(e.askedAt);
    els.meta.replaceChildren(...[e.machine ? els.machine : null, els.agent, e.delegate ? els.delegate : null, els.age]
      .filter(Boolean).flatMap((el, i) => (i ? [' \u00b7 ', el] : [el])));
    // Two entries can both have Approve once: each button says which session it answers, and on
    // which machine when several are shown, since two machines can have sessions of one title.
    const subject = e.machine ? `${e.title} on ${e.machine}` : e.title;
    els.title.setAttribute('aria-label', `${subject}: open the session`);
    els.approve?.setAttribute('aria-label', `Approve once: ${subject}`);
    els.deny?.setAttribute('aria-label', `Deny: ${subject}`);
    els.resend?.setAttribute('aria-label', `Try again: ${subject}`);
    this.#age(view);
  }

  #age(view, now = Date.now()) {
    const d = duration(view.entry.askedAt, now);
    view.els.age.textContent = d;
    view.els.age.hidden = !d;
  }

  // As a tap on the card's button: the same state, the same check, the same service entry, so the
  // card shows the answer going too, and a second press while one is on its way sends nothing.
  #decide(view, which) {
    const e = view.entry;
    const service = view.service;
    if (!service) { return; }
    const current = service.get(e.hostId, e.sessionId, e.questionId);
    if (current && (current.state === 'sending' || current.state === 'waiting')) { return; }
    const { answer, problem } = prepareAnswer(e.question, tapState(e.question, e.decisions[which].value), e.sessionId);
    if (problem) {
      const text = `${problem[0].toUpperCase()}${problem.slice(1)}.`;
      view.els.status.textContent = text;
      view.els.status.className = 'needs-status bad';
      view.els.status.hidden = false;
      view.els.live.textContent = text;
      return;
    }
    service.submit({ hostId: e.hostId, sessionId: e.sessionId, questionId: e.questionId, answer });
  }

  // Whether an entry whose question has gone stays for its answer's outcome (LINGER_READ_MS).
  #lingers(view) {
    if (view.lingerOver) { return false; }
    if (view.lingering) { return true; }
    const x = view.entry;
    if (!view.service) { return false; }
    const current = view.service.get(x.hostId, x.sessionId, x.questionId);
    const now = Date.now();
    if (inFlight(current)) {
      view.lingerCap = setTimeout(() => this.#endLinger(view), LINGER_MAX_MS);
    } else if (current && view.outcomeUntil > now) {
      // Its outcome came before its question went: shown for what is left of its time.
      view.lingerEnd = setTimeout(() => this.#endLinger(view), view.outcomeUntil - now);
    } else {
      return false;
    }
    view.lingering = true;
    view.el.dataset.lingering = 'true';
    view.els.approve.disabled = view.els.deny.disabled = true;
    view.els.resend.hidden = true;
    return true;
  }

  #unlinger(view) {
    view.lingering = false;
    view.lingerOver = false;
    delete view.el.dataset.lingering;
    clearTimeout(view.lingerCap);
    clearTimeout(view.lingerEnd);
  }

  #endLinger(view) {
    clearTimeout(view.lingerCap);
    clearTimeout(view.lingerEnd);
    view.lingerOver = true;
    this.schedule();
  }

  #show(view, entry) {
    const { els } = view;
    clearTimeout(view.pause);
    const { again, pause } = answerControls(entry);
    view.el.dataset.answer = answerState(entry);
    // Held on this device for a machine that isn't connected: the ring on its button stands still.
    view.el.classList.toggle('still', entry?.state === 'waiting' && entry.reason === 'not connected');
    view.receipt.show(entry);
    // Its outcome, come in while this entry watched it go: shown for its check, or long enough to
    // read, whichever of it and its question going comes first.
    const now = Date.now();
    if (!entry || inFlight(entry)) {
      view.outcomeUntil = 0;
    } else if (view.wasInFlight) {
      const delivered = entry.state === 'done' && entry.outcome === 'delivered';
      view.outcomeUntil = delivered ? view.receipt.checkUntil ?? now + ReceiptTiming.sendCheckHoldMs : now + LINGER_READ_MS;
    }
    view.wasInFlight = inFlight(entry);
    if (view.lingering && !inFlight(entry)) {
      clearTimeout(view.lingerEnd);
      view.lingerEnd = setTimeout(() => this.#endLinger(view), Math.max(0, view.outcomeUntil - now));
    }
    // Its question gone, nothing in it can be pressed or tried again.
    els.approve.disabled = els.deny.disabled = !again || pause || view.lingering;
    els.resend.hidden = entry?.state !== 'failed' || view.lingering;
    if (pause && !view.lingering) { view.pause = setTimeout(() => { els.approve.disabled = els.deny.disabled = !!view.lingering; }, BUSY_PAUSE_MS); }
    const sent = !again ? entry.answer?.fields?.[view.entry.question.fields[0].name] : undefined;
    for (const b of [els.approve, els.deny]) { b.setAttribute('aria-pressed', typeof sent === 'string' && b.dataset.value === sent ? 'true' : 'false'); }
  }

  #drop(view) {
    clearTimeout(view.lingerCap);
    clearTimeout(view.lingerEnd);
    view.unwatch?.();
    view.receipt?.destroy();
    clearTimeout(view.pause);
    view.el.remove();
  }
}
