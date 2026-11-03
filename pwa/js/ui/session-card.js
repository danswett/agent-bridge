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
// Conversation and Details is the header's button.
//
// Delegates (ui.md §6.3, #176): at three panes a strip under the header, the delegate chosen
// there shown in the inspector in place of Details and Tools; below that a Delegates tab, whose
// list pushes the delegate chosen in its place; on a phone a chip row as well. The conversation
// says when the main agent's own delegates start and report back, each name opening the delegate.
// Nothing types to a delegate: its one action fills the reply box to the main agent.
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
import { lastSegment, pathView, setFacts } from './meta.js';
import { RowMenu } from './row-menu.js';
import { DelegateDetail, DelegatesBlock, DelegateStrip } from './delegates-view.js';
import { backgroundCounts, delegateEvents, mainVoice } from '../attribution.js';
import { delegateOf } from '../needs-you.js';
import { enter, leave, scrollBehavior, slide, stopTravel } from './motion.js';

const AGENT_NAME = { copilot: 'Copilot', claude: 'Claude', codex: 'Codex', unknown: 'Agent' };
// A tool call's glyph: running turns, as working does; a finished one is done or failed.
const TOOL_GLYPH = { running: 'working', done: 'done', failed: 'failed' };
// How long a question's controls ignore a pointer's click once the question appears or is
// scrolled into view (ui.md §5.3, Scroll to a question). A click is often already on its way
// when the content under it changes: people take about a quarter of a second to see a change
// and more to stop a click, so twice that is kept. One click on a choice sends the answer, and
// a click aimed at the agent's last lines must not answer a question that moved under it. No
// motion token measures this, and reduced motion keeps it: a jump moves the content as surely.
// A key still acts at once, since nothing moves the focus to a question.
export const QUESTION_ARRIVAL_GUARD_MS = 500;
// A smooth scroll steps every frame (16 ms at 60 Hz, 33 ms on a phone saving power): a pause of
// several frames is a scroll come to rest. Short, so a scroll the person starts just after it
// isn't taken for part of it.
const SCROLL_SETTLED_MS = 100;
export const TABS = Object.freeze(['conversation', 'delegates', 'tools']);

const plural = (n, one, many) => `${n} ${n === 1 ? one : many}`;

