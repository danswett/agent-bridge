// #1279: the words a Copy button shows after a press, and when they go.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { copyFeedback } from '../../js/ui/copy-feedback.js';

// Timers that run only when told to, so the linger is checked without waiting.
function fakeTimers() {
  const pending = new Map();
  let next = 1;
  return {
    set: (fn, ms) => { pending.set(next, { fn, ms }); return next++; },
    clear: (id) => { pending.delete(id); },
    pending,
    runAll() { for (const [id, t] of [...pending]) { pending.delete(id); t.fn(); } },
  };
}

function setup(copy, linger = 5000) {
  const timers = fakeTimers();
  const said = [];
  const fb = copyFeedback({ copy, say: (text, ok) => said.push([text, ok]), linger, timers });
  return { timers, said, fb };
}

test('a copy that worked says Copied for the linger, then says nothing', async () => {
  const { timers, said, fb } = setup(async () => true, 5000);
  assert.equal(await fb.press(), true);
  assert.deepEqual(said, [['Copied', true]]);
  assert.equal([...timers.pending.values()][0].ms, 5000);
  timers.runAll();
  assert.deepEqual(said.at(-1), ['', true]);
});

test('a copy that failed, or threw, says Couldn\'t copy', async () => {
  for (const copy of [async () => false, async () => { throw new Error('refused'); }, async () => undefined]) {
    const { said, fb } = setup(copy);
    assert.equal(await fb.press(), false);
    assert.deepEqual(said, [["Couldn't copy", false]]);
  }
});

test('a second press restarts the linger instead of letting the first clear its words', async () => {
  const { timers, fb } = setup(async () => true);
  await fb.press();
  await fb.press();
  assert.equal(timers.pending.size, 1);
});

test('cancel drops the pending clear', async () => {
  const { timers, fb } = setup(async () => true);
  await fb.press();
  fb.cancel();
  assert.equal(timers.pending.size, 0);
});
