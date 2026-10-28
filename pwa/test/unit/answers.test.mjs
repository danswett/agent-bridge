import { test } from 'node:test';
import assert from 'node:assert/strict';
import {
  Limits, answerability, answerRequest, buildAnswer, checkAnswer, draftText, initialState,
  optionTone, orderOptions, problemField, requestProblem, tapToAnswer, utf8Bytes,
} from '../../js/answers.js';
import { readAnswerResult, readQuestion } from '../../js/protocol.js';

// The same questions as tests/AgentBridge.Protocol.Tests/AnswerTests.cs, read as the app
// reads them, so each case below is a case there.
const form = (...fields) => readQuestion({ id: 'q1', kind: 'form', message: 'Pick', fields, askedAt: '2026-10-28T14:00:00.000Z' });
const choice = (name, { other = false, required = true } = {}) => ({
  name, type: 'choice', options: [{ value: 'a', label: 'A (Recommended)' }, { value: 'b' }],
  ...(other ? { allowOther: true } : {}), ...(required ? { required: true } : {}),
});
const fields = (f) => ({ fields: f });

test('a choice takes one of its option values and nothing else', () => {
  const q = form(choice('answer'));
  assert.equal(checkAnswer(q, fields({ answer: 'a' })), null);
  assert.match(checkAnswer(q, fields({ answer: 'A (Recommended)' })), /not one of the choices/);
  assert.match(checkAnswer(q, fields({ answer: { other: 'c' } })), /no other answer/);
});

test('other text is accepted only where the field allows it and must say something', () => {
  const q = form(choice('answer', { other: true }));
  assert.equal(checkAnswer(q, fields({ answer: { other: 'neither, use c' } })), null);
  assert.match(checkAnswer(q, fields({ answer: { other: '  ' } })), /empty/);
  assert.match(checkAnswer(q, fields({ answer: { other: 'x'.repeat(Limits.text + 1) } })), /longer than 4 KiB/);
  assert.match(checkAnswer(q, fields({ answer: { other: 'c', also: 1 } })), /unexpected properties beside other/);
  assert.match(checkAnswer(q, fields({ answer: 3 })), /expected a choice/);
});

test('a multi choice takes a list with at most one other', () => {
  const q = form({ name: 'pick', type: 'multi', options: [{ value: 'a' }, { value: 'b' }], allowOther: true });
  assert.equal(checkAnswer(q, fields({ pick: ['a', 'b', { other: 'c' }] })), null);
  assert.match(checkAnswer(q, fields({ pick: [{ other: 'c' }, { other: 'd' }] })), /at most one other/);
  assert.match(checkAnswer(q, fields({ pick: 'a' })), /expected a list/);
  assert.match(checkAnswer(q, fields({ pick: ['z'] })), /not one of the choices/);
});

test('booleans, numbers and text must have their own JSON type', () => {
  const q = form({ name: 'ok', type: 'boolean' }, { name: 'n', type: 'number' }, { name: 't', type: 'text' });
  assert.equal(checkAnswer(q, fields({ ok: true, n: 3, t: 'hi' })), null);
  assert.match(checkAnswer(q, fields({ ok: 'true' })), /true or false/);
  assert.match(checkAnswer(q, fields({ n: '3' })), /a number/);
  assert.match(checkAnswer(q, fields({ t: 3 })), /expected text/);
  // Not JSON numbers: JSON.stringify would send them as null.
  assert.match(checkAnswer(q, fields({ n: Number.NaN })), /a number/);
  assert.match(checkAnswer(q, fields({ n: Infinity })), /a number/);
});

test('required fields must be answered and unknown fields are refused', () => {
  const q = form(choice('answer'), choice('extra', { required: false }));
  assert.equal(checkAnswer(q, fields({ answer: 'b' })), null);
  assert.match(checkAnswer(q, fields({ extra: 'a' })), /needs an answer/);
  assert.match(checkAnswer(q, fields({ answer: 'a', nope: 1 })), /no field "nope"/);
  // A field left out entirely, when none is required, is no answer at all.
  assert.match(checkAnswer(form(choice('x', { required: false })), fields({})), /the answer is empty/);
});