// Why nothing can be given for a session now, for under its controls, or null (#269): its
// machine is offline, reconnecting, back and its whole list not yet in, or no longer offering
// sessions. what: 'answered', 'replied to'...
export function staleNote(stale, machineName, what) {
  if (!stale) { return null; }
  const name = machineName || 'The machine';
  if (stale.reason === 'offline') { return `${name} is offline. This can't be ${what} from here until it is back.`; }
  if (stale.reason === 'unsupported') { return `${name} doesn't support Agent Bridge sessions now, so this can't be ${what} from here.`; }
  const where = stale.reason === 'confirming' ? 'is back' : 'is reconnecting';
  return `${name} ${where}. This can be ${what} once it has sent its sessions again.`;
}

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
  // Why its machine's sessions can't be acted on now ({ reason, since }), or null (#269); and
  // the words that said so when last drawn, which move on with the clock.
  #stale = null;
  #shownStale = null;
  // The question last drawn (undefined before the first drawing), and whether a finger or a
  // mouse button is on the conversation: what decides a scroll to a question that arrives.
  #asked = undefined;
  #touches = false;
  #pressing = false;
  // A mouse or pen over the conversation: someone reading it, perhaps about to click.
  #hovering = false;
  // Whether the conversation is read to its end (ui.md §5.3, Scroll to a question), and where it
  // was scrolled to when that was measured: kept while only its content changes, since a response
  // growing below isn't followed and so doesn't take the reader from its end; measured again once
  // it has scrolled since (read from scrollTop, before its scroll event has come). Forgotten (null)
  // while it isn't on screen, and when it may come back, where the browser can have moved it.
  #readToEnd = null;
  #readAt = 0;
  // Until when the question's controls ignore a pointer's click (QUESTION_ARRIVAL_GUARD_MS).
  #guardUntil = 0;
  // The question's view the guard covers, and the question it was armed for.
  #guarded = null;
  #guardedId = null;
  // How the guarded question was last drawn: held (a #269 note, or null) and answerable here.
  #guardedHeld = null;
  #guardedAnswerable = false;
  #guardTimer = null;
  // Listening for the end of a smooth scroll to the question, until it ends or is replaced.
  #scrollEnd = null;
  // A question that arrived in the drawing under way, to bring into sight once it is all drawn.
  #arrived = null;
  #menu = new RowMenu();
  // The delegate open, in the inspector or pushed in the Delegates tab.
  #delegate = null;
  // Whether the inspector was a pane of its own at the last layout.
  #wasInspector = false;

  // hostId: the machine the session is on, which everything the view sends goes to.
  // fetchDetail(id) -> Promise<Session|null>, for the tool history (session.get).
  // answering() -> { service, replies, stops, hostId, capabilities } | null: who answers
  // questions, sends replies and ends sessions, and whether the session's machine takes each at
  // all. labelled: whether the row names its machine, as it does with every machine shown.
  // onSelect(card): its row was chosen. onPin(card, pinned), onBack(card): the header's Pin and
  // (on a phone) Back. layout() -> { inspector, wide }: whether the inspector is a pane of its own
  // now, and whether the window is wide enough for one (the strip's width).
  // stale: why its machine's sessions can't be acted on now ({ reason, since }), or null.
  constructor(session, { hostId = null, fetchDetail, fetchDelegate = null, machineName, labelled = false, answering = () => null, onSelect = () => {}, onPin = () => {}, onBack = () => {}, layout = () => ({ inspector: false, wide: false }), viewing = () => true, stale = null }) {
    Object.assign(this, { hostId, fetchDetail, fetchDelegate, machineName, labelled, answering, onSelect, onPin, onBack, layout, viewing });
    this.#stale = stale;
    const n = ++seq;
    this.ids = { row: `s${n}-row`, glyph: `s${n}-glyph`, unread: `s${n}-unread`, title: `s${n}-title`, conversation: `s${n}-conversation`, tools: `s${n}-tools`, details: `s${n}-details`, tabC: `s${n}-tab-c`, tabD: `s${n}-tab-d`, tabT: `s${n}-tab-t`, delegates: `s${n}-delegates`, delegate: `s${n}-delegate` };
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
    // The delegate strip (§6.3): at three panes under the header; on a phone a chip row.
    // Its item pressed again lets the delegate go, unless the inspector is hidden: then it shows it.
    this.strip = new DelegateStrip({ onSelect: (id) => (id === null && !this.layout().inspector && this.layout().wide ? this.openDelegate(this.#delegate) : id === null ? this.closeDelegate() : this.openDelegate(id)) });

    // Conversation / Delegates / Tools, where the inspector isn't a pane of its own (ui.md §6.3).
    // Delegates is there only while the host lists any.
    e.tabC = h('button', { type: 'button', role: 'tab', class: 'view-tab', id: this.ids.tabC, 'aria-controls': this.ids.conversation, onclick: () => this.setTab('conversation') }, 'Conversation');
    e.tabD = h('button', { type: 'button', role: 'tab', class: 'view-tab', id: this.ids.tabD, 'aria-controls': this.ids.delegates, hidden: true, onclick: () => this.setTab('delegates') }, 'Delegates');
    e.tabT = h('button', { type: 'button', role: 'tab', class: 'view-tab', id: this.ids.tabT, 'aria-controls': this.ids.tools, onclick: () => this.setTab('tools') }, 'Tools');
    e.tabs = h('div', { class: 'view-tabs', role: 'tablist', 'aria-label': 'Session' }, e.tabC, e.tabD, e.tabT);
    e.tabs.addEventListener('keydown', (ev) => {
      if (ev.key !== 'ArrowLeft' && ev.key !== 'ArrowRight') { return; }
      ev.preventDefault();
      const tabs = [['conversation', e.tabC], ['delegates', e.tabD], ['tools', e.tabT]].filter(([, el]) => !el.hidden);
      const i = Math.max(0, tabs.findIndex(([t]) => t === this.#tab));
      const [next, el] = tabs[(i + (ev.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length];
      this.setTab(next);
      el.focus();
    });

    // The conversation (ui.md §6.4): one voice per line, the main agent's.
    e.userText = h('p', { class: 'plain' });
    e.user = h('div', { class: 'user-message', hidden: true }, h('span', { class: 'sr-only' }, 'Your last message: '), h('div', { class: 'bubble' }, e.userText));
    e.agentAge = h('time', { class: 'speaker-age' });
    // "Main agent" where the host says whose words these are (attribution.js); a host that doesn't
    // may have a delegate's here, so they are only the agent's, by its name.
    e.voice = h('span', { class: 'speaker-name voice-label', hidden: true }, 'Main agent');
    e.agentLabel = h('span', { class: 'speaker-name' });
    e.speaker = h('p', { class: 'speaker' }, e.voice, e.agentLabel, e.agentAge);
    e.reasoningText = h('p', { class: 'plain' });
    e.reasoning = h('details', { class: 'reasoning', hidden: true }, h('summary', null, 'Show reasoning'), e.reasoningText);
    e.reasoning.addEventListener('toggle', () => { e.reasoning.querySelector('summary').textContent = e.reasoning.open ? 'Hide reasoning' : 'Show reasoning'; });
    e.truncated = h('p', { class: 'note truncated', hidden: true }, 'Earlier text not shown: the response was longer than the machine sends.');
    e.dropped = h('p', { class: 'note dropped', hidden: true }, 'The main agent\'s latest response was left out: too long to send with its delegates.');
    e.response = h('div', { class: 'md response' });
    e.toolName = h('span', { class: 'tool-name' });
    e.toolSummary = h('span', { class: 'tool-summary' });
    e.toolGlyph = glyph('working', { size: 12 });
    e.tool = h('p', { class: 'tool-line', hidden: true }, e.toolGlyph, e.toolName, e.toolSummary);
    // Its own delegates starting and reporting back (§6.4): one meta line each, the name opening
    // it; after its words and before what it is running now.
    e.events = h('ol', { class: 'delegate-events', 'aria-label': 'Delegates started and finished', hidden: true });
    e.agentBlock = h('div', { class: 'agent-block' }, e.speaker, e.reasoning, e.truncated, e.dropped, e.response, e.events, e.tool);
    e.question = h('div', { class: 'question-slot' });
    this.#guardQuestion(e.question);
    e.stream = h('div', { class: 'stream' }, e.user, e.agentBlock, e.question);
    e.reply = h('div', { class: 'reply-slot' });
    e.scroll = h('div', { class: 'stream-scroll' }, e.stream);
    this.#watchHold(e.scroll);
    e.conversation = h('div', { class: 'conversation', id: this.ids.conversation, role: 'tabpanel', 'aria-labelledby': this.ids.tabC },
      e.scroll, e.reply);

    // The inspector: Details, then Tools.
    e.facts = h('dl', { class: 'facts' });
    // End session lives in Details (ui.md §6.2), last and furthest from Send.
    e.stop = h('div', { class: 'stop-slot' });
    e.details = h('section', { class: 'details', id: this.ids.details, 'aria-labelledby': `${this.ids.details}-h` },
      e.detailsHead = h('h3', { class: 'section-head', id: `${this.ids.details}-h`, tabindex: '-1' }, 'Details'), e.facts, e.stop);
    e.historyBody = h('div', { class: 'history-body' });
    e.tools = h('section', { class: 'tools-panel', id: this.ids.tools, role: 'tabpanel', 'aria-labelledby': this.ids.tabT },
      h('h3', { class: 'section-head' }, 'Tools'), e.historyBody);
    // Delegates (#176, ui.md §6.3): each under its own name, never in the main agent's place. The
    // Delegates tab lists them (or, from a host that lists none, says how many there are); the one
    // opened shows in the inspector, or pushed in the list's place, its own words fetched from the
    // machine (session.get with agent). Nothing here replies to a delegate.
    this.delegates = new DelegatesBlock({ onOpen: (id) => this.openDelegate(id) });
    e.delegateCounts = h('p', { class: 'note delegate-counts', hidden: true });
    e.delegates = h('section', { class: 'delegates-panel', id: this.ids.delegates, role: 'tabpanel', 'aria-labelledby': this.ids.tabD, hidden: true }, this.delegates.element, e.delegateCounts);
    this.detail = new DelegateDetail({
      fetchDelegate: this.fetchDelegate ? (agent) => this.fetchDelegate(this.#s.id, agent) : null,
      onOpen: (id) => this.openDelegate(id),
      onAsk: (text) => this.#askToStop(text),
      onBack: () => this.closeDelegate({ focus: true }),
      canAsk: () => !!this.#reply,
      idBase: this.ids.delegate,
    });
    // The delegate's pane is in the document only while one is open.
    e.inspector = h('aside', { class: 'inspector', 'aria-label': 'Inspector' }, e.details, e.delegates, e.tools);

    this.view = h('section', { class: 'session-view', 'aria-labelledby': this.ids.title, hidden: true, dataset: { tab: 'conversation' } },
      e.header, this.strip.element, e.tabs, h('div', { class: 'view-body' }, e.conversation, e.inspector));
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
    // Whether the conversation is read to its end, before this update draws anything into it.
    const atEnd = this.#reading();
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
    // Its machine away (#269, ui.md §2.2): none of the rules apply, since what the row last said
    // may no longer be so. The failed glyph (a host unreachable), and in secondary text that it
    // is offline and since when, or reconnecting; no driven overlay.
    const stale = this.#staleText();
    this.#shownStale = stale?.join(' ') ?? null;
    for (const el of [this.element, this.view]) {
      if (this.#stale) { el.dataset.stale = this.#stale.reason; } else { delete el.dataset.stale; }
    }
    const away = stale?.join(' \u00b7 ');
    const words = stale ? { ...state, glyph: 'failed', driven: false, facts: stale, text: away, tone: 'muted', spoken: away.charAt(0).toLowerCase() + away.slice(1) } : state;

    // The row.
    e.rowTitle.textContent = title;
    setGlyph(e.rowGlyph, words.glyph, { size: 16, driven: words.driven, label: statusLabel(words, { title: s.title, machine: this.machineName }) });
    if (this.#changed('row', [words.text, words.tone, words.driven, s.driverPrincipal, where, s.repository, this.labelled ? this.machineName : null])) {
      // Rule 7 on a row: "Driven by an agent" in agent colour, in place of the status words.
      const status = words.driven
        ? h('span', { class: 'row-status driver', dataset: { tone: 'agent' }, title: s.driverPrincipal ?? null }, 'Driven by an agent')
        : h('span', { class: 'row-status', dataset: { tone: words.tone } }, words.facts[0]);
      setFacts(e.rowFacts, [status, this.labelled && this.machineName ? h('span', { class: 'machine-label' }, this.machineName) : whereView()]);
    }

    // The header: one meta line, glyph · status · machine · repository.
    e.titleText.textContent = title;
    setGlyph(e.glyph, words.glyph, { size: 16, driven: words.driven });
    if (this.#changed('meta', [words.text, words.tone, words.driven, s.driverPrincipal, this.machineName, where, s.repository])) {
      e.statusText.dataset.tone = words.tone;
      setFacts(e.statusText, words.facts);
      const driver = words.driven ? h('span', { class: 'driver', title: s.driverPrincipal ?? null }, 'Driven by an agent') : null;
      setFacts(e.meta, [e.statusText, driver, this.machineName ? h('span', { class: 'session-machine' }, this.machineName) : null, whereView()]);
    }
    // The main agent's words dated by when it wrote them, not by a delegate's later update.
    const said = mainVoice(s).at ?? s.updatedAt;
    e.agentAge.dateTime = said;
    e.agentAge.title = localTime(said);
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
    // The main agent's own words only (ui.md §1 principle 3); a delegate's are on its own row.
    const voice = mainVoice(s);
    if (this.#changed('response', voice.response ?? '')) {
      e.response.replaceChildren(renderMarkdown(voice.response ?? ''));
    }
    e.response.hidden = !voice.response;
    e.truncated.hidden = !voice.responseTruncated;
    e.dropped.hidden = !voice.responseDropped;
    e.reasoning.hidden = !voice.reasoning;
    e.reasoningText.textContent = voice.reasoning ?? '';
    e.voice.hidden = !voice.attributed;
    e.agentLabel.hidden = voice.attributed;
    e.agentLabel.textContent = AGENT_NAME[s.agent] ?? AGENT_NAME.unknown;
    this.#renderDelegates(s);
    e.agentBlock.hidden = !voice.response && !voice.responseDropped && !voice.reasoning && !t && e.events.hidden;
    const echoed = (s.activity.userMessage ?? '') !== e.userText.textContent;
    e.user.hidden = !s.activity.userMessage;
    e.userText.textContent = s.activity.userMessage ?? '';
    // Send acknowledged (ui.md §5.3): your message, back from the machine, rises into place.
    if (!first && echoed && s.activity.userMessage) { enter(e.user, 'acknowledge', { dy: 1 }); }

    if (this.#changed('facts', [s.agent, s.model, s.repository, s.branch, s.cwd, this.machineName, s.startedAt, s.activity.turnCount, s.activity.compactions, s.activity.background, s.processId])) {
      this.#renderFacts(s);
    }
    this.#bringQuestion(atEnd);

    // The tools shown follow the session: refetched (at most every 2 s) as it changes.
    if (!first && this.#history.loaded && this.#toolsShown() && this.#changed('historyAt', s.updatedAt)) { this.#scheduleHistory(); }
  }

  // The strip, the Delegates tab and the delegate open (§6.3), and the conversation's events.
  #renderDelegates(s, now = Date.now()) {
    const e = this.els;
    // Every update, open or not: the detail remembers what a trimmed list leaves out.
    if (!this.detail.update(s, now) && this.#delegate) { this.closeDelegate(); }
    this.strip.update(s, now, this.#delegate);
    this.delegates.update(s, now, this.#delegate);
    const listed = !this.delegates.element.hidden;
    // A host that lists none (Claude, Codex, one before sub-agents) says only how many (rule 8).
    const counts = !listed && !Array.isArray(s.subagents) ? backgroundCounts(s) : null;
    e.delegateCounts.textContent = counts ?? '';
    e.delegateCounts.hidden = !counts;
    e.delegates.hidden = !listed && !counts;
    e.tabD.hidden = e.delegates.hidden;
    if (e.tabD.hidden && this.#tab === 'delegates') { this.setTab('conversation'); }
    this.#renderEvents(s, now);
  }

  #renderEvents(s, now = Date.now()) {
    const e = this.els;
    const events = delegateEvents(s, now);
    if (!this.#changed('events', events)) { return; }
    // Rebuilt as their ages move on: a name with keyboard focus keeps it, in its new line.
    const focused = e.events.contains(document.activeElement) ? document.activeElement.closest('.delegate-event')?.dataset.key : null;
    e.events.replaceChildren(...events.map((ev) => h('li', { class: 'delegate-event', dataset: { kind: ev.kind, key: ev.key } },
      glyph(ev.glyph ?? 'delegating', { size: 12 }),
      h('span', { class: 'delegate-event-text' },
        ev.before ? `${ev.before}${ev.name ? ' ' : ''}` : '',
        ev.name ? h('button', { type: 'button', class: 'delegate-link', dataset: { id: ev.id }, onclick: () => this.openDelegate(ev.id) }, ev.name) : null,
        ev.after ? ` ${ev.after}` : ''))));
    e.events.hidden = events.length === 0;
    if (focused) { [...e.events.children].find((li) => li.dataset.key === focused)?.querySelector('.delegate-link')?.focus({ preventScroll: true }); }
  }

  // A delegate opened from the strip, the list, a nested list or the conversation: in the
  // inspector at three panes (brought back, if it was hidden), else pushed in the Delegates tab.
  openDelegate(id) {
    if (!id) { this.closeDelegate(); return; }
    if (!(this.#s.subagents ?? []).some((a) => a.id === id)) { return; }
    // Opened from inside the detail (its nested list): the button pressed goes with the detail it
    // was in, so focus follows to the delegate opened, at any width.
    const fromDetail = this.detail.element.contains(document.activeElement);
    this.#delegate = id;
    if (!this.detail.element.isConnected) { this.els.inspector.insertBefore(this.detail.element, this.els.tools); }
    this.detail.select(id, this.#s);
    this.view.dataset.delegate = 'open';
    if (this.layout().wide && !this.layout().inspector) { this.onInspector?.(); }
    const inspector = this.layout().inspector;
    if (!inspector) { this.setTab('delegates'); }
    this.#renderDelegates(this.#s);
    this.detail.shown();
    // Pushed in the list's place, focus goes to it; in the inspector it stays where it was.
    if (!inspector || fromDetail) { this.detail.focus(); }
    this.onDelegate?.();
  }

  // Back to the session's own Details and Tools, or to the Delegates tab's list.
  closeDelegate({ focus = false } = {}) {
    const id = this.#delegate;
    if (!id) { return; }
    this.#delegate = null;
    this.detail.select(null);
    this.detail.element.remove();
    delete this.view.dataset.delegate;
    this.#renderDelegates(this.#s);
    this.#revealTools();
    if (focus) { (this.delegates.row(id) ?? this.els.tabD).focus(); }
    this.onDelegate?.();
  }

  get openDelegateId() { return this.#delegate; }

  // "Ask main agent to stop it": the words go in the reply box, after anything already there, for
  // the person to read and send. Nothing is sent; Send, as every reply, goes to the main agent.
  #askToStop(text) {
    if (!this.#reply) { return; }
    if (!this.layout().inspector) { this.setTab('conversation'); }
    this.#reply.prefill(text);
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

  // Its machine went offline, came back, or sent a whole new list since (#269): what the row and
  // header say. What it offers to send follows answering(), drawn again by refreshQuestion().
  setStale(stale) {
    const same = (a, b) => (a?.reason ?? null) === (b?.reason ?? null) && (a?.since ?? null) === (b?.since ?? null);
    if (same(stale, this.#stale)) { return; }
    this.#stale = stale ?? null;
    if (this.#s) { this.update(this.#s); }
  }

  // ui.md §2.2 (#269), as facts: "Offline · last seen 2m ago" while its machine is away,
  // "Reconnecting…" while it has no open channel and is still tried; null otherwise, including
  // while its snapshot is still coming (its rows say what that snapshot says, and still take
  // nothing) and for a machine that no longer offers sessions (its host line says so).
  #staleText(now = Date.now()) {
    const stale = this.#stale;
    if (!stale || stale.reason === 'confirming' || stale.reason === 'unsupported') { return null; }
    if (stale.reason === 'reconnecting') { return ['Reconnecting…']; }
    const seen = ago(stale.since, now);
    return seen ? ['Offline', `last seen ${seen}`] : ['Offline'];
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
      this.element.classList.add('unread');
      // Activity dot (ui.md §5.3): fades in, and later out; it never pulses or repeats.
      enter(e.unread, 'activityDot');
    } else if (!unread && e.unread) {
      leave(e.unread, 'activityDot');
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
    this.#readToEnd = null;
    this.element.classList.toggle('selected', selected);
    if (selected) { this.els.open.setAttribute('aria-current', 'true'); } else { this.els.open.removeAttribute('aria-current'); }
    this.view.hidden = !selected;
    // Its question shows now, where a tap or a click may already be headed (a phone's push puts
    // the view under the finger that opened it): the same wait as a question arriving.
    if (selected && this.#guarded?.isConnected) { this.#guard(); }
    if (!selected) { this.#menu.close(false); }
    this.layoutChanged();
  }

  // Its view is on screen again with nothing of its own changed (Sessions back from another
  // page, the page back from the background): a question it shows waits as one arriving does,
  // where it is. Whether the conversation shows is read from the page, not from the tab picked:
  // with the inspector beside it, Tools picked earlier leaves the conversation on screen.
  reshow() {
    if (this.#selected && this.#guarded?.isConnected && this.viewing() && this.els.scroll.getClientRects().length) { this.#guard(); }
  }

  // The inspector became a pane of its own, or stopped being one.
  layoutChanged() {
    this.#readToEnd = null;
    const { inspector } = this.layout();
    // A delegate shown in the inspector stays shown as the window narrows: pushed in the Delegates
    // tab. Only then: a tab chosen at a narrower width is left as it was.
    if (this.#delegate && this.#wasInspector && !inspector && !this.layout().wide && this.#tab !== 'delegates') { this.setTab('delegates'); }
    this.#wasInspector = inspector;
    this.els.inspectorButton.setAttribute('aria-label', inspector ? 'Hide inspector' : 'Show inspector');
    this.els.inspectorButton.title = this.els.inspectorButton.getAttribute('aria-label');
    this.#revealTools();
    this.detail.shown();
  }

  // Conversation or Tools, where they are tabs; Details, from the header's button.
  setTab(tab) {
    const was = this.#tab;
    const before = this.#tabEl(was);
    const from = before?.getClientRects().length ? before.getBoundingClientRect() : null;
    this.#tab = tab === 'tools' || tab === 'details' || (tab === 'delegates' && !this.els.tabD.hidden) ? tab : 'conversation';
    this.#applyTab();
    this.#revealTools();
    this.detail.shown();
    if (this.#tab === was) { return; }
    this.#readToEnd = null;
    // Back on the conversation, its question shows again where a click may be headed: the same
    // wait as a question arriving, one that came while another tab was shown included.
    if (this.#tab === 'conversation' && this.#guarded?.isConnected && this.#selected) { this.#guard(); }
    // Tab switch (ui.md §5.3): the bar under the tab slides from the one left, and what the tab
    // shows fades in without sliding.
    const now = this.#tabEl(this.#tab);
    if (from && now?.getClientRects().length) {
      const r = now.getBoundingClientRect();
      slide(now, 'selection', from.left - r.left, 0, { pseudo: '::after', scaleX: from.width / r.width });
    }
    // A delegate pushed in the list's place is what the Delegates tab shows, the list hidden.
    const delegates = this.#delegate && !this.layout().inspector ? this.detail.element : this.els.delegates;
    const shown = { conversation: this.els.conversation, delegates, tools: this.els.tools, details: this.els.details }[this.#tab];
    if (from && shown?.getClientRects().length) { enter(shown, 'tab'); }
  }

  #tabEl(tab) { return { conversation: this.els.tabC, delegates: this.els.tabD, tools: this.els.tabT }[tab] ?? null; }

  #applyTab() {
    const e = this.els;
    this.view.dataset.tab = this.#tab;
    for (const [tab, el] of [['conversation', e.tabC], ['delegates', e.tabD], ['tools', e.tabT]]) {
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
      // A delegate open holds the inspector in Details' place: it gives way.
      if (this.#delegate) { this.closeDelegate(); }
      this.els.details.scrollIntoView({ block: 'nearest' });
      this.focusDetails();
      return;
    }
    const opening = this.#tab !== 'details';
    this.setTab(opening ? 'details' : 'conversation');
    if (opening) { this.focusDetails(); }
  }

  // Focus to Details' heading, which a screen reader reads as "Details, heading": the same place
  // however Details was opened, and never a button inside it or the question card (#249).
  focusDetails() { this.els.detailsHead.focus(); }

  // Tools shown: fetched the first time, and again when the session changed while they were
  // hidden (updates refetch only what is on screen).
  #revealTools() {
    if (!this.#toolsShown()) { return; }
    if (!this.#history.loaded || (this.#history.at !== this.#s.updatedAt && !this.#history.timer)) { this.#loadHistory(); }
  }

  #toolsShown() {
    // On screen only while Sessions shows (and, on a phone, the session is pushed). At three
    // panes a delegate open takes the inspector in their place, so they are not fetched then.
    return this.#selected && this.viewing() && (this.layout().inspector ? !this.#delegate : this.#tab === 'tools');
  }

  // A phone's overflow menu: Pin, Details and End session (ui.md §6.2).
  #openMore() {
    const end = this.#stop && !this.#stop.element.querySelector('.end-button')?.disabled ? this.#stop : null;
    this.#menu.show(this.els.header, [
      { label: this.#pinned ? 'Unpin' : 'Pin', run: () => this.onPin(this, !this.#pinned) },
      { label: 'Details', run: () => { this.setTab('details'); this.focusDetails(); } },
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
    this.delegates.dispose();
    this.detail.dispose();
    clearTimeout(this.#history.timer);
  }

  // The machine's capabilities changed (a reconnect to another version of its host).
  refreshQuestion() {
    if (!this.#s) { return; }
    const atEnd = this.#reading();
    this.#renderQuestion(this.#s);
    this.#renderReply(this.#s);
    this.#renderStop(this.#s);
    this.#bringQuestion(atEnd);
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
    this.#stop?.update(s, { held: staleNote(ctx?.stale, this.machineName, 'ended') });
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
    this.#reply?.update(s, { attach: ctx?.capabilities?.includes('sessions.attach') === true, largeMax: ctx?.largeAttachments?.() ?? 0, offline: staleNote(ctx?.stale, this.machineName, 'replied to') });
  }

  #renderQuestion(s) {
    const q = s.question;
    const ctx = this.answering();
    const able = q ? answerability(q, ctx?.capabilities ?? [], s.agent) : null;
    const answerable = !!(able?.ok && ctx?.service);
    // Who asks (ui.md §6.4): the main agent, or the delegate the question or waitingOn names,
    // by the rule Needs you uses, so opening its entry never changes who asks (#249).
    const by = q ? delegateOf(s) : null;
    const asker = q ? (by ? `${by} asks` : 'Main agent asks') : null;
    // Its machine offline, or back without its whole list yet (#269): the form stays, with what
    // was entered, but takes nothing until the machine says the question is still asked.
    const held = staleNote(ctx?.stale, this.machineName, 'answered');
    if (!this.#changed('question', [q, answerable, answerable ? ctx.hostId : null, able?.why, ctx?.readOnly ?? null, asker, held])) { return; }
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
      this.#panel.setHeld(held);
      view = this.#panel.element;
    } else if (q) {
      view = renderQuestion(q, { machineName: this.machineName, why: able.why, readOnly: ctx?.readOnly ?? null, asker });
    }
    // A new question replaces a receipt that has said its last word.
    if (q && this.#receipt?.final) { this.#dropReceipt(); }
    // Scroll to a question (ui.md §5.3): one that arrives is brought into sight once the whole
    // update is drawn (#bringQuestion), as long as the conversation was read to its end.
    const arrived = this.#asked !== undefined && !!q && q.id !== this.#asked;
    this.#asked = q?.id ?? null;
    this.els.question.replaceChildren(...[this.#orphanView(), this.#receipt?.element, view].filter(Boolean));
    // An approval's command never slides: a push still under way stops travelling.
    if (view) { stopTravel(view); }
    if (this.#receipt?.fresh) {
      this.#receipt.fresh = false;
      enter(this.#receipt.element, 'acknowledge', { dy: 1 });
    }
    // A question newly drawn where the person can see it: its controls wait before a click counts.
    // Not the same question redrawn (reworded, or its machine's capabilities changed), unless it
    // has just become answerable: its hold lifted (#269) or its machine now taking answers. The
    // note that said why goes, and Send or Approve once moves up under the pointer as it does.
    const unheld = !!this.#guardedHeld && !held;
    const opened = !this.#guardedAnswerable && answerable;
    this.#guarded = view || null;
    const same = view && q.id === this.#guardedId;
    let armed = false;
    if (view && (!same || unheld || opened)) {
      this.#guardedId = q.id;
      if (this.#selected && this.viewing() && this.els.scroll.getClientRects().length) {
        this.#guard();
        armed = true;
      }
    }
    // The same question redrawn while its wait still runs: the new view says so for what is left
    // of it, no longer.
    if (view && !armed) { this.#mark(); }
    this.#guardedHeld = view ? held : null;
    this.#guardedAnswerable = !!(view && answerable);
    this.#arrived = arrived && view ? view : null;
  }

  #guard() {
    this.#guardUntil = (globalThis.performance?.now() ?? Date.now()) + QUESTION_ARRIVAL_GUARD_MS;
    this.#mark();
  }

  // Said on the question while the wait lasts (data-arriving), so what waits on it can tell.
  #mark() {
    const view = this.#guarded;
    const left = this.#guardUntil - (globalThis.performance?.now() ?? Date.now());
    clearTimeout(this.#guardTimer);
    if (left <= 0) { return; }
    view?.setAttribute?.('data-arriving', '');
    this.#guardTimer = setTimeout(() => view?.removeAttribute?.('data-arriving'), left);
  }

  // A click from a pointer (detail > 0) on the question's own controls during the guard is
  // dropped before any handler, form or default action sees it: a choice, Approve once, Deny,
  // Send, a radio or its label. A key's activation (detail 0) goes through, and so does anything
  // else in the slot (a kept draft's Dismiss, say), which the question didn't bring.
  #guardQuestion(slot) {
    slot.addEventListener('click', (e) => {
      if (e.detail === 0 || (globalThis.performance?.now() ?? Date.now()) >= this.#guardUntil) { return; }
      if (!this.#guarded?.contains(e.target) || !e.target.closest?.('button, input, select, label, summary, a')) { return; }
      e.preventDefault();
      e.stopImmediatePropagation();
    }, true);
  }

  #atEnd() {
    const sc = this.els.scroll;
    return sc.scrollHeight - sc.scrollTop - sc.clientHeight < 24;
  }

  #reading() {
    const sc = this.els.scroll;
    if (!sc.getClientRects().length) {
      this.#readToEnd = null;
      return this.#atEnd();
    }
    if (this.#readToEnd === null || sc.scrollTop !== this.#readAt) {
      this.#readToEnd = this.#atEnd();
      this.#readAt = sc.scrollTop;
    }
    return this.#readToEnd;
  }

  // Smoothly, and never into a conversation scrolled back through or held: atEnd is where the
  // reader was before the update, since a longer response drawn with the question moves it.
  // Held is a finger on it, a mouse button down, or a mouse or pen over it: someone reading,
  // whose next click must land on what they were reading (ui.md §5.3).
  #bringQuestion(atEnd) {
    const view = this.#arrived;
    this.#arrived = null;
    const sc = this.els.scroll;
    if (view?.isConnected && atEnd && !this.#touches && !this.#pressing && !this.#hovering && this.#selected && this.viewing() && sc.getClientRects().length) {
      this.#scrollTo(view, scrollBehavior());
    }
  }

  // The question brought into sight on purpose (a notification, a Needs you entry): its controls
  // wait as they do for one that arrives. behavior: the page's smooth scroll, or a jump. el: the
  // part of it to bring into sight (a notification's focused answer), the whole question if not.
  revealQuestion(behavior = scrollBehavior(), el = null) {
    const view = this.#guarded?.isConnected ? this.#guarded : null;
    if (view) { this.#scrollTo(el && view.contains(el) ? el : view, behavior); }
  }

  // A scroll to the question: the guard runs from now and, for a smooth scroll that has somewhere
  // to go, again from each step of it until it lands, since that takes the browser's own time. Only
  // that scroll, heard for a little while: a question already in sight makes no scroll, and a
  // scroll the person makes later must not hold the question's buttons back. A newer scroll
  // replaces it.
  #scrollTo(view, behavior) {
    const sc = this.els.scroll;
    this.#scrollEnd?.abort();
    this.#scrollEnd = null;
    const v = view.getBoundingClientRect();
    const box = sc.getBoundingClientRect();
    // A pixel's rounding is nowhere to go.
    const moves = sc.getClientRects().length > 0 && (v.top < box.top - 1 || v.bottom > box.bottom + 1);
    view.scrollIntoView({ block: 'nearest', behavior });
    this.#guard();
    if (!moves || behavior !== 'smooth') { return; }
    // Every step of the scroll re-arms it, so it runs out only after the question has stopped
    // moving: `scroll` fires in every engine, where `scrollend` isn't in Safari yet. Listening
    // stops at `scrollend`, once the steps stop, or as soon as the person takes the scroller
    // over, so a scroll of their own after it can't hold a click they mean.
    const ended = this.#scrollEnd = new AbortController();
    const { signal } = ended;
    let settle = 0;
    const done = () => { clearTimeout(settle); ended.abort(); };
    sc.addEventListener('scroll', () => {
      this.#guard();
      clearTimeout(settle);
      settle = setTimeout(done, SCROLL_SETTLED_MS);
    }, { signal, passive: true });
    sc.addEventListener('scrollend', () => { this.#guard(); done(); }, { signal });
    for (const type of ['wheel', 'touchstart', 'pointerdown', 'keydown']) { sc.addEventListener(type, done, { signal, passive: true }); }
    setTimeout(done, 3 * QUESTION_ARRIVAL_GUARD_MS);
  }

  // A finger on the conversation holds it until the last finger lifts. Touch events, not pointer
  // events: once the browser takes a touch over to scroll it sends pointercancel while the finger
  // is still down. A mouse button holds it until released, wherever that is, and a mouse or pen
  // holds it while it is over it.
  #watchHold(scroller) {
    // Any scroll: measured afresh at the next update (a scroll away and back to the same place
    // included). Not measured here: an update can come before this event does.
    scroller.addEventListener('scroll', () => { this.#readToEnd = null; }, { passive: true });
    // What the person opens or presses in it (Show reasoning, a disclosure in a response) can
    // grow it with no scroll: where they are is measured afresh at the next update.
    for (const type of ['toggle', 'click']) { scroller.addEventListener(type, () => { this.#readToEnd = null; }, true); }
    const fingers = (e) => { this.#touches = (e.touches?.length ?? 0) > 0; };
    scroller.addEventListener('touchstart', () => { this.#touches = true; }, { passive: true });
    for (const end of ['touchend', 'touchcancel']) { scroller.addEventListener(end, fingers, { passive: true }); }
    scroller.addEventListener('pointerenter', (e) => { if (e.pointerType !== 'touch') { this.#hovering = true; } });
    scroller.addEventListener('pointerleave', (e) => { if (e.pointerType !== 'touch') { this.#hovering = false; } });
    scroller.addEventListener('pointerdown', (e) => {
      if (e.pointerType === 'touch') { return; }
      this.#pressing = true;
      const win = scroller.ownerDocument?.defaultView;
      const up = () => {
        this.#pressing = false;
        win?.removeEventListener('pointerup', up, true);
        win?.removeEventListener('pointercancel', up, true);
      };
      win?.addEventListener('pointerup', up, true);
      win?.addEventListener('pointercancel', up, true);
    });
  }

  #watchReceipt({ service, hostId, sessionId }, questionId) {
    this.#dropReceipt();
    const element = h('p', { class: 'answer-receipt', role: 'status', 'aria-live': 'polite' });
    const receipt = { element, final: false, unwatch: null, fresh: true };
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
    e.rowAge.textContent = ago(s.updatedAt, now);
    e.agentAge.textContent = ago(mainVoice(s).at ?? s.updatedAt, now);
    this.delegates.refreshTimes(now);
    this.strip.refreshTimes(now);
    this.detail.refreshTimes(now);
    this.#renderEvents(s, now);
    // "last seen 2m ago" moves on with nothing from the machine, which is away.
    if (this.#stale && (this.#staleText(now)?.join(' ') ?? null) !== this.#shownStale) { this.update(s); }
  }

  #scheduleHistory() {
    if (this.#history.timer) { return; }
    this.#history.timer = setTimeout(() => { this.#history.timer = null; this.#loadHistory(); }, 2000);
  }

  async #loadHistory() {
    // A refresh queued while Tools showed is dropped if they no longer do (another page, tab or
    // session): revealing them again fetches what is stale.
    if (this.#history.loading || !this.fetchDetail || !this.#toolsShown()) { return; }
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
    // The session changed while this was on its way (an update then had nothing to refresh, or
    // found this still loading): what came may be older than it, so it is asked again.
    if (this.#history.loaded && this.#s.updatedAt !== at && this.#toolsShown()) { this.#scheduleHistory(); }
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

