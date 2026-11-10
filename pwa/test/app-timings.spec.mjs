import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { buildSite } from '../tools/build-site.mjs';
import { startStaticServer } from './serve.mjs';

const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const RELAY = 'https://timings.relay.invalid';
let site, appUrl;

test.beforeAll(async ({}, info) => {
  const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
  const dir = buildSite({ out: path.join(root, '.site', `timings-${info.project.name}`), relay: RELAY });
  site = await startStaticServer({ dir });
  appUrl = `http://127.0.0.1:${site.address().port}/`;
});

test.afterAll(async () => { await new Promise((resolve) => site ? site.close(resolve) : resolve()); });

test('a settled reply is uploaded on the page timer, which stops with the signed-in page', async ({ page }) => {
  const posts = [];
  await page.route('**/v1/stats/app-timings', async (route) => {
    const headers = { 'access-control-allow-origin': new URL(appUrl).origin };
    if (route.request().method() === 'OPTIONS') {
      await route.fulfill({ status: 204, headers: { ...headers, 'access-control-allow-methods': 'POST', 'access-control-allow-headers': 'authorization,content-type' } });
      return;
    }
    posts.push({ body: route.request().postDataJSON(), token: route.request().headers().authorization });
    await route.fulfill({ status: 202, headers, json: { accepted: 1, dropped: 0 } });
  });
  await page.clock.install();
  await page.goto(`${appUrl}?demo=1&static=1&answerDelay=0`);
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(async () => {
    const { BridgeConnection } = await import('/js/connection.js');
    const connection = new BridgeConnection({ base: 'https://timings.relay.invalid', key: {}, account: { token: 'test-account-token' } });
    connection.relay = { open: true };
    window.agentBridgeDemo.connection.reportTimings = (drain) => connection.reportTimings(drain);
  });
  await open(page, IDLE);
  const reply = view(page, IDLE).locator('form.reply');
  await reply.getByRole('textbox', { name: 'Reply' }).fill('A private reply that must not be uploaded');
  await reply.getByRole('button', { name: 'Send' }).click();
  await expect(view(page, IDLE)).toHaveAttribute('data-status', 'working');
  await page.clock.pauseAt(await page.evaluate(() => Date.now()) + 1000);
  await page.clock.runFor(4200);
  await page.evaluate((id) => {
    const d = window.agentBridgeDemo, at = new Date(Date.now()).toISOString();
    d.upsert({ ...d.host.get(id), status: 'idle', statusSince: at, updatedAt: at });
  }, IDLE);
  await page.clock.runFor(50);
  await expect.poll(() => page.evaluate(() => window.agentBridgeDemo.page.turns.recent().length)).toBe(1);
  const [sample] = await page.evaluate(() => window.agentBridgeDemo.page.turns.recent());
  await page.clock.runFor(60000);
  await expect.poll(() => posts.length).toBe(1);
  expect(posts[0]).toEqual({
    body: { samples: [{ app: 'PWA', route: 'send-to-settled', ms: sample.ms, at: sample.at }] },
    token: 'Bearer test-account-token',
  });
  expect(await page.evaluate(() => window.agentBridgeDemo.page.turns.drain())).toEqual([]);
  await page.evaluate(() => window.agentBridgeDemo.page.clear());
  await page.clock.runFor(120000);
  expect(posts).toHaveLength(1);
});
