// One session, twice: its row in the sessions list, and its view in the session pane
// (docs/design/ui.md §1 principle 4, §6.1, §6.2, §6.4, §6.5; #179). Nothing expands in place:
// the row is two lines, and choosing it shows the view beside the list on a desktop, or pushes
// it on a phone.
//
// The row: the status glyph (aligned to line 1), the title, and at most two facts: the status
// words (or "Driven by an agent"), then the machine with several shown, or else the repository.
// The age trails it, or the new-activity dot does. The model, SHA, counts and pid are never on
// the row: they are in Details.
//
// The view: a header (title; one meta line of glyph · status · machine · repository; Pin,
// Details, and the inspector's toggle, or on a phone Back and an overflow menu), then the
// conversation (your last message as a bubble, the main agent's label and reply with its
// reasoning behind "Show reasoning", the question as a Needs you card) over the reply box; and
// the inspector, with Details (everything the row leaves out, and End session) and Tools. The
// inspector is a pane of its own at the three-pane width; below it, Tools is a tab beside
// Conversation and Details is the header's button. Delegates (U8, #176) join both later.
//
// Status is a glyph and the words of ui.md §2.2 (js/status.js): no coloured edge or glow (#178).
import { h } from './dom.js';
import { designIcon, glyph, setGlyph } from './glyph.js';
import { ago, duration, localTime } from './time.js';
import { renderMarkdown } from '../markdown.js';
import { sessionStatus, statusLabel } from '../status.js';
import { QuestionPanel, describeAnswer, renderQuestion } from './question-view.js';
import { answerability } from '../answers.js';
import { ReplyBox } from './reply-view.js';
import { EndSession } from './stop-view.js';
import { factsOf, lastSegment, pathView, setFacts } from './meta.js';
import { RowMenu } from './row-menu.js';

