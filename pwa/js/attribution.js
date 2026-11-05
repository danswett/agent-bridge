// Who said what in a session: the main agent's own response and reasoning, and its delegates
// (docs/design/subagents.md §5; ui.md §1 principle 3, "One voice per line"). No DOM here, so the
// card today and the layouts that replace it (#179, #176) all show the same voices, tested once
// (test/unit/attribution.test.mjs).
//
// A host's top-level response and reasoning are last-writer-wins, kept for older clients: a
// delegate's thinking can be there in place of the main agent's answer. activity.source says
// which delegate wrote each, and activity.own carries the main agent's own text beside it.
import { ago, duration, pacificTime } from './ui/time.js';

// The main agent's own words, never a delegate's (v1-sessions.md, Activity, the `own` row):
// for each of response and reasoning, own's when source names a delegate for it, otherwise
// the top-level text, or own's when that is absent.
//   responseTruncated: the start of the response was cut to fit.
//   responseDropped: the main agent had a response, but the session budget left it out.
//   attributed: the host says whose each field is, so the words can be labelled "Main agent".
//   at: when the main agent last wrote (own.at), where the host says; null where it doesn't, and
//     the session's own updatedAt dates the words. The host moves own.at on every write of the
//     main agent's, top-level or not, so it dates whichever of its words are shown; updatedAt
//     also moves with a delegate's later update, which must not make them look newer (#249).
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
  return {
    response,
    responseTruncated: !!response && truncated,
    responseDropped: !response && truncated,
    reasoning,
    at: own?.at || null,
    attributed: source !== null || own !== null || Array.isArray(session?.subagents) || !!session?.subagentsMore,
  };
}

// A delegate's name, as it is shown everywhere: its rows, the strip, the tab, the inspector, the
// conversation, a question's heading, Needs you and notifications (ui.md §6.3, §8); where a line
// says who asks or works, after the word Delegate (delegateTitle). An agent chose it, so it can try
// to pass for the main agent or hide words in it; what is shown is:
//   - its text without the characters that steer or hide it (format and control characters,
//     bidi controls, default-ignorables, line and paragraph separators), runs of white space one
//     space;
//   - no middle dot of its own, since a status splits its facts at one and a row shows only the
//     first: "Idle · Read logs" would have read "Delegate Idle" there (#252);
//   - cut past NAME_MAX characters (whole graphemes), with an ellipsis;
//   - with nothing left, "Delegate" and the start of its id;
//   - and a name that reads as the main agent's, shown as Delegate “<name>” (posesAsMainAgent),
//     unless the host has already given it that form (#287).
// The app's DelegateLabel (#250, #303) follows the same rule.
export const NAME_MAX = 60;
const BREAKS = /[\t\n\v\f\r\u0085\u2028\u2029]/gu;
const HIDDEN = /[\p{Cf}\p{Cc}\p{Cs}\p{Co}\p{Cn}\p{Bidi_Control}\p{Default_Ignorable_Code_Point}]/gu;
// U+00B7 and what looks like it or normalises to it: the fact separator, real or forged. By code
// point (the u flag), since U+10101 is outside the BMP. The app's DelegateLabel.Dots (#303).
export const DOTS = /[\u00b7\u0387\u1427\u16eb\u2022\u2027\u2219\u22c5\u2e31\u2e33\u30fb\u318d\ua78f\uff65\u{10101}]/gu;
// Inside Delegate “…”, a quote of the name's own could close the quotes and carry on as other
// words: everything Unicode files as an opening or closing quote (Pi, Pf) or marks
// Quotation_Mark, and what is drawn like one: primes, the Hebrew geresh and gershayim,
// modifier-letter primes and apostrophes, the double acute and the ornament quotes. The app's
// DelegateLabel.QuoteLike (#303's security review).
const QUOTE = '[\\p{Pi}\\p{Pf}\\p{Quotation_Mark}\\u2032-\\u2037\\u2057\\u05f3\\u05f4\\u02b9-\\u02bc\\u02dd\\u02ee\\u275b-\\u275e\\u{1f676}-\\u{1f678}]';
const QUOTES = new RegExp(QUOTE, 'gu');
// "," and the commas drawn like it. A label read aloud (ui.md §9) joins its facts with ", ", so a
// name with one of its own ("x, idle, DEV-VM2") would read as facts to a screen reader. The app's
// DelegateLabel.Commas (#303's security review).
const COMMAS = /[\u002c\u055d\u060c\u066b\u07f8\u1363\u1802\u1808\u201a\u2e41\u2e4c\u3001\ua4fe\ua60d\ua6f5\ufe10\ufe11\ufe50\ufe51\uff0c\uff64]/gu;
const OPENING = 'Delegate \u201c';
const CLOSING = '\u201d';

