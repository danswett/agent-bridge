// Scrolling a long conversation on an iPhone never holds the page up (#1684, part C of #1702).
// The owner's phone hung periodically while scrolling a long session; #1687 removed the per-scroll
// layout reads and #1719 the custom-property relayout. The last test keeps that proven: a 500-turn
// session, an iPhone's WebKit viewport, the whole history drawn, then the pane is scrolled to the top
// and back and no task may run over 50 ms. The first two are the owner's case: idle on a long session,
// a live update or a new turn lands, then a scroll must answer, and the turns already drawn must not
// be taken out and put back.
// WebKit has no long-task observer, so a heartbeat (one message-channel task after another) is
// timed instead: a task that runs long shows as a gap between two beats. The page is drawn at 1x:
// a headless host paints 3x in software and its raster stalls, which are not the page's, would
// show as gaps of their own.
import { test, expect } from '@playwright/test';
import { go } from './nav.mjs';
import { HISTORY } from '../js/history.js';

const TURNS = 500;
const BUDGET_MS = 50;

test.use({
  viewport: { width: 390, height: 844 },
  deviceScaleFactor: 1,
  isMobile: true,
  hasTouch: true,
  userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
});

async function openLong(page) {
  await page.goto('?demo=1&static=1&signin=1&name=DEV-VM1#/sessions');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate((turnCount) => {
    const at = new Date().toISOString();
    window.agentBridgeDemo.upsert({ id: 'long-1', agent: 'copilot', title: 'A long one.', cwd: 'C:\\Users\\demo\\repos\\agent-bridge', status: 'working', statusSince: at, startedAt: at, updatedAt: at, processId: 4300, activity: { userMessage: 'A long one.', turnCount, longReplies: true } });
  }, TURNS);
  await go(page, 'sessions');
  await page.locator('.session[data-id="long-1"] .row-main').click();
  const pane = page.locator('.session-view[data-id="long-1"] .stream-scroll');
  await expect.poll(() => pane.locator('.history-turn').count(), { timeout: 30000 }).toBeGreaterThanOrEqual(HISTORY.fillTurns);
  return pane;
}

// Turns already drawn must never be taken out and put back: that dirties the layout of every one.
const watchRemoved = (pane) => pane.evaluate((el) => {
  const history = el.querySelector('.history');
  window.__out = new Set();
  new MutationObserver((records) => { for (const r of records) { for (const n of r.removedNodes) { window.__out.add(n); } } }).observe(history, { childList: true });
});
const putBack = (page) => page.evaluate(() => [...window.__out].filter((n) => n.isConnected).length);

// The owner's case: idle on a long session, one message lands, then he scrolls.
test('a scroll right after one live update lands responds within 100 ms', async ({ page, browserName }) => {
  const pane = await openLong(page);
  await page.evaluate(() => { const s = window.agentBridgeDemo.host.get('long-1'); window.agentBridgeDemo.upsert({ ...s, updatedAt: new Date().toISOString() }); });
  await page.waitForTimeout(1500);
  await watchRemoved(pane);
  const costs = [];
  for (let i = 0; i < 7; i += 1) {
    costs.push(await pane.evaluate(async (el) => {
      window.agentBridgeDemo.upsert({ ...window.agentBridgeDemo.host.get('long-1'), updatedAt: new Date().toISOString() });
      const start = performance.now();
      await new Promise((r) => setTimeout(r, 0));
      el.scrollTop -= 300;
      void el.scrollTop;
      await new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0)));
      return Math.round(performance.now() - start);
    }));
    await page.waitForTimeout(400);
  }
  expect(await putBack(page), 'drawn turns taken out and put back').toBe(0);
  // Timing is only held on Chromium: CI's WebKit runs in software and is several times slower.
  if (browserName === 'chromium') {
    // The first two scrolls pay for first layout of the long turns; the budget covers the repeats.
    expect(Math.max(...costs.slice(2)), `scroll after update took ${JSON.stringify(costs)} ms`).toBeLessThan(100);
  } else {
    expect(Math.max(...costs), `scroll after update took ${JSON.stringify(costs)} ms`).toBeLessThan(1000);
  }
});

// A new message adds a turn: the turns already drawn must not be redrawn (Engineering review of #1707).
test('a scroll right after a new turn arrives responds within 100 ms', async ({ page, browserName }) => {
  const pane = await openLong(page);
  await page.waitForTimeout(1500);
  await watchRemoved(pane);
  const costs = [];
  for (let i = 0; i < 6; i += 1) {
    costs.push(await pane.evaluate(async (el) => {
      const s = window.agentBridgeDemo.host.get('long-1');
      const turnCount = s.activity.turnCount + 1;
      window.agentBridgeDemo.upsert({ ...s, updatedAt: new Date().toISOString(), activity: { ...s.activity, turnCount, turn: `long-1-turn-${turnCount}` } });
      const start = performance.now();
      await new Promise((r) => setTimeout(r, 0));
      el.scrollTop -= 300;
      void el.scrollTop;
      await new Promise((r) => requestAnimationFrame(() => setTimeout(r, 0)));
      return Math.round(performance.now() - start);
    }));
    await page.waitForTimeout(600);
  }
  expect(await putBack(page), 'drawn turns taken out and put back').toBe(0);
  if (browserName === 'chromium') {
    expect(Math.max(...costs.slice(2)), `scroll after a new turn took ${JSON.stringify(costs)} ms`).toBeLessThan(100);
  } else {
    // A loose bound so a real hang still fails on WebKit: no scroll waits over 1 s.
    expect(Math.max(...costs), `scroll after a new turn took ${JSON.stringify(costs)} ms`).toBeLessThan(1000);
  }
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
