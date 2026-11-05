// The receipt of a reply or an answer on its way (#600; ui.md §6.5, §5.3 and §9, as the owner
// decided them on #617 and revised them the same day to "quiet unless something's wrong").
//
// The normal path shows nothing but Send's own ring: Sending, Sent, Passed to the agent and Up
// next are heard, not seen. Delivered turns Send's ring to the check for about a second, and a
// confirmed answer resolves to "You answered · its value". A line of words appears only for an
// exception: a stall, a wait, a resend meeting the first copy, an outcome other than delivered.
// From Send the line keeps its room, empty, so neither an exception nor the outcome moves
// anything.
//
// The stages are the machine's (sessions.progress); nothing here invents a stage nobody
// reported. Nothing is shown or spoken for the first 400 ms; after that a screen reader hears the
// furthest stage reached, at most one a second (the later of two), and an outcome at once.
//
// ReceiptClock does the timing and holds no DOM, so the unit tests drive it with a mocked clock;
// drawReceipt and holdCheck draw what it decides.

import { h } from './dom.js';
import { designIcon } from './glyph.js';
import { enter, leave } from './motion.js';
import { tapToAnswer } from '../answers.js';
import { reveal } from '../hidden-text.js';

// In one place so they can be tuned once the owner has seen them live (ui.md §6.5).
export const ReceiptTiming = Object.freeze({
  // Nothing shown or spoken for this long after Send; Send's ring covers it.
  quietMs: 400,
  // Nothing heard from the machine: "Still sending…".
  stillSendingMs: 3000,
  // Sent, and not passed to the agent: "· still waiting on machine".
  sentStallMs: 5000,
  // Passed to an idle agent, and not picked up: "· not picked up yet". Up next has none: the
  // agent is busy, and waiting is expected.
  passedStallMs: 10000,
  // At most one announcement this often; of two inside it, only the later is spoken.
  announceGapMs: 1000,
  // Delivered: Send shows the check this long, then its own glyph again (sendCheckHold).
  sendCheckHoldMs: 1000,
});

const machine = (name) => name || 'the machine';
const uploading = (entry) => !!entry.progress && entry.progress.total > 0 && entry.progress.sent < entry.progress.total;

// A state heard but not seen (the normal path), and one with a line (an exception).
const heardOnly = (key, speech) => ({ key, text: '', more: null, tone: 'room', line: false, speech });
const lineOf = (key, text, more, speech, extra = {}) => ({ key, text, more, tone: 'progress', line: true, speech, ...extra });

// The words of an answer or a reply still on its way, by what its entry says (answer-service.js):
// { key, text, more, tone, line, speech, still }. line: whether it shows (an exception) or is only
// heard. more: the secondary words after " · ". still: Send's ring stands still (nothing moves
// while the machine isn't connected).
export function describeInFlight(kind, entry, { machineName = null, stalled = false } = {}) {
  const subject = kind === 'answer' ? 'Answer' : 'Reply';
  const m = machine(machineName);
  // An answer never carries working (a question holds the session): there is no Up next for one.
  const stage = kind === 'answer' && entry.stage === 'queued' ? 'typed' : entry.stage;
  if (entry.state === 'waiting') {
    // Held on this device, with no channel to the machine.
    if (entry.reason === 'not connected') {
      return lineOf('held', `Waiting for ${m}`, `it goes when ${m} is back`, `${subject} waiting for ${m}`, { still: true });
    }
    // A resend met the first copy still in flight: the stage it had reached stays.
    if (entry.reason === 'still being delivered') {
      return lineOf(`${stage ?? 'sending'}+delivering`, STAGE_TEXT[stage] ?? 'Sending…', 'still being delivered', `${subject} still being delivered`);
    }
    // Connected, but no answer came back (a lost response, a timeout): it goes again.
    return lineOf('retrying', 'Still sending…', null, `${subject} still sending`);
  }
  if (uploading(entry)) { return null; }
  switch (stage) {
    case 'received':
      return stalled ? lineOf('received', 'Sent', `still waiting on ${m}`, `${subject} sent, still waiting on ${m}`) : heardOnly('received', `${subject} sent`);
    case 'typed':
      return stalled
        ? lineOf('typed', 'Passed to the agent', 'not picked up yet', `${subject} passed to the agent, not picked up yet`)
        : heardOnly('typed', `${subject} passed to the agent`);
    case 'queued':
      return heardOnly('queued', `${subject} up next, after the agent's current step`);
    default:
      return stalled ? lineOf('sending', 'Still sending…', null, `${subject} still sending`) : heardOnly('sending', `Sending ${subject.toLowerCase()}`);
  }
}

