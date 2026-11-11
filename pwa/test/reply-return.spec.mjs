import { test, expect } from '@playwright/test';
import { open, go, view } from './nav.mjs';

const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const input = (page) => view(page, IDLE).getByRole('textbox', { name: 'Reply' });
const send = (page) => view(page, IDLE).getByRole('button', { name: 'Send', exact: true });
const log = (page) => page.evaluate(() => window.agentBridgeDemo.host.replyLog);

async function start(page) {
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await open(page, IDLE);
}

async function choose(page, name) {
  await go(page, 'settings');
  const group = page.getByRole('group', { name: 'Return key in replies' });
  await group.getByRole('button', { name, exact: true }).click();
  await expect(group.getByRole('button', { name, exact: true })).toHaveAttribute('aria-pressed', 'true');
  await open(page, IDLE);
}

test('default Return makes a new line; Ctrl+Return sends; composition never sends', async ({ page }) => {
  await start(page);
  await input(page).fill('first');
  await input(page).press('Enter');
  await expect(input(page)).toHaveValue('first\n');
  await input(page).dispatchEvent('keydown', { key: 'Enter', ctrlKey: true, isComposing: true });
  expect(await log(page)).toEqual([]);
  await input(page).press('Control+Enter');
  await expect.poll(() => log(page)).toHaveLength(1);
});

for (const [platform, userAgent, modifier, key] of [
  ['windows', 'Mozilla/5.0 (Windows NT 10.0; Win64; x64)', 'Ctrl', 'Enter'],
  ['macos', 'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)', 'Cmd', 'Return'],
]) {
  test(`${platform}: Send applies at once and survives reload; Shift and IME keep text`, async ({ page }) => {
    await page.addInitScript((ua) => {
      Object.defineProperty(navigator, 'userAgentData', { value: undefined });
      Object.defineProperty(navigator, 'userAgent', { value: ua });
    }, userAgent);
    await start(page);
    await expect(send(page)).toHaveAttribute('title', `Send (${modifier}+${key})`);
    await choose(page, 'Send');
    await expect(send(page)).toHaveAttribute('title', `Send (${key})`);
    await expect(send(page)).toHaveAttribute('aria-description', `Send (${key})`);
    await page.reload();
    await page.waitForSelector('html[data-demo-ready]');
    await open(page, IDLE);
    await expect(send(page)).toHaveAttribute('title', `Send (${key})`);
    await input(page).fill('kept');
    await input(page).press('Shift+Enter');
    await expect(input(page)).toHaveValue('kept\n');
    await input(page).dispatchEvent('compositionstart');
    await input(page).dispatchEvent('keydown', { key: 'Enter', ctrlKey: true });
    await input(page).dispatchEvent('compositionend');
    await input(page).dispatchEvent('keydown', { key: 'Enter', keyCode: 229 });
    expect(await log(page)).toEqual([]);
    await input(page).press('Enter');
    await expect.poll(() => log(page)).toHaveLength(1);
    await expect(input(page)).toHaveValue('');
    await input(page).fill('modifier still sends');
    await input(page).press(platform === 'macos' ? 'Meta+Enter' : 'Control+Enter');
    await expect.poll(() => log(page)).toHaveLength(2);
    await choose(page, 'New line');
    await expect(send(page)).toHaveAttribute('title', `Send (${modifier}+${key})`);
  });
}

for (const width of [320, 1024]) {
  test.describe(`touch at ${width}px`, () => {
    test.use({ viewport: { width, height: 820 }, hasTouch: true });
    test('choices fit; phone Return stays a new line while tablet hardware Return sends', async ({ page }) => {
      await start(page);
      await choose(page, 'Send');
      await go(page, 'settings');
      const group = page.getByRole('group', { name: 'Return key in replies' });
      for (const element of await group.locator('button, .note, h4').all()) {
        expect(await element.evaluate((e) => e.scrollWidth <= e.clientWidth + 1)).toBe(true);
      }
      await expect(group.getByText('On a phone the Return key always makes a new line. Use the Send button.')).toBeVisible({ visible: width === 320 });
      await open(page, IDLE);
      await input(page).fill('touch');
      await input(page).press('Enter');
      if (width === 320) {
        await expect(input(page)).toHaveValue('touch\n');
        expect(await log(page)).toEqual([]);
        await expect(send(page)).toBeVisible();
        await send(page).tap();
      }
      await expect.poll(() => log(page)).toHaveLength(1);
    });
  });
}
