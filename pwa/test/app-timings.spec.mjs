import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';

test('timings pause while hidden, survive an offline return, and stop with the signed-in page', async ({ page }) => {
  const posts = [];
  await page.route('**/v1/stats/app-timings', async (route) => {
    posts.push({ body: route.request().postDataJSON(), token: route.request().headers().authorization });
    await route.fulfill({ status: 202, json: { accepted: 1, dropped: 0 } });
  });
  await page.clock.install();
  await page.goto('?demo=1&static=1&answerDelay=0');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(async () => {
    const { BridgeConnection } = await import('/js/connection.js');
    const connection = new BridgeConnection({ base: location.origin, key: {}, account: { token: 'test-account-token' } });
    connection.relay = { open: true };
    window.__timingConnection = connection;
    window.__timingCalls = 0;
    Object.defineProperty(window.agentBridgeDemo.connection, 'ready', { get: () => connection.ready });
    window.agentBridgeDemo.connection.reportTimings = (drain) => { window.__timingCalls++; return connection.reportTimings(drain); };
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
  const visibility = (visible) => page.evaluate((visible) => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: visible ? 'visible' : 'hidden' });
    document.dispatchEvent(new Event('visibilitychange'));
  }, visible);
  await visibility(false);
  await page.evaluate(() => { window.__timingConnection.relay.open = false; });
  await page.clock.runFor(120000);
  expect(await page.evaluate(() => window.__timingCalls)).toBe(0);
  expect(await page.evaluate(() => window.agentBridgeDemo.page.turns.recent())).toEqual([sample]);
  await visibility(true);
  await page.clock.runFor(60000);
  expect(await page.evaluate(() => window.__timingCalls)).toBe(0);
  expect(await page.evaluate(() => window.agentBridgeDemo.page.turns.recent())).toEqual([sample]);
  await page.evaluate(() => { window.__timingConnection.relay.open = true; });
  await visibility(false);
  await visibility(true);
  await expect.poll(() => posts.length).toBe(1);
  expect(posts[0]).toEqual({
    body: { samples: [{ app: 'PWA', route: 'send-to-settled', ms: sample.ms, at: sample.at }] },
    token: 'Bearer test-account-token',
  });
  expect(await page.evaluate(() => window.agentBridgeDemo.page.turns.drain())).toEqual([]);
  await page.evaluate(() => {
    const turns = window.agentBridgeDemo.page.turns;
    turns.sent('test', 'next'); turns.seen('test', 'next', 'working'); turns.seen('test', 'next', 'idle');
  });
  await visibility(false);
  await visibility(true);
  expect(posts).toHaveLength(1);
  expect(await page.evaluate(() => window.agentBridgeDemo.page.turns.recent().length)).toBe(1);
  await page.clock.runFor(60000);
  await expect.poll(() => posts.length).toBe(2);
  await page.evaluate(() => window.agentBridgeDemo.page.clear());
  const calls = await page.evaluate(() => window.__timingCalls);
  await visibility(false);
  await visibility(true);
  await page.clock.runFor(120000);
  expect(posts).toHaveLength(2);
  expect(await page.evaluate(() => window.__timingCalls)).toBe(calls);
});
