// Screenshots of demo mode in WebKit at an iPhone's size, light and dark, for the README
// and pull requests:  node test/screenshots.mjs  (writes docs/demo-*.png)
import { webkit, devices } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildSite } from '../tools/build-site.mjs';
import { startStaticServer, APP_PATH } from './serve.mjs';

const pwa = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const server = await startStaticServer({ dir: buildSite({ out: path.join(pwa, '.site', 'shots') }) });
const url = `http://127.0.0.1:${server.address().port}${APP_PATH}?demo=1&static=1`;
const browser = await webkit.launch();
try {
  for (const scheme of ['light', 'dark']) {
    const context = await browser.newContext({ ...devices['iPhone 13'], colorScheme: scheme, reducedMotion: 'reduce', serviceWorkers: 'block' });
    const page = await context.newPage();
    await page.goto(url);
    await page.waitForSelector('html[data-demo-ready]');
    // Fixed relative times, so the pictures don't change with the clock.
    await page.evaluate(() => { for (const t of document.querySelectorAll('.since:not(:empty), .updated')) { t.textContent = t.classList.contains('since') ? 'for 4m' : 'updated 1m ago'; } });
    await page.screenshot({ path: path.join(pwa, 'docs', `demo-${scheme}.png`), fullPage: true });
    await context.close();
    console.log(`docs/demo-${scheme}.png`);
  }
} finally {
  await browser.close();
  server.close();
}
