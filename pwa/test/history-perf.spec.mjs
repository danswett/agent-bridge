// A session starts scrolling within 200 ms of opening it, whatever its history's length (#1622).
// The budget is for the page as it would be on a phone: chromium runs with the CPU slowed
// (WebKit cannot be slowed, so it runs at full speed, which is the same bound, easier to meet).
import { test, expect } from '@playwright/test';
import { go } from './nav.mjs';

const BUDGET_MS = 200;
const TURNS = 500;

async function longSession(page, browserName) {
  if (browserName === 'chromium') {
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: 2 });
  }
  await page.goto('?demo=1&static=1&signin=1&name=DEV-VM1#/sessions');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate((turnCount) => {
    const at = new Date().toISOString();
    window.agentBridgeDemo.upsert({ id: 'long-1', agent: 'copilot', title: 'A long one.', cwd: 'C:\\Users\\demo\\repos\\agent-bridge', status: 'idle', statusSince: at, startedAt: at, updatedAt: at, processId: 4300, activity: { userMessage: 'A long one.', turnCount, longReplies: true } });
  }, TURNS);
  await expect(page.locator('.session[data-id="long-1"]')).toBeVisible();
}

// Clicks the row in the page itself and reads, frame by frame, how long until the pane has turns
// to scroll, and the longest a frame was held up from then until the older turns were all in.
function openAndTime(page) {
  return page.evaluate(() => new Promise((resolve) => {
    const frame = () => new Promise((r) => { requestAnimationFrame(() => r()); });
    (async () => {
      const t0 = performance.now();
      document.querySelector('.session[data-id="long-1"] .row-main').click();
      const pane = () => document.querySelector('.session-view[data-id="long-1"] .stream-scroll');
      const turns = () => pane()?.querySelectorAll('.history-turn').length ?? 0;
      while (turns() === 0) { await frame(); }
      const firstScrollMs = performance.now() - t0;
      const firstTurns = turns();
      let worstFrameMs = 0;
      let last = performance.now();
      while (turns() < 60) {
        await frame();
        const now = performance.now();
        worstFrameMs = Math.max(worstFrameMs, now - last);
        last = now;
      }
      resolve({ firstScrollMs, firstTurns, worstFrameMs, scrollGap: pane().scrollHeight - pane().scrollTop - pane().clientHeight });
    })();
  }));
}

test(`a ${TURNS}-turn session scrolls within ${BUDGET_MS} ms of opening, and older turns fill in without holding up a frame`, async ({ page, browserName }) => {
  await longSession(page, browserName);
  await go(page, 'sessions');
  const m = await openAndTime(page);
  expect(m.firstScrollMs).toBeLessThan(BUDGET_MS);
  expect(m.firstTurns).toBeLessThanOrEqual(5);
  expect(m.worstFrameMs).toBeLessThan(BUDGET_MS);
  // Older turns arrived above, and the view stayed at the latest, not moved by them.
  expect(m.scrollGap).toBeLessThan(4);
});
