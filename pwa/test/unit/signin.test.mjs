import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { accountStore, startDeviceFlow } from '../../js/signin.js';

const rows = JSON.parse(readFileSync(new URL('../../../apple/Tests/AgentBridgeAppleCoreTests/Resources/device-pages.json', import.meta.url)));
const device = { verificationUri: rows[0].uri, deviceCode: 'fixture', userCode: 'ABCD', interval: 1, expiresIn: 90 };

test('only the exact GitHub device page is offered by a relay response', async t => {
  for (const { uri, valid } of rows) {
    t.mock.method(globalThis, 'fetch', async () => Response.json({ ...device, verificationUri: uri }));
    if (valid) { assert.equal((await startDeviceFlow('')).verificationUri, uri); }
    else { await assert.rejects(startDeviceFlow(''), /untrusted/); }
    t.mock.restoreAll();
  }
});

test('unusable codes and polling limits fail before the first poll', async t => {
  for (const patch of [{ userCode: '' }, { deviceCode: ' ' }, { expiresIn: 0 }, { expiresIn: 3601 },
    { interval: -1 }, { interval: 1.5 }, { interval: 61 }, { interval: '1' }, { interval: null }]) {
    const fetch = t.mock.method(globalThis, 'fetch', async () => Response.json({ ...device, ...patch }));
    await assert.rejects(startDeviceFlow(''), /invalid/);
    assert.equal(fetch.mock.callCount(), 1);
    t.mock.restoreAll();
  }
});

test('malformed pending responses stop polling immediately', async t => {
  t.mock.method(globalThis, 'setTimeout', fn => { queueMicrotask(fn); });
  for (const pending of [{}, null, { pending: true, interval: null }, { pending: true, interval: 0 }, { pending: true, interval: 61 }]) {
    let requests = 0;
    const fetch = t.mock.method(globalThis, 'fetch', async () => {
      if (++requests > 2) { throw new Error('unexpected extra poll'); }
      return requests === 1 ? Response.json(device) : Response.json(pending, { status: 202 });
    });
    await assert.rejects((await startDeviceFlow('')).wait(), /invalid polling/);
    assert.equal(requests, 2);
    fetch.mock.restore();
  }
});

test('only a complete account returned with status 200 can enter the signed-in state', async t => {
  t.mock.method(globalThis, 'setTimeout', fn => { queueMicrotask(fn); });
  const account = { token: 'fixture', accountId: '42', login: 'fixture' };
  for (const [status, body] of [[200, ''], [200, '<html>'], [200, '{}'], [200, 'null'],
    [200, JSON.stringify({ ...account, login: ' ' })], [200, JSON.stringify({ ...account, accountId: 42 })],
    [200, JSON.stringify({ accountId: '42', login: 'fixture' })], [201, JSON.stringify(account)], [200, JSON.stringify(account)]]) {
    const fetch = t.mock.method(globalThis, 'fetch', async (_url, opts) => opts.body ? new Response(body, { status }) : Response.json(device));
    const result = (await startDeviceFlow('')).wait();
    if (status === 200 && body === JSON.stringify(account)) { assert.deepEqual(await result, account); }
    else { await assert.rejects(result, /sign-in/); }
    fetch.mock.restore();
  }
});

test('an invalid account cannot be persisted or loaded from storage', () => {
  const storage = { value: '{"token":"fixture"}', getItem() { return this.value; }, setItem(_, v) { this.value = v; } };
  assert.equal(accountStore.load(storage), null);
  assert.throws(() => accountStore.save({ token: 'fixture' }, storage), /incomplete/);
  assert.equal(storage.value, '{"token":"fixture"}');
  const account = { token: 'fixture', accountId: '42', login: 'fixture' };
  accountStore.save(account, storage);
  assert.deepEqual(accountStore.load(storage), account);
});

test('cancelling a wait interrupts a long delay without polling', async t => {
  const controller = new AbortController();
  const fetch = t.mock.method(globalThis, 'fetch', async (_url, options) => {
    assert.equal(options.signal, controller.signal);
    return Response.json({ ...device, interval: 60 });
  });
  const flow = await startDeviceFlow('', controller.signal);
  const wait = flow.wait();
  controller.abort();
  await assert.rejects(wait, { name: 'AbortError' });
  assert.equal(fetch.mock.callCount(), 1);
});

test('even a token fetch that ignores abort cannot return a late account', async t => {
  const controller = new AbortController();
  const started = Promise.withResolvers();
  const reply = Promise.withResolvers();
  t.mock.method(globalThis, 'setTimeout', fn => { queueMicrotask(fn); });
  t.mock.method(globalThis, 'fetch', async (_url, options) => {
    assert.equal(options.signal, controller.signal);
    if (!options.body) { return Response.json(device); }
    started.resolve();
    return reply.promise;
  });
  const wait = (await startDeviceFlow('', controller.signal)).wait();
  await started.promise;
  controller.abort();
  reply.resolve(Response.json({ token: 'fixture', accountId: '42', login: 'fixture' }));
  await assert.rejects(wait, { name: 'AbortError' });
});
