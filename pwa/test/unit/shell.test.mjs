import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { parseRoute, routeHash } from '../../js/ui/shell.js';

test('New session has only a decorative plus, never a visible label that squeezes Sort', () => {
  const html = readFileSync(new URL('../../index.html', import.meta.url), 'utf8');
  const link = html.match(/<a\b[^>]*class="button new-session"[^>]*>([\s\S]*?)<\/a>/);
  assert.ok(link);
  assert.match(link[0], /aria-label="New session"/);
  assert.match(link[0], /title="New session"/);
  assert.match(link[1], /aria-hidden="true"/);
  assert.match(link[1], /#icon-plus/);
  assert.equal(link[1].trim(), '<svg class="ab-icon ab-icon-20" viewBox="0 0 20 20" aria-hidden="true" focusable="false"><use href="design/sprite.svg#icon-plus"/></svg>');
});

test('a page is read from the address hash, and anything else is the sessions page', () => {
  assert.deepEqual(parseRoute('#/usage'), { route: 'usage', rest: [] });
  assert.deepEqual(parseRoute('#/machines/'), { route: 'machines', rest: [] });
  assert.deepEqual(parseRoute(''), { route: 'sessions', rest: [] });
  assert.deepEqual(parseRoute('#/nowhere'), { route: 'sessions', rest: [] });
  assert.deepEqual(parseRoute('#top'), { route: 'sessions', rest: [] });
});

test('what follows a page is kept, decoded, and written back encoded', () => {
  assert.deepEqual(parseRoute('#/sessions/demo-laptop/a%2Fb'), { route: 'sessions', rest: ['demo-laptop', 'a/b'] });
  assert.equal(routeHash('sessions', 'demo-laptop', 'a/b'), '#/sessions/demo-laptop/a%2Fb');
  assert.equal(routeHash('nowhere'), '#/sessions');
  // A malformed escape is no page detail at all, never a thrown error at boot.
  assert.deepEqual(parseRoute('#/sessions/%E0%A4%A'), { route: 'sessions', rest: [] });
});