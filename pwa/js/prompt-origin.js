// A prompt the person didn't type (#489, ui.md §6.4): a schedule's, another agent's, or one the
// host knows only as not the person's (activity.userMessageFrom). It is never drawn as the
// person's bubble, but as a line of its own in agent colour, collapsed; the person's own prompt,
// and every prompt from a host that doesn't send the field, is unchanged. No DOM here, so the
// rules are tested once (test/unit/prompt-origin.test.mjs); the app's PromptOrigin follows them.
import { DOTS, aloud, cleanLabel, quoted } from './attribution.js';
import { reveal } from './hidden-text.js';

// The protocol's cap on userMessageLabel (v1-sessions.md), held here too: an agent's text, from
// a host that might not.
export const LABEL_MAX_BYTES = 64;

const encoder = new TextEncoder();
const bytes = (s) => encoder.encode(s).length;
const segmenter = typeof Intl !== 'undefined' && Intl.Segmenter ? new Intl.Segmenter(undefined, { granularity: 'grapheme' }) : null;
const graphemes = (s) => (segmenter ? Array.from(segmenter.segment(s), (g) => g.segment) : Array.from(s));

// An agent's short text, drawn as a fact on a label line: nothing hidden or steering, on one
// line, and no middle dot of its own, which would add a fact ("4 · same as last time"); cut to
// max UTF-8 bytes at a whole grapheme, the ellipsis included.
export function promptFact(text, max = LABEL_MAX_BYTES) {
  const clean = cleanLabel(text).replace(DOTS, '-');
  if (bytes(clean) <= max) { return clean; }
  let out = '';
  for (const g of graphemes(clean)) {
    if (bytes(`${out}${g}\u2026`) > max) { break; }
    out += g;
  }
  return `${out.trimEnd()}\u2026`;
}

// A schedule's label as drawn: "#4" for a number, and anything else in quotes, so no label of an
// agent's reads as words of the line's own ("Scheduled prompt · “Main agent”"), whatever letters
// it draws them with.
function scheduleName(raw) {
  const label = promptFact(raw);
  if (!label) { return null; }
  return /^\d+$/.test(label) ? `#${label}` : quoted(label);
}

// Who sent the session's latest prompt, when not the person, or null for the person's own (and
// for no prompt). repeat: the schedule sent the same text last time (PromptMemory).
//   from: 'schedule', 'agent' or 'other';
//   icon: 'clock' (a schedule) or 'driven' (the driven-badge diamond);
//   facts: the label line's words, in agent colour, joined with " · " only to be drawn;
//   by: secondary text after them (the agent driving, when the host knows), or null;
//   spoken: what the label reads as aloud, before the disclosure's words;
//   preview: the prompt on one line, for the two-line preview; text: all of it, for Show prompt.
// Every word an agent chose has gone through promptFact or reveal: no hidden or bidi character
// is drawn as nothing, and no label can read as the person's or the main agent's.
export function promptOrigin(session, { repeat = false } = {}) {
  const a = session?.activity ?? {};
  const from = a.userMessageFrom ?? 'person';
  if (from === 'person' || !a.userMessage) { return null; }
  const text = reveal(a.userMessage);
  const preview = text.replace(/\s+/gu, ' ').trim();
  if (from === 'schedule') {
    const name = scheduleName(a.userMessageLabel);
    return {
      from, icon: 'clock', by: null, repeat, text, preview,
      facts: ['Scheduled prompt', name, repeat ? 'same as last time' : null].filter(Boolean),
      spoken: aloud(name ? `Scheduled prompt ${name.replace(/^#/, '')}` : 'Scheduled prompt'),
    };
  }
  const by = from === 'agent' && session.driver === 'agent' && session.driverPrincipal ? promptFact(session.driverPrincipal, 128) || null : null;
  return from === 'agent'
    ? { from, icon: 'driven', by, repeat: false, text, preview, facts: ['Sent by an agent'], spoken: 'Prompt sent by an agent' }
    : { from: 'other', icon: 'driven', by: null, repeat: false, text, preview, facts: ['Automatic prompt'], spoken: 'Automatic prompt' };
}

// The disclosure's name (ui.md §9): whose prompt, then the words on it, "Scheduled prompt 4, Show
// prompt", and aria-expanded says collapsed or expanded after it. The words shown stay in the name
// (WCAG 2.5.3, label in name), so speech input can say "Show prompt", and the state is said once.
export const disclosureName = (origin, open) => `${origin.spoken}, ${open ? 'Hide prompt' : 'Show prompt'}`;

