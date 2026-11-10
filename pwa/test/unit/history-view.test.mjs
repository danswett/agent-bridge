// What the conversation draws before the latest exchange (#1285 S2a): which turns, and which
// meta line when there is nothing to show.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { WORDS, historyPlan, shownParts } from '../../js/ui/history-view.js';
import { HistoryStore } from '../../js/history-store.js';
import { readSession } from '../../js/protocol.js';

const turn = (id) => ({ id, prompt: null, parts: [] });
const snap = (phase, turns = []) => ({ phase, turns });

test('the latest turn is the card\'s live exchange and is left out', () => {
  const plan = historyPlan(snap('ready', [turn('a'), turn('b'), turn('c')]), 'c');
  assert.deepEqual(plan.turns.map((t) => t.id), ['a', 'b']);
  assert.equal(plan.note, null);
});

test('states say why nothing is drawn', () => {
  assert.equal(historyPlan(snap('loading')).note, WORDS.loading);
  assert.equal(historyPlan(snap('ready')).note, WORDS.empty);
  assert.equal(historyPlan(snap('unavailable')).note, WORDS.unavailable);
  assert.equal(historyPlan(snap('ready'), 'c').note, null);
  assert.equal(historyPlan(snap('failed')).note, null);
});

// #1628 (the owner's PWA, 9 Nov): the card draws the latest turn live and the history draws the
// rest. Turns that came only from activity, a few seconds apart, have no stored parts, so when the
// next one started each one's reply was replaced by the next turn's, until a page was read.
test('a turn only activity has told keeps its reply once the next turn is the live one', () => {
  const store = new HistoryStore({ current: () => null });
  const said = (n, text, replied) => readSession({
    id: 's1', agent: 'copilot', status: 'working', updatedAt: new Date().toISOString(),
    activity: { turn: `t${n}`, responseTurn: `t${replied}`, userMessage: `go ${n}`, response: text },
  });
  store.open('h', 's1', { session: said(1, 'first answer', 1) }).catch(() => {});
  store.live('h', said(2, 'second answer', 2));
  store.live('h', said(3, 'second answer', 2));

  const snapshot = store.get('h', 's1');
  const plan = historyPlan(snapshot, 't3');
  assert.deepEqual(plan.turns.map((t) => t.id), ['t1', 't2']);
  assert.deepEqual(plan.turns.map((t) => shownParts(t).map((p) => p.text)), [['first answer'], ['second answer']]);
});

test('a stored reply is drawn as it is, and the live words are not drawn beside it', () => {
  const stored = { id: 'a', parts: [{ kind: 'response', text: 'stored' }], live: { response: 'live' } };
  assert.deepEqual(shownParts(stored).map((p) => p.text), ['stored']);
});
