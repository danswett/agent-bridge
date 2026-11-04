import { test, expect } from '@playwright/test';
import { shoot, THEMES, WIDTHS } from './shoot.mjs';
import { open, view } from '../nav.mjs';

// WebKit's Send ring, in flight with its line empty (the quiet window, and the normal path after
// it), on a phone in dark: the engine iOS draws the PWA with.
const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';

test('Send\'s ring while a reply is on its way, WebKit, dark, 390', async ({ page }) => {
  const theme = THEMES.find((t) => t.name === 'dark');
  const width = WIDTHS.find((w) => w.name === '390');
  await page.setViewportSize(width.viewport);
  await page.emulateMedia({ colorScheme: 'dark' });
  await page.goto('?demo=1&static=1&answerDelay=200');
  await page.waitForSelector('html[data-demo-ready]');
  await open(page, IDLE);
  const reply = view(page, IDLE).locator('form.reply');
  await reply.getByRole('textbox', { name: 'Reply' }).fill('Ship it once the tests pass.');
  await page.evaluate(() => window.agentBridgeDemo.host.holdResponses());
  await reply.getByRole('button', { name: 'Send' }).click();
  await expect(reply.getByRole('button', { name: 'Send' })).toHaveAttribute('aria-busy', 'true');
  await shoot(page, 'reply-ring-webkit', theme, width);
});