const AGENT_NAME = { copilot: 'Copilot', claude: 'Claude', codex: 'Codex', unknown: 'Agent' };
// A tool call's glyph: running turns, as working does; a finished one is done or failed.
const TOOL_GLYPH = { running: 'working', done: 'done', failed: 'failed' };
export const TABS = Object.freeze(['conversation', 'tools']);

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
  #selected = false;
  #pinned = false;
  #tab = 'conversation';
  #menu = new RowMenu();

  // hostId: the machine the session is on, which everything the view sends goes to.
  // fetchDetail(id) -> Promise<Session|null>, for the tool history (session.get).
  // answering() -> { service, replies, stops, hostId, capabilities } | null: who answers
  // questions, sends replies and ends sessions, and whether the session's machine takes each at
  // all. labelled: whether the row names its machine, as it does with every machine shown.
  // onSelect(card): its row was chosen. onPin(card, pinned), onBack(card): the header's Pin and
  // (on a phone) Back. layout() -> { inspector }: whether the inspector is a pane of its own now.
  constructor(session, { hostId = null, fetchDetail, machineName, labelled = false, answering = () => null, onSelect = () => {}, onPin = () => {}, onBack = () => {}, layout = () => ({ inspector: false }), viewing = () => true }) {
    Object.assign(this, { hostId, fetchDetail, machineName, labelled, answering, onSelect, onPin, onBack, layout, viewing });
    const n = ++seq;
    this.ids = { row: `s${n}-row`, glyph: `s${n}-glyph`, unread: `s${n}-unread`, title: `s${n}-title`, conversation: `s${n}-conversation`, tools: `s${n}-tools`, details: `s${n}-details`, tabC: `s${n}-tab-c`, tabT: `s${n}-tab-t` };
    this.#buildRow();
    this.#buildView();
    this.update(session);
  }

  get id() { return this.#s.id; }
  get session() { return this.#s; }
  get selected() { return this.#selected; }
  get tab() { return this.#tab; }

  #buildRow() {
    const e = this.els = {};
    e.rowGlyph = glyph('idle', { size: 16 });
    e.rowGlyph.id = this.ids.glyph;
    e.rowTitle = h('span', { class: 'row-title' });
    e.rowLine1 = h('span', { class: 'row-line1' }, e.rowTitle);
    e.rowFacts = h('span', { class: 'row-facts' });
    e.rowAge = h('time', { class: 'row-age' });
    // The row reads as its glyph's name: "title, status, machine" (ui.md §9).
    e.open = h('button', { type: 'button', class: 'row-main', id: this.ids.row, 'aria-labelledby': this.ids.glyph, onclick: () => this.onSelect(this) },
      h('span', { class: 'row-glyph' }, e.rowGlyph),
      h('span', { class: 'row-text' }, e.rowLine1, e.rowFacts),
      e.rowAge);
    this.element = h('div', { class: 'session' }, e.open);
  }

  #buildView() {
    const e = this.els;
    // The header (ui.md §6.2).
    e.back = h('button', { type: 'button', class: 'icon-button back', 'aria-label': 'Sessions', title: 'Sessions', onclick: () => this.onBack(this) }, designIcon('back', { cls: 'ab-icon-20' }));
    e.titleText = h('h2', { class: 'session-title', id: this.ids.title });
    e.glyph = glyph('idle', { size: 16 });
    e.statusText = h('span', { class: 'status-text' });
    e.meta = h('span', { class: 'meta-facts' });
    e.statusLine = h('p', { class: 'status-line' }, e.glyph, e.meta);
    e.pin = h('button', { type: 'button', class: 'icon-button pin', 'aria-pressed': 'false', 'aria-label': 'Pin', title: 'Pin', onclick: () => this.onPin(this, !this.#pinned) }, designIcon('pin', { cls: 'ab-icon-20' }));
    e.detailsButton = h('button', { type: 'button', class: 'icon-button details-button', 'aria-pressed': 'false', 'aria-controls': this.ids.details, 'aria-label': 'Details', title: 'Details', onclick: () => this.showDetails() }, designIcon('inspector', { cls: 'ab-icon-20' }));
    e.inspectorButton = h('button', { type: 'button', class: 'icon-button inspector-button', 'aria-label': 'Hide inspector', title: 'Hide inspector', onclick: () => this.onInspector?.() }, designIcon('sidebar', { cls: 'ab-icon-20' }));
    e.more = h('button', { type: 'button', class: 'icon-button more', 'aria-haspopup': 'menu', 'aria-label': 'More', title: 'More', onclick: () => this.#openMore() }, designIcon('more', { cls: 'ab-icon-20' }));
    e.actions = h('div', { class: 'header-actions' }, e.pin, e.detailsButton, e.inspectorButton, e.more);
    e.header = h('header', { class: 'session-header' }, e.back, h('div', { class: 'header-main' }, e.titleText, e.statusLine), e.actions);

    // Conversation / Tools, where the inspector isn't a pane of its own.
    e.tabC = h('button', { type: 'button', role: 'tab', class: 'view-tab', id: this.ids.tabC, 'aria-controls': this.ids.conversation, onclick: () => this.setTab('conversation') }, 'Conversation');
    e.tabT = h('button', { type: 'button', role: 'tab', class: 'view-tab', id: this.ids.tabT, 'aria-controls': this.ids.tools, onclick: () => this.setTab('tools') }, 'Tools');
    e.tabs = h('div', { class: 'view-tabs', role: 'tablist', 'aria-label': 'Session' }, e.tabC, e.tabT);
    e.tabs.addEventListener('keydown', (ev) => {
      if (ev.key !== 'ArrowLeft' && ev.key !== 'ArrowRight') { return; }
      ev.preventDefault();
      const next = this.#tab === 'tools' ? 'conversation' : 'tools';
      this.setTab(next);
      (next === 'tools' ? e.tabT : e.tabC).focus();
    });

    // The conversation (ui.md §6.4): one voice per line, the main agent's.
    e.userText = h('p', { class: 'plain' });
    e.user = h('div', { class: 'user-message', hidden: true }, h('span', { class: 'sr-only' }, 'Your last message: '), h('div', { class: 'bubble' }, e.userText));
    e.agentAge = h('time', { class: 'speaker-age' });
    e.speaker = h('p', { class: 'speaker' }, h('span', { class: 'speaker-name' }, 'Main agent'), e.agentAge);
    e.reasoningText = h('p', { class: 'plain' });
    e.reasoning = h('details', { class: 'reasoning', hidden: true }, h('summary', null, 'Show reasoning'), e.reasoningText);
    e.reasoning.addEventListener('toggle', () => { e.reasoning.querySelector('summary').textContent = e.reasoning.open ? 'Hide reasoning' : 'Show reasoning'; });
    e.truncated = h('p', { class: 'note truncated', hidden: true }, 'Earlier text not shown: the response was longer than the machine sends.');
    e.response = h('div', { class: 'md response' });
    e.toolName = h('span', { class: 'tool-name' });
    e.toolSummary = h('span', { class: 'tool-summary' });
    e.toolGlyph = glyph('working', { size: 12 });
    e.tool = h('p', { class: 'tool-line', hidden: true }, e.toolGlyph, e.toolName, e.toolSummary);
    e.agentBlock = h('div', { class: 'agent-block' }, e.speaker, e.reasoning, e.truncated, e.response, e.tool);
    e.question = h('div', { class: 'question-slot' });
    e.stream = h('div', { class: 'stream' }, e.user, e.agentBlock, e.question);
    e.reply = h('div', { class: 'reply-slot' });
    e.conversation = h('div', { class: 'conversation', id: this.ids.conversation, role: 'tabpanel', 'aria-labelledby': this.ids.tabC },
      h('div', { class: 'stream-scroll' }, e.stream), e.reply);

    // The inspector: Details, then Tools.
    e.facts = h('dl', { class: 'facts' });
    // End session lives in Details (ui.md §6.2), last and furthest from Send.
    e.stop = h('div', { class: 'stop-slot' });
    e.details = h('section', { class: 'details', id: this.ids.details, 'aria-labelledby': `${this.ids.details}-h` },
      h('h3', { class: 'section-head', id: `${this.ids.details}-h` }, 'Details'), e.facts, e.stop);
    e.historyBody = h('div', { class: 'history-body' });
    e.tools = h('section', { class: 'tools-panel', id: this.ids.tools, role: 'tabpanel', 'aria-labelledby': this.ids.tabT },
      h('h3', { class: 'section-head' }, 'Tools'), e.historyBody);
    e.inspector = h('aside', { class: 'inspector', 'aria-label': 'Inspector' }, e.details, e.tools);

    this.view = h('section', { class: 'session-view', 'aria-labelledby': this.ids.title, hidden: true, dataset: { tab: 'conversation' } },
      e.header, e.tabs, h('div', { class: 'view-body' }, e.conversation, e.inspector));
    this.#applyTab();
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
    const state = sessionStatus(s);
    for (const el of [this.element, this.view]) {
      el.dataset.id = s.id;
      if (this.hostId) { el.dataset.hostId = this.hostId; }
      el.dataset.status = s.status;
      el.dataset.agent = s.agent;
      el.dataset.view = state.view;
      el.dataset.rule = String(state.rule);
      // An agent launched it, or answered or replied to it, and nobody has taken it back since.
      el.dataset.driver = state.driven ? 'agent' : 'person';
    }
    const title = s.title || 'Untitled session';
    const where = s.repository ?? lastSegment(s.cwd);
    const whereView = () => (s.repository ? h('span', { class: 'repo' }, s.repository) : where ? h('span', { class: 'repo mono' }, where) : null);

    // The row.
    e.rowTitle.textContent = title;
    setGlyph(e.rowGlyph, state.glyph, { size: 16, driven: state.driven, label: statusLabel(state, { title: s.title, machine: this.machineName }) });
    if (this.#changed('row', [state.text, state.tone, state.driven, s.driverPrincipal, where, s.repository, this.labelled ? this.machineName : null])) {
      // Rule 7 on a row: "Driven by an agent" in agent colour, in place of the status words.
      const status = state.driven
        ? h('span', { class: 'row-status driver', dataset: { tone: 'agent' }, title: s.driverPrincipal ?? null }, 'Driven by an agent')
        : h('span', { class: 'row-status', dataset: { tone: state.tone } }, factsOf(state.text)[0]);
      setFacts(e.rowFacts, [status, this.labelled && this.machineName ? h('span', { class: 'machine-label' }, this.machineName) : whereView()]);
    }

    // The header: one meta line, glyph · status · machine · repository.
    e.titleText.textContent = title;
    setGlyph(e.glyph, state.glyph, { size: 16, driven: state.driven });
    if (this.#changed('meta', [state.text, state.tone, state.driven, s.driverPrincipal, this.machineName, where, s.repository])) {
      e.statusText.dataset.tone = state.tone;
      setFacts(e.statusText, factsOf(state.text));
      const driver = state.driven ? h('span', { class: 'driver', title: s.driverPrincipal ?? null }, 'Driven by an agent') : null;
      setFacts(e.meta, [e.statusText, driver, this.machineName ? h('span', { class: 'session-machine' }, this.machineName) : null, whereView()]);
    }
    e.agentAge.dateTime = s.updatedAt;
    e.agentAge.title = localTime(s.updatedAt);
    e.rowAge.dateTime = s.updatedAt;
    e.rowAge.title = localTime(s.updatedAt);
    this.refreshTimes();

    const t = s.activity.currentTool;
    e.tool.hidden = !t;
    if (t) {
      e.tool.dataset.status = t.status;
      // Named for its state: the words beside it are the tool's name and summary (ui.md §9).
      setGlyph(e.toolGlyph, TOOL_GLYPH[t.status], { size: 12, label: t.status });
      e.toolName.textContent = t.name;
      e.toolSummary.textContent = t.summary ?? '';
      e.toolSummary.title = t.summary ?? '';
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
    e.agentBlock.hidden = !s.activity.response && !s.activity.reasoning && !t;
    e.user.hidden = !s.activity.userMessage;
    e.userText.textContent = s.activity.userMessage ?? '';

    if (this.#changed('facts', [s.agent, s.model, s.repository, s.branch, s.cwd, this.machineName, s.startedAt, s.activity.turnCount, s.activity.compactions, s.activity.background, s.processId])) {
      this.#renderFacts(s);
    }

    // The tools shown follow the session: refetched (at most every 2 s) as it changes.
    if (!first && this.#history.loaded && this.#toolsShown() && this.#changed('historyAt', s.updatedAt)) { this.#scheduleHistory(); }
  }

  // Details (ui.md §6.2): everything the row leaves out, as a property list.
  #renderFacts(s) {
    const e = this.els;
    const rows = [];
    const add = (term, value, cls) => {
      if (value === null || value === undefined || value === '') { return; }
      rows.push(h('div', { class: `fact${cls ? ` fact-${cls}` : ''}` }, h('dt', null, term), h('dd', null, value)));
    };
    const b = s.activity.background;
    add('Agent', h('span', { class: 'agent-name' }, AGENT_NAME[s.agent]), 'agent');
    add('Machine', this.machineName || null, 'machine');
    add('Repository', s.repository, 'repo');
    add('Branch', s.branch ? h('span', { class: 'branch' }, s.branch) : null, 'branch');
    add('Folder', s.cwd ? pathView(s.cwd) : null, 'folder');
    add('Model', s.model, 'model');
    add('Started', s.startedAt ? h('time', { datetime: s.startedAt, title: localTime(s.startedAt) }, localTime(s.startedAt)) : null, 'started');
    e.since = h('span', { class: 'since' });
    add('In this state', e.since, 'since');
    add('Background', [b?.agents ? plural(b.agents, 'background agent', 'background agents') : null, b?.shells ? plural(b.shells, 'background command', 'background commands') : null].filter(Boolean).join(' \u00b7 ') || null, 'background');
    add('Turns', s.activity.turnCount !== null && s.activity.turnCount !== undefined ? `${s.activity.turnCount} turns` : null, 'turns');
    add('Compactions', s.activity.compactions ? plural(s.activity.compactions, 'compaction', 'compactions') : null, 'compactions');
    add('Process', s.processId !== null && s.processId !== undefined ? `pid ${s.processId}` : null, 'pid');
    e.facts.replaceChildren(...rows);
    this.refreshTimes();
  }

  // What the session's machine is called, and whether its row says so. What is sent from the
  // view from now on says it too.
  setMachine(name, labelled) {
    if (name === this.machineName && labelled === this.labelled) { return; }
    this.machineName = name;
    this.labelled = labelled;
    this.#last = {};
    if (this.#s) { this.update(this.#s); }
  }

  // Activity since the session was last on screen (session-order.js): semibold, and the 6 px
  // Bridge dot at the row's trailing edge in place of its age (ui.md §6.1), named for a screen
  // reader.
  setUnread(unread) {
    const e = this.els;
    if (unread && !e.unread) {
      e.unread = h('span', { class: 'unread-dot' }, h('span', { class: 'sr-only', id: this.ids.unread }, 'New activity'));
      e.rowLine1.append(e.unread);
      e.open.setAttribute('aria-labelledby', `${this.ids.glyph} ${this.ids.unread}`);
    } else if (!unread && e.unread) {
      e.unread.remove();
      e.unread = null;
      e.open.setAttribute('aria-labelledby', this.ids.glyph);
    }
    this.element.classList.toggle('unread', unread);
  }

  setPinned(pinned) {
    this.#pinned = pinned;
    if (pinned) { this.element.dataset.pinned = 'true'; } else { delete this.element.dataset.pinned; }
    const e = this.els;
    e.pin.setAttribute('aria-pressed', pinned ? 'true' : 'false');
    e.pin.setAttribute('aria-label', pinned ? 'Unpin' : 'Pin');
    e.pin.title = pinned ? 'Unpin' : 'Pin';
  }

  // The row chosen, and its view the one shown.
  setSelected(selected) {
    if (selected === this.#selected) { return; }
    this.#selected = selected;
    this.element.classList.toggle('selected', selected);
    if (selected) { this.els.open.setAttribute('aria-current', 'true'); } else { this.els.open.removeAttribute('aria-current'); }
    this.view.hidden = !selected;
    if (!selected) { this.#menu.close(false); }
    this.layoutChanged();
  }

  // The inspector became a pane of its own, or stopped being one.
  layoutChanged() {
    const { inspector } = this.layout();
    this.els.inspectorButton.setAttribute('aria-label', inspector ? 'Hide inspector' : 'Show inspector');
    this.els.inspectorButton.title = this.els.inspectorButton.getAttribute('aria-label');
    this.#revealTools();
  }

  // Conversation or Tools, where they are tabs; Details, from the header's button.
  setTab(tab) {
    this.#tab = tab === 'tools' || tab === 'details' ? tab : 'conversation';
    this.#applyTab();
    this.#revealTools();
  }

  #applyTab() {
    const e = this.els;
    this.view.dataset.tab = this.#tab;
    for (const [tab, el] of [['conversation', e.tabC], ['tools', e.tabT]]) {
      const on = this.#tab === tab;
      el.setAttribute('aria-selected', on ? 'true' : 'false');
      el.tabIndex = on || (this.#tab === 'details' && tab === 'conversation') ? 0 : -1;
    }
    e.detailsButton.setAttribute('aria-pressed', this.#tab === 'details' ? 'true' : 'false');
  }

  // Details: in the inspector, where it is a pane (shown, if it was hidden); otherwise in place
  // of the conversation, and the button again goes back.
  showDetails() {
    if (this.layout().inspector) {
      this.els.details.scrollIntoView({ block: 'nearest' });
      this.els.details.querySelector('.section-head').focus?.();
      return;
    }
    this.setTab(this.#tab === 'details' ? 'conversation' : 'details');
  }

  // Tools shown: fetched the first time, and again when the session changed while they were
  // hidden (updates refetch only what is on screen).
  #revealTools() {
    if (!this.#toolsShown()) { return; }
    if (!this.#history.loaded || (this.#history.at !== this.#s.updatedAt && !this.#history.timer)) { this.#loadHistory(); }
  }

  #toolsShown() {
    // On screen only while Sessions shows (and, on a phone, the session is pushed).
    return this.#selected && this.viewing() && (this.layout().inspector || this.#tab === 'tools');
  }

  // A phone's overflow menu: Pin, Details and End session (ui.md §6.2).
  #openMore() {
    const end = this.#stop && !this.#stop.element.querySelector('.end-button')?.disabled ? this.#stop : null;
    this.#menu.show(this.els.header, [
      { label: this.#pinned ? 'Unpin' : 'Pin', run: () => this.onPin(this, !this.#pinned) },
      { label: 'Details', run: () => { this.setTab('details'); this.els.details.querySelector('.section-head').focus?.(); } },
      ...(end ? [{ label: 'End session', run: () => { this.setTab('details'); end.element.querySelector('.end-button').click(); } }] : []),
    ], { back: this.els.more, label: `${this.#s.title || 'Session'}: more` });
  }

  // The session is going: it ended, or its machine is no longer shown.
  dispose() {
    this.#panel?.destroy();
    this.#panel = null;
    this.#reply?.destroy();
    this.#reply = null;
    this.#stop?.destroy();
    this.#stop = null;
    this.#menu.close(false);
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
    // Who asks (ui.md §6.4): the main agent, or the delegate named.
    const asker = q ? (q.agent ? `${s.subagents?.find((a) => a.id === q.agent)?.name ?? 'A delegate'} asks` : 'Main agent asks') : null;
    if (!this.#changed('question', [q, answerable, answerable ? ctx.hostId : null, able?.why, ctx?.readOnly ?? null, asker])) { return; }
    const old = this.#panel;
    const sameQuestion = !!(old && q && old.questionId === q.id && answerable && old.answering.hostId === ctx.hostId);
    if (old && !sameQuestion) {
      // The question changed or went while it was being answered. Text the person typed
      // and hasn't sent is kept in the view, not dropped with the form; an answer that went
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
        asker,
      });
      view = this.#panel.element;
    } else if (q) {
      view = renderQuestion(q, { machineName: this.machineName, why: able.why, readOnly: ctx?.readOnly ?? null, asker });
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
    const e = this.els;
    // Idle needs no clock; delegating does, since it is waiting on something.
    const since = sessionStatus(s).view === 'idle' ? '' : duration(s.statusSince, now);
    if (e.since) {
      e.since.textContent = since ? `for ${since}` : '';
      e.since.closest('.fact')?.toggleAttribute('hidden', !since);
    }
    const u = ago(s.updatedAt, now);
    e.rowAge.textContent = u;
    e.agentAge.textContent = u;
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
    const at = this.#s.updatedAt;
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
      this.#history.at = at;
    } catch (e) {
      body.replaceChildren(h('p', { class: 'note warn' }, `Couldn't load the history: ${e.message}`));
    } finally {
      this.#history.loading = false;
    }
  }
}

function toolRow(t) {
  return h('li', { class: `tool ${t.status}`, dataset: { status: t.status } },
    h('span', { class: 'tool-mark' }, glyph(TOOL_GLYPH[t.status], { size: 12 })),
    h('span', { class: 'sr-only' }, `${t.status}: `),
    h('span', { class: 'tool-name' }, t.name),
    t.summary ? h('span', { class: 'tool-summary', title: t.summary }, t.summary) : null,
    t.startedAt ? h('time', { class: 'tool-time', datetime: t.startedAt, title: localTime(t.startedAt) }, ago(t.startedAt)) : null);
}

