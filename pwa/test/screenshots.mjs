// Screenshots of demo mode in WebKit at an iPhone's size, for the README and pull requests:
//   node test/screenshots.mjs
// docs/demo-light.png and docs/demo-dark.png: the sessions list, light and dark.
// docs/answer.png: answering the worked example's question, on its way to the machine.
// docs/approval.png: a permission request, answered with a tap.
// docs/all-machines.png: every machine at once, each card (folded here) naming its machine.
// docs/needs-you-light.png, needs-you-dark.png and needs-you-wide.png: Needs you (#165) on a phone and wide.
import { webkit, devices } from '@playwright/test';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildSite } from '../tools/build-site.mjs';
import { startStaticServer, APP_PATH } from './serve.mjs';

const pwa = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const server = await startStaticServer({ dir: buildSite({ out: path.join(pwa, '.site', 'shots') }) });
const url = `http://127.0.0.1:${server.address().port}${APP_PATH}?demo=1&static=1`;
const browser = await webkit.launch();

async function open(scheme, query = '', device = devices['iPhone 13']) {
  const context = await browser.newContext({ ...device, colorScheme: scheme, reducedMotion: 'reduce', serviceWorkers: 'block' });
  const page = await context.newPage();
  await page.goto(url + query);
  await page.waitForSelector('html[data-demo-ready]');
  // Fixed relative times, so the pictures don't change with the clock.
  await page.evaluate(() => { for (const t of document.querySelectorAll('.since:not(:empty), .updated')) { t.textContent = t.classList.contains('since') ? 'for 4m' : 'updated 1m ago'; } });
  return { context, page };
}

const out = (name) => path.join(pwa, 'docs', name);
// For a picture of one card: the sticky top bar would otherwise sit across it.
const fixTimes = (page) => page.evaluate(() => { for (const t of document.querySelectorAll('.since:not(:empty), .updated')) { t.textContent = t.classList.contains('since') ? 'for 4m' : 'updated 1m ago'; } });
const hideBar = (page) => page.evaluate(() => { document.querySelector('.topbar').hidden = true; });

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
    // Folded, so the picture holds the machines and every card's machine and status.
    for (const fold of await page.locator('.session .fold').all()) { await fold.click(); }
    await fixTimes(page);
    await hideBar(page);
    // In the page's own coordinates: folding scrolled it.
    const [top, bottom] = await page.evaluate(() => ['machines', 'sessions'].map((id) => {
      const r = document.getElementById(id).getBoundingClientRect();
      return { y: r.top + scrollY, end: r.bottom + scrollY };
    }));
    await page.screenshot({ path: out('all-machines.png'), fullPage: true, clip: { x: 0, y: top.y - 8, width: page.viewportSize().width, height: bottom.end - top.y + 16 } });
    await context.close();
    console.log('docs/all-machines.png');
  }

  // Needs you (#165), with every machine shown: a permission prompt answered in the lane, a
  // delegate's question, the forms that open their sessions and a read-only machine's question.
  const wide = { viewport: { width: 1440, height: 900 } };
  for (const [scheme, name, device] of [['light', 'needs-you-light.png', undefined], ['dark', 'needs-you-dark.png', undefined], ['light', 'needs-you-wide.png', wide]]) {
    const { context, page } = await open(scheme, '&machines=3', device);
    await page.evaluate(() => {
      const d = window.agentBridgeDemo;
      const t = new Date().toISOString();
      const land = d.host.get('8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35');
      d.upsert({
        ...land, status: 'waiting', statusSince: t, updatedAt: t,
        question: {
          id: 'r1#shot', kind: 'approval', askedAt: t, message: 'Allow **shell**?\n\n```\ngit push origin main --force-with-lease\n```',
          fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'approve', label: 'Approve once' }, { value: 'deny', label: 'Deny' }] }],
        },
      });
      const claude = d.host.get('6f1c2b7e-claude-example');
      d.upsert({
        ...claude, status: 'waiting', statusSince: t, updatedAt: t, waitingOn: 'agent-7',
        subagents: [{ id: 'agent-7', kind: 'background', status: 'waiting', name: 'Read witness shell logic' }],
        question: { id: 'q-delegate', kind: 'form', agent: 'agent-7', answerAtMachine: true, askedAt: t, message: 'Keep the old witness format?', fields: [] },
      });
    });
    await page.locator('#needs-you .needs-entry').nth(4).waitFor();
    await page.evaluate(() => { for (const t of document.querySelectorAll('.needs-age')) { t.textContent = '4m'; } });
    await fixTimes(page);
    await hideBar(page);
    const [lane, list] = await page.evaluate(() => ['needs-you', 'session-list'].map((id) => {
      const r = document.getElementById(id).getBoundingClientRect();
      return { x: r.left, y: r.top + scrollY, width: r.width };
    }));
    await page.screenshot({ path: out(name), fullPage: true, clip: { x: 0, y: lane.y - 8, width: page.viewportSize().width, height: list.y - lane.y + 160 } });
    await context.close();
    console.log(`docs/${name}`);
  }
} finally {
  await browser.close();
  server.close();
}
