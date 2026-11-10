// Settings > This app (#1246 S1): the release id and build date the running app was stamped with
// (tools/build-site.mjs; test/serve.mjs stamps the site it serves), selectable, with a Copy button
// that says "Copied" politely, so the owner and Quality can say which build was tested.
import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { go } from './nav.mjs';

for (const { name, viewport } of [{ name: 'on a phone', viewport: { width: 390, height: 1800 } }, { name: 'on a desktop', viewport: { width: 1440, height: 1800 } }]) {
  test(`Settings shows the release id and build date, and copies them ${name}`,   async ({ page, context, browserName }) => {
      // Only chromium has these permissions to grant; elsewhere the button's own fallback copies.
      if (browserName === 'chromium') { await context.grantPermissions(['clipboard-read', 'clipboard-write']); }    await page.setViewportSize(viewport);
    await page.goto('?demo=1&static=1');
    await page.waitForSelector('html[data-demo-ready]');
    await go(page, 'settings');

    const section = page.getByRole('region', { name: 'This app' });
    await expect(section.getByRole('heading', { name: 'This app' })).toBeVisible();
    await expect(section.locator('#release-id')).toHaveText('64a4019');
    await expect(section.locator('#release-id')).toHaveCSS(browserName === 'webkit' ? '-webkit-user-select' : 'user-select', 'all');
    await expect(section.locator('#release-date')).toHaveText('· built Nov 7, 2026');

    await section.getByRole('button', { name: 'Copy app version' }).click();
    await expect(section.getByRole('status')).toHaveText('Copied');
    await expect(section.getByRole('status')).toHaveAttribute('aria-live', 'polite');
    if (browserName === 'chromium') {
      expect(await page.evaluate(() => navigator.clipboard.readText())).toBe('64a4019 · built Nov 7, 2026');
    }

    const { violations } = await new AxeBuilder({ page }).include('#release').analyze();
    expect(violations).toEqual([]);
  });
}
