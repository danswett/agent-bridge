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
//   - no middle dot of its own, since a status splits its facts at one and a row shows only the
//     first: "Main agent asks · Read logs" would have read "Main agent asks" there (#252);
//   - cut past NAME_MAX characters (whole graphemes), with an ellipsis;
//   - with nothing left, "Delegate" and the start of its id;
//   - and a name that reads as the main agent's, shown as Delegate “<name>”, so no delegate is
//     ever labelled as the main agent (posesAsMainAgent).
// The app's DelegateLabel (#250) follows the same rule.
export const NAME_MAX = 60;
const BREAKS = /[\t\n\v\f\r\u0085\u2028\u2029]/gu;
const HIDDEN = /[\p{Cf}\p{Cc}\p{Cs}\p{Co}\p{Cn}\p{Bidi_Control}\p{Default_Ignorable_Code_Point}]/gu;
// U+00B7 and what looks like it or normalises to it: the fact separator, real or forged.
const DOTS = /[\u00b7\u0387\u16eb\u2022\u2027\u2219\u22c5\u2e31\u30fb\ua78f\uff65]/gu;
// Inside Delegate “…”, a quote of the name's own could close the quotes and carry on as other words.
const QUOTES = /["\u00ab\u00bb\u201c\u201d\u201e\u201f\u2033\u2039\u203a\uff02]/gu;

// Text an agent wrote, safe to put in a label: nothing hidden or steering, on one line.
export function cleanLabel(text) {
  return String(text ?? '').replace(BREAKS, ' ').replace(HIDDEN, '').replace(/\s+/gu, ' ').trim();
}

// The start of a delegate's id, for a delegate with no name to show.
export function shortId(id) {
  return cleanLabel(id).replace(/[^A-Za-z0-9_-]/g, '').slice(0, 8);
}

const segmenter = typeof Intl !== 'undefined' && Intl.Segmenter ? new Intl.Segmenter(undefined, { granularity: 'grapheme' }) : null;
const graphemes = (s) => (segmenter ? Array.from(segmenter.segment(s), (g) => g.segment) : Array.from(s));

// Letters other scripts and forms draw as the Latin ones of "main agent" (from Unicode's
// confusables), as the Latin letter. NFKD has already folded fullwidth and other compatibility
// forms, and accents are gone. l, 1 and | pass for i in many faces.
const LOOKALIKES = new Map(Object.entries({
  m: '\u041c\u043c\u039c\u03fa\u1d0d\u04cd\u04ce\u13b7\u15f0\ua4df\u2c98',
  a: '\u0410\u0430\u0391\u03b1\u0251\u237a\u1d00\u13aa\u15c5\ua4ee',
  i: '\u0406\u0456\u0399\u03b9\u0131\u0269\u026a\u04c0\u04cf\u2373\u13a5\ua647\u01c0\ua4f2\u2c92l1|',
  n: '\u039d\u0578\u057c\u0274\ua4e0\u2c9a',
  g: '\u0261\u0262\u0581\u050c\u13c0\ua4d6\u018d',
  e: '\u0415\u0435\u0404\u0454\u0395\u03b5\u04bd\u212e\u1d07\u13ac\ua4f0',
  t: '\u0422\u0442\u03a4\u03c4\u1d1b\u13a2\ua4d4\u22a4\u2ca6',
}).flatMap(([latin, like]) => Array.from(like, (c) => [c, latin])));

// Whether a cleaned name reads as the main agent's: "main" then a word starting "agent", or a
// word starting "mainagent", anywhere in it ("Main agent", "MAIN-AGENT asks", "Ask main
// agent"), as the app's DelegateLabel decides. Before the words are compared, compatibility
// forms are folded (NFKD), accents and case dropped, lookalike letters read as the Latin ones
// and "rn" as "m". A word that mixes Latin letters with others is how a lookalike the table
// misses would be used, so there each other letter can stand for any.
export function posesAsMainAgent(name) {
  const folded = Array.from(String(name ?? '').normalize('NFKD').replace(/\p{M}/gu, ''), (c) => LOOKALIKES.get(c) ?? c)
    .join('').toLowerCase().replace(/rn/g, 'm');
  const words = folded.split(/[^\p{L}\p{N}]+/u).filter(Boolean).map((w) => {
    const chars = Array.from(w);
    const mixed = chars.some((c) => /[a-z]/.test(c)) && chars.some((c) => /[^\x00-\x7f]/.test(c));
    return chars.map((c) => (mixed && /[^\x00-\x7f]/.test(c) ? null : c));
  });
  const starts = (w, s) => !!w && w.length >= s.length && Array.from(s).every((c, i) => w[i] === null || w[i] === c);
  return words.some((w, i) => starts(w, 'mainagent') || (w.length === 4 && starts(w, 'main') && starts(words[i + 1], 'agent')));
}

export function delegateName(agent) {
  const name = cleanLabel(agent?.name).replace(DOTS, '-');
  if (!name) {
    const id = shortId(agent?.id);
    return id ? `Delegate ${id}` : 'Delegate';
  }
  const chars = graphemes(name);
  const shown = chars.length > NAME_MAX ? `${chars.slice(0, NAME_MAX - 1).join('').trimEnd()}\u2026` : name;
  return posesAsMainAgent(name) ? `Delegate \u201c${shown.replace(QUOTES, "'")}\u201d` : shown;
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
