import { test } from 'node:test';
import assert from 'node:assert/strict';
import { HostClient } from '../../js/host-client.js';
import { Limits, hostJsonBytes } from '../../js/answers.js';
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
  await assert.rejects(a, (e) => /one: nope/.test(e.message) && e.fromHost === true);
});

test('a timeout is not an answer from the host', async () => {
  const client = new HostClient(fakeTransport(), { timeoutMs: 10 });
  await assert.rejects(client.request('hello'), (e) => /timed out/.test(e.message) && e.fromHost === false);
});

test('a request on a dead transport fails at once instead of timing out', async () => {
  const t = fakeTransport();
  t.send = () => Promise.reject(new Error('not connected to the relay'));
  const client = new HostClient(t, { timeoutMs: 60000 });
  await assert.rejects(client.request('hello'), /not connected to the relay/);
});

test('a reply or answer is said to have gone once its transport has taken it, and never when the transport refuses it', async () => {
  const t = fakeTransport();
  let take;
  t.send = (m) => { t.sent.push(m); return new Promise((r) => { take = r; }); };
  const client = new HostClient(t, { timeoutMs: 60000 });
  const went = [];

  client.reply({ id: 's1', request: 'r1', text: 'go' }, { onSent: () => went.push('r1') }).catch(() => {});
  assert.deepEqual(went, [], 'not as the call returns');
  await flush();
  assert.deepEqual(went, [], 'nor while the transport still has it');
  take();
  await flush();
  assert.deepEqual(went, ['r1']);

  t.send = () => Promise.reject(new Error('not connected to the relay'));
  await assert.rejects(client.answer({ id: 's1', question: 'q1', request: 'r2', text: 'yes' }, { onSent: () => went.push('r2') }), /not connected/);
  assert.deepEqual(went, ['r1']);
  client.close();
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
  assert.deepEqual(store.list().map((s) => s.id), ['b', 'a'], 'a tick alone does not move a session');
  t.host({ ev: 'session.upsert', seq: 5, data: session('a', { status: 'waiting', updatedAt: '2026-10-28T04:00:03.000Z' }) });
  assert.deepEqual(store.list().map((s) => s.id), ['a', 'b'], 'a change of status moves a session to the top');
  t.host({ ev: 'session.remove', seq: 6, data: { id: 'b' } });
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
  t.host({ ev: 'session.upsert', seq: 1, data: session('a') });
  t.host({ ev: 'session.upsert', seq: 1, data: session('a') });
  await flush();
  assert.equal(t.sent.filter((m) => m.op === 'sessions.subscribe').length, 2);
});

test('a channel\'s first event above 1 is a gap: the events before it were lost', async () => {
  const t = fakeTransport();
  await subscribed(t, new SessionsStore());
  t.host({ ev: 'session.upsert', seq: 41, data: session('a') });
  await flush();
  assert.equal(t.sent.filter((m) => m.op === 'sessions.subscribe').length, 2);
});

