import { test } from 'node:test';
import assert from 'node:assert/strict';
import { matchesSessionId } from '../../js/session-id-search.js';

// Owner request 09:33 PT Nov 24: search finds a session by its id too: the full GUID or any prefix of 4 or more hex characters,
// any case, with or without dashes. Fewer than 4 characters match the name only.
const ID = '0287fdf0-1a2b-4c3d-8e9f-0123456789ab';

test('a session id matches by its full GUID, an 8-hex prefix, a prefix with or without dashes, and in any case', () => {
  for (const query of [ID, ID.toUpperCase(), ID.replaceAll('-', ''), '0287fdf0', '0287FDF0', '0287-fdf0', '0287-FDF0-1a2b', '0287', '0287fd', '0287fdf0-1a']) {
    assert.equal(matchesSessionId(query, ID), true, query);
  }
});

test('fewer than 4 hex characters never match an id, and neither does a prefix the id does not start with', () => {
  for (const query of ['', '0', '02', '028', '0-2-8', '8700', 'fdf0', '0287fdf1', '0287fdf0-1a2b-4c3d-8e9f-0123456789abc', 'not hex', '0287 fdf0', 'zzzz']) {
    assert.equal(matchesSessionId(query, ID), false, query);
  }
});

test('a missing or non-text id matches nothing', () => {
  for (const id of [undefined, null, 42, {}]) { assert.equal(matchesSessionId('0287fdf0', id), false); }
});