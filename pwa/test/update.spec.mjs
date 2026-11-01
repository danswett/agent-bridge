// A machine's updates in the machines list, in demo mode (#51): what its app found, Install
// (which its app carries out once no session there is busy, and the outcome coming back), a
// press the machine refuses, Check all, and none at all on a read-only machine.
import { test, expect } from '@playwright/test';

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1&updates=1&updateStep=300${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

const updateOf = (page) => page.locator('#machine-list [data-device-id="demo"] .machine-update');
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test('a release the machine found is shown, and Install downloads it, installs it and says it updated', async ({ page }) => {
  await openDemo(page);
  const block = updateOf(page);
  await expect(block.locator('.machine-update-line')).toHaveText('0.2.0 is available (running 0.1.0).');

  await block.getByRole('button', { name: 'Install 0.2.0 on Demo machine when no session there is busy' }).click();

  await expect(block.locator('.machine-update-line')).toHaveText(/^(Downloading 0\.2\.0 \(50%\)|Installing 0\.2\.0: its app and host restart)\u2026$/);
  await expect(block.locator('.machine-update-line')).toHaveText(/^Up to date: 0\.2\.0, checked /);
  await expect(block.locator('.machine-update-last')).toHaveText('Updated from 0.1.0 to 0.2.0 just now.');
  // Nothing newer: Install goes, Check stays.
  await expect(block.locator('.machine-update-install')).toHaveCount(0);
  await expect(block.locator('.machine-update-check')).toBeVisible();
  const log = await page.evaluate(() => window.agentBridgeDemo.updateLog);
  expect(log).toHaveLength(1);
  expect(log[0].op).toBe('update.install');
  expect(log[0].request).toMatch(UUID);
});

test("a press the machine refuses says why on the machine's row, and Check all asks it to check", async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => window.agentBridgeDemo.scriptUpdates("only a person's phone, browser or Windows app can ask a machine to update"));
  const block = updateOf(page);

  await block.locator('.machine-update-install').click();

  await expect(block.getByRole('alert')).toHaveText("Only a person's phone, browser or Windows app can ask a machine to update.");
  await expect(block.locator('.machine-update-line')).toHaveText('0.2.0 is available (running 0.1.0).');

  await page.locator('#machine-list .machine-check-all').click();
  await expect(block.getByRole('alert')).toHaveCount(0);
  await expect.poll(() => page.evaluate(() => window.agentBridgeDemo.updateLog.map((e) => e.op))).toEqual(['update.install', 'update.check']);
});

test("a machine's app that is not running is said, with nothing to press", async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => window.agentBridgeDemo.setUpdate({ current: '0.1.0', installer: false, unavailable: 'the Agent Bridge app is not running on this machine, and it is what installs updates', phase: 'idle' }));
  const block = updateOf(page);

  await expect(block.locator('.machine-update-line')).toHaveText('The Agent Bridge app is not running on this machine, and it is what installs updates.');
  await expect(block.getByRole('button')).toHaveCount(0);
});

test('a read-only machine offers no updates', async ({ page }) => {
  await openDemo(page, '&ahb=1');
  await expect(page.locator('#machine-list [data-device-id="demo"]')).toBeVisible();
  await expect(page.locator('#machine-list .machine-update')).toHaveCount(0);
  await expect(page.locator('#machine-list .machine-check-all')).toHaveCount(0);
});
