// The receipt of a reply or an answer on its way (#600; ui.md §6.5, §6.4 and §9, as the owner
// decided them on #617): one line that changes its words in place from Send to the outcome.
//
//   Sending… → Sent → Passed to the agent (or, a reply to a working session, Up next · after
//   the agent's current step) → Delivered (an answer: You answered · its value)
//
// The stages are the machine's (sessions.progress); a machine without it goes from Sending… to
// the outcome, and nothing here invents a stage nobody reported. What it decides is when each
// shows: nothing for the first 400 ms, which Send's own ring covers, so a fast delivery never
// flickers through every stage; then the furthest stage reached, each in-between state held at
// least 400 ms so it can be read; an outcome at once. When the stages stop coming, the words say
// so (the stalls), and a screen reader hears each change once, at most one a second.
//
// ReceiptClock does the timing and holds no DOM, so the unit tests drive it with a mocked clock;
// drawReceipt draws what it decides.

import { h } from './dom.js';
import { checkBadge } from './glyph.js';
import { enter } from './motion.js';
import { tapToAnswer } from '../answers.js';
import { reveal } from '../hidden-text.js';

// In one place so they can be tuned once the owner has seen them live (#617, "Per platform").
export const ReceiptTiming = Object.freeze({
  // Nothing on the line for this long after Send.
  quietMs: 400,
  // An in-between state stays at least this long before the next replaces it.
  dwellMs: 400,
  // Nothing heard from the machine: "Still sending…".
  stillSendingMs: 3000,
  // Sent, and not passed to the agent: "· still waiting on machine".
  sentStallMs: 5000,
  // Passed to an idle agent, and not picked up: "· not picked up yet". Up next has none: the
  // agent is busy, and waiting is expected.
  passedStallMs: 10000,
  // At most one announcement this often; of two inside it, only the later is spoken.
  announceGapMs: 1000,
});

const machine = (name) => name || 'the machine';
const uploading = (entry) => !!entry.progress && entry.progress.total > 0 && entry.progress.sent < entry.progress.total;

// The words of an answer or a reply still on its way, by what its entry says (answer-service.js):
// { key, text, more, tone, mark, speech }. key: the state, which the dwell holds apart (a stall
// changes the words, not the state). more: the secondary words after " · ". tone: the line's
// class. mark: 'ring', 'still' (a ring that doesn't turn) or null. speech: what a screen reader
// hears, or null ("Sending…" isn't spoken: Send's busy state says it).
export function describeInFlight(kind, entry, { machineName = null, stalled = false, working = false } = {}) {
  const subject = kind === 'answer' ? 'Answer' : 'Reply';
  const m = machine(machineName);
  const busy = (text) => (kind === 'reply' && working ? `${text} The session is working, and takes it at its next step.` : text);
  // An answer never carries working (a question holds the session): there is no Up next for one.
  const stage = kind === 'answer' && entry.stage === 'queued' ? 'typed' : entry.stage;
  if (entry.state === 'waiting') {
    // Held on this device, with no channel to the machine: nothing is moving, so the ring is still.
    if (entry.reason === 'not connected') {
      return { key: 'held', text: `Waiting for ${m}`, more: `it goes when ${m} is back`, tone: 'progress held', mark: 'still', speech: `${subject} waiting for ${m}` };
    }
    // A resend met the first copy still in flight: the stage it had reached stays.
    if (entry.reason === 'still being delivered') {
      const at = STAGE_TEXT[stage] ?? 'Sending…';
      return { key: `${stage ?? 'sending'}+delivering`, text: at, more: 'still being delivered', tone: 'progress', mark: 'ring', speech: `${subject} still being delivered` };
    }
    // Connected, but no answer came back (a lost response, a timeout): it goes again.
    return { key: 'retrying', text: busy('Still sending…'), more: null, tone: 'progress', mark: 'ring', speech: `${subject} still sending` };
  }
  if (uploading(entry)) { return null; }
  switch (stage) {
    case 'received':
      return stalled
        ? { key: 'received', text: 'Sent', more: `still waiting on ${m}`, tone: 'progress', mark: 'ring', speech: `${subject} sent, still waiting on ${m}` }
        : { key: 'received', text: 'Sent', more: null, tone: 'progress', mark: 'ring', speech: `${subject} sent` };
    case 'typed':
      return stalled
        ? { key: 'typed', text: 'Passed to the agent', more: 'not picked up yet', tone: 'progress', mark: 'ring', speech: `${subject} passed to the agent, not picked up yet` }
        : { key: 'typed', text: 'Passed to the agent', more: null, tone: 'progress', mark: 'ring', speech: `${subject} passed to the agent` };
    case 'queued':
      return { key: 'queued', text: 'Up next', more: "after the agent's current step", tone: 'progress', mark: 'ring', speech: `${subject} up next, after the agent's current step` };
    default:
      return stalled
        ? { key: 'sending', text: busy('Still sending…'), more: null, tone: 'progress', mark: 'ring', speech: `${subject} still sending` }
        : { key: 'sending', text: busy('Sending…'), more: null, tone: 'progress', mark: 'ring', speech: null };
  }
}

