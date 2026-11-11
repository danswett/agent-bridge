import { test } from 'node:test';
import assert from 'node:assert/strict';
import { HostClient } from '../../js/host-client.js';
import { LoadFeed } from '../../js/load-feed.js';
import { loadBars } from '../../js/load-bars.js';

const sample = (extra = {}) => ({ state: 'fresh', ageMs: 2000, staleAfterMs: 15000,
  cpu: { percent: 37.5 }, memory: { totalBytes: 100, availableBytes: 25 }, ...extra });
async function fake(t, { capabilities = ['load'], hold = [], result = sample() } = {}) {
  const sent = [];
  let seq = 0;
  const transport = {
    async send(m) {
      sent.push(m);
      if (!hold.includes(m.op)) { answer(m, m.op === 'hello' ? { protocol: 1, capabilities } : result); }
    },
    close() {},
  };
  const answer = (m, r) => transport.onmessage({ re: m.id, ok: true, result: r });
  const client = new HostClient(transport);
  t.after(() => client.close());
  await client.hello();
  return { client, sent, answer,
    ops: () => sent.filter((m) => m.op !== 'hello').map((m) => m.op),
    update: (data) => transport.onmessage({ ev: 'load.update', seq: ++seq, data }) };
}
const flush = () => new Promise((resolve) => setImmediate(resolve));

test('get then subscribe feeds exact machine figures and ignores all other fields', async (t) => {
  const a = await fake(t), b = await fake(t, { result: sample({ cpu: undefined }) });
  const changes = [];
  const feed = new LoadFeed({ now: () => 0, onChange: (id) => changes.push(id) });
  await Promise.all([feed.attach('a', a.client), feed.attach('b', b.client)]);
  assert.deepEqual(a.ops(), ['load.get', 'load.subscribe']);
  a.update(sample({ sessions: [{ name: 'ignored' }], future: true, cpu: { percent: 12, cores: 8 } }));
  assert.deepEqual(feed.load('a'), sample({ cpu: { percent: 12 } }));
  assert.equal(loadBars(feed.load('a')).memory.percent, 75);
  assert.equal(loadBars(feed.load('b')).cpu.percent, null);
  assert.ok(changes.includes('a') && changes.includes('b'));
});

test('only the host state and age determine freshness, never elapsed client time', async (t) => {
  let now = 100;
  const a = await fake(t), feed = new LoadFeed({ now: () => now });
  await feed.attach('a', a.client);
  now += 1000000;
  assert.equal(loadBars(feed.load('a')).state, 'fresh');
  a.update(sample({ ageMs: 15001 }));
  assert.equal(feed.load('a').ageMs, 15001);
  assert.equal(loadBars(feed.load('a')).state, 'stale');
  assert.equal(loadBars(feed.load('a')).cpu.percent, null);
  now += 1000;
  a.update(sample({ state: 'stale', ageMs: 0 }));
  assert.equal(loadBars(feed.load('a')).state, 'stale');
});

test('no load capability makes no load request and exposes no record or demo figure', async (t) => {
  const a = await fake(t, { capabilities: [] }), feed = new LoadFeed();
  await feed.attach('a', a.client);
  a.update(sample());
  assert.deepEqual(a.ops(), []);
  assert.equal(feed.load('a'), null);
  assert.equal(loadBars(feed.load('a')).word, 'Unavailable');
});

test('duplicate attachment subscribes once and disconnect clears before reconnect subscribes again', async (t) => {
  const a = await fake(t), feed = new LoadFeed();
  await Promise.all([feed.attach('a', a.client), feed.attach('a', a.client)]);
  assert.deepEqual(a.ops(), ['load.get', 'load.subscribe']);
  a.client.close();
  assert.equal(feed.load('a'), null);
  const b = await fake(t);
  await feed.attach('a', b.client);
  assert.deepEqual(b.ops(), ['load.get', 'load.subscribe']);
  await feed.detach('a');
  assert.deepEqual(b.ops(), ['load.get', 'load.subscribe', 'load.unsubscribe']);
  b.update(sample());
  assert.equal(feed.load('a'), null);
});

test('late responses cannot restore a disconnected record or overwrite a newer update', async (t) => {
  const a = await fake(t, { hold: ['load.get'] }), feed = new LoadFeed();
  const pending = feed.attach('a', a.client);
  await flush();
  a.client.close();
  await pending;
  assert.equal(feed.load('a'), null);
  const b = await fake(t, { hold: ['load.subscribe'] });
  const attached = feed.attach('a', b.client);
  await flush();
  b.update(sample({ cpu: { percent: 90 } }));
  b.answer(b.sent.find((m) => m.op === 'load.subscribe'), sample());
  await attached;
  assert.equal(feed.load('a').cpu.percent, 90);
});

test('malformed and oversized updates are ignored and valid updates are limited to one per second', async (t) => {
  t.mock.method(console, 'error', () => {});
  let now = 0;
  const a = await fake(t), feed = new LoadFeed({ now: () => now });
  await feed.attach('a', a.client);
  for (const raw of [null, [], { state: 'fresh' }, sample({ future: 'x'.repeat(65536) })]) { a.update(raw); }
  assert.deepEqual(feed.load('a'), sample());
  assert.ok(console.error.mock.callCount() > 0);
  a.update(sample({ cpu: { percent: 0 }, memory: undefined }));
  assert.equal(loadBars(feed.load('a')).cpu.percent, 0);
  assert.equal(feed.load('a').memory, undefined);
  now = 999;
  a.update(sample({ cpu: { percent: 99 } }));
  assert.equal(feed.load('a').cpu.percent, 0);
  now = 1000;
  a.update(sample({ cpu: { percent: 20 } }));
  assert.equal(feed.load('a').cpu.percent, 20);
});
