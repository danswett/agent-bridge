// What the conversation draws before the latest exchange (#1285 S2a): which turns, and which
// meta line when there is nothing to show.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { WORDS, historyPlan } from '../../js/ui/history-view.js';

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