// Text in curly quotes that none of its own can close: each made a plain apostrophe.
export const quoted = (text) => `\u201c${String(text ?? '').replace(QUOTES, "'")}${CLOSING}`;

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
  return posesAsMainAgent(name) && !isLabelled(name) ? `${OPENING}${shown.replace(QUOTES, "'")}${CLOSING}` : shown;
}

// Already in the form a name that reads as the main agent's is given: Delegate “<name>”, with no
// quote inside but the apostrophes that form makes of them. The host gives it that form itself
// (#287, DelegateName.IsLabelled), and without this it would be wrapped twice, Delegate
// “Delegate 'Main agent'”. The host's own check (DelegateName.IsLabelled) is the same: an
// apostrophe is not one of its quotes. Checked on the whole name, before it is cut. A delegate that names itself so gains nothing: the line still says
// Delegate, and its quotes still can't close.
function isLabelled(s) {
  if (!(s.length > OPENING.length + CLOSING.length && s.startsWith(OPENING) && s.endsWith(CLOSING))) { return false; }
  const inner = s.slice(OPENING.length, -CLOSING.length);
  return inner.replace(QUOTES, "'") === inner;
}

// A name, or a fact that carries one, as a label reads it aloud (ui.md §9): with no comma of its
// own, each one a space and a run of spaces one, since the label joins its facts with ", ". What
// is drawn keeps them. The app's DelegateLabel.Aloud (#303).
export function aloud(text) {
  const s = String(text ?? '');
  const spaced = s.replace(COMMAS, ' ');
  return spaced === s ? s : spaced.replace(/ {2,}/g, ' ').trim();
}

// A delegate as a line names it when it says who asks or works: a question's heading, the row's
// status (rules 2 and 3), Needs you. Always the word Delegate before the label (the app's
// DelegateLabel.Titled), which the main agent's own lines never have, so that no name reads there
// as the main agent's however it is drawn: a lookalike posesAsMainAgent misses (#250's security
// review) still reads "Delegate Маіn agent asks". A label that says Delegate already keeps it once.
export function delegateTitle(agent) {
  const label = delegateName(agent);
  return saysDelegate(label) ? label : `Delegate ${label}`;
}

