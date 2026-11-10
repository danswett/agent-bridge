// The conversation before the latest exchange (#1285 S2a): a session with turns shows the earlier
// ones read-only above its latest exchange, and opens at the latest.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

test('earlier turns are drawn above the latest exchange, which is drawn once', async ({ page }) => {
  await page.goto('?demo=1&static=1&signin=1&name=DEV-VM1#/sessions');
  await page.waitForSelector('html[data-demo-ready]');
  await open(page, 'signin-1');
  const v = view(page, 'signin-1');
  // signin-1 has three turns: two earlier, and the latest the card draws live.
  await expect(v.locator('.history-turn')).toHaveCount(2);
  await expect(v.locator('.history-turn').first()).toContainText('Demo prompt 1');
  await expect(v.getByText('Demo prompt 3')).toHaveCount(0);
  await expect(v.locator('.history-turn button, .history-turn textarea')).toHaveCount(0);
  const scroll = v.locator('.stream-scroll');
  await expect.poll(() => scroll.evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight)).toBeLessThan(4);
});
