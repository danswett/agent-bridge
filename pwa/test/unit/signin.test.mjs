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
