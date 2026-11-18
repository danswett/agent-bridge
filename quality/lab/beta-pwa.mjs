// Fake-account acceptance only: observe the installed host, then open its simulated session.
import path from 'node:path';
import { chromium, expect } from '../../pwa/node_modules/@playwright/test/index.mjs';
import { P256KeyPair } from '../../pwa/js/noise-p256.js';
import { deviceIdOf } from '../../pwa/js/identity.js';
import { Relay } from '../../pwa/js/relay.js';
import { startDeviceFlow } from '../../pwa/js/signin.js';
import { buildSite } from '../../pwa/tools/build-site.mjs';
import { startStaticServer } from '../../pwa/test/serve.mjs';
import { open, view } from '../../pwa/test/nav.mjs';

const [operation, base, name, port, session, out] = process.argv.slice(2);
if (!/^http:\/\/127\.0\.0\.1:\d+$/.test(base) || !/^beta-accept-[0-9a-f]{32}$/.test(name)) {
  throw new Error('acceptance requires its loopback relay and isolated host name');
}
// The PowerShell parent bounds the entire process and owns its process tree.
if (operation === 'presence') {
  const key = await P256KeyPair.generate();
  const account = await (await startDeviceFlow(base)).wait();
  const relay = new Relay({ base, key, deviceId: await deviceIdOf(key.public), name: 'acceptance-observer' });
  let timer;
  try {
    const present = new Promise((resolve, reject) => {
      timer = setTimeout(() => reject(new Error('installed host absent from relay presence after 30 s')), 30000);
      relay.onPresence = (devices) => { if (devices.some((d) => d.kind === 'host' && d.name === name)) { resolve(); } };
    });
    await Promise.all([relay.connect(account.token), present]);
  } finally { clearTimeout(timer); relay.close(); }
} else if (operation === 'drive') {
  const dir = buildSite({ out: path.join(out, 'pwa-site'), relay: base });
  const site = await startStaticServer({ dir, port: Number(port) });
  let browser;
  try {
    browser = await chromium.launch();
    const context = await browser.newContext({ serviceWorkers: 'block' });
    const page = await context.newPage();
    page.setDefaultTimeout(30000);
    await page.goto(`http://127.0.0.1:${site.address().port}/`);
    await page.click('#signin-button');
    await expect(page.locator('#conn')).toHaveText('Connected', { timeout: 30000 });
    await expect(page.locator(`[data-device-name="${name}"]`)).not.toHaveCount(0, { timeout: 30000 });
    await open(page, session);
    await expect(view(page, session)).toContainText('Beta acceptance: proceed?', { timeout: 30000 });
    await page.screenshot({ path: path.join(out, 'pwa-session.png') });
  } finally {
    try { await browser?.close(); } finally { await new Promise((resolve) => site.close(resolve)); }
  }
} else { throw new Error('unknown acceptance client operation'); }
