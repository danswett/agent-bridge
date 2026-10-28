// Answering a question (session.answer): what the phone may send, built from what the
// person chose, and checked before it goes. docs/protocol/v1-sessions.md, "session.answer".
//
// checkAnswer mirrors src/AgentBridge.Protocol/Answers.cs (Answers.Check), case for case
// and with the same wording, so the app never sends something the host would refuse and
// a refusal it does get reads the same as one it worked out itself. Nothing here touches
// the DOM; test/unit/answers.test.mjs covers it in Node.

export const Limits = Object.freeze({
  // Free text in an answer: session.answer's text, a text field, or a field's other.
  text: 4 * 1024,
  // The whole session.answer request as sent.
  request: 16 * 1024,
});

const encoder = new TextEncoder();
// UTF-8 bytes, as the host counts them: a lone surrogate counts as U+FFFD (3 bytes) in both.
export const utf8Bytes = (s) => encoder.encode(s).length;

// C#'s string.IsNullOrWhiteSpace, near enough: \s covers the Unicode spaces it does.
const blank = (s) => s === null || s === undefined || /^\s*$/u.test(s);
const isObject = (v) => v !== null && typeof v === 'object' && !Array.isArray(v);
const has = (o, k) => Object.prototype.hasOwnProperty.call(o, k);

const textProblem = (text, what) =>
  (typeof text === 'string' && utf8Bytes(text) > Limits.text ? `${what} is longer than ${Limits.text / 1024} KiB` : null);

const label = (field) => field.title ?? field.name;

function choiceProblem(field, value) {
  if (typeof value === 'string') {
    return field.options?.some((o) => o.value === value) ? null : 'not one of the choices';
  }
  if (isObject(value) && has(value, 'other')) {
    if (field.allowOther !== true) { return 'this choice takes no other answer'; }
    if (typeof value.other !== 'string' || blank(value.other)) { return 'the other answer is empty'; }
    if (Object.keys(value).length !== 1) { return 'unexpected properties beside other'; }
    return textProblem(value.other, 'the other answer');
  }
  return 'expected a choice';
}

function multiProblem(field, value) {
  let others = 0;
  for (const item of value) {
    if (isObject(item)) { others++; }
    const problem = choiceProblem(field, item);
    if (problem) { return problem; }
  }
  return others > 1 ? 'at most one other answer' : null;
}

function valueProblem(field, value) {
  switch (field.type) {
    case 'choice': return choiceProblem(field, value);
    case 'multi': return Array.isArray(value) ? multiProblem(field, value) : 'expected a list of choices';
    case 'boolean': return typeof value === 'boolean' ? null : 'expected true or false';
    // NaN and Infinity are not JSON numbers: JSON.stringify would send null.
    case 'number': return typeof value === 'number' && Number.isFinite(value) ? null : 'expected a number';
    default: return typeof value === 'string' ? textProblem(value, 'the text') : 'expected text';
  }
}

// Null when the answer fits the question; otherwise why not, worded as the host words it.
// `answer` is the session.answer request data ({ id, question, request, fields | text });
// the size check counts all of it, so pass the whole request (answerRequest builds one).
// Whether the session is still asking the question is the host's to say (stale), not this.
export function checkAnswer(question, answer) {
  if (question.truncated === true) { return 'the question was too large to show in full; answer it at the machine'; }
  // Per-text limits alone don't bound the request: several text fields, each within its
  // own limit, can add up to more than a host promised to take.
  if (hostJsonBytes(answer) > Limits.request) { return `the answer is larger than ${Limits.request / 1024} KiB`; }
  const fields = isObject(answer.fields) ? answer.fields : null;
  if (question.fields.length === 0) {
    if (fields && Object.keys(fields).length > 0) { return 'this question takes a text answer, not fields'; }
    return textProblem(answer.text, 'the answer') ?? (blank(answer.text) ? 'the answer is empty' : null);
  }
  if (answer.text !== undefined && answer.text !== null) { return 'this question takes fields, not a text answer'; }
  const given = fields ?? {};
  for (const name of Object.keys(given)) {
    if (!question.fields.some((f) => f.name === name)) { return `the question has no field "${name}"`; }
  }
  for (const field of question.fields) {
    const value = has(given, field.name) ? given[field.name] : undefined;
    if (value === undefined || value === null) {
      if (field.required === true) { return `"${label(field)}" needs an answer`; }
      continue;
    }
    const problem = valueProblem(field, value);
    if (problem) { return `"${label(field)}": ${problem}`; }
  }
  return Object.keys(given).length === 0 ? 'the answer is empty' : null;
}

