// The conversation before the latest exchange (ui.md §6.4), drawn read-only from the history
// store's turns. The latest turn is the card's own live exchange, so it is left out here.
import { h } from './dom.js';
import { designIcon, drivenBadge, glyph } from './glyph.js';
import { setFacts } from './meta.js';
import { disclosureName, historyPromptOrigin } from '../prompt-origin.js';
import { renderMarkdown } from '../markdown.js';
import { timeButton, timeRow, timeTicker } from './message-time-view.js';
import { sentMessage } from './sent-message.js';
import { renderResolved } from './question-view.js';
import { closedQuestionIds } from '../history.js';
import { liveVoice } from '../attribution.js';
import { messageInstant } from './message-time.js';

export const WORDS = {
  loading: 'Loading conversation\u2026',
  empty: 'No messages yet',
  unavailable: 'Earlier messages aren\u2019t available from this machine',
  unreadable: 'A turn here couldn\u2019t be read',
  cut: 'Cut short \u00b7 see the machine',
  loadEarlier: 'Load earlier messages',
  loadingEarlier: 'Loading earlier messages\u2026',
  earlierFailed: 'Couldn\u2019t load earlier messages',
  retry: 'Try again',
  start: 'Start of session',
  limited: 'Start of available history',
};

let promptIds = 0;

// A prompt the person didn't type, collapsed as the live card draws it (#1611): its title in agent
// colour, who sent it, one line of it, and Show prompt for the rest. Open or shut is kept by the
// node, which a render reuses while the turn is the same.
function originView(origin, at = null) {
  const full = h('div', { class: 'prompt-full', id: `history-prompt-${++promptIds}`, hidden: true }, h('p', { class: 'plain' }, origin.text));
  const words = h('span', null, 'Show prompt');
  const toggle = h('button', { type: 'button', class: 'prompt-toggle', 'aria-expanded': 'false', 'aria-controls': full.id, 'aria-label': disclosureName(origin, false) },
    designIcon('chevron', { size: 16, cls: 'prompt-chevron' }), words);
  const facts = h('span', { class: 'prompt-facts' });
  setFacts(facts, origin.facts);
  const root = h('div', { class: 'prompt-origin', dataset: { from: origin.from } },
    h('p', { class: 'prompt-label' }, h('span', { class: 'prompt-icon' }, origin.icon === 'clock' ? designIcon('clock', { size: 16 }) : drivenBadge()), facts, origin.by ? h('span', { class: 'prompt-by' }, origin.by) : null, timeButton(at)),
    h('p', { class: 'prompt-preview' }, origin.preview), toggle, full);
  toggle.addEventListener('click', () => {
    const open = toggle.getAttribute('aria-expanded') !== 'true';
    full.hidden = !open;
    root.toggleAttribute('data-open', open);
    toggle.setAttribute('aria-expanded', String(open));
    toggle.setAttribute('aria-label', disclosureName(origin, open));
    words.textContent = open ? 'Hide prompt' : 'Show prompt';
  });
  return root;
}

const TOOL_GLYPH = { running: 'working', done: 'done', failed: 'failed' };

// The line above the oldest turn (ui.md §6.4): a way to load earlier turns, or what stands there
// instead. Null when the machine holds nothing yet.
export function topPlan(snapshot) {
  if (snapshot.phase !== 'ready' || !snapshot.turns.length) { return null; }
  if (snapshot.loading === 'earlier') { return { kind: 'loading', text: WORDS.loadingEarlier }; }
  if (snapshot.error?.on === 'earlier') { return { kind: 'error', text: WORDS.earlierFailed }; }
  if (snapshot.more) { return { kind: 'load', text: WORDS.loadEarlier }; }
  const s = snapshot.start;
  if (!s) { return null; }
  if (s.limited) { return { kind: 'start', text: WORDS.limited }; }
  const day = s.at ? new Date(s.at) : null;
  return { kind: 'start', text: day && !Number.isNaN(day.getTime()) ? `${WORDS.start} \u00b7 ${day.toLocaleDateString(undefined, { dateStyle: 'medium' })}` : WORDS.start };
}

// What to draw for a snapshot: a meta line saying why there is nothing (or null), and the turns
// before the latest one.
export function historyPlan(snapshot, latestId = null) {
  const turns = snapshot.turns.filter((t) => t.id !== latestId || t.parts.some((p) => p.kind === 'question'));
  let note = null;
  if (snapshot.phase === 'loading' && !snapshot.turns.length) { note = WORDS.loading; }
  else if (snapshot.phase === 'unavailable') { note = WORDS.unavailable; }
  else if (snapshot.phase === 'ready' && !snapshot.turns.length && !latestId) { note = WORDS.empty; }
  return { note, turns };
}

