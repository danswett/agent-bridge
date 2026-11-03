// A session's delegates in the session pane (ui.md §6.3; docs/design/subagents.md §6):
//   DelegateStrip: at three panes, under the header, one item per delegate the main agent
//     started, its glyph, name and how long or "N nested", and "N finished" folding the rest; on a
//     phone the same items as a chip row.
//   DelegatesBlock: the Delegates tab's list, every delegate nested under its parent, the
//     finished ones folded into "N finished"; and a delegate's own nested list.
//   DelegateDetail: the delegate opened from any of them, in the inspector or pushed in the
//     tab: its name and glyph; Status, Model, Kind and ID; its Task, Latest words and Reasoning;
//     its Tools; the delegates it started; and "Ask main agent to stop it", which fills the reply
//     box and sends nothing. Its own words come from the machine (session.get with agent).
// There is no reply, Stop or typing to a delegate anywhere here: replies only ever go to the main
// agent. Which delegates, in what order and with which glyph is attribution.js's.
import { h, icon } from './dom.js';
import { glyph, setGlyph } from './glyph.js';
import { renderMarkdown } from '../markdown.js';
import { askToStopText, delegateList, delegateName, delegateState, delegateTime, descendants } from '../attribution.js';
import { clock, localTime } from './time.js';

const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;
const TOOL_GLYPH = { running: 'working', done: 'done', failed: 'failed' };

// One delegate's row, in the Delegates tab or a delegate's nested list: its glyph on line 1, its
// name and how long it has run, and its latest line, which is its words and never the main
// agent's. Pressed, it opens the delegate.
class DelegateRow {
  #a = null;
  // Its latest line, kept when a list the session budget trimmed leaves it out: that is not the
  // delegate saying nothing.
  #line = null;

  constructor(id, onOpen) {
    this.id = id;
    const e = this.els = {};
    e.glyph = glyph('idle', { size: 14 });
    e.name = h('span', { class: 'delegate-name' });
    e.time = h('span', { class: 'delegate-time' });
    e.under = h('span', { class: 'sr-only' });
    e.line = h('span', { class: 'delegate-line' });
    e.open = h('button', { type: 'button', class: 'delegate-summary', onclick: () => onOpen(id) },
      h('span', { class: 'delegate-head' }, e.glyph, h('span', { class: 'delegate-words' }, e.name, e.under, e.time), icon('chevron', 'icon delegate-chevron')), e.line);
    this.element = h('li', { class: 'delegate', dataset: { id } }, e.open);
  }

