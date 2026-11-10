// Following a live turn on an installed iPhone (#1702 part B): a conversation scrolled to the
// bottom stays pinned there while the agent's reply streams in, stops following once the reader
// scrolls up, and follows again when the reader comes back to the bottom.
import { test, expect } from '@playwright/test';
import { open } from './nav.mjs';

const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const paragraphs = (n) => Array.from({ length: n }, (_, i) => `Line ${i + 1} of a streaming answer.`).join('\n\n');

test.use({
  viewport: { width: 390, height: 844 },
  deviceScaleFactor: 3,
  isMobile: true,
  hasTouch: true,
  userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
});

let streamed = 0;

async function stream(page, lines) {
  streamed += 1;
  await page.evaluate(({ id, response, at }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({ ...s, status: 'working', activity: { ...(s.activity ?? {}), response, reasoning: null, userMessage: null, currentTool: null }, question: null, updatedAt: at });
  }, { id: IDLE, response: paragraphs(lines), at: new Date(Date.now() + 1000 * streamed).toISOString() });
}

const gap = (page) => page.evaluate((id) => {
  const p = document.querySelector(`.session-view[data-id="${id}"] .stream-scroll`);
  return p.scrollHeight - p.scrollTop - p.clientHeight;
  }, IDLE);

async function openStreaming(page) {
  await page.addInitScript(() => { Object.defineProperty(navigator, 'standalone', { value: true }); });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  streamed = 0;
  await stream(page, 30);
  const v = await open(page, IDLE);
  const pane = v.locator('.stream-scroll');
  // The click that opened it left a mouse resting on the view; a finger leaves none.
  await page.mouse.move(1, 1);
  await expect.poll(() => pane.evaluate((el) => el.scrollHeight - el.clientHeight)).toBeGreaterThan(200);
  await pane.evaluate((el) => { el.scrollTop = el.scrollHeight; });
  await expect.poll(() => gap(page)).toBeLessThan(4);
  return pane;
}

test('a conversation at the bottom stays pinned there while the reply streams in', async ({ page }) => {
  await openStreaming(page);
  for (const lines of [40, 55, 70, 90]) {
    await stream(page, lines);
    await expect.poll(() => gap(page)).toBeLessThan(4);
  }
});

test('a reader who scrolled up is not pulled down by the stream, and it follows again once they return to the bottom', async ({ page }) => {
  const pane = await openStreaming(page);
  await pane.evaluate((el) => { el.dispatchEvent(new Event('wheel')); el.scrollTop = 0; });
  await stream(page, 60);
  await stream(page, 80);
  await expect.poll(() => pane.evaluate((el) => el.scrollTop)).toBeLessThan(4);
  expect(await gap(page)).toBeGreaterThan(400);
  await pane.evaluate((el) => { el.dispatchEvent(new Event('wheel')); el.scrollTop = el.scrollHeight; });
  await expect.poll(() => gap(page)).toBeLessThan(4);
  await stream(page, 100);
  await expect.poll(() => gap(page)).toBeLessThan(4);
});