// Turn identity is required before comparing words: repeated text in another turn is not evidence.
export function liveMessageTimes(snapshot, session) {
  const a = session.activity;
  const t = a.turn ? snapshot?.turns.find((t) => t.id === a.turn) : null;
  const none = { prompt: null, response: null };
  if (!t || t.provisional || t.unreadable) { return none; }
  const dated = (at) => messageInstant(at) === null ? null : at;
  const prompt = t.prompt && t.prompt.text === a.userMessage
    && (t.prompt.from ?? 'person') === (a.userMessageFrom ?? 'person') ? dated(t.at) : null;
  const reply = t.parts.findLast((p) => p.kind === 'response' && p.text);
  const response = a.responseTurn === a.turn && reply && reply.text === liveVoice(session).response ? dated(reply.at) : null;
  return { prompt, response };
}

// A time on its own line under what it dates, only when there is one to show.
function when(iso) {
  const b = timeButton(iso);
  return b ? h('div', { class: 'msg-time-row' }, b) : null;
}

function resolvedView(question) {
  const record = renderResolved(question, { asker: question.kind === 'approval' ? 'Permission request' : 'Main agent asks' });
  record.summary.prepend(h('p', { class: 'answer-receipt' }, 'Question closed'));
  return record;
}

function part(p, inline = null) {
  if (p.kind === 'question' && inline?.kept && p.question?.id !== inline.activeId && !inline.kept.has(p.question?.id)) { return null; }
  if (p.kind === 'question' && inline?.records?.has(p.question?.id)) { return inline.records.get(p.question.id); }
  if (inline && p.kind === 'question' && p.question?.id === inline.questionId) { return inline.slot; }
  if (p.kind === 'response' && p.text) {
    return h('div', { class: 'history-block' }, h('div', { class: 'md history-reply' }, renderMarkdown(p.text)), when(p.at), p.truncated ? h('p', { class: 'history-meta history-cut' }, WORDS.cut) : null);
  }
  if (p.kind === 'reasoning' && p.text) {
    return h('div', null, h('details', { class: 'history-reasoning' }, h('summary', null, 'Show reasoning'), h('p', { class: 'plain' }, p.text)), when(p.at));
  }
  if (p.kind === 'tool' && p.tool) {
    return h('p', { class: 'history-tool', dataset: { status: p.tool.status } }, glyph(TOOL_GLYPH[p.tool.status] ?? 'done', { size: 12, label: p.tool.status }),
      h('span', { class: 'history-tool-name' }, p.tool.name), h('span', { class: 'history-tool-summary' }, p.tool.summary ?? ''), timeButton(p.at));
  }
  if (p.kind === 'delegate' && p.delegate) {
    const d = p.delegate;
    return h('p', { class: 'history-meta' }, `Delegate ${d.name ?? ''} ${d.event === 'started' ? 'started' : 'reported'}`.replace('  ', ' '), timeButton(p.at));
  }
  if (p.kind === 'question' && p.question) {
    return resolvedView(p.question);
  }
  return null;
}

// A turn only activity has told has no stored parts yet: its reply, the words activity gave it while it
// was the live turn, stays drawn under it until a page brings the stored ones.
export function shownParts(t) {
  if (t.parts.some((p) => p.kind === 'response' && p.text) || !t.live?.response) { return t.parts; }
  return [...t.parts, { kind: 'response', text: t.live.response, truncated: t.live.responseTruncated === true }];
}

export function turnView(t, inline = null) {
  if (t.unreadable) { return h('section', { class: 'history-turn', dataset: { turn: t.id } }, h('p', { class: 'history-meta' }, WORDS.unreadable)); }
  const from = t.prompt?.from ?? 'person';
  const prompt = !t.prompt ? null
    : from === 'person'
      ? h('div', { class: 'history-prompt' }, sentMessage(t.prompt.text ?? '', 'history-bubble'), timeRow(t.at, 'msg-time-owner'))
      : (() => { const o = historyPromptOrigin(t.prompt); return o ? originView(o, t.at) : null; })();
  return h('section', { class: 'history-turn', dataset: { turn: t.id } }, prompt, shownParts(t).map((p) => part(p, inline)), t.truncated ? h('p', { class: 'history-meta history-cut' }, WORDS.cut) : null);
}

// Turns drawn in one go when nothing is drawn yet (the newest, so the pane scrolls at once), and
// older turns drawn per frame after that (#1622). Building a turn (markdown, tool rows) is what
// costs; 500 of them in one task was the hang.
export const FIRST_DRAW = 5;
export const SLICE = 2;
// A frame's share for drawing older turns, past the first SLICE: the load ends sooner, and no
// frame is held up for long.
const SLICE_MS = 6;

const nextFrame = (fn) => (globalThis.requestAnimationFrame ? globalThis.requestAnimationFrame(fn) : setTimeout(fn, 16));

