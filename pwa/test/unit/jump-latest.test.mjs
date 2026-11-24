// The "Jump to latest" pill's rules (ui.md §6.4 H3, H3a, #1720): when it shows, and what it says.
// ScrollFollow's part of it (away, newCount, jump) is in scroll-follow.test.mjs.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { awayFrom, jumpText } from '../../js/ui/jump-latest.js';

test('it shows only when the view is more than a screen above the end', () => {
  const at = (top, max = 1000) => awayFrom({ top, max, client: 400 });
  assert.deepEqual([at(1000), at(700), at(600), at(599), at(0)], [false, false, false, true, true]);
  assert.equal(at(0, 300), false, 'a conversation of less than a screen of scroll never does');
});

test('it says what arrived meanwhile, and a question waiting first', () => {
  assert.deepEqual([{}, { count: 0 }, { count: 1 }, { count: 12 }].map(jumpText),
    ['Jump to latest', 'Jump to latest', '1 new · Jump to latest', '12 new · Jump to latest']);
  assert.equal(jumpText({ question: true }), 'Question · Jump to latest');
  assert.equal(jumpText({ count: 3, question: true }), 'Question · Jump to latest');
});
