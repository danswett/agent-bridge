// The panes' dividers from 760 px up (ui.md §5.2, #1030): a drag, Left and Right, a double-click
// or double-tap to reset, the limits, the widths kept on the device, the inspector giving way
// first, and no divider on a phone.
import { test, expect } from '@playwright/test';

const KEY = 'demo:agent-bridge.app.panes';
const list = '#list-divider';
const inspector = '#inspector-divider';
const insp = '.session-view:not([hidden]) .inspector';

test.use({ viewport: { width: 1440, height: 900 }, hasTouch: false });

async function openDemo(page) {
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await page.locator('.session').first().click();
  await expect(page.locator(insp)).toBeVisible();
}

const width = async (page, sel) => (await page.locator(sel).boundingBox()).width;
const stored = (page) => page.evaluate((k) => JSON.parse(localStorage.getItem(k) ?? 'null'), KEY);

async function drag(page, sel, dx) {
  const box = await page.locator(sel).boundingBox();
  const x = box.x + box.width / 2;
  const y = box.y + box.height / 2;
  await page.mouse.move(x, y);
  await page.mouse.down();
  await page.mouse.move(x + dx / 2, y, { steps: 4 });
  await page.mouse.move(x + dx, y, { steps: 4 });
  await page.mouse.up();
}

test('dragging the list divider sets the list pane to where the pointer is, and keeps it', async ({ page }) => {
  await openDemo(page);
  expect(await width(page, '#list-pane')).toBe(320);
  await drag(page, list, 60);
  expect(await width(page, '#list-pane')).toBe(380);
  await expect(page.locator(list)).toHaveAttribute('aria-valuenow', '380');
  expect(await stored(page)).toEqual({ list: 380 });
});

test('a drag the system cancels puts the pane back and saves nothing, even with a frame queued', async ({ page }) => {
  await openDemo(page);
  const box = await page.locator(list).boundingBox();
  const x = box.x + box.width / 2;
  const y = box.y + box.height / 2;
  await page.mouse.move(x, y);
  await page.mouse.down();
  // Move and cancel in one task, so the frame the move queued is still waiting at the cancel.
  await page.evaluate(({ sel, x, y }) => {
    const el = document.querySelector(sel);
    el.dispatchEvent(new PointerEvent('pointermove', { pointerId: 1, clientX: x + 60, clientY: y, bubbles: true }));
    el.dispatchEvent(new PointerEvent('pointercancel', { pointerId: 1, bubbles: true }));
  }, { sel: list, x, y });
  await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))));
  expect(await width(page, '#list-pane')).toBe(320);
  await expect(page.locator(list)).toHaveAttribute('aria-valuenow', '320');
  await page.mouse.up();
  expect(await stored(page)).toBeNull();
  await page.locator(list).focus();
  await page.keyboard.press('ArrowRight');
  expect(await width(page, '#list-pane')).toBe(336);
  expect(await stored(page)).toEqual({ list: 336 });
});

test('dragging the inspector divider left makes the inspector wider', async ({ page }) => {
  await openDemo(page);
  expect(await width(page, insp)).toBe(336);
  await drag(page, inspector, -80);
  expect(await width(page, insp)).toBe(416);
  await expect(page.locator(inspector)).toHaveAttribute('aria-valuenow', '416');
});

test('the dividers are separators in the tab order that name the pane and its limits', async ({ page }) => {
  await openDemo(page);
  await expect(page.locator(list)).toHaveAttribute('role', 'separator');
  await expect(page.locator(list)).toHaveAttribute('aria-orientation', 'vertical');
  await expect(page.locator(list)).toHaveAttribute('aria-label', 'Resize the session list');
  await expect(page.locator(list)).toHaveAttribute('aria-valuemin', '264');
  await expect(page.locator(list)).toHaveAttribute('aria-valuemax', '480');
  await expect(page.locator(inspector)).toHaveAttribute('aria-label', 'Resize the delegate inspector');
  await expect(page.locator(inspector)).toHaveAttribute('aria-valuemin', '280');
  await expect(page.locator(inspector)).toHaveAttribute('aria-valuemax', '560');
  await expect(page.locator(list)).toHaveAttribute('tabindex', '0');
});

