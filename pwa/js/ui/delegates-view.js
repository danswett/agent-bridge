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
    if (d) {
      this.#place(d.active, e.active, live, now);
      this.#place(d.finished, e.finishedList, live, now);
      e.active.hidden = d.active.length === 0;
      e.finished.hidden = d.finished.length === 0;
      e.finishedCount.textContent = `${d.finished.length} finished`;
      e.more.hidden = !d.more;
      e.more.textContent = d.more ? `${plural(d.more, 'more delegate', 'more delegates')} not listed` : '';
    }
    for (const [id, row] of this.#rows) {
      if (!live.has(id)) { row.dispose(); this.#rows.delete(id); }
    }
  }

  // Rows are kept and moved, never rebuilt, so an open one stays open as its delegate changes;
  // and only moved when the order changed, so focus stays where it was.
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
    const same = items.length === list.children.length && items.every((el, i) => list.children[i] === el);
    if (!same) { list.replaceChildren(...items); }
  }

  refreshTimes(now = Date.now()) {
    for (const row of this.#rows.values()) { row.refreshTime(now); }
  }

  dispose() {
    for (const row of this.#rows.values()) { row.dispose(); }
    this.#rows.clear();
  }
}

class DelegateRow {
  #a = null;
  #state = { loading: false, timer: null, key: null, loaded: false };

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
    e.detail.addEventListener('toggle', () => { if (e.detail.open) { this.#load(); } });
    this.element = h('li', { class: 'delegate', dataset: { id } }, e.detail);
  }

  update(a, now) {
    const first = this.#a === null;
    this.#a = a;
    const e = this.els;
    const root = this.element;
    root.dataset.status = a.status;
    root.dataset.depth = String(Math.min(a.depth, 3));
    setGlyph(e.glyph, a.glyph, { size: 14, label: a.words });
    e.name.textContent = a.name || 'Delegate';
    e.line.textContent = a.line ?? '';
    e.line.hidden = !a.line;
    this.refreshTime(now);
    e.facts.textContent = [a.kind === 'background' ? 'Background' : 'Task', a.model,
      a.currentTool ? `${a.currentTool.name}${a.currentTool.summary ? `: ${a.currentTool.summary}` : ''}` : null,
      a.shells ? plural(a.shells, 'background command', 'background commands') : null].filter(Boolean).join(' · ');
    e.task.textContent = a.task ?? '';
    e.taskBlock.hidden = !a.task;
    // An open delegate follows its entry: fetched again (at most every 2 s) as it changes.
    const key = JSON.stringify([a.status, a.outcome, a.line, a.endedAt]);
    if (!first && key !== this.#state.key && e.detail.open) { this.#schedule(); }
    this.#state.key = key;
  }

  refreshTime(now = Date.now()) {
    if (this.#a) { this.els.time.textContent = delegateTime(this.#a, now); }
  }

  dispose() {
    clearTimeout(this.#state.timer);
    this.#state.timer = null;
  }

  #schedule() {
    if (this.#state.timer) { return; }
    this.#state.timer = setTimeout(() => { this.#state.timer = null; this.#load(); }, 2000);
  }

  async #load() {
    const e = this.els;
    if (this.#state.loading) { return; }
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
        this.#show({ response: full.response, truncated: full.responseTruncated, reasoning: full.reasoning });
        this.#state.loaded = true;
      }
    } catch (err) {
      this.#show({ status: `Couldn't load the delegate: ${err.reason ?? err.message}`, warn: true });
    } finally {
      this.#state.loading = false;
    }
  }

  #show({ response = null, truncated = false, reasoning = null, status = null, warn = false }) {
    const e = this.els;
    e.status.hidden = !status;
    e.status.className = warn ? 'note warn' : 'note';
    e.status.textContent = status ?? '';
    // A failed fetch keeps what was fetched before; with nothing fetched yet, its latest line.
    if (status && this.#state.loaded) { return; }
    e.latest.replaceChildren(renderMarkdown(response || this.#a?.line || ''));
    e.latestBlock.hidden = !(response || this.#a?.line);
    e.truncated.hidden = !(truncated && response);
    e.reasoningText.textContent = reasoning ?? '';
    e.reasoning.hidden = !reasoning;
  }
}
