import { test } from 'node:test';
import assert from 'node:assert/strict';
import { checkAnswer } from '../../js/answers.js';
import { checkLaunch } from '../../js/launch.js';
import { checkReply } from '../../js/replies.js';
import { readQuestion } from '../../js/protocol.js';

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
