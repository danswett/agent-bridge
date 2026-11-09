// A machine's updates in the machines list, in demo mode (#51): what its app found, Install
// (which its app carries out once no session there is busy, and the outcome coming back), a
// press the machine refuses, Check all, and none at all on a read-only machine; and Update now
// (#1159): one press on a quiet machine, its confirmation on a busy one, a confirm outcome, a
// refusal's code, why it can't be pressed, and Restarting to update. On the Machines page.
import { test, expect } from '@playwright/test';

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1&updates=1&updateStep=300${query}#/machines`);
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

// ---- Update now (#1159) ----

const NOW_LABEL = 'Update now: 0.2.0 on Demo machine, without waiting';
const LAND = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const sheetOf = (page) => page.locator('dialog.update-now-sheet');
// What the host's app reports, changed in part.
const setUpdate = (page, change) => page.evaluate((c) => window.agentBridgeDemo.setUpdate({ ...window.agentBridgeDemo.host.update, ...c }), change);
const QUIET = { busy: { working: 0, waiting: 0 } };

test('Update now on a quiet machine goes on one press, asking nothing, and says it updated', async ({ page }) => {
  await openDemo(page, '&updateNow=1');
  await setUpdate(page, QUIET);
  const block = updateOf(page);

  await block.getByRole('button', { name: NOW_LABEL }).click();

  await expect(block.locator('.machine-update-line')).toHaveText(/^(Downloading 0\.2\.0 \u00b7 40%|Restarting to update\u2026)$/);
  await expect(block.locator('.machine-update-last')).toHaveText('Updated to 0.2.0.');
  await expect(sheetOf(page)).toHaveCount(0);
  const log = await page.evaluate(() => window.agentBridgeDemo.updateLog);
  expect(log).toHaveLength(1);
  expect(log[0]).toMatchObject({ op: 'update.install', now: true, release: '0.2.0', accept: null, expires: 120 });
  expect(log[0].request).toMatch(UUID);
});

