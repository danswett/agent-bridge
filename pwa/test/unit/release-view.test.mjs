import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { releaseLine } from '../../js/ui/release-view.js';

const pwa = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const SHA = '64a4019c0ffee0123456789abcdef0123456789a';

test('a stamped build reads as its 7-character release id and the date it was built, in the person\'s locale', () => {
  assert.deepEqual(releaseLine(SHA, '2026-11-07T20:15:00.000Z', 'en-US'), { id: '64a4019', date: '· built Nov 7, 2026', copy: '64a4019 · built Nov 7, 2026' });
  assert.notEqual(releaseLine(SHA, '2026-11-07T20:15:00.000Z', 'de-DE').date, releaseLine(SHA, '2026-11-07T20:15:00.000Z', 'en-US').date);
});

test('a local build with no stamp shows dev and no date, and an unreadable date is no date', () => {
  assert.deepEqual(releaseLine('dev', '', 'en-US'), { id: 'dev', date: '', copy: 'dev' });
  assert.equal(releaseLine(SHA, 'not a time', 'en-US').date, '');
  assert.equal(releaseLine(undefined, undefined, 'en-US').id, 'dev');
});

test('the bug-report bundle is still given the release id as appVersion', () => {
  assert.match(readFileSync(path.join(pwa, 'js', 'main.js'), 'utf8'), /appVersion: APP_VERSION,/);
});
