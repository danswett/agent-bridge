// Screenshots of demo mode in WebKit at an iPhone's size, for the README and pull requests:
//   node test/screenshots.mjs
// docs/demo-light.png and docs/demo-dark.png: the sessions list, light and dark.
// docs/answer.png: answering the worked example's question, on its way to the machine.
// docs/approval.png: a permission request, answered with a tap.
// docs/all-machines.png: every machine at once, each card (folded here) naming its machine.
import { webkit, devices } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildSite } from '../tools/build-site.mjs';
import { startStaticServer, APP_PATH } from './serve.mjs';

const pwa = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const server = await startStaticServer({ dir: buildSite({ out: path.join(pwa, '.site', 'shots') }) });
const url = `http://127.0.0.1:${server.address().port}${APP_PATH}?demo=1&static=1`;
const browser = await webkit.launch();

async function open(scheme, query = '') {
  const context = await browser.newContext({ ...devices['iPhone 13'], colorScheme: scheme, reducedMotion: 'reduce', serviceWorkers: 'block' });
  const page = await context.newPage();
  await page.goto(url + query);
  await page.waitForSelector('html[data-demo-ready]');
  // Fixed relative times, so the pictures don't change with the clock.
  await page.evaluate(() => { for (const t of document.querySelectorAll('.since:not(:empty), .updated')) { t.textContent = t.classList.contains('since') ? 'for 4m' : 'updated 1m ago'; } });
  return { context, page };
}

const out = (name) => path.join(pwa, 'docs', name);
// For a picture of one card: the bottom navigation, fixed over the page, would otherwise sit across it.
const fixTimes = (page) => page.evaluate(() => { for (const t of document.querySelectorAll('.since:not(:empty), .updated')) { t.textContent = t.classList.contains('since') ? 'for 4m' : 'updated 1m ago'; } });
const hideBar = (page) => page.evaluate(() => { document.getElementById('nav').hidden = true; });

try {
  for (const scheme of ['light', 'dark']) {
    const { context, page } = await open(scheme);
    await page.screenshot({ path: out(`demo-${scheme}.png`), fullPage: true });
    await context.close();
    console.log(`docs/demo-${scheme}.png`);
  }

  {
    // An answer that never comes back, so the picture holds it on its way.
    const { context, page } = await open('light', '&answerDelay=600000');
    const card = page.locator('.session').first();
    const q = card.locator('.question');
    await q.getByRole('checkbox', { name: 'pwa' }).check();
    await q.getByRole('textbox', { name: 'Notes (optional)' }).fill('Start with the answer form; Web Push after.');
    await q.locator('input[type="number"]').fill('3');
    await q.getByRole('button', { name: 'Send answer' }).click();
    await q.locator('.answer-status').waitFor();
    await hideBar(page);
    await card.locator('.frame').screenshot({ path: out('answer.png') });
    await context.close();
    console.log('docs/answer.png');
  }

  {
    const { context, page } = await open('light');
    await page.evaluate(() => {
      const d = window.agentBridgeDemo;
      const s = d.host.get('019a1b2c-codex-example');
      const t = new Date().toISOString();
      d.upsert({
        ...s, status: 'waiting', statusSince: t, updatedAt: t,
        question: {
          id: 'approval-shot', kind: 'approval', askedAt: t, message: 'Run `git push --force-with-lease origin p1/answer-pwa`?',
          fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'yes', label: 'Yes' }, { value: 'always', label: 'Yes, and don\'t ask again for git push' }, { value: 'no', label: 'No, and tell the agent what to do instead' }] }],
        },
      });
    });
    const card = page.locator('.session[data-id="019a1b2c-codex-example"]');
    await card.locator('.question[data-question="approval-shot"]').waitFor();
    await fixTimes(page);
    await hideBar(page);
    await card.locator('.frame').screenshot({ path: out('approval.png') });
    await context.close();
    console.log('docs/approval.png');
  }

  {
    const { context, page } = await open('light', '&machines=3');
    // Folded, so the picture holds the machine tabs and every card's machine and status.
    for (const fold of await page.locator('.session .fold').all()) { await fold.click(); }
    await fixTimes(page);
    await hideBar(page);
    // In the page's own coordinates: folding scrolled it.
    const [top, bottom] = await page.evaluate(() => ['sessions', 'sessions'].map((id) => {
      const r = document.getElementById(id).getBoundingClientRect();
      return { y: r.top + scrollY, end: r.bottom + scrollY };
    }));
    await page.screenshot({ path: out('all-machines.png'), fullPage: true, clip: { x: 0, y: top.y - 8, width: page.viewportSize().width, height: bottom.end - top.y + 16 } });
    await context.close();
    console.log('docs/all-machines.png');
  }
} finally {
  await browser.close();
  server.close();
}
