// One session's card: the old dashboard's session card, rebuilt for the phone. A surface
// whose edge and glow say what the session is doing (blue and breathing while it works,
// amber while it waits on you, calm when idle, a still, dashed blue edge while it is
// idle but delegating: its own turn is over and work it started in the background is not,
// and a dotted grey one while the machine ends it), with a header that stays visible when
// the card is folded, and the response, reasoning, history and question below it, a box to
// reply to the session, and End session at the foot.
import { h, icon } from './dom.js';
import { ago, duration, localTime } from './time.js';
import { renderMarkdown } from '../markdown.js';
import { QuestionPanel, describeAnswer, renderQuestion } from './question-view.js';
import { answerability } from '../answers.js';
import { ReplyBox } from './reply-view.js';
import { EndSession } from './stop-view.js';

const AGENT_NAME = { copilot: 'Copilot', claude: 'Claude', codex: 'Codex', unknown: 'Agent' };
const STATUS_TEXT = { working: 'Working', waiting: 'Waiting on you', idle: 'Idle', ending: 'Ending session…' };
const TOOL_MARK = { running: '⟳', done: '✓', failed: '✗' };

// What the card shows: the status, except that an idle session still waiting on work it
// started in the background is not finished, and reads as delegating, as on the old
// dashboard. Agents are named first: a session waiting on both is delegating more than it
// is running a command. A session waiting on a prompt only the machine can answer says so,
// so nobody expects to answer it from here.
export function cardState(s) {
  const b = s.activity.background;
  if (s.status === 'waiting' && s.question?.answerAtMachine) { return { view: 'waiting', text: 'Waiting at the machine' }; }
  if (s.status !== 'idle' || !b) { return { view: s.status, text: STATUS_TEXT[s.status] }; }
  return { view: 'delegating', text: b.agents > 0 ? 'Waiting for background agents' : 'Waiting for background commands' };
}

const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;

let seq = 0;

export class SessionCard {
  #s = null;
  #last = {};
  #history = { loaded: false, loading: false };
  #panel = null;
  #receipt = null;
  #orphan = null;
  #reply = null;
  // The host the reply box is for, and what it held when it last went away (the machine
  // stopped taking replies for a moment, say), so it comes back with the words in it.
  #replyHost = null;
  #replyDraft = { hostId: null, text: '' };
  #stop = null;
  #stopHost = null;

  // fetchDetail(id) -> Promise<Session|null>, for the tool history (session.get).
  // answering() -> { service, replies, stops, hostId, capabilities } | null: who answers
  // questions, sends replies and ends sessions, and whether the connected machine takes
  // each at all.
  constructor(session, { folds, fetchDetail, machineName, answering = () => null }) {
    Object.assign(this, { folds, fetchDetail, machineName, answering });
    const n = ++seq;
    this.ids = { title: `s${n}-title`, body: `s${n}-body` };
    this.#build();
    this.update(session);
  }

