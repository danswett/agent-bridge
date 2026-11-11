import { test, expect } from '@playwright/test';

test.describe('wide machine tabs scroll whole (#1894)', () => {
  for (const width of [760, 820, 1180]) {
    for (const hasTouch of [false, true]) {
      test.describe(`${width}px ${hasTouch ? 'touch' : 'pointer'}`, () => {
        test.use({ viewport: { width, height: 900 }, hasTouch });

        test('keeps every tab, fades only overflow edges and reveals the chosen whole name', async ({ page }) => {
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
              { id: 'tab-last', kind: 'host', name: 'Last workstation with a long name' },
            ];
            const draw = (current) => renderMachineTabs(document.getElementById('machine-tabs'), {
              presence, current, me: 'browser', onSelect: draw,
            });
            window.chooseFixtureMachine = draw;
            draw('all');
          });
          const bar = page.locator('#machine-tabs');
          const list = bar.getByRole('tablist');
          const tabs = list.getByRole('tab');
          await expect(tabs).toHaveCount(7);
          await expect(bar.locator('.machine-more')).toHaveCount(0);
          const last = tabs.last();
          await expect(last).toHaveText('Last workstation\u2026');
          await expect(last).toHaveAttribute('title', 'Last workstation with a long name');
          await expect(last).toHaveAccessibleName('Last workstation with a long name');
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
          const chosenFits = () => list.evaluate((el) => {
            const outer = el.getBoundingClientRect();
            const chosen = el.querySelector('[aria-selected="true"]').getBoundingClientRect();
            return chosen.left >= outer.left - 1 && chosen.right <= outer.right + 1;
          });
          await expect.poll(chosenFits).toBe(true);
          await expect.poll(() => fade('before')).toBe(true);
          await expect.poll(() => fade('after')).toBe(false);
          await page.setViewportSize({ width: width === 1180 ? 760 : 1180, height: 900 });
          await expect.poll(chosenFits).toBe(true);
          await page.keyboard.press('Home');
          await expect(tabs.first()).toHaveAttribute('aria-selected', 'true');
          await expect.poll(chosenFits).toBe(true);
          await expect.poll(() => fade('before')).toBe(false);
          await page.keyboard.press('ArrowRight');
          await page.keyboard.press('ArrowRight');
          await expect(tabs.nth(2)).toHaveAttribute('aria-selected', 'true');
          await expect.poll(chosenFits).toBe(true);
          await list.evaluate((el) => new Promise((resolve) => {
            const resized = new ResizeObserver(() => { resized.disconnect(); resolve(); });
            resized.observe(el);
            el.closest('.sessions').style.setProperty('--list-w', '400px');
          }));
          await expect.poll(chosenFits).toBe(true);
          await page.keyboard.press('Home');
          await page.evaluate(() => window.chooseFixtureMachine('tab-middle'));
          await expect(tabs.nth(2)).toHaveAttribute('aria-selected', 'true');
          await expect.poll(chosenFits).toBe(true);
          expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
        });
      });
    }
  }
});
