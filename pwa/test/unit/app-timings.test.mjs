import { test } from 'node:test';
import assert from 'node:assert/strict';
import { AppTimings } from '../../js/app-timings.js';
import { TurnClock } from '../../js/turn-clock.js';

test('settled turns retain their settlement time and post only the aggregate fields', async () => {
  let now = 1700000000000;
  const clock = new TurnClock({ now: () => now });
  clock.sent('private-host', 'private-session'); clock.seen('private-host', 'private-session', 'working');
  now += 4200; clock.seen('private-host', 'private-session', 'idle');
  let request;
  const reporter = new AppTimings({ now: () => now, fetch: async (...args) => { request = args; return { status: 202 }; } });
  await reporter.post('https://relay.example', 'account-token', () => clock.drain());
  assert.equal(request[0], 'https://relay.example/v1/stats/app-timings');
  assert.deepEqual(JSON.parse(request[1].body), { samples: [{ app: 'PWA', route: 'send-to-settled', ms: 4200, at: 1700000004 }] });
  assert.deepEqual(request[1].headers, { Authorization: 'Bearer account-token', 'Content-Type': 'application/json' });
  assert.equal(request[1].redirect, 'error');
  assert.equal(request[1].credentials, 'omit');
  assert.deepEqual(clock.drain(), []);
});

test('a refused batch is dropped, and the next drain waits a minute without retrying it', async () => {
  let now = 100000;
  let drains = 0;
  const calls = [], logs = [];
  const reporter = new AppTimings({
    now: () => now, log: (line) => logs.push(line),
    fetch: async (_, r) => { calls.push(JSON.parse(r.body)); return { status: 429 }; },
  });
  const drain = () => { drains++; return [{ app: 'PWA', ms: 1, at: 100 }]; };
  await reporter.post('https://relay.example', 'token', drain);
  now += 59999; await reporter.post('https://relay.example', 'token', drain);
  assert.equal(drains, 1);
  now++; await reporter.post('https://relay.example', 'token', drain);
  assert.equal(drains, 2);
  assert.equal(calls.length, 2);
  assert.match(logs[0], /429/);
});

test('batches fit both relay limits, clamp slow turns, and a network failure is contained', async () => {
  const logs = [];
  const reporter = new AppTimings({ log: (line) => logs.push(line), fetch: async (_, r) => {
    assert.ok(new TextEncoder().encode(r.body).length <= 4096);
    const { samples } = JSON.parse(r.body);
    assert.ok(samples.length > 0 && samples.length <= 50);
    assert.ok(samples.every((s) => s.ms === 600000));
    throw new TypeError('network failed with private details');
  } });
  await reporter.post('https://relay.example', 'token', () => Array.from({ length: 100 }, () => ({ app: 'PWA', ms: 700000, at: 1700000000 })));
  assert.deepEqual(logs, ['app timings: request failed; batch dropped']);
});

test('a slow request cannot overlap another upload even after a minute', async () => {
  let now = 0, drains = 0, finish;
  const reporter = new AppTimings({ now: () => now, fetch: () => new Promise((resolve) => { finish = resolve; }) });
  const drain = () => { drains++; return [{ ms: 1, at: 1700000000 }]; };
  const pending = reporter.post('https://relay.example', 'token', drain);
  now += 60000; await reporter.post('https://relay.example', 'token', drain);
  assert.equal(drains, 1);
  finish({ status: 202 }); await pending;
});