  get id() { return this.#s.id; }
  get session() { return this.#s; }

  #build() {
    const e = this.els = {};
    e.badge = h('span', { class: 'agent-badge' });
    e.titleText = h('span', { class: 'title-text' });
    e.fold = h('button', { class: 'fold', type: 'button', id: this.ids.title, 'aria-controls': this.ids.body, onclick: () => this.setFolded(!this.folded) },
      e.badge, e.titleText, icon('chevron', 'icon chevron'));
    e.agentName = h('span', { class: 'agent-name' });
    e.meta = h('p', { class: 'meta' });
    e.statusText = h('span', { class: 'status-text' });
    e.since = h('span', { class: 'since' });
    e.updated = h('time', { class: 'updated' });
    e.statusLine = h('p', { class: 'status-line' },
      h('span', { class: 'dot', 'aria-hidden': 'true' }), e.statusText, e.since, e.updated);
    e.toolName = h('span', { class: 'tool-name' });
    e.toolSummary = h('span', { class: 'tool-summary' });
    e.tool = h('p', { class: 'tool-line', hidden: true },
      h('span', { class: 'spinner', 'aria-hidden': 'true' }), e.toolName, e.toolSummary);

    e.question = h('div', { class: 'question-slot' });
    e.truncated = h('p', { class: 'note truncated', hidden: true }, 'Earlier text not shown: the response was longer than the machine sends.');
    e.response = h('div', { class: 'md response' });
    e.reasoningText = h('p', { class: 'plain' });
    e.reasoning = h('details', { class: 'reasoning', hidden: true }, h('summary', null, 'Reasoning'), e.reasoningText);
    e.userText = h('p', { class: 'plain' });
    e.user = h('details', { class: 'user-message', hidden: true }, h('summary', null, 'Your last message'), e.userText);
    e.historyBody = h('div', { class: 'history-body' });
    e.history = h('details', { class: 'history' }, h('summary', null, 'Tool history'), e.historyBody);
    e.history.addEventListener('toggle', () => { if (e.history.open) { this.#loadHistory(); } });
    e.facts = h('p', { class: 'facts' });
    e.reply = h('div', { class: 'reply-slot' });
    // End session is the card's footer, last and furthest from Send, as on the old dashboard.
    e.stop = h('div', { class: 'stop-slot' });
    e.body = h('div', { class: 'body', id: this.ids.body },
      e.question, e.truncated, e.response, e.reasoning, e.user, e.reply, e.history, e.facts, e.stop);

    e.frame = h('div', { class: 'frame' },
      h('header', { class: 'head' }, h('h3', { class: 'title' }, e.fold), e.meta, e.statusLine, e.tool),
      e.body);
    this.element = h('article', { class: 'session', 'aria-labelledby': this.ids.title },
      e.frame, h('div', { class: 'glow', 'aria-hidden': 'true' }));
  }

  #changed(key, value) {
    const v = typeof value === 'string' ? value : JSON.stringify(value);
    if (this.#last[key] === v) { return false; }
    this.#last[key] = v;
    return true;
  }

  update(s) {
    const first = this.#s === null;
    this.#s = s;
    const e = this.els;
    const root = this.element;
    root.dataset.id = s.id;
    root.dataset.status = s.status;
    root.dataset.agent = s.agent;
    const state = cardState(s);
    root.dataset.view = state.view;
    // Toggled, not assigned: the frame also carries 'collapsed', which only folding changes.
    for (const st of ['working', 'waiting', 'idle', 'delegating', 'ending']) { e.frame.classList.toggle(st, st === state.view); }

    if (this.#changed('agent', s.agent)) {
      e.badge.className = `agent-badge agent-${s.agent}`;
      e.badge.replaceChildren(icon(s.agent));
      e.agentName.textContent = AGENT_NAME[s.agent];
    }
    e.titleText.textContent = s.title || 'Untitled session';

    if (this.#changed('meta', [s.agent, s.repository, s.branch, s.model, s.cwd])) {
      const bits = [e.agentName];
      const where = s.repository ?? lastSegment(s.cwd);
      if (where) { bits.push(h('span', { class: 'repo' }, where)); }
      if (s.branch) { bits.push(h('span', { class: 'branch' }, s.branch)); }
      if (s.model) { bits.push(h('span', { class: 'model' }, s.model)); }
      e.meta.replaceChildren(...bits);
    }

    e.statusText.textContent = state.text;
    e.updated.dateTime = s.updatedAt;
    e.updated.title = localTime(s.updatedAt);
    this.refreshTimes();

    const t = s.activity.currentTool;
    e.tool.hidden = !t;
    if (t) {
      e.tool.dataset.status = t.status;
      e.toolName.textContent = t.name;
      e.toolSummary.textContent = t.summary ?? '';
    }

    this.#renderQuestion(s);
    this.#renderReply(s);
    this.#renderStop(s);
    if (this.#changed('response', s.activity.response ?? '')) {
      e.response.replaceChildren(renderMarkdown(s.activity.response ?? ''));
    }
    e.response.hidden = !s.activity.response;
    e.truncated.hidden = !(s.activity.responseTruncated && s.activity.response);
    e.reasoning.hidden = !s.activity.reasoning;
    e.reasoningText.textContent = s.activity.reasoning ?? '';
    e.user.hidden = !s.activity.userMessage;
    e.userText.textContent = s.activity.userMessage ?? '';

    const facts = [];
    const b = s.activity.background;
    if (b?.agents) { facts.push(plural(b.agents, 'background agent', 'background agents')); }
    if (b?.shells) { facts.push(plural(b.shells, 'background command', 'background commands')); }
    if (s.activity.turnCount !== null) { facts.push(`${s.activity.turnCount} turns`); }
    if (s.activity.compactions) { facts.push(`${s.activity.compactions} compaction${s.activity.compactions === 1 ? '' : 's'}`); }
    if (s.processId !== null) { facts.push(`pid ${s.processId}`); }
    if (s.cwd) { facts.push(s.cwd); }
    e.facts.textContent = facts.join(' · ');
    e.facts.hidden = facts.length === 0;

    // A new question opens a folded card: it is waiting on you, and must not stay folded
    // away unseen.
    const reopened = this.folds.noteQuestion(s.id, s.question?.id ?? null);
    if (first || reopened) { this.#applyFolded(this.folds.isFolded(s.id)); }

    // An open history follows the session: refetched (at most every 2 s) as it changes.
    if (!first && e.history.open && this.#changed('historyAt', s.updatedAt)) { this.#scheduleHistory(); }
  }

  // The card is going: its session ended or another machine is shown.
  dispose() {
    this.#panel?.destroy();
    this.#panel = null;
    this.#reply?.destroy();
    this.#reply = null;
    this.#stop?.destroy();
    this.#stop = null;
    this.#dropReceipt();
    clearTimeout(this.#history.timer);
  }

  // The machine's capabilities changed (a reconnect to another version of its host).
  refreshQuestion() {
    if (!this.#s) { return; }
    this.#renderQuestion(this.#s);
    this.#renderReply(this.#s);
    this.#renderStop(this.#s);
  }

  // End session, while the connected machine can end sessions.
  #renderStop(s) {
    const ctx = this.answering();
    const hostId = ctx?.stops && ctx.capabilities?.includes('sessions.stop') ? ctx.hostId : null;
    if (this.#stopHost !== hostId || (hostId && !this.#stop)) {
      this.#stop?.destroy();
      this.#stop = hostId ? new EndSession({ machineName: this.machineName, stopping: { service: ctx.stops, hostId, sessionId: s.id } }) : null;
      this.#stopHost = hostId;
      this.els.stop.replaceChildren(...(this.#stop ? [this.#stop.element] : []));
    }
    this.#stop?.update(s);
  }

  // The reply box, while the connected machine takes replies.
  #renderReply(s) {
    const ctx = this.answering();
    const hostId = ctx?.replies && ctx.capabilities?.includes('sessions.reply') ? ctx.hostId : null;
    if (this.#replyHost !== hostId || (hostId && !this.#reply)) {
      if (this.#reply) {
        this.#replyDraft = { hostId: this.#replyHost, text: this.#reply.text };
        this.#reply.destroy();
        this.#reply = null;
      }
      if (hostId) {
        this.#reply = new ReplyBox({ machineName: this.machineName, replying: { service: ctx.replies, hostId, sessionId: s.id }, attach: ctx.capabilities.includes('sessions.attach'), largeMax: ctx.largeAttachments?.() ?? 0 });
        if (this.#replyDraft.hostId === hostId) { this.#reply.els.input.value = this.#replyDraft.text; }
      }
      this.#replyHost = hostId;
      this.els.reply.replaceChildren(...(this.#reply ? [this.#reply.element] : []));
    }
    this.#reply?.update(s, { attach: ctx?.capabilities?.includes('sessions.attach') === true, largeMax: ctx?.largeAttachments?.() ?? 0 });
  }

  #renderQuestion(s) {
    const q = s.question;
    const ctx = this.answering();
    const able = q ? answerability(q, ctx?.capabilities ?? [], s.agent) : null;
    const answerable = !!(able?.ok && ctx?.service);
    if (!this.#changed('question', [q, answerable, answerable ? ctx.hostId : null, able?.why, ctx?.readOnly ?? null])) { return; }
    const old = this.#panel;
    const sameQuestion = !!(old && q && old.questionId === q.id && answerable && old.answering.hostId === ctx.hostId);
    if (old && !sameQuestion) {
      // The question changed or went while it was being answered. Text the person typed
      // and hasn't sent is kept on the card, not dropped with the form; an answer that went
      // keeps saying what became of it.
      const draft = old.unsentText();
      const entry = old.entry;
      if (draft) {
        this.#orphan = { text: draft, why: !q || q.id !== old.questionId ? 'changed' : 'unanswerable' };
      } else if (entry) {
        this.#watchReceipt(old.answering, old.questionId);
      }
    }
    old?.destroy();
    this.#panel = null;
    let view = '';
    if (q && answerable) {
      this.#panel = new QuestionPanel(q, {
        machineName: this.machineName,
        answering: { service: ctx.service, hostId: ctx.hostId, sessionId: s.id },
        state: sameQuestion ? old.state : null,
      });
      view = this.#panel.element;
    } else if (q) {
      view = renderQuestion(q, { machineName: this.machineName, why: able.why, readOnly: ctx?.readOnly ?? null });
    }
    // A new question replaces a receipt that has said its last word.
    if (q && this.#receipt?.final) { this.#dropReceipt(); }
    this.els.question.replaceChildren(...[this.#orphanView(), this.#receipt?.element, view].filter(Boolean));
  }

  #watchReceipt({ service, hostId, sessionId }, questionId) {
    this.#dropReceipt();
    const element = h('p', { class: 'answer-receipt', role: 'status', 'aria-live': 'polite' });
    const receipt = { element, final: false, unwatch: null };
    const show = (entry) => {
      if (!entry) { return; }
      const { text, tone } = describeAnswer(entry, this.machineName);
      element.className = `answer-receipt ${tone}`;
      element.dataset.answer = entry.state === 'done' ? entry.outcome : entry.state;
      element.textContent = `Your answer: ${text}`;
      receipt.final = entry.state === 'done';
    };
    receipt.unwatch = service.watch(hostId, sessionId, questionId, show);
    show(service.get(hostId, sessionId, questionId));
    this.#receipt = receipt;
  }

  #dropReceipt() {
    this.#receipt?.unwatch?.();
    this.#receipt?.element.remove();
    this.#receipt = null;
  }

  #orphanView() {
    const o = this.#orphan;
    if (!o) { return null; }
    if (!o.element) {
      const ta = h('textarea', { class: 'text-input', readonly: true, rows: 3, 'aria-label': 'What you typed' });
      ta.value = o.text;
      o.element = h('div', { class: 'question-changed', role: 'alert' },
        h('p', { class: 'note warn' }, o.why === 'changed'
          ? 'The question changed before you sent your answer. It was answered at the machine, or the agent moved on. What you typed is kept here.'
          : 'This question can no longer be answered from here. What you typed is kept here.'),
        ta,
        h('button', { type: 'button', class: 'button secondary', onclick: () => { this.#orphan = null; o.element.remove(); } }, 'Dismiss'));
    }
    return o.element;
  }

  refreshTimes(now = Date.now()) {
    const s = this.#s;
    // Idle needs no clock; delegating does, since it is waiting on something.
    const since = cardState(s).view === 'idle' ? '' : duration(s.statusSince, now);
    this.els.since.textContent = since ? `for ${since}` : '';
    const u = ago(s.updatedAt, now);
    this.els.updated.textContent = u ? `updated ${u}` : '';
  }

  get folded() { return this.els.frame.classList.contains('collapsed'); }

  setFolded(folded) {
    this.folds.setFolded(this.#s.id, folded);
    this.#applyFolded(folded);
  }

  #applyFolded(folded) {
    this.els.frame.classList.toggle('collapsed', folded);
    this.els.body.hidden = folded;
    this.els.fold.setAttribute('aria-expanded', folded ? 'false' : 'true');
  }

  #scheduleHistory() {
    if (this.#history.timer) { return; }
    this.#history.timer = setTimeout(() => { this.#history.timer = null; this.#loadHistory(); }, 2000);
  }

  async #loadHistory() {
    if (this.#history.loading || !this.fetchDetail) { return; }
    this.#history.loading = true;
    const body = this.els.historyBody;
    if (!this.#history.loaded) { body.replaceChildren(h('p', { class: 'note', role: 'status' }, 'Loading…')); }
    try {
      const full = await this.fetchDetail(this.#s.id);
      if (!full) {
        body.replaceChildren(h('p', { class: 'note' }, 'This session has ended.'));
      } else {
        const list = full.activity.history ?? [];
        body.replaceChildren(list.length
          ? h('ol', { class: 'tools' }, list.slice(-50).reverse().map(toolRow))
          : h('p', { class: 'note' }, 'No tool calls yet.'));
      }
      this.#history.loaded = true;
    } catch (e) {
      body.replaceChildren(h('p', { class: 'note warn' }, `Couldn't load the history: ${e.message}`));
    } finally {
      this.#history.loading = false;
    }
  }
}

function toolRow(t) {
  return h('li', { class: `tool ${t.status}`, dataset: { status: t.status } },
    h('span', { class: 'tool-mark', 'aria-hidden': 'true' }, TOOL_MARK[t.status]),
    h('span', { class: 'sr-only' }, `${t.status}: `),
    h('span', { class: 'tool-name' }, t.name),
    t.summary ? h('span', { class: 'tool-summary' }, t.summary) : null,
    t.startedAt ? h('time', { class: 'tool-time', datetime: t.startedAt, title: localTime(t.startedAt) }, ago(t.startedAt)) : null);
}

function lastSegment(path) {
  if (!path) { return null; }
  const parts = path.split(/[\\/]/).filter(Boolean);
  return parts[parts.length - 1] ?? null;
}
