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
import { renderMarkdown } from '../markdown.js';
import {
  Limits, answerRequest, buildAnswer, checkAnswer, draftText, initialState, isRecommended,
  optionTone, orderOptions, problemField, tapToAnswer, utf8Bytes,
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

function optionRow(label, { chosen, multi, other }) {
  const mark = multi ? (chosen ? '☑' : '☐') : (chosen ? '◉' : '○');
  return h('li', { class: `opt${chosen ? ' chosen' : ''}${other ? ' other' : ''}` },
    h('span', { class: 'mark', 'aria-hidden': 'true' }, mark),
    h('span', { class: 'label' }, label),
    chosen ? h('span', { class: 'badge' }, 'default') : null);
}

// The field's title, required badge and kind of answer: spans, so it can be a legend.
function headParts(field, titleId) {
  return [
    h('span', { class: 'field-title', id: titleId }, field.title ?? field.name),
    field.required ? h('span', { class: 'badge req' }, 'required') : null,
    h('span', { class: 'field-type' }, TYPE_LABEL[field.type]),
  ];
}

function readOnlyField(field, idBase) {
  const titleId = `${idBase}-t`;
  const parts = [h('div', { class: 'field-head' }, headParts(field, titleId))];
  if (field.description) { parts.push(h('p', { class: 'field-desc' }, field.description)); }

  const chosen = defaults(field);
  if (field.type === 'choice' || field.type === 'multi') {
    const multi = field.type === 'multi';
    const list = h('ul', { class: 'opts', 'aria-labelledby': titleId });
    for (const o of field.options) {
      list.append(optionRow(o.label, { chosen: chosen.has(o.value), multi }));
    }
    const known = new Set(field.options.map((o) => o.value));
    const otherDefault = [...chosen].find((v) => !known.has(v));
    if (field.allowOther) {
      list.append(optionRow(otherDefault ? `Other: ${otherDefault}` : 'Other (type your answer)', { chosen: !!otherDefault, multi, other: true }));
    }
    parts.push(list);
  } else if (field.type === 'boolean') {
    const d = field.default;
    const list = h('ul', { class: 'opts inline', 'aria-labelledby': titleId });
    list.append(optionRow('Yes', { chosen: d === true, multi: false }));
    list.append(optionRow('No', { chosen: d === false, multi: false }));
    parts.push(list);
  } else {
    const d = formatValue(field.default);
    parts.push(h('div', { class: `answer-box${d ? '' : ' empty'}`, 'aria-labelledby': titleId },
      d ? [h('span', { class: 'badge' }, 'default'), ' ', d] : (field.type === 'number' ? 'A number' : 'Free text')));
  }
  return h('li', { class: `field field-${field.type}`, dataset: { field: field.name, type: field.type } }, parts);
}

function shell(q, idBase, answerable) {
  const heading = q.kind === 'approval' ? 'Permission requested' : 'Waiting on you';
  const section = h('section', {
    class: `question${answerable ? ' answerable' : ''}`, 'aria-labelledby': `${idBase}-h`,
    dataset: { kind: q.kind, question: q.id, answerable: answerable ? 'true' : 'false' },
  }, h('h4', { class: 'question-head', id: `${idBase}-h` }, heading));
  const message = h('div', { class: 'md question-message', id: `${idBase}-m` });
  message.append(renderMarkdown(q.message));
  section.append(message);
  return section;
}

// why: 'truncated' (it didn't arrive whole), 'atMachine' (a prompt only the machine can
// answer, such as a permission prompt) or 'machine' (the machine takes no answers from the
// phone).
export function renderQuestion(q, { machineName, why = 'machine' } = {}) {
  const idBase = `q${++seq}`;
  const section = shell(q, idBase, false);
  // A prompt answered at the machine shows what it asks, not choices nobody can make here.
  if (q.fields.length && why !== 'atMachine') {
    section.append(h('ol', { class: 'fields' }, q.fields.map((f, k) => readOnlyField(f, `${idBase}-f${k}`))));
  }
  const at = where(machineName);
  section.append(why === 'truncated'
    ? h('p', { class: 'note warn read-only-why', role: 'note' },
      `Part of this question didn't fit in the message from the machine, so it can't be answered from here. Answer at the machine${at}.`)
    : why === 'atMachine'
      ? h('p', { class: 'note warn read-only-why', role: 'note' },
        `This prompt can only be answered at the machine${at}.`)
      : h('p', { class: 'note read-only-why', role: 'note' },
        `This machine doesn't take answers from the phone yet. Answer at the machine${at}.`));
  return section;
}

// ---- answering ----

const OUTCOME_TEXT = {
  delivered: () => 'Delivered. The session has your answer.',
  unconfirmed: (at) => `Sent, but not confirmed. Check at the machine${at}.`,
  stale: () => 'Already answered or gone: the session is no longer asking this.',
  busy: () => 'Another answer to this session is being delivered. Try again in a moment.',
  refused: () => 'The machine refused this answer.',
};

// What an answer's progress or outcome says, for the form and for the card once the
// question has gone. tone: progress | ok | warn | bad.
export function describeAnswer(entry, machineName) {
  const at = where(machineName);
  switch (entry.state) {
    case 'sending':
      return { text: entry.attempts > 1 ? 'Sending again…' : 'Sending…', tone: 'progress' };
    case 'waiting':
      return {
        text: entry.reason === 'still being delivered'
          ? 'The machine is still delivering it…'
          : `Waiting for the connection to the machine${at}. Your answer goes as soon as it is back.`,
        tone: 'progress',
      };
    case 'failed':
      return { text: `Couldn't send it: ${entry.reason ?? 'the machine said no'}.`, tone: 'bad' };
    default: {
      const outcome = OUTCOME_TEXT[entry.outcome] ? entry.outcome : 'unconfirmed';
      let text = OUTCOME_TEXT[outcome](at);
      if (outcome === 'refused' && entry.reason) { text = `The machine refused this answer: ${entry.reason}.`; }
      if (outcome === 'unconfirmed' && entry.reason) { text += ` (${entry.reason})`; }
      return { text, tone: outcome === 'delivered' ? 'ok' : outcome === 'refused' ? 'bad' : 'warn' };
    }
  }
}

// After busy, Send comes back after this long: long enough not to collide again at once.
export const BUSY_PAUSE_MS = 2000;

const kib = (n) => `${(n / 1024).toFixed(1)} KiB`;
const capitalise = (s) => (s ? s[0].toUpperCase() + s.slice(1) : s);
const sendKey = (ev) => ev.key === 'Enter' && (ev.ctrlKey || ev.metaKey);

// One question's answer form, live: it follows the answer's progress in the service and
// keeps what the person has entered across re-renders of the same question.
export class QuestionPanel {
  #unwatch = null;
  #busyTimer = null;

  // answering: { service, hostId, sessionId }. state: what a previous panel for the same
  // question held, so a re-render loses nothing.
  constructor(q, { machineName, answering, state = null }) {
    this.q = q;
    this.machineName = machineName;
    this.answering = answering;
    this.questionId = q.id;
    this.idBase = `q${++seq}`;
    this.tap = tapToAnswer(q);
    this.state = state ?? initialState(q);
    this.otherOpen = this.tap && this.state[q.fields[0].name].otherChosen === true;
    this.element = shell(q, this.idBase, true);
    this.#build();
    const { service, hostId, sessionId } = answering;
    this.#unwatch = service.watch(hostId, sessionId, q.id, (e) => this.#show(e));
    this.#show(service.get(hostId, sessionId, q.id));
  }

  destroy() { this.#unwatch?.(); clearTimeout(this.#busyTimer); }

  get entry() { const { service, hostId, sessionId } = this.answering; return service.get(hostId, sessionId, this.q.id); }

  // Text the person typed that has not gone to the machine, which would be lost if the
  // panel went away silently.
  unsentText() {
    const e = this.entry;
    const unsent = !e || e.state === 'failed' || (e.state === 'done' && ['busy', 'refused'].includes(e.outcome));
    return unsent ? draftText(this.q, this.state) : '';
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
    e.send = h('button', { type: 'submit', class: 'button send' }, q.kind === 'approval' ? 'Send' : 'Send answer');
    e.resend = h('button', { type: 'button', class: 'button secondary resend', hidden: true, onclick: () => this.#resend() }, 'Try again');
    e.actions = h('div', { class: 'answer-actions' }, e.send, e.resend);
    e.status = h('p', { class: 'answer-status', role: 'status', 'aria-live': 'polite' });
    e.form.append(e.error, e.actions, e.status);
    // A tap question has no Send until Other is opened.
    e.send.hidden = this.tap && !this.otherOpen;
    this.element.append(e.form);
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
    const group = h('div', { class: `choices${approval ? ' approval' : ''}`, role: 'group', 'aria-labelledby': titleId });
    for (const o of orderOptions(field.options)) {
      const tone = approval ? optionTone(o) : 'neutral';
      const isDefault = field.default === o.value;
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
    if (field.allowOther) {
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

  #send({ tapped = null } = {}) {
    const entry = this.entry;
    if (entry && (entry.state === 'sending' || entry.state === 'waiting')) { return; }
    const q = this.q;
    const { answer, problems } = buildAnswer(q, this.state);
    if (problems.length) { this.#fail(problems[0].message, problems[0].field); return; }
    const { service, hostId, sessionId } = this.answering;
    // Checked as the request will be sent: a stand-in request id is the same length as the
    // UUID the service gives it, so the 16 KiB check counts the same bytes.
    const data = answerRequest({ sessionId, questionId: q.id, request: '00000000-0000-4000-8000-000000000000', answer });
    const problem = checkAnswer(q, data);
    if (problem) { this.#fail(problem, problemField(q, problem) ?? (q.fields.length === 0 ? '' : null)); return; }
    this.#clearError();
    tapped?.setAttribute('aria-pressed', 'true');
    service.submit({ hostId, sessionId, questionId: q.id, answer });
  }

  #resend() {
    const { service, hostId, sessionId } = this.answering;
    service.resend(hostId, sessionId, this.q.id);
  }

  #show(entry) {
    const e = this.els;
    clearTimeout(this.#busyTimer);
    const shown = entry ? describeAnswer(entry, this.machineName) : { text: '', tone: '' };
    // Busy and refused were not typed: the person may answer again (after busy, in a
    // moment). Every other state holds the form: on its way, or final. Unconfirmed is final
    // and is never resent, from here or automatically.
    const again = !entry || (entry.state === 'done' && ['busy', 'refused'].includes(entry.outcome));
    const pause = entry?.state === 'done' && entry.outcome === 'busy';
    this.element.dataset.answer = entry ? (entry.state === 'done' ? (OUTCOME_TEXT[entry.outcome] ? entry.outcome : 'unconfirmed') : entry.state) : 'none';
    e.status.textContent = shown.text;
    e.status.className = `answer-status${shown.tone ? ` ${shown.tone}` : ''}`;
    e.status.hidden = !shown.text;
    e.form.setAttribute('aria-busy', shown.tone === 'progress' ? 'true' : 'false');
    e.controls.disabled = !again || pause;
    e.send.disabled = !again || pause;
    e.resend.hidden = entry?.state !== 'failed';
    if (pause) { this.#busyTimer = setTimeout(() => { e.controls.disabled = false; e.send.disabled = false; }, BUSY_PAUSE_MS); }
    // A tap question shows which option went; one that can be answered again shows none.
    const sent = !again && this.tap ? entry.answer?.fields?.[this.q.fields[0].name] : undefined;
    for (const b of this.element.querySelectorAll('.choice[data-value]')) {
      b.setAttribute('aria-pressed', typeof sent === 'string' && b.dataset.value === sent ? 'true' : 'false');
    }
  }
}
