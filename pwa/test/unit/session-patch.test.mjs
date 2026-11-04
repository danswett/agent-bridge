import { test } from 'node:test';
import assert from 'node:assert/strict';
import { HostClient } from '../../js/host-client.js';
import { SessionsStore } from '../../js/sessions-store.js';
import { PatchReasons, applyPatch, readPatch } from '../../js/session-patch.js';

// session.patch and resuming a subscription (docs/protocol/v1-sessions.md, Patches and Resuming
// without a snapshot): what the PWA does with them. The shared vectors replay the patches
// themselves (protocol-vectors.test.mjs).

function fakeTransport() {
  return {
    sent: [],
    onmessage: null,
    onclose: null,
    send(m) { this.sent.push(m); return Promise.resolve(); },
    close() {},
    host(m) { this.onmessage(JSON.parse(JSON.stringify(m))); },
    requests(op) { return this.sent.filter((m) => m.op === op); },
    lastRequest(op) { return this.requests(op).at(-1); },
  };
}

const flush = () => new Promise((r) => setImmediate(r));
const session = (id, extra = {}) => ({ id, agent: 'copilot', status: 'idle', statusSince: '2026-10-28T04:00:00.000Z', updatedAt: '2026-10-28T04:00:00.000Z', activity: {}, ...extra });

// A client subscribed for patches to the run "run-1", its snapshot under way.
async function subscribed(t, store, result = { revision: 1, count: 0 }) {
  const client = new HostClient(t, { store, timeoutMs: 500 });
  const p = client.subscribe({ patch: true, instance: 'run-1' });
  await flush();
  t.host({ re: t.lastRequest('sessions.subscribe').id, ok: true, result });
  await p;
  return client;
}

test('a patch on the session as the channel last gave it shows what the host has, and moves the revision resumed from', async () => {
  const t = fakeTransport();
  const store = new SessionsStore();
  await subscribed(t, store, { revision: 4, count: 1 });
  t.host({ ev: 'session.upsert', seq: 1, data: session('s1', { title: 'Patches', subagents: [{ id: 'a1', kind: 'task', status: 'working' }, { id: 'a2', kind: 'task', status: 'working' }], activity: { response: 'Reading' } }) });
  t.host({ ev: 'sessions.synced', seq: 2, data: { revision: 4 } });

  t.host({
    ev: 'session.patch', seq: 3,
    data: { id: 's1', base: 1, revision: 6, set: { status: 'working' }, subagents: { set: [{ id: 'a2', status: 'ended', outcome: 'done' }], remove: ['a1'] }, append: { activity: { response: ' the spec.' } } },
  });

  const s = store.get('s1');
  assert.equal(s.status, 'working');
  assert.equal(s.activity.response, 'Reading the spec.');
  assert.deepEqual(s.subagents.map((a) => [a.id, a.status, a.outcome]), [['a2', 'ended', 'done']]);
  assert.equal(s.title, 'Patches');
  assert.deepEqual(store.resumeFrom('run-1'), { instance: 'run-1', revision: 6 });
  // The next patch is on that one.
  t.host({ ev: 'session.patch', seq: 4, data: { id: 's1', base: 3, revision: 7, set: { title: 'Renamed' } } });
  assert.equal(store.get('s1').title, 'Renamed');
});

test('a patch on another event than the session came in is dropped whole, and the list is subscribed again in full', async () => {
  const t = fakeTransport();
  const store = new SessionsStore();
  const client = await subscribed(t, store);
  t.host({ ev: 'session.upsert', seq: 1, data: session('s1', { title: 'Before' }) });
  t.host({ ev: 'sessions.synced', seq: 2, data: { revision: 4 } });
  let gap = null;
  client.on('gap', (g) => { gap = g; });

  t.host({ ev: 'session.patch', seq: 3, data: { id: 's1', base: 2, revision: 5, set: { title: 'After' } } });
  await flush();

  assert.equal(store.get('s1').title, 'Before');
  assert.ok(gap, 'the list is not the host\'s until a new snapshot');
  assert.equal(client.synced, false);
  // Not resumed: the list the client holds is not the host's whole list any more.
  assert.equal(t.requests('sessions.subscribe').length, 2);
  assert.deepEqual(t.lastRequest('sessions.subscribe').data, { patch: true });
});

