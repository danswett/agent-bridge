// The Needs you lane on screen (ui.md §6.1, #165), above the sessions list and apart from it:
// rows never move for it. What it lists and offers comes from ../needs-you.js; this draws it.
// Entries are kept by session and updated in place, so a button with focus keeps it while the
// lane redraws around it.
import { h } from './dom.js';
import { glyph } from './glyph.js';
import { needsYou, AppBadge } from '../needs-you.js';
import { tapState, prepareAnswer } from '../answers.js';
import { BUSY_PAUSE_MS, answerControls, answerState, describeAnswer } from './question-view.js';
import { duration, localTime } from './time.js';
import { renderMarkdown } from '../markdown.js';

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
    this.ticker = setInterval(() => this.refreshTimes(), 15000);
    this.render();
  }

  get count() { return this.#entries.size; }

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
    });
    const live = new Set();
    const want = [];
    for (const e of lane) {
      live.add(e.key);
      let view = this.#entries.get(e.key);
      // A new question is a new entry: what became of an answer to the last one is not this one's.
      if (view && (view.questionId !== e.questionId || view.mode !== e.mode || !view.els.prompt !== (e.prompt === null))) { this.#drop(view); view = null; }
      if (!view) {
        view = this.#build(e);
        this.#entries.set(e.key, view);
      }
      this.#fill(view, e);
      want.push(view.el);
    }
    for (const [key, view] of this.#entries) {
      if (!live.has(key)) { this.#drop(view); this.#entries.delete(key); }
    }
    let prev = null;
    for (const el of want) {
      const at = prev ? prev.nextSibling : this.list.firstChild;
      if (at !== el) { this.list.insertBefore(el, at); }
      prev = el;
    }
    while (prev ? prev.nextSibling : this.list.firstChild) { (prev ? prev.nextSibling : this.list.firstChild).remove(); }
    this.head.textContent = `Needs you \u00b7 ${lane.length}`;
    this.container.hidden = lane.length === 0;
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
    // An approval answered here shows its prompt whole: everything Approve once allows.
    els.prompt = e.prompt !== null ? h('div', { class: 'md needs-prompt' }) : null;
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
      els.status = h('p', { class: 'needs-status', role: 'status', 'aria-live': 'polite', hidden: true });
      parts.push(h('div', { class: 'needs-actions' }, els.approve, els.deny, els.resend), els.status);
    } else if (e.mode === 'atMachine') {
      parts.push(h('p', { class: 'needs-at-machine' }, 'Answer at the machine'));
    }
    view.el = h('li', { class: 'needs-entry', dataset: { key: e.key, hostId: e.hostId, id: e.sessionId, mode: e.mode, kind: e.kind } }, parts);
    if (e.mode === 'approve') {
      const ctx = this.answering(e.hostId);
      if (ctx?.service) {
        view.service = ctx.service;
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
      els.prompt.replaceChildren(renderMarkdown(e.prompt ?? ''));
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
      view.els.status.textContent = `${problem[0].toUpperCase()}${problem.slice(1)}.`;
      view.els.status.className = 'needs-status bad';
      view.els.status.hidden = false;
      return;
    }
    service.submit({ hostId: e.hostId, sessionId: e.sessionId, questionId: e.questionId, answer });
  }

  #show(view, entry) {
    const { els } = view;
    clearTimeout(view.pause);
    const shown = entry ? describeAnswer(entry, this.machine(view.entry.hostId).name) : { text: '', tone: '' };
    const { again, pause } = answerControls(entry);
    view.el.dataset.answer = answerState(entry);
    els.status.textContent = shown.text;
    els.status.className = `needs-status${shown.tone ? ` ${shown.tone}` : ''}`;
    els.status.hidden = !shown.text;
    els.approve.disabled = els.deny.disabled = !again || pause;
    els.resend.hidden = entry?.state !== 'failed';
    if (pause) { view.pause = setTimeout(() => { els.approve.disabled = els.deny.disabled = false; }, BUSY_PAUSE_MS); }
    const sent = !again ? entry.answer?.fields?.[view.entry.question.fields[0].name] : undefined;
    for (const b of [els.approve, els.deny]) { b.setAttribute('aria-pressed', typeof sent === 'string' && b.dataset.value === sent ? 'true' : 'false'); }
  }

  #drop(view) {
    view.unwatch?.();
    clearTimeout(view.pause);
    view.el.remove();
  }
}
