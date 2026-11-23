// The PWA's port of the roster library's joining side of the code exchange (src/AgentBridge.Roster/EnrollmentExchange.cs; docs/protocol/
// devices.md "The exchange state machines"): the same values for the same inputs, the same limits, no I/O.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fromHex, hex } from '../../js/roster/codec.js';
import { shown } from '../../js/roster/enrollment.js';
import { EnrollmentLockout, EnrollmentJoiner } from '../../js/enrollment-exchange.js';

const V = JSON.parse(readFileSync(new URL('../../../docs/protocol/vectors/roster.json', import.meta.url), 'utf8')).enrollment.exchanges[0];
const MIN = 60_000;
const WRONG = '000000';
const clock = () => { const c = { t: 1_000_000, now: () => c.t }; return c; };
const lockout = (c) => new EnrollmentLockout('ended-attempts', c.now);
const joiner = (c, lock = lockout(c), opts = {}) => EnrollmentJoiner.create(V.account, fromHex(V.h), lock, c.now, { nonce: fromHex(V.nN), ...opts });
// A joiner that has committed and taken the approver's offer.
const offered = async (c, lock) => { const j = await joiner(c, lock); j.commit(); await j.onOffer(fromHex(V.nA)); return j; };

test('the joiner computes the library\'s commitment, reveal and code 1 for the same values, and takes code 2 with or without the space', async () => {
  const c = clock();
  const j = await joiner(c);
  assert.equal(j.code1Shown, null);
  assert.equal(hex(j.commit()), V.commit);
  assert.equal(hex(await j.onOffer(fromHex(V.nA))), V.nN);
  assert.equal(j.code1Shown, shown(V.code1));
  assert.equal(j.enterCode2(shown(V.code2)), 'match');
  assert.equal((await (await offered(c)).enterCode2(V.code2)), 'match');
});

test('an offer before the commit, a second offer, or a peer value of the wrong shape is refused', async () => {
  const c = clock();
  assert.equal(await (await joiner(c)).onOffer(fromHex(V.nA)), null);
  const j = await joiner(c);
  j.commit();
  for (const bad of [new Uint8Array(5), null, 'x'.repeat(32), undefined]) assert.equal(await j.onOffer(bad), null);
  assert.ok(await j.onOffer(fromHex(V.nA)));
  assert.equal(await j.onOffer(fromHex(V.nA)), null);
});

test('anything but the right code is wrong, three wrong entries end the attempt, and an ended attempt stays ended', async () => {
  const j = await offered(clock());
  assert.deepEqual(['12345', 'abcdef'].map((bad) => j.enterCode2(bad)), ['wrong', 'wrong']);
  assert.equal(j.enterCode2(WRONG), 'ended');
  assert.equal(j.enterCode2(V.code2), 'ended');
});

test('three ended attempts in an hour lock joining for 15 minutes, and a lock refuses the commitment', async () => {
  const c = clock();
  const lock = lockout(c);
  for (let i = 0; i < 3; i++) {
    const j = await offered(c, lock);
    assert.deepEqual([j.enterCode2(WRONG), j.enterCode2(WRONG), j.enterCode2(WRONG)], ['wrong', 'wrong', 'ended']);
  }
  assert.equal((await joiner(c, lock)).commit(), null);
  c.t += 15 * MIN;
  assert.ok((await joiner(c, lock)).commit());
});

test('an attempt ends after five minutes and counts as an ended attempt, and a sooner deadline ends it sooner', async () => {
  const c = clock();
  const lock = lockout(c);
  for (let i = 0; i < 3; i++) { const j = await offered(c, lock); c.t += 5 * MIN; assert.equal(j.enterCode2(V.code2), 'ended'); }
  assert.equal((await joiner(c, lock)).commit(), null);
  const d = clock();
  const soon = await joiner(d, lockout(d), { expires: d.t + MIN });
  soon.commit();
  d.t += MIN;
  assert.equal(await soon.onOffer(fromHex(V.nA)), null);
});

test('the exchange has no I/O and no signing: it imports nothing but the roster values', () => {
  const src = readFileSync(new URL('../../js/enrollment-exchange.js', import.meta.url), 'utf8');
  assert.doesNotMatch(src, /transfer\.js|signing-key|cap-sign-key|commands|subtle|fetch|WebSocket|localStorage/);
});
