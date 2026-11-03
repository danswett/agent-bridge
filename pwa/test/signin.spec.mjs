import { test, expect } from '@playwright/test';

const pageUrl = 'https://github.com/login/device';
const device = { verificationUri: pageUrl, deviceCode: 'fixture', userCode: 'ABCD', interval: 1, expiresIn: 90 };

test('cancel and restart cannot let a late first request overwrite the next code', async ({ page }) => {
  await page.addInitScript(({ device }) => {
    const original = window.fetch;
    let count = 0;
    window.fetch = (url, options) => {
      if (!String(url).endsWith('/v1/auth/device')) { return original(url, options); }
      if (++count === 1) {
        window.firstSignIn = options.signal;
        return new Promise(resolve => { window.releaseSignIn = () => resolve(Response.json(device)); });
      }
      return Promise.resolve(Response.json({ ...device, userCode: 'WXYZ', interval: 60 }));
    };
  }, { device });
  await page.goto('./');
  await page.locator('#signin-button').click();
  await expect.poll(() => page.evaluate(() => Boolean(window.releaseSignIn))).toBe(true);
  await page.getByRole('button', { name: 'Cancel sign-in' }).click();
  await expect(page.locator('#signin-button')).toBeFocused();
  expect(await page.evaluate(() => window.firstSignIn.aborted)).toBe(true);
  await page.locator('#signin-button').click();
  await expect(page.locator('#code')).toHaveText('WXYZ');
  await page.evaluate(() => window.releaseSignIn());
  await expect(page.locator('#code')).toHaveText('WXYZ');
  await expect(page.locator('#signin-error')).toBeHidden();
  await page.getByRole('button', { name: 'Cancel sign-in' }).click();
  await expect(page.locator('#code-box')).toBeHidden();
  expect(await page.evaluate(() => localStorage.getItem('agent-bridge.app.account'))).toBeNull();
});
