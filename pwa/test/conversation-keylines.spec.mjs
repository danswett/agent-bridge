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

const cases = [390, 900, 1100, 1280, 1440, 1920].flatMap((width) =>
  ['default', 'reserved'].map((scrollbars) => ({ width, scrollbars })));
for (const { width, scrollbars } of cases) {
  for (const inspector of ['shown', 'hidden']) {
    test.describe(`${width}px, details ${inspector}, ${scrollbars} scrollbars`, () => {
      test.use({ viewport: { width, height: 900 }, hasTouch: width < 760 });

      test('header, tabs, delegates and reply follow the message column (#2339)', async ({ page, browserName }) => {
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
        const pane = await edges(page.locator('#session-pane'));
        const header = width < 760 ? { left: pane.left + 52, right: pane.right - 52 } : column;
        await sameEdges(view.locator('.session-header'), pane);
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

for (const width of [1440, 1920]) {
  test(`Tools and Details keep the header's reading column at ${width}px (#2339)`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await page.goto('?demo=1&static=1');
    await page.waitForSelector('html[data-demo-ready]');
    const view = await open(page, '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35');
    const sideColumn = await edges(view.locator('.inspector'), true);
    await view.locator('.inspector-button').click();
    await expect(view.locator('.inspector')).toBeHidden();
    await motionsDone(page);
    const column = await edges(view.locator('.stream'));
    await view.getByRole('tab', { name: 'Tools', exact: true }).click();
    await expect(view.locator('.tools-panel')).toBeVisible();
    await motionsDone(page);
    await sameEdges(view.locator('.tools-panel'), column);
    await sameEdges(view.locator('.session-header'), column, true);
    await sameEdges(view.locator('.view-tabs'), column, true);
    await view.locator('.details-button').click();
    await expect(view.locator('.details')).toBeVisible();
    await motionsDone(page);
    await sameEdges(view.locator('.details'), column);
    await sameEdges(view.locator('.session-header'), column, true);
    await sameEdges(view.locator('.view-tabs'), column, true);
    await view.locator('.inspector-button').click();
    await expect(view.locator('.conversation')).toBeVisible();
    await motionsDone(page);
    await sameEdges(view.locator('.details'), sideColumn);
    await sameEdges(view.locator('.tools-panel'), sideColumn);
    await sameEdges(view.locator('.session-header'), await edges(page.locator('#session-pane')));
    await sameEdges(view.locator('.session-header'), await edges(view.locator('.stream')), true);
  });
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

for (const standalone of [false, true]) {
  test.describe(`${standalone ? 'iOS standalone' : 'browser'} move-strip keylines`, () => {
    if (standalone) {
      test.use({
        hasTouch: true,
        userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
      });
    }
    for (const width of [390, 760, 1440]) {
      test(`at ${width}px the move strip and composer follow messages with navigation clearance (#2339)`, async ({ page }) => {
        await page.setViewportSize({ width, height: 900 });
        if (standalone) {
          await page.addInitScript(() => { Object.defineProperty(navigator, 'standalone', { value: true }); });
        }
        await page.goto('?demo=1&static=1');
        await page.waitForSelector('html[data-demo-ready]');
        const id = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
        const view = await open(page, id);
        if (standalone) {
          await page.evaluate(() => {
            document.documentElement.dataset.platform = 'ios';
            document.documentElement.dataset.standalone = 'true';
          });
        }
        await page.evaluate((id) => {
          window.agentBridgeDemo.moves.set(id, {
            moveId: 'keylines', from: 'Laptop', to: 'Studio PC', session: id, steps: [],
          });
        }, id);
        await expect(view.locator('.move-strip')).toContainText('Moving from Laptop to Studio PC');
        await motionsDone(page);
        const pane = await edges(page.locator('#session-pane'));
        const column = await edges(view.locator('.stream'));
        const compactNavigation = width < 760 || (standalone && width === 760);
        const header = compactNavigation ? { left: pane.left + 52, right: pane.right - 52 } : column;
        await sameEdges(view.locator('.session-header'), header, true);
        await sameEdges(view.locator('.move-strip'), column, true);
        await sameEdges(view.locator('.reply-box'), column);
      });
    }
  });
}
