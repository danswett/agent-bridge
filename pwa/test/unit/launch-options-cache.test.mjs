import test from 'node:test';
import assert from 'node:assert/strict';
import { LaunchOptionsCache } from '../../js/launch-options-cache.js';

const settle = () => new Promise((r) => setTimeout(r, 0));

test('a machine asked once keeps its answer and the time it came', async () => {
  const cache = new LaunchOptionsCache({ now: () => 1234 });
  await cache.ask('a', async () => ({ agents: [] }));
  assert.deepEqual(cache.get('a'), { options: { agents: [] }, at: 1234, error: null, asking: false });
});

test('a failed ask keeps the last good answer and says why it failed', async () => {
  const cache = new LaunchOptionsCache();
  await cache.ask('a', async () => ({ n: 1 }));
  await assert.rejects(cache.ask('a', async () => { throw new Error('timed out'); }));
  const got = cache.get('a');
  assert.deepEqual(got.options, { n: 1 });
  assert.equal(got.error, 'timed out');
});

test('a machine that never answered has no options and carries the reason', async () => {
  const cache = new LaunchOptionsCache();
  await assert.rejects(cache.ask('a', async () => { throw new Error('not connected'); }));
  assert.equal(cache.get('a').options, null);
  assert.equal(cache.get('a').error, 'not connected');
});

test('two asks of one machine at once share a single request', async () => {
  const cache = new LaunchOptionsCache();
  let calls = 0;
  const load = async () => { calls += 1; return { ok: true }; };
  await Promise.all([cache.ask('a', load), cache.ask('a', load)]);
  assert.equal(calls, 1);
});

test('sync asks each open machine once, one at a time, and not again while it stays open', async () => {
  const cache = new LaunchOptionsCache();
  const log = [];
  const mk = (id) => ({ hostId: id, load: async () => { log.push(`start ${id}`); await settle(); log.push(`end ${id}`); return { id }; } });
  cache.sync([mk('a'), mk('b')]);
  cache.sync([mk('a'), mk('b')]);
  await settle(); await settle(); await settle(); await settle();
  assert.deepEqual(log, ['start a', 'end a', 'start b', 'end b']);
  cache.sync([mk('a'), mk('b')]);
  await settle();
  assert.equal(log.length, 4);
});

test('a machine marked stale is asked again by the next sync, and a watcher hears each answer', async () => {
  const cache = new LaunchOptionsCache();
  const heard = [];
  cache.watch((id) => heard.push(id));
  let n = 0;
  const m = { hostId: 'a', load: async () => ({ n: ++n }) };
  cache.sync([m]);
  await settle(); await settle();
  cache.stale('a');
  cache.sync([m]);
  await settle(); await settle();
  assert.equal(cache.get('a').options.n, 2);
  assert.deepEqual(heard, ['a', 'a']);
});

test('an ask made fresh after an event is not answered by one sent before it, whose late answer is dropped', async () => {
  const cache = new LaunchOptionsCache();
  let release;
  const old = cache.ask('a', () => new Promise((r) => { release = () => r({ n: 'old' }); }));
  const fresh = cache.ask('a', async () => ({ n: 'new' }), { fresh: true });
  assert.deepEqual(await fresh, { n: 'new' });
  release();
  await old;
  assert.deepEqual(cache.get('a').options, { n: 'new' });
});

test('a machine marked stale is not answered from an ask already on its way, and is asked again', async () => {
  const cache = new LaunchOptionsCache();
  let calls = 0;
  const load = () => { calls += 1; return new Promise((r) => setTimeout(() => r({ calls }), 5)); };
  cache.sync([{ hostId: 'a', load }]);
  await settle();
  cache.stale('a');
  cache.sync([{ hostId: 'a', load }]);
  await new Promise((r) => setTimeout(r, 40));
  assert.equal(calls, 2);
  assert.equal(cache.get('a').options.calls, 2);
});