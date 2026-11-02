import { test } from 'node:test';
import assert from 'node:assert/strict';
import { parseRoute, routeHash } from '../../js/ui/shell.js';

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