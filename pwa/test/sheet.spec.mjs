import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

async function open(page, options = {}) {
  await page.evaluate(async (options) => {
    const { openSheet } = await import('./js/ui/sheet.js');
    const { h } = await import('./js/ui/dom.js');
    const anchor = document.querySelector('#session-sort');
    anchor.focus();
    window.sheetAnchor = anchor;
    window.sheet = openSheet({
      title: 'Move to group', ...options, anchor,
      content: h('div', {}, Array.from({ length: 30 }, (_, i) => h('div', {},
        h('button', { type: 'button', class: 'button secondary' }, `Group ${i + 1}`)))),
      footer: h('button', { type: 'button', class: 'button' }, 'New group'),
      onClose: () => { window.sheetClosed = (window.sheetClosed ?? 0) + 1; },
    });
  }, options);
  return page.getByRole(options.kind === 'alert' ? 'alertdialog' : 'dialog');
}

for (const [layout, viewport] of [
  ['phone', { width: 390, height: 664 }],
  ['iPad', { width: 820, height: 1180 }],
]) {
  test.describe(layout, () => {
    test.use({ viewport, hasTouch: true });
    test.beforeEach(async ({ page }) => {
      await page.addInitScript(() => {
        window.csp = [];
        document.addEventListener('securitypolicyviolation', (e) => window.csp.push(e.violatedDirective));
      });
      await page.goto('?demo=1&static=1');
      await page.waitForSelector('html[data-demo-ready]');
    });
    test.afterEach(async ({ page }) => {
      expect(await page.evaluate(() => window.csp)).toEqual([]);
    });

    test('long content scrolls within the cap while Cancel and footer stay visible', async ({ page }) => {
      await page.evaluate(() => {
        const root = document.documentElement;
        root.dataset.platform = 'ios';
        root.dataset.standalone = 'true';
        root.style.setProperty('--env-safe-top', '47px');
        root.style.setProperty('--env-safe-bottom', '34px');
      });
      const dialog = await open(page);
      await expect(dialog).toBeVisible();
      const box = await dialog.boundingBox();
      expect(box.height).toBeLessThanOrEqual(Math.min(viewport.height * .7, viewport.height - 47 - 96) + 1);
      expect(box.x).toBeGreaterThanOrEqual(0);
      expect(box.y).toBeGreaterThanOrEqual(47);
      expect(box.x + box.width).toBeLessThanOrEqual(viewport.width);
      if (layout === 'iPad') {
        expect(box.width).toBe(340);
        const anchor = await page.locator('#session-sort').boundingBox();
        expect(box.x).toBeGreaterThanOrEqual(anchor.x + anchor.width);
      }
      expect(await dialog.evaluate((el) => parseFloat(getComputedStyle(el).paddingBottom))).toBe(50);
      const body = dialog.locator('.sg-sheet-body');
      expect(await body.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);
      await body.evaluate((el) => { el.scrollTop = el.scrollHeight; });
      await expect(dialog.getByRole('button', { name: 'Cancel' })).toBeInViewport();
      await expect(dialog.getByRole('button', { name: 'New group' })).toBeInViewport();
      await expect(dialog.getByRole('button', { name: 'Group 30', exact: true })).toBeInViewport();
    });

    test('focus wraps, background stays inert and Escape or Cancel restores the opener once', async ({ page }) => {
      const dialog = await open(page);
      const cancel = dialog.getByRole('button', { name: 'Cancel' });
      const last = dialog.getByRole('button', { name: 'New group' });
      await expect(cancel).toBeFocused();
      await page.keyboard.press('Shift+Tab');
      await expect(last).toBeFocused();
      await page.keyboard.press('Tab');
      await expect(cancel).toBeFocused();
      await page.evaluate(() => window.sheetAnchor.focus());
      await expect(cancel).toBeFocused();
      await page.keyboard.press('Escape');
      await expect(dialog).toHaveCount(0);
      expect(await page.evaluate(() => document.activeElement === window.sheetAnchor)).toBe(true);
      await open(page);
      await page.getByRole('button', { name: 'Cancel', exact: true }).click();
      expect(await page.evaluate(() => window.sheetClosed)).toBe(2);
      expect(await page.evaluate(() => document.activeElement === window.sheetAnchor)).toBe(true);
    });

    test('form, menu and alert choose their layout and an alert can focus its safe action', async ({ page }) => {
      for (const [kind, width] of [['form', 440], ['menu', 240], ['alert', 300]]) {
        const dialog = await open(page, { kind });
        const box = await dialog.boundingBox();
        if (layout === 'iPad' || kind === 'alert') {
          expect(box.width).toBe(width);
          if (kind !== 'menu') {
            expect(box.x + box.width / 2).toBeCloseTo(viewport.width / 2, 0);
            expect(box.y + box.height / 2).toBeCloseTo(viewport.height / 2, 0);
          }
        } else {
          expect(box.width).toBe(viewport.width);
          expect(box.y + box.height).toBeCloseTo(viewport.height, 0);
        }
        await page.evaluate(() => window.sheet.close());
      }
      await page.evaluate(async () => {
        const { openSheet } = await import('./js/ui/sheet.js');
        const keep = document.createElement('button');
        keep.textContent = 'Keep group';
        window.sheet = openSheet({ title: 'Delete group?', kind: 'alert', cancelLabel: null, footer: keep, initialFocus: keep });
      });
      await expect(page.getByRole('button', { name: 'Keep group' })).toBeFocused();
      await page.keyboard.press('Tab');
      await expect(page.getByRole('button', { name: 'Keep group' })).toBeFocused();
    });

    test('resizing keeps the open sheet inside the visible viewport', async ({ page }) => {
      await open(page, { kind: 'form' });
      await page.setViewportSize({ width: viewport.width, height: 360 });
      const box = await page.getByRole('dialog').boundingBox();
      expect(box.y).toBeGreaterThanOrEqual(0);
      expect(box.y + box.height).toBeLessThanOrEqual(360);
      expect(box.height).toBeLessThanOrEqual(252);
    });

    test('a keyboard-only visual viewport change keeps a form above the keyboard', async ({ page }) => {
      // Like iphone-reply-viewport.spec: layout regression, not a real installed-device receipt.
      await page.evaluate(() => {
        const vv = new EventTarget();
        Object.assign(vv, { height: innerHeight, width: innerWidth, offsetTop: 0, offsetLeft: 0, scale: 1 });
        Object.defineProperty(window, 'visualViewport', { value: vv, configurable: true });
      });
      await open(page, { kind: 'form' });
      for (const [height, top] of [[320, 80], [360, 0], [viewport.height, 0]]) {
        await page.evaluate(({ height, top }) => {
          Object.assign(visualViewport, { height, offsetTop: top });
          visualViewport.dispatchEvent(new Event('resize'));
          visualViewport.dispatchEvent(new Event('scroll'));
        }, { height, top });
        const box = await page.getByRole('dialog').boundingBox();
        expect(box.y).toBeGreaterThanOrEqual(top);
        expect(box.y + box.height).toBeLessThanOrEqual(top + height + 1);
        await expect(page.getByRole('button', { name: 'Cancel', exact: true })).toBeInViewport();
      }
    });

    for (const colorScheme of ['light', 'dark']) {
      for (const contrast of ['no-preference', 'more']) {
        test(`scrim and accessibility in ${colorScheme}, contrast ${contrast}`, async ({ page }) => {
          await page.emulateMedia({ colorScheme, contrast, reducedMotion: 'reduce' });
          const dialog = await open(page);
          const opacity = await dialog.evaluate((el) => Number(getComputedStyle(el, '::backdrop').opacity));
          expect(opacity).toBe(contrast === 'more' ? .6 : colorScheme === 'dark' ? .32 : .22);
          const result = await new AxeBuilder({ page }).withTags([
            'wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22a', 'wcag22aa', 'best-practice',
          ]).analyze();
          expect(result.violations).toEqual([]);
          expect(await dialog.getByRole('button', { name: 'Cancel' }).evaluate((el) =>
            el.getBoundingClientRect().height >= 44 && el.getBoundingClientRect().width >= 44)).toBe(true);
        });
      }
    }
  });
}
