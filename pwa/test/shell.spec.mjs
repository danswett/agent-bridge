// The shell (docs/design/ui.md §7, PWA; #179): on a desktop, the web app bar and the rail; on a
// phone, the page's title row and the bottom navigation. Every page is a route, so a reload stays
// on it and the browser's own back goes between them.
import { test, expect } from '@playwright/test';
import { go } from './nav.mjs';

const CODEX = '019a1b2c-codex-example';

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

const overflow = (page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
const route = (page) => page.evaluate(() => document.body.dataset.page);

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

for (const width of [900, 1440]) {
  test.describe(`at ${width} px`, () => {
    test.use({ viewport: { width, height: 900 } });

    test('a 56 px app bar holds the mark and name, search, machines online and the connection, over a rail of every page', async ({ page }) => {
      await openDemo(page, '&machines=2');
      const bar = await page.locator('#appbar').boundingBox();
      expect(bar.height).toBe(56);
      expect(bar.y).toBe(0);
      await expect(page.locator('.brand')).toBeVisible();
      await expect(page.locator('.brand-name')).toHaveText('Agent Bridge');
      await expect(page.locator('#search')).toBeVisible();
      await expect(page.locator('#online')).toHaveText('2 machines');
      await expect(page.locator('#conn')).toHaveText('Demo');
      const nav = page.locator('#nav');
      await expect(nav.locator('.nav-item')).toHaveCount(5);
      for (const n of ['sessions', 'machines', 'launch', 'usage', 'settings']) { await expect(nav.locator(`[data-nav="${n}"]`)).toBeVisible(); }
      // A rail down the left, Settings at its foot.
      const sessions = await nav.locator('[data-nav="sessions"]').boundingBox();
      const settings = await nav.locator('[data-nav="settings"]').boundingBox();
      expect(sessions.x).toBeLessThan(48);
      expect(settings.x).toBe(sessions.x);
      expect(settings.y).toBeGreaterThan(800);
      await expect(nav.locator('[data-nav="sessions"]')).toHaveAttribute('aria-current', 'page');
      await expect(nav.locator('[data-nav="sessions"]')).toHaveAccessibleName('Sessions');
      expect(await overflow(page)).toBeLessThanOrEqual(0);
    });
  });
}

test('each page is a route: the rail goes there, a reload stays, and the browser\'s back returns', async ({ page }) => {
  await openDemo(page);
  await go(page, 'machines');
  expect(new URL(page.url()).hash).toBe('#/machines');
  await expect(page.locator('#machines')).toBeVisible();
  await expect(page.locator('#session-list')).toBeHidden();
  await go(page, 'launch');
  await expect(page.locator('#launch .launch-title')).toHaveText('Start a session on Demo machine');
  await go(page, 'settings');
  await expect(page.locator('#nav [data-nav="settings"]')).toHaveAttribute('aria-current', 'page');

  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  expect(await route(page)).toBe('settings');
  await page.goBack();
  await expect.poll(() => route(page)).toBe('launch');
  await page.goBack();
  await expect.poll(() => route(page)).toBe('machines');
  await page.goBack();
  await expect.poll(() => route(page)).toBe('sessions');
  await expect(page.locator('#session-list')).toBeVisible();
});

test('Ctrl+K focuses search, which narrows the list to the sessions that match, and Escape clears it', async ({ page }) => {
  await openDemo(page);
  await go(page, 'usage');
  await page.keyboard.press('Control+k');
  await expect(page.locator('#search')).toBeFocused();
  await page.keyboard.type('flaky');
  // Search is the sessions list's: typing takes you there.
  await expect.poll(() => route(page)).toBe('sessions');
  await expect(page.locator('.session')).toHaveCount(1);
  await expect(page.locator(`.session[data-id="${CODEX}"]`)).toBeVisible();
  // While it narrows the list, rows don't move.
  await expect(page.locator('#session-list')).toHaveAttribute('data-filtered', 'true');
  await page.keyboard.type('zzz');
  await expect(page.locator('.session')).toHaveCount(0);
  await expect(page.locator('#sessions-empty')).toHaveText('No sessions match your search.');
  await page.keyboard.press('Escape');
  await expect(page.locator('#search')).toHaveValue('');
  await expect(page.locator('.session')).toHaveCount(4);
  await expect(page.locator('#sessions-empty')).toBeHidden();
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

  test('a 24/32 title row with Search and New, and a bottom bar of Sessions, Machines, Usage and Settings with a Bridge indicator', async ({ page }) => {
    await openDemo(page, '&machines=2');
    // The app bar gives way to the page's own title row while all is well.
    await expect(page.locator('#appbar')).toBeHidden();
    const title = page.locator('#sessions-h');
    expect(await title.evaluate((e) => [getComputedStyle(e).fontSize, getComputedStyle(e).lineHeight, getComputedStyle(e).fontWeight])).toEqual(['24px', '32px', '600']);
    await expect(page.locator('.search-open')).toBeVisible();
    await expect(page.locator('.new-session')).toHaveAccessibleName('New session');
    const head = await page.locator('.sessions-head').boundingBox();
    for (const b of [page.locator('.search-open'), page.locator('.new-session')]) {
      const r = await b.boundingBox();
      expect(r.height).toBeGreaterThanOrEqual(44);
      expect(r.x + r.width).toBeLessThanOrEqual(head.x + head.width + 0.5);
    }
    // Machine tabs under it.
    await expect(page.locator('#machine-tabs [role="tab"]')).toHaveText(['All', 'Demo machine', 'Laptop']);

    const nav = page.locator('#nav');
    const bar = await nav.boundingBox();
    expect(bar.y + bar.height).toBe(844);
    await expect(nav.locator('.nav-item:visible')).toHaveText(['Sessions', 'Machines', 'Usage', 'Settings']);
    const current = nav.locator('[aria-current="page"]');
    await expect(current).toHaveText('Sessions');
    const indicator = await current.evaluate((e) => getComputedStyle(e, '::before').backgroundColor);
    const bridge = await page.evaluate(() => { const p = document.createElement('i'); p.style.color = 'var(--ab-bridge)'; document.body.append(p); const c = getComputedStyle(p).color; p.remove(); return c; });
    expect(indicator).toBe(bridge);
    for (const item of await nav.locator('.nav-item:visible').all()) { expect((await item.boundingBox()).height).toBeGreaterThanOrEqual(44); }
    expect(await overflow(page)).toBeLessThanOrEqual(0);

    // New is the way to Launch.
    await page.locator('.new-session').tap();
    await expect(page.locator('#launch .launch-title')).toHaveText('Start a session on Demo machine');
    await page.goBack();
    await expect.poll(() => route(page)).toBe('sessions');
  });

  test('Search opens a search row over the title, and closing it brings back the whole list', async ({ page }) => {
    await openDemo(page);
    await page.locator('.search-open').tap();
    await expect(page.locator('#appbar')).toBeVisible();
    await expect(page.locator('#search')).toBeFocused();
    await page.keyboard.type('relay');
    await expect(page.locator('.session')).toHaveCount(1);
    await page.locator('#search-close').tap();
    await expect(page.locator('#appbar')).toBeHidden();
    await expect(page.locator('.session')).toHaveCount(4);
  });

  test('the app bar comes back as a strip while the connection has news', async ({ page }) => {
    await openDemo(page);
    await expect(page.locator('#appbar')).toBeHidden();
    await page.evaluate(() => { document.body.dataset.conn = 'warn'; document.getElementById('conn').textContent = 'Reconnecting…'; });
    await expect(page.locator('#conn')).toBeVisible();
    await expect(page.locator('.brand')).toBeHidden();
  });
});

test('signed out, there is only the sign-in page: no navigation, no search', async ({ page }) => {
  await page.goto('./');
  await expect(page.locator('#signin')).toBeVisible();
  await expect(page.locator('#nav')).toBeHidden();
  await expect(page.locator('#search')).toBeHidden();
  await expect(page.locator('#online')).toBeHidden();
  await expect(page.locator('#avatar')).toBeHidden();
  await expect(page.locator('[data-route]:visible')).toHaveCount(0);
});
