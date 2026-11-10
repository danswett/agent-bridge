import { test } from 'node:test';
import assert from 'node:assert/strict';
import { SessionsStore, byActivity } from '../../js/sessions-store.js';

const T = (n) => `2026-11-09T10:00:${String(n).padStart(2, '0')}.000Z`;
const raw = (over = {}) => ({ id: 's1', agent: 'copilot', status: 'working', updatedAt: T(0), activity: { turnCount: 1 }, ...over });

test('a first-seen session starts at its host updatedAt', () => {
  const store = new SessionsStore();
  assert.equal(store.upsert(raw({ updatedAt: T(5) })).activityAt, T(5));
});

test('a finished turn moves activityAt', () => {
  const store = new SessionsStore();
  store.upsert(raw());
  const s = store.upsert(raw({ status: 'idle', updatedAt: T(9), activity: { turnCount: 2 } }));
  assert.equal(s.activityAt, T(9));
});

test('a tool tick, background count, title and model change do not move activityAt', () => {
  const store = new SessionsStore();
  store.upsert(raw());
  const s = store.upsert(raw({
    updatedAt: T(20), title: 'New title', model: 'm2',
    activity: { turnCount: 1, currentTool: { id: 't', name: 'Bash', status: 'running' }, response: 'text', background: { agents: 1, shells: 0 } },
  }));
  assert.equal(s.activityAt, T(0));
});

test('a prompt and a question asked or answered each move activityAt', () => {
  const store = new SessionsStore();
  store.upsert(raw());
  assert.equal(store.upsert(raw({ updatedAt: T(2), activity: { turnCount: 1, userMessage: 'go' } })).activityAt, T(2));
  const q = { id: 'q1', kind: 'ask', message: 'ok?' };
  assert.equal(store.upsert(raw({ updatedAt: T(3), activity: { turnCount: 1, userMessage: 'go' }, question: q })).activityAt, T(3));
  assert.equal(store.upsert(raw({ updatedAt: T(4), activity: { turnCount: 1, userMessage: 'go' } })).activityAt, T(4));
});

test('activityAt never moves back', () => {
  const store = new SessionsStore();
  store.upsert(raw({ updatedAt: T(30) }));
  const s = store.upsert(raw({ status: 'idle', updatedAt: T(10) }));
  assert.equal(s.activityAt, T(30));
});

test('a snapshot after a reconnect does not change activityAt', () => {
  const store = new SessionsStore();
  store.upsert(raw());
  store.clear();
  const s = store.upsert(raw({ status: 'idle', updatedAt: T(40), activity: { turnCount: 5 } }), { snapshot: true });
  assert.equal(s.activityAt, T(0));
});

test('ties sort by session id, then machine, whatever the input order', () => {
  const at = (hostId, id) => ({ hostId, session: { id, updatedAt: T(0), activityAt: T(1) } });
  const rows = [at('m2', 'b'), at('m2', 'a'), at('m1', 'a'), { hostId: 'm9', session: { id: 'z', updatedAt: T(0), activityAt: T(7) } }];
  const keys = (list) => list.map((e) => `${e.session.id}@${e.hostId}`);
  const want = ['z@m9', 'a@m1', 'a@m2', 'b@m2'];
  assert.deepEqual(keys([...rows].sort(byActivity)), want);
  assert.deepEqual(keys([...rows].reverse().sort(byActivity)), want);
});

test('the store lists the most recently active first, not the most recently updated', () => {
  const store = new SessionsStore();
  store.upsert(raw({ id: 'a', updatedAt: T(1) }));
  store.upsert(raw({ id: 'b', updatedAt: T(2) }));
  store.upsert(raw({ id: 'a', updatedAt: T(50), activity: { turnCount: 1, currentTool: { id: 't', name: 'Bash', status: 'running' } } }));
  assert.deepEqual(store.list().map((s) => s.id), ['b', 'a']);
});