test('a question without fields takes non-empty text, and one with fields takes no text', () => {
  const plain = form();
  assert.equal(checkAnswer(plain, { text: 'ship it' }), null);
  assert.match(checkAnswer(plain, { text: ' ' }), /empty/);
  assert.match(checkAnswer(plain, fields({ answer: 'a' })), /not fields/);
  assert.match(checkAnswer(form(choice('answer')), { text: 'a' }), /not a text answer/);
  assert.match(checkAnswer(plain, { text: 'é'.repeat(Limits.text / 2 + 1) }), /longer than 4 KiB/);
});

test('a truncated question is never answered remotely', () => {
  const q = { ...form(choice('answer')), truncated: true };
  assert.match(checkAnswer(q, fields({ answer: 'a' })), /answer it at the machine/);
  assert.deepEqual(answerability(q, ['sessions.read', 'sessions.answer']), { ok: false, why: 'truncated' });
});

test('an outcome from a newer host reads as unconfirmed, never as delivered', () => {
  assert.deepEqual(readAnswerResult({ outcome: 'queued', reason: 'later' }), { outcome: 'unconfirmed', reason: 'later' });
  assert.deepEqual(readAnswerResult({ outcome: 'delivered' }), { outcome: 'delivered', reason: null });
  assert.deepEqual(readAnswerResult(null), { outcome: 'unconfirmed', reason: null });
  for (const o of ['delivered', 'unconfirmed', 'stale', 'busy', 'refused']) { assert.equal(readAnswerResult({ outcome: o }).outcome, o); }
});

test('the 4 KiB limit counts UTF-8 bytes, not characters', () => {
  const q = form({ name: 't', type: 'text' });
  // 1,366 emoji are 2,732 UTF-16 code units and 5,464 bytes: over, though far fewer characters.
  const emoji = '😀'.repeat(1366);
  assert.equal(utf8Bytes(emoji), 5464);
  assert.match(checkAnswer(q, fields({ t: emoji })), /longer than 4 KiB/);
  assert.equal(checkAnswer(q, fields({ t: '😀'.repeat(1024) })), null);
  assert.equal(checkAnswer(q, fields({ t: 'x'.repeat(Limits.text) })), null);
  assert.match(checkAnswer(q, fields({ t: '€'.repeat(1366) })), /longer than 4 KiB/);
});

test('a whole request over 16 KiB is caught before it is sent', () => {
  const q = form(...['a', 'b', 'c', 'd', 'e'].map((name) => ({ name, type: 'text' })));
  const answer = { fields: Object.fromEntries(['a', 'b', 'c', 'd', 'e'].map((k) => [k, 'x'.repeat(4000)])) };
  assert.equal(checkAnswer(q, answer), null);
  const data = answerRequest({ sessionId: 's1', questionId: 'q1', request: 'r1', answer });
  assert.match(requestProblem(data), /longer than 16 KiB/);
  assert.equal(requestProblem(answerRequest({ sessionId: 's1', questionId: 'q1', request: 'r1', answer: { fields: { a: 'x' } } })), null);
  assert.deepEqual(answerRequest({ sessionId: 's1', questionId: 'q1', request: 'r1', answer: { text: 'hi' } }), { id: 's1', question: 'q1', request: 'r1', text: 'hi' });
});

test('answering is offered only by a machine that takes answers, for a question that arrived whole', () => {
  const q = form(choice('answer'));
  assert.deepEqual(answerability(q, ['sessions.read']), { ok: false, why: 'machine' });
  assert.deepEqual(answerability(q, ['sessions.read', 'sessions.answer']), { ok: true, why: null });
});

test('defaults pre-fill the form, and an untouched form sends them', () => {
  const q = form(
    { name: 'plan', type: 'choice', options: [{ value: 'go' }, { value: 'change' }], default: 'go', required: true },
    { name: 'areas', type: 'multi', options: [{ value: 'host' }, { value: 'pwa' }], default: ['pwa', 'host'] },
    { name: 'urgent', type: 'boolean', default: false },
    { name: 'notes', type: 'text', default: 'none' },
    { name: 'count', type: 'number', default: 3 },
  );
  const { answer, problems } = buildAnswer(q, initialState(q));
  assert.deepEqual(problems, []);
  // Multi values go in the options' order, whatever order they were ticked in.
  assert.deepEqual(answer, { fields: { plan: 'go', areas: ['host', 'pwa'], urgent: false, notes: 'none', count: 3 } });
  assert.equal(checkAnswer(q, answer), null);
});

