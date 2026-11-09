// The conversation before the latest exchange (ui.md §6.4), drawn read-only from the history
// store's turns. The latest turn is the card's own live exchange, so it is left out here.
import { h } from './dom.js';
import { glyph } from './glyph.js';
import { renderMarkdown } from '../markdown.js';

export const WORDS = {
  loading: 'Loading conversation\u2026',
  empty: 'No messages yet',
  unavailable: 'Earlier messages aren\u2019t available from this machine',
  unreadable: 'A turn here couldn\u2019t be read',
  cut: 'Cut short \u00b7 see the machine',
};

const SENDER = { schedule: 'Scheduled prompt', agent: 'Prompt sent by an agent', other: 'Prompt from another source' };
const OUTCOME = { answered: 'Answered', approved: 'Approved', denied: 'Denied', cancelled: 'Not answered' };
const TOOL_GLYPH = { running: 'working', done: 'done', failed: 'failed' };

// What to draw for a snapshot: a meta line saying why there is nothing (or null), and the turns
// before the latest one.
export function historyPlan(snapshot, latestId = null) {
  const turns = snapshot.turns.filter((t) => t.id !== latestId);
  let note = null;
  if (snapshot.phase === 'loading' && !snapshot.turns.length) { note = WORDS.loading; }
  else if (snapshot.phase === 'unavailable') { note = WORDS.unavailable; }
  else if (snapshot.phase === 'ready' && !snapshot.turns.length && !latestId) { note = WORDS.empty; }
  return { note, turns };
}

function part(p) {
  if (p.kind === 'response' && p.text) {
    return h('div', { class: 'history-block' }, h('div', { class: 'md history-reply' }, renderMarkdown(p.text)), p.truncated ? h('p', { class: 'history-meta history-cut' }, WORDS.cut) : null);
  }
  if (p.kind === 'reasoning' && p.text) {
    return h('details', { class: 'history-reasoning' }, h('summary', null, 'Show reasoning'), h('p', { class: 'plain' }, p.text));
  }
  if (p.kind === 'tool' && p.tool) {
    return h('p', { class: 'history-tool', dataset: { status: p.tool.status } }, glyph(TOOL_GLYPH[p.tool.status] ?? 'done', { size: 12, label: p.tool.status }),
      h('span', { class: 'history-tool-name' }, p.tool.name), h('span', { class: 'history-tool-summary' }, p.tool.summary ?? ''));
  }
  if (p.kind === 'delegate' && p.delegate) {
    const d = p.delegate;
    return h('p', { class: 'history-meta' }, `Delegate ${d.name ?? ''} ${d.event === 'started' ? 'started' : 'reported'}`.replace('  ', ' '));
  }
  if (p.kind === 'question' && p.question) {
    const said = OUTCOME[p.outcome];
    return h('p', { class: 'history-meta' }, [p.question.message, said ? ` \u00b7 ${said}` : '', p.answer ? `: ${p.answer}` : ''].join(''));
  }
  return null;
}

export function turnView(t) {
  if (t.unreadable) { return h('section', { class: 'history-turn', dataset: { turn: t.id } }, h('p', { class: 'history-meta' }, WORDS.unreadable)); }
  const from = t.prompt?.from ?? 'person';
  const prompt = !t.prompt ? null
    : from === 'person'
      ? h('div', { class: 'history-prompt' }, h('div', { class: 'history-bubble' }, h('p', { class: 'plain' }, t.prompt.text ?? '')))
      : h('div', { class: 'history-origin' }, h('p', { class: 'history-origin-label' }, t.prompt.label ?? SENDER[from] ?? SENDER.other), h('p', { class: 'plain' }, t.prompt.text ?? ''));
  return h('section', { class: 'history-turn', dataset: { turn: t.id } }, prompt, t.parts.map(part), t.truncated ? h('p', { class: 'history-meta history-cut' }, WORDS.cut) : null);
}

export class HistoryView {
  element = h('div', { class: 'stream history', 'aria-label': 'Earlier conversation', hidden: true });
  #drawn = new Map();

  // snapshot: the store's. latestId: the turn the card draws live, left out.
  render(snapshot, latestId = null) {
    const { note, turns } = historyPlan(snapshot, latestId);
    const keep = new Map();
    const nodes = [];
    if (note) { nodes.push(h('p', { class: 'history-meta history-state' }, note)); }
    for (const t of turns) {
      // Turns are replaced when they change, so the same object is the same drawing.
      const old = this.#drawn.get(t.id);
      const node = old?.turn === t ? old.node : turnView(t);
      keep.set(t.id, { turn: t, node });
      nodes.push(node);
    }
    this.#drawn = keep;
    this.element.replaceChildren(...nodes);
    this.element.hidden = !nodes.length;
    return nodes.length > 0;
  }
}
