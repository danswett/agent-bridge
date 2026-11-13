import { test, expect } from '@playwright/test';
import { open, motionsDone } from './nav.mjs';

async function edges(locator, content = false) {
  return locator.evaluate((el, content) => {
    const box = el.getBoundingClientRect();
    const style = getComputedStyle(el);
    return {
      left: box.left + (content ? parseFloat(style.borderLeftWidth) + parseFloat(style.paddingLeft) : 0),
      right: box.right - (content ? parseFloat(style.borderRightWidth) + parseFloat(style.paddingRight) : 0),
    };
  }, content);
}

async function sameEdges(locator, column, content = false) {
  const actual = await edges(locator, content);
  for (const edge of ['left', 'right']) {
    expect.soft(Math.abs(actual[edge] - column[edge]), `${await locator.getAttribute('class')} ${edge}`).toBeLessThanOrEqual(1);
  }
}

const cases = [390, 900, 1100, 1440, 1920].flatMap((width) =>
  ['default', 'reserved'].map((scrollbars) => ({ width, scrollbars })));
for (const { width, scrollbars } of cases) {
  for (const inspector of ['shown', 'hidden']) {
    test.describe(`${width}px, details ${inspector}, ${scrollbars} scrollbars`, () => {
      test.use({ viewport: { width, height: 900 }, hasTouch: width < 760 });

      test('header, tabs, delegates and reply share the message column edges (#2339)', async ({ page, browserName }) => {
        await page.goto('?demo=1&static=1&delegates=1');
        await page.waitForSelector('html[data-demo-ready]');
        const view = await open(page, 'demo-delegates');
        await expect(view.locator('.reply-box')).toBeVisible();
        await expect(view.locator('.response')).not.toBeEmpty();
        if (width >= 1280) {
          if (inspector === 'hidden') { await view.locator('.inspector-button').click(); }
          await expect(view.locator('.inspector')).toBeVisible({ visible: inspector === 'shown' });
        }
        if (scrollbars === 'reserved') {
          await view.locator('.response').evaluate((el) => { el.textContent = 'A long conversation with a vertical scrollbar. '.repeat(400); });
          await view.locator('.stream-scroll').evaluate((el) => { el.style.scrollbarGutter = 'stable'; });
          expect(await view.locator('.stream-scroll').evaluate((el) => el.scrollHeight - el.clientHeight)).toBeGreaterThan(0);
          const gutter = await view.locator('.stream-scroll').evaluate((el) => el.getBoundingClientRect().width - el.clientWidth);
          if (browserName === 'webkit' && gutter === 0) {
            test.info().annotations.push({ type: 'note', description: 'Headless WebKit uses overlay scrollbars; overflow and all column edges remain checked.' });
          } else {
            expect(gutter, 'the classic scrollbar reserves horizontal space').toBeGreaterThan(0);
          }
        }
        await motionsDone(page);

        const column = await edges(view.locator('.stream'));
        await sameEdges(view.locator('.agent-block'), column);
        await sameEdges(view.locator('.reply-box'), column);
        // ui.md 6.2 keeps a phone's title between Back and More (44px targets + 8px gap).
        const header = width < 760 ? { left: column.left + 36, right: column.right - 36 } : column;
        await sameEdges(view.locator('.session-header'), header, true);
        for (const selector of ['.view-tabs', '.delegate-strip']) {
          const element = view.locator(selector);
          if (await element.isVisible()) { await sameEdges(element, column, true); }
        }
        const box = await view.locator('.reply-box').boundingBox();
        const send = await view.locator('.send').boundingBox();
        expect(send.x).toBeGreaterThanOrEqual(box.x);
        expect(send.x + send.width).toBeLessThanOrEqual(box.x + box.width);
        expect(send.y).toBeGreaterThanOrEqual(box.y);
        expect(send.y + send.height).toBeLessThanOrEqual(box.y + box.height);
        expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(width);
      });
    });
  }
}

test('resizing another tab does not retain the hidden conversation width (#2339)', async ({ page }) => {
  await page.setViewportSize({ width: 1100, height: 900 });
  await page.goto('?demo=1&static=1&delegates=1');
  await page.waitForSelector('html[data-demo-ready]');
  const view = await open(page, 'demo-delegates');
  await view.getByRole('tab', { name: 'Tools', exact: true }).click();
  await page.setViewportSize({ width: 900, height: 900 });
  await motionsDone(page);
  const pane = await edges(page.locator('#session-pane'));
  await sameEdges(view.locator('.session-header'), { left: pane.left + 24, right: pane.right - 24 }, true);
  await view.getByRole('tab', { name: 'Conversation', exact: true }).click();
  await motionsDone(page);
  const column = await edges(view.locator('.stream'));
  await sameEdges(view.locator('.reply-box'), column);
  await sameEdges(view.locator('.session-header'), column, true);
  await sameEdges(view.locator('.view-tabs'), column, true);
});
