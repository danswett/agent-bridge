import { test } from 'node:test';
import assert from 'node:assert/strict';
import { SentImages } from '../../js/sent-images.js';

function setup() {
  const saved = new Map();
  let now = 0, account = 'one', refuse = false;
  const store = new SentImages({
    account: () => account, now: () => now,
    get: async (key) => structuredClone(saved.get(key)),
    put: async (key, value) => { if (refuse) { throw new DOMException('private detail', 'QuotaExceededError'); } saved.set(key, structuredClone(value)); },
    remove: async (key) => { saved.delete(key); },
  });
  const keep = (ref, size = 1, type = 'image/png', seed = 1) => store.remember([{ id: ref, ref }], [{ id: ref, name: 'same.png', type, bytes: new Uint8Array(size).fill(seed) }], account);
  return { store, keep, saved: () => saved.get('sent-images'), clock: (v) => { now = v; }, account: (v) => { account = v; }, refuse: () => { refuse = true; } };
}

test('same-name sends bind distinct bytes by exact ref, never filename, on this account only', async () => {
  const s = setup();
  await s.keep('@/private/first', 2, 'image/png', 1);
  await s.keep('@/private/second', 2, 'image/png', 2);
  assert.equal((await s.store.get('@/private/first')).bytes[0], 1);
  assert.equal((await s.store.get('@/private/second')).bytes[0], 2);
  assert.equal(await s.store.get('same.png'), null);
  assert.ok(!JSON.stringify(s.saved()).includes('/private/'));
  s.account('two');
  assert.equal(await s.store.get('@/private/first'), null);
  assert.equal(await setup().store.get('@/private/first'), null);
});

for (const [label, count, size] of [['image count', 33, 1], ['total bytes', 3, 3 * 1024 * 1024]]) {
  test(`the ${label} cap evicts the oldest first`, async () => {
    const s = setup();
    for (let i = 0; i < count; i++) { s.clock(i); await s.keep(String(i), size); }
    assert.equal(await s.store.get('0'), null);
    assert.ok(await s.store.get(String(count - 1)));
    assert.ok(s.saved().entries.length <= 32);
    assert.ok(s.saved().entries.reduce((n, e) => n + e.bytes.length, 0) <= 8 * 1024 * 1024);
  });
}

test('expiry is pruned on read and write, and oversize or SVG files are not retained', async () => {
  const s = setup();
  await s.keep('old'); s.clock(24 * 60 * 60 * 1000);
  assert.equal(await s.store.get('old'), null);
  assert.equal(s.saved().entries.length, 0);
  await s.keep('old'); s.clock(48 * 60 * 60 * 1000); await s.keep('new');
  assert.equal(s.saved().entries.length, 1);
  await s.keep('large', 8 * 1024 * 1024 + 1); await s.keep('svg', 1, 'image/svg+xml');
  assert.equal(await s.store.get('large'), null); assert.equal(await s.store.get('svg'), null);
});

test('quota refusal leaves a chip, clear deletes bytes, and late outcomes cannot restore them', async () => {
  const s = setup();
  await s.keep('old'); await s.store.clear();
  assert.equal(await s.store.get('old'), null);
  s.refuse(); await s.keep('new');
  assert.equal(await s.store.get('new'), null);
  s.account('two');
  await s.store.remember([{ id: 'old', ref: 'old' }], [{ id: 'old', type: 'image/png', bytes: new Uint8Array(1) }], 'one');
  assert.equal(await s.store.get('old'), null);
});

test('a clear in another tab prevents an earlier send from restoring bytes', async () => {
  const saved = new Map();
  let queue = Promise.resolve();
  const options = { account: () => 'one', get: async (key) => saved.get(key), put: async (key, value) => saved.set(key, value), remove: async (key) => saved.delete(key), serial: (task) => { queue = queue.then(task); return queue; } };
  const a = new SentImages(options), b = new SentImages(options);
  const stamp = await b.stamp?.();
  await a.clear();
  await b.remember([{ id: 'old', ref: 'old' }], [{ id: 'old', type: 'image/png', bytes: new Uint8Array(1) }], 'one', stamp);
  assert.equal(await b.get('old'), null);
});

test('clearing still deletes bytes when saving the clear generation hits quota', async () => {
  const s = setup();
  await s.keep('old'); s.refuse(); await s.store.clear();
  assert.equal(await s.store.get('old'), null);
});