const STAGE_TEXT = { received: 'Sent', typed: 'Passed to the agent', queued: 'Up next' };

// What a confirmed answer says it was, after "You answered · " (the owner's decision on #617): a
// tap question's option by its label; free text by its first line, cut to about 40 characters;
// nothing for a form of several fields. Drawn as text, its hidden and bidi characters as escapes
// (#546), so a value can't reorder or hide the words around it.
const VALUE_MAX = 40;
export function answeredValue(question, answer) {
  if (!question || !answer) { return null; }
  if (typeof answer.text === 'string') { return firstLine(answer.text); }
  const fields = answer.fields ?? {};
  if (question.fields.length !== 1) { return null; }
  const field = question.fields[0];
  const v = fields[field.name];
  if (tapToAnswer(question)) {
    if (typeof v === 'string') { return firstLine(field.options.find((o) => o.value === v)?.label ?? v); }
    if (v && typeof v.other === 'string') { return firstLine(v.other); }
    return null;
  }
  return typeof v === 'string' ? firstLine(v) : null;
}

function firstLine(text) {
  const lines = String(text).trim().split(/\r\n|[\r\n\u0085\u2028\u2029]/);
  const chars = [...lines[0].trim()];
  if (chars.length === 0) { return null; }
  const cut = chars.length > VALUE_MAX;
  const shown = cut ? chars.slice(0, VALUE_MAX - 1).join('').trimEnd() : chars.join('');
  return reveal(cut || lines.length > 1 ? `${shown}…` : shown);
}

// The whole line as one string: the words, then the secondary words after " · ".
export const receiptText = (d) => (d ? (d.more ? `${d.text} · ${d.more}` : d.text) : '');

// What a request's receipt has been through, by request, kept apart from any one view: a question
// card is drawn afresh on each update of its session, and the quiet window, the dwell, the stalls
// and what was announced belong to the request, not to the drawing.
const KEEP = 50;
const histories = new Map();
function historyFor(request, now) {
  let h = histories.get(request);
  if (!h) {
    h = { start: now, stage: null, stageAt: now, sendingSince: null, shown: null, said: null };
    histories.set(request, h);
    if (histories.size > KEEP) { histories.delete(histories.keys().next().value); }
  }
  return h;
}

// When the request's current state stalls, or null for one that doesn't.
function stallDue(entry, h, T) {
  if (entry.state !== 'sending' || uploading(entry)) { return null; }
  // A send that went again (a lost response, a reconnect) has been going a while: Still sending….
  if (!entry.stage) { return entry.attempts > 1 ? h.stageAt : h.sendingSince + T.stillSendingMs; }
  if (entry.stage === 'received') { return h.stageAt + T.sentStallMs; }
  if (entry.stage === 'typed') { return h.stageAt + T.passedStallMs; }
  return null;
}

const QUIET = Object.freeze({ key: 'quiet', text: '', more: null, tone: 'quiet', mark: null, speech: null });

// describe(entry, { stalled }): the words for the entry as it is now (describeReply,
// describeAnswer). draw(d): puts them on the line when they change, or clears it (d null).
// say(text): the announcement, rate-limited here.
export class ReceiptClock {
  #entry = null;
  #timer = null;
  #drawn = undefined;
  #pending = null;
  #sayTimer = null;
  #saidAt = -Infinity;

