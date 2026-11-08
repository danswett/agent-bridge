// Settings > Keyboard check (#1209): each keydown listed with key, code, modifiers, focus and
// whether the app used it, on the practice targets and on a real session row. In demo mode.
import { test, expect } from '@playwright/test';
import { go } from './nav.mjs';

async function openSettings(page) {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto('?demo=1&static=1#/settings');
  await page.waitForSelector('html[data-demo-ready]');
}

const entries = (page) => page.locator('#key-check-list li');
// The entry for one key, whatever else (a modifier's own keydown) came beside it.
const entryFor = (page, key) => entries(page).filter({ hasText: `key "${key}"` });

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test('the keyboard check lists Option+Up on the practice button and Option+Down on a session row, and whether the app used each', async ({ page }) => {
  await openSettings(page);
  const section = page.locator('#key-check');
  await expect(section.locator('#key-check-mode')).toHaveText(/^Open in the browser on .+ \(display-mode: browser/);
  await expect(section.locator('.key-check-button')).toBeHidden();
  await expect(entries(page)).toHaveCount(0);

  await section.getByRole('button', { name: 'Start keyboard check' }).click();
  await expect(section.getByRole('button', { name: 'Stop keyboard check' })).toBeVisible();
  await expect(section.getByRole('status')).toHaveText('Keyboard check on. Keys you press anywhere in the app are listed here.');

  await section.locator('.key-check-button').focus();
  await page.keyboard.press('Alt+ArrowUp');
  await expect(entryFor(page, 'ArrowUp')).toHaveText(
    /^\d+\. key "ArrowUp", code "ArrowUp" \u00b7 Option \(altKey\) yes \u00b7 Control \(ctrlKey\) no \u00b7 Command \(metaKey\) no \u00b7 Shift \(shiftKey\) no \u00b7 focus on the practice button \(button\.button\.secondary\.key-check-button\) \u00b7 used by the app: no$/);

  // On a real row, on the Sessions page: the row's own handler takes Alt+Arrow.
  await go(page, 'sessions');
  const row = page.locator('.session > .row-main').first();
  await row.focus();
  await expect(row).toBeFocused();
  await page.keyboard.press('Alt+ArrowDown');
  await go(page, 'settings');
  await expect(entryFor(page, 'ArrowDown')).toHaveText(/ \u00b7 Option \(altKey\) yes .* focus on a session row \(button\.row-main#[^)]+\) \u00b7 used by the app: yes$/);
  // Never a session's title: the row is named by what it is.
  const title = (await row.textContent()).trim().slice(0, 20);
  expect(await entries(page).allTextContents()).not.toContainEqual(expect.stringContaining(title));

  // Off: nothing more is listed.
  await section.getByRole('button', { name: 'Stop keyboard check' }).click();
  await expect(section.getByRole('button', { name: 'Start keyboard check' })).toBeVisible();
  const listed = await entries(page).count();
  await section.locator('#key-check-toggle').press('ArrowRight');
  await expect(entryFor(page, 'ArrowRight')).toHaveCount(0);
  expect(await entries(page).count()).toBe(listed);

  await section.getByRole('button', { name: 'Clear' }).click();
  await expect(entries(page)).toHaveCount(0);
  await expect(section.getByRole('button', { name: 'Copy results' })).toBeHidden();
});

test('a typing key in a text box during the check is listed without the key or its code', async ({ page }) => {
  await openSettings(page);
  await page.locator('#key-check-toggle').click();
  await go(page, 'sessions');
  const search = page.locator('#search');
  if (!await search.isVisible()) { await page.locator('.search-open').click(); }
  await search.focus();
  await page.keyboard.press('q');
  await go(page, 'settings');
  await expect(entries(page).first()).toHaveText(/^1\. key a typing key \(not shown\), code \(not shown\) .* focus on a text box \(input[^)]*#search\)/);
  expect(await entries(page).allTextContents()).not.toContainEqual(expect.stringContaining('KeyQ'));
});
