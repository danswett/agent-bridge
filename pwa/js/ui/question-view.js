// A pending question. Answerable from the phone when the machine says it takes answers
// (`sessions.answer`) and the question arrived whole; otherwise shown read-only with what
// it asks and the shape of the answer it wants, and a pointer to the machine.
//
// The answer form is built for a thumb: every control is at least 44 px, a question with
// one single-choice field answers on a tap, and anything else has a Send button. Values are
// built and checked by ../answers.js before anything is sent, so the host never sees an
// answer it would refuse for its shape; ../answer-service.js sends it and resends it, with
// the same request, until there is an outcome.
import { h } from './dom.js';
import { BRAND } from '../brand.js';
import { UPDATING, holdKey, holdOf } from './hold.js';
import { renderMarkdown } from '../markdown.js';
import { ReceiptClock, answeredValue, describeInFlight, drawReceipt, drawn, holdCheck, receiptLive } from './receipt.js';
import { openConfirmSheet } from './escaped-sheet.js';
import { COMMAND_LIFETIME_S } from '../transfer.js';
import {
  AnswerReasons, Limits, NOT_SENT, draftText, initialState, isRecommended, optionTone, orderOptions, prepareAnswer,
  tapToAnswer, utf8Bytes,
} from '../answers.js';

const TYPE_LABEL = { choice: 'Choose one', multi: 'Choose any', boolean: 'Yes or no', text: 'Text answer', number: 'Number' };

// A default can be any JSON value; it is shown as text, never interpreted.
export function formatValue(v) {
  if (v === null || v === undefined) { return ''; }
  if (typeof v === 'string') { return v; }
  if (typeof v === 'boolean') { return v ? 'Yes' : 'No'; }
  if (typeof v === 'number') { return String(v); }
  try { return JSON.stringify(v); } catch { return ''; }
}

let seq = 0;
const where = (machineName) => (machineName ? ` on ${machineName}` : '');

// ---- read-only ----

function defaults(field) {
  const d = field.default;
  if (d === null || d === undefined) { return new Set(); }
  return new Set((Array.isArray(d) ? d : [d]).map((x) => (typeof x === 'string' ? x : formatValue(x))));
}

// A resolved question never shows a default: only what the person answered, with a check and the
// words "Your answer" so it never rests on colour (ui.md §6.4, Resolved).
function optionRow(label, { chosen, multi, other, resolved }) {
  if (resolved) {
    return h('li', { class: `opt${chosen ? ' chosen' : ''}${other ? ' other' : ''}` },
      h('span', { class: 'mark', 'aria-hidden': 'true' }, chosen ? '✓' : ''),
      h('span', { class: 'label' }, label),
      chosen ? h('span', { class: 'badge answered' }, 'Your answer') : null);
  }
  const mark = multi ? (chosen ? '☑' : '☐') : (chosen ? '◉' : '○');
  return h('li', { class: `opt${chosen ? ' chosen' : ''}${other ? ' other' : ''}` },
    h('span', { class: 'mark', 'aria-hidden': 'true' }, mark),
    h('span', { class: 'label' }, label),
    chosen ? h('span', { class: 'badge' }, 'default') : null);
}

// What was answered for one field: the values chosen and any typed words. Nothing for a question
// that closed without a record of an answer.
function answeredOf(field, answer) {
  const v = typeof answer?.text === 'string' ? answer.text : answer?.fields?.[field.name];
  const values = new Set();
  let other = null;
  for (const x of Array.isArray(v) ? v : [v]) {
    if (typeof x === 'string') { values.add(x); } else if (typeof x === 'number' || typeof x === 'boolean') { values.add(formatValue(x)); } else if (x && typeof x.other === 'string') { other = x.other.trim(); }
  }
  return { values, other };
}

// The field's title, required badge and kind of answer: spans, so it can be a legend.
function headParts(field, titleId) {
  return [
    h('span', { class: 'field-title', id: titleId }, field.title ?? field.name),
    field.required ? h('span', { class: 'badge req' }, 'required') : null,
    h('span', { class: 'field-type' }, TYPE_LABEL[field.type]),
  ];
}

function readOnlyField(field, idBase, resolved = null) {
  const titleId = `${idBase}-t`;
  const parts = [h('div', { class: 'field-head' }, headParts(field, titleId))];
  if (field.description) { parts.push(h('p', { class: 'field-desc' }, field.description)); }

  // A resolved question marks what was answered, never its defaults.
  const done = resolved ? answeredOf(field, resolved.answer) : null;
  const chosen = done ? done.values : defaults(field);
  if (field.type === 'choice' || field.type === 'multi') {
    const multi = field.type === 'multi';
    const list = h('ul', { class: 'opts', 'aria-labelledby': titleId });
    for (const o of field.options) {
      list.append(optionRow(o.label, { chosen: chosen.has(o.value), multi, resolved: !!resolved }));
    }
    const known = new Set(field.options.map((o) => o.value));
    const otherDefault = done ? done.other : [...chosen].find((v) => !known.has(v));
    if (field.allowOther) {
      list.append(optionRow(otherDefault ? `Other: ${otherDefault}` : 'Other (type your answer)', { chosen: !!otherDefault, multi, other: true, resolved: !!resolved }));
    }
    parts.push(list);
  } else if (field.type === 'boolean') {
    const d = done ? [...done.values][0] === 'Yes' ? true : [...done.values][0] === 'No' ? false : null : field.default;
    const list = h('ul', { class: 'opts inline', 'aria-labelledby': titleId });
    list.append(optionRow('Yes', { chosen: d === true, multi: false, resolved: !!resolved }));
    list.append(optionRow('No', { chosen: d === false, multi: false, resolved: !!resolved }));
    parts.push(list);
  } else if (done) {
    const said = done.other ?? [...done.values][0] ?? '';
    parts.push(h('div', { class: `answer-box${said ? '' : ' empty'}`, 'aria-labelledby': titleId },
      said ? [h('span', { class: 'badge answered' }, '✓ Your answer'), ' ', said] : (field.type === 'number' ? 'A number' : 'Free text')));
  } else {
    const d = formatValue(field.default);
    parts.push(h('div', { class: `answer-box${d ? '' : ' empty'}`, 'aria-labelledby': titleId },
      d ? [h('span', { class: 'badge' }, 'default'), ' ', d] : (field.type === 'number' ? 'A number' : 'Free text')));
  }
  return h('li', { class: `field field-${field.type}`, dataset: { field: field.name, type: field.type } }, parts);
}

