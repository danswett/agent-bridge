import { test, expect } from '@playwright/test';

const textLeft = (locator) => locator.evaluate((el) => {
  const range = document.createRange();
  range.selectNodeContents(el);
  return range.getBoundingClientRect().left;
});

for (const hasTouch of [false, true]) {
  test.describe(`shared pane text edges (#1893), ${hasTouch ? 'touch' : 'pointer'}`, () => {
    test.use({ hasTouch });

    for (const width of [760, 820, 1180]) {
      test.describe(`${width} px`, () => {
        test.use({ viewport: { width, height: 900 } });
        test.beforeEach(async ({ page }) => {
          await page.goto('?demo=1&static=1&machines=2');
          await page.waitForSelector('html[data-demo-ready]');
        });

        test('list text shares the 20 px edge while row and card backgrounds stay at 8 px', async ({ page }) => {
          const pane = await page.locator('#list-pane').boundingBox();
          const row = page.locator('#session-list .session').first();
          for (const locator of [
            page.locator('#sessions-h'),
            page.locator('#session-count'),
            page.locator('.machine-tab-name').first(),
          ]) {
            expect.soft(await textLeft(locator)).toBeCloseTo(pane.x + 20, 1);
          }
          expect((await row.locator('.row-glyph .glyph').boundingBox()).x).toBeCloseTo(pane.x + 20, 1);
          expect((await row.locator('.row-main').boundingBox()).x).toBeCloseTo(pane.x + 8, 1);
          expect((await page.locator('#needs-you').boundingBox()).x).toBeCloseTo(pane.x + 8, 1);
        });

        test('the session title, first view tab text and reply box share the 24 px edge', async ({ page }) => {
          await page.locator('#session-list .row-main').first().click();
          const view = page.locator('.session-view:not([hidden])');
          const pane = await page.locator('#session-pane').boundingBox();
          expect.soft(await textLeft(view.locator('.session-title'))).toBeCloseTo(pane.x + 24, 1);
          expect.soft(await textLeft(view.locator('.view-tab').first())).toBeCloseTo(pane.x + 24, 1);
          expect((await view.locator('.reply-box').boundingBox()).x).toBeCloseTo(pane.x + 24, 1);
        });
      });
    }
  });
}
