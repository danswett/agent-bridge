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
    await expect(section.locator('#release-status')).toHaveText('Copied');
    await expect(section.locator('#release-status')).toHaveAttribute('aria-live', 'polite');
    if (browserName === 'chromium') {
      expect(await page.evaluate(() => navigator.clipboard.readText())).toBe('64a4019 · built Nov 7, 2026');
    }

    const { violations } = await new AxeBuilder({ page }).include('#release').analyze();
    expect(violations).toEqual([]);
  });

  test(`Settings checks published versions and retries a failed check ${name}`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await fakeRegistration(page);
    let reply;
    let requests = 0;
    await page.route('**/version.json', async (route) => {
      requests++;
      await new Promise((resolve) => { reply = resolve; });
      if (requests === 3) { await route.abort(); return; }
      await route.fulfill({ json: {
        version: requests === 1 ? '64a4019c0ffee0123456789abcdef0123456789a' : 'b'.repeat(40),
        builtAt: '2026-11-08T20:00:00Z',
      } });
    });
    await page.goto('?demo=1&static=1');
    await page.waitForSelector('html[data-demo-ready]');
    await go(page, 'settings');
    const section = page.getByRole('region', { name: 'This app' });
    const check = section.getByRole('button', { name: 'Check for updates', exact: true });
    const result = section.locator('#release-result');
    await expect(section.locator('#release-newer')).toBeHidden();
    expect(requests).toBe(0);
    for (const expected of ['Up to date', '', "Couldn't check for updates.", '']) {
      reply = null;
      await check.click();
      await expect.poll(() => !!reply).toBe(true);
      const checking = section.getByRole('button', { name: 'Checking…', exact: true });
      await expect(checking).toBeDisabled();
      await expect(checking).toHaveAttribute('aria-busy', 'true');
      await checking.evaluate((button) => button.click());
      reply();
      await expect(check).toBeEnabled();
      await expect(check).toHaveAttribute('aria-busy', 'false');
      await expect(result).toHaveText(expected);
      await expect(result).toHaveAttribute('aria-live', 'polite');
      if (requests > 1) {
        await expect(section.locator('#release-newer')).toHaveText('Newer version bbbbbbb · built Nov 8, 2026');
      }
    }
    expect(requests).toBe(4);
    expect(await page.evaluate(() => window.releaseTest.updates)).toBe(3);
    await expect(section.locator('#release-id')).toHaveText('64a4019');
    const { violations } = await new AxeBuilder({ page }).include('#release').analyze();
    expect(violations).toEqual([]);
  });

  test(`Settings follows installation and reloads only when asked ${name}`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await fakeRegistration(page);
    await page.route('**/version.json', (route) => route.fulfill({ json: {
      version: 'b'.repeat(40), builtAt: '2026-11-08T20:00:00Z',
    } }));
    await page.goto('?demo=1&static=1');
    await page.waitForSelector('html[data-demo-ready]');
    await go(page, 'settings');
    const section = page.getByRole('region', { name: 'This app' });
    await section.getByRole('button', { name: 'Check for updates' }).click();
    await expect(section.locator('#release-newer')).toBeVisible();
    await page.evaluate(() => window.releaseTest.install());
    await expect(section.locator('#release-result')).toHaveText('Installing the new version…');
    await expect(section.getByRole('button', { name: 'Check for updates' })).toBeHidden();
    await page.evaluate(() => window.releaseTest.worker.to('installed'));
    await expect(section.getByRole('button', { name: 'Reload', exact: true })).toBeHidden();
    await page.evaluate(() => navigator.serviceWorker.dispatchEvent(new Event('controllerchange')));
    await expect(section.locator('#release-result')).toHaveText('A new version of the app is ready.');
    await section.getByRole('button', { name: 'Copy app version' }).click();
    await expect(section.locator('#release-result')).toHaveText('A new version of the app is ready.');
    await expect(section.locator('#release-id')).toHaveText('64a4019');
    const { violations } = await new AxeBuilder({ page }).include('#release').analyze();
    expect(violations).toEqual([]);
    await Promise.all([
      page.waitForEvent('load'),
      section.getByRole('button', { name: 'Reload', exact: true }).click(),
    ]);
  });
}

async function fakeRegistration(page) {
  await page.addInitScript(() => {
    const registration = new EventTarget();
    registration.update = async () => { window.releaseTest.updates++; };
    const serviceWorker = new EventTarget();
    serviceWorker.controller = { state: 'activated', scriptURL: new URL('sw.js', location.href).href };
    serviceWorker.register = async () => registration;
    Object.defineProperty(navigator, 'serviceWorker', { value: serviceWorker });
    window.releaseTest = {
      updates: 0,
      install() {
        const worker = new EventTarget();
        worker.state = 'installing';
        worker.to = (state) => { worker.state = state; worker.dispatchEvent(new Event('statechange')); };
        this.worker = registration.installing = worker;
        registration.dispatchEvent(new Event('updatefound'));
      },
    };
  });
}
