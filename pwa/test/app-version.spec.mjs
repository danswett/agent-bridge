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
      await expect(checking).toHaveAttribute('aria-disabled', 'true');
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
    expect(await page.evaluate(() => window.releaseTest.updates)).toBe(5);
    await expect(section.locator('#release-id')).toHaveText('64a4019');
    const { violations } = await new AxeBuilder({ page }).include('#release').analyze();
    expect(violations).toEqual([]);
  });

  for (const phase of ['installing', 'ready']) {
    for (const moved of [false, true]) {
      test(`Check keeps keyboard focus and ${moved ? 'leaves moved focus alone' : 'hands it off'} on ${phase} ${name}`, async ({ page }) => {
        await page.setViewportSize(viewport);
        await fakeRegistration(page);
        let finish;
        let requests = 0;
        await page.route('**/version.json', async (route) => {
          requests++;
          await new Promise((resolve) => { finish = resolve; });
          await route.fulfill({ json: { version: 'b'.repeat(40), builtAt: '' } });
        });
        await page.goto('?demo=1&static=1');
        await page.waitForSelector('html[data-demo-ready]');
        await go(page, 'settings');
        const section = page.getByRole('region', { name: 'This app' });
        const check = section.locator('#release-check');
        const copy = section.getByRole('button', { name: 'Copy app version' });
        const result = section.locator('#release-result');
        await check.focus();
        await page.keyboard.press('Enter');
        await expect.poll(() => !!finish).toBe(true);
        await expect(check).toBeFocused();
        expect(await check.evaluate((button) => button.disabled)).toBe(false);
        await expect(check).toHaveAttribute('aria-disabled', 'true');
        await expect(check).toHaveAttribute('aria-busy', 'true');
        await page.keyboard.press('Enter');
        await page.keyboard.press('Space');
        expect(requests).toBe(1);
        if (moved) { await page.keyboard.press('Shift+Tab'); await expect(copy).toBeFocused(); }
        await page.evaluate((phase) => {
          window.releaseTest.install();
          if (phase === 'ready') { window.releaseTest.worker.to('activated'); }
        }, phase);
        finish();
        await expect(check).toBeHidden();
        const target = moved ? copy : phase === 'ready' ? section.getByRole('button', { name: 'Reload', exact: true }) : result;
        await expect(target).toBeFocused();
        await page.evaluate(() => window.releaseTest.worker.to('activated'));
        await expect(result).toHaveText('A new version of the app is ready.');
        await expect(target).toBeFocused();
      });
    }
  }

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

  test(`a first installation is not offered as an update ${name}`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await fakeRegistration(page, false);
    await page.route('**/version.json', (route) => route.fulfill({ json: {
      version: '64a4019c0ffee0123456789abcdef0123456789a', builtAt: '',
    } }));
    await page.goto('?demo=1&static=1');
    await page.waitForSelector('html[data-demo-ready]');
    await go(page, 'settings');
    await page.evaluate(() => {
      window.releaseTest.worker.to('activating');
      navigator.serviceWorker.controller = window.releaseTest.worker;
      navigator.serviceWorker.dispatchEvent(new Event('controllerchange'));
    });
    const section = page.getByRole('region', { name: 'This app' });
    await expect(section.getByRole('button', { name: 'Reload', exact: true })).toBeHidden();
    await section.getByRole('button', { name: 'Check for updates' }).click();
    await expect(section.locator('#release-result')).toHaveText('Up to date');
    expect(await page.evaluate(() => window.releaseTest.updates)).toBe(2);
    await page.evaluate(() => window.releaseTest.worker.to('activated'));
    await expect(section.getByRole('button', { name: 'Reload', exact: true })).toBeHidden();
    await page.evaluate(() => { window.releaseTest.install(); window.releaseTest.worker.to('installed'); });
    await expect(section.getByRole('button', { name: 'Reload', exact: true })).toBeHidden();
    await page.evaluate(() => window.releaseTest.worker.to('activated'));
    await expect(section.locator('#release-result')).toHaveText('A new version of the app is ready.');
  });
  test(`launch and foreground checks find a stalled version with Reload ${name}`, async ({ page }) => {
    await page.setViewportSize(viewport);
    const start = new Date('2026-11-11T08:00:00-08:00');
    await page.clock.install({ time: start });
    await fakeRegistration(page, true, true);
    let requests = 0;
    await page.route('**/version.json', (route) => {
      requests++;
      return route.fulfill({ json: { version: 'b'.repeat(40), builtAt: '' } });
    });
    await page.goto('?demo=1&static=1');
    await page.waitForSelector('html[data-demo-ready]');
    await go(page, 'settings');
    const section = page.getByRole('region', { name: 'This app' });
    await expect(section.locator('#release-newer')).toBeVisible();
    expect(requests).toBe(1);
    await page.clock.pauseAt(new Date(start.getTime() + 120000));
    const foreground = () => page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
    await foreground();
    await expect.poll(() => requests).toBe(2);
    await foreground();
    expect(requests).toBe(2);
    await page.clock.runFor(4999);
    await foreground();
    expect(requests).toBe(2);
    await page.clock.runFor(1);
    await foreground();
    await expect.poll(() => requests).toBe(3);
    await page.evaluate(() => { window.releaseTest.install(); window.releaseTest.worker.to('installed'); });
    await page.clock.runFor(9999);
    await expect(section.locator('#release-result')).toHaveText('Installing the new version…');
    await page.clock.runFor(1);
    await expect(section.locator('#release-result')).toHaveText("The new version installed but hasn't started.");
    await expect(section.getByRole('button', { name: 'Check for updates' })).toBeHidden();
    const reload = section.getByRole('button', { name: 'Reload', exact: true });
    await expect(reload).toBeVisible();
    await expect(section.locator('#release-id')).toHaveText('64a4019');
    await Promise.all([page.waitForEvent('load'), reload.click()]);
  });

  test(`failed installs use the banner sentence in This app ${name}`, async ({ page }) => {
    await page.setViewportSize(viewport);
    await fakeRegistration(page);
    await page.goto('?demo=1&static=1');
    await page.waitForSelector('html[data-demo-ready]');
    await go(page, 'settings');
    await page.evaluate(() => {
      for (let i = 0; i < 3; i++) { window.releaseTest.install(); window.releaseTest.worker.to('redundant'); }
    });
    await expect(page.locator('#stuck-banner')).toBeVisible();
    await expect(page.locator('#release-result')).toHaveText(await page.locator('#stuck-banner span').textContent());
    await expect(page.locator('#release-check')).toBeVisible();
  });
}

