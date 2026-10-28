// A pending question, read-only. Shows what the agent asked and the shape of the answer
// it wants (options, defaults, free text), so the phone already tells you what a session
// is waiting on. Answering from here is the next slice; until then it says so plainly.
import { h } from './dom.js';
import { renderMarkdown } from '../markdown.js';

const TYPE_LABEL = { choice: 'Choose one', multi: 'Choose any', boolean: 'Yes or no', text: 'Text answer', number: 'Number' };

// A default can be any JSON value; it is shown as text, never interpreted.
export function formatValue(v) {
  if (v === null || v === undefined) { return ''; }
  if (typeof v === 'string') { return v; }
  if (typeof v === 'boolean') { return v ? 'Yes' : 'No'; }
  if (typeof v === 'number') { return String(v); }
  try { return JSON.stringify(v); } catch { return ''; }
}

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

function renderField(field, idBase) {
  const titleId = `${idBase}-t`;
  const parts = [];
  const title = field.title ?? field.name;
  parts.push(h('div', { class: 'field-head' },
    h('span', { class: 'field-title', id: titleId }, title),
    field.required ? h('span', { class: 'badge req' }, 'required') : null,
    h('span', { class: 'field-type' }, TYPE_LABEL[field.type])));
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

let seq = 0;

export function renderQuestion(q, { machineName } = {}) {
  const idBase = `q${++seq}`;
  const heading = q.kind === 'approval' ? 'Permission requested' : 'Waiting on you';
  const section = h('section', { class: 'question', 'aria-labelledby': `${idBase}-h`, dataset: { kind: q.kind, question: q.id } },
    h('h4', { class: 'question-head', id: `${idBase}-h` }, heading));
  const message = h('div', { class: 'md question-message' });
  message.append(renderMarkdown(q.message));
  section.append(message);
  if (q.fields.length) {
    section.append(h('ol', { class: 'fields' }, q.fields.map((f, k) => renderField(f, `${idBase}-f${k}`))));
  }
  const where = machineName ? ` on ${machineName}` : '';
  if (q.truncated) {
    section.append(h('p', { class: 'note warn', role: 'note' },
      `Part of this question didn't fit in the message from the machine. Answer it at the machine${where}.`));
  }
  section.append(h('p', { class: 'note', role: 'note' },
    `Read-only for now: answering from your phone is coming next. Answer in the session${where}.`));
  return section;
}
