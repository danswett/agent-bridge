import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import test from 'node:test';
import { createMockHost } from './mock-host.mjs';

const fixture = JSON.parse(readFileSync(new URL('../../docs/protocol/connector-read-conversations-v1.json', import.meta.url), 'utf8'));

test('every recorded request keeps host results, errors and response-before-snapshot ordering', () => {
  const reply = createMockHost();
  for (const [index, entry] of fixture.cases.entries()) {
    const messages = reply({ id: index + 1, ...entry.request });
    assert.deepEqual(messages[0], { re: index + 1, ...entry.response });
    assert.deepEqual(messages.slice(1).map(({ seq, ...event }) => event), entry.events ?? []);
  }
});

test('resubscription replays a replacement snapshot without restarting channel sequence', () => {
  const reply = createMockHost();
  const first = reply({ id: 1, op: 'sessions.subscribe', data: {} });
  const second = reply({ id: 2, op: 'sessions.subscribe', data: {} });
  assert.equal(first[0].result.count, 1);
  assert.deepEqual(first.slice(1).map(event => event.ev), ['session.upsert', 'sessions.synced']);
  assert.deepEqual([...first.slice(1), ...second.slice(1)].map(event => event.seq), [1, 2, 3, 4]);
});

test('get includes tool history while list and snapshot leave it out', () => {
  const reply = createMockHost();
  const id = '88888888-8888-4888-8888-888888888888';
  const full = reply({ id: 1, op: 'session.get', data: { id } })[0].result;
  assert.equal(full.activity.history[0].name, 'powershell');
  const listed = reply({ id: 2, op: 'sessions.list', data: {} })[0].result.sessions[0];
  const snapshot = reply({ id: 3, op: 'sessions.subscribe', data: {} })[1].data;
  assert.equal(Object.hasOwn(listed.activity, 'history'), false);
  assert.equal(Object.hasOwn(snapshot.activity, 'history'), false);
});

test('writes, unknown fixture data and reused or malformed IDs never look successful', () => {
  const reply = createMockHost();
  assert.equal(reply({ id: 1, op: 'session.reply', data: {} })[0].ok, false);
  assert.equal(reply({ id: 2, op: 'session.get', data: { id: 'unrecorded' } })[0].ok, false);
  assert.throws(() => reply({ id: 1, op: 'hello' }), /already used/);
  for (const id of [0, -1, 1.5, '3', true, Number.MAX_SAFE_INTEGER + 1]) {
    assert.throws(() => reply({ id, op: 'hello' }), /safe-integer/);
  }
});

test('the stdio mock emits only protocol lines and fails explicitly without echoing bad input', () => {
  const tool = fileURLToPath(new URL('./mock-host.mjs', import.meta.url));
  const success = spawnSync(process.execPath, [tool], {
    input: '{"id":7,"op":"hello","data":{}}\n', encoding: 'utf8', timeout: 10000,
  });
  assert.equal(success.status, 0, success.stderr);
  assert.equal(JSON.parse(success.stdout).re, 7);
  assert.equal(success.stderr, '');
  const bad = spawnSync(process.execPath, [tool], {
    input: '{"private-fixture-marker":\n', encoding: 'utf8', timeout: 10000,
  });
  assert.equal(bad.status, 1);
  assert.equal(bad.stdout, '');
  assert.doesNotMatch(bad.stderr, /private-fixture-marker/);
  assert.match(bad.stderr, /Invalid mock input/);
});
