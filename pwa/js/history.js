// Bridge Protocol v1, session history (#292): docs/protocol/v1-history.md. Mirrors
// src/AgentBridge.Protocol/History.cs, and reads a host's pages as protocol.js reads a session:
// by v1-sessions.md's Malformed records, so a field of the wrong type reads as absent, a record
// the spec calls strict is refused rather than defaulted, and an unknown enum value reads as the
// one the spec says (an unknown outcome is never `approved`, an unknown sender never the person).
//
// What comes out keeps the wire's own names, so the shared vectors (docs/protocol/vectors/history.json)
// hold against it as they are, with the fields a view needs but the wire leaves out filled in
// (false, 0, null) rather than absent.
import { Ops, Capabilities, readQuestion, readSubagentKind, readSubagentOutcome, readTool, readUserMessageFrom } from './protocol.js';

export const HISTORY = Object.freeze({
  op: Ops.sessionHistory,
  capability: Capabilities.sessionsHistory,
  // Turns a page asks for when the client names no limit, and the most it may (H3).
  defaultTurns: 20,
  maxTurns: 50,
  // What the first page of an open asks for (#1622): few enough to draw in one frame, so the pane
  // scrolls at once. Older pages come in the background at the page size.
  firstTurns: 5,
  // The newest turns of a session a host serves, and so about what a client keeps (H3, H6).
  kept: 500,
  maxCursorBytes: 256,
});

// The time a required one that is missing or unreadable reads as (Malformed records rule 1).
const NO_TIME = '0001-01-01T00:00:00.000Z';

const str = (v) => (typeof v === 'string' ? v : null);
const obj = (v) => (v !== null && typeof v === 'object' && !Array.isArray(v) ? v : null);
const present = (v) => v !== undefined && v !== null;
const tolerant = (known, fallback) => (v) => (known.includes(v) ? v : fallback);

export const PartKinds = Object.freeze(['response', 'reasoning', 'tool', 'delegate', 'question']);
export const readPartKind = tolerant(PartKinds, 'unknown');
export const readDelegateEventKind = tolerant(['started', 'reported'], 'unknown');
// An outcome this app does not know is `unknown`, never `approved`.
export const readOutcome = tolerant(['answered', 'approved', 'denied', 'cancelled', 'unknown'], 'unknown');

// session.history's request, checked as the host checks it (HistoryProtocol.Check), word for word:
// why the host would refuse it, or null.
export function checkHistoryRequest(data) {
  if (typeof data?.id !== 'string' || data.id.trim() === '') { return 'session.history needs a session id'; }
  if (data.limit !== undefined && data.limit !== null && !(Number.isInteger(data.limit) && data.limit >= 1 && data.limit <= HISTORY.maxTurns)) { return `limit must be 1 to ${HISTORY.maxTurns}`; }
  if (data.before !== undefined && data.before !== null) {
    const bytes = typeof data.before === 'string' ? new TextEncoder().encode(data.before).length : 0;
    if (bytes < 1 || bytes > HISTORY.maxCursorBytes) { return `a cursor is 1 to ${HISTORY.maxCursorBytes} bytes`; }
  }
  return null;
}

// What started a turn, or null when the record is refused: a sender that is not text (strict, security).
export function readPrompt(raw) {
  const p = obj(raw);
  if (!p || (present(p.from) && typeof p.from !== 'string')) { return null; }
  return { text: str(p.text) ?? '', truncated: p.truncated === true, from: readUserMessageFrom(p.from), label: str(p.label) };
}

// One of the main agent's own delegates starting or reporting back, or null when it names no
// delegate as text (strict, identity).
export function readDelegateEvent(raw) {
  const d = obj(raw);
  if (!d || typeof d.agent !== 'string') { return null; }
  return {
    agent: d.agent,
    event: readDelegateEventKind(d.event),
    name: str(d.name),
    kind: present(d.kind) ? readSubagentKind(d.kind) : null,
    outcome: present(d.outcome) ? readSubagentOutcome(d.outcome) : null,
  };
}

