// The long press that lifts a session card (#592 D2): each platform's own.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { longPressMs } from '../../js/ui/reorder.js';

test('a long press is 400 ms on Android and 500 on iOS, iPadOS and everywhere else', () => {
  assert.equal(longPressMs('android'), 400);
  for (const p of ['ios', 'ipados', 'windows', 'macos', 'linux', 'chromeos', 'unknown']) { assert.equal(longPressMs(p), 500, p); }
});