async function fakeRegistration(page, controlled = true, automatic = false) {
  await page.addInitScript(({ controlled, automatic }) => {
    // Existing manual-check cases start with a quiet, current cold-start result.
    const fetch = window.fetch.bind(window);
    let cold = !automatic;
    window.fetch = (...args) => {
      if (cold && args[0] === 'version.json') {
        cold = false;
        return Promise.resolve(new Response(JSON.stringify({ version: '64a4019c0ffee0123456789abcdef0123456789a', builtAt: '' })));
      }
      return fetch(...args);
    };
    const registration = new EventTarget();
    registration.update = async () => { window.releaseTest.updates++; };
    const serviceWorker = new EventTarget();
    serviceWorker.controller = controlled ? { state: 'activated', scriptURL: new URL('sw.js', location.href).href } : null;
    serviceWorker.register = async () => registration;
    Object.defineProperty(navigator, 'serviceWorker', { value: serviceWorker });
    window.releaseTest = {
      updates: 0,
      install() {
        const worker = new EventTarget();
        worker.scriptURL = new URL('sw.js', location.href).href;
        worker.state = 'installing';
        worker.to = (state) => {
          worker.state = state;
          if (state === 'installed') { registration.waiting = worker; registration.installing = null; }
          worker.dispatchEvent(new Event('statechange'));
        };
        this.worker = registration.installing = worker;
        registration.dispatchEvent(new Event('updatefound'));
      },
    };
    if (!controlled) { window.releaseTest.install(); }
  }, { controlled, automatic });
}