const STAGE_TEXT = { received: 'Sent', typed: 'Passed to the agent', queued: 'Up next' };

// What a confirmed answer says it was, after "You answered · " (ui.md §6.5): a tap question's
// option by its label; free text by its first line, its runs of white space as one space, cut at
// the last word break within 40 characters (or at 40, with none after the 20th) and ended with
// "…" when longer; nothing for a form of several fields. Its hidden and bidi characters are drawn
// as escapes (#546), and drawReceipt isolates it in its own direction.
const VALUE_MAX = 40;
const VALUE_BREAK_MIN = 20;
export function answeredValue(question, answer) {
  if (!question || !answer) { return null; }
  if (typeof answer.text === 'string') { return firstLine(answer.text); }
  const fields = answer.fields ?? {};
  if (question.fields.length !== 1) { return null; }
  const field = question.fields[0];
  const v = fields[field.name];
  if (tapToAnswer(question)) {
    if (typeof v === 'string') {
      const label = field.options.find((o) => o.value === v)?.label ?? v;
      return label.trim() ? reveal(label.trim()) : null;
    }
    if (v && typeof v.other === 'string') { return firstLine(v.other); }
    return null;
  }
  return typeof v === 'string' ? firstLine(v) : null;
}

function firstLine(text) {
  const lines = String(text).trim().split(/\r\n|[\r\n\u0085\u2028\u2029]/);
  const chars = [...lines[0].replace(/\s+/gu, ' ').trim()];
  if (chars.length === 0) { return null; }
  let shown = chars;
  if (chars.length > VALUE_MAX) {
    const at = chars.lastIndexOf(' ', VALUE_MAX);
    shown = chars.slice(0, at >= VALUE_BREAK_MIN ? at : VALUE_MAX);
  }
  const words = shown.join('').trimEnd();
  return reveal(shown.length < chars.length || lines.length > 1 ? `${words}…` : words);
}

// The whole line as one string: the words, then the secondary words after " · ".
export const receiptText = (d) => (d?.line ? (d.more ? `${d.text} · ${d.more}` : d.text) : '');