// asker: who asks, as the conversation says it (ui.md §6.4): "Main agent asks", or the delegate's
// name. Without one (the Needs you lane), what kind of question it is.
function shell(q, idBase, answerable, asker = null) {
  const heading = asker ?? (q.kind === 'approval' ? 'Permission requested' : 'Waiting on you');
  const section = h('section', {
    class: `question${answerable ? ' answerable' : ''}`, 'aria-labelledby': `${idBase}-h`,
    dataset: { kind: q.kind, question: q.id, answerable: answerable ? 'true' : 'false' },
  }, h('h4', { class: 'question-head', id: `${idBase}-h` }, heading));
  const approval = q.kind === 'approval';
  // Copilot asked several at once and shows them one at a time, this one first (#690): each is
  // its own question, so an answer to this one never reaches the next.
  if (approval && q.waiting > 1) {
    section.append(h('p', { class: 'note queue', role: 'note' }, `1 of ${q.waiting} permissions waiting`));
  }
  // An approval's message, all of it, is drawn exactly as written, fence markers included, in the
  // code face and left to right, with its hidden characters and right-to-left letters as escapes;
  // nothing in it is read as Markdown (#220, #415, ui.md §6.4): Approve once allows what runs.
  // Other questions draw as Markdown.
  const message = h('div', { class: `md question-message${approval ? ' md-approval' : ''}`, id: `${idBase}-m`, dir: approval ? 'ltr' : null });
  message.append(renderMarkdown(q.message, document, { approval }));
  section.append(message);
  return section;
}

// why: 'truncated' (it didn't arrive whole), 'atMachine' (a prompt only the machine can
// answer, such as a permission prompt of a kind no host drives), 'approvalOff' (a permission
// prompt at a machine where approving from the phone is turned off) or 'machine' (the
// machine takes no answers from the phone).
export function renderQuestion(q, { machineName, why = 'machine', readOnly = null, asker = null } = {}) {
  const idBase = `q${++seq}`;
  const section = shell(q, idBase, false, asker);
  // A prompt answered at the machine shows what it asks, not choices nobody can make here.
  const atMachine = why === 'atMachine' || why === 'approvalOff';
  if (q.fields.length && !atMachine) {
    section.append(h('ol', { class: 'fields' }, q.fields.map((f, k) => readOnlyField(f, `${idBase}-f${k}`))));
  }
  const at = where(machineName);
  const shape = why === 'atMachine' && q.answerAtMachineReason === 'shape';
  // A prompt only the machine could answer anyway still says why the machine is read-only.
  if (readOnly && why !== 'machine') {
    section.append(h('p', { class: 'note warn read-only-machine', role: 'note' },
      `This machine is read-only from ${BRAND.name}: ${readOnly}${/[.!?]$/.test(readOnly) ? '' : '.'}`));
  }
  section.append(why === 'truncated'
    ? h('p', { class: 'note warn read-only-why', role: 'note' },
      `Part of this question didn't fit in the message from the machine, so it can't be answered from here. Answer at the machine${at}.`)
    : why === 'approvalOff'
      ? h('p', { class: 'note warn read-only-why', role: 'note' },
        `Approving permission prompts from ${BRAND.name} is turned off on this machine. Answer at the machine${at}.`)
      : why === 'atMachine'
        ? h('p', { class: 'note warn read-only-why', role: 'note' },
          shape ? `Answer at the machine${machineName ? ` · ${machineName}` : ''}` : `This prompt can only be answered at the machine${at}.`)
        : readOnly
          // The machine said why (agent-ha-bridge runs there): its words say where to answer.
          ? h('p', { class: 'note warn read-only-why', role: 'note' },
            `This machine is read-only from ${BRAND.name}: ${readOnly}${/[.!?]$/.test(readOnly) ? '' : '.'}`)
          : h('p', { class: 'note read-only-why', role: 'note' },
            `This machine doesn't take answers from ${BRAND.name} yet. Answer at the machine${at}.`));
  if (shape) {
    section.append(h('p', { class: 'note warn at-machine-explanation' },
      'This kind of question can only be answered at the machine.'));
  }
  return section;
}

