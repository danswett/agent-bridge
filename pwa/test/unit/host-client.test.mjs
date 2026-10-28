import { test } from 'node:test';
import assert from 'node:assert/strict';
import { HostClient } from '../../js/host-client.js';
import { SessionsStore } from '../../js/sessions-store.js';

// A transport that records what the client sends and lets the test play the host.
function fakeTransport() {
  return {
    sent: [],
    onmessage: null,
    onclose: null,
    closed: false,
    send(m) { this.sent.push(m); return Promise.resolve(); },
    close() { this.closed = true; },
    host(m) { this.onmessage(JSON.parse(JSON.stringify(m))); },
    lastRequest(op) { return [...this.sent].reverse().find((m) => m.op === op); },
  };
}

const flush = () => new Promise((r) => setImmediate(r));
const session = (id, extra = {}) => ({ id, agent: 'copilot', status: 'idle', statusSince: '2026-10-28T04:00:00.000Z', updatedAt: '2026-10-28T04:00:00.000Z', activity: {}, ...extra });

async function subscribed(t, store) {
  const client = new HostClient(t, { store, timeoutMs: 500 });
  const p = client.subscribe();
  await flush();
  t.host({ re: t.lastRequest('sessions.subscribe').id, ok: true, result: { revision: 1, count: 0 } });
  await p;
  return client;
}

test('a response settles the request it answers, by id', async () => {
  const t = fakeTransport();
  const client = new HostClient(t, { store: new SessionsStore() });
  const a = client.request('one');
  const b = client.request('two');
  await flush();
  t.host({ re: t.sent[1].id, ok: true, result: 'second' });
  t.host({ re: t.sent[0].id, ok: false, error: 'nope' });
  assert.equal(await b, 'second');
  await assert.rejects(a, /one: nope/);
});

test('a request on a dead transport fails at once instead of timing out', async () => {
  const t = fakeTransport();
  t.send = () => Promise.reject(new Error('not connected to the relay'));
  const client = new HostClient(t, { timeoutMs: 60000 });
  await assert.rejects(client.request('hello'), /not connected to the relay/);
});

test('closing the transport fails every pending request', async () => {
  const t = fakeTransport();
  const client = new HostClient(t, { timeoutMs: 60000 });
  const p = client.request('hello');
  t.onclose(new Error('the relay connection closed'));
  await assert.rejects(p, /relay connection closed/);
  await assert.rejects(client.request('hello'), /closed/);
});

test('the subscription snapshot shows sessions as they arrive and drops stale ones on synced', async () => {
  const t = fakeTransport();
  const store = new SessionsStore();
  store.upsert(session('gone-while-away'));
  store.upsert(session('still-here'));
  await subscribed(t, store);
  t.host({ ev: 'session.upsert', seq: 1, data: session('still-here', { title: 'updated' }) });
  t.host({ ev: 'session.upsert', seq: 2, data: session('new') });
  // Before synced, nothing is dropped: the old list stays on screen while the snapshot comes in.
  assert.deepEqual(store.list().map((s) => s.id).sort(), ['gone-while-away', 'new', 'still-here']);
  t.host({ ev: 'sessions.synced', seq: 3, data: { revision: 9 } });
  assert.deepEqual(store.list().map((s) => s.id).sort(), ['new', 'still-here']);
  assert.equal(store.get('still-here').title, 'updated');
  assert.equal(store.revision, 9);
});

test('a session removed during the snapshot stays removed after synced', async () => {
  const t = fakeTransport();
  const store = new SessionsStore();
  await subscribed(t, store);
  t.host({ ev: 'session.upsert', seq: 1, data: session('a') });
  t.host({ ev: 'session.remove', seq: 2, data: { id: 'a' } });
  t.host({ ev: 'sessions.synced', seq: 3, data: { revision: 2 } });
  assert.equal(store.size, 0);
});

test('live upserts and removes after synced change the list', async () => {
  const t = fakeTransport();
  const store = new SessionsStore();
  await subscribed(t, store);
  t.host({ ev: 'sessions.synced', seq: 1, data: { revision: 1 } });
  t.host({ ev: 'session.upsert', seq: 2, data: session('a', { updatedAt: '2026-10-28T04:00:00.000Z' }) });
  t.host({ ev: 'session.upsert', seq: 3, data: session('b', { updatedAt: '2026-10-28T04:00:01.000Z' }) });
  assert.deepEqual(store.list().map((s) => s.id), ['b', 'a']);
  t.host({ ev: 'session.upsert', seq: 4, data: session('a', { updatedAt: '2026-10-28T04:00:02.000Z' }) });
  assert.deepEqual(store.list().map((s) => s.id), ['a', 'b'], 'an update moves a session to the top');
  t.host({ ev: 'session.remove', seq: 5, data: { id: 'b' } });
  assert.deepEqual(store.list().map((s) => s.id), ['a']);
});

