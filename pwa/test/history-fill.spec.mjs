import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const ID = 'history-fill';
const COUNT = 160;
const LATENCY_MS = 400;

async function start(page, { short = false, height = 800 } = {}) {
  await page.setViewportSize({ width: 1440, height });
  await page.goto('?demo=1&static=1&signin=1&name=DEV-VM1#/sessions');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(({ ID, count, latency, short }) => {
    const demo = window.agentBridgeDemo;
    const turns = Array.from({ length: count }, (_, i) => ({
      id: `${ID}-${i + 1}`, at: new Date(Date.UTC(2026, 0, 1, 0, i)).toISOString(),
      prompt: { text: `Prompt ${i + 1}`, from: 'person' },
      parts: [{ kind: 'response', text: short ? 'Done.' : `Reply ${i + 1}.\n\n${'A long-history paragraph.\n\n'.repeat(12)}` }],
      partsMore: 0, truncated: false, unreadable: false,
    }));
    const latest = turns.at(-1);
    demo.host.historyTurns.set(ID, turns);
    demo.host.historyDelayMs = latency;
    demo.upsert({
      id: ID, agent: 'copilot', title: 'History fill fixture', status: 'idle',
      startedAt: turns[0].at, updatedAt: latest.at, statusSince: latest.at,
      activity: {
        turn: latest.id, responseTurn: latest.id, turnCount: count,
        userMessage: latest.prompt.text, response: latest.parts[0].text,
      },
    });
    const traffic = window.historyFillTraffic = { pages: [], pending: 0, last: performance.now() };
    const request = demo.client.request.bind(demo.client);
    const bytes = (value) => new TextEncoder().encode(JSON.stringify(value)).length;
    demo.client.request = async (op, data) => {
      if (op !== 'session.history' || data.id !== ID) { return request(op, data); }
      traffic.pending++;
      traffic.last = performance.now();
      const requestBytes = bytes({ op, data });
      try {
        const result = await request(op, data);
        traffic.pages.push({ requestBytes, responseBytes: bytes(result), turns: result.turns?.length ?? 0 });
        return result;
      } finally {
        traffic.pending--;
        traffic.last = performance.now();
      }
    };
  }, { ID, count: COUNT, latency: LATENCY_MS, short });
  await open(page, ID);
}

async function settled(page) {
  await page.waitForFunction(() => {
    const t = window.historyFillTraffic;
    return t.pages.length && t.pending === 0 && performance.now() - t.last > 1200;
  });
}

async function report(page, label) {
  const measured = await page.evaluate(() => {
    const pages = window.historyFillTraffic.pages;
    return {
      requests: pages.length,
      requestBytes: pages.reduce((n, p) => n + p.requestBytes, 0),
      responseBytes: pages.reduce((n, p) => n + p.responseBytes, 0),
      fetchedTurns: pages.reduce((n, p) => n + p.turns, 0),
    };
  });
  const result = { fixture: ID, turns: COUNT, latencyMs: LATENCY_MS, label, ...measured };
  console.log(`History fill JSON payloads (not wire bytes): ${JSON.stringify(result)}`);
  await test.info().attach(`${label}-history-payloads`, { body: JSON.stringify(result, null, 2), contentType: 'application/json' });
  return measured;
}

test('opening long history does not fetch beyond the first view on a poor link', async ({ page }) => {
  await start(page);
  await settled(page);
  const measured = await report(page, 'automatic-open');
  expect(measured.requests).toBeLessThanOrEqual(2);
  // The newest five long turns already overflow, so no older page is needed.
  expect(measured.requests).toBe(1);
  const scroll = view(page, ID).locator('.stream-scroll');
  await expect.poll(() => scroll.evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight)).toBeLessThan(4);
});

test('a tall first view with short turns fills with at most one older page', async ({ page }) => {
  await start(page, { short: true, height: 1600 });
  await settled(page);
  const measured = await report(page, 'tall-short-open');
  expect(measured.requests).toBe(2);
  await expect.poll(() => view(page, ID).locator('.stream-scroll').evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);
});

test('resize and reopening do not restart the automatic older-page allowance', async ({ page }) => {
  await start(page, { short: true, height: 1600 });
  await settled(page);
  const before = await report(page, 'before-resize');
  await page.setViewportSize({ width: 1440, height: 6000 });
  await open(page, 'signin-1');
  await open(page, ID);
  await settled(page);
  const after = await report(page, 'after-reopen');
  expect(before.requests).toBe(2);
  expect(after.requests).toBe(before.requests);
});

test('near-top loading, Try again and manual Earlier still reach the full history', async ({ page }) => {
  await start(page);
  await settled(page);
  const v = view(page, ID);
  const rows = v.locator('.history-turn');
  const before = await rows.count();
  await page.evaluate(() => {
    const client = window.agentBridgeDemo.client;
    const history = client.history.bind(client);
    let fail = true;
    client.history = (id, options) => {
      if (id === 'history-fill' && options?.before && fail) {
        fail = false;
        return Promise.reject(new Error('Earlier page failed for this test'));
      }
      return history(id, options);
    };
  });
  await v.locator('.stream-scroll').evaluate((el) => { el.dispatchEvent(new Event('wheel')); el.scrollTop = 0; });
  const retry = v.getByRole('button', { name: 'Try again', exact: true });
  await expect(retry).toBeVisible();
  await retry.evaluate((b) => b.click());
  await expect.poll(() => rows.count()).toBeGreaterThan(before);
  while (await rows.count() < COUNT - 1) {
    const n = await rows.count();
    await v.getByRole('button', { name: 'Load earlier messages', exact: true }).evaluate((b) => b.click());
    await expect.poll(() => rows.count()).toBeGreaterThan(n);
  }
  await expect(rows).toHaveCount(COUNT - 1);
  await expect(v.locator('.history-top')).toContainText('Start of session');
  await expect(rows.first()).toContainText('Prompt 1');
  await expect(v.getByRole('button', { name: 'Load earlier messages' })).toHaveCount(0);
  await report(page, 'manual-full-history');
});