// A finished turn count (v1-sessions.md turnCount) read only while the session is idle, where it is
// every prompt sent. While a turn runs it can be one less or not (a turn no prompt started, such as
// Claude's for a delegate reporting back, takes none off), and so can't say a prompt was sent.
function idleTurns(s) {
  const n = s?.activity?.turnCount;
  return s?.status === 'idle' && Number.isSafeInteger(n) && n >= 0 ? n : null;
}

// What a session's view remembers of its prompts, to fold a schedule's repeats and date them.
// A session carries only its latest prompt, so "the same as last time" is what this view saw.
// A new prompt is one whose words or sender changed, or, with the same words, a finished turn more
// than this view saw once that prompt had settled (been seen idle, its own turn counted). So a
// schedule sending the same text again shows once its turn is done, since a turn of the same
// prompt (an agent's reply to a delegate reporting back) looks the same while it runs; and turns
// that end before a prompt has settled are never put down to it, since they can be its own or an
// earlier prompt's whose end this view missed. It repeats when its schedule's previous prompt,
// seen here, had the same text. Seen for the first time, nothing is folded: a wrong "same as last
// time" would hide a prompt that changed. Its age is when its turn began (statusSince when it
// started running, seen here); a prompt first seen after its turn has no age to show.
export class PromptMemory {
  // The prompt shown: { key, repeat, at, settled }.
  #current = null;
  // The most turns seen finished, while idle; whether the session ran when last seen; the turn
  // count last seen, in any state; and the turns seen beginning, each with the count it ran at,
  // until a turn's end is accounted for.
  #idle = null;
  #running = false;
  #count = null;
  #starts = [];
  #bySchedule = new Map();

  // { repeat, at, fresh } for the session's latest prompt; fresh: it is a new one since the last.
  observe(s) {
    const a = s?.activity ?? {};
    const from = a.userMessageFrom ?? 'person';
    const text = a.userMessage ?? null;
    const running = s?.status === 'working' || s?.status === 'waiting';
    const count = Number.isSafeInteger(a.turnCount) && a.turnCount >= 0 ? a.turnCount : null;
    // A turn begins when the session starts running, not at a question answered or a resume
    // within it; and when the count rises while it runs, its idle between missed here. That later
    // turn can be a prompt's or one no prompt started (Claude's for a notice), which only the count
    // at its end tells apart.
    const later = this.#running && count !== null && this.#count !== null && count > this.#count;
    if (running && (!this.#running || later)) { this.#starts = [...this.#starts.slice(-15), { count, at: s.statusSince ?? null }]; }
    this.#running = running;
    if (count !== null) { this.#count = count; }
    // A host that omits the words (SessionBudget can) says nothing of which prompt this is: the one
    // shown stays it, and the turns that end meanwhile wait to be put down to the words that return.
    if (text === null) { return { repeat: false, at: null, fresh: false }; }
    const turns = idleTurns(s);
    let more = 0;
    if (turns !== null) {
      if (this.#idle !== null) { more = Math.max(0, turns - this.#idle); }
      this.#idle = Math.max(this.#idle ?? -1, turns);
    }
    // The latest prompt's turn ran one short of every prompt counted at its end; a turn seen at any
    // other count wasn't its, and with none, its start wasn't seen.
    const ran = turns === null ? null : this.#starts.filter((t) => t.count === turns - 1).at(-1)?.at ?? null;
    if (turns !== null) { this.#starts = []; }
    // Apart, not joined: a label can hold a line break, so no separator keeps two prompts apart.
    const key = JSON.stringify([from, a.userMessageLabel ?? null, text]);
    const cur = this.#current;
    let at;
    let settled;
    if (!cur || cur.key !== key) {
      at = running ? s.statusSince ?? null : null;
      // Seen idle, its own turn is among those counted.
      settled = turns !== null;
    } else {
      if (!cur.settled || more === 0) {
        if (turns !== null) { cur.settled = true; }
        return { repeat: cur.repeat, at: cur.at, fresh: false };
      }
      // The same words sent again, its turn over now: dated by when that turn began.
      at = ran;
      settled = true;
    }
    let repeat = false;
    if (from === 'schedule') {
      // By the label as drawn: two an agent wrote apart but drawn alike are one to the person, and
      // "same as last time" means the words last shown under it.
      const label = JSON.stringify([from, scheduleName(a.userMessageLabel)]);
      repeat = this.#bySchedule.get(label) === text;
      this.#bySchedule.set(label, text);
    }
    this.#current = { key, repeat, at, settled };
    return { repeat, at, fresh: cur !== null };
  }
}
