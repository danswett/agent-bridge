import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

test('the service worker header comment stays within 100 characters per line', () => {
  const lines = readFileSync(new URL('../../sw.js', import.meta.url), 'utf8').split(/\r?\n/);
  assert.match(lines[0], /^\/\//, 'sw.js starts with a header comment');
  const tooWide = [];
  for (let i = 0; i < lines.length && lines[i].startsWith('//'); i++) {
    if (lines[i].length > 100) {
      tooWide.push({ line: i + 1, length: lines[i].length });
    }
  }
  assert.deepEqual(tooWide, [], 'sw.js header comment lines are at most 100 characters');
});
