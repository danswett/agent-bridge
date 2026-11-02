// A session's delegates on its card (ui.md §6.3, in today's layout): one row each, with its
// glyph, its name, how long it has run or "finished", and its latest line, which is its words and
// never the main agent's. Finished ones fold into "N finished". Opening a row shows the delegate's
// task, and its own latest response and reasoning, fetched from the machine (session.get with
// agent). There is no reply, Stop or typing here: replies only ever go to the main agent
// (subagents.md §6). Which delegates, in what order and with which glyph is attribution.js's.
import { h, icon } from './dom.js';
import { glyph, setGlyph } from './glyph.js';
import { renderMarkdown } from '../markdown.js';
import { delegateList, delegateTime } from '../attribution.js';

const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;

export class DelegatesBlock {
  #rows = new Map();

  // fetchDelegate(agentId) -> Promise<Subagent|null>, or null when nothing can fetch one.
  constructor({ fetchDelegate = null } = {}) {
    this.fetchDelegate = fetchDelegate;
    const e = this.els = {};
    e.active = h('ul', { class: 'delegate-list' });
    e.finishedCount = h('span');
    e.finishedList = h('ul', { class: 'delegate-list' });
    e.finished = h('details', { class: 'delegates-finished', hidden: true }, h('summary', null, e.finishedCount), e.finishedList);
    e.more = h('p', { class: 'note delegates-more', hidden: true });
    this.element = h('section', { class: 'delegates', 'aria-label': 'Delegates', hidden: true },
      h('h4', { class: 'delegates-head' }, 'Delegates'), e.active, e.finished, e.more);
  }

