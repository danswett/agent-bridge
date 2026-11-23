// The PWA's port of the roster library's approving side of the code exchange (src/AgentBridge.Roster/EnrollmentExchange.cs), run against
// the joining side from enrollment-exchange.js: the two ends compute the library's values, and the limits are the library's.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fromHex, hex } from '../../js/roster/codec.js';
import { shown } from '../../js/roster/enrollment.js';
import { EnrollmentLockout, EnrollmentJoiner } from '../../js/enrollment-exchange.js';
import { EnrollmentApprover } from '../../js/enrollment-approver.js';

const V = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/roster.json', import.meta.url), 'utf8')).enrollment.exchanges[0];
const MIN = 60_000;
const WRONG = '000000';
const clock = () => { const c = { t: 1_000_000, now: () => c.t }; return c; };

// One exchange with the vector's values: the joiner has committed and the approver holds the commitment.
async function pair({ lockA, c = clock(), pendingId = 'p1' } = {}) {
  const h = fromHex(V.h);
  const join = await EnrollmentJoiner.create(V.account, h, new EnrollmentLockout('ended-attempts', c.now), c.now, { nonce: fromHex(V.nN) });
  const approver = new EnrollmentApprover(V.account, h, pendingId, lockA ?? new EnrollmentLockout('wrong-entries', c.now), c.now);
  const commit = join.commit();
  const nA = await approver.onCommit(commit, fromHex(V.nA));
  return { c, join, approver, commit, nA };
}
async function exchange(p) { assert.equal(await p.approver.onReveal(await p.join.onOffer(p.nA)), true); }

test('the approver\'s code 2 and transcript exist only after code 1 is typed right, and are the library\'s values', async () => {
  const p = await pair();
  assert.equal(hex(p.nA), V.nA);
  await exchange(p);
  assert.deepEqual([p.approver.code2Shown, p.approver.transcriptToSign], [null, null]);
  assert.equal(p.approver.enterCode1(shown(V.code1)), 'match');
  assert.equal(p.approver.code2Shown, shown(V.code2));
  assert.equal(hex(p.approver.transcriptToSign), V.transcript);
  assert.equal(p.join.enterCode2(p.approver.code2Shown), 'match');
});

test('a reveal that is not the committed nonce abandons the attempt, and a second commit or a peer value of the wrong shape is refused', async () => {
  const p = await pair();
  assert.equal(await p.approver.onCommit(p.commit), null);
  assert.equal(await p.approver.onReveal(new Uint8Array(32)), false);
  assert.equal(p.approver.enterCode1(V.code1), 'ended');
  for (const bad of [new Uint8Array(31), null, 'x'.repeat(32)]) assert.equal(await (await pair()).approver.onCommit(bad), null);
  assert.equal(await (await pair()).approver.onReveal('x'.repeat(32)), false);
  const q = await pair();
  await exchange(q);
  assert.equal(await q.approver.onReveal(fromHex(V.nN)), false);
});

test('anything but the right code is wrong, three wrong entries end the attempt, and code 2 stays hidden', async () => {
  const p = await pair();
  await exchange(p);
  assert.deepEqual(['12345', 'abcdef'].map((bad) => p.approver.enterCode1(bad)), ['wrong', 'wrong']);
  assert.equal(p.approver.enterCode1(WRONG), 'ended');
  assert.equal(p.approver.enterCode1(V.code1), 'ended');
  assert.equal(p.approver.code2Shown, null);
});

test('three wrong code 1 entries in an hour over any devices lock the approver for 15 minutes', async () => {
  const c = clock();
  const lockA = new EnrollmentLockout('wrong-entries', c.now);
  const first = await pair({ lockA, c, pendingId: 'a' });
  await exchange(first);
  for (let i = 0; i < 3; i++) first.approver.enterCode1(WRONG);
  const other = await pair({ lockA, c, pendingId: 'b' });
  await exchange(other);
  assert.equal(other.approver.enterCode1(V.code1), 'locked');
  c.t += 15 * MIN;
  const later = await pair({ lockA, c, pendingId: 'b' });
  await exchange(later);
  assert.equal(later.approver.enterCode1(V.code1), 'match');
});

test('an attempt ends after five minutes, and that blocks no pending device at an approver', async () => {
  const c = clock();
  const lockA = new EnrollmentLockout('wrong-entries', c.now);
  const p = await pair({ lockA, c });
  await exchange(p);
  c.t += 5 * MIN;
  assert.equal(p.approver.enterCode1(V.code1), 'ended');
  assert.equal(lockA.isBlocked('p1'), false);
});

test('a second reveal while the first is being checked does not end the attempt, and a code that is not text is a wrong entry', async () => {
  const p = await pair();
  const reveal = await p.join.onOffer(p.nA);
  const [a, b] = await Promise.all([p.approver.onReveal(reveal), p.approver.onReveal(new Uint8Array(32))]);
  assert.deepEqual([a, b], [true, false]);
  assert.equal(p.approver.enterCode1(123456), 'wrong');
  assert.equal(p.approver.enterCode1(shown(V.code1)), 'match');
});

test('the approver lockout blocks a pending device for 15 minutes, locks over any devices, forgets marks after an hour and counts no expiry', () => {
  const c = clock();
  const lock = new EnrollmentLockout('wrong-entries', c.now);
  assert.deepEqual([0, 1, 2].map(() => lock.recordWrong('a')), [false, false, true]);
  assert.equal(lock.isBlocked('a'), true);
  assert.equal(lock.isBlocked('b'), true, 'three marks in an hour lock the approver for any device');
  c.t += 15 * MIN;
  assert.equal(lock.isBlocked('a'), false);
  const slow = new EnrollmentLockout('wrong-entries', c.now);
  slow.recordWrong('x'); c.t += 61 * MIN; slow.recordWrong('y'); slow.recordWrong('z');
  assert.equal(slow.isBlocked('w'), false);
  slow.recordEnded('x'); slow.recordEnded('x'); slow.recordEnded('x');
  assert.equal(slow.isBlocked('w'), false);
});
