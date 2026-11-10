// #1279 (#1245 S1): a Copy button on the ID row of a session's Details, so a session id can be
// pasted into a bug report without selecting it by hand on a phone.
import { test, expect } from '@playwright/test';
import { open } from './nav.mjs';

const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';

async function openDetails(page, width) {
  await page.setViewportSize({ width, height: 900 });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  const card = await open(page, COPILOT);
  if (width < 600) {
    await card.locator('.more').click();
    await page.locator('.row-menu').getByRole('menuitem', { name: 'Details' }).click();
  }
  await expect(card.locator('.facts .fact-id')).toBeVisible();
  return card;
}

// Records what reaches the clipboard, through execCommand('copy') or the Clipboard API, either of
// which can be made to refuse.
async function stubClipboard(page, { api = 'ok', exec = true } = {}) {
  await page.evaluate(({ api, exec }) => {
    window.__copies = [];
    const clipboard = {
      writeText: (text) => {
        if (api === 'reject') { return Promise.reject(new DOMException('Not allowed', 'NotAllowedError')); }
        window.__copies.push(text);
        return Promise.resolve();
      },
    };
    Object.defineProperty(navigator, 'clipboard', { configurable: true, get: () => clipboard });
    document.execCommand = (command) => {
      const a = document.activeElement;
      if (!exec || command !== 'copy' || !(a instanceof HTMLTextAreaElement)) { return false; }
      window.__copies.push(a.value.slice(a.selectionStart, a.selectionEnd));
      return true;
    };
  }, { api, exec });
}

for (const width of [390, 1440]) {
  test(`at ${width} wide, Copy session ID copies exactly the id and says Copied, then goes quiet`, async ({ page }) => {
    const card = await openDetails(page, width);
    await stubClipboard(page);
    const button = card.locator('.fact-id').getByRole('button', { name: 'Copy session ID' });
    await button.click();
    await expect(card.locator('.fact-id [role="status"]')).toHaveText('Copied');
    expect(await page.evaluate(() => window.__copies)).toEqual([COPILOT]);
    await expect(card.locator('.fact-id [role="status"]')).toHaveText('', { timeout: 8000 });
  });
}

test('when both copy routes refuse, Copy session ID says Couldn\'t copy', async ({ page }) => {
  const card = await openDetails(page, 1440);
  await stubClipboard(page, { api: 'reject', exec: false });
  await card.locator('.fact-id').getByRole('button', { name: 'Copy session ID' }).click();
  await expect(card.locator('.fact-id [role="status"]')).toHaveText("Couldn't copy");
  expect(await page.evaluate(() => window.__copies)).toEqual([]);
});

test('Copy session ID keeps one size and sits right after the id, at every width and after it speaks', async ({ page }) => {
  const sizes = [];
  for (const width of [390, 1440]) {
    const card = await openDetails(page, width);
    await stubClipboard(page);
    const button = card.locator('.fact-id').getByRole('button', { name: 'Copy session ID' });
    const before = await button.boundingBox();
    await button.click();
    await expect(card.locator('.fact-id [role="status"]')).toHaveText('Copied');
    const after = await button.boundingBox();
    expect(after.width).toBe(before.width);
    expect(after.height).toBe(before.height);
    expect(before.width).toBeGreaterThanOrEqual(44);
    expect(before.height).toBeGreaterThanOrEqual(44);
    sizes.push([before.width, before.height]);
  }
  expect(sizes[0]).toEqual(sizes[1]);
});

test('Copy session ID is reached by keyboard, last in Details order, and shows a focus ring', async ({ page }) => {
  const card = await openDetails(page, 1440);
  const button = card.locator('.fact-id').getByRole('button', { name: 'Copy session ID' });
  await button.focus();
  await page.keyboard.press('Shift+Tab');
  await page.keyboard.press('Tab');
  await expect(button).toBeFocused();
  expect(await button.evaluate((b) => getComputedStyle(b).outlineStyle)).not.toBe('none');
  await stubClipboard(page);
  await page.keyboard.press('Enter');
  await expect(card.locator('.fact-id [role="status"]')).toHaveText('Copied');
  // Next in the tab order is End session, which follows the rows.
  await page.keyboard.press('Tab');
  await expect(card.locator('.end-session').getByRole('button')).toBeFocused();
});
