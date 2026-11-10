// An installed iPhone's phone layout (#1682): the session pane ends at the screen's bottom edge
// (never past it), the reply box sits the home indicator's band plus its own padding above that
// edge, and the header sits under the status bar with no blur or filter over it.
// #1627 extended the pane past the viewport to chase a gap measured from a faked screen; the
// owner's phone then showed the box too low and the header blurred. This pins the layout that works.
// The platform and safe areas are set as the platform layer sets them (data-platform,
// data-standalone, --env-safe-*), because a desktop browser has no real safe area.
import { test, expect } from '@playwright/test';
import { open } from './nav.mjs';

const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const later = (ms = 1000) => new Date(Date.now() + ms).toISOString();
const LONG = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
const SAFE_TOP = 47;
const SAFE_BOTTOM = 34;

test.use({
  viewport: { width: 390, height: 844 },
  deviceScaleFactor: 3,
  isMobile: true,
  hasTouch: true,
  userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
});

async function installed(page, { long }) {
  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'standalone', { value: true });
    // iOS standalone can lay the page out a status bar short of the screen; the layout must not chase that gap (#1627 did).
    Object.defineProperty(window.screen, 'width', { value: 390 });
    Object.defineProperty(window.screen, 'height', { value: 891 });
  });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  if (long) {
    await page.evaluate(({ id, response, at }) => {
      const d = window.agentBridgeDemo;
      const s = d.host.get(id) ?? { id };
      d.upsert({ ...s, id, activity: { ...(s.activity ?? {}), response, reasoning: null, userMessage: null, currentTool: null }, question: null, updatedAt: at });
    }, { id: IDLE, response: LONG, at: later() });
  }
  const v = await open(page, IDLE);
  await expect(v.locator('form.reply').getByRole('textbox', { name: 'Reply' })).toBeVisible();
  await page.evaluate(([top, bottom]) => {
    const h = document.documentElement;
    h.dataset.platform = 'ios';
    h.dataset.standalone = 'true';
    h.style.setProperty('--env-safe-top', `${top}px`);
    h.style.setProperty('--env-safe-bottom', `${bottom}px`);
  }, [SAFE_TOP, SAFE_BOTTOM]);
  await frame(page);
}

const frame = (page) => page.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))));

const geometry = (page) => page.evaluate(() => {
  const pane = document.querySelector('.session-pane').getBoundingClientRect();
  const box = [...document.querySelectorAll('.reply-box')].map((e) => e.getBoundingClientRect()).find((r) => r.width > 0 && r.height > 0);
  const top = document.querySelector('.session-top');
  const bar = [...document.querySelectorAll('.session-bar > .icon-button')].map((e) => e.getBoundingClientRect()).find((r) => r.width > 0);
  const blurred = [top, ...top.querySelectorAll('*'), document.querySelector('.session-pane')].filter((el) => {
    const cs = getComputedStyle(el);
    return [cs.backdropFilter, cs.webkitBackdropFilter, cs.filter].some((f) => f && f !== 'none');
  }).length;
  return {
    paneBottomGap: innerHeight - pane.bottom,
    paneTop: pane.top,
    docOverflow: document.documentElement.scrollHeight - innerHeight,
    boxGap: innerHeight - box.bottom,
    barTop: bar.top,
    blurred,
  };
});

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

for (const long of [true, false]) {
  test(`installed on an iPhone, a ${long ? 'long' : 'short'} conversation ends at the screen's edge, the reply box sits 46 above it and the header is clear and unblurred`, async ({ page }, info) => {
    await installed(page, { long });
    const at = await geometry(page);
    // The pane is the screen: not a pixel past its bottom edge, and the page does not scroll.
    expect(at.paneBottomGap).toBe(0);
    expect(at.paneTop).toBe(0);
    expect(at.docOverflow).toBeLessThanOrEqual(0);
    // The box: the home indicator's band (34) and the slot's own padding (12).
    expect(at.boxGap).toBe(SAFE_BOTTOM + 12);
    // The header starts under the status bar and nothing blurs it.
    expect(at.barTop).toBeGreaterThanOrEqual(SAFE_TOP);
    expect(at.blurred).toBe(0);
    await info.attach(`installed-iphone-${long ? 'long' : 'short'}.png`, { body: await page.screenshot(), contentType: 'image/png' });

    // The keyboard up: the viewport shrinks to the keyboard's top and no home indicator is cleared.
    await page.evaluate(() => document.documentElement.style.setProperty('--env-safe-bottom', '0px'));
    await page.setViewportSize({ width: 390, height: 500 });
    await frame(page);
    const up = await geometry(page);
    expect(up.paneBottomGap).toBe(0);
    expect(up.boxGap).toBe(12);
    expect(up.blurred).toBe(0);

    // Closed again: back to where it was.
    await page.setViewportSize({ width: 390, height: 844 });
    await page.evaluate((px) => document.documentElement.style.setProperty('--env-safe-bottom', `${px}px`), SAFE_BOTTOM);
    await frame(page);
    expect(await geometry(page)).toEqual(at);
  });
}
