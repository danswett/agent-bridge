import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { readQuestion } from '../../js/protocol.js';
import { describeToken, escapedApprove, marksExact, pieces, segments, shownDigest, tokens, warnings } from '../../js/escaped.js';

// An approval the host escaped anything in (#803, v1-sessions.md "Escaped approvals").

const vectors = JSON.parse(readFileSync(fileURLToPath(new URL('../../../docs/protocol/vectors/sessions.json', import.meta.url)), 'utf8'));
const escapedVector = vectors.records.Question.valid.find((v) => v.name === 'an-escaped-approval').json;
const CAN = ['sessions.answer', 'sessions.approveEscaped'];

test('an approval with nothing escaped keeps its one-tap Approve', () => {
  assert.equal(escapedApprove(readQuestion({ kind: 'approval', message: 'Run `ls`?' }), CAN), null);
});

test('an escaped approval is confirmed only where the machine offers it to this device', () => {
  const q = readQuestion(escapedVector);
  assert.equal(escapedApprove(q, CAN), 'confirm');
  assert.equal(escapedApprove(q, ['sessions.answer']), 'device');
});

test('an escaped approval the machine keeps for itself is approved only there', () => {
  const q = readQuestion({ ...escapedVector, escaped: { ...escapedVector.escaped, approve: 'machine' } });
  assert.equal(escapedApprove(q, CAN), 'machine');
});

test('an escaped approval with too many escapes to mark is approved only at the machine, and says why', () => {
  const q = readQuestion({ ...escapedVector, escaped: { ...escapedVector.escaped, approve: 'tooMany' } });
  assert.equal(q.escaped.approve, 'tooMany');
  assert.equal(escapedApprove(q, CAN), 'many');
});

test('escaped text that cannot be read is held at the machine, never read as nothing escaped', () => {
  for (const escaped of [7, 'x', {}, { ...escapedVector.escaped, marks: 'all' }, { ...escapedVector.escaped, shown: 'ABC' }, { ...escapedVector.escaped, approve: 'tap' }, { ...escapedVector.escaped, marks: [[1, 0]] }]) {
    const q = readQuestion({ ...escapedVector, escaped });
    assert.deepEqual(q.escaped, { shown: null, marks: [], approve: 'machine' }, JSON.stringify(escaped));
    assert.equal(escapedApprove(q, CAN), 'machine', JSON.stringify(escaped));
  }
});

test('a mark that is not exactly two whole numbers as sent is held at the machine, as the app holds it', () => {
  // The vector's own mark, with a third number, a null or text in it: read past as [start, length],
  // it would be offered the confirmation for a mark the host never sent (#836 review).
  const [[start, length]] = escapedVector.escaped.marks;
  for (const mark of [[start, length, 7], [start, null, length], [start, String(length)]]) {
    const q = readQuestion({ ...escapedVector, escaped: { ...escapedVector.escaped, marks: [mark] } });
    assert.deepEqual(q.escaped, { shown: null, marks: [], approve: 'machine' }, JSON.stringify(mark));
    assert.equal(escapedApprove(q, CAN), 'machine', JSON.stringify(mark));
  }
});

test('the digest of what this device shows is the one the host sent for the same message', async () => {
  assert.equal(await shownDigest(escapedVector.message), escapedVector.escaped.shown);
  assert.notEqual(await shownDigest(escapedVector.message.replace('\\u202E', '\\u202D')), escapedVector.escaped.shown);
});

test('the host\'s marks pick out exactly its escapes, and the text is drawn whole around them', () => {
  const q = readQuestion(escapedVector);
  const parts = segments(q.message, q.escaped.marks);
  assert.equal(parts.map((p) => p.text).join(''), q.message);
  assert.deepEqual(parts.filter((p) => p.mark).map((p) => p.text), ['\\u202E']);
});

test('marks that overlap or lie outside the message are not drawn, and the text is still whole', () => {
  const parts = segments('ab\\u200Bcd', [[2, 6], [4, 2], [9, 5]]);
  assert.equal(parts.map((p) => p.text).join(''), 'ab\\u200Bcd');
  assert.deepEqual(parts.filter((p) => p.mark).map((p) => p.text), ['\\u200B']);
});

test('a mark is exactly one escape as the host writes it, or the question is held at the machine', () => {
  const message = 'echo \\u202E; rm -rf work \\u{E0041}';
  assert.equal(marksExact(message, [[5, 6], [25, 9]]), true);
  // More than the escape, a lower-case or short one, overlapping, out of order or outside.
  for (const marks of [[[5, 19]], [[5, 5]], [[5, 6], [5, 6]], [[25, 9], [5, 6]], [[30, 9]], [[0, 4]]]) {
    assert.equal(marksExact(message, marks), false, JSON.stringify(marks));
  }
  assert.equal(marksExact('a \\u202e', [[2, 6]]), false);
  const q = readQuestion({ ...escapedVector, escaped: { ...escapedVector.escaped, marks: [[escapedVector.escaped.marks[0][0], 10]] } });
  assert.deepEqual(q.escaped, { shown: null, marks: [], approve: 'machine' });
  assert.equal(escapedApprove(q, CAN), 'machine');
});

test('a mark that is not a whole number, runs backwards or ends past the message is never taken, and the text stays whole', () => {
  const message = 'echo \\u202E done';
  for (const m of [[NaN, 6], [5, NaN], [5.5, 6], [5, -3], [5, 0], [-1, 6], [Number.MAX_SAFE_INTEGER, 6], [5, Number.MAX_SAFE_INTEGER], [2 ** 31 - 1, 6], ['5', 6], [5, Infinity]]) {
    assert.equal(marksExact(message, [m]), false, JSON.stringify(m));
    assert.equal(segments(message, [m]).map((p) => p.text).join(''), message, JSON.stringify(m));
    assert.equal(segments(message, [m, [5, 6]]).map((p) => p.text).join(''), message, JSON.stringify(m));
  }
});

test('every character inside a mark is kept, the text beside an escape as text', () => {
  const run = '\\u202E; rm -rf work';
  assert.deepEqual(pieces(run), [{ text: '\\u202E', token: true }, { text: '; rm -rf work', token: false }]);
  assert.equal(pieces(run).map((p) => p.text).join(''), run);
});

test('a run of escapes reads as each character by name', () => {
  assert.deepEqual(tokens('\\u202E\\u{E0041}'), ['\\u202E', '\\u{E0041}']);
  assert.equal(describeToken('\\u202E'), 'hidden character, right-to-left override');
  assert.equal(describeToken('\\u{E0041}'), 'hidden character, U+E0041');
  assert.equal(describeToken('\\u05D0'), 'right-to-left letter, U+05D0');
});

test('the warning says that bidi escapes reorder what is seen only when there are some', () => {
  const rlo = warnings({ message: 'a\\u202Eb', marks: [[1, 6]] });
  assert.ok(rlo.some((w) => w.includes('reorder')));
  const zwsp = warnings({ message: 'a\\u200Bb', marks: [[1, 6]] });
  assert.ok(!zwsp.some((w) => w.includes('reorder')));
  assert.ok(zwsp.some((w) => w.includes('aren\'t drawn')));
});
