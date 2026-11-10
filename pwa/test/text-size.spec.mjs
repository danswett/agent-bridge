// Text size (docs/design/ui.md §4.4): Smaller, Default and Larger in Settings scale every type
// role together on this device, are kept across a reload and are on the page before its first
// paint. Text fields stay 16 px, so Safari doesn't zoom on focus.
import { test, expect } from '@playwright/test';
import { go } from './nav.mjs';

async function openDemo(page) {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
}

const sizes = (page) => page.evaluate(() => {
  const px = (sel) => getComputedStyle(document.querySelector(sel)).fontSize;
  return { title: px('#sessions-h'), row: px('.session .row-title'), search: px('.search-input') };
});

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test.use({ viewport: { width: 1180, height: 820 } });

test('Smaller and Larger scale the type, are kept across a reload, and leave text fields at 16 px', async ({ page }) => {
  await openDemo(page);
  const before = await sizes(page);
  expect(before).toEqual({ title: '20px', row: '14px', search: expect.any(String) });

  await go(page, 'settings');
  const group = page.getByRole('group', { name: 'Text size' });
  await expect(group.getByRole('button')).toHaveText(['Smaller', 'Default', 'Larger']);
  await expect(group.getByRole('button', { name: 'Default' })).toHaveAttribute('aria-pressed', 'true');

  await group.getByRole('button', { name: 'Smaller' }).click();
  await expect(group.getByRole('button', { name: 'Smaller' })).toHaveAttribute('aria-pressed', 'true');
  await expect(group.getByRole('button', { name: 'Default' })).toHaveAttribute('aria-pressed', 'false');
  expect(await page.evaluate(() => document.documentElement.dataset.textSize)).toBe('smaller');

  // Kept, and on the page before main.js runs.
  await page.route('**/js/main.js', (r) => r.abort());
  await page.reload();
  expect(await page.evaluate(() => document.documentElement.dataset.textSize)).toBe('smaller');
  await page.unroute('**/js/main.js');
  await openDemo(page);
  const smaller = await sizes(page);
  expect(smaller.title).toBe('18px');
  expect(parseFloat(smaller.row)).toBeCloseTo(12.6, 1);
  expect(smaller.search).toBe(before.search);

  await go(page, 'settings');
  await page.getByRole('button', { name: 'Larger' }).click();
  await go(page, 'sessions');
  const larger = await sizes(page);
  expect(larger.title).toBe('23px');
  expect(parseFloat(larger.row)).toBeCloseTo(16.1, 1);
  expect(larger.search).toBe(before.search);
  for (const sel of ['.search-input']) {
    expect(await page.locator(sel).evaluate((e) => getComputedStyle(e).fontSize)).toBe(before.search);
  }

  // Default takes the attribute away, so the page is as it was.
  await go(page, 'settings');
  await page.getByRole('button', { name: 'Default' }).click();
  expect(await page.evaluate(() => document.documentElement.hasAttribute('data-text-size'))).toBe(false);
  expect(await page.evaluate(() => localStorage.getItem('agent-bridge.app.textSize'))).toBeNull();
});

test('no role goes under 11 px at Smaller', async ({ page }) => {
  await page.addInitScript(() => localStorage.setItem('agent-bridge.app.textSize', 'smaller'));
  await openDemo(page);
  const roles = await page.evaluate(() => {
    const s = getComputedStyle(document.documentElement);
    return ['title', 'row', 'reading', 'meta', 'section', 'code'].map((r) => {
      const probe = document.createElement('span');
      probe.style.fontSize = `var(--type-${r})`;
      document.body.append(probe);
      const px = parseFloat(getComputedStyle(probe).fontSize);
      probe.remove();
      return [r, px, s.getPropertyValue('--text-scale').trim()];
    });
  });
  for (const [r, px, scale] of roles) {
    expect(scale, r).toBe('0.9');
    expect(px, r).toBeGreaterThanOrEqual(11);
  }
  // Rules sized in rem scale with the root too, so they keep the floor themselves (.badge, #1040).
  const under = await page.evaluate(() => {
    const out = [];
    const walk = (rules) => {
      for (const rule of rules) {
        if (rule.cssRules) walk(rule.cssRules);
        const size = rule.style?.fontSize;
        if (!size || !size.includes('rem')) continue;
        const probe = document.createElement('span');
        probe.style.fontSize = size;
        document.body.append(probe);
        const px = parseFloat(getComputedStyle(probe).fontSize);
        probe.remove();
        if (px < 11) out.push(`${rule.selectorText}: ${size} = ${px}px`);
      }
    };
    for (const sheet of document.styleSheets) walk(sheet.cssRules);
    return out;
  });
  expect(under).toEqual([]);
});

test.describe('a narrow phone', () => {
  test.use({ viewport: { width: 320, height: 700 } });
  for (const size of [null, 'larger']) {
    test(`keeps "Sessions" whole and Sort before the icons in reading order (${size ?? 'default'})`, async ({ page }) => {
      if (size) await page.addInitScript((s) => localStorage.setItem('agent-bridge.app.textSize', s), size);
      await openDemo(page);
      const box = (sel) => page.locator(`.sessions-head ${sel}`).first().boundingBox();
      const title = await box('.page-title');
      const lineHeight = await page.locator('#sessions-h').evaluate((e) => parseFloat(getComputedStyle(e).lineHeight));
      expect(title.height).toBeLessThanOrEqual(lineHeight + 1);
      const sort = await box('.sort');
      const search = await box('.search-open');
      expect(await page.locator('.sessions-head .sort').evaluate((e) => e.scrollWidth <= e.clientWidth + 1)).toBe(true);
      expect(sort.width).toBeGreaterThanOrEqual(48);
      expect(search.x).toBeGreaterThanOrEqual(sort.x + sort.width - 1);
      expect(title.x + title.width).toBeLessThanOrEqual(sort.x + 1);
    });
  }
});
