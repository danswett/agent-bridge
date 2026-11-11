import { test, expect } from '@playwright/test';
import { go, open, view } from './nav.mjs';

test('help opens only by choice, traps focus and Escape returns to either opener', async ({ page }) => {
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  const dialog = page.getByRole('dialog', { name: 'Keyboard shortcuts', exact: true });
  await expect(dialog).toBeHidden();
  const row = page.locator('.session > .row-main').first();
  await row.focus();
  await page.keyboard.press('?');
  await expect(dialog).toBeVisible();
  const close = dialog.getByRole('button', { name: 'Close', exact: true });
  await expect(close).toBeFocused();
  const focusProbes = [];
  for (const [index, key] of ['Tab', 'Tab', 'Shift+Tab', 'Shift+Tab'].entries()) {
    await page.keyboard.press(key);
    const focus = await dialog.evaluate((el, { index, key }) => {
      const active = document.activeElement;
      return {
        index, key, activeElement: { tag: active?.tagName ?? null, id: active?.id ?? null },
        hasFocus: document.hasFocus(), dialogOpen: el.open, contained: el.contains(active),
        pageRoot: active === document.body || active === document.documentElement,
      };
    }, { index: index + 1, key });
    focusProbes.push(focus);
    // Native Tab may visit browser chrome, but must never focus background page content.
    expect(focus.dialogOpen && (focus.contained || (!focus.hasFocus && focus.pageRoot)),
      `Focus stays inside Keyboard shortcuts after ${key}; probes: ${JSON.stringify(focusProbes)}`).toBe(true);
    if (!focus.hasFocus) {
      await page.keyboard.press('Shift+Tab');
      await expect(close).toBeFocused();
      await expect.poll(() => close.evaluate((el) => el === document.activeElement && document.hasFocus())).toBe(true);
    }
  }
  await expect(dialog.locator('dt')).toHaveText(['Move a row', "Open a row's menu", 'Open the selected session', 'Search', 'Close']);
  await expect(dialog.locator('dd').first()).toHaveText('Tab to its Move button, then Space, ↑ or ↓, Space. Or Alt+↑ / Alt+↓ on the row.');
  await expect(dialog.locator('dd').nth(1)).toHaveText('Shift+F10 or the menu key');
  await expect(dialog.locator('dd').nth(2)).toHaveText('Enter');
  await expect(dialog.locator('dd').nth(3)).toHaveText(/^(Ctrl\+K|⌘K)$/);
  await expect(dialog.locator('dd').nth(4)).toHaveText('Esc');
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(row).toBeFocused();

  await go(page, 'settings');
  await expect(dialog).toBeHidden();
  const settings = page.getByRole('button', { name: 'Keyboard shortcuts', exact: true });
  await settings.click();
  await expect(dialog).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(dialog).toBeHidden();
  await expect(settings).toBeFocused();
});

test('question mark types normally in search, a reply and editable content', async ({ page }) => {
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  const dialog = page.getByRole('dialog', { name: 'Keyboard shortcuts', exact: true });
  await page.keyboard.press('Control+k');
  const search = page.locator('#search');
  await search.press('?');
  await expect(search).toHaveValue('?');
  await expect(dialog).toBeHidden();
  await search.press('Escape');
  await open(page, '6f1c2b7e-claude-example');
  const reply = page.locator('.session-view:not([hidden]) textarea').first();
  await reply.press('?');
  await expect(reply).toHaveValue('?');
  await expect(dialog).toBeHidden();
  await page.evaluate(() => {
    const editor = document.createElement('div');
    editor.contentEditable = 'true';
    document.body.append(editor);
    editor.focus();
  });
  await page.keyboard.press('?');
  await expect(dialog).toBeHidden();
});

test('question mark leaves a question form radio and checkbox focused without opening help', async ({ page }) => {
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  const id = '6f1c2b7e-claude-example';
  await page.evaluate((id) => {
    const host = window.agentBridgeDemo.host;
    const at = new Date(Date.now() + 1000).toISOString();
    host.upsert({ ...host.get(id), status: 'waiting', statusSince: at, updatedAt: at, question: {
      kind: 'form', id: 'q-help-inputs', askedAt: at, message: 'Configure the run',
      fields: [
        { name: 'mode', title: 'Mode', type: 'choice', options: [{ value: 'fast', label: 'Fast' }, { value: 'full', label: 'Full' }] },
        { name: 'extras', title: 'Extras', type: 'multi', options: [{ value: 'trace', label: 'Trace' }] },
      ],
    } });
  }, id);
  await open(page, id);
  const question = view(page, id).locator('.question');
  const dialog = page.getByRole('dialog', { name: 'Keyboard shortcuts', exact: true });
  for (const control of [question.getByRole('radio', { name: 'Fast', exact: true }), question.getByRole('checkbox', { name: 'Trace', exact: true })]) {
    await control.focus();
    await control.press('?');
    await expect(control).toBeFocused();
    await expect(control).not.toBeChecked();
    await expect(dialog).toBeHidden();
  }
  expect(await page.evaluate(() => window.agentBridgeDemo.host.answerLog)).toHaveLength(0);
});