test('a gap in seq subscribes again, once, and the new snapshot replaces the list', async () => {
  const t = fakeTransport();
  const store = new SessionsStore();
  await subscribed(t, store);
  t.host({ ev: 'session.upsert', seq: 1, data: session('a') });
  t.host({ ev: 'sessions.synced', seq: 2, data: { revision: 1 } });
  const before = t.sent.filter((m) => m.op === 'sessions.subscribe').length;
  t.host({ ev: 'session.upsert', seq: 5, data: session('b') });
  t.host({ ev: 'session.upsert', seq: 7, data: session('c') });
  await flush();
  const subs = t.sent.filter((m) => m.op === 'sessions.subscribe');
  assert.equal(subs.length, before + 1, 'one resubscribe for gaps found while one is in flight');
  assert.equal(t.sent.filter((m) => m.op === 'sessions.list').length, 0);
  t.host({ re: subs.at(-1).id, ok: true, result: { revision: 4, count: 1 } });
  t.host({ ev: 'session.upsert', seq: 8, data: session('c') });
  t.host({ ev: 'sessions.synced', seq: 9, data: { revision: 4 } });
  assert.deepEqual(store.list().map((s) => s.id), ['c']);
});

test('a duplicate or backwards seq is a gap too', async () => {
  const t = fakeTransport();
  await subscribed(t, new SessionsStore());
  t.host({ ev: 'session.upsert', seq: 3, data: session('a') });
  t.host({ ev: 'session.upsert', seq: 3, data: session('a') });
  await flush();
  assert.equal(t.sent.filter((m) => m.op === 'sessions.subscribe').length, 2);
});

test('the first event after subscribing sets the baseline, whatever its seq', async () => {
  const t = fakeTransport();
  await subscribed(t, new SessionsStore());
  t.host({ ev: 'session.upsert', seq: 41, data: session('a') });
  t.host({ ev: 'session.upsert', seq: 42, data: session('b') });
  await flush();
  assert.equal(t.sent.filter((m) => m.op === 'sessions.subscribe').length, 1);
});

test('unknown events are ignored but still count for seq', async () => {
  const t = fakeTransport();
  const store = new SessionsStore();
  const client = await subscribed(t, store);
  const seen = [];
  client.on('event', (m) => seen.push(m.ev));
  t.host({ ev: 'session.upsert', seq: 1, data: session('a') });
  t.host({ ev: 'session.renamed', seq: 2, data: { id: 'a', title: 'x' } });
  t.host({ ev: 'session.upsert', seq: 3, data: session('b') });
  await flush();
  assert.deepEqual(seen, ['session.upsert', 'session.renamed', 'session.upsert']);
  assert.equal(store.get('a').title, null);
  assert.equal(t.sent.filter((m) => m.op === 'sessions.subscribe').length, 1, 'no resubscribe: seq was continuous');
});

test('malformed frames are ignored without breaking the client', async () => {
  const t = fakeTransport();
  const store = new SessionsStore();
  await subscribed(t, store);
  for (const junk of [null, 42, 'text', [], { ev: 7 }, { re: 'x' }, { ev: 'session.upsert', seq: 1, data: { title: 'no id' } }, { ev: 'session.upsert', seq: 2, data: null }]) {
    t.onmessage(junk);
  }
  t.host({ ev: 'session.upsert', seq: 3, data: session('ok') });
  assert.deepEqual(store.list().map((s) => s.id), ['ok']);
});

test('listAll follows next cursors to the last page', async () => {
  const t = fakeTransport();
  const client = new HostClient(t);
  const p = client.listAll();
  await flush();
  assert.deepEqual(t.sent[0].data, {});
  t.host({ re: t.sent[0].id, ok: true, result: { sessions: [session('a')], revision: 3, next: 'c1' } });
  await flush();
  assert.deepEqual(t.sent[1].data, { cursor: 'c1' });
  t.host({ re: t.sent[1].id, ok: true, result: { sessions: [session('b')], revision: 3 } });
  const r = await p;
  assert.deepEqual(r.sessions.map((s) => s.id), ['a', 'b']);
  assert.equal(r.revision, 3);
});

test('getSession returns null for a session that has ended', async () => {
  const t = fakeTransport();
  const client = new HostClient(t);
  const p = client.getSession('x');
  await flush();
  assert.deepEqual(t.sent[0], { id: 1, op: 'session.get', data: { id: 'x' } });
  t.host({ re: 1, ok: true, result: { missing: true } });
  assert.equal(await p, null);
});