test('Left and Right move the divider 16 px, and Home and End go to its limits', async ({ page }) => {
  await openDemo(page);
  await page.locator(list).focus();
  await page.keyboard.press('ArrowRight');
  expect(await width(page, '#list-pane')).toBe(336);
  await page.keyboard.press('ArrowLeft');
  await page.keyboard.press('ArrowLeft');
  expect(await width(page, '#list-pane')).toBe(304);
  await page.keyboard.press('Home');
  expect(await width(page, '#list-pane')).toBe(264);
  await page.keyboard.press('End');
  expect(await width(page, '#list-pane')).toBe(480);
  await page.locator(inspector).focus();
  await page.keyboard.press('ArrowLeft');
  expect(await width(page, insp)).toBe(352);
});

test('a double-click on a divider puts its pane back to the default', async ({ page }) => {
  await openDemo(page);
  await drag(page, list, 100);
  expect(await width(page, '#list-pane')).toBe(420);
  await page.locator(list).dblclick();
  expect(await width(page, '#list-pane')).toBe(320);
  expect(await stored(page)).toBeNull();
});

test('a double-tap on a divider resets it as a double-click does', async ({ browser }) => {
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, hasTouch: true });
  const page = await context.newPage();
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await page.locator(list).focus();
  await page.keyboard.press('End');
  expect(await width(page, '#list-pane')).toBe(480);
  const box = await page.locator(list).boundingBox();
  await page.touchscreen.tap(box.x + box.width / 2, 300);
  await page.touchscreen.tap(box.x + box.width / 2, 300);
  await expect.poll(() => width(page, '#list-pane')).toBe(320);
  await context.close();
});

test('a pane stops at its limits however far the pointer goes', async ({ page }) => {
  await openDemo(page);
  await drag(page, list, -400);
  expect(await width(page, '#list-pane')).toBe(264);
  await drag(page, list, 600);
  expect(await width(page, '#list-pane')).toBe(480);
  await drag(page, inspector, 600);
  expect(await width(page, insp)).toBe(280);
  await drag(page, inspector, -900);
  expect(await width(page, insp)).toBeLessThanOrEqual(560);
});

test('the session pane is never left under 440 px', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 900 });
  await openDemo(page);
  await page.locator(list).focus();
  await page.keyboard.press('End');
  await page.locator(inspector).focus();
  await page.keyboard.press('End');
  expect(await width(page, '#session-pane')).toBeGreaterThanOrEqual(440);
  await page.locator(inspector).focus();
  for (let i = 0; i < 40; i++) { await page.keyboard.press('ArrowLeft'); }
  expect(await width(page, '#session-pane')).toBeGreaterThanOrEqual(440);
});

test('widths kept are there after a reload', async ({ page }) => {
  await openDemo(page);
  await drag(page, list, 50);
  await drag(page, inspector, -64);
  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  await page.locator('.session').first().click();
  expect(await width(page, '#list-pane')).toBe(370);
  expect(await width(page, insp)).toBe(400);
});

test('the inspector gives way first when the window narrows, and gets its width back when it widens', async ({ page }) => {
  await page.setViewportSize({ width: 1800, height: 900 });
  await openDemo(page);
  await page.locator(list).focus();
  await page.keyboard.press('End');
  await page.locator(inspector).focus();
  await page.keyboard.press('End');
  expect(await width(page, insp)).toBe(560);
  await page.setViewportSize({ width: 1280, height: 900 });
  await expect.poll(() => width(page, insp)).toBeLessThan(560);
  expect(await width(page, '#list-pane')).toBe(480);
  await page.setViewportSize({ width: 1800, height: 900 });
  await expect.poll(() => width(page, insp)).toBe(560);
});

test('storage that refuses writes does not stop the dividers working', async ({ page }) => {
  await page.addInitScript(() => {
    const set = Storage.prototype.setItem;
    Storage.prototype.setItem = function (k, v) { if (String(k).includes('panes')) { throw new Error('blocked'); } return set.call(this, k, v); };
  });
  await openDemo(page);
  await drag(page, list, 40);
  expect(await width(page, '#list-pane')).toBe(360);
});

