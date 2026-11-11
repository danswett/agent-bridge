import { test, expect } from '@playwright/test';
import { open, motionsDone } from './nav.mjs';

const ID = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const paragraphs = (turn, lines) => Array.from({ length: lines }, (_, i) => `Turn ${turn}, paragraph ${i + 1} of the streaming reply.`).join('\n\n');

test.use({
  viewport: { width: 390, height: 844 },
  deviceScaleFactor: 1,
  // Mobile WebKit rejects wheel input; keep the phone viewport and standalone page.
  isMobile: false,
  hasTouch: true,
  reducedMotion: 'reduce',
  userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
});

async function say(page, turn, lines) {
  await page.evaluate(({ id, turn, response }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({
      ...s, status: 'working', question: null, updatedAt: new Date().toISOString(),
      activity: {
        ...s.activity, turnCount: turn, turn: `${id}-turn-${turn}`, responseTurn: `${id}-turn-${turn}`,
        userMessage: `Message ${turn}`, response, reasoning: null, currentTool: null,
        own: undefined, source: undefined,
      },
    });
  }, { id: ID, turn, response: paragraphs(turn, lines) });
  await expect(page.locator(`.session-view[data-id="${ID}"] .stream-scroll`)).toContainText(`Turn ${turn}, paragraph ${lines} of the streaming reply.`);
  await motionsDone(page);
}

const gap = (pane) => pane.evaluate((el) => el.scrollHeight - el.clientHeight - el.scrollTop);

async function wheel(page, pane, delta) {
  const box = await pane.boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.wheel(0, delta);
  // A resting mouse must not be mistaken for a request to stop following.
  await page.mouse.move(1, 1);
}

async function start(page) {
  await page.addInitScript(() => { Object.defineProperty(navigator, 'standalone', { value: true }); });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await say(page, 12, 30);
  const v = await open(page, ID);
  const pane = v.locator('.stream-scroll');
  await expect(pane.locator('.history-turn')).toHaveCount(11);
  await expect.poll(() => pane.evaluate((el) => el.scrollHeight - el.clientHeight)).toBeGreaterThan(400);
  // Opening at the bottom belongs to #1646; this slice starts with the person's wheel input.
  await wheel(page, pane, 100000);
  await expect.poll(() => gap(pane)).toBeLessThan(4);
  await motionsDone(page);
  return pane;
}

const position = (pane) => pane.evaluate((el) => {
  const box = el.getBoundingClientRect();
  const row = [...el.querySelectorAll('.history-reply p, .response p')].find((p) => {
    const r = p.getBoundingClientRect();
    return r.height > 0 && r.bottom > box.top && r.top < box.bottom;
  });
  if (!row) { throw new Error('No visible reply paragraph in the conversation'); }
  return { top: el.scrollTop, text: row.textContent, y: row.getBoundingClientRect().top - box.top };
});

async function scrollUp(page, pane) {
  const before = await pane.evaluate((el) => el.scrollTop);
  await wheel(page, pane, -40);
  await expect.poll(() => pane.evaluate((el) => el.scrollTop)).toBe(before - 40);
  await motionsDone(page);
  return position(pane);
}

test('a reader 40 px above the bottom stays on the same pixel and paragraph as rows arrive and the last row grows', async ({ page }) => {
  const pane = await start(page);
  const held = await scrollUp(page, pane);
  for (const [turn, lines] of [[13, 30], [13, 45], [14, 30], [14, 50]]) {
    await say(page, turn, lines);
    expect(await position(pane)).toEqual(held);
  }
});

test('a live turn at the bottom is followed all the way to its end (#1705)', async ({ page }) => {
  const pane = await start(page);
  for (const lines of [40, 55, 70, 90]) {
    await say(page, 12, lines);
    await expect.poll(() => gap(pane)).toBeLessThan(4);
  }
});

test('wheel input back to the bottom re-engages following for new rows and streaming growth', async ({ page }) => {
  const pane = await start(page);
  await scrollUp(page, pane);
  await wheel(page, pane, 100000);
  await expect.poll(() => gap(pane)).toBeLessThan(4);
  for (const [turn, lines] of [[13, 30], [13, 45], [14, 30]]) {
    await say(page, turn, lines);
    await expect.poll(() => gap(pane)).toBeLessThan(4);
  }
});