// What a request's receipt has been through, by request, kept apart from any one view: a question
// card is drawn afresh on each update of its session, and the quiet window, the stalls and what
// was announced belong to the request, not to the drawing. So does its announcement's timer: one
// still waiting out the rate limit when its view goes is said all the same, by another view of
// the request on screen (voices), or else on the page's own line, never lost and never said on a
// line now showing another request. said: what has been spoken; pending: what is to be, once the
// limit allows.
const KEEP = 50;
const histories = new Map();
function historyFor(request, now) {
  let h = histories.get(request);
  if (!h) {
    h = { start: now, stage: null, stageAt: now, sendingSince: null, delivering: false, said: new Set(), pending: null, pendingKey: null, saidAt: -Infinity, sayTimer: null, voices: new Set() };
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

// at once: an outcome, said now whatever was said in the last second, in place of anything
// still waiting. alert: a failure, said assertively on the page's alert line (ui.md §9.1 A6).
// key: what makes it news, its words unless given: an outcome's is its attempt's, so Try again
// failing the same way is heard again, while a view drawn afresh never says it twice.
function speak(h, text, now, gapMs, atOnce, alert = false, key = text) {
  if (!text || h.said.has(key)) {
    // The request is back in a state already said, or one with nothing to say: what was waiting
    // was about a state it has left (a disconnect's "waiting for" after the connection is back).
    clearTimeout(h.sayTimer);
    h.sayTimer = null;
    h.pending = null;
    return;
  }
  h.pending = text;
  h.pendingKey = key;
  if (atOnce) {
    clearTimeout(h.sayTimer);
    h.sayTimer = null;
    sayNow(h, now, alert);
    return;
  }
  const wait = h.saidAt + gapMs - now();
  if (wait <= 0) { sayNow(h, now); } else if (!h.sayTimer) { h.sayTimer = setTimeout(() => { h.sayTimer = null; sayNow(h, now); }, wait); }
}

function sayNow(h, now, alert = false) {
  if (h.pending === null) { return; }
  const text = h.pending;
  h.pending = null;
  h.said.add(h.pendingKey ?? text);
  h.pendingKey = null;
  h.saidAt = now();
  if (alert) {
    sayAlert(text);
    return;
  }
  const voice = [...h.voices].find((v) => v.heard());
  if (voice) { voice.say(text); } else { sayOnPage(text); }
}

// The page's own line, for an announcement no view of its request is on screen to say: a receipt
// gone with its question, a reply box moved on to the next reply, a session's card while another
// is open. Mounted empty as the app starts: a live region filled as it is inserted can be taken
// for its first content and not said. One made later (the page's body replaced) takes its words a
// moment after it is in.
let pageLine = null;
let alertLine = null;
function mountPageLine() {
  pageLine = receiptLive();
  alertLine = h('span', { class: 'sr-only receipt-alert', role: 'alert' });
  document.body.append(pageLine, alertLine);
}
// A failure, on the page's alert line: one line for the page, so it is said whichever view
// showed it, and only there, so it is never said twice (ui.md §9.1 A6).
function sayAlert(text) {
  if (typeof document === 'undefined') { return; }
  if (alertLine?.isConnected) {
    alertLine.textContent = text;
    return;
  }
  mountPageLine();
  const line = alertLine;
  setTimeout(() => { line.textContent = text; }, 100);
}
function sayOnPage(text) {
  if (typeof document === 'undefined') { return; }
  if (pageLine?.isConnected) {
    pageLine.textContent = text;
    return;
  }
  mountPageLine();
  const line = pageLine;
  setTimeout(() => { line.textContent = text; }, 100);
}

// The line's room, empty: from Send, and after a delivery that has nothing to say.
const ROOM = Object.freeze({ key: 'room', text: '', more: null, tone: 'room', line: false, speech: null });

// describe(entry, { stalled }): the words for the entry as it is now (describeReply,
// describeAnswer). draw(d): puts them on the line when they change; d is ROOM-like (no words,
// the room kept) or null (no line). say(text): the announcement, rate-limited here. heard():
// whether say is heard now: a view not drawn (a session's card while another is open) leaves the
// request's announcement to one that is, such as Needs you, or to the page's line. delivered():
// the request this view saw on its way has been delivered: Send's check (holdCheck).
export class ReceiptClock {
  #entry = null;
  #timer = null;
  #drawn = undefined;
  // The history of the request this clock shows, whose announcements it may say.
  #h = null;
  // The request this clock saw in flight, so a view drawn after its delivery never shows the check.
  #seen = null;

  constructor({ describe, draw, say = () => {}, heard = () => true, delivered = () => {}, now = () => Date.now(), timing = ReceiptTiming }) {
    Object.assign(this, { describe, draw, say, heard, delivered, now, timing });
  }

  show(entry) {
    this.#entry = entry ?? null;
    this.#tick();
  }

  destroy() {
    clearTimeout(this.#timer);
    this.#timer = null;
    this.#voice(null);
  }

  #voice(h) {
    if (h === this.#h) { return; }
    this.#h?.voices.delete(this);
    this.#h = h;
    h?.voices.add(this);
  }

  #tick() {
    clearTimeout(this.#timer);
    this.#timer = null;
    const entry = this.#entry;
    if (!entry) { this.#voice(null); this.#put(null); return; }
    const T = this.timing;
    const t = this.now();
    const h = historyFor(entry.request, t);
    this.#voice(h);
    if (entry.state !== 'sending' && entry.state !== 'waiting') {
      // An outcome is never held: shown and said at once, past any stage not yet said.
      const d = this.describe(entry, { stalled: false });
      this.#put(d.line ? d : ROOM);
      speak(h, d.speech, this.now, T.announceGapMs, true, d.tone === 'bad', `${entry.attempts ?? 0}\n${d.speech}`);
      if (entry.state === 'done' && entry.outcome === 'delivered' && this.#seen === entry.request) {
        this.#seen = null;
        this.delivered();
      }
      return;
    }
    this.#seen = entry.request;
    const stage = entry.stage ?? null;
    if (stage !== h.stage) { h.stage = stage; h.stageAt = t; h.delivering = false; }
    // A resend that met the first copy in flight goes again every few seconds, and is busy again
    // until the first copy settles: between, it still is being delivered, not newly sent, so the
    // line doesn't flicker between the two and say both each time.
    if (entry.state === 'waiting') { h.delivering = entry.reason === 'still being delivered'; }
    const now = h.delivering && entry.state === 'sending' ? { ...entry, state: 'waiting', reason: 'still being delivered' } : entry;
    if (now.state === 'sending' && !stage && !uploading(now)) { h.sendingSince ??= t; } else if (uploading(now)) { h.sendingSince = null; }
    const due = stallDue(now, h, T);
    const d = this.describe(now, { stalled: due !== null && t >= due });
    let wake = due !== null && t < due ? due : null;
    if (t < h.start + T.quietMs) {
      this.#put(ROOM);
      wake = h.start + T.quietMs;
    } else {
      this.#put(d.line ? d : ROOM);
      speak(h, d.speech, this.now, T.announceGapMs, false);
    }
    if (wake !== null) { this.#timer = setTimeout(() => this.#tick(), Math.max(0, wake - t)); }
  }

  #put(d) {
    // By request too: a view may take a line's room away (the reply box, typed in), and the next
    // request must put it back even where the words are the same.
    const signature = d ? `${this.#entry?.request ?? ''}|${d.tone}|${receiptText(d)}` : null;
    if (signature !== this.#drawn) {
      this.#drawn = signature;
      this.draw(d);
    }
  }
}

// Draws a receipt into its line: words only (Send's ring and check say the rest). A line that
// appears fades in place with acknowledge, and one that goes fades out in place, which the motion
// tokens turn into a cross-fade under reduced motion (ui.md §5.3); the room stays either way.
// enters: false for a line whose entrance is its own (a card's receipt enters as it is put in
// place). d null: no line, and no room.
export function drawReceipt(line, base, d, { enters = true } = {}) {
  const had = line.textContent !== '';
  if (had && enters) { leave(line, 'acknowledge'); }
  const parts = [];
  if (d?.line && d.text) { parts.push(h('span', { class: 'receipt-words' }, d.text)); }
  if (d?.line && d.more) { parts.push(h('span', { class: 'receipt-more' }, ' · ', h('bdi', {}, d.more))); }
  line.replaceChildren(...parts);
  line.className = `${base}${d?.tone ? ` ${d.tone}` : ''}`;
  if (enters && parts.length) { enter(line, 'acknowledge', { dy: 1 }); }
}

// Send's check (ui.md §5.3, §6.5): delivered, the button's ring gives way to the check, which
// holds sendCheckHold and gives way to the button's own glyph or words. The button never changes
// size or fill (app.css), and its name stays its own: "Reply delivered" is said on the receipt.
const checks = new WeakMap();
export function holdCheck(button, { holdMs = ReceiptTiming.sendCheckHoldMs } = {}) {
  if (!button?.isConnected) { return; }
  if (!button.querySelector(':scope > .send-check')) { button.append(designIcon('check', { cls: 'send-check' })); }
  button.classList.add('checked');
  clearTimeout(checks.get(button));
  checks.set(button, setTimeout(() => { button.classList.remove('checked'); checks.delete(button); }, holdMs));
}

// A new request from the same control: an earlier delivery's check gives way to its ring at once,
// so the new request never looks delivered.
export function dropCheck(button) {
  if (!button) { return; }
  clearTimeout(checks.get(button));
  checks.delete(button);
  button.classList.remove('checked');
}

// The line a screen reader hears the receipt on (ui.md §9): polite, and apart from the words on
// screen, so each change is said once, prefixed by what it is about.
export const receiptLive = () => h('span', { class: 'sr-only receipt-live', role: 'status', 'aria-live': 'polite' });

// Whether a live line is drawn, so what it says is heard: one inside a hidden view has no boxes.
export const drawn = (live) => live.getClientRects().length > 0;

// The page's line, there before anything is said on it (sayOnPage).
if (typeof document !== 'undefined' && document.body) { mountPageLine(); }