function syncChildren(parent, nodes) {
  const wanted = new Set(nodes);
  for (const n of [...parent.children]) { if (!wanted.has(n)) { n.remove(); } }
  let at = parent.firstElementChild;
  for (const n of nodes) {
    if (n === at) { at = at.nextElementSibling; } else { parent.insertBefore(n, at); }
  }
}

function questionPlaces(t, inline) {
  const questions = t.parts.filter((p) => p.kind === 'question' && p.question);
  return {
    placements: questions.filter((p) => p.question.id === inline.activeId || inline.kept.has(p.question.id)).map((p) => inline.records.get(p.question.id) ?? (p.question.id === inline.questionId ? inline.slot : null)).filter(Boolean),
    keptQuestions: questions.filter((p) => inline.kept.has(p.question.id)).map((p) => p.question.id).join('\n'),
  };
}

export class HistoryView {
  element = h('div', { class: 'history', 'aria-label': 'Earlier conversation', hidden: true });
  #drawn = new Map();
  // Whether the question's slot was drawn inside a turn, where the agent asked it (#1657).
  hosted = false;
  latest = null;
  #backlog = [];
  #framing = false;
  #noteNode = null;
  #label = h('span', { class: 'history-meta history-state', tabindex: '-1', role: 'status', hidden: true });
  #button = h('button', { type: 'button', class: 'history-load', hidden: true, onclick: () => { if (this.#button.getAttribute('aria-disabled') !== 'true') { this.onLoadEarlier?.(); } } });
  #top = h('div', { class: 'history-top', hidden: true }, this.#label, this.#button);
  #topKind = null;
  #topText = null;
  #ticker;
  #inline = null;
  #records = new Map();

  // onLoadEarlier(): the person asked for the page before the oldest turn (the button, or Try again).
  onLoadEarlier = null;

