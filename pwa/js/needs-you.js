// The Needs you lane (ui.md §6.1, #165): one entry per pending question across the machines
// shown, oldest first, above the list. It refers to sessions and never moves their rows. What
// each entry says and offers is decided here, not in the markup, which #179 rewrites.
//
// An entry offers one of three things (its mode):
//   approve    an approval this device can answer, its whole prompt shown: Approve once and Deny,
//              sent exactly as the card's buttons send them (answers.js prepareAnswer, the same
//              AnswerService entry);
//   atMachine  a prompt no host can answer from here: it says so, with no controls;
//   open       anything else (a form, or a question this machine won't take from here): its
//              session is opened, where the card says what it asks and why.
import { answerability, tapToAnswer } from './answers.js';
import { parseMarkdown } from './markdown.js';

const AGENT_NAME = { copilot: 'Copilot', claude: 'Claude', codex: 'Codex', unknown: 'Agent' };
const LINE_MAX = 140;

// Markdown down to one line of plain text: no emphasis, code or heading marks, whitespace
// collapsed, cut with an ellipsis.
export function plainLine(text, max = LINE_MAX) {
  const line = String(text ?? '')
    .replace(/^\s{0,3}(?:#{1,6}\s+|>\s?|[-*+]\s+|\d+[.)]\s+)/, '')
    .replace(/(\*\*|__|`+)/g, '')
    .replace(/(^|[\s(])[*_]([^*_\s][^*_]*?)[*_](?=[\s).,!?:;]|$)/g, '$1$2')
    .replace(/\[([^\]]*)\]\([^)]*\)/g, '$1')
    .replace(/\s+/g, ' ')
    .trim();
  return line.length > max ? `${line.slice(0, max - 1).trimEnd()}\u2026` : line;
}

const FENCE = /^\s{0,3}(`{3,}|~{3,})/;

// The line an entry shows: a question's first line of prose, never a code fence's.
export function questionLine(q) {
  const first = (q?.message ?? '').split('\n').find((l) => l.trim() && !FENCE.test(l));
  return plainLine(first ?? '');
}

// How much of an approval's prompt the lane can show whole. Approve once allows everything the
// prompt describes (a command, a URL, a tool's arguments), so it is offered in the lane only when
// all of it is on screen, as the card shows it: Markdown drawn as Markdown, code literal and
// wrapped. A longer one (an edit's diff, a long command) opens its session instead, and so does
// one with a table, which scrolls sideways.
export const PROMPT_MAX = Object.freeze({ chars: 600, lines: 12 });

const hasTable = (nodes) => nodes.some((n) => n && typeof n === 'object' && (n.tag === 'table' || (Array.isArray(n.children) && hasTable(n.children))));

export function wholePrompt(q) {
  const m = (q?.message ?? '').trim();
  return m.length > 0 && m.length <= PROMPT_MAX.chars && m.split('\n').length <= PROMPT_MAX.lines && !hasTable(parseMarkdown(m));
}

// The agents whose permission prompts a host writes whole and literal, so the lane can show what
// Approve once allows: Copilot's (CopilotPermission, code in a fence that the command cannot
// close). A Codex approval's command reaches the host cut short with nothing saying so, and is
// answered on its card.
const INLINE_AGENTS = new Set(['copilot']);

// Who asked a session's question: null for the session's own, else the delegate's name from
// `subagents` (ui.md §2.2 rule 2). A delegate the list left out (the host's limit or budget)
// is still a delegate, just unnamed.
export function delegateOf(session) {
  const q = session?.question;
  const id = q?.agent ?? (q ? session.waitingOn : null);
  if (!id) { return null; }
  return session.subagents?.find((a) => a.id === id)?.name || 'A delegate';
}

// The approve and deny options of an approval answered on a tap, or null when it has no such
// pair (an older host's approval with other words for them is answered at its card).
export function decisions(q) {
  if (q?.kind !== 'approval' || !tapToAnswer(q)) { return null; }
  const options = q.fields[0].options;
  const approve = options.find((o) => o.value === 'approve');
  const deny = options.find((o) => o.value === 'deny');
  return approve && deny ? { approve, deny } : null;
}

const askedAt = (s) => s.question.askedAt ?? s.statusSince ?? s.updatedAt ?? '';

// entries: [{ hostId, session }] from every machine shown. capabilities(hostId): what that
// machine takes from here ([] for one not connected). nameOf(hostId): its name. labelled: whether
// entries name their machine (more than one shown).
export function needsYou(entries, { capabilities = () => [], nameOf = () => '', labelled = false } = {}) {
  const out = [];
  for (const { hostId, session: s } of entries) {
    const q = s?.question;
    if (!q) { continue; }
    const machine = nameOf(hostId) ?? '';
    const able = answerability(q, capabilities(hostId) ?? [], s.agent);
    const pair = decisions(q);
    const whole = q.kind === 'approval' && wholePrompt(q);
    const mode = q.answerAtMachine ? 'atMachine' : pair && able.ok && whole && INLINE_AGENTS.has(s.agent) ? 'approve' : 'open';
    out.push({
      key: `${hostId}\n${s.id}`,
      hostId,
      sessionId: s.id,
      questionId: q.id,
      question: q,
      kind: q.kind,
      title: s.title || 'Untitled session',
      machine: labelled ? machine : '',
      sortName: machine,
      agent: AGENT_NAME[s.agent] ?? AGENT_NAME.unknown,
      delegate: delegateOf(s),
      askedAt: askedAt(s),
      line: questionLine(q),
      // An approval's prompt in full, when it fits: what Approve once allows, all of it.
      prompt: whole ? q.message : null,
      mode,
      decisions: mode === 'approve' ? pair : null,
    });
  }
  // Oldest first; questions asked at the same moment by machine, then session.
  return out.sort((a, b) => cmp(a.askedAt, b.askedAt) || cmp(a.sortName, b.sortName) || cmp(a.hostId, b.hostId) || cmp(a.sessionId, b.sessionId));
}

const cmp = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

// The lane's count on the app's icon (the Badging API, where the browser has it): set when it
// changes, cleared at none. Nothing to do where there is no badge, and a refusal is no error.
export class AppBadge {
  #last = null;

  constructor(nav = globalThis.navigator) { this.nav = nav; }

  get supported() { return typeof this.nav?.setAppBadge === 'function'; }

  set(count) {
    const n = Number.isSafeInteger(count) && count > 0 ? count : 0;
    if (n === this.#last || !this.supported) { return; }
    this.#last = n;
    let p;
    try {
      p = n > 0 ? this.nav.setAppBadge(n) : typeof this.nav.clearAppBadge === 'function' ? this.nav.clearAppBadge() : this.nav.setAppBadge(0);
    } catch { return; }
    p?.catch?.(() => {});
  }
}
