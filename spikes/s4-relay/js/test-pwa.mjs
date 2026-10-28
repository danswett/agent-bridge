// Runs the PWA in Chromium and WebKit (Safari's engine) against the dev stack: a fake
// GitHub, the relay and a demo host. The PWA signs in, proves its key, finds the host,
// opens a Noise channel through the relay and makes requests. The dev stack prints LEAK
// if any forwarded frame contains the echo test's plaintext.
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { chromium, webkit } from 'playwright';

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const dll = path.join(root, 'src', 'S4.DevStack', 'bin', 'Release', 'net10.0', 's4-devstack.dll');
const port = 8781;

const stack = spawn('dotnet', [dll, 'serve', '--port', String(port)], { stdio: ['ignore', 'pipe', 'inherit'] });
let out = '';
stack.stdout.on('data', (d) => { out += d.toString(); process.stdout.write(d); });
await new Promise((ok, fail) => {
  const t = setTimeout(() => fail(new Error('dev stack did not start')), 60000);
  const poll = setInterval(() => { if (out.includes('ready ')) { clearTimeout(t); clearInterval(poll); ok(); } }, 100);
});

let failed = 0;
for (const [name, type] of [['chromium', chromium], ['webkit', webkit]]) {
  const browser = await type.launch();
  try {
    const page = await browser.newPage();
    page.on('pageerror', (e) => console.log(`[${name}] page error: ${e.message}`));
    await page.goto(`http://127.0.0.1:${port}/`);
    await page.click('#signin');
    await page.waitForSelector('#account:not([hidden])', { timeout: 30000 });
    await page.waitForFunction(() => document.querySelector('#relay-state')?.textContent.includes('key proven'), null, { timeout: 15000 });
    await page.click('[data-device-name="demo-host"] button', { timeout: 15000 });
    await page.waitForFunction(() => document.querySelector('#peer-check')?.textContent.includes('matches'), null, { timeout: 30000 });
    const hs = await page.textContent('#hs-info');

    await page.click('#snapshot');
    await page.waitForFunction(() => document.querySelector('#result')?.textContent.includes('"machine": "demo-host"'), null, { timeout: 15000 });

    await page.click('#echo');
    await page.waitForFunction(() => document.querySelector('#result')?.textContent.startsWith('echoed intact'), null, { timeout: 15000 });

    await page.click('#ping');
    await page.waitForFunction(() => document.querySelector('#pings')?.textContent.includes('median'), null, { timeout: 60000 });
    const pings = await page.textContent('#pings');

    // The service worker registers, so the page can be installed and work offline.
    const sw = await page.evaluate(async () => !!(await navigator.serviceWorker?.getRegistration()));
    console.log(`PASS  ${name}: signed in, proved its key, channel to demo-host; ${hs}; ${pings}; service worker ${sw}`);
  } catch (e) {
    failed++;
    console.log(`FAIL  ${name}: ${e.message}`);
  } finally {
    await browser.close();
  }
}

stack.kill();
if (out.includes('LEAK')) { failed++; console.log('FAIL  the relay forwarded plaintext'); }
else { console.log('PASS  no forwarded frame contained plaintext'); }
process.exit(failed ? 1 : 0);
