// The account's offline machines in the machines list, in demo mode (#50): the relay's roster
// lists one, with when it was last seen, and Forget takes two presses.
import { test, expect } from '@playwright/test';

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

const offline = (page) => page.locator('#machine-list .machine.offline');

test('an offline machine is listed after the online ones with when it was last seen, and forgetting it takes two presses', async ({ page }) => {
  await openDemo(page, '&offline=1');
  const rows = page.locator('#machine-list .machine');
  await expect(rows).toHaveCount(2);
  await expect(rows.nth(0)).not.toHaveClass(/offline/);
  const row = offline(page);
  await expect(row).toHaveAttribute('data-device-name', 'Travel laptop');
  await expect(row.locator('.machine-name')).toHaveText('Travel laptop');
  await expect(row.locator('.machine-seen')).toHaveText(' \u00b7 last seen 3d ago');
  // Nothing to open there: not a button, as an online machine is.
  await expect(row.locator('.machine-button')).toHaveCount(0);
  // The summary counts what is online.
  await page.locator('#machines').getByRole('button', { name: 'Machines' }).click();
  await expect(page.locator('.machines-summary')).toHaveText('Viewing Demo machine \u00b7 1 online');
  await page.locator('#machines').getByRole('button', { name: 'Machines' }).click();

  await row.getByRole('button', { name: 'Forget Travel laptop' }).click();
  const armed = row.getByRole('button', { name: 'Forget Travel laptop: press again to confirm' });
  await expect(armed).toHaveText('Press again to forget');
  await expect(armed).toHaveClass(/armed/);
  await expect(offline(page)).toHaveCount(1);

  await armed.click();

  await expect(offline(page)).toHaveCount(0);
  await expect(rows).toHaveCount(1);
  expect(await page.evaluate(() => window.__cspViolations)).toEqual([]);
});

test('a keyboard forgets with two presses on the same button, which keeps focus while it is redrawn', async ({ page }) => {
  await openDemo(page, '&offline=1');
  await offline(page).locator('.machine-forget').focus();

  await page.keyboard.press('Enter');
  await expect(offline(page).locator('.machine-forget')).toHaveText('Press again to forget');
  await expect(offline(page).locator('.machine-forget')).toBeFocused();
  await page.keyboard.press('Enter');

  await expect(offline(page)).toHaveCount(0);
});

test('an arm lapses after a few seconds, and the next press arms again', async ({ page }) => {
  await openDemo(page, '&offline=1&forgetMs=300');
  const button = offline(page).locator('.machine-forget');
  await button.click();
  await expect(button).toHaveText('Press again to forget');

  await expect(button).toHaveText('Forget');
  await button.click();
  await expect(button).toHaveText('Press again to forget');
  await expect(offline(page)).toHaveCount(1);
});

test('a refused forget says why and keeps the machine', async ({ page }) => {
  await openDemo(page, '&offline=1');
  await page.evaluate(() => window.agentBridgeDemo.scriptForgets('rate-limited'));
  const row = offline(page);
  await row.locator('.machine-forget').click();
  await row.locator('.machine-forget').click();

  await expect(offline(page).locator('.machine-error')).toHaveText('Too many machines forgotten just now. Try again in a minute.');
  await expect(offline(page).locator('.machine-error')).toHaveAttribute('role', 'alert');
  await expect(offline(page).locator('.machine-forget')).toHaveText('Forget');
});

test('a machine that comes back leaves the list and takes its arm with it', async ({ page }) => {
  await openDemo(page, '&offline=1');
  await offline(page).locator('.machine-forget').click();
  await expect(offline(page).locator('.machine-forget')).toHaveText('Press again to forget');

  await page.evaluate(() => window.agentBridgeDemo.reconnectOffline());

  await expect(offline(page)).toHaveCount(0);
});

test('a machine back and gone again between two lists is armed again, not forgotten', async ({ page }) => {
  await openDemo(page, '&offline=1');
  await offline(page).locator('.machine-forget').click();
  await expect(offline(page).locator('.machine-forget')).toHaveText('Press again to forget');

  await page.evaluate(() => window.agentBridgeDemo.bounceOffline());

  await expect(offline(page).locator('.machine-forget')).toHaveText('Forget');
  await expect(offline(page).locator('.machine-seen')).toHaveText(' \u00b7 last seen just now');
});

test('a relay without a roster lists only what is online', async ({ page }) => {
  await openDemo(page);
  await expect(page.locator('#machine-list .machine')).toHaveCount(1);
  await expect(offline(page)).toHaveCount(0);
});
