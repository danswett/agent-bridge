// One session's card: the old dashboard's session card, rebuilt for the phone. A surface
// whose edge and glow say what the session is doing (blue and breathing while it works,
// amber while it waits on you, calm when idle), with a header that stays visible when the
// card is folded, and the response, reasoning, history and question below it.
import { h, icon } from './dom.js';
import { ago, duration, localTime } from './time.js';
import { renderMarkdown } from '../markdown.js';
import { renderQuestion } from './question-view.js';

const AGENT_NAME = { copilot: 'Copilot', claude: 'Claude', codex: 'Codex', unknown: 'Agent' };
const STATUS_TEXT = { working: 'Working', waiting: 'Waiting on you', idle: 'Idle' };
const TOOL_MARK = { running: '⟳', done: '✓', failed: '✗' };

let seq = 0;

export class SessionCard {
  #s = null;
  #last = {};
  #history = { loaded: false, loading: false };

  // fetchDetail(id) -> Promise<Session|null>, for the tool history (session.get).
  constructor(session, { folds, fetchDetail, machineName }) {
    Object.assign(this, { folds, fetchDetail, machineName });
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
    e.body = h('div', { class: 'body', id: this.ids.body },
      e.question, e.truncated, e.response, e.reasoning, e.user, e.history, e.facts);

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
    // Toggled, not assigned: the frame also carries 'collapsed', which only folding changes.
    for (const st of ['working', 'waiting', 'idle']) { e.frame.classList.toggle(st, st === s.status); }

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

    e.statusText.textContent = STATUS_TEXT[s.status];
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

    if (this.#changed('question', s.question)) {
      e.question.replaceChildren(s.question ? renderQuestion(s.question, { machineName: this.machineName }) : '');
    }
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

  refreshTimes(now = Date.now()) {
    const s = this.#s;
    const since = s.status === 'idle' ? '' : duration(s.statusSince, now);
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
