import { test } from 'node:test';
import assert from 'node:assert/strict';
import { checkAnswer } from '../../js/answers.js';
import { checkLaunch } from '../../js/launch.js';
import { checkReply } from '../../js/replies.js';
import { readQuestion } from '../../js/protocol.js';
import { hasHidden, reveal, notesFor, unescapeShown, HIDDEN_NOTE } from '../../js/hidden-text.js';
import { parseApproval, codeText, approvalNotes, textOf, revealed } from '../../js/markdown.js';

// The cases of tests/AgentBridge.Protocol.Tests/InvisibleTests.cs: a reply, launch prompt or
// answer is judged by its first visible base character, never by what hides in front of it,
// exactly as the host judges it (#448).

// Each shows as nothing: format characters, default-ignorables (Hangul fillers, variation
// selectors, U+034F, tags), a mark with no base, U+2800, and an unassigned code point.
const LEAD_INS = [
  '\uFEFF', '\u200B', '\u2060', '\u200C\u200D \uFEFF', '\u{E0001}',
  '\u3164', '\u115F', '\uFFA0', '\u034F', '\uFE0F', '\u2065', '\u{E0000}', '\u{E0100}',
  '\u0301', '\u20DD', '\u2800', '\u0378', '\u3164 \u034F\uFE0F',
];

// What people write, which none of this may turn into nothing.
const WORDS = ['👍', '👨‍👩‍👧', '❤️', '1️⃣', 'नमस्ते', '안녕하세요', 'สวัสดี', 'مرحبا', 'ship it', '\u3164ship it'];

const REQUEST = '6f1c2a9e-4b7d-4f0a-9b8e-2d1c3e4f5a6b';
const reply = (text) => checkReply({ id: 's1', request: REQUEST, text });
const launch = (prompt) => checkLaunch({ prompt });
const plain = readQuestion({ id: 'q1', kind: 'form', message: 'Pick', fields: [], askedAt: '2026-10-28T14:00:00.000Z' });
const withOther = readQuestion({
  id: 'q1', kind: 'form', message: 'Pick', askedAt: '2026-10-28T14:00:00.000Z',
  fields: [{ name: 'answer', type: 'choice', options: [{ value: 'a' }], allowOther: true, required: true }],
});
const text = (t) => checkAnswer(plain, { id: 's1', question: 'q1', request: 'r1', text: t });
const other = (t) => checkAnswer(withOther, { id: 's1', question: 'q1', request: 'r1', fields: { answer: { other: t } } });
const name = (s) => JSON.stringify(s);

test('a reply or prompt starting a command behind invisible characters is refused', () => {
  for (const lead of LEAD_INS) {
    assert.match(reply(`${lead}/clear`), /^a reply cannot start with \//, name(lead));
    assert.match(reply(`${lead}!dir`), /^a reply cannot start with !/, name(lead));
    assert.match(launch(`${lead}/clear`), /^a prompt cannot start with \//, name(lead));
    assert.match(launch(`${lead}!rm -rf .`), /^a prompt cannot start with !/, name(lead));
    // A lone / too: an agent that drops the rest leaves it in its box for the next Enter.
    assert.match(reply(`${lead}/`), /^a reply cannot start with \//, name(lead));
  }
});

test('a reply or answer of nothing but invisible characters is empty', () => {
  for (const lead of LEAD_INS) {
    assert.equal(reply(lead), 'the reply is empty', name(lead));
    assert.equal(text(lead), 'the answer is empty', name(lead));
    assert.equal(other(lead), '"answer": the other answer is empty', name(lead));
  }
});

test('emoji, joined emoji and words in any script are still said', () => {
  for (const words of WORDS) {
    assert.equal(reply(words), null, name(words));
    assert.equal(launch(words), null, name(words));
    assert.equal(text(words), null, name(words));
    assert.equal(other(words), null, name(words));
    assert.equal(reply(`\uFEFF${words}`), null, name(words));
  }
});

test('a combining mark on whitespace or a line start is escaped and noted in approvals and code', () => {
  for (const [raw, shown] of [
    ['true \u0307#; echo RAN', 'true \\u0307#; echo RAN'],
    ['echo safe\n\u0307#; echo RAN', 'echo safe\n\\u0307#; echo RAN'],
    ['\u0307\u20DD', '\\u0307\\u20DD'],
    ['true \u0307\u093E\u{1D165}#', 'true \\u0307\\u093E\\u{1D165}#'],
    ['echo\t\u0307#', 'echo\t\\u0307#'],
    ['echo\u0085\u0307#', 'echo\\u0085\\u0307#'],
  ]) {
    assert.equal(hasHidden(raw), true, JSON.stringify(raw));
    assert.equal(reveal(raw), shown);
    assert.deepEqual(notesFor(raw), [HIDDEN_NOTE]);
    assert.equal(codeText(raw), shown);
    const message = `Allow shell?\n\n${raw}`;
    assert.equal(revealed(parseApproval(message)).map(textOf).join(''), `Allow shell?\n\n${shown}`);
    assert.deepEqual(approvalNotes(message), [HIDDEN_NOTE]);
  }
});

test('a combining mark on a visible base stays unchanged in approvals and code', () => {
  for (const raw of ['echo e\u0307\u20DD', 'echo \u{1F600}\u0307', 'echo \u0939\u093E']) {
    assert.equal(hasHidden(raw), false);
    assert.equal(reveal(raw), raw);
    assert.equal(codeText(raw), raw);
    assert.deepEqual(notesFor(raw), []);
    assert.deepEqual(approvalNotes(raw), []);
  }
});

test('host-written combining mark escapes are read back only on a blank base', () => {
  for (const [shown, raw] of [
    ['true \\u0307#; echo RAN', 'true \u0307#; echo RAN'],
    ['echo safe\n\\u0307#', 'echo safe\n\u0307#'],
    ['\\u0307\\u20DD\\u{1D165}', '\u0307\u20DD\u{1D165}'],
    ['echo\\u00A0\\u0307#', 'echo\u00A0\u0307#'],
    ['echo\\u0085\\u0307#', 'echo\u0085\u0307#'],
  ]) {
    assert.equal(unescapeShown(shown), raw);
    assert.deepEqual(approvalNotes(shown), [HIDDEN_NOTE]);
  }
  for (const shown of ['echo e\\u0307', '\\u0041\\u0307', '\\u0020\\u0307', '\\uD83D\\uDE00\\u0307', '\\u{110000}\\u0307']) {
    assert.equal(unescapeShown(shown), shown);
    assert.deepEqual(approvalNotes(shown), []);
  }
});