  update(session, now = Date.now()) {
    const d = delegateList(session);
    const e = this.els;
    this.element.hidden = !d;
    const live = new Set();
    // A row that moves keeps keyboard focus: moving a node in the DOM drops it.
    const focused = this.element.contains(document.activeElement) ? document.activeElement : null;
    if (d) {
      // An open delegate that finishes moves into the fold, which opens so it stays in view.
      const wasActive = new Set([...e.active.children].map((el) => el.dataset.id));
      const moved = d.finished.some((r) => wasActive.has(r.id) && this.#rows.get(r.id)?.open);
      this.#place(d.active, e.active, live, now);
      this.#place(d.finished, e.finishedList, live, now);
      if (moved) { e.finished.open = true; }
      e.active.hidden = d.active.length === 0;
      e.finished.hidden = d.finished.length === 0;
      e.finishedCount.textContent = `${d.finished.length} finished`;
      e.more.hidden = !d.more;
      e.more.textContent = d.more ? `${plural(d.more, 'more delegate', 'more delegates')} not listed` : '';
    }
    for (const [id, row] of this.#rows) {
      if (!live.has(id)) { row.dispose(); row.element.remove(); this.#rows.delete(id); }
    }
    if (focused?.isConnected && document.activeElement !== focused) { focused.focus({ preventScroll: true }); }
  }

  // Rows are kept and moved, never rebuilt, so an open one stays open as its delegate changes;
  // and only the rows out of place are moved.
  #place(rows, list, live, now) {
    const items = rows.map((r) => {
      live.add(r.id);
      let row = this.#rows.get(r.id);
      if (!row) {
        row = new DelegateRow(r.id, this.fetchDelegate);
        this.#rows.set(r.id, row);
      }
      row.update(r, now);
      return row.element;
    });
    items.forEach((el, i) => { if (list.children[i] !== el) { list.insertBefore(el, list.children[i] ?? null); } });
    // What is left after them is rows that moved to the other list or went.
    while (list.children.length > items.length) { list.lastElementChild.remove(); }
  }

  refreshTimes(now = Date.now()) {
    for (const row of this.#rows.values()) { row.refreshTime(now); }
  }

  dispose() {
    for (const row of this.#rows.values()) { row.dispose(); }
    this.#rows.clear();
  }
}

// An open delegate that is running is fetched again this often: a host sends no update when only
// its line changes (v1-sessions.md, Subagent), so its words would otherwise stand still.
const POLL_MS = 10000;
// And at most this often when its entry changes.
const CHANGE_MS = 2000;

class DelegateRow {
  #a = null;
  #state = { loading: false, again: false, timer: null, due: 0, key: null, loaded: false };
  // Its task and latest line, from the list or a fetch, whichever came last. A list the session
  // budget trimmed leaves them out; that is not the delegate saying nothing.
  #task = null;
  #line = null;
  #listLine = null;

  constructor(id, fetchDelegate) {
    this.id = id;
    this.fetchDelegate = fetchDelegate;
    const e = this.els = {};
    e.glyph = glyph('idle', { size: 14 });
    e.name = h('span', { class: 'delegate-name' });
    e.time = h('span', { class: 'delegate-time' });
    e.line = h('span', { class: 'delegate-line' });
    e.facts = h('p', { class: 'delegate-facts' });
    e.task = h('p', { class: 'plain' });
    e.taskBlock = h('div', { class: 'delegate-task', hidden: true }, h('h5', null, 'Task'), e.task);
    e.status = h('p', { class: 'note', role: 'status', hidden: true });
    e.truncated = h('p', { class: 'note truncated', hidden: true }, 'Earlier text not shown: the response was longer than the machine sends.');
    e.latest = h('div', { class: 'md' });
    e.latestBlock = h('div', { class: 'delegate-latest', hidden: true }, h('h5', null, 'Latest'), e.truncated, e.latest);
    e.reasoningText = h('p', { class: 'plain' });
    e.reasoning = h('details', { class: 'reasoning', hidden: true }, h('summary', null, 'Reasoning'), e.reasoningText);
    e.detail = h('details', { class: 'delegate-detail' },
      h('summary', { class: 'delegate-summary' },
        h('span', { class: 'delegate-head' }, e.glyph, e.name, e.time, icon('chevron', 'icon delegate-chevron')), e.line),
      h('div', { class: 'delegate-body' }, e.facts, e.taskBlock, e.status, e.latestBlock, e.reasoning));
    e.detail.addEventListener('toggle', () => {
      if (e.detail.open) { this.#load(); } else { this.#stop(); }
    });
    this.element = h('li', { class: 'delegate', dataset: { id } }, e.detail);
  }

  get open() { return this.els.detail.open; }

  update(a, now) {
    const first = this.#a === null;
    this.#a = a;
    const e = this.els;
    const root = this.element;
    root.dataset.status = a.status;
    root.dataset.depth = String(Math.min(a.depth, 3));
    setGlyph(e.glyph, a.glyph, { size: 14, label: a.words });
    e.name.textContent = a.name || 'Delegate';
    if (a.line !== null && a.line !== this.#listLine) { this.#line = a.line; }
    this.#listLine = a.line;
    if (a.task) { this.#task = a.task; }
    this.#drawWords();
    this.refreshTime(now);
    e.facts.textContent = [a.kind === 'background' ? 'Background' : 'Task', a.model,
      a.currentTool ? `${a.currentTool.name}${a.currentTool.summary ? `: ${a.currentTool.summary}` : ''}` : null,
      a.shells ? plural(a.shells, 'background command', 'background commands') : null].filter(Boolean).join(' · ');
    // An open delegate follows its entry: fetched again (at most every 2 s) as it changes.
    const key = JSON.stringify([a.status, a.outcome, a.line, a.endedAt]);
    if (!first && key !== this.#state.key && e.detail.open) { this.#schedule(CHANGE_MS); }
    this.#state.key = key;
  }

  #drawWords() {
    const e = this.els;
    e.line.textContent = this.#line ?? '';
    e.line.hidden = !this.#line;
    e.task.textContent = this.#task ?? '';
    e.taskBlock.hidden = !this.#task;
  }

  refreshTime(now = Date.now()) {
    if (this.#a) { this.els.time.textContent = delegateTime(this.#a, now); }
  }

  dispose() {
    this.#stop();
  }

  #stop() {
    clearTimeout(this.#state.timer);
    this.#state.timer = null;
    this.#state.again = false;
  }

  // A fetch in ms, unless one is due sooner.
  #schedule(ms) {
    const due = Date.now() + ms;
    if (this.#state.timer && this.#state.due <= due) { return; }
    clearTimeout(this.#state.timer);
    this.#state.due = due;
    this.#state.timer = setTimeout(() => { this.#state.timer = null; this.#load(); }, ms);
  }

  async #load() {
    const e = this.els;
    // One at a time; a fetch asked for meanwhile goes once this one is back.
    if (this.#state.loading) { this.#state.again = true; return; }
    if (!this.fetchDelegate) { this.#show({}); return; }
    this.#state.loading = true;
    if (!this.#state.loaded) {
      e.status.hidden = false;
      e.status.className = 'note';
      e.status.textContent = 'Loading…';
    }
    try {
      const full = await this.fetchDelegate(this.id);
      if (!full) {
        this.#show({ status: 'This delegate is no longer listed.' });
      } else {
        if (full.line) { this.#line = full.line; }
        if (full.task) { this.#task = full.task; }
        this.#drawWords();
        this.#show({ response: full.response, truncated: full.responseTruncated, reasoning: full.reasoning });
        this.#state.loaded = true;
      }
    } catch (err) {
      this.#show({ status: `Couldn't load the delegate: ${err.reason ?? err.message}`, warn: true });
    } finally {
      this.#state.loading = false;
    }
    if (!e.detail.open) { return; }
    if (this.#state.again) {
      this.#state.again = false;
      this.#load();
    } else if (this.#a && (this.#a.status === 'working' || this.#a.status === 'waiting')) {
      this.#schedule(POLL_MS);
    }
  }

  #show({ response = null, truncated = false, reasoning = null, status = null, warn = false }) {
    const e = this.els;
    e.status.hidden = !status;
    e.status.className = warn ? 'note warn' : 'note';
    e.status.textContent = status ?? '';
    // A failed fetch keeps what was fetched before; with nothing fetched yet, its latest line.
    if (status && this.#state.loaded) { return; }
    e.latest.replaceChildren(renderMarkdown(response || this.#line || ''));
    e.latestBlock.hidden = !(response || this.#line);
    e.truncated.hidden = !(truncated && response);
    e.reasoningText.textContent = reasoning ?? '';
    e.reasoning.hidden = !reasoning;
  }
}