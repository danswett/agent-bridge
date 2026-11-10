// Scrolling a long conversation on an iPhone never holds the page up for 50 ms (#1684, part C of #1702).
// The owner's phone hung periodically while scrolling a long session; #1687 removed the per-scroll
// layout reads. This keeps that proven: a 500-turn session, an iPhone's WebKit viewport, the whole
// history drawn, then the pane is scrolled to the top and back and no task may run over 50 ms.
// WebKit has no long-task observer, so a heartbeat (one message-channel task after another) is
// timed instead: a task that runs long shows as a gap between two beats. The page is drawn at 1x:
// a headless host paints 3x in software and its raster stalls, which are not the page's, would
// show as gaps of their own.
import { test, expect } from '@playwright/test';
import { go } from './nav.mjs';

const TURNS = 500;
const BUDGET_MS = 50;

test.use({
  viewport: { width: 390, height: 844 },
  deviceScaleFactor: 1,
  isMobile: true,
  hasTouch: true,
  userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
});

test(`scrolling a ${TURNS}-turn session on an iPhone never runs a task over ${BUDGET_MS} ms`, async ({ page }) => {
  await page.goto('?demo=1&static=1&signin=1&name=DEV-VM1#/sessions');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate((turnCount) => {
    const at = new Date().toISOString();
    window.agentBridgeDemo.upsert({ id: 'long-1', agent: 'copilot', title: 'A long one.', cwd: 'C:\\Users\\demo\\repos\\agent-bridge', status: 'idle', statusSince: at, startedAt: at, updatedAt: at, processId: 4300, activity: { userMessage: 'A long one.', turnCount, longReplies: true } });
  }, TURNS);
  await go(page, 'sessions');
  await page.locator('.session[data-id="long-1"] .row-main').click();
  const pane = page.locator('.session-view[data-id="long-1"] .stream-scroll');
  // Wait until the history has stopped filling in (it is drawn a few turns a frame), so the scroll
  // is over as much of the 500 turns as the app draws.
  let drawn = 0;
  let stableFor = 0;
  await expect.poll(async () => {
    const n = await pane.locator('.history-turn').count();
    stableFor = n === drawn ? stableFor + 1 : 0;
    drawn = n;
    return stableFor >= 4 && n > 100;
  }, { timeout: 30000, intervals: [500] }).toBe(true);

  const m = await pane.evaluate((el) => new Promise((resolve) => {
    let running = true;
    let worstMs = 0;
    let last = performance.now();
    const channel = new MessageChannel();
    channel.port1.onmessage = () => {
      const now = performance.now();
      worstMs = Math.max(worstMs, now - last);
      last = now;
      if (running) { channel.port2.postMessage(0); }
    };
    channel.port2.postMessage(0);
    const frame = () => new Promise((r) => { requestAnimationFrame(() => r()); });
    (async () => {
      const STEPS = 80;
      const range = () => el.scrollHeight - el.clientHeight;
      const startAt = el.scrollTop;
      let scrollEvents = 0;
      el.addEventListener('scroll', () => { scrollEvents++; }, { passive: true });
      for (let i = 1; i <= STEPS; i++) { el.scrollTop = startAt - (startAt * i) / STEPS; await frame(); }
      for (let i = 1; i <= STEPS; i++) { el.scrollTop = (range() * i) / STEPS; await frame(); }
      running = false;
      await frame();
      resolve({ worstMs, scrollEvents, range: range() });
    })();
  }));

  expect(m.range).toBeGreaterThan(10000);
  expect(m.scrollEvents).toBeGreaterThan(50);
  expect(m.worstMs).toBeLessThan(BUDGET_MS);
});