  // Draws the line above the oldest turn; the turns below stay where they are on screen.
  #drawTop(plan) {
    if ((plan?.kind ?? null) === this.#topKind && (plan?.text ?? null) === this.#topText) { return; }
    this.#topKind = plan?.kind ?? null;
    this.#topText = plan?.text ?? null;
    this.anchor(() => {
      this.#top.hidden = !plan;
      this.#top.dataset.state = plan?.kind ?? '';
      if (!plan) { return; }
      // One button and one status line live across the states, so keyboard focus stays on the
      // button while it loads, fails and retries.
      const hadFocus = this.#button.ownerDocument.activeElement === this.#button;
      const asks = plan.kind === 'load' || plan.kind === 'error';
      this.#label.textContent = plan.kind === 'error' || plan.kind === 'start' ? plan.text : '';
      this.#label.hidden = !this.#label.textContent;
      this.#button.hidden = !(asks || plan.kind === 'loading');
      this.#button.textContent = plan.kind === 'error' ? WORDS.retry : plan.text;
      if (plan.kind === 'loading') { this.#button.setAttribute('aria-disabled', 'true'); } else { this.#button.removeAttribute('aria-disabled'); }
      if (hadFocus && this.#button.hidden) { this.#label.focus(); }    });
  }

  // opts: now, setTimer, clearTimer, doc (message-time-view's timeTicker), for a test's clock.
  constructor(opts = {}) { this.#ticker = timeTicker(this.element, opts); }

  dispose() { this.#ticker.stop(); }

  // anchor(change): runs change() so that what the person is looking at stays where it is while
  // turns are put in above it. onSettled(): the older turns held are all drawn.
  anchor = (change) => change();
  onSettled = () => {};

  // True while older turns held are waiting to be drawn.
  get pending() { return this.#backlog.length > 0; }

  // snapshot: the store's. latestId: the turn the card draws live, left out. inline: the question
  // slot and the id of the question in it, drawn at that question's place in its turn.
  render(snapshot, latestId = null, inline = null) {
    return this.#withFocus(inline, () => this.#render(snapshot, latestId, inline));
  }

  // First placement can move a focused form through a detached turn before attaching it.
  #withFocus(inline, change) {
    const doc = this.element.ownerDocument;
    const focused = doc.activeElement;
    const root = [inline?.slot, ...(inline?.records?.values() ?? []), ...(this.#inline?.records?.values() ?? [])].find((p) => p?.contains(focused));
    const parent = root?.parentNode;
    const caret = root && typeof focused.selectionStart === 'number' ? [focused.selectionStart, focused.selectionEnd, focused.selectionDirection ?? 'none'] : null;
    const result = change();
    const displaced = !doc.activeElement || doc.activeElement === doc.body || (doc.activeElement === focused && root?.parentNode !== parent);
    if (root && focused.isConnected && displaced && focused.getClientRects().length) {
      focused.focus({ preventScroll: true });
      if (caret) { focused.setSelectionRange(...caret); }
    }
    return result;
  }

  #render(snapshot, latestId, inline) {
    const { note, turns } = historyPlan(snapshot, latestId);
    this.latest = turns.find((t) => t.id === latestId) ?? null;
    const questions = turns.flatMap((t) => t.parts.filter((p) => p.kind === 'question' && p.question));
    const kept = inline?.kept ?? closedQuestionIds(turns, [], inline?.activeId);
    const records = new Map(inline?.records);
    const reconstructed = new Map();
    for (const { question } of questions) {
      if (!kept.has(question.id) || records.has(question.id) || question.id === inline?.questionId) { continue; }
      const key = JSON.stringify(question);
      const old = this.#records.get(question.id);
      const record = old?.key === key ? old.record : resolvedView(question);
      reconstructed.set(question.id, { key, record });
      records.set(question.id, record);
    }
    this.#records = reconstructed;
    inline = { ...inline, kept, records };
    this.#inline = inline;
    // The turns drawn already stay; the newest few are drawn if none is; those older than the
    // oldest drawn wait for their slices.
    const oldest = turns.findIndex((t) => this.#drawn.has(t.id));
    const from = oldest < 0 ? Math.max(0, turns.length - FIRST_DRAW) : oldest;
    this.#backlog = turns.slice(0, from);
    const keep = new Map();
    const nodes = [this.#top];
    this.hosted = false;
    if (note) {
      if (this.#noteNode?.textContent !== note) { this.#noteNode = h('p', { class: 'history-meta history-state' }, note); }
      nodes.push(this.#noteNode);
    }
    for (const t of turns.slice(from)) {
      // Turns are replaced when they change, so the same object is the same drawing.
      const { placements, keptQuestions } = questionPlaces(t, inline);
      this.hosted ||= placements.includes(inline.slot);
      const old = this.#drawn.get(t.id);
      let node = old?.node;
      if (old?.turn !== t || old.keptQuestions !== keptQuestions || old.placements?.length !== placements.length || placements.some((p, i) => old.placements[i] !== p || !old.node.contains(p))) {
        // Build around placeholders so a live question is never detached while its turn updates.
        const holders = new Map(placements.map((p) => [p, h('span')]));
        const fresh = turnView(t, { ...inline, slot: holders.get(inline.slot) ?? inline.slot, records: new Map([...inline.records ?? []].map(([id, p]) => [id, holders.get(p) ?? p])) });
        const originals = new Map([...holders].map(([p, placeholder]) => [placeholder, p]));
        node ??= fresh;
        syncChildren(node, [...fresh.children].map((p) => originals.get(p) ?? p));
      }
      keep.set(t.id, { turn: t, node, placements, keptQuestions });
      nodes.push(node);
    }
    this.#drawn = keep;
    // A live exchange re-renders this on every update with the same drawn turns, and a new message
    // adds one at the end. Re-inserting them all dirtied the layout of every turn, so the next scroll
    // read (scrollTop) cost ~100 ms on 100 turns, per update: the stalls in #1684. Turns already
    // drawn stay where they are; only turns that are gone (or rebuilt) are taken out, and the new
    // ones put in their place.
    syncChildren(this.element, nodes);
    const shown = nodes.length > 1;
    this.#drawTop(this.onLoadEarlier ? topPlan(snapshot) : null);
    this.element.hidden = !shown && this.#top.hidden;
    this.#ticker.refresh();
    if (this.#backlog.length) { this.#schedule(); }
    return shown;
  }

  #schedule() {
    if (this.#framing) { return; }
    this.#framing = true;
    nextFrame(() => { this.#framing = false; this.#slice(); });
  }

  // The newest of the waiting turns, a few at a time, above what is drawn.
  #slice() {
    this.#withFocus(this.#inline, () => this.#drawSlice());
  }

  #drawSlice() {
    const started = performance.now();
    const batch = [];
    while (this.#backlog.length && (batch.length < SLICE || performance.now() - started < SLICE_MS)) {
      const t = this.#backlog.pop();
      batch.unshift(t);
      const places = questionPlaces(t, this.#inline);
      this.hosted ||= places.placements.includes(this.#inline.slot);
      this.#drawn.set(t.id, { turn: t, node: turnView(t, this.#inline), ...places });
    }
    if (batch.length) {
      const nodes = batch.map((t) => this.#drawn.get(t.id).node);
      const top = this.element.querySelector(':scope > .history-turn');
      this.anchor(() => { if (top) { top.before(...nodes); } else { this.element.append(...nodes); } this.element.hidden = false; });
            this.#ticker.refresh();
    }
    if (this.#backlog.length) { this.#schedule(); } else { this.onSettled(); }
  }
}