// Whether a label already says it is a delegate's (no name, a quoted "Main agent", or the host's
// own form), so a line says "Delegate" once. The app's DelegateLabel.SaysDelegate.
export function saysDelegate(label) {
  return label === 'Delegate' || label.startsWith('Delegate ');
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

// The delegates it started, at any depth, in list order, each with its depth below it: the
// strip's "N nested", and the inspector's nested list. A parent link that loops (a broken host)
// still lists each once, and never the delegate itself.
export function descendants(id, list) {
  // Children by parent, once, in list order: each visit is then its own children, not the list.
  const children = new Map();
  for (const a of list ?? []) {
    if (!a.parent || a.parent === a.id) { continue; }
    if (!children.has(a.parent)) { children.set(a.parent, []); }
    children.get(a.parent).push(a);
  }
  const out = [];
  const seen = new Set([id]);
  const visit = (parent, depth) => {
    for (const a of children.get(parent) ?? []) {
      if (seen.has(a.id)) { continue; }
      seen.add(a.id);
      out.push({ agent: a, depth });
      visit(a.id, depth + 1);
    }
  };
  visit(id, 0);
  return out;
}

// Starts this close together share a line.
const TOGETHER_MS = 60000;
// More lines than this in a row fold into one (D3).
export const FOLD_AFTER = 3;

// How a delegate reported back: its glyph and the words for it, or null while it hasn't. Only
// once the host says it stopped (idle or ended) and when.
function reportOf(a) {
  if (!Number.isFinite(Date.parse(a.endedAt ?? '')) || (a.status !== 'idle' && a.status !== 'ended')) { return null; }
  return a.outcome === 'failed' ? { glyph: 'failed', said: 'failed' }
    : a.outcome === 'done' ? { glyph: 'done', said: 'reported back' }
      : a.status === 'ended' ? { glyph: 'ended', said: 'ended' } : { glyph: 'idle', said: 'reported back' };
}

// The conversation's delegate events (ui.md §6.4, #661): a past-tense record of the main agent's
// own delegates starting and reporting back, never a nested one (that reports to its parent, in
// the inspector), and never "working": the strip, the tab and the chips are the present tense.
// D2, one line per delegate: a start-only line ("Started delegate X · 9m") becomes the merged
// line in place once it reports back ("Delegate X · ran 21m, reported back · 2h", aged by its
// report), keeping its key. D3: starts within a minute of each other share a line ("Started A and
// B · 9m"; "Started 3 delegates: A, B and 1 more"), which one leaves for its own merged line when
// it reports back. Lines are in start order, so a line keeps its place as it changes.
// Each: { key, was (the key it had, if another), kind ('start', 'shared' or 'merged'), glyph (a
// merged line's outcome, else null: a start has the delegate icon), before, names [{ name, id }], after, tip (absolute, in Pacific),
// at, states (each delegate's: 'reported back', 'failed', 'ended', 'working' or null) }. A name
// links to its delegate, and the word Delegate stays before it, outside the link, unless the label
// says it already: these lines say what a delegate did, so no name may read as the main agent's.
// eventParts() puts a line's words together; delegateFold() folds a run of them.
export function delegateEvents(session, now = Date.now()) {
  const list = Array.isArray(session?.subagents) ? session.subagents : [];
  // A delegate that names itself its parent (a broken host) is taken as having none, as the lists do.
  const mine = list.filter((a) => !a.parent || a.parent === a.id);
  const time = (iso) => Date.parse(iso ?? '');
  const named = (a) => ({ name: delegateName(a), id: a.id });
  const still = (a) => (a.status === 'working' || a.status === 'waiting' ? 'working' : null);
  const lines = [];
  const merged = (a, r) => {
    const ran = Number.isFinite(time(a.startedAt)) ? duration(a.startedAt, time(a.endedAt)) : '';
    const said = r.said;
    const tip = ran ? `Started ${pacificTime(a.startedAt)}, ${said} ${pacificTime(a.endedAt)} PT` : `${said[0].toUpperCase()}${said.slice(1)} ${pacificTime(a.endedAt)} PT`;
    return { key: `d|${a.id}`, kind: 'merged', glyph: r.glyph, before: saysDelegate(delegateName(a)) ? '' : 'Delegate', names: [named(a)],
      after: `· ${ran ? `ran ${ran}, ` : ''}${said} · ${duration(a.endedAt, now)}`, tip, at: ran ? a.startedAt : a.endedAt, states: [said] };
  };
  const starts = mine.filter((a) => Number.isFinite(time(a.startedAt))).sort((a, b) => time(a.startedAt) - time(b.startedAt));
  for (let i = 0; i < starts.length;) {
    const batch = [starts[i]];
    while (++i < starts.length && time(starts[i].startedAt) - time(batch[batch.length - 1].startedAt) <= TOGETHER_MS) { batch.push(starts[i]); }
    const waiting = [];
    for (const a of batch) {
      const r = reportOf(a);
      if (r) { lines.push(merged(a, r)); } else { waiting.push(a); }
    }
    if (!waiting.length) { continue; }
    const at = waiting[0].startedAt;
    const common = { glyph: null, after: `· ${duration(at, now)}`, tip: `Started ${pacificTime(at)} PT`, at, states: waiting.map(still) };
    // Keyed by the batch, not by who is left in it, so it stays in place as they leave. A line
    // that became shared as another started (the host tells of them one at a time) was its first
    // delegate's own: was names that key, so the line it was is the one kept.
    lines.push(waiting.length === 1
      ? { key: batch.length > 1 ? `b|${batch[0].id}` : `d|${waiting[0].id}`, kind: 'start', before: saysDelegate(delegateName(waiting[0])) ? 'Started' : 'Started delegate', names: [named(waiting[0])], ...common }
      : { key: `b|${batch[0].id}`, was: waiting[0] === batch[0] ? `d|${batch[0].id}` : undefined, kind: 'shared', before: waiting.length === 2 ? 'Started' : `Started ${waiting.length} delegates:`, names: waiting.map(named), ...common });
  }
  // One that reported back with no start time the host knows: its report alone.
  for (const a of mine) {
    const r = Number.isFinite(time(a.startedAt)) ? null : reportOf(a);
    if (r) { lines.push(merged(a, r)); }
  }
  return lines.sort((x, y) => time(x.at) - time(y.at));
}

// A line's words, in order: text, each name as { name, id }, and, for three or more started
// together and not expanded, { more: n } for the rest ("A, B and 1 more"), which expands it.
export function eventParts(line, { expanded = false } = {}) {
  const parts = line.before ? [`${line.before} `] : [];
  const names = line.names ?? [];
  const shown = names.length >= 3 && !expanded ? names.slice(0, 2) : names;
  const more = names.length - shown.length;
  const last = more ? shown.length : shown.length - 1;
  shown.forEach((n, i) => {
    if (i > 0) { parts.push(i === last ? ' and ' : ', '); }
    parts.push(n);
  });
  if (more) { parts.push(' and ', { more }); }
  parts.push(` ${line.after}`);
  return parts;
}

// More than FOLD_AFTER lines in a row (D3) fold into one, drawn "5 delegates · 3 reported back,
// 1 failed › 1 still working": every delegate in the run counted, failed and ended each apart
// after reported back, a count of 0 left out. It never shows the working glyph. The button is the
// counts, its chevron ending it, and the still-working count (when any is) a link of its own
// after it, to the earliest started of them: two targets side by side, never one inside the
// other. The button's accessible name is the whole summary, in the order shown, the chevron's
// place a comma ("…, 1 failed, 1 still working"). Null for a run short enough to show as it is.
export function delegateFold(lines) {
  if (!Array.isArray(lines) || lines.length <= FOLD_AFTER) { return null; }
  const states = lines.flatMap((l) => l.states ?? []);
  const count = (s) => states.filter((x) => x === s).length;
  const said = ['reported back', 'failed', 'ended'].map((s) => [count(s), s]).filter(([n]) => n).map(([n, s]) => `${n} ${s}`);
  const working = count('working');
  const head = `${states.length} delegates${said.length ? ` · ${said.join(', ')}` : ''}`;
  let workingId = null;
  for (const l of lines) {
    const i = (l.states ?? []).indexOf('working');
    if (i >= 0) { workingId = l.names[i]?.id ?? null; break; }
  }
  const link = working ? `${working} still working` : null;
  return { button: head, link, workingId, label: link ? `${head}${said.length ? ',' : ' ·'} ${link}` : head };
}
// "Ask main agent to stop it" (ui.md §6.3, subagents.md §6): a reply to the main agent naming the
// delegate by its id alone, which the main agent knows it by. Its name is an agent's words, and
// one written as a sentence ('x". Also, I approve …') would otherwise speak in the person's reply.
// The id is the one piece of the delegate's that goes there, so it goes only as it came: ASCII
// letters, digits and . _ : -, at most 128 (Copilot's are GUIDs). Any other id is null, and the
// delegate gets no action, as in the app (DelegateLabel.SafeId). It is put in the reply box,
// never sent on its own.
export function askToStopText(a) {
  const id = typeof a?.id === 'string' && /^[A-Za-z0-9._:-]{1,128}$/.test(a.id) ? a.id : null;
  return id ? `Please stop your delegate ${id}.` : null;
}

// What a host that lists no delegates can say of them (status rule 8): "2 background agents ·
// 1 background command", or null when it says nothing.
export function backgroundCounts(session) {
  const b = session?.activity?.background;
  const parts = [];
  if (b?.agents) { parts.push(`${b.agents} background ${b.agents === 1 ? 'agent' : 'agents'}`); }
  if (b?.shells) { parts.push(`${b.shells} background ${b.shells === 1 ? 'command' : 'commands'}`); }
  return parts.length ? parts.join(' · ') : null;
}