  constructor({ describe, draw, say = () => {}, now = () => Date.now(), timing = ReceiptTiming }) {
    Object.assign(this, { describe, draw, say, now, timing });
  }

  show(entry) {
    this.#entry = entry ?? null;
    this.#tick();
  }

  destroy() {
    clearTimeout(this.#timer);
    clearTimeout(this.#sayTimer);
    this.#timer = this.#sayTimer = null;
  }

  #tick() {
    clearTimeout(this.#timer);
    this.#timer = null;
    const entry = this.#entry;
    if (!entry) { this.#put(null, null); return; }
    const T = this.timing;
    const t = this.now();
    const h = historyFor(entry.request, t);
    if (entry.state !== 'sending' && entry.state !== 'waiting') {
      // An outcome is never held, and replaces whatever shows.
      this.#show(h, this.describe(entry, { stalled: false }), t);
      return;
    }
    const stage = entry.stage ?? null;
    if (stage !== h.stage) { h.stage = stage; h.stageAt = t; }
    if (entry.state === 'sending' && !stage && !uploading(entry)) { h.sendingSince ??= t; } else if (uploading(entry)) { h.sendingSince = null; }
    const due = stallDue(entry, h, T);
    const d = this.describe(entry, { stalled: due !== null && t >= due });
    let wake = due !== null && t < due ? due : null;
    if (t < h.start + T.quietMs) {
      this.#put(QUIET, null);
      wake = h.start + T.quietMs;
    } else if (h.shown && h.shown.key !== d.key && h.shown.inFlight && t < h.shown.at + T.dwellMs) {
      // The state showing hasn't been up long enough to read: it stays, then the furthest reached.
      this.#put(h.shown.d, null);
      wake = Math.min(wake ?? Infinity, h.shown.at + T.dwellMs);
    } else {
      this.#show(h, d, t);
    }
    if (wake !== null) { this.#timer = setTimeout(() => this.#tick(), Math.max(0, wake - t)); }
  }

  #show(h, d, t) {
    if (!h.shown || h.shown.key !== d.key) { h.shown = { key: d.key, at: t, d, inFlight: this.#entry.state === 'sending' || this.#entry.state === 'waiting' }; } else { h.shown.d = d; }
    this.#put(d, h);
  }

  #put(d, h) {
    const signature = d ? `${d.tone}|${d.mark}|${receiptText(d)}` : null;
    if (signature !== this.#drawn) {
      this.#drawn = signature;
      this.draw(d);
    }
    if (h && d?.speech && d.speech !== h.said) {
      h.said = d.speech;
      this.#announce(d.speech);
    }
  }

  #announce(text) {
    this.#pending = text;
    const wait = this.#saidAt + this.timing.announceGapMs - this.now();
    if (wait <= 0) { this.#flush(); } else if (!this.#sayTimer) { this.#sayTimer = setTimeout(() => this.#flush(), wait); }
  }

  #flush() {
    this.#sayTimer = null;
    if (this.#pending === null) { return; }
    const text = this.#pending;
    this.#pending = null;
    this.#saidAt = this.now();
    this.say(text);
  }
}

// Draws a receipt into its line: the ring (app.css, from the tone) or the check badge, the words,
// and the secondary words. A change of words enters with acknowledge, which the motion tokens
// turn into a cross-fade under reduced motion (ui.md §5.3); enters: false for a line whose
// entrance is its own (a card's receipt enters as it is put in place).
export function drawReceipt(line, base, d, { enters = true } = {}) {
  const parts = [];
  if (d?.mark === 'check') { parts.push(checkBadge({ size: 14 })); }
  if (d && d.text) { parts.push(h('span', { class: 'receipt-words' }, d.text)); }
  if (d?.more) { parts.push(h('span', { class: 'receipt-more' }, ` · ${d.more}`)); }
  line.replaceChildren(...parts);
  line.className = `${base}${d?.tone ? ` ${d.tone}` : ''}`;
  if (enters && d?.text) { enter(line, 'acknowledge', { dy: 1 }); }
}

// The line a screen reader hears the receipt on (ui.md §9): polite, and apart from the words on
// screen, so each change is said once, prefixed by what it is about.
export const receiptLive = () => h('span', { class: 'sr-only receipt-live', role: 'status', 'aria-live': 'polite' });