  update(a, now, selected = false) {
    this.#a = a;
    const e = this.els;
    this.element.dataset.status = a.status;
    this.element.dataset.depth = String(Math.min(a.depth, 3));
    setGlyph(e.glyph, a.glyph, { size: 14, label: a.words });
    e.name.textContent = a.name;
    // Who started it, said to a screen reader, which can't see the indent; and shown where the
    // indent can't say it: a parent in the other list, or deeper than the indent goes.
    e.under.textContent = a.parentName ? `under ${a.parentName}` : '';
    e.under.className = a.parentName && (a.depth === 0 || a.depth > 3) ? 'delegate-under' : 'sr-only';
    if (a.line !== null && a.line !== undefined) { this.#line = a.line; }
    e.line.textContent = this.#line ?? '';
    e.line.hidden = !this.#line;
    this.setSelected(selected);
    this.refreshTime(now);
  }

  setSelected(selected) {
    if (selected) { this.els.open.setAttribute('aria-current', 'true'); } else { this.els.open.removeAttribute('aria-current'); }
  }

  refreshTime(now = Date.now()) {
    if (this.#a) { this.els.time.textContent = delegateTime(this.#a, now); }
  }
}

// Rows are kept and moved, never rebuilt, so the one being read keeps focus; and only those out
// of place are moved.
function place(rows, list, map, live, make, now, selected) {
  const items = rows.map((r) => {
    live.add(r.id);
    let row = map.get(r.id);
    if (!row) {
      row = make(r.id);
      map.set(r.id, row);
    }
    row.update(r, now, r.id === selected);
    return row.element;
  });
  items.forEach((el, i) => { if (list.children[i] !== el) { list.insertBefore(el, list.children[i] ?? null); } });
  // What is left after them is rows that moved to the other list or went.
  while (list.children.length > items.length) { list.lastElementChild.remove(); }
}

// Moving a node in the DOM drops keyboard focus: put back where it was.
function keepFocus(root, fn) {
  const focused = root.contains(document.activeElement) ? document.activeElement : null;
  fn();
  if (focused?.isConnected && document.activeElement !== focused) { focused.focus({ preventScroll: true }); }
}

export class DelegatesBlock {
  #rows = new Map();
  #selected = null;
  #finishedIds = new Set();

  // onOpen(id): a row pressed.
  constructor({ onOpen = () => {} } = {}) {
    this.onOpen = onOpen;
    const e = this.els = {};
    e.active = h('ul', { class: 'delegate-list' });
    e.finishedCount = h('span');
    e.finishedList = h('ul', { class: 'delegate-list' });
    e.finished = h('details', { class: 'delegates-finished', hidden: true }, h('summary', null, e.finishedCount), e.finishedList);
    e.more = h('p', { class: 'note delegates-more', hidden: true });
    this.element = h('section', { class: 'delegates', 'aria-label': 'Delegates', hidden: true },
      h('h4', { class: 'delegates-head' }, 'Delegates'), e.active, e.finished, e.more);
  }

  // selected: the delegate open now, whose row says so; one that finishes moves into the fold,
  // which opens so it stays in view.
  update(session, now = Date.now(), selected = this.#selected) {
    this.#selected = selected;
    const d = delegateList(session);
    const e = this.els;
    this.element.hidden = !d;
    const live = new Set();
    keepFocus(this.element, () => {
      if (d) {
        const finished = new Set(d.finished.map((r) => r.id));
        if (selected && finished.has(selected) && !this.#finishedIds.has(selected)) { e.finished.open = true; }
        this.#finishedIds = finished;
        const make = (id) => new DelegateRow(id, (x) => this.onOpen(x));
        place(d.active, e.active, this.#rows, live, make, now, selected);
        place(d.finished, e.finishedList, this.#rows, live, make, now, selected);
        e.active.hidden = d.active.length === 0;
        e.finished.hidden = d.finished.length === 0;
        e.finishedCount.textContent = `${d.finished.length} finished`;
        e.more.hidden = !d.more;
        e.more.textContent = d.more ? `${plural(d.more, 'more delegate', 'more delegates')} not listed` : '';
      }
      for (const id of [...this.#rows.keys()]) { if (!live.has(id)) { this.#rows.get(id).element.remove(); this.#rows.delete(id); } }
    });
  }

  row(id) { return this.#rows.get(id)?.els.open ?? null; }

  refreshTimes(now = Date.now()) {
    for (const row of this.#rows.values()) { row.refreshTime(now); }
  }

  dispose() { this.#rows.clear(); }
}

// The strip (and a phone's chip row): one item per delegate the main agent started, then "N
// finished", and the finished ones while it is open. The item open in the inspector is pressed.
export class DelegateStrip {
  #items = new Map();
  #open = false;
  #session = null;
  #selected = null;
  // The finished items as last drawn, so the fold opens as the one open finishes, once.
  #finishedIds = new Set();

  // onSelect(id): an item pressed: its delegate, or null to let the one open go.
  constructor({ onSelect = () => {} } = {}) {
    this.onSelect = onSelect;
    this.fold = h('button', { type: 'button', class: 'strip-item strip-fold', 'aria-expanded': 'false', onclick: () => { this.#open = !this.#open; this.update(this.#session); } },
      glyph('done', { size: 14 }), h('span', { class: 'strip-name' }));
    this.more = h('span', { class: 'strip-more' });
    this.element = h('div', { class: 'delegate-strip', role: 'group', 'aria-label': 'Delegates', hidden: true });
  }

  update(session, now = Date.now(), selected = this.#selected) {
    const was = this.#selected;
    this.#session = session;
    this.#selected = selected;
    const d = delegateList(session);
    const list = session?.subagents ?? [];
    // The delegates the main agent started: no parent, or one the list doesn't have. Not depth 0,
    // which is a row's place in its own group: a child that finished while its parent works is
    // depth 0 among the finished, and would pass for one of the main agent's own here.
    const ids = new Set(list.map((a) => a.id));
    const top = (rows) => rows.filter((r) => !r.parent || r.parent === r.id || !ids.has(r.parent));
    const active = d ? top(d.active) : [];
    const finished = d ? top(d.finished) : [];
    this.element.hidden = !d || (active.length === 0 && finished.length === 0 && !d.more);
    if (!d) { return; }
    // An open delegate stays in view as it finishes, and a finished one chosen is shown: the fold
    // opens then, once, so it can still be shut over it.
    const finishedNow = new Set(finished.map((r) => r.id));
    if (selected && finishedNow.has(selected) && (selected !== was || !this.#finishedIds.has(selected))) { this.#open = true; }
    this.#finishedIds = finishedNow;
    const live = new Set();
    const els = active.map((r) => this.#item(r, list, now, selected, live));
    if (finished.length) {
      this.fold.querySelector('.strip-name').textContent = `${finished.length} finished`;
      this.fold.setAttribute('aria-expanded', this.#open ? 'true' : 'false');
      els.push(this.fold);
      if (this.#open) { els.push(...finished.map((r) => this.#item(r, list, now, selected, live))); }
    }
    // What the host left out of the list (its limit or the session budget) is still said here,
    // where at three panes the Delegates list is not shown.
    if (d.more) {
      this.more.textContent = `${plural(d.more, 'more delegate', 'more delegates')} not listed`;
      els.push(this.more);
    }
    keepFocus(this.element, () => {
      els.forEach((el, i) => { if (this.element.children[i] !== el) { this.element.insertBefore(el, this.element.children[i] ?? null); } });
      while (this.element.children.length > els.length) { this.element.lastElementChild.remove(); }
    });
    for (const id of [...this.#items.keys()]) { if (!live.has(id)) { this.#items.delete(id); } }
  }

  #item(r, list, now, selected, live) {
    live.add(r.id);
    let it = this.#items.get(r.id);
    if (!it) {
      const g = glyph('idle', { size: 14 });
      const name = h('span', { class: 'strip-name' });
      const facts = h('span', { class: 'strip-facts' });
      const el = h('button', { type: 'button', class: 'strip-item', dataset: { id: r.id }, 'aria-pressed': 'false', onclick: () => this.onSelect(this.#selected === r.id ? null : r.id) }, g, h('span', { class: 'strip-words' }, name, facts));
      it = { el, g, name, facts, row: r };
      this.#items.set(r.id, it);
    }
    it.row = r;
    setGlyph(it.g, r.glyph, { size: 14, label: r.words });
    it.name.textContent = r.name;
    const nested = descendants(r.id, list).length;
    it.facts.textContent = [delegateTime(r, now), nested ? `${nested} nested` : null].filter(Boolean).join(' · ');
    it.el.title = r.name;
    it.el.setAttribute('aria-pressed', r.id === selected ? 'true' : 'false');
    return it.el;
  }

  item(id) { return this.#items.get(id)?.el ?? null; }

  refreshTimes(now = Date.now()) {
    if (this.#session) { this.update(this.#session, now); }
  }
}

// An open delegate that is running is fetched again this often: a host sends no update when only
// its line changes (v1-sessions.md, Subagent), so its words would otherwise stand still.
const POLL_MS = 10000;
// And at most this often when its entry changes.
const CHANGE_MS = 2000;

export class DelegateDetail {
  #id = null;
  #a = null;
  #session = null;
  #state = { loading: false, again: false, timer: null, due: 0, key: null, loaded: false, skipped: false, gen: 0 };
  #fetched = null;
  // The Markdown shown, so a fetch that brings the same words leaves focus and selection be.
  #shown = null;
  // Its task and latest line, from the list or a fetch, whichever came last. A list the session
  // budget trimmed leaves them out; that is not the delegate saying nothing.
  #task = null;
  #line = null;
  #listLine = null;
  // Moves on with each new line from the list, so a fetch sent before it can't put back an older one.
  #lineRev = 0;
  #nested = new Map();
  // Every listed delegate's task and line as last sent, whether open or not, so one opened after
  // the budget trimmed them still has them.
  #known = new Map();

  // fetchDelegate(agentId) -> Promise<Subagent|null>, or null. onOpen(id): a nested delegate
  // pressed. onAsk(text): "Ask main agent to stop it". onBack(): back to the list, where the
  // detail is pushed in its place. canAsk(): whether the session takes replies.
  constructor({ fetchDelegate = null, onOpen = () => {}, onAsk = () => {}, onBack = () => {}, canAsk = () => false, idBase = 'delegate' } = {}) {
    Object.assign(this, { fetchDelegate, onOpen, onAsk, onBack, canAsk });
    const e = this.els = {};
    e.back = h('button', { type: 'button', class: 'button secondary small delegate-back', onclick: () => this.onBack() }, 'All delegates');
    e.glyph = glyph('idle', { size: 16 });
    e.name = h('span', { class: 'delegate-title-name' });
    // A fixed "Delegate" over its name: whatever an agent named it, this pane is a delegate's.
    e.label = h('p', { class: 'section-head delegate-label', id: `${idBase}-label` }, 'Delegate');
    e.title = h('h3', { class: 'delegate-title', id: `${idBase}-title`, tabindex: '-1', 'aria-describedby': `${idBase}-label` }, e.glyph, e.name);
    e.props = h('dl', { class: 'facts delegate-props' });
    e.status = h('p', { class: 'note', role: 'status', hidden: true });
    e.task = h('p', { class: 'plain' });
    e.taskBlock = h('div', { class: 'delegate-task', hidden: true }, h('h4', { class: 'section-head' }, 'Task'), e.task);
    e.truncated = h('p', { class: 'note truncated', hidden: true }, 'Earlier text not shown: the response was longer than the machine sends.');
    e.latest = h('div', { class: 'md' });
    e.latestBlock = h('div', { class: 'delegate-latest', hidden: true }, h('h4', { class: 'section-head' }, 'Latest'), e.truncated, e.latest);
    e.reasoningText = h('p', { class: 'plain' });
    e.reasoning = h('details', { class: 'reasoning', hidden: true }, h('summary', null, 'Reasoning'), e.reasoningText);
    e.tools = h('ol', { class: 'tools' });
    e.toolsBlock = h('div', { class: 'delegate-tools', hidden: true }, h('h4', { class: 'section-head' }, 'Tools'), e.tools);
    e.nested = h('ul', { class: 'delegate-list' });
    e.nestedBlock = h('div', { class: 'delegate-nested', hidden: true }, h('h4', { class: 'section-head' }, 'Delegates it started'), e.nested);
    e.ask = h('button', { type: 'button', class: 'button secondary delegate-ask', hidden: true, onclick: () => { if (this.#a) { this.onAsk(askToStopText(this.#a)); } } }, 'Ask main agent to stop it');
    this.element = h('section', { class: 'delegate-pane', 'aria-labelledby': `${idBase}-label ${idBase}-title`, hidden: true },
      e.back, e.label, e.title, e.props, e.status, e.taskBlock, e.latestBlock, e.reasoning, e.toolsBlock, e.nestedBlock, e.ask);
  }

  get id() { return this.#id; }

  // A delegate opened, or null to close it. Its words are fetched once it is on screen.
  select(id, session = this.#session) {
    if (id === this.#id) { return; }
    this.#stop();
    this.#id = id;
    this.#a = null;
    this.#fetched = null;
    this.#shown = null;
    const known = this.#known.get(id);
    this.#task = known?.task ?? null;
    this.#line = known?.line ?? null;
    this.#listLine = null;
    this.#state = { loading: false, again: false, timer: null, due: 0, key: null, loaded: false, skipped: false, gen: this.#state.gen + 1 };
    this.els.reasoning.open = false;
    this.element.hidden = !id;
    if (!id) { return; }
    if (session) { this.update(session); }
    this.#schedule(0);
  }

  // Its entry in the session's list, as it changes. Returns false when it is no longer listed.
  update(session, now = Date.now()) {
    this.#session = session;
    const list = session?.subagents ?? [];
    this.#remember(list);
    if (!this.#id) { return true; }
    const raw = list.find((a) => a.id === this.#id);
    if (!raw) { return false; }
    const names = new Map(list.map((a) => [a.id, delegateName(a)]));
    const a = { ...raw, name: delegateName(raw), ...delegateState(raw), parentName: raw.parent ? names.get(raw.parent) ?? null : null };
    const first = this.#a === null;
    this.#a = a;
    const e = this.els;
    this.element.dataset.id = a.id;
    this.element.dataset.status = a.status;
    setGlyph(e.glyph, a.glyph, { size: 16, label: a.words });
    e.name.textContent = a.name;
    if (a.line !== null && a.line !== undefined && a.line !== this.#listLine) { this.#line = a.line; this.#lineRev++; }
    this.#listLine = a.line;
    if (a.task) { this.#task = a.task; }
    this.#drawProps(now);
    this.#drawWords();
    this.#drawTools(now);
    this.#drawNested(list, now);
    e.ask.hidden = a.status === 'ended' || !this.canAsk() || !askToStopText(a);
    // Followed as it changes: fetched again (at most every 2 s).
    const key = JSON.stringify([a.status, a.outcome, a.line, a.endedAt]);
    if (!first && key !== this.#state.key) { this.#schedule(CHANGE_MS); }
    this.#state.key = key;
    return true;
  }

  #remember(list) {
    const live = new Set();
    for (const a of list) {
      live.add(a.id);
      const k = this.#known.get(a.id) ?? { task: null, line: null };
      if (a.task) { k.task = a.task; }
      if (a.line !== null && a.line !== undefined) { k.line = a.line; }
      this.#known.set(a.id, k);
    }
    for (const id of [...this.#known.keys()]) { if (!live.has(id)) { this.#known.delete(id); } }
  }

  #drawProps(now) {
    const a = this.#a;
    const time = delegateTime(a, now);
    const rows = [
      ['Status', time ? `${a.words} · ${time}` : a.words],
      ['Model', this.#fetched?.model ?? a.model],
      ['Kind', a.kind === 'background' ? 'Background' : 'Task'],
      ['ID', h('span', { class: 'mono' }, a.id)],
      ['Commands', a.shells ? plural(a.shells, 'background command', 'background commands') : null],
      ['Under', a.parentName],
    ].filter(([, v]) => v);
    const key = JSON.stringify(rows.map(([k, v]) => [k, typeof v === 'string' ? v : v.textContent]));
    if (this.els.props.dataset.key === key) { return; }
    this.els.props.dataset.key = key;
    this.els.props.replaceChildren(...rows.map(([k, v]) => h('div', { class: 'fact', dataset: { fact: k.toLowerCase() } }, h('dt', null, k), h('dd', null, v))));
  }

  #drawWords() {
    const e = this.els;
    e.task.textContent = this.#task ?? '';
    e.taskBlock.hidden = !this.#task;
    const f = this.#fetched;
    const response = f?.response || null;
    const latest = response || this.#line || '';
    if (latest !== this.#shown) {
      this.#shown = latest;
      e.latest.replaceChildren(renderMarkdown(latest));
    }
    e.latestBlock.hidden = !latest;
    e.truncated.hidden = !(f?.responseTruncated && response);
    const reasoning = f?.reasoning ?? '';
    if (e.reasoningText.textContent !== reasoning) { e.reasoningText.textContent = reasoning; }
    e.reasoning.hidden = !reasoning;
  }

  // Its tool calls, newest first: the fetched history, or the one running now. Name and
  // arguments in mono, a glyph per call, and how long each took as a clock.
  #drawTools(now) {
    const e = this.els;
    const calls = this.#fetched?.history?.length ? [...this.#fetched.history].reverse() : this.#a.currentTool ? [this.#a.currentTool] : [];
    const rows = calls.slice(0, 50).map((t) => h('li', { class: `tool ${t.status}`, dataset: { status: t.status } },
      h('span', { class: 'tool-mark' }, glyph(TOOL_GLYPH[t.status] ?? 'idle', { size: 12 })),
      h('span', { class: 'sr-only' }, `${t.status}: `),
      h('span', { class: 'tool-name' }, t.name),
      t.summary ? h('span', { class: 'tool-summary', title: t.summary }, t.summary) : h('span'),
      t.startedAt ? h('time', { class: 'tool-time', datetime: t.startedAt, title: localTime(t.startedAt) }, clock(t.startedAt, t.endedAt, now)) : null));
    const key = JSON.stringify(calls.slice(0, 50).map((t) => [t.id, t.status, t.name, t.summary, t.startedAt, t.endedAt, t.endedAt ? '' : clock(t.startedAt, null, now)]));
    if (e.tools.dataset.key !== key) {
      e.tools.dataset.key = key;
      e.tools.replaceChildren(...rows);
    }
    e.toolsBlock.hidden = rows.length === 0;
  }

  // The delegates it started, at any depth, each indented under its own parent; pressed, opened.
  #drawNested(list, now) {
    const e = this.els;
    const names = new Map(list.map((a) => [a.id, delegateName(a)]));
    const rows = descendants(this.#a.id, list).map(({ agent, depth }) => ({ ...agent, name: delegateName(agent), ...delegateState(agent), depth, parentName: names.get(agent.parent) ?? null }));
    const live = new Set();
    keepFocus(e.nested, () => {
      place(rows, e.nested, this.#nested, live, (id) => new DelegateRow(id, (x) => this.onOpen(x)), now, null);
      for (const id of [...this.#nested.keys()]) { if (!live.has(id)) { this.#nested.get(id).element.remove(); this.#nested.delete(id); } }
    });
    e.nestedBlock.hidden = rows.length === 0;
  }

  refreshTimes(now = Date.now()) {
    if (!this.#a) { return; }
    this.#drawProps(now);
    this.#drawTools(now);
    for (const row of this.#nested.values()) { row.refreshTime(now); }
  }

  // Focus to its name, which a screen reader reads as its heading.
  focus() { this.els.title.focus(); }

  // It came on screen (a tab, a layout or the inspector shown): fetched now if a fetch was due
  // while it was hidden, or none has come back yet.
  shown() {
    if (this.#id && !this.#state.loading && (!this.#state.loaded || this.#state.skipped)) { this.#schedule(0); }
  }

  dispose() {
    this.#stop();
    this.#id = null;
    this.#known.clear();
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
    this.#state.timer = setTimeout(() => this.#tick(), ms);
  }

  // Nothing is fetched for a delegate nobody can see: its session left, its pane hidden, or the
  // app in the background. It is looked at again later, and fetched once it shows.
  #tick() {
    this.#state.timer = null;
    if (!this.#id) { return; }
    if (document.hidden || this.element.getClientRects().length === 0) { this.#state.skipped = true; this.#schedule(POLL_MS); return; }
    this.#state.skipped = false;
    this.#load();
  }

  async #load() {
    const e = this.els;
    if (!this.#id) { return; }
    // One at a time; a fetch asked for meanwhile goes once this one is back.
    if (this.#state.loading) { this.#state.again = true; return; }
    if (!this.fetchDelegate) { return; }
    const gen = this.#state.gen;
    const id = this.#id;
    this.#state.loading = true;
    if (!this.#state.loaded) { this.#note('Loading…'); }
    const rev = this.#lineRev;
    let failed = false;
    try {
      const full = await this.fetchDelegate(id);
      if (gen !== this.#state.gen) { return; }
      if (!full) {
        this.#note('This delegate is no longer listed.');
      } else {
        if (full.line && rev === this.#lineRev) { this.#line = full.line; }
        if (full.task) { this.#task = full.task; }
        this.#fetched = full;
        this.#state.loaded = true;
        this.#note(null);
        this.#drawWords();
        if (this.#a) { this.#drawProps(Date.now()); this.#drawTools(Date.now()); }
      }
    } catch (err) {
      if (gen !== this.#state.gen) { return; }
      // A failed fetch keeps what was fetched before; with nothing fetched yet, its latest line.
      this.#note(`Couldn't load the delegate: ${err.reason ?? err.message}`, true);
      failed = true;
    } finally {
      if (gen === this.#state.gen) { this.#state.loading = false; }
    }
    if (gen !== this.#state.gen || !this.#id) { return; }
    if (this.#state.again) {
      this.#state.again = false;
      this.#load();
    } else if (failed || (this.#a && (this.#a.status === 'working' || this.#a.status === 'waiting'))) {
      // A failed fetch (a reconnect, say) is tried again, finished delegate or not.
      this.#schedule(POLL_MS);
    }
  }

  #note(text, warn = false) {
    const e = this.els;
    e.status.hidden = !text;
    e.status.className = warn ? 'note warn' : 'note';
    e.status.textContent = text ?? '';
  }
}