// The session.answer request data.
export function answerRequest({ sessionId, questionId, request, answer }) {
  const data = { id: sessionId, question: questionId, request };
  if (answer.text !== undefined) { data.text = answer.text; } else { data.fields = answer.fields; }
  return data;
}

// The UTF-8 bytes of a request as the C# host serializes it to check its size: Answers.Check
// measures JsonSerializer.SerializeToUtf8Bytes(answer, ProtocolJson.Options), not the bytes
// this app sent. That encoder (UnsafeRelaxedJsonEscaping) writes BMP text raw but escapes
// a character outside the BMP (most emoji) as a surrogate pair, \uXXXX\uXXXX: 12 bytes
// where JSON.stringify writes 4. It also escapes characters it doesn't consider safe to
// write raw; counted here as escaped are controls, format characters, unassigned and
// private-use code points and the line and paragraph separators, which may count a few
// bytes more than the host does but never fewer. Null and absent request properties are
// left out, as WhenWritingNull does.
export function hostJsonBytes(value) {
  return utf8Bytes(hostJson(value, true));
}

const SHORT_ESCAPES = { '"': '\\"', '\\': '\\\\', '\b': '\\b', '\t': '\\t', '\n': '\\n', '\f': '\\f', '\r': '\\r' };
const ESCAPED = /[\p{Cc}\p{Cf}\p{Cn}\p{Co}\p{Zl}\p{Zp}]/u;
const u = (n) => `\\u${n.toString(16).toUpperCase().padStart(4, '0')}`;

function hostString(s) {
  let out = '"';
  for (const ch of s) {
    const cp = ch.codePointAt(0);
    if (SHORT_ESCAPES[ch]) { out += SHORT_ESCAPES[ch]; } else if (cp > 0xffff) {
      out += u(0xd800 + ((cp - 0x10000) >> 10)) + u(0xdc00 + ((cp - 0x10000) & 0x3ff));
    } else if (cp >= 0xd800 && cp <= 0xdfff) {
      // A lone surrogate: .NET writes the replacement character.
      out += u(0xfffd);
    } else if (ESCAPED.test(ch)) { out += u(cp); } else { out += ch; }
  }
  return `${out}"`;
}

function hostJson(v, top = false) {
  if (typeof v === 'string') { return hostString(v); }
  if (Array.isArray(v)) { return `[${v.map((x) => hostJson(x)).join(',')}]`; }
  if (isObject(v)) {
    const parts = [];
    for (const [k, x] of Object.entries(v)) {
      if (x === undefined || (top && x === null)) { continue; }
      parts.push(`${hostString(k)}:${hostJson(x)}`);
    }
    return `{${parts.join(',')}}`;
  }
  return JSON.stringify(v) ?? 'null';
}

// Whether this phone may answer a question, and if not, why, for the read-only view.
export function answerability(question, capabilities) {
  if (!capabilities.includes('sessions.answer')) { return { ok: false, why: 'machine' }; }
  if (question.truncated) { return { ok: false, why: 'truncated' }; }
  return { ok: true, why: null };
}

const RECOMMENDED = /\(recommended\)/i;
export const isRecommended = (option) => RECOMMENDED.test(option.label);

// Recommended options first, as the old dashboard's cards and ask_user prompts put them;
// otherwise the agent's order.
export function orderOptions(options) {
  return [...options.filter(isRecommended), ...options.filter((o) => !isRecommended(o))];
}

// One choice field and nothing else: a tap on an option is the whole answer.
export function tapToAnswer(question) {
  return question.fields.length === 1 && question.fields[0].type === 'choice';
}

