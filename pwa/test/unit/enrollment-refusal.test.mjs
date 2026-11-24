import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { ENROLLMENT_REFUSALS, enrollmentRefusalMessage, isEnrollmentRefusal } from '../../js/roster/enrollment-refusal.js';

// The same eight codes, in the same order, as docs/protocol/devices.md ("Enrollment refusals") and the C# library.
test('the codes are the ones the protocol document lists, each with words for a person', () => {
  const doc = readFileSync(new URL('../../../docs/protocol/devices.md', import.meta.url), 'utf8').replace(/\r\n/g, '\n');
  const section = doc.split('## Enrollment refusals')[1].split('\n## ')[0];
  const listed = [...section.matchAll(/^\| `([a-z-]+)` \|/gm)].map((m) => m[1]);
  assert.deepEqual([...ENROLLMENT_REFUSALS], listed);
  assert.equal(listed.length, 8);
  for (const code of listed) {
    assert.ok(isEnrollmentRefusal(code));
    assert.notEqual(enrollmentRefusalMessage(code), '');
  }
  assert.equal(isEnrollmentRefusal('attempt-store'), false);
  assert.equal(enrollmentRefusalMessage('attempt-store'), '');
  assert.match(enrollmentRefusalMessage('removed'), /sign out and in again/);
});
