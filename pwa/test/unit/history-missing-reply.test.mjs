import { test } from 'node:test';
import assert from 'node:assert/strict';
import { HistoryStore } from '../../js/history-store.js';
import { HISTORY, readHistoryResult } from '../../js/history.js';
import { shownParts } from '../../js/ui/history-view.js';

const A = { id: 'a', at: '2026-11-11T06:37:19Z', prompt: { text: 'Question A', from: 'person' },
  parts: [0, 1, 2, 3].map((turnId) => ({ id: `a-${turnId}`, turnId, kind: 'response', text: turnId === 3 ? 'Answer A' : '' })) };
const B = { id: 'b', at: '2026-11-11T06:37:59Z', prompt: { text: 'Scheduled B', from: 'schedule', label: 'Watch' },
  parts: [0, 1].map((turnId) => ({ id: `b-${turnId}`, turnId, kind: 'response', text: turnId === 1 ? 'Answer B' : '' })) };
const said = (t, response = null, responseTurn = t.id) => ({ id: 's', agent: 'copilot',
  activity: { turn: t.id, responseTurn, userMessage: t.prompt.text, userMessageFrom: t.prompt.from, response } });
const words = (store) => store.get('h', 's').turns.flatMap((t) => [t.prompt.text, ...shownParts(t).filter((p) => p.kind === 'response' && p.text).map((p) => p.text)]);
async function setup() {
  let page = [];
  let calls = 0;
  const client = { capabilities: [HISTORY.capability], history: async () => { calls++; return readHistoryResult({ turns: page }); } };
  const store = new HistoryStore({ current: () => ({ client }) });
  await store.open('h', 's');
  return { store, serve: (turns) => { page = turns; }, calls: () => calls };
}
const settle = () => new Promise(setImmediate);

for (const ordered of [false, true]) {
  test(`${ordered ? 'in-order' : 'jumped'} snapshots retain the question reply before the scheduled prompt`, async () => {
    const { store, serve, calls } = await setup();
    store.live('h', said(A));
    if (ordered) { store.live('h', said(A, 'Answer A')); }
    serve([A, B]);
    store.live('h', said(B, 'Answer B'));
    store.live('h', said(B, 'Answer B'));
    await settle();
    assert.deepEqual(words(store), ['Question A', 'Answer A', 'Scheduled B', 'Answer B']);
    assert.equal(calls(), ordered ? 1 : 2, 'one catch-up request, not one per update');
  });
}

test('a lagging newest page and an empty page never discard an unanswered provisional turn', async () => {
  const { store, serve } = await setup();
  store.live('h', said(A));
  serve([B]);
  store.live('h', said(B, 'Answer B'));
  await settle();
  assert.deepEqual(words(store), ['Question A', 'Scheduled B', 'Answer B']);
  serve([]);
  await store.open('h', 's');
  assert.ok(store.get('h', 's').turns.some((t) => t.id === 'a'));
  serve([A, B]);
  await store.open('h', 's');
  assert.deepEqual(words(store), ['Question A', 'Answer A', 'Scheduled B', 'Answer B']);
});

test('stored-only history retains replies with restarted assistant indexes and empty earlier messages', async () => {
  const { store, serve } = await setup();
  serve([A, B]);
  await store.open('h', 's');
  assert.deepEqual(words(store), ['Question A', 'Answer A', 'Scheduled B', 'Answer B']);
});

test('empty current or earlier activity never replaces reply text', async () => {
  const { store } = await setup();
  store.live('h', said(A, 'Answer A'));
  store.live('h', said(A, ''));
  store.live('h', said(B, '', A.id));
  assert.equal(store.get('h', 's').turns[0].live.response, 'Answer A');
});
