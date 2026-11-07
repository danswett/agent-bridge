import { mkdir } from 'node:fs/promises';
import path from 'node:path';
import { test, expect } from '@playwright/test';

test.use({ viewport: { width: 1440, height: 1000 } });

async function openFeedbackDialog(page) {
  await page.goto('./');
  await page.evaluate(() => {
    const text = (selector, value) => { document.querySelector(selector).textContent = value; };
    const fill = (selector, values) => {
      document.querySelector(selector).replaceChildren(...values.map((value) => {
        const item = document.createElement('li');
        item.textContent = value;
        return item;
      }));
    };

    text('#feedback-platform', 'Windows 11');
    text('#feedback-app-version', '1.0.0');
    text('#feedback-browser', 'Chromium');
    text('#feedback-protocol-version', '1');
    text('#feedback-hosts-summary', 'Machine versions (3)');
    text('#feedback-events-summary', '24 connection events');
    text('#feedback-error-codes-summary', '2 error codes');
    fill('#feedback-hosts-list', ['Windows 11 · host 1', 'Windows 11 · host 2', 'Windows 11 · host 3']);
    fill('#feedback-events-list', Array.from({ length: 24 }, (_, i) => `Connection event ${i + 1}: reconnect completed`));
    fill('#feedback-error-codes-list', ['relay-message-error', 'transport-error']);
    document.querySelector('#feedback-included').open = true;
    document.querySelector('#feedback-hosts').open = true;
    document.querySelector('#feedback-events').open = true;
    document.querySelector('#feedback-error-codes').open = true;
    document.querySelector('#feedback-dialog').showModal();
  });
}

async function settleAnimations(page) {
  await page.evaluate(async () => {
    await Promise.all(document.getAnimations().map((animation) => animation.finished));
  });
}

test('feedback disclosure stays keyboard accessible and actions remain pinned across color modes', async ({ page }) => {
  const captureDir = process.env.PWA_FEEDBACK_CAPTURE_DIR;
  if (captureDir) { await mkdir(captureDir, { recursive: true }); }

  await openFeedbackDialog(page);
  const included = page.locator('#feedback-included');
  const summary = included.locator(':scope > summary');
  const actions = page.locator('.feedback-actions');
  const dialog = page.locator('#feedback-dialog');

  await page.emulateMedia({ colorScheme: 'light', forcedColors: 'none', reducedMotion: 'no-preference' });
  await included.evaluate((element) => { element.open = false; });
  await settleAnimations(page);
  const closedChevron = await summary.evaluate((element) => getComputedStyle(element, '::after').transform);
  await page.locator('#feedback-note').focus();
  await page.keyboard.press('Tab');
  await expect(summary).toBeFocused();
  await expect(summary).toHaveCSS('outline-style', 'solid');
  await page.keyboard.press('Enter');
  await expect(included).toHaveAttribute('open', '');
  await settleAnimations(page);
  const openChevron = await summary.evaluate((element) => getComputedStyle(element, '::after').transform);
  expect(openChevron).not.toBe(closedChevron);

  const layout = await page.evaluate(() => {
    const surface = document.querySelector('#feedback-dialog');
    const footer = document.querySelector('.feedback-actions');
    const rect = footer.getBoundingClientRect();
    const surfaceRect = surface.getBoundingClientRect();
    return {
      position: getComputedStyle(footer).position,
      bottom: getComputedStyle(footer).bottom,
      scrolls: surface.scrollHeight > surface.clientHeight,
      footerVisible: rect.top >= surfaceRect.top && rect.bottom <= surfaceRect.bottom,
      footerBottomGap: surfaceRect.bottom - rect.bottom,
      top: rect.top,
    };
  });
  expect(layout.position).toBe('sticky');
  expect(layout.bottom).toBe('0px');
  expect(layout.scrolls).toBe(true);
  expect(layout.footerVisible).toBe(true);
  expect(layout.footerBottomGap).toBeLessThanOrEqual(1);
  await page.locator('#feedback-error-codes > summary').focus();
  const focusClearance = await page.evaluate(() => {
    const focused = document.activeElement.getBoundingClientRect();
    const actions = document.querySelector('.feedback-actions').getBoundingClientRect();
    return actions.top - focused.bottom;
  });
  expect(focusClearance).toBeGreaterThanOrEqual(0);
  await page.evaluate(() => { document.activeElement.blur(); });
  await page.evaluate(() => { document.querySelector('#feedback-dialog').scrollTop = 0; });

  if (captureDir) {
    await page.screenshot({ path: path.join(captureDir, 'feedback-1440-light-expanded-sticky.png') });
  }

  await page.evaluate(() => { document.querySelector('#feedback-dialog').scrollTop = 120; });
  await expect.poll(() => actions.boundingBox().then((box) => box.y)).toBeCloseTo(layout.top, 0);

  await page.emulateMedia({ colorScheme: 'dark', forcedColors: 'none', reducedMotion: 'no-preference' });
  await page.evaluate(() => { document.querySelector('#feedback-dialog').scrollTop = 0; });
  if (captureDir) {
    await page.screenshot({ path: path.join(captureDir, 'feedback-1440-dark-expanded-sticky.png') });
  }

  await page.emulateMedia({ colorScheme: 'light', forcedColors: 'active', reducedMotion: 'reduce' });
  const reducedMotion = await summary.evaluate((element) => getComputedStyle(element, '::after').transitionDuration);
  expect(reducedMotion).toBe('0s');
  const forcedColors = await summary.evaluate((element) => getComputedStyle(element, '::after').borderRightColor);
  expect(forcedColors).not.toBe('rgba(0, 0, 0, 0)');
  if (captureDir) {
    await page.screenshot({ path: path.join(captureDir, 'feedback-1440-forced-colors-expanded-sticky.png') });
  }

  await page.emulateMedia({ colorScheme: 'light', forcedColors: 'none', reducedMotion: 'no-preference' });
  await page.evaluate(() => {
    const error = document.querySelector('#feedback-error');
    error.textContent = "Can't reach Slipgates right now. Try again in a moment.";
    error.dataset.kind = 'send';
    error.hidden = false;
    document.querySelector('#feedback-included').open = false;
    document.querySelector('#feedback-send').textContent = 'Retry';
    document.querySelector('#feedback-dialog').scrollTop = 0;
  });
  await settleAnimations(page);
  if (captureDir) {
    await page.screenshot({ path: path.join(captureDir, 'feedback-1440-light-failed-sticky.png') });
  }
});
