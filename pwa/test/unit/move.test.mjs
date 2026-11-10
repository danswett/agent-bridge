import { test } from 'node:test';
import assert from 'node:assert/strict';
import { moveView, moves } from '../../js/move.js';

const NAMES = ['handoffRequested', 'handoffConfirmed', 'artifactsPushed', 'newLaunched', 'newOnline'];
const at = (n, extra = {}) => ({ from: 'A', to: 'B', steps: NAMES.slice(0, n).map((name) => ({ name, at: 'x', ms: 1 })), ...extra });
const states = (m) => moveView(m).steps.map((s) => s.state);

test('steps reached are done and the next one is current', () => {
  assert.deepEqual(states(at(2)), ['done', 'done', 'current', 'notYet', 'notYet']);
  assert.equal(moveView(at(2)).summary, 'Step 3 of 5: Files sent');
});

test('a failure marks the next step failed and later ones not reached', () => {
  const v = moveView(at(2, { outcome: 'failed', reason: 'Too large.' }));
  assert.deepEqual(v.steps.map((s) => s.state), ['done', 'done', 'failed', 'notReached', 'notReached']);
  assert.equal(v.summary, 'Failed at step 3 of 5: Files sent');
  assert.equal(v.note, 'Too large. The original session is untouched and you can still answer it.');
});

test('refused and timeout fail too; launched stays moving', () => {
  assert.equal(moveView(at(0, { outcome: 'refused' })).steps[0].state, 'failed');
  assert.equal(moveView(at(4, { outcome: 'timeout' })).steps[4].state, 'failed');
  assert.equal(moveView(at(4, { outcome: 'launched' })).steps[4].state, 'current');
});

test('online means all five done and moved', () => {
  const v = moveView(at(5, { outcome: 'online' }));
  assert.ok(v.moved);
  assert.equal(v.summary, 'Moved to B');
});

test('the store tells subscribers, and clears', () => {
  const seen = [];
  const off = moves.subscribe((id, m) => seen.push([id, m?.to]));
  moves.set('s', at(1));
  moves.set('s', null);
  off();
  assert.deepEqual(seen, [['s', 'B'], ['s', undefined]]);
  assert.equal(moves.get('s'), undefined);
});