const panes = ['#list-pane', '#session-pane', insp];
const selectable = (page) => page.evaluate((sels) => sels.map((s) => { const c = getComputedStyle(document.querySelector(s)); return c.userSelect || c.webkitUserSelect; }), panes);
const selectedText = (page) => page.evaluate(() => String(window.getSelection()));

for (const [name, divider, dx] of [['list', list, 40], ['inspector', inspector, -40]]) {
  test(`dragging the ${name} divider selects no text in any pane, and clears one made before`, async ({ page }) => {
    await openDemo(page);
    await page.evaluate(() => {
      const range = document.createRange();
      range.selectNodeContents(document.querySelector('#session-pane'));
      getSelection().removeAllRanges();
      getSelection().addRange(range);
    });
    expect(await selectedText(page)).not.toBe('');
    const box = await page.locator(divider).boundingBox();
    const x = box.x + box.width / 2;
    const y = box.y + box.height / 2;
    await page.mouse.move(x, y);
    await page.mouse.down();
    expect(await selectedText(page)).toBe('');
    await page.mouse.move(x + dx, y, { steps: 6 });
    // Over each pane in turn: a pointer sweeping across them is what made a selection flicker.
    for (const sel of panes) {
      const pane = await page.locator(sel).boundingBox();
      await page.mouse.move(pane.x + pane.width / 2, pane.y + 200, { steps: 3 });
      expect(await selectedText(page)).toBe('');
    }
    expect(await selectable(page)).toEqual(['none', 'none', 'none']);
    const started = await page.evaluate(() => {
      const e = new Event('selectstart', { bubbles: true, cancelable: true });
      document.querySelector('#session-pane').dispatchEvent(e);
      return e.defaultPrevented;
    });
    expect(started).toBe(true);
    await page.mouse.up();
    expect(await page.evaluate(() => document.querySelector('#sessions').dataset.paneDragging)).toBeUndefined();
  });
}

test('text can be selected in every pane again once a drag is over, and after one is cancelled', async ({ page }) => {
  await openDemo(page);
  await drag(page, inspector, -40);
  expect((await selectable(page)).every((v) => v !== 'none')).toBe(true);
  const box = await page.locator(list).boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + 100);
  await page.mouse.down();
  await page.evaluate((sel) => document.querySelector(sel).dispatchEvent(new PointerEvent('pointercancel', { pointerId: 1, bubbles: true })), list);
  await page.mouse.up();
  expect((await selectable(page)).every((v) => v !== 'none')).toBe(true);
  const text = await page.evaluate(() => {
    const range = document.createRange();
    range.selectNodeContents(document.querySelector('#session-pane'));
    getSelection().removeAllRanges();
    getSelection().addRange(range);
    return String(getSelection());
  });
  expect(text).not.toBe('');
});

test('a phone has no divider', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await expect(page.locator(list)).toBeHidden();
  await expect(page.locator(inspector)).toBeHidden();
});

test('two panes at 760 px have the list divider, no inspector divider, and a session of at least 440', async ({ page }) => {
  await page.setViewportSize({ width: 760, height: 900 });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await expect(page.locator(list)).toBeVisible();
  await expect(page.locator(inspector)).toBeHidden();
  expect(await width(page, '#session-pane')).toBeGreaterThanOrEqual(440);
  expect(await width(page, '#list-pane')).toBeGreaterThanOrEqual(264);
});

test('the divider is 8 px wide for a fine pointer and 44 px for a coarse one', async ({ page, browser }) => {
  await openDemo(page);
  expect(await width(page, list)).toBe(8);
  const context = await browser.newContext({ viewport: { width: 1440, height: 900 }, hasTouch: true, isMobile: true });
  const touch = await context.newPage();
  await touch.goto('?demo=1&static=1');
  await touch.waitForSelector('html[data-demo-ready]');
  expect(await width(touch, list)).toBe(44);
  await context.close();
});
