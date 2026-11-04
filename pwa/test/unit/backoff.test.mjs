// The wait before the relay connection tries again (backoff.js, #578): full jitter up to
// min(30 s, 1 s * 2^n), and a 1012's first retry spread over 0-5 s or the relay's own hint.
// The host and the Windows app keep the same schedule (ReconnectBackoffTests.cs).
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { CAP_MS, RESTARTING, ReconnectBackoff, spreadFromMs } from '../../js/backoff.js';

const pinned = (r) => new ReconnectBackoff(() => r);
const schedule = (b, n) => Array.from({ length: n }, () => b.next());

test('the longest waits double from one second and stop at thirty', () => {
  const waits = schedule(pinned(0.999_999_999), 8);
  assert.deepEqual(waits.map((w) => Math.round(w / 1000)), [1, 2, 4, 8, 16, 30, 30, 30]);
  assert.ok(waits.every((w) => w <= CAP_MS));
});

test('a wait is drawn from zero up to its ceiling, not fixed at it', () => {
  assert.deepEqual(schedule(pinned(0), 8), [0, 0, 0, 0, 0, 0, 0, 0]);
  assert.deepEqual(schedule(pinned(0.5), 6), [500, 1000, 2000, 4000, 8000, 15000]);
});

test('a random source outside zero to one never stretches or reverses a wait', () => {
  for (const r of [1, 1.5, -1, NaN]) { assert.equal(pinned(r).next(), 0); }
});

test('only a successful connection starts the schedule again', () => {
  const b = pinned(0.5);
  schedule(b, 4);
  assert.equal(b.attempt, 4);
  b.reset();
  assert.equal(b.attempt, 0);
  assert.equal(b.next(), 500);
});

test('many attempts stay at the cap', () => {
  const b = pinned(0.999_999_999);
  schedule(b, 200);
  const w = b.next();
  assert.ok(w > 29000 && w <= CAP_MS, `${w}`);
});

test('after a 1012 the first retry is spread over five seconds, then the schedule carries on', () => {
  const b = pinned(0.5);
  assert.equal(b.next(RESTARTING, 'restarting'), 2500);
  assert.equal(b.next(1006, ''), 1000);
  const w = pinned(0.999_999_999).next(RESTARTING, 'restarting');
  assert.ok(w > 4900 && w <= 5000, `${w}`);
});

test('other close codes get the ordinary jittered wait, whatever their reason says', () => {
  for (const code of [1000, 1006, 4000, 4008, undefined]) {
    assert.equal(pinned(0.5).next(code, 'restarting;spread=60'), 500);
  }
});

test('a 1012 can widen or narrow its spread within one to sixty seconds', () => {
  for (const [reason, seconds] of [['restarting;spread=20', 20], ['restarting;spread=0', 1], ['restarting;spread=600', 60], ['restarting;spread=1', 1]]) {
    assert.equal(spreadFromMs(reason), seconds * 1000, reason);
    assert.equal(pinned(0.5).next(RESTARTING, reason), seconds * 500, reason);
  }
});

test('a malformed hint is ignored and the spread stays five seconds', () => {
  for (const reason of [undefined, null, '', 'restarting', 'restarting;spread=', 'restarting;spread=-5', 'restarting;spread=+5',
    'restarting;spread= 5', 'restarting;spread=5.5', 'restarting;spread=0x10', 'restarting;spread=99999999999999999999',
    'restarting;spread=5;spread=60', 'RESTARTING;spread=20', 'stopping;spread=20', 'restarting;wait=20', 'restarting;spread=２０',
    'restarting;spread=20\n', 42]) {
    assert.equal(spreadFromMs(reason), null, String(reason));
    assert.equal(pinned(0.5).next(RESTARTING, reason), 2500, String(reason));
  }
});

test('a narrow hint late in the schedule does not shorten the ordinary ceiling', () => {
  const b = pinned(0.5);
  schedule(b, 5);
  assert.equal(b.next(RESTARTING, 'restarting;spread=1'), 15000);
});
