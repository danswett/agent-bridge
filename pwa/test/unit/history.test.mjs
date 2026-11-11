// What history.js reads and checks beyond the shared vectors (protocol-vectors.test.mjs replays
// those): the strict fields of v1-sessions.md's Malformed records, and absent and null alike.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { checkHistoryRequest, closedQuestionIds, readHistoryResult, readTurn } from '../../js/history.js';

const AT = '2026-10-28T04:22:43.182Z';

const closed = (id, n) => ({ question: { id, askedAt: new Date(Date.parse(AT) + n * 1000).toISOString() } });

test('one newest-20 window interleaves stored and disjoint local closed questions by time', () => {
  const parts = Array.from({ length: 20 }, (_, i) => ({ kind: 'question', ...closed(`stored-${i}`, i) }));
  const kept = closedQuestionIds([{ parts }], [closed('local-old', 5.5), closed('local-new', 20), closed('active', 21)], 'active');
  assert.deepEqual([...kept], [
    ...Array.from({ length: 4 }, (_, i) => `stored-${i + 2}`), 'local-old',
    ...Array.from({ length: 14 }, (_, i) => `stored-${i + 6}`), 'local-new',
  ]);
});

test('repeated history and local ids are counted once before limiting, and active questions do not count', () => {
  const parts = Array.from({ length: 21 }, (_, i) => ({ kind: 'question', ...closed(`q${i}`, i) }));
  parts.push(parts[20], parts[20], parts[20]);
  assert.deepEqual([...closedQuestionIds([{ parts }], [closed('q20', 99)])], Array.from({ length: 20 }, (_, i) => `q${i + 1}`));
  assert.deepEqual([...closedQuestionIds([{ parts }], [closed('q20', 99)], 'q20')], Array.from({ length: 20 }, (_, i) => `q${i}`));
});

test('undated questions use their event time, with unreadable history dates oldest and stable', () => {
  const parts = [
    { kind: 'question', question: { id: 'missing' } },
    { kind: 'question', question: { id: 'bad', askedAt: 'unreadable' }, at: 'unreadable' },
    { kind: 'question', question: { id: 'stored', askedAt: 'unreadable' }, at: AT },
  ];
  assert.deepEqual([...closedQuestionIds([{ parts }], [{ question: { id: 'local' }, at: new Date(Date.parse(AT) + 1000).toISOString() }])], ['missing', 'bad', 'stored', 'local']);
});

test('a turn whose prompt is not a record is refused, never read as one with no prompt', () => {
  for (const prompt of ['Run the tests', 42, true, ['x']]) {
    assert.equal(readTurn({ id: 't', at: AT, prompt, parts: [] }), null, JSON.stringify(prompt));
  }
  assert.equal(readTurn({ id: 't', at: AT, prompt: null, parts: [] }).prompt, null);
  assert.equal(readTurn({ id: 't', at: AT, unreadable: true }).prompt, null);
});

test('a turn the host refused is left out of its page and the turns around it are kept', () => {
  const page = readHistoryResult({ turns: [{ id: 'a', at: AT }, { id: 'b', at: AT, prompt: { text: 'x', from: 7 } }, { id: 'c', at: AT }], start: { at: AT } });
  assert.deepEqual(page.turns.map((t) => t.id), ['a', 'c']);
});

test('a page that repeats a turn id keeps both turns with distinct keys and logs only the count', (t) => {
  const warnings = [];
  t.mock.method(console, 'warn', (...args) => warnings.push(args));
  const page = readHistoryResult({ turns: [{ id: 'a', at: AT, prompt: { text: 'one' } }, { id: 'a', at: AT, prompt: { text: 'again' } }, { id: 'b', at: AT }], start: { at: AT } });
  assert.equal(new Set(page.turns.map((t) => t.id)).size, 3);
  assert.deepEqual(page.turns.map((t) => t.prompt?.text), ['one', 'again', undefined]);
  assert.equal(page.turns[1].id, 'a', 'the newest occurrence retains the live identity');
  assert.deepEqual(warnings, [['Duplicate history turn ids: 1']]);
});

test('a page of more turns than a page may hold, with a cursor or without, and a cursor too long to send back, are not pages', () => {
  const turns = Array.from({ length: 51 }, (_, i) => ({ id: `t${i}`, at: AT }));
  assert.equal(readHistoryResult({ turns, before: 'h1.AA' }), null);
  assert.equal(readHistoryResult({ turns, start: { at: AT } }), null);
  assert.equal(readHistoryResult({ turns: turns.slice(0, 50), start: { at: AT } }).turns.length, 50);
  assert.equal(readHistoryResult({ turns: [{ id: 'a', at: AT }], before: 'x'.repeat(257) }), null);
  assert.notEqual(readHistoryResult({ turns: [{ id: 'a', at: AT }], before: 'x'.repeat(256) }), null);
});

test('a request names its cursor and limit or leaves them out, and null is as absent as undefined', () => {
  assert.equal(checkHistoryRequest({ id: 's', before: null, limit: null }), null);
  assert.equal(checkHistoryRequest({ id: 's', before: 'h1.x', limit: 50 }), null);
  assert.equal(checkHistoryRequest({ id: 's', limit: 1.5 }), 'limit must be 1 to 50');
  assert.equal(checkHistoryRequest({ id: 's', before: 7 }), 'a cursor is 1 to 256 bytes');
  assert.equal(checkHistoryRequest({ id: 's', before: 'é'.repeat(129) }), 'a cursor is 1 to 256 bytes');
});

test('an outcome the app does not know is unknown, never approved, and a part of an unknown kind is kept as unknown', () => {
  const turn = readTurn({
    id: 't', at: AT,
    parts: [
      { id: 'p0', kind: 'question', at: AT, outcome: 'maybe', question: { id: 'q', kind: 'approval', message: 'Run it?' } },
      { id: 'p1', kind: 'poll', at: AT },
    ],
  });
  assert.equal(turn.parts[0].outcome, 'unknown');
  assert.equal(turn.parts[1].kind, 'unknown');
});