test('Update now on a busy machine asks first, focus on Cancel; Cancel sends nothing, and Update now sends what it showed', async ({ page }) => {
  await openDemo(page, '&updateNow=1');
  const block = updateOf(page);
  const now = block.getByRole('button', { name: NOW_LABEL });

  await now.click();
  const sheet = sheetOf(page);
  await expect(sheet.getByRole('heading')).toHaveText('Update Demo machine now?');
  await expect(sheet.locator('.update-now-session')).toHaveText([
    'Plan Migration to Windows App \u00b7 Question waiting',
    'Refactor the relay client \u00b7 Mid-turn',
    'Land the P1 slices \u00b7 Mid-turn',
  ]);
  await expect(sheet.locator('.update-now-pause')).toHaveText('They pause while the bridge restarts, usually under a minute.');
  await expect(sheet.locator('.update-now-warn')).toHaveText('A question or approval waiting may have to be answered at the machine afterwards.');
  await expect(sheet.getByRole('button', { name: 'Cancel' })).toBeFocused();

  await sheet.getByRole('button', { name: 'Cancel' }).click();
  await expect(sheet).toHaveCount(0);
  await expect(block.getByRole('button', { name: NOW_LABEL })).toBeFocused();
  expect(await page.evaluate(() => window.agentBridgeDemo.updateLog)).toEqual([]);

  // Escape is Cancel too.
  await page.keyboard.press('Enter');
  await expect(sheet.getByRole('button', { name: 'Cancel' })).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(sheet).toHaveCount(0);
  expect(await page.evaluate(() => window.agentBridgeDemo.updateLog)).toEqual([]);

  // By keyboard: Tab from Cancel to Update now.
  await block.getByRole('button', { name: NOW_LABEL }).focus();
  await page.keyboard.press('Enter');
  await expect(sheet.getByRole('button', { name: 'Cancel' })).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(sheet.getByRole('button', { name: 'Update now' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect(block.locator('.machine-update-last')).toHaveText('Updated to 0.2.0.');
  const [sent] = await page.evaluate(() => window.agentBridgeDemo.updateLog);
  expect(sent.accept).toEqual({
    working: 2, waiting: 1,
    sessions: [
      { id: '02d9e5d5-6b35-49bd-b083-39572fbff648', state: 'question' },
      { id: '6f1c2b7e-claude-example', state: 'working' },
      { id: LAND, state: 'working' },
    ],
  });
});

test('a machine busier than the confirmation covered shows it again with what is busy now, an approval named and warned of', async ({ page }) => {
  await openDemo(page, '&updateNow=1');
  await setUpdate(page, QUIET);
  // Busy at the machine since this device last heard: the press finds it so.
  await page.evaluate((id) => {
    const h = window.agentBridgeDemo.host;
    h.update = { ...h.update, busy: { working: 0, waiting: 1, sessions: [{ id, state: 'approval' }] } };
  }, LAND);
  const block = updateOf(page);

  await block.getByRole('button', { name: NOW_LABEL }).click();
  const sheet = sheetOf(page);
  await expect(sheet.locator('.update-now-session')).toHaveText(['Land the P1 slices \u00b7 Approval waiting']);
  await expect(sheet.locator('.update-now-warn')).toBeVisible();
  await expect(sheet.getByRole('button', { name: 'Cancel' })).toBeFocused();
  await sheet.getByRole('button', { name: 'Update now' }).click();

  await expect(block.locator('.machine-update-last')).toHaveText('Updated to 0.2.0.');
  const log = await page.evaluate(() => window.agentBridgeDemo.updateLog);
  expect(log.map((e) => e.accept)).toEqual([null, { working: 0, waiting: 1, sessions: [{ id: LAND, state: 'approval' }] }]);
});

test("a refused Update now is worded from its code, never the machine's text", async ({ page }) => {
  await openDemo(page, '&updateNow=1');
  await setUpdate(page, QUIET);
  await page.evaluate(() => window.agentBridgeDemo.scriptUpdates({ code: 'release-changed', reason: 'host words nobody sees' }));
  const block = updateOf(page);

  await block.getByRole('button', { name: NOW_LABEL }).click();

  await expect(block.getByRole('alert')).toHaveText('Not updated: the update changed. Check for updates again.');
  await expect(page.getByText('host words nobody sees')).toHaveCount(0);
});

test("a machine where Update now can't be pressed says why in its place, and Install stays", async ({ page }) => {
  await openDemo(page, '&updateNow=1');
  await setUpdate(page, { canUpdateNow: false, updateNowWhy: 'old-app' });
  const block = updateOf(page);

  await expect(block.locator('.machine-update-why')).toHaveText('Update now needs a newer app there. Install still works.');
  await expect(block.locator('.machine-update-now')).toHaveCount(0);
  await expect(block.locator('.machine-update-install')).toBeVisible();
});

test("an Update now waiting reads Creative's line for its code, never the machine's waiting text", async ({ page }) => {
  await openDemo(page, '&updateNow=1');
  await setUpdate(page, { phase: 'waiting', request: 'r1', requestNow: true, waitingCode: 'turn-running', waiting: 'host words nobody sees' });

  await expect(updateOf(page).locator('.machine-update-line')).toHaveText('Update ready \u00b7 waiting for the current turn to finish');
});

test('a machine that left for an Update now reads Restarting to update, announced, until its time is up', async ({ page }) => {
  await openDemo(page, '&updateNow=1&machines=2&restartMs=1500');
  await page.evaluate(() => { window.agentBridgeDemo.host.holdUpdateNow = true; });
  await setUpdate(page, QUIET);
  const block = updateOf(page);

  await block.getByRole('button', { name: NOW_LABEL }).click();
  await expect(block.locator('.machine-update-line')).toHaveText('Restarting to update\u2026');
  await expect(page.locator('.machine-update-announce')).toHaveText(/Demo machine: Restarting to update\u2026/);

  await page.evaluate(() => window.agentBridgeDemo.setOnline('demo', false));
  const row = page.locator('#machine-list [data-device-id="demo"]');
  await expect(row.locator('.machine-state')).toHaveText('Restarting to update\u2026');
  await expect(row).toHaveCount(0, { timeout: 5000 });
});
