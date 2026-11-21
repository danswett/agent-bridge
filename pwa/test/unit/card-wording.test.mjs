import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import vm from 'node:vm';

const source = readFileSync(new URL('../../js/ui/session-card.js', import.meta.url), 'utf8')
  .replace(/^import .*;\r?$/gm, '').replace(/^export /gm, '');
const { cardStatusFacts, pauseCardSpinners } = vm.runInNewContext(
  `${source}\n({ cardStatusFacts, pauseCardSpinners })`,
);
const words = { rule: 3, facts: ['Working', 'task: Delegate Read logs'] };
const session = (id = 'a', compactions = 0) => ({ id, activity: { compactions } });

test('a working status chooses the original verb stably per session id', () => {
  const first = cardStatusFacts(session(), words);
  assert.equal(first[0], 'Synthesizing');
  assert.equal(cardStatusFacts(session('b'), words)[0], 'Deliberating');
  assert.equal(cardStatusFacts(session(), words)[0], first[0]);
  assert.equal(first[1], words.facts[1], 'delegate context is preserved');
  assert.equal(words.facts[0], 'Working', 'list-row status rules are unchanged');
});

test('waiting, idle and stale status lines never claim the session is working', () => {
  for (const [rule, label] of [[1, 'Waiting on you'], [6, 'Idle'], [12, 'Not responding']]) {
    assert.equal(cardStatusFacts(session(), { rule, facts: [label] })[0], label);
  }
  assert.equal(cardStatusFacts(session(), words, true)[0], 'Working');
});

test('compaction status says compacted once or compacted N times and is absent at zero', () => {
  for (const [count, expected] of [[0, null], [1, 'compacted once'], [3, 'compacted 3 times']]) {
    const facts = cardStatusFacts(session('a', count), words);
    assert.deepEqual(Array.from(facts.slice(2)), expected ? [expected] : []);
  }
});

test('card spinners pause immediately when hidden and resume without changing the verb', () => {
  const doc = new EventTarget();
  doc.visibilityState = 'hidden';
  const row = { dataset: {} }, view = { dataset: {} };
  const stop = pauseCardSpinners(doc, [row, view]);
  assert.equal(row.dataset.pageHidden, 'true', 'initially hidden tabs start paused');
  assert.equal(view.dataset.pageHidden, 'true');
  doc.visibilityState = 'visible';
  doc.dispatchEvent(new Event('visibilitychange'));
  assert.equal(row.dataset.pageHidden, undefined);
  assert.equal(view.dataset.pageHidden, undefined);
  doc.visibilityState = 'hidden';
  doc.dispatchEvent(new Event('visibilitychange'));
  assert.equal(view.dataset.pageHidden, 'true');
  stop();
  doc.visibilityState = 'visible';
  doc.dispatchEvent(new Event('visibilitychange'));
  assert.equal(view.dataset.pageHidden, 'true', 'disposed cards no longer listen');
});