// A question its session no longer lists, kept as it was asked: the item that stays in the
// conversation once the cause of an amber or red line is gone (ui.md §7, "Lines that outlive
// their cause"). Read-only, with no options chosen for the person: an approval shows its message
// alone. The line under it (NO_LONGER_WAITING) is the card's.
//
// It is collapsed: the record (who asked, the question, the options as plain text with what was
// answered marked) is hidden behind one button, "Show question" or "Hide question", and the card's
// line sits beside it (section.summary). answer: what the person answered here, if the answer went
// through; none when it closed without a record. An approval shows its message alone, exactly as
// shell() draws it, and no option or answer text of the agent's.
export function renderResolved(q, { asker = null, answer = null } = {}) {
  const asked = asker ? asker.replace(/ asks$/, ' asked') : (q.kind === 'approval' ? 'Permission requested' : 'Question');
  const idBase = `q${++seq}`;
  const section = shell(q, idBase, false, asked);
  section.classList.add('resolved');
  section.dataset.resolved = 'true';
  if (q.fields.length && q.kind !== 'approval') {
    section.append(h('ol', { class: 'fields' }, q.fields.map((f, k) => readOnlyField(f, `${idBase}-f${k}`, { answer }))));
  }
  const record = h('div', { class: 'resolved-record', id: `${idBase}-r`, hidden: true }, [...section.childNodes]);
  const toggle = h('button', {
    type: 'button', class: 'button secondary resolved-toggle', 'aria-expanded': 'false', 'aria-controls': record.id,
    onclick: () => {
      const open = record.hidden;
      record.hidden = !open;
      toggle.setAttribute('aria-expanded', String(open));
      toggle.textContent = open ? 'Hide question' : 'Show question';
    },
  }, 'Show question');
  section.summary = h('div', { class: 'resolved-summary' }, toggle);
  section.append(record, section.summary);
  return section;
}

// ---- answering ----

const OUTCOME_TEXT = {
  // Said as "You answered · value" (describeAnswer), in text2 with no badge: the button's check said it went.
  delivered: () => 'You answered',
  unconfirmed: () => 'Sent, but not confirmed. Check the conversation.',
  stale: () => 'Already answered or gone: the session is no longer asking this.',
  busy: () => 'Another answer to this session is being delivered. Try again in a moment.',
  refused: () => 'The machine refused this answer.',
};

// What the host says of a stale answer for a form it left part answered and cancelled (code
// formReset, #1123): said in this app's own words. The shared sentence for the code (answers.js
// AnswerReasons) is not used here; see ui.md §7.
export const NOT_DELIVERED = 'Not delivered: the form could not take your answer at the machine.';

// The words a person gave in an answer, as they would type them again: one line per field for a form
// of several, the label of a choice and not its value. For keeping them in view when the answer
// did not go.
export function answeredWords(question, answer) {
  if (!question || !answer) { return ''; }
  if (typeof answer.text === 'string') { return answer.text.trim(); }
  const said = (field, v) => {
    if (Array.isArray(v)) { return v.map((x) => said(field, x)).filter(Boolean).join(', '); }
    if (typeof v === 'boolean') { return v ? 'Yes' : 'No'; }
    if (typeof v === 'number') { return String(v); }
    if (v && typeof v.other === 'string') { return v.other.trim(); }
    if (typeof v === 'string') { return (field.options?.find((o) => o.value === v)?.label ?? v).trim(); }
    return '';
  };
  const lines = [];
  for (const field of question.fields) {
    const words = said(field, answer.fields?.[field.name]);
    if (words) { lines.push(question.fields.length > 1 ? `${field.title ?? field.name}: ${words}` : words); }
  }
  return lines.join('\n');
}

const UNCONFIRMED_STAGE_TEXT = {
  typed: () => 'Typed into the prompt, but not confirmed as submitted. Check the conversation.',
  recorded: () => 'The session recorded something for this question, but not confirmed as this answer. Check the conversation.',
};

// What an answer's progress or outcome says, for the form, the Needs you lane and the card once
// the question has gone: the receipt's words (receipt.js; ui.md §6.4 and §6.5, decided on #617).
// On its way it is only heard (Sending, Sent, Passed to the agent, never Up next and never
// "answered"), unless it stalls or waits. Confirmed: the card resolves to "You answered · value",
// in text2 with no badge, where question gives the value (answeredValue). Any other outcome keeps
// its words. stalled: whether the state it is in has stalled (ReceiptClock). Returns { key, text,
// more, tone, line, speech }; tone: progress | done | warn | bad, or room for nothing to show.
export function describeAnswer(entry, machineName, { question = null, stalled = false } = {}) {
  switch (entry.state) {
    case 'sending':
    case 'waiting':
      return describeInFlight('answer', entry, { machineName, stalled });
    case 'failed': {
      const text = `Couldn't send it: ${entry.reason ?? 'the machine said no'}.`;
      return { key: 'failed', text, more: null, tone: 'bad', line: true, speech: `Answer: ${text}` };
    }
    default: {
      const outcome = OUTCOME_TEXT[entry.outcome] ? entry.outcome : 'unconfirmed';
      if (outcome === 'delivered') {
        const value = answeredValue(question, entry.answer);
        return {
          key: 'delivered', text: 'You answered', more: value, tone: 'done', line: true,
          speech: value ? `Answer delivered. You answered ${value}` : 'Answer delivered. You answered',
        };
      }
      let text = OUTCOME_TEXT[outcome]();
      // A stale answer from a form the host left part answered and cancelled (code formReset): the
      // question was not withdrawn or answered elsewhere, the host could not finish typing the answer
      // into it, so none of it went. Its own words, never "already answered" (ui.md §7).
      if (outcome === 'stale' && entry.detail?.code === 'formReset') {
        const words = answeredWords(question, entry.answer);
        return { key: 'notDelivered', text: NOT_DELIVERED, more: null, tone: 'warn', line: true, speech: `Answer: ${NOT_DELIVERED}`, ...(words ? { words } : {}) };
      }
      // The host's own words, as it said them: a sentence of its own already ends with its stop
      // (the too-tall refusal does, #1213), and gets no second one.
      if (outcome === 'refused' && entry.reason) { text = `The machine refused this answer: ${entry.reason}${/[.!?]$/.test(entry.reason.trimEnd()) ? '' : '.'}`; }
      // A reason with fixed words of its own, the same in every client (#908).
      if (outcome === 'refused' && AnswerReasons[entry.detail?.code]) { text = NOT_SENT + AnswerReasons[entry.detail.code]; }
      // How far the host knows it got (#467): typed into the prompt, or recorded differently.
      if (outcome === 'unconfirmed' && UNCONFIRMED_STAGE_TEXT[entry.detail?.stage]) { text = UNCONFIRMED_STAGE_TEXT[entry.detail.stage](); }
      if (outcome === 'unconfirmed' && entry.reason) { text += ` (${entry.reason})`; }
      return { key: outcome, text, more: null, tone: outcome === 'refused' ? 'bad' : 'warn', line: true, speech: `Answer: ${text}` };
    }
  }
}

