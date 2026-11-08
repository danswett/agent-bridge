// What history.js reads and checks beyond the shared vectors (protocol-vectors.test.mjs replays
// those): the strict fields of v1-sessions.md's Malformed records, and absent and null alike.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { checkHistoryRequest, readHistoryResult, readTurn } from '../../js/history.js';

const AT = '2026-10-28T04:22:43.182Z';

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

test('a page that repeats a turn id holds it once, at its first place', () => {
  const page = readHistoryResult({ turns: [{ id: 'a', at: AT, prompt: { text: 'one' } }, { id: 'a', at: AT, prompt: { text: 'again' } }, { id: 'b', at: AT }], start: { at: AT } });
  assert.deepEqual(page.turns.map((t) => t.id), ['a', 'b']);
  assert.equal(page.turns[0].prompt.text, 'one');
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
