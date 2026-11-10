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

// Clicks the row in the page itself and reads how long until the pane has turns to scroll (seen
// as they are put in, not at the next frame), and the longest a frame was held up from then until
// the older turns were all in, beyond the longest an idle frame took (a slow host's frame rate is not
// the page's).
function openAndTime(page) {
  return page.evaluate(() => new Promise((resolve) => {
    const frame = () => new Promise((r) => { requestAnimationFrame(() => r()); });
    (async () => {
      let idleFrameMs = 0;
      let at = performance.now();
      for (let i = 0; i < 8; i++) { await frame(); const n = performance.now(); idleFrameMs = Math.max(idleFrameMs, n - at); at = n; }
      const pane = () => document.querySelector('.session-view[data-id="long-1"] .stream-scroll');
      const turns = () => pane()?.querySelectorAll('.history-turn').length ?? 0;
      const t0 = performance.now();
      let firstScrollMs = null;
      let firstTurns = 0;
      const seen = new MutationObserver(() => {
        if (firstScrollMs === null && turns() > 0) { firstScrollMs = performance.now() - t0; firstTurns = turns(); seen.disconnect(); }
      });
      seen.observe(document.body, { childList: true, subtree: true });
      document.querySelector('.session[data-id="long-1"] .row-main').click();
      while (firstScrollMs === null) { await frame(); }
      let worstFrameMs = 0;
      let last = performance.now();
      while (turns() < 60) {
        await frame();
        const now = performance.now();
        worstFrameMs = Math.max(worstFrameMs, now - last);
        last = now;
      }
      resolve({ firstScrollMs, firstTurns, worstFrameMs: worstFrameMs - idleFrameMs, scrollGap: pane().scrollHeight - pane().scrollTop - pane().clientHeight });
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

// Opening shows the latest at the bottom and stays there until the person scrolls (#1646).
const gap = (page) => page.evaluate(() => { const p = document.querySelector('.session-view[data-id="long-1"] .stream-scroll'); return p.scrollHeight - p.scrollTop - p.clientHeight; });

test('a long session opens at the bottom, and stays there while older turns and a late reply arrive (#1646)', async ({ page, browserName }) => {
  await longSession(page, browserName);
  await page.evaluate(() => { window.agentBridgeDemo.host.historyDelayMs = 300; });
  await go(page, 'sessions');
  await page.locator('.session[data-id="long-1"] .row-main').click();
  const pane = page.locator('.session-view[data-id="long-1"] .stream-scroll');
  await expect(pane.locator('.history-turn')).not.toHaveCount(0);
  await expect.poll(() => pane.locator('.history-turn').count()).toBeGreaterThan(30);
  expect(await gap(page)).toBeLessThan(4);
  await page.evaluate(() => {
    const host = window.agentBridgeDemo.host;
    const s = host.get('long-1');
    window.agentBridgeDemo.upsert({ ...s, updatedAt: new Date(Date.now() + 1000).toISOString(), activity: { ...s.activity, turnCount: s.activity.turnCount + 1 } });
  });
  await expect.poll(() => pane.locator('.history-turn').count()).toBeGreaterThan(30);
  await expect.poll(() => gap(page)).toBeLessThan(4);
});

test('a person who scrolled up is not taken back to the bottom as older turns arrive (#1646)', async ({ page, browserName }) => {
  await longSession(page, browserName);
  await go(page, 'sessions');
  await page.locator('.session[data-id="long-1"] .row-main').click();
  const pane = page.locator('.session-view[data-id="long-1"] .stream-scroll');
  await expect.poll(() => pane.locator('.history-turn').count()).toBeGreaterThan(12);
  await pane.evaluate((el) => { el.dispatchEvent(new Event('wheel')); el.scrollTop = 0; });
  const before = await pane.locator('.history-turn').count();
  await expect.poll(() => pane.locator('.history-turn').count()).toBeGreaterThan(before + 20);
  expect(await gap(page)).toBeGreaterThan(200);
});