test('events lost just after subscribing again are a gap too, as seq carries on across subscriptions', async () => {
  const t = fakeTransport();
  const client = await subscribed(t, new SessionsStore());
  const synced = [];
  client.on('synced', () => synced.push(true));
  t.host({ ev: 'sessions.synced', seq: 1, data: { revision: 1 } });
  t.host({ ev: 'session.upsert', seq: 4, data: session('a') });
  await flush();
  const subs = () => t.sent.filter((m) => m.op === 'sessions.subscribe');
  assert.equal(subs().length, 2);
  t.host({ re: subs().at(-1).id, ok: true, result: { revision: 2, count: 1 } });
  // 5, the new snapshot's first upsert, is lost.
  t.host({ ev: 'sessions.synced', seq: 6, data: { revision: 2 } });
  await flush();
  assert.deepEqual([subs().length, synced.length], [3, 1], 'asked again, and the snapshot not taken as whole');
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

test('a message that arrives after the client closed is ignored', async () => {
  const t = fakeTransport();
  const store = new SessionsStore();
  const client = await subscribed(t, store);
  client.close();
  store.clear();
  // A frame from the old machine that was still being decrypted when the user switched.
  t.host({ ev: 'session.upsert', seq: 1, data: session('from-the-old-machine') });
  assert.equal(store.size, 0);
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

test('a delegate is asked for by agent, and a host that answers with the whole session is not taken for the delegate', async () => {
  const t = fakeTransport();
  const client = new HostClient(t);
  const p = client.getSubagent('s1', 'agent-3a91');
  await flush();
  assert.deepEqual(t.sent[0], { id: 1, op: 'session.get', data: { id: 's1', agent: 'agent-3a91' } });
  t.host({ re: 1, ok: true, result: { id: 'agent-3a91', kind: 'background', name: 'Run full test suite', status: 'working', response: 'Bisect over the last 10 commits.', reasoning: 'Timing.' } });
  const a = await p;
  assert.deepEqual([a.name, a.response, a.reasoning], ['Run full test suite', 'Bisect over the last 10 commits.', 'Timing.']);

  // A host before sub-agents ignores agent and sends the session.
  const old = client.getSubagent('s1', 'agent-3a91');
  await flush();
  t.host({ re: 2, ok: true, result: session('s1', { activity: { response: 'The session\'s, not the delegate\'s.' } }) });
  await assert.rejects(old, /does not report delegates in detail/);

  const gone = client.getSubagent('s1', 'agent-gone');
  await flush();
  t.host({ re: 3, ok: true, result: { missing: true } });
  assert.equal(await gone, null);
});

test('usage.subscribe returns the report and each usage.update is heard as usage', async () => {
  const t = fakeTransport();
  const client = await subscribed(t, new SessionsStore());
  const heard = [];
  client.on('usage', (r) => heard.push(r));
  const p = client.subscribeUsage();
  await flush();
  t.host({ re: t.lastRequest('usage.subscribe').id, ok: true, result: { agents: [{ agent: 'codex', label: 'Codex', source: 'transcript', measuredAt: '2026-10-28T04:00:00.000Z', windows: [{ key: 'primary', label: '5h limit', percent: 3 }] }] } });
  assert.equal((await p).agents[0].windows[0].percent, 3);
  t.host({ ev: 'usage.update', seq: 1, data: { agents: [{ agent: 'codex', label: 'Codex', source: 'transcript', measuredAt: '2026-10-28T04:10:00.000Z', windows: [{ key: 'primary', label: '5h limit', percent: 4 }] }] } });
  assert.equal(heard.length, 1);
  assert.equal(heard[0].agents[0].windows[0].percent, 4);
});

test('a gap subscribes to usage again too, so a lost update is replaced by the current report', async () => {
  const t = fakeTransport();
  const client = await subscribed(t, new SessionsStore());
  const heard = [];
  client.on('usage', (r) => heard.push(r));
  const p = client.subscribeUsage();
  await flush();
  t.host({ re: t.lastRequest('usage.subscribe').id, ok: true, result: { agents: [] } });
  await p;
  t.host({ ev: 'usage.update', seq: 1, data: { agents: [] } });
  t.host({ ev: 'usage.update', seq: 3, data: { agents: [] } });
  await flush();
  assert.equal(t.sent.filter((m) => m.op === 'usage.subscribe').length, 2);
  t.host({ re: t.lastRequest('usage.subscribe').id, ok: true, result: { agents: [{ agent: 'claude', label: 'Claude Code', source: 'api', measuredAt: '2026-10-28T04:00:00.000Z', windows: [{ key: 'session', label: 'Session (5h)', percent: 9 }] }] } });
  await flush();
  assert.equal(heard.at(-1).agents[0].agent, 'claude');
});

test('usage asked for with the other machines\' readings asks for them again after a gap', async () => {
  const t = fakeTransport();
  const client = await subscribed(t, new SessionsStore());
  const p = client.subscribeUsage({ shared: true });
  await flush();
  assert.deepEqual(t.lastRequest('usage.subscribe').data, { shared: true });
  t.host({ re: t.lastRequest('usage.subscribe').id, ok: true, result: { agents: [] } });
  await p;
  t.host({ ev: 'usage.update', seq: 1, data: { agents: [] } });
  t.host({ ev: 'usage.update', seq: 3, data: { agents: [] } });
  await flush();
  assert.equal(t.sent.filter((m) => m.op === 'usage.subscribe').length, 2);
  assert.deepEqual(t.lastRequest('usage.subscribe').data, { shared: true });
  const g = client.usage();
  await flush();
  // Without it, the host's own report, asked for as before #172.
  assert.deepEqual(t.lastRequest('usage.get').data, {});
  t.host({ re: t.lastRequest('usage.get').id, ok: true, result: { agents: [] } });
  await g;
});
test('what holds the host\'s discovery arrives with the snapshot and with each change, and an unreadable one holds nothing', async () => {
  const t = fakeTransport();
  const store = new SessionsStore();
  const changes = [];
  let client = null;
  // What a view drawing on each change sees: the hold, with the snapshot counted as in.
  const seen = [];
  store.onChange((c) => { changes.push(c.type); seen.push([c.type, client?.synced, store.held?.reason ?? null]); });
  client = await subscribed(t, store);

  t.host({ ev: 'sessions.synced', seq: 1, data: { revision: 1, discoveryHeld: { reason: 'an unrecognised copilot process (pid 4120)', agents: ['copilot', 'gemini', 7] } } });
  assert.deepEqual(store.held, { reason: 'an unrecognised copilot process (pid 4120)', agents: ['copilot', 'unknown'] });
  assert.deepEqual(seen.filter(([type]) => type === 'held' || type === 'synced'), [
    ['held', true, 'an unrecognised copilot process (pid 4120)'],
    ['synced', true, 'an unrecognised copilot process (pid 4120)'],
  ]);

  t.host({ ev: 'discovery.changed', seq: 2, data: { discoveryHeld: { reason: 'an unrecognised copilot process (pid 4120)', agents: ['copilot', 'gemini'] } } });
  assert.equal(changes.filter((c) => c === 'held').length, 1, 'the same hold again is not news');

  t.host({ ev: 'discovery.changed', seq: 3, data: {} });
  assert.equal(store.held, null);
  t.host({ ev: 'discovery.changed', seq: 4, data: { discoveryHeld: { reason: 42 } } });
  assert.equal(store.held, null);
});

// ---- progress (sessions.progress, #600) ----

async function greeted(capabilities) {
  const t = fakeTransport();
  const client = new HostClient(t, { timeoutMs: 1000 });
  const p = client.hello();
  await flush();
  t.host({ re: t.lastRequest('hello').id, ok: true, result: { protocol: 1, machine: { id: 'm' }, capabilities } });
  await p;
  return { t, client };
}

test('a reply asks for progress only from a host that offers sessions.progress', async () => {
  const older = await greeted(['sessions.read', 'sessions.reply']);
  older.client.reply({ id: 's1', request: 'r1', text: 'go' }, { onProgress: () => {} }).catch(() => {});
  await flush();
  assert.deepEqual(older.t.lastRequest('session.reply').data, { id: 's1', request: 'r1', text: 'go' });
  const newer = await greeted(['sessions.read', 'sessions.reply', 'sessions.answer', 'sessions.progress']);
  newer.client.reply({ id: 's1', request: 'r1', text: 'go' }, { onProgress: () => {} }).catch(() => {});
  newer.client.answer({ id: 's1', question: 'q1', request: 'r2', text: 'yes' }, { onProgress: () => {} }).catch(() => {});
  await flush();
  assert.equal(newer.t.lastRequest('session.reply').data.progress, true);
  assert.equal(newer.t.lastRequest('session.answer').data.progress, true);
  older.client.close();
  newer.client.close();
});

test('an answer that fits the 16 KiB only without the progress flag goes without it, rather than be refused for asking', async () => {
  const { t, client } = await greeted(['sessions.read', 'sessions.answer', 'sessions.progress']);
  const answer = (text) => ({ id: 's1', question: 'q1', request: '00000000-0000-4000-8000-000000000000', text });
  // 8 bytes under the limit as it is: "progress":true would put it 8 over, and the host refuse it.
  const text = 'x'.repeat(Limits.request - 8 - hostJsonBytes(answer('')));
  assert.equal(hostJsonBytes(answer(text)), Limits.request - 8);
  client.answer(answer(text), { onProgress: () => {} }).catch(() => {});
  await flush();
  assert.equal('progress' in t.lastRequest('session.answer').data, false);
  client.answer(answer('short'), { onProgress: () => {} }).catch(() => {});
  await flush();
  assert.equal(t.lastRequest('session.answer').data.progress, true);
  client.close();
});

test('progress frames tell a reply how far it has got, in order, and never settle it', async () => {
  const { t, client } = await greeted(['sessions.read', 'sessions.reply', 'sessions.progress']);
  const heard = [];
  let settled = false;
  const p = client.reply({ id: 's1', request: 'r1', text: 'go' }, { onProgress: (s) => heard.push(s) }).finally(() => { settled = true; });
  await flush();
  const re = t.lastRequest('session.reply').id;
  t.host({ re, progress: { stage: 'received' } });
  t.host({ re, progress: { stage: 'typed', working: true } });
  // A stage this app doesn't know, from a newer host, settles nothing either: the service passes it over.
  t.host({ re, progress: { stage: 'pondering' } });
  await flush();
  assert.equal(settled, false);
  assert.deepEqual(heard, [{ stage: 'received', working: false }, { stage: 'typed', working: true }, { stage: 'pondering', working: false }]);
  t.host({ re, ok: true, result: { outcome: 'delivered', timings: { typed: 352, recorded: 1190, confirmed: 1361, bogus: 'x' } } });
  assert.deepEqual(await p, { outcome: 'delivered', reason: null, stage: null, timings: { typed: 352, recorded: 1190, confirmed: 1361 } });
});

test('a progress frame for a request the client is not waiting on is ignored', async () => {
  const { t, client } = await greeted(['sessions.read', 'sessions.reply', 'sessions.progress']);
  const heard = [];
  const p = client.reply({ id: 's1', request: 'r1', text: 'go' }, { onProgress: (s) => heard.push(s) });
  await flush();
  const re = t.lastRequest('session.reply').id;
  t.host({ re: re + 7, progress: { stage: 'typed', working: false } });
  t.host({ re, ok: true, result: { outcome: 'delivered' } });
  assert.deepEqual(await p, { outcome: 'delivered', reason: null, stage: null });
  // Nor one for the request after its response: it is settled and gone.
  t.host({ re, progress: { stage: 'received' } });
  assert.deepEqual(heard, []);
});

// ---- sessions.focus (#1190) ----

const waitMs = (ms) => new Promise((r) => setTimeout(r, ms));
const focusOffered = ['sessions.read', 'sessions.focus'];
const focusSent = (t) => t.sent.filter((m) => m.op === 'sessions.focus').map((m) => m.data);

async function focusing(capabilities = focusOffered, focusGapMs = 0) {
  const t = fakeTransport();
  const client = new HostClient(t, { timeoutMs: 1000, focusGapMs });
  const p = client.hello();
  await flush();
  t.host({ re: t.lastRequest('hello').id, ok: true, result: { protocol: 1, machine: { id: 'm' }, capabilities } });
  await p;
  return { t, client };
}

async function subscribeAgain(t, client) {
  const p = client.subscribe();
  await flush();
  t.host({ re: t.lastRequest('sessions.subscribe').id, ok: true, result: { revision: 1, count: 0 } });
  await p;
  await flush();
}

test('the page says it is visible, and which session it shows, each time that changes', async () => {
  const { t, client } = await focusing();
  client.setFocus({ visible: true });
  client.setFocus({ visible: true });
  await flush();
  client.setFocus({ visible: true, open: 's1' });
  await flush();
  client.setFocus({ visible: false, open: 's1' });
  await flush();
  client.setFocus({ visible: true, open: null });
  await flush();
  assert.deepEqual(focusSent(t), [{ visible: true }, { visible: true, open: 's1' }, { visible: false, open: 's1' }, { visible: true }]);
  client.close();
});

test('the focus is said again after every sessions.subscribe, for the channel it is on', async () => {
  const { t, client } = await focusing();
  client.setFocus({ visible: true, open: 's1' });
  await flush();
  assert.equal(focusSent(t).length, 1);
  await subscribeAgain(t, client);
  await subscribeAgain(t, client);
  assert.deepEqual(focusSent(t), Array(3).fill({ visible: true, open: 's1' }));
  client.close();
});

test('a focus said before the host has answered hello goes once subscribing begins, and not before', async () => {
  const t = fakeTransport();
  const client = new HostClient(t, { timeoutMs: 1000, focusGapMs: 0 });
  client.setFocus({ visible: true, open: 's1' });
  await flush();
  assert.deepEqual(focusSent(t), []);
  const p = client.hello();
  await flush();
  t.host({ re: t.lastRequest('hello').id, ok: true, result: { protocol: 1, machine: { id: 'm' }, capabilities: focusOffered } });
  await p;
  await subscribeAgain(t, client);
  assert.deepEqual(focusSent(t), [{ visible: true, open: 's1' }]);
  client.close();
});

test('nothing is sent to a host that does not offer sessions.focus', async () => {
  const { t, client } = await focusing(['sessions.read', 'sessions.patch']);
  client.setFocus({ visible: true, open: 's1' });
  await flush();
  await subscribeAgain(t, client);
  client.setFocus({ visible: false });
  await flush();
  assert.deepEqual(focusSent(t), []);
  client.close();
});

test('rapid changes keep inside the host\'s limit: one goes at once, then only the latest state after the gap', async () => {
  const { t, client } = await focusing(focusOffered, 60);
  for (let i = 0; i < 40; i++) { client.setFocus({ visible: i % 2 === 0, open: `s${i}` }); }
  await flush();
  assert.deepEqual(focusSent(t), [{ visible: true, open: 's0' }]);
  await waitMs(150);
  assert.deepEqual(focusSent(t), [{ visible: true, open: 's0' }, { visible: false, open: 's39' }]);
  // Said and then taken back inside the gap: what the host was last told is still right.
  client.setFocus({ visible: true, open: 's5' });
  client.setFocus({ visible: false, open: 's6' });
  client.setFocus({ visible: true, open: 's5' });
  await waitMs(150);
  assert.deepEqual(focusSent(t).slice(2), [{ visible: true, open: 's5' }]);
  client.close();
});

test('a focus the host refuses, or that never gets there, does not break subscribing or reach the page', async () => {
  const { t, client } = await focusing();
  client.setFocus({ visible: true });
  await flush();
  t.host({ re: t.lastRequest('sessions.focus').id, ok: false, error: 'too many requests' });
  await flush();
  t.send = () => Promise.reject(new Error('not connected to the relay'));
  client.setFocus({ visible: false });
  await flush();
  t.send = function send(m) { this.sent.push(m); return Promise.resolve(); };
  await subscribeAgain(t, client);
  assert.equal(client.closed, false);
  client.close();
  client.setFocus({ visible: true });
  await flush();
});

test('the focus carries the age of the last input as it is sent, an integer, and leaves it out when there is none to report', async () => {
  const { t, client } = await focusing();
  let idle = null;
  client.setFocus({ visible: true, idle: () => idle });
  await flush();
  idle = 1500.9;
  client.setFocus({ visible: true, open: 's1', idle: () => idle });
  await flush();
  idle = 0;
  client.setFocus({ visible: false, open: 's1', idle: () => idle });
  await flush();
  for (const bad of [-1, NaN, 'soon', undefined]) {
    idle = bad;
    client.refreshFocus();
    await waitMs(5);
  }
  assert.deepEqual(focusSent(t).slice(0, 3), [{ visible: true }, { visible: true, open: 's1', idleMs: 1500 }, { visible: false, open: 's1', idleMs: 0 }]);
  assert.equal(focusSent(t).length, 3, 'refreshes are held to the input gap');
  client.close();
});

test('a burst of input keeps inside the host\'s limit: refreshFocus says the focus again at most once in the input gap, with the age at that moment', async () => {
  const t = fakeTransport();
  const client = new HostClient(t, { timeoutMs: 1000, focusGapMs: 0, inputGapMs: 60 });
  const p = client.hello();
  await flush();
  t.host({ re: t.lastRequest('hello').id, ok: true, result: { protocol: 1, machine: { id: 'm' }, capabilities: focusOffered } });
  await p;
  let idle = 10;
  client.setFocus({ visible: true, idle: () => idle });
  await flush();
  for (let i = 0; i < 100; i++) { idle = 20 + i; client.refreshFocus(); }
  await flush();
  assert.deepEqual(focusSent(t), [{ visible: true, idleMs: 10 }]);
  await waitMs(120);
  assert.deepEqual(focusSent(t), [{ visible: true, idleMs: 10 }, { visible: true, idleMs: 119 }], 'once, as it stood when sent');
  client.close();
});

test('the question on screen goes with the open session as openQuestion, again when it changes, and never without a session', async () => {
  const { t, client } = await focusing();
  client.setFocus({ visible: true, open: 's1', openQuestion: 'q1' });
  await flush();
  client.setFocus({ visible: true, open: 's1', openQuestion: 'q1' });
  client.setFocus({ visible: true, open: 's1', openQuestion: 'q2' });
  await flush();
  client.setFocus({ visible: true, open: 's1' });
  await flush();
  client.setFocus({ visible: true, openQuestion: 'q2' });
  await flush();
  assert.deepEqual(focusSent(t), [
    { visible: true, open: 's1', openQuestion: 'q1' },
    { visible: true, open: 's1', openQuestion: 'q2' },
    { visible: true, open: 's1' },
    { visible: true },
  ]);
  // Said again after a subscribe as it stands.
  client.setFocus({ visible: true, open: 's1', openQuestion: 'q9' });
  await flush();
  await subscribeAgain(t, client);
  assert.deepEqual(focusSent(t).slice(-2), Array(2).fill({ visible: true, open: 's1', openQuestion: 'q9' }));
  client.close();
});

test('refreshing the focus sends nothing to a host that does not offer it, or before anything is said', async () => {
  const older = await focusing(['sessions.read']);
  older.client.setFocus({ visible: true });
  older.client.refreshFocus();
  const fresh = await focusing(focusOffered, 0);
  fresh.client.refreshFocus();
  await flush();
  assert.deepEqual(focusSent(older.t), []);
  assert.deepEqual(focusSent(fresh.t), []);
  older.client.close();
  fresh.client.close();
});