test('a reconnect to the same run asks to resume, and its snapshot keeps what was held, drops what ended and takes what changed', async () => {
  const store = new SessionsStore();
  const first = fakeTransport();
  await subscribed(first, store, { revision: 4, count: 3 });
  for (const [i, id] of ['kept', 'changed', 'ended'].entries()) { first.host({ ev: 'session.upsert', seq: i + 1, data: session(id, { title: id }) }); }
  first.host({ ev: 'sessions.synced', seq: 4, data: { revision: 4 } });
  first.host({ ev: 'session.patch', seq: 5, data: { id: 'kept', base: 1, revision: 9, set: { title: 'kept, renamed' } } });

  // The channel dropped; a new one to the same run of the host.
  const second = fakeTransport();
  const client = new HostClient(second, { store, timeoutMs: 500 });
  const p = client.subscribe({ patch: true, instance: 'run-1' });
  await flush();
  assert.deepEqual(second.lastRequest('sessions.subscribe').data, { patch: true, since: { instance: 'run-1', revision: 9 } });
  second.host({ re: second.lastRequest('sessions.subscribe').id, ok: true, result: { revision: 12, count: 1, resumed: true } });
  await p;
  second.host({ ev: 'session.upsert', seq: 1, data: session('changed', { title: 'changed while away' }) });
  // Not whole until synced: nothing is dropped yet.
  assert.equal(client.synced, false);
  assert.ok(store.has('ended'));
  second.host({ ev: 'sessions.synced', seq: 2, data: { revision: 12, live: ['changed', 'kept'] } });

  assert.equal(client.synced, true);
  assert.deepEqual(store.list().map((s) => [s.id, s.title]).sort(), [['changed', 'changed while away'], ['kept', 'kept, renamed']]);
  assert.deepEqual(store.resumeFrom('run-1'), { instance: 'run-1', revision: 12 });
  // A session the new channel has sent nothing of comes whole the first time it changes.
  second.host({ ev: 'session.patch', seq: 3, data: { id: 'kept', revision: 13, set: session('kept', { title: 'whole' }) } });
  assert.equal(store.get('kept').title, 'whole');
});

test('another run of the host, or a host without patches, is never asked to resume', async () => {
  const store = new SessionsStore();
  const t = fakeTransport();
  await subscribed(t, store, { revision: 4, count: 0 });
  t.host({ ev: 'sessions.synced', seq: 1, data: { revision: 4 } });

  const restartedT = fakeTransport();
  const restarted = new HostClient(restartedT, { store, timeoutMs: 500 });
  restarted.subscribe({ patch: true, instance: 'run-2' }).catch(() => {});
  const olderT = fakeTransport();
  const older = new HostClient(olderT, { store, timeoutMs: 500 });
  older.subscribe({ patch: false, instance: 'run-1' }).catch(() => {});
  await flush();

  assert.deepEqual(restartedT.lastRequest('sessions.subscribe').data, { patch: true });
  assert.deepEqual(olderT.lastRequest('sessions.subscribe').data, {});
  restarted.close();
  older.close();
});

test('a resumed snapshot naming a session the client never had is not whole: it subscribes again without resuming', async () => {
  const store = new SessionsStore();
  const t = fakeTransport();
  await subscribed(t, store, { revision: 4, count: 1 });
  t.host({ ev: 'session.upsert', seq: 1, data: session('kept') });
  t.host({ ev: 'sessions.synced', seq: 2, data: { revision: 4 } });

  const again = fakeTransport();
  const client = new HostClient(again, { store, timeoutMs: 500 });
  const p = client.subscribe({ patch: true, instance: 'run-1' });
  await flush();
  again.host({ re: again.lastRequest('sessions.subscribe').id, ok: true, result: { revision: 6, count: 0, resumed: true } });
  await p;
  again.host({ ev: 'sessions.synced', seq: 1, data: { revision: 6, live: ['kept', 'never-sent'] } });
  await flush();

  assert.equal(client.synced, false);
  assert.equal(store.whole, false);
  assert.equal(again.requests('sessions.subscribe').length, 2);
  assert.deepEqual(again.lastRequest('sessions.subscribe').data, { patch: true });
});

test('after a gap the revision held before it is resumed from, and nothing after the gap moves it', async () => {
  const store = new SessionsStore();
  const t = fakeTransport();
  const client = await subscribed(t, store, { revision: 4, count: 1 });
  t.host({ ev: 'session.upsert', seq: 1, data: session('s1') });
  t.host({ ev: 'sessions.synced', seq: 2, data: { revision: 4 } });
  t.host({ ev: 'session.patch', seq: 3, data: { id: 's1', base: 1, revision: 5, set: { title: 'one' } } });

  // seq 4 was lost: whatever came with it may be missing, so 5's revision says nothing.
  t.host({ ev: 'session.remove', seq: 5, data: { id: 'other', revision: 8 } });
  await flush();

  assert.equal(t.requests('sessions.subscribe').length, 2);
  assert.deepEqual(t.lastRequest('sessions.subscribe').data, { patch: true, since: { instance: 'run-1', revision: 5 } });
  assert.equal(client.synced, false);
});

test('a patch is applied to a copy, never to the session it is on, and a key "__proto__" is only a field', () => {
  const held = session('s1', { activity: { response: 'a' } });
  const patch = readPatch(JSON.parse('{"id":"s1","base":1,"set":{"__proto__":{"polluted":true},"activity":{"response":"b"}}}'));

  const applied = applyPatch(held, 1, patch);

  assert.equal(held.activity.response, 'a');
  assert.equal(applied.activity.response, 'b');
  assert.equal(Object.getPrototypeOf(applied), Object.prototype);
  assert.equal({}.polluted, undefined);
  assert.throws(() => applyPatch(held, 2, patch), { message: PatchReasons.notOnHeld });
});
