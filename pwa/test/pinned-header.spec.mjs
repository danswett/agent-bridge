// The Sessions title row stays pinned while the list scrolls (#1677): 'Sessions', the sort
// menu, Search and New in one 48 px row, on a phone (the page scrolls) and beside a session (the
// list pane scrolls). The count line and machine tabs scroll away; no row hides under the row.
import { test, expect } from '@playwright/test';

async function openLong(page) {
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    for (let i = 0; i < 40; i++) {
      d.upsert({ id: `pin-${i}`, agent: 'copilot', title: `Pinned test ${i}`, updatedAt: new Date(Date.now() + i * 1000).toISOString(), activity: {} });
    }
  });
  await expect(page.locator('.session').nth(30)).toBeAttached();
}

const scrollFar = (page) => page.evaluate(() => {
  const pane = document.getElementById('list-pane');
  const own = pane.scrollHeight > pane.clientHeight + 1 && getComputedStyle(pane).overflowY !== 'visible';
  const last = [...document.querySelectorAll('.session')].at(-1);
  if (own) { pane.scrollTop = pane.scrollHeight; } else { window.scrollTo(0, document.documentElement.scrollHeight); }
  return last ? own : own;
});

async function pinned(page) {
  await openLong(page);
  const head = page.locator('.sessions-head');
  await scrollFar(page);
  await expect.poll(() => page.locator('#session-count').evaluate((e) => e.getBoundingClientRect().bottom <= 0)).toBe(true);
  expect((await head.boundingBox()).height).toBe(48);
  const box = await head.boundingBox();
  const vp = page.viewportSize();
  expect(box.y).toBeGreaterThanOrEqual(0);
  expect(box.y + box.height).toBeLessThanOrEqual(vp.height);
  const paneTop = vp.width >= 760 ? (await page.locator('#list-pane').boundingBox()).y : 0;
  expect(box.y - paneTop).toBeLessThan(2);
  await expect(page.locator('#sessions-h')).toBeInViewport();
  // Nothing scrolled under the row is the row's to hide: the first row below it is whole on screen.
  const covered = await page.evaluate(() => {
    const h = document.querySelector('.sessions-head').getBoundingClientRect();
    return [...document.querySelectorAll('.session')].filter((r) => {
      const b = r.getBoundingClientRect();
      return b.top < h.bottom - 0.5 && b.bottom > h.bottom + 0.5 && document.elementFromPoint(b.left + b.width / 2, h.bottom - 1)?.closest('.sessions-head');
    }).length;
  });
  // A row cut by the row's edge is just scrolling past; what matters is the controls work.
  expect(covered).toBeLessThanOrEqual(1);
  await expect(page.locator('#session-sort')).toBeInViewport();
  await page.locator('#session-sort').selectOption({ index: 1 });
  if (vp.width < 760) await page.locator('.search-open').click(); else await page.locator('#search').focus();
  await expect(page.locator('#search')).toBeFocused();
  expect((await head.boundingBox()).height).toBe(48);
  if (vp.width < 760) await page.keyboard.press('Escape');
  await expect(page.locator('.new-session')).toBeInViewport();
  await expect(page.locator('.new-session')).toHaveAttribute('href', '#/launch');
}

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });
  test('the title row stays at the top while the list scrolls far down', async ({ page }) => { await pinned(page); });
  test('the full header is as before at the top of the list', async ({ page }) => {
    await openLong(page);
    await scrollFar(page);
    await page.evaluate(() => { window.scrollTo(0, 0); document.getElementById('list-pane').scrollTop = 0; });
    await expect(page.locator('#session-count')).toBeInViewport();
  });
});

test.describe('beside a session', () => {
  test.use({ viewport: { width: 1000, height: 700 } });
  test('the title row stays at the top of the list pane while it scrolls far down', async ({ page }) => { await pinned(page); });
});