test('a default that is not an option becomes Other text where Other is allowed', () => {
  const q = form(
    { name: 'c', type: 'choice', options: [{ value: 'a' }], allowOther: true, default: 'something else' },
    { name: 'm', type: 'multi', options: [{ value: 'a' }], allowOther: true, default: ['a', { other: 'and b' }] },
    { name: 'n', type: 'choice', options: [{ value: 'a' }], default: 'not an option' },
  );
  const st = initialState(q);
  assert.deepEqual(st.c, { selected: null, otherChosen: true, other: 'something else' });
  assert.deepEqual(st.m, { selected: ['a'], otherChosen: true, other: 'and b' });
  assert.deepEqual(st.n, { selected: null, otherChosen: false, other: '' });
  assert.deepEqual(buildAnswer(q, st).answer.fields, { c: { other: 'something else' }, m: ['a', { other: 'and b' }] });
});

test('blank text and empty numbers are no answer; a number box holding words is a problem', () => {
  const q = form({ name: 't', type: 'text', required: true }, { name: 'n', type: 'number', title: 'Count' });
  const st = initialState(q);
  st.t.text = '   ';
  assert.deepEqual(buildAnswer(q, st).answer.fields, {});
  assert.match(checkAnswer(q, buildAnswer(q, st).answer), /"t" needs an answer/);
  st.t.text = 'hi';
  st.n.text = '-2.5';
  assert.deepEqual(buildAnswer(q, st).answer.fields, { t: 'hi', n: -2.5 });
  st.n.bad = true;
  assert.deepEqual(buildAnswer(q, st).problems, [{ field: 'n', message: '"Count": expected a number' }]);
});

test('Other ticked with nothing typed is sent as such, for the check to name', () => {
  const q = form(choice('answer', { other: true }));
  const st = initialState(q);
  st.answer.otherChosen = true;
  const { answer } = buildAnswer(q, st);
  assert.deepEqual(answer.fields, { answer: { other: '' } });
  const problem = checkAnswer(q, answer);
  assert.match(problem, /"answer": the other answer is empty/);
  assert.equal(problemField(q, problem), 'answer');
});

test('a question without fields is answered with its text', () => {
  const q = form();
  assert.deepEqual(initialState(q), { text: '' });
  assert.deepEqual(buildAnswer(q, { text: 'ship it' }).answer, { text: 'ship it' });
});

test('options labelled (Recommended) come first; otherwise the agent\'s order stands', () => {
  const opts = [{ value: 'a', label: 'Rewrite it' }, { value: 'b', label: 'Patch it in place (Recommended)' }, { value: 'c', label: 'Leave it' }];
  assert.deepEqual(orderOptions(opts).map((o) => o.value), ['b', 'a', 'c']);
  assert.deepEqual(orderOptions(opts.slice(0, 1).concat(opts.slice(2))).map((o) => o.value), ['a', 'c']);
});

test('one single-choice field answers on a tap; anything else needs Send', () => {
  assert.equal(tapToAnswer(form(choice('answer'))), true);
  assert.equal(tapToAnswer(form(choice('answer'), choice('two'))), false);
  assert.equal(tapToAnswer(form({ name: 'm', type: 'multi', options: [] })), false);
  assert.equal(tapToAnswer(form({ name: 'b', type: 'boolean' })), false);
  assert.equal(tapToAnswer(form()), false);
});

test('an approval\'s choices are toned by what they do', () => {
  assert.equal(optionTone({ value: 'yes', label: 'Yes' }), 'allow');
  assert.equal(optionTone({ value: 'always', label: 'Yes, and don\'t ask again' }), 'allow');
  assert.equal(optionTone({ value: 'no', label: 'No, and tell the agent what to do' }), 'deny');
  assert.equal(optionTone({ value: 'denied', label: 'Deny' }), 'deny');
  assert.equal(optionTone({ value: 'x', label: 'Explain first' }), 'neutral');
});

test('only text the person typed counts as a draft worth keeping', () => {
  const q = form({ name: 'notes', type: 'text', default: 'pre-filled' }, choice('answer', { other: true }), { name: 'b', type: 'boolean' });
  const st = initialState(q);
  assert.equal(draftText(q, st), '');
  st.b.value = true;
  st.answer.selected = 'a';
  assert.equal(draftText(q, st), '');
  st.notes.text = 'my notes';
  st.answer.otherChosen = true;
  st.answer.other = 'my own';
  assert.equal(draftText(q, st), 'notes: my notes\nanswer: my own');
  assert.equal(draftText(form(), { text: 'just text' }), 'just text');
});
