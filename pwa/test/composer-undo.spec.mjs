import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

async function composer(page) {
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await open(page, IDLE);
  return view(page, IDLE).getByRole('textbox', { name: 'Reply' });
}

for (const event of ['visibilitychange', 'pagehide']) {
  test(`${event} blurs the composer without losing its draft or refocusing on return`, async ({ page }) => {
    const input = await composer(page);
    await input.fill('unfinished draft');
    await page.evaluate((event) => {
      Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
      document.dispatchEvent(new Event('visibilitychange'));
    }, event);
    await expect(input).toBeFocused();
    await page.evaluate((event) => {
      if (event === 'visibilitychange') {
        Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' });
        document.dispatchEvent(new Event(event));
      } else {
        window.dispatchEvent(new PageTransitionEvent(event, { persisted: true }));
      }
    }, event);
    await expect(input).not.toBeFocused();
    await expect(input).toHaveValue('unfinished draft');
    await page.evaluate(() => {
      Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'visible' });
      document.dispatchEvent(new Event('visibilitychange'));
      window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true }));
    });
    await expect(input).not.toBeFocused();
  });
}

test('a delivered keyboard send leaves an empty unfocused composer with no sent text to undo', async ({ page }) => {
  const input = await composer(page);
  await input.pressSequentially('send this once');
  await input.press('Control+Enter');
  await expect(view(page, IDLE).locator('form.reply')).toHaveAttribute('data-reply', 'delivered');
  await expect(input).toHaveValue('');
  await expect(input).not.toBeFocused();
  await input.focus();
  await input.press('Control+z');
  await expect(input).toHaveValue('');
});

test('deleting the draft clears its undo history without dismissing the composer', async ({ page }) => {
  const input = await composer(page);
  await input.pressSequentially('discard this draft');
  await input.press('Control+a');
  await input.press('Backspace');
  await expect(input).toHaveValue('');
  await expect(input).toBeFocused();
  await input.press('Control+z');
  await expect(input).toHaveValue('');
});