// How an approval's fixed choices look: allow, refuse, or neither.
const DENY = /^\s*(no\b|deny|denied|reject|decline|abort|cancel|don't|do not|never)/i;
const ALLOW = /^\s*(yes\b|approve|allow|accept|ok\b|proceed|always)/i;
export function optionTone(option) {
  const text = `${option.label}`;
  if (DENY.test(text) || DENY.test(option.value)) { return 'deny'; }
  if (ALLOW.test(text) || ALLOW.test(option.value)) { return 'allow'; }
  return 'neutral';
}

// ---- the form's state ----
//
// What the person has chosen, one entry per field, as the controls hold it:
//   choice  { selected: value|null, otherChosen, other }
//   multi   { selected: [values], otherChosen, other }
//   boolean { value: true|false|null }
//   text    { text }
//   number  { text, bad }   (bad: the input holds something that isn't a number)
// and for a question without fields, { text }.

const known = (field, v) => typeof v === 'string' && field.options.some((o) => o.value === v);

function initialField(field) {
  const d = field.default;
  switch (field.type) {
    case 'choice': {
      if (known(field, d)) { return { selected: d, otherChosen: false, other: '' }; }
      const other = otherDefault(field, d);
      return { selected: null, otherChosen: other !== null, other: other ?? '' };
    }
    case 'multi': {
      const list = Array.isArray(d) ? d : d === null || d === undefined ? [] : [d];
      const selected = list.filter((v) => known(field, v));
      const other = list.map((v) => otherDefault(field, v)).find((v) => v !== null) ?? null;
      return { selected, otherChosen: other !== null, other: other ?? '' };
    }
    case 'boolean': return { value: typeof d === 'boolean' ? d : null };
    case 'number': return { text: typeof d === 'number' && Number.isFinite(d) ? String(d) : typeof d === 'string' && d.trim() !== '' && Number.isFinite(Number(d)) ? d.trim() : '', bad: false };
    default: return { text: typeof d === 'string' ? d : typeof d === 'number' || typeof d === 'boolean' ? String(d) : '' };
  }
}

// A default that isn't one of the options can only be offered as Other text.
function otherDefault(field, v) {
  if (!field.allowOther) { return null; }
  if (typeof v === 'string' && v.trim() !== '' && !known(field, v)) { return v; }
  if (isObject(v) && typeof v.other === 'string') { return v.other; }
  return null;
}

export function initialState(question) {
  if (question.fields.length === 0) { return { text: '' }; }
  return Object.fromEntries(question.fields.map((f) => [f.name, initialField(f)]));
}

// A lone surrogate can't be read by the C# host at all (System.Text.Json throws on one),
// so text goes as well-formed UTF-16, with U+FFFD in its place, as a browser would encode it.
export const wellFormed = (s) => s.replace(/[\uD800-\uDBFF](?![\uDC00-\uDFFF])|(?<![\uD800-\uDBFF])[\uDC00-\uDFFF]/g, '\uFFFD');

// What the form says, as the request's { fields } or { text }, plus problems only the form
// can see (a number box holding something else). Blank text is no answer; an Other ticked
// with nothing in it is sent as such, for checkAnswer to name.
export function buildAnswer(question, state) {
  if (question.fields.length === 0) { return { answer: { text: wellFormed(state.text ?? '') }, problems: [] }; }
  const fields = {};
  const problems = [];
  for (const field of question.fields) {
    const st = state[field.name];
    if (!st) { continue; }
    switch (field.type) {
      case 'choice':
        if (st.otherChosen) { fields[field.name] = { other: wellFormed(st.other) }; } else if (st.selected !== null) { fields[field.name] = st.selected; }
        break;
      case 'multi': {
        const order = field.options.map((o) => o.value);
        const list = order.filter((v) => st.selected.includes(v));
        if (st.otherChosen) { list.push({ other: wellFormed(st.other) }); }
        if (list.length) { fields[field.name] = list; }
        break;
      }
      case 'boolean':
        if (st.value !== null) { fields[field.name] = st.value; }
        break;
      case 'number': {
        const text = st.text.trim();
        if (st.bad) { problems.push({ field: field.name, message: `"${label(field)}": expected a number` }); break; }
        if (text === '') { break; }
        const n = Number(text);
        if (Number.isFinite(n)) { fields[field.name] = n; } else { problems.push({ field: field.name, message: `"${label(field)}": expected a number` }); }
        break;
      }
      default:
        if (!blank(st.text)) { fields[field.name] = wellFormed(st.text); }
        break;
    }
  }
  return { answer: { fields }, problems };
}

// The field a problem from checkAnswer is about, by its quoted label, so the form can mark
// it and move focus there.
export function problemField(question, message) {
  const m = /^"([^"]*)"/.exec(message ?? '');
  if (!m) { return null; }
  return question.fields.find((f) => label(f) === m[1])?.name ?? null;
}

// Whether the person has put something into the form that leaving would lose: text they
// typed, beyond what the question pre-filled.
export function draftText(question, state) {
  if (!state) { return ''; }
  if (question.fields.length === 0) { return blank(state.text) ? '' : state.text; }
  const init = initialState(question);
  const bits = [];
  for (const field of question.fields) {
    const st = state[field.name];
    if (!st) { continue; }
    const typed = field.type === 'choice' || field.type === 'multi' ? (st.otherChosen ? st.other : '') : field.type === 'boolean' ? '' : st.text;
    const was = field.type === 'choice' || field.type === 'multi' ? init[field.name].other : field.type === 'boolean' ? '' : init[field.name].text;
    if (!blank(typed) && typed !== was) { bits.push(question.fields.length > 1 ? `${label(field)}: ${typed}` : typed); }
  }
  return bits.join('\n');
}
