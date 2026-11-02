// Who said what in a session: the main agent's own response and reasoning, and its delegates
// (docs/design/subagents.md §5; ui.md §1 principle 3, "One voice per line"). No DOM here, so the
// card today and the layouts that replace it (#179, #176) all show the same voices, tested once
// (test/unit/attribution.test.mjs).
//
// A host's top-level response and reasoning are last-writer-wins, kept for older clients: a
// delegate's thinking can be there in place of the main agent's answer. activity.source says
// which delegate wrote each, and activity.own carries the main agent's own text beside it.
import { ago, duration } from './ui/time.js';

// The main agent's own words, never a delegate's (v1-sessions.md, Activity, the `own` row):
// for each of response and reasoning, own's when source names a delegate for it, otherwise
// the top-level text, or own's when that is absent.
//   responseTruncated: the start of the response was cut to fit.
//   responseDropped: the main agent had a response, but the session budget left it out.
//   attributed: the host says whose each field is, so the words can be labelled "Main agent".
//   at: when the main agent last wrote, where its words are own's and the host says (own.at);
//     null where they are the top-level ones, which the session's own updatedAt dates. A
//     delegate's later update must not make the main agent's words look newer (#249).
export function mainVoice(session) {
  const a = session?.activity ?? {};
  const source = a.source ?? null;
  const own = a.own ?? null;
  let response;
  let truncated;
  if (source?.response || !a.response) {
    // A field source names but own lacks: the main agent has none, unless the budget dropped it.
    response = own?.response || null;
    truncated = own?.responseTruncated === true;
  } else {
    response = a.response;
    truncated = a.responseTruncated === true;
  }
  const reasoning = (source?.reasoning ? own?.reasoning : a.reasoning || own?.reasoning) || null;
  const owns = !!(source?.response || source?.reasoning || !a.response);
  return {
    response,
    responseTruncated: !!response && truncated,
    responseDropped: !response && truncated,
    reasoning,
    at: owns && own?.at ? own.at : null,
    attributed: source !== null || own !== null || Array.isArray(session?.subagents) || !!session?.subagentsMore,
  };
}

// A delegate's name, as it is shown everywhere: its rows, the strip, the tab, the inspector, the
// conversation, a question's heading, Needs you and notifications (ui.md §6.3, §8). An agent
// chose it, so it can try to pass for the main agent or hide words in it; what is shown is:
//   - its text without the characters that steer or hide it (format and control characters,
//     bidi controls, default-ignorables, line and paragraph separators), runs of white space one
//     space;
//   - cut past NAME_MAX characters, with an ellipsis;
//   - with nothing left, "Delegate" and the start of its id;
//   - and a name that reads "main agent" once NFKC-normalised and case-folded, shown as
//     Delegate “<name>”, so no delegate is ever labelled as the main agent.
export const NAME_MAX = 60;
const BREAKS = /[\t\n\v\f\r\u0085\u2028\u2029]/gu;
const HIDDEN = /[\p{Cf}\p{Cc}\p{Bidi_Control}\p{Default_Ignorable_Code_Point}]/gu;
const MAIN_AGENT = 'main agent';

// Text an agent wrote, safe to put in a label: nothing hidden or steering, on one line.
export function cleanLabel(text) {
  return String(text ?? '').replace(BREAKS, ' ').replace(HIDDEN, '').replace(/\s+/gu, ' ').trim();
}

// The start of a delegate's id, for a delegate with no name to show.
export function shortId(id) {
  return Array.from(cleanLabel(id).replace(/\s/gu, '')).slice(0, 8).join('');
}

export function delegateName(agent) {
  const name = cleanLabel(agent?.name);
  if (!name) {
    const id = shortId(agent?.id);
    return id ? `Delegate ${id}` : 'Delegate';
  }
  const folded = name.normalize('NFKC').toLowerCase().normalize('NFKC').replace(/\s+/gu, ' ').trim();
  const chars = Array.from(name);
  const shown = chars.length > NAME_MAX ? `${chars.slice(0, NAME_MAX - 1).join('').trimEnd()}\u2026` : name;
  return folded === MAIN_AGENT ? `Delegate \u201c${shown}\u201d` : shown;
}

// A delegate's state, as its glyph (ui.md §2.1) and its word. Waiting is a question of its own;
// idle, a background delegate between runs, which a message can wake; ended, done or failed
// from how its last run went, or plain ended when the host never saw it end.
export function delegateState(a) {
  switch (a.status) {
    case 'working': return { glyph: 'working', words: 'Working', running: true };
    case 'waiting': return { glyph: 'needs', words: 'Asks', running: true };
    case 'ended':
      if (a.outcome === 'failed') { return { glyph: 'failed', words: 'Failed', running: false }; }
      if (a.outcome === 'done') { return { glyph: 'done', words: 'Done', running: false }; }
      return { glyph: 'ended', words: 'Ended', running: false };
    default: return { glyph: 'idle', words: 'Idle', running: false };
  }
}

// The session's delegates, as a card lists them: { active, finished, more }, or null when the
// host lists none (Claude and Codex, or a host before sub-agents: status rule 8 says what it can).
// A list the session budget emptied but counted in subagentsMore still says how many.
//   active: running ones first (working, waiting), then idle ones, each nested under its parent.
//   finished: ended ones, folded into "N finished", nested the same way.
//   more: entries the host left out (subagentsMore).
// Each row: the entry, plus { depth, glyph, words, running, parentName }. parentName names the
// delegate that started it, when that one is listed, so a screen reader hears what the indent
// shows.
export function delegateList(session) {
  const list = Array.isArray(session?.subagents) ? session.subagents : [];
  const more = session?.subagentsMore ?? 0;
  if (list.length === 0 && !more) { return null; }
  const names = new Map(list.map((a) => [a.id, delegateName(a)]));
  const rows = list.map((a) => ({ ...a, name: delegateName(a), ...delegateState(a), parentName: a.parent && a.parent !== a.id ? names.get(a.parent) ?? null : null }));
  const running = rows.filter((r) => r.running);
  const idle = rows.filter((r) => !r.running && r.status !== 'ended');
  const ended = rows.filter((r) => r.status === 'ended');
  return { active: nest([...running, ...idle]), finished: nest(ended), more };
}

// Each entry under its parent when the parent is in the same group, in the order given; the
// rest at the top. A parent link that loops (a broken host) still lists every entry once.
function nest(group) {
  const ids = new Set(group.map((r) => r.id));
  const children = new Map();
  for (const r of group) {
    if (r.parent && r.parent !== r.id && ids.has(r.parent)) {
      if (!children.has(r.parent)) { children.set(r.parent, []); }
      children.get(r.parent).push(r);
    }
  }
  const out = [];
  const seen = new Set();
  const visit = (r, depth) => {
    if (seen.has(r.id)) { return; }
    seen.add(r.id);
    out.push({ ...r, depth });
    for (const c of children.get(r.id) ?? []) { visit(c, depth + 1); }
  };
  for (const r of group) {
    if (!r.parent || r.parent === r.id || !ids.has(r.parent)) { visit(r, 0); }
  }
  for (const r of group) { visit(r, 0); }
  return out;
}

// How long a running delegate has been at it ("4m"), or when it finished ("finished 3m ago").
export function delegateTime(row, now = Date.now()) {
  if (row.status === 'working' || row.status === 'waiting') { return duration(row.startedAt, now); }
  const when = ago(row.endedAt, now);
  return when ? `finished ${when}` : 'finished';
}