// A question as session.question carried it, read-only here. One whose id, kind or message is not
// text is not read at all (Malformed records rule 4: the part is kept, without it).
function readHeldQuestion(raw) {
  const q = obj(raw);
  if (!q || typeof q.id !== 'string' || typeof q.kind !== 'string' || typeof q.message !== 'string') { return null; }
  return readQuestion(q);
}

// One thing in a turn after its prompt, or null when it has no id as text or its outcome is not
// text (strict). A kind this app does not know reads as `unknown`, which a view skips.
export function readPart(raw) {
  const p = obj(raw);
  if (!p || typeof p.id !== 'string' || p.id === '' || (present(p.outcome) && typeof p.outcome !== 'string')) { return null; }
  const kind = readPartKind(p.kind);
  return {
    id: p.id,
    kind,
    at: str(p.at) ?? NO_TIME,
    text: str(p.text),
    truncated: p.truncated === true,
    tool: kind === 'tool' ? readTool(p.tool) : null,
    delegate: kind === 'delegate' ? readDelegateEvent(p.delegate) : null,
    question: kind === 'question' ? readHeldQuestion(p.question) : null,
    outcome: kind === 'question' ? (present(p.outcome) ? readOutcome(p.outcome) : null) : null,
    answer: str(p.answer),
    answeredAt: str(p.answeredAt),
  };
}

// A prompt and everything until the next prompt, or null when it has no id as text or its prompt's
// sender is not text: left out of its page, the turns around it kept (rule 2).
export function readTurn(raw) {
  const t = obj(raw);
  if (!t || typeof t.id !== 'string' || t.id === '') { return null; }
  let prompt = null;
  if (present(t.prompt)) {
    // Strict (security): a prompt that is not a record refuses the turn, as one whose sender is not text does.
    prompt = readPrompt(t.prompt);
    if (prompt === null) { return null; }
  }
  return {
    id: t.id,
    at: str(t.at) ?? NO_TIME,
    prompt,
    parts: Array.isArray(t.parts) ? t.parts.map(readPart).filter(Boolean) : [],
    partsMore: Number.isSafeInteger(t.partsMore) && t.partsMore > 0 ? t.partsMore : 0,
    truncated: t.truncated === true,
    unreadable: t.unreadable === true,
  };
}

// Where the history a host serves begins: { at } the session's first record, { limited } the host
// serves no more though the session has it. Null when it is not a record.
export function readStart(raw) {
  const s = obj(raw);
  if (!s) { return null; }
  return { at: str(s.at), limited: s.limited === true };
}

// session.history's result, one of:
//   { turns, before, start, partial }  a page, oldest turn first; before is the cursor for the page
//                                      before it (null when nothing older is served, and start says
//                                      where it begins)
//   { missing: true }                  the session is not one the host lists
//   { stale: true }                    the cursor can't be continued from: ask again without it
//   { unavailable: why }               the host has no history for this session
// or null when it is not a result this app can read: a cursor that is not text, or longer than a
// request may carry, or a start that is not a record refuse the page, as the host's reader would
// (strict, identity), and a page with no list of turns, or one of more than a page holds, is not one.
export function readHistoryResult(raw) {
  const r = obj(raw);
  if (!r) { return null; }
  if (r.missing === true) { return { missing: true }; }
  if (r.stale === true) { return { stale: true }; }
  if (typeof r.unavailable === 'string') { return { unavailable: r.unavailable }; }
  if (present(r.before) && typeof r.before !== 'string') { return null; }
  // A cursor longer than a request may carry would be refused on the next request, every time.
  if (typeof r.before === 'string' && new TextEncoder().encode(r.before).length > HISTORY.maxCursorBytes) { return null; }
  const start = present(r.start) ? readStart(r.start) : null;
  if (present(r.start) && start === null) { return null; }
  // A host sends at most the 50 turns a page may ask for: more is not a page of this protocol.
  if (!Array.isArray(r.turns) || r.turns.length > HISTORY.maxTurns) { return null; }
  // A turn id is unique within a session: one a page repeats is held once, at its first place.
  const seen = new Set();
  const turns = r.turns.map(readTurn).filter((t) => t && !seen.has(t.id) && seen.add(t.id));
  return {
    turns,
    before: r.before ? r.before : null,
    start,
    partial: r.partial === true,
  };
}