// What a question that has left its session's list says of an answer that did not go through. The
// host cannot tell a question the agent withdrew from one answered elsewhere, so this is one
// neutral closed state and no cause is promised (ui.md §7).
export const NO_LONGER_WAITING = 'Question closed';

// describeAnswer for an answer whose question is no longer asked: an answer that was not sent, was
// refused or came too late is only "No longer waiting" (tone closed, never amber or red); one that
// may have gone in keeps its words, neutral, and still becomes "You answered" if a recheck finds
// it delivered. In flight and delivered are as describeAnswer has them.
export function describeClosed(entry, machineName, opts) {
  const d = describeAnswer(entry, machineName, opts);
  const gone = entry.state === 'failed' || (entry.state === 'done' && (['busy', 'refused'].includes(entry.outcome) || (entry.outcome === 'stale' && d.key !== 'notDelivered')));
  if (gone) { return { key: 'closed', text: NO_LONGER_WAITING, more: null, tone: 'closed', line: true, speech: NO_LONGER_WAITING }; }
  // Words the host gave a reason for, or that may have gone in, are kept, neutral.
  if (d.key === 'unconfirmed' || d.key === 'notDelivered') { return { ...d, tone: 'closed' }; }
  return d;
}

// After busy, Send comes back after this long: long enough not to collide again at once.
export const BUSY_PAUSE_MS = 2000;

// What an answer's progress allows. Busy and refused were not typed: the person may answer
// again (after busy, in a moment: pause). Every other state holds the controls: on its way, or
// final. Unconfirmed is final and is never resent, from here or automatically. The card's form
// and the Needs you lane (#165) both follow this.
export function answerControls(entry) {
  // A stale answer from a cancelled form was not delivered, so a question still listed takes it again.
  const again = !entry || (entry.state === 'done' && (['busy', 'refused'].includes(entry.outcome) || (entry.outcome === 'stale' && entry.detail?.code === 'formReset')));
  const pause = entry?.state === 'done' && entry.outcome === 'busy';
  return { again, pause };
}

// An answer's progress or outcome as one word, for data-answer: an unknown outcome is unconfirmed.
export function answerState(entry) {
  if (!entry) { return 'none'; }
  return entry.state === 'done' ? (OUTCOME_TEXT[entry.outcome] ? entry.outcome : 'unconfirmed') : entry.state;
}

const kib = (n) => `${(n / 1024).toFixed(1)} KiB`;
const capitalise = (s) => (s ? s[0].toUpperCase() + s.slice(1) : s);
const sendKey = (ev) => ev.key === 'Enter' && (ev.ctrlKey || ev.metaKey);

// One question's answer form, live: it follows the answer's progress in the service and
// keeps what the person has entered across re-renders of the same question.
export class QuestionPanel {
  #unwatch = null;
  #busyTimer = null;
  #held = null;
  #holdKey = '';
  // A quiet hold while the app catches up after a return (hold.js), or null.
  #quiet = null;
  #receipt = null;
  // The text the form held when its answer last went (unsentText).
  #sentDraft = null;

