import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { startDeviceFlow } from '../../js/signin.js';

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
