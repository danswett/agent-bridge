import { test, expect } from '@playwright/test';
import { go, open, motionsDone } from './nav.mjs';

const ID = 'open-latest-long';
const TURNS = 80;
const reply = (turn) => Array.from({ length: 30 }, (_, i) => `Reply ${turn}, paragraph ${i + 1}.`).join('\n\n');
const gap = (pane) => pane.evaluate((el) => el.scrollHeight - el.clientHeight - el.scrollTop);

test.use({
  viewport: { width: 390, height: 844 },
  deviceScaleFactor: 1,
  isMobile: true,
  hasTouch: true,
  reducedMotion: 'reduce',
  userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
});

async function arrive(page, turn) {
  await page.evaluate(({ id, turn, response }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({
      ...s, status: 'working', updatedAt: new Date().toISOString(),
      activity: {
        ...s.activity, turnCount: turn, turn: `${id}-turn-${turn}`, responseTurn: `${id}-turn-${turn}`,
        userMessage: `Message ${turn}`, response,
      },
    });
  }, { id: ID, turn, response: reply(turn) });
  await expect(page.locator(`.session-view[data-id="${ID}"] .stream-scroll`)).toContainText(`Reply ${turn}, paragraph 30.`);
  await motionsDone(page);
}

for (const arriving of [false, true]) {
  test(`opening and reopening a long session shows the latest reply${arriving ? ', including a row arriving after open' : ''} (#1646)`, async ({ page }) => {
    await page.addInitScript(() => { Object.defineProperty(navigator, 'standalone', { value: true }); });
    await page.goto('?demo=1&static=1#/sessions');
    await page.waitForSelector('html[data-demo-ready]');
    await page.evaluate(({ id, turns, response }) => {
      const at = new Date().toISOString();
      window.agentBridgeDemo.host.historyDelayMs = 100;
      window.agentBridgeDemo.upsert({
        id, agent: 'copilot', title: 'Long conversation', cwd: 'C:\\demo', status: 'idle',
        statusSince: at, startedAt: at, updatedAt: at, processId: 4300, question: null,
        activity: {
          turnCount: turns, longReplies: true, turn: `${id}-turn-${turns}`, responseTurn: `${id}-turn-${turns}`,
          userMessage: `Message ${turns}`, response,
        },
      });
    }, { id: ID, turns: TURNS, response: reply(TURNS) });
    const v = await open(page, ID);
    const pane = v.locator('.stream-scroll');
    await expect.poll(() => pane.locator('.history-turn').count()).toBeGreaterThan(20);
    await expect.poll(() => gap(pane)).toBeLessThan(4);
    await expect(v.locator('.response p').last()).toBeInViewport();
    await expect(pane.locator('.history-turn')).toHaveCount(TURNS - 1);
    await expect.poll(() => gap(pane)).toBeLessThan(4);

    await pane.evaluate((el) => { el.dispatchEvent(new Event('touchmove')); el.scrollTop = 0; });
    await expect.poll(() => gap(pane)).toBeGreaterThan(400);
    await go(page, 'sessions');
    await open(page, ID);
    if (arriving) { await arrive(page, TURNS + 1); }
    await expect.poll(() => gap(pane)).toBeLessThan(4);
    await expect(v.locator('.response p').last()).toBeInViewport();

    // Opening starts a new follow interval; scrolling up still ends it.
    await pane.evaluate((el) => { el.dispatchEvent(new Event('touchmove')); el.scrollTop -= 80; });
    await expect.poll(() => gap(pane)).toBeGreaterThan(40);
    await motionsDone(page);
    const held = await pane.evaluate((el) => el.scrollTop);
    await arrive(page, TURNS + (arriving ? 2 : 1));
    expect(await pane.evaluate((el) => el.scrollTop)).toBe(held);
  });
}