  // answering: { service, hostId, sessionId }. state: what a previous panel for the same
  // question held, so a re-render loses nothing. escaped: for an approval the host escaped anything
  // in (#803), { mode, sign, title }: mode 'confirm' opens the confirmation sheet, whose button
  // alone calls sign({ session, question, shown, request }); 'device' or 'machine' offer Deny only,
  // saying why. Null for any other question.
  // onSend(typed): called as an answer goes, with the text the form held, so the card can let go of
  // text it kept for the person once that text is sent. listed(): whether the session still lists
  // this question, for what is said of its answer once the panel is gone.
  constructor(q, { machineName, answering, state = null, asker = null, escaped = null, onSend = null, listed = () => false, sentDraft = null }) {
    this.q = q;
    this.#sentDraft = sentDraft;
    this.asker = asker;
    this.onSend = onSend;
    this.machineName = machineName;
    this.answering = answering;
    this.escaped = escaped;
    this.questionId = q.id;
    this.idBase = `q${++seq}`;
    this.tap = tapToAnswer(q);
    this.state = state ?? initialState(q);
    this.otherOpen = this.tap && this.state[q.fields[0].name].otherChosen === true;
    this.element = shell(q, this.idBase, true, asker);
    this.#build();
    this.#receipt = new ReceiptClock({
      describe: (entry, { stalled }) => describeAnswer(entry, this.machineName, { question: this.q, stalled }),
      // Followed past this panel's going: said as the card's receipt says it (a question that has left is closed).
      followDescribe: (entry, { stalled }) => (listed() ? describeAnswer : describeClosed)(entry, this.machineName, { question: this.q, stalled }),
      draw: (d) => drawReceipt(this.els.status, 'answer-status', d),
      say: (text) => { this.els.live.textContent = text; },
      heard: () => drawn(this.els.live),
      // The button that sent it: the option tapped, or Send.
      delivered: () => holdCheck(this.element.querySelector('.choice[aria-pressed="true"]') ?? this.els.send),
      follow: (fn) => answering.service.watch(answering.hostId, answering.sessionId, q.id, fn),
    });
    const { service, hostId, sessionId } = answering;
    this.#unwatch = service.watch(hostId, sessionId, q.id, (e) => this.#show(e));
    this.#show(service.get(hostId, sessionId, q.id));
  }

  destroy() { this.#unwatch?.(); clearTimeout(this.#busyTimer); this.#receipt.destroy(); }

  // Why nothing can be sent now (its machine offline, #269), or null: everything entered stays,
  // and the controls take nothing until it is null again.
  setHeld(held) {
    const hold = holdOf(held);
    const key = holdKey(hold);
    if (key === this.#holdKey) { return; }
    this.#holdKey = key;
    const text = hold.text || null;
    this.#held = text;
    this.#quiet = hold.updating ? hold : null;
    const e = this.els;
    // The offline line takes the place of the actions; focus on one of them moves to the line (rule 8).
    const focused = hold.offline && this.element.contains(this.element.ownerDocument.activeElement) && (e.actions.contains(this.element.ownerDocument.activeElement) || e.controls.contains(this.element.ownerDocument.activeElement));
    e.held.textContent = text ?? '';
    e.held.hidden = !text;
    if (hold.offline) { e.held.tabIndex = -1; } else { e.held.removeAttribute('tabindex'); }
    e.actions.hidden = hold.offline;
    if (!hold.updating) { e.updating.hidden = true; e.updating.textContent = ''; }
    this.element.dataset.held = text ? 'true' : 'false';
    this.element.dataset.hold = hold.updating ? (hold.drawn ? 'drawn' : 'quiet') : 'none';
    this.#show(this.entry);
    if (focused) { e.held.focus(); }
  }

  get entry() { const { service, hostId, sessionId } = this.answering; return service.get(hostId, sessionId, this.q.id); }

  // The text the form held when its answer last went, carried to a panel drawn again for the same question.
  get sentDraft() { return this.#sentDraft; }

  // When the check on the control that sent its delivered answer ends, or null.
  get checkUntil() { return this.#receipt.checkUntil; }

  // Text the person typed that has not gone to the machine, which would be lost if the
  // panel went away silently. After a stale answer from a cancelled form (formReset) the form takes
  // an answer again, so what was changed since that answer went is unsent too; what went is kept
  // by the card's receipt.
  unsentText() {
    const e = this.entry;
    const reset = e?.state === 'done' && e.outcome === 'stale' && e.detail?.code === 'formReset';
    const unsent = !e || e.state === 'failed' || (e.state === 'done' && ['busy', 'refused'].includes(e.outcome));
    const text = unsent || reset ? draftText(this.q, this.state) : '';
    return reset && text === this.#sentDraft ? '' : text;
  }

  #build() {
    const q = this.q;
    const id = this.idBase;
    const e = this.els = {};
    e.form = h('form', { class: 'answer', novalidate: true, 'aria-labelledby': `${id}-h`, 'aria-describedby': `${id}-m` });
    e.form.addEventListener('submit', (ev) => { ev.preventDefault(); this.#send(); });
    // A disabled fieldset disables everything in it at once, while an answer is on its way.
    e.controls = h('fieldset', { class: 'answer-controls' }, h('legend', { class: 'sr-only' }, 'Your answer'));
    e.form.append(e.controls);
    if (this.tap) {
      this.#buildTap(q.fields[0]);
    } else if (q.fields.length === 0) {
      e.controls.append(h('div', { class: 'field field-text plain-answer', dataset: { field: '', type: 'text' } }, this.#textArea({
        id: `${id}-text`, label: 'Your answer', value: this.state.text, rows: 3,
        onInput: (v) => { this.state.text = v; },
      })));
    } else {
      e.controls.append(h('ol', { class: 'fields' }, q.fields.map((f, k) => this.#field(f, `${id}-f${k}`))));
    }
    e.error = h('p', { class: 'answer-error', id: `${id}-err`, role: 'alert', hidden: true });
    e.held = h('p', { class: 'note warn answer-held', role: 'status', hidden: true });
    e.send = h('button', { type: 'submit', class: 'button send' }, q.kind === 'approval' ? 'Send' : 'Send answer');
    e.resend = h('button', { type: 'button', class: 'button secondary resend', hidden: true, onclick: () => this.#resend() }, 'Try again');
    e.actions = h('div', { class: 'answer-actions' }, e.send, e.resend);
    // The receipt's words, and apart from them the line a screen reader hears it on (receipt.js):
    // outside the form, which is aria-busy while the answer goes, and a reader may hold back
    // what a busy region says until it is done.
    e.status = h('p', { class: 'answer-status' });
    e.live = receiptLive();
    e.updating = h('p', { class: 'note answer-updating', hidden: true });
    e.form.append(e.held, e.error, e.actions, e.updating, e.status);
    // A tap question has no Send until Other is opened.
    e.send.hidden = this.tap && !this.otherOpen;
    this.element.append(e.form, e.live);
  }

  #buildTap(field) {
    const id = this.idBase;
    const e = this.els;
    const approval = this.q.kind === 'approval';
    const st = this.state[field.name];
    // An approval's one field is the decision itself: the heading says what it is, unless
    // the agent gave the field a title of its own.
    const headed = !approval || field.title !== null;
    const titleId = headed ? `${id}-f0-t` : `${id}-h`;
    // Other the agent offers and the machine cannot send yet (#908): its reason describes the
    // choices, read with the group and with the disabled Other, and is said under them.
    const unavailable = AnswerReasons[field.otherUnavailable] ?? null;
    const noteId = unavailable ? `${id}-other-unavailable` : null;
    const group = h('div', { class: `choices${approval ? ' approval' : ''}`, role: 'group', 'aria-labelledby': titleId, 'aria-describedby': noteId });
    const escaped = approval ? this.escaped : null;
    for (const o of orderOptions(field.options)) {
      const tone = approval ? optionTone(o) : 'neutral';
      const isDefault = field.default === o.value;
      if (escaped && o.value === 'approve') {
        // Approve once never goes on a tap for one the host escaped anything in: it opens the sheet,
        // or is not offered here at all (one verb per state).
        if (escaped.mode === 'confirm') {
          group.append(h('button', {
            type: 'button', class: 'choice tone-allow escaped-approve', dataset: { value: o.value, escaped: 'confirm' },
            'aria-haspopup': 'dialog', onclick: () => this.#confirmEscaped(),
          }, h('span', { class: 'label' }, 'Approve once…')));
        }
        continue;
      }
      const b = h('button', {
        type: 'button',
        class: `choice tone-${tone}${isRecommended(o) ? ' recommended' : ''}${isDefault ? ' default' : ''}`,
        dataset: { value: o.value },
        'aria-pressed': 'false',
        onclick: () => {
          st.selected = o.value;
          st.otherChosen = false;
          this.#send({ tapped: b });
        },
      }, h('span', { class: 'label' }, o.label), isDefault ? h('span', { class: 'badge' }, 'default') : null);
      group.append(b);
    }
    const parts = headed ? [h('div', { class: 'field-head' }, headParts(field, titleId))] : [];
    if (field.description) { parts.push(h('p', { class: 'field-desc' }, field.description)); }
    parts.push(group);
    if (escaped && escaped.mode !== 'confirm') { parts.push(this.#escapedWhy(escaped.mode)); }
    if (unavailable) {
      // Shown as the agent offers it, its label unchanged, and never pressable.
      group.append(h('button', { type: 'button', class: 'choice other-toggle other-unavailable', disabled: true, 'aria-describedby': noteId }, h('span', { class: 'label' }, 'Other…')));
      parts.push(h('p', { id: noteId, class: 'field-desc other-unavailable-note' }, unavailable));
    } else if (field.allowOther) {
      const boxId = `${id}-other`;
      e.otherToggle = h('button', {
        type: 'button', class: 'choice other-toggle', 'aria-expanded': this.otherOpen ? 'true' : 'false', 'aria-controls': boxId,
        onclick: () => this.#toggleOther(),
      }, h('span', { class: 'label' }, 'Other…'));
      group.append(e.otherToggle);
      e.otherBox = h('div', { class: 'other-box', id: boxId, hidden: !this.otherOpen },
        this.#textArea({
          id: `${id}-other-text`, label: `Your own answer for ${field.title ?? field.name}`, value: st.other, rows: 2,
          onInput: (v) => { st.other = v; st.otherChosen = true; },
        }));
      parts.push(e.otherBox);
    }
    e.controls.append(h('div', { class: `field field-choice tap${approval ? ' approval' : ''}`, dataset: { field: field.name, type: field.type } }, parts));
  }

  // Where Approve once was, why it is not offered here (ui.md §6.4, "Escaped approvals").
  #escapedWhy(mode) {
    const machine = this.machineName ?? 'the machine';
    if (mode === 'machine') {
      return h('p', { class: 'note escaped-why', role: 'note', dataset: { escaped: 'machine' } }, `${machine} takes this approval only at the machine.`);
    }
    if (mode === 'many') {
      return h('p', { class: 'note escaped-why', role: 'note', dataset: { escaped: 'many' } }, `Too many hidden characters to show here. Approve this at ${machine}.`);
    }
    return h('div', { class: 'note escaped-why', role: 'note', dataset: { escaped: 'device' } },
      h('p', {}, `Approve this at ${machine}, or from a device approved there.`),
      h('details', { class: 'escaped-help' },
        h('summary', {}, 'How to approve a device'),
        h('p', {}, `At ${machine}, open the Windows app's Settings, turn on answering folder-trust questions from devices, and approve this device: `
          + 'a device approved for those can approve these too while that is on, unless the machine lists its own (permissions.escapedApprovalDevices in its config.json).')));
  }

  // The sheet, and Approve once only from its own button: signed for the answer's request when it is
  // pressed, then sent as any answer is, so a resend carries the same confirmation.
  #confirmEscaped() {
    if (this.#quiet) { this.#updating(); return; }
    if (this.#held || this.#sheetOpen) { return; }
    const entry = this.entry;
    if (entry && (entry.state === 'sending' || entry.state === 'waiting')) { return; }
    const q = this.q;
    const { service, hostId, sessionId } = this.answering;
    this.#sheetOpen = true;
    openConfirmSheet(q, {
      title: this.escaped.title, machineName: this.machineName,
      sign: async ({ shown, pressed, cancelled = () => false }) => {
        // Signed as of the press, so it lasts COMMAND_LIFETIME_S from then, as the host holds it to;
        // one already that old (a tab suspended after the press) is not signed or sent at all. No
        // press time, no confirmation: a default here would be the signing time again.
        if (!Number.isFinite(pressed)) { throw new Error('It could not be signed: the time it was pressed is unknown. Press again'); }
        const stale = () => Date.now() - pressed >= COMMAND_LIFETIME_S * 1000;
        const expired = () => new Error(`Not sent: this confirmation is more than ${COMMAND_LIFETIME_S / 60} minutes old. Press again`);
        if (stale()) { throw expired(); }
        // A return's hold that began while the sheet was open: nothing is signed or sent until it lifts (#950).
        if (this.#quiet) { throw new Error(`${UPDATING} Press again in a moment`); }
        const request = globalThis.crypto.randomUUID();
        const confirm = await this.escaped.sign({ session: sessionId, question: q.id, shown, request, now: pressed });
        // Closed while it was signed: the confirmation is dropped, and nothing is sent (#836 review).
        if (cancelled()) { return false; }
        if (stale()) { throw expired(); }
        if (this.#quiet) { throw new Error(`${UPDATING} Press again in a moment`); }
        // The machine's route gone while it was signed (#269's gate): nothing is queued, so the sheet
        // must not close as if it went. It stays open saying so, its button the Try again, which
        // signs afresh once the machine is back (#836 review 5780082443).
        const entry = service.submit({ hostId, sessionId, questionId: q.id, answer: { fields: { [q.fields[0].name]: 'approve' }, confirm }, request });
        if (!entry) { throw new Error(`Not sent: ${this.machineName ?? 'the machine'} isn't connected now. Press again once it's back`); }
        this.state[q.fields[0].name].selected = 'approve';
        this.state[q.fields[0].name].otherChosen = false;
      },
    }).finally(() => { this.#sheetOpen = false; });
  }

  #sheetOpen = false;

  #toggleOther() {
    const e = this.els;
    this.otherOpen = !this.otherOpen;
    e.otherToggle.setAttribute('aria-expanded', this.otherOpen ? 'true' : 'false');
    e.otherBox.hidden = !this.otherOpen;
    e.send.hidden = !this.otherOpen;
    this.state[this.q.fields[0].name].otherChosen = this.otherOpen;
    this.#clearError();
    if (this.otherOpen) { e.otherBox.querySelector('textarea').focus(); }
  }

  // A byte count that appears as text nears the limit: the limit is UTF-8 bytes, which an
  // emoji or CJK text reaches long before 4,096 characters.
  #counter(ta, id) {
    const counter = h('span', { class: 'bytes', id, 'aria-live': 'polite' });
    const update = () => {
      const n = utf8Bytes(ta.value);
      const over = n > Limits.text;
      counter.textContent = n > Limits.text * 0.75 ? `${over ? 'Too long: ' : ''}${kib(n)} of ${Limits.text / 1024} KiB` : '';
      counter.classList.toggle('over', over);
      if (over) { ta.setAttribute('aria-invalid', 'true'); } else { ta.removeAttribute('aria-invalid'); }
    };
    ta.addEventListener('input', update);
    update();
    ta.setAttribute('aria-describedby', id);
    return counter;
  }

  #textArea({ id, label, value, onInput, rows = 3, labelledBy = null }) {
    const ta = h('textarea', { id, rows, class: 'text-input', 'aria-labelledby': labelledBy });
    ta.value = value ?? '';
    ta.addEventListener('input', () => { onInput(ta.value); this.#clearError(); });
    // Ctrl+Enter or Cmd+Enter sends, as in most chat boxes; Enter alone is a new line.
    ta.addEventListener('keydown', (ev) => { if (sendKey(ev)) { ev.preventDefault(); this.#send(); } });
    const counter = this.#counter(ta, `${id}-bytes`);
    return h('div', { class: 'text-wrap' },
      labelledBy ? null : h('label', { for: id, class: 'text-label' }, label), ta, counter);
  }

  #field(field, idBase) {
    const st = this.state[field.name];
    const titleId = `${idBase}-t`;
    const parts = [h('legend', { class: 'field-head' }, headParts(field, titleId))];
    if (field.description) { parts.push(h('p', { class: 'field-desc' }, field.description)); }
    const name = `${idBase}-v`;
    if (field.type === 'choice' || field.type === 'multi') {
      const multi = field.type === 'multi';
      const list = h('div', { class: 'opts' });
      for (const o of orderOptions(field.options)) {
        const input = h('input', {
          type: multi ? 'checkbox' : 'radio', name, value: o.value,
          checked: multi ? st.selected.includes(o.value) : (!st.otherChosen && st.selected === o.value),
        });
        input.addEventListener('change', () => {
          if (multi) {
            st.touched = true;
            st.selected = input.checked ? [...new Set([...st.selected, o.value])] : st.selected.filter((v) => v !== o.value);
          } else if (input.checked) {
            st.selected = o.value;
            st.otherChosen = false;
          }
          this.#clearError();
        });
        list.append(h('label', { class: `opt${isRecommended(o) ? ' recommended' : ''}`, dataset: { value: o.value } }, input, h('span', { class: 'label' }, o.label)));
      }
      if (field.allowOther) {
        const other = h('input', { type: multi ? 'checkbox' : 'radio', name, value: '', class: 'other-choice', checked: st.otherChosen });
        const textId = `${idBase}-other`;
        const text = h('textarea', { id: textId, rows: 2, class: 'text-input other-text', 'aria-label': `Your own answer for ${field.title ?? field.name}` });
        text.value = st.other;
        other.addEventListener('change', () => {
          st.otherChosen = other.checked;
          if (multi) { st.touched = true; }
          if (other.checked) { text.focus(); }
          this.#clearError();
        });
        // Typing in Other chooses it: nobody types an answer they don't mean to give.
        text.addEventListener('input', () => {
          st.other = text.value;
          if (!other.checked && text.value !== '') {
            other.checked = true;
            st.otherChosen = true;
          }
          this.#clearError();
        });
        text.addEventListener('keydown', (ev) => { if (sendKey(ev)) { ev.preventDefault(); this.#send(); } });
        const counter = this.#counter(text, `${textId}-bytes`);
        list.append(h('label', { class: 'opt other' }, other, h('span', { class: 'label' }, 'Other…')), h('div', { class: 'other-box' }, text, counter));
      }
      parts.push(list);
    } else if (field.type === 'boolean') {
      const yes = h('button', { type: 'button', class: 'toggle', 'aria-pressed': st.value === true ? 'true' : 'false', dataset: { value: 'true' } }, 'Yes');
      const no = h('button', { type: 'button', class: 'toggle', 'aria-pressed': st.value === false ? 'true' : 'false', dataset: { value: 'false' } }, 'No');
      const set = (v) => {
        // Pressing the chosen one again clears it, so an optional answer can be left out.
        st.value = st.value === v ? null : v;
        yes.setAttribute('aria-pressed', st.value === true ? 'true' : 'false');
        no.setAttribute('aria-pressed', st.value === false ? 'true' : 'false');
        this.#clearError();
      };
      yes.addEventListener('click', () => set(true));
      no.addEventListener('click', () => set(false));
      parts.push(h('div', { class: 'opts inline toggles' }, yes, no));
    } else if (field.type === 'number') {
      const input = h('input', { id: `${idBase}-n`, type: 'number', step: 'any', inputmode: 'decimal', class: 'number-input', 'aria-labelledby': titleId, 'aria-required': field.required ? 'true' : null });
      input.value = st.text;
      input.addEventListener('input', () => { st.text = input.value; st.bad = input.validity.badInput; this.#clearError(); });
      parts.push(input);
    } else {
      parts.push(this.#textArea({ id: `${idBase}-text`, labelledBy: titleId, value: st.text, onInput: (v) => { st.text = v; } }));
      if (field.required) { parts[parts.length - 1].querySelector('textarea').setAttribute('aria-required', 'true'); }
    }
    return h('li', { class: `field field-${field.type}`, dataset: { field: field.name, type: field.type } },
      h('fieldset', { class: 'field-set' }, parts));
  }

  #clearError() {
    const e = this.els;
    if (e.error.hidden) { return; }
    e.error.hidden = true;
    e.error.textContent = '';
    for (const el of this.element.querySelectorAll('.field.invalid')) { el.classList.remove('invalid'); }
  }

  #fail(message, fieldName) {
    const e = this.els;
    e.error.textContent = `${capitalise(message)}.`;
    e.error.hidden = false;
    const field = fieldName === null ? null : [...this.element.querySelectorAll('.field[data-field]')].find((f) => f.dataset.field === fieldName);
    field?.classList.add('invalid');
    const target = (field ?? e.controls).querySelector('textarea, input, button:not([disabled])');
    target?.focus();
  }

  // A press while the app catches up: said under the controls, in text2; never a toast.
  #updating() {
    this.els.updating.textContent = UPDATING;
    this.els.updating.hidden = false;
  }

  #send({ tapped = null } = {}) {
    if (this.#quiet) { this.#updating(); return; }
    if (this.#held) { return; }
    const entry = this.entry;
    if (entry && (entry.state === 'sending' || entry.state === 'waiting')) { return; }
    const q = this.q;
    const { service, hostId, sessionId } = this.answering;
    const { answer, problem, field } = prepareAnswer(q, this.state, sessionId);
    if (problem) { this.#fail(problem, field); return; }
    this.#clearError();
    tapped?.setAttribute('aria-pressed', 'true');
    const typed = draftText(q, this.state);
    this.#sentDraft = typed;
    service.submit({ hostId, sessionId, questionId: q.id, answer });
    this.onSend?.(typed);
  }

  #resend() {
    if (this.#quiet) { this.#updating(); return; }
    if (this.#held) { return; }
    // A form the host cancelled: nothing went, so what was typed is sent again as a new request.
    if (this.entry?.state === 'done' && this.entry.outcome === 'stale') { this.#send(); return; }
    const { service, hostId, sessionId } = this.answering;
    service.resend(hostId, sessionId, this.q.id);
  }

  #show(entry) {
    const e = this.els;
    clearTimeout(this.#busyTimer);
    const { again, pause } = answerControls(entry);
    const held = !!this.#held;
    this.element.dataset.answer = answerState(entry);
    // Answer acknowledged (ui.md §5.3): each new state of the answer rises into place (receipt.js).
    // The question and its buttons never move.
    this.#receipt.show(entry);
    e.status.hidden = !entry;
    e.form.setAttribute('aria-busy', entry?.state === 'sending' || entry?.state === 'waiting' ? 'true' : 'false');
    // Held on this device for a machine that isn't connected: the ring on its button stands still.
    e.form.classList.toggle('still', entry?.state === 'waiting' && entry.reason === 'not connected');
    e.controls.disabled = !again || pause || held;
    e.send.disabled = !again || pause || held;
    // A quiet hold: aria-disabled at once, so focus stays; a press does nothing but say why.
    for (const b of [e.send, ...this.element.querySelectorAll('.choice')]) {
      if (this.#quiet && again && !pause) { b.setAttribute('aria-disabled', 'true'); } else { b.removeAttribute('aria-disabled'); }
    }
    if (!this.#quiet) { e.updating.hidden = true; e.updating.textContent = ''; }
    e.resend.hidden = !(entry?.state === 'failed' || (entry?.state === 'done' && entry.outcome === 'stale' && again));
    e.resend.disabled = held;
    if (pause) { this.#busyTimer = setTimeout(() => { e.controls.disabled = !!this.#held; e.send.disabled = !!this.#held; }, BUSY_PAUSE_MS); }
    // A tap question shows which option went; one that can be answered again shows none.
    const sent = !again && this.tap ? entry.answer?.fields?.[this.q.fields[0].name] : undefined;
    for (const b of this.element.querySelectorAll('.choice[data-value]')) {
      b.setAttribute('aria-pressed', typeof sent === 'string' && b.dataset.value === sent ? 'true' : 'false');
    }
  }
}
