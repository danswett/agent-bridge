// Screenshots of demo mode in WebKit at an iPhone's size, for the README and pull requests:
//   node test/screenshots.mjs
// docs/demo-light.png and docs/demo-dark.png: the sessions list, light and dark.
// docs/answer.png: answering the worked example's question, on its way to the machine, in its
// session pushed over the list.
// docs/approval.png: a permission request, answered with a tap.
// docs/all-machines.png: every machine at once, each row naming its machine.
// docs/needs-you-light.png, needs-you-dark.png and needs-you-wide.png: Needs you (#165) on a phone and wide.
// docs/delegates-light.png and delegates-dark.png: a session's main agent and its delegates (#176), wide.
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
  await fixTimes(page);
  return { context, page };
}

const out = (name) => path.join(pwa, 'docs', name);
// Fixed relative times, so the pictures don't change with the clock.
async function fixTimes(page) {
  await page.evaluate(() => {
    for (const t of document.querySelectorAll('.fact-since dd')) { t.textContent = 'for 4m'; }
    for (const t of document.querySelectorAll('.fact-updated dd')) { t.textContent = '1m ago'; }
    for (const t of document.querySelectorAll('.row-age, .speaker-age')) { t.textContent = '1m ago'; }
  });
}
// The bottom navigation is fixed: out of a full-page picture of the list.
const hideBar = (page) => page.evaluate(() => { document.getElementById('nav').hidden = true; });
// A session, opened from its row as a person would: pushed over the list on a phone.
async function show(page, id) {
  await page.locator(`.session[data-id="${id}"] .row-main`).click();
  await page.locator(`.session-view[data-id="${id}"]`).waitFor();
  await fixTimes(page);
  return page.locator(`.session-view[data-id="${id}"]`);
}

try {
  for (const scheme of ['light', 'dark']) {
    const { context, page } = await open(scheme);
    await hideBar(page);
    await page.screenshot({ path: out(`demo-${scheme}.png`), fullPage: true });
    await context.close();
    console.log(`docs/demo-${scheme}.png`);
  }

  {
    // An answer that never comes back, so the picture holds it on its way.
    const { context, page } = await open('light', '&answerDelay=600000');
    const card = await show(page, '02d9e5d5-6b35-49bd-b083-39572fbff648');
    const q = card.locator('.question');
    await q.getByRole('checkbox', { name: 'pwa' }).check();
    await q.getByRole('textbox', { name: 'Notes (optional)' }).fill('Start with the answer form; Web Push after.');
    await q.locator('input[type="number"]').fill('3');
    await q.getByRole('button', { name: 'Send answer' }).click();
    await q.locator('.answer-status').waitFor();
    await q.locator('.answer-status').scrollIntoViewIfNeeded();
    await page.screenshot({ path: out('answer.png') });
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
    const card = await show(page, '019a1b2c-codex-example');
    await card.locator('.question[data-question="approval-shot"]').waitFor();
    await card.locator('.question').scrollIntoViewIfNeeded();
    await page.screenshot({ path: out('approval.png') });
    await context.close();
    console.log('docs/approval.png');
  }

  {
    const { context, page } = await open('light', '&machines=3');
    // The machine tabs and every row's machine and status.
    await fixTimes(page);
    await page.locator('#list-pane').screenshot({ path: out('all-machines.png') });
    await context.close();
    console.log('docs/all-machines.png');
  }

  // Needs you (#165), with every machine shown: a permission prompt answered in the lane, a
  // delegate's question, the forms that open their sessions and a read-only machine's question.
  // Tall enough for the whole lane: at desktop widths the list pane scrolls on its own.
  const wide = { viewport: { width: 1440, height: 1400 } };
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
    await page.screenshot({ path: out(name), fullPage: true, clip: { x: Math.max(0, lane.x - 8), y: lane.y - 8, width: Math.min(page.viewportSize().width, lane.width + 16), height: list.y - lane.y + 160 } });
    await context.close();
    console.log(`docs/${name}`);
  }

  // The main agent and its delegates in one session (#176, ui.md §6.3): the main agent's own
  // words in the conversation, and beside them in the inspector each delegate under its name,
  // one of them opened to its own latest response.
  for (const scheme of ['light', 'dark']) {
    const { context, page } = await open(scheme, '&delegates=1', { viewport: { width: 1440, height: 1000 } });
    await page.locator('.session[data-id="demo-delegates"] .row-main').click();
    const card = page.locator('.session-view[data-id="demo-delegates"]');
    const suite = card.locator('.delegate[data-id="demo-agent-suite"]');
    await suite.locator('.delegate-summary').click();
    await suite.locator('.delegate-latest:not([hidden])').waitFor();
    await card.locator('.delegates-finished > summary').click();
    await fixTimes(page);
    await page.evaluate(() => { for (const t of document.querySelectorAll('.delegate-time')) { t.textContent = /finished/.test(t.textContent) ? 'finished 11m ago' : t.closest('[data-depth="1"]') ? '2m' : '16m'; } });
    await hideBar(page);
    await card.screenshot({ path: out(`delegates-${scheme}.png`) });
    await context.close();
    console.log(`docs/delegates-${scheme}.png`);
  }
} finally {
  await browser.close();
  server.close();
}
