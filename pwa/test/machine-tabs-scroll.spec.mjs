import { test, expect } from '@playwright/test';

const chosenFits = (list) => list.evaluate((el) => {
  const outer = el.getBoundingClientRect();
  const chosen = el.querySelector('[aria-selected="true"]').getBoundingClientRect();
  return chosen.left >= outer.left - 1 && chosen.right <= outer.right + 1;
});
const resizePane = (list, width) => list.evaluate((el, width) => new Promise((resolve) => {
  const resized = new ResizeObserver(() => { resized.disconnect(); resolve(); });
  resized.observe(el);
  el.closest('.sessions').style.setProperty('--list-w', `${width}px`);
}), width);

test.describe('wide machine tabs scroll whole (#1894)', () => {
  for (const width of [760, 820, 1180]) {
    for (const hasTouch of [false, true]) {
      test.describe(`${width}px ${hasTouch ? 'touch' : 'pointer'}`, () => {
        test.use({ viewport: { width, height: 900 }, hasTouch });

        test.beforeEach(async ({ page }) => {
          await page.goto('?demo=1&static=1&machines=3');
          await page.waitForSelector('html[data-demo-ready]');
          await page.evaluate(async () => {
            const { renderMachineTabs } = await import('/js/ui/views.js');
            const presence = [
              { id: 'tab-first', kind: 'host', name: 'SixteenLettersAB' },
              { id: 'tab-middle', kind: 'host', name: 'DESKTOP-ABCDEFGH0' },
              { id: 'tab-extra-1', kind: 'host', name: 'DESKTOP-ABCDEFGH1' },
              { id: 'tab-extra-2', kind: 'host', name: 'DESKTOP-ABCDEFGH2' },
              { id: 'tab-extra-3', kind: 'host', name: 'DESKTOP-ABCDEFGH3' },
              { id: 'tab-last', kind: 'host', name: 'W'.repeat(17) },
            ];
            const draw = (current) => renderMachineTabs(document.getElementById('machine-tabs'), {
              presence, current, me: 'browser', onSelect: draw,
            });
            window.chooseFixtureMachine = draw;
            draw('all');
          });
        });

        test('keeps every tab, fades only overflow edges and reveals the chosen whole name', async ({ page }) => {
          const bar = page.locator('#machine-tabs');
          const list = bar.getByRole('tablist');
          const tabs = list.getByRole('tab');
          await expect(tabs).toHaveCount(7);
          await expect(bar.locator('.machine-more')).toHaveCount(0);
          const last = tabs.last();
          await expect(last).toHaveText(`${'W'.repeat(16)}\u2026`);
          await expect(last).toHaveAttribute('title', 'W'.repeat(17));
          await expect(last).toHaveAccessibleName('W'.repeat(17));
          await expect(tabs.nth(1)).toHaveText('SixteenLettersAB');

          const fade = (edge) => bar.evaluate((el, edge) => {
            const style = getComputedStyle(el, `::${edge}`);
            return Number(style.opacity) > 0 && style.backgroundImage.includes('linear-gradient');
          }, edge);
          await expect.poll(() => fade('before')).toBe(false);
          await expect.poll(() => fade('after')).toBe(true);
          await tabs.first().focus();
          await page.keyboard.press('End');
          await expect(last).toBeFocused();
          await expect(last).toHaveAttribute('aria-selected', 'true');
          await expect.poll(() => chosenFits(list)).toBe(true);
          await expect.poll(() => fade('before')).toBe(true);
          await expect.poll(() => fade('after')).toBe(false);
          await page.setViewportSize({ width: width === 1180 ? 760 : 1180, height: 900 });
          await expect.poll(() => chosenFits(list)).toBe(true);
          await page.keyboard.press('Home');
          await expect(tabs.first()).toHaveAttribute('aria-selected', 'true');
          await expect.poll(() => chosenFits(list)).toBe(true);
          await expect.poll(() => fade('before')).toBe(false);
          await page.keyboard.press('ArrowRight');
          await page.keyboard.press('ArrowRight');
          await expect(tabs.nth(2)).toHaveAttribute('aria-selected', 'true');
          await expect.poll(() => chosenFits(list)).toBe(true);
          await resizePane(list, 400);
          await expect.poll(() => chosenFits(list)).toBe(true);
          await page.keyboard.press('Home');
          await page.evaluate(() => window.chooseFixtureMachine('tab-middle'));
          await expect(tabs.nth(2)).toHaveAttribute('aria-selected', 'true');
          await expect.poll(() => chosenFits(list)).toBe(true);
          expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        });

        test('fits the widest label when its selected pane shrinks to 264 px at Larger', async ({ page }) => {
          const list = page.locator('#machine-tabs [role="tablist"]');
          await resizePane(list, 400);
          await page.evaluate(() => { document.documentElement.dataset.textSize = 'larger'; });
          await list.getByRole('tab').first().focus();
          await page.keyboard.press('End');
          await expect.poll(() => chosenFits(list)).toBe(true);
          await resizePane(list, 264);
          expect((await page.locator('#list-pane').boundingBox()).width).toBe(264);
          await expect.poll(() => chosenFits(list)).toBe(true);
          await page.keyboard.press('Home');
          await page.keyboard.press('End');
          await expect(list.getByRole('tab').last()).toBeFocused();
          await expect.poll(() => chosenFits(list)).toBe(true);
          await expect(list.getByRole('tab').last()).toHaveAccessibleName('W'.repeat(17));
        });

        test('keeps the complete keyboard focus ring inside the scrollport', async ({ page }, info) => {
          const list = page.locator('#machine-tabs [role="tablist"]');
          await list.getByRole('tab').first().focus();
          for (const key of ['End', 'Home', 'ArrowRight']) {
            await page.keyboard.press(key);
            const ring = await list.getByRole('tab', { selected: true }).evaluate((el) => {
              const style = getComputedStyle(el), tab = el.getBoundingClientRect();
              const clip = el.parentElement.getBoundingClientRect();
              return { style: style.outlineStyle, reach: parseFloat(style.outlineWidth) + parseFloat(style.outlineOffset),
                room: [tab.left - clip.left, clip.right - tab.right, tab.top - clip.top, clip.bottom - tab.bottom] };
            });
            const name = `machine-tab-focus-${key}.png`;
            await info.attach(name, { body: await list.screenshot({ path: info.outputPath(name) }), contentType: 'image/png' });
            expect(ring.style).toBe('solid');
            expect(ring.reach).toBeGreaterThan(0);
            for (const room of ring.room) { expect(room).toBeGreaterThanOrEqual(ring.reach); }
          }
        });
      });
    }
  }
});
