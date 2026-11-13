import { beforeEach, test } from 'node:test';
import assert from 'node:assert/strict';
import { ReplyService } from '../../js/reply-service.js';
import { accountStore } from '../../js/signin.js';

const slot = { hostId: 'h1', sessionId: 's1' };
const key = 'agent-bridge.app.replies:"a":["h1","s1",""]';
const flush = () => new Promise((resolve) => setImmediate(resolve));
beforeEach((t) => t.mock.timers.enable({ apis: ['setTimeout'] }));
function fixture() {
  const values = new Map(), copies = new Map(), calls = [];
  const storage = {
    get length() { return values.size; },
    key: (i) => [...values.keys()][i],
    getItem: (k) => values.get(k) ?? null,
    setItem: (k, v) => values.set(k, v),
    removeItem: (k) => values.delete(k),
  };
  let tail = Promise.resolve(), serial = 0, time = 1000, online = true, lose = true;
  const locks = { request: (_key, fn) => { const result = tail.then(fn); tail = result.catch(() => {}); return result; } };
  accountStore.save({ accountId: 'a', token: 'fixture-only' }, storage);
  const client = {
    closed: false,
    async reply(data) {
      assert.equal(JSON.parse(storage.getItem(key)).request, data.request, 'persist before sending');
      calls.push(data);
      if (!copies.has(data.request)) { copies.set(data.request, data); }
      if (lose) { client.closed = true; throw new Error('lost final response'); }
      return { outcome: 'delivered' };
    },
  };
  const make = () => new ReplyService({
    current: () => online ? { hostId: 'h1', client } : null,
    uuid: () => `request-${++serial}`, now: () => time,
    persistence: { storage, account: 'a', locks },
  });
  return {
    storage, copies, calls, make,
    pending: () => JSON.parse(storage.getItem(key)),
    reconnect() { client.closed = false; lose = false; },
    offline() { online = false; },
    elapse() { time += 8 * 60 * 1000 + 1; },
  };
}

test('a reload after losing the final response reconciles the original payload and one host copy', async () => {
  const f = fixture(), first = f.make();
  await first.submit({ ...slot, text: 'once' });
  await flush();
  const saved = f.pending();
  assert.deepEqual(saved, { ...slot, questionId: '', request: 'request-1', text: 'once', attachments: [], firstSentAt: 1000 });
  first.clear();
  f.reconnect();
  const reloaded = f.make();
  reloaded.ready();
  await flush();
  assert.equal(f.calls.length, 2);
  assert.deepEqual(f.calls[1], f.calls[0]);
  assert.equal(f.copies.size, 1);
  assert.equal(reloaded.get('h1', 's1').outcome, 'delivered');
  assert.equal(f.pending(), null);
});

test('an offline reload keeps the identity and does not replace its payload with edited text', async () => {
  const f = fixture(), first = f.make();
  await first.submit({ ...slot, text: 'original' });
  await flush();
  first.clear();
  const saved = f.pending();
  f.offline();
  const reloaded = f.make();
  await reloaded.submit({ ...slot, text: 'edited draft' });
  reloaded.ready();
  assert.deepEqual(f.pending(), saved);
  assert.equal(reloaded.get('h1', 's1').state, 'waiting');
  assert.equal(f.calls.length, 1);
  reloaded.clear();
});

test('expired identity becomes an unconfirmed draft, and an explicit edited send gets a new UUID', async () => {
  const f = fixture(), first = f.make();
  await first.submit({ ...slot, text: 'original' });
  await flush();
  first.clear();
  f.elapse();
  f.reconnect();
  const reloaded = f.make();
  reloaded.ready();
  assert.equal(f.calls.length, 1);
  assert.equal(reloaded.get('h1', 's1').outcome, 'unconfirmed');
  assert.equal(reloaded.get('h1', 's1').answer.text, 'original');
  assert.match(reloaded.get('h1', 's1').reason, /may have arrived/i);
  assert.equal(f.pending(), null);
  await reloaded.submit({ ...slot, text: 'edited' });
  await flush();
  assert.equal(f.calls.at(-1).text, 'edited');
  assert.notEqual(f.calls.at(-1).request, 'request-1');
});

test('an unavailable attachment after reload stops without resending a changed payload', () => {
  const f = fixture();
  f.storage.setItem(key, JSON.stringify({ ...slot, questionId: '', request: 'old', text: 'look', attachments: ['missing'], firstSentAt: 1000 }));
  const reloaded = f.make();
  reloaded.ready();
  assert.equal(f.calls.length, 0);
  assert.equal(reloaded.get('h1', 's1').answer.text, 'look');
  assert.equal(reloaded.get('h1', 's1').outcome, 'unconfirmed');
  assert.match(reloaded.get('h1', 's1').reason, /attachment.*missing/i);
  assert.equal(f.pending(), null);
});

test('two tabs claiming one uncertain slot share one UUID and unchanged payload', async () => {
  const f = fixture(), one = f.make(), two = f.make();
  await Promise.all([one.submit({ ...slot, text: 'first' }), two.submit({ ...slot, text: 'different' })]);
  await flush();
  assert.equal(f.copies.size, 1);
  assert.ok(f.calls.every((call) => call.request === 'request-1' && call.text === 'first'));
  assert.equal(f.pending().text, 'first');
  one.clear();
  two.clear();
});

for (const exit of ['sign-out', 'account switch']) {
  test(`${exit} clears pending identities and prevents a waiting old-account resend`, async () => {
    const f = fixture(), service = f.make();
    await service.submit({ ...slot, text: 'private' });
    await flush();
    if (exit === 'sign-out') { accountStore.clear(f.storage); }
    else { accountStore.save({ accountId: 'b', token: 'fixture-only' }, f.storage); }
    f.reconnect();
    service.ready();
    await flush();
    assert.equal(f.pending(), null);
    assert.equal(f.calls.length, 1);
    service.clear();
  });
}

for (const failure of ['quota', 'storage', 'locks']) {
  test(`${failure} failure blocks the first send with an explicit error`, async () => {
    const f = fixture(), service = failure === 'locks'
      ? new ReplyService({ current: () => ({ hostId: 'h1', client: {} }), persistence: { storage: f.storage, account: 'a', locks: null } })
      : f.make();
    if (failure === 'quota') { f.storage.setItem = () => { throw new DOMException('full', 'QuotaExceededError'); }; }
    if (failure === 'storage') { f.storage.getItem = () => { throw new Error('blocked'); }; }
    await assert.rejects(async () => service.submit({ ...slot, text: 'must not send' }), /storage|quota|locks/i);
    assert.equal(f.calls.length, 0);
    service.clear();
  });
}
