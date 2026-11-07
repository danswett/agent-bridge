// Coming back to the app (#950, ui.md "Coming back to the app"): for 3 s after the app is shown
// again what was last seen stays as it was; held controls are aria-disabled at once and drawn
// disabled after 600 ms; a list not whole after 1 s says "Updating…" in the title row's slot; and at
// 3 s the away states show in place, with a strip if the client's own link is down. The page's
// clock is the test's, so each point lands where the test puts it.
import { readFileSync } from 'node:fs';
import { test, expect } from '@playwright/test';
import { open } from './nav.mjs';

const LAPTOP = 'demo-laptop';
const IDLE = 'laptop-copilot-1';
const ASK = 'laptop-ask-1';

const card = (page, id) => page.locator(`.session[data-host-id="${LAPTOP}"][data-id="${id}"]`);
const sv = (page, id) => page.locator(`.session-view[data-host-id="${LAPTOP}"][data-id="${id}"]`);
const setOnline = (page, on) => page.evaluate((on) => window.agentBridgeDemo.setOnline(LAPTOP_ID, on), on);
const slot = (page) => page.locator('#return-slot');

async function openDemo(page) {
  await page.addInitScript(() => { window.LAPTOP_ID = 'demo-laptop'; });
  await page.goto('?demo=1&static=1&machines=2');
  await page.waitForSelector('html[data-demo-ready]');
}

// The page's clock held from here, and the app shown again.
async function comeBack(page) {
  await page.clock.install();
  await page.clock.pauseAt(await page.evaluate(() => Date.now()) + 2000);
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
}

test.use({ viewport: { width: 1440, height: 900 }, hasTouch: false });

test('a machine that goes away just after a return reads as last seen for 3 s, then shows offline in place', async ({ page }) => {
  await openDemo(page);
  await comeBack(page);
  await setOnline(page, false);
  await page.clock.runFor(1500);
  await expect(card(page, IDLE).locator('.row-glyph .glyph')).not.toHaveAttribute('data-glyph', 'failed');
  await expect(card(page, IDLE).locator('.row-status')).not.toHaveText('Offline');
  await expect(card(page, IDLE)).not.toHaveAttribute('data-stale', /./);
  await expect(page.locator('#host-state')).toBeHidden();
  await page.clock.runFor(1600);
  await expect(card(page, IDLE)).toHaveAttribute('data-stale', 'offline');
  await expect(card(page, IDLE).locator('.row-status')).toHaveText('Offline');
});

test('a reply held for a return is aria-disabled at once, drawn disabled after 600 ms, and a press says Updating… under it', async ({ page }) => {
  await openDemo(page);
  await open(page, IDLE, LAPTOP);
  const v = sv(page, IDLE);
  await v.locator('.reply-input').fill('Push it when done');
  await comeBack(page);
  await setOnline(page, false);
  await page.clock.runFor(10);
  const send = v.locator('.reply .send');
  await expect(send).toHaveAttribute('aria-disabled', 'true');
  expect(await send.evaluate((b) => b.disabled)).toBe(false);
  await expect(v.locator('form.reply')).toHaveAttribute('data-hold', 'quiet');
  await expect(v.locator('.reply-held')).toBeHidden();
  await send.evaluate((b) => b.click());
  await expect(v.locator('.reply-updating')).toHaveText('Updating…');
  expect(await page.evaluate(() => window.agentBridgeDemo.machines['demo-laptop'].replyLog.length)).toBe(0);
  await page.clock.runFor(600);
  await expect(v.locator('form.reply')).toHaveAttribute('data-hold', 'drawn');
  await page.clock.runFor(2500);
  await expect(v.locator('.reply-held')).toHaveText('Laptop is offline · you can answer once it\'s back');
  await expect(v.locator('.reply-updating')).toBeHidden();
});

test('a list not whole 1 s after a return says Updating… in the title row\'s slot, unannounced, and moves no row', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => window.agentBridgeDemo.machines['demo-laptop'].holdSnapshot());
  await comeBack(page);
  await setOnline(page, false);
  await page.clock.runFor(100);
  await setOnline(page, true);
  await page.clock.runFor(700);
  const top = () => page.locator('#session-list > .session').first().evaluate((c) => c.getBoundingClientRect().top);
  const before = await top();
  await expect(slot(page)).toHaveText('');
  await page.clock.runFor(300);
  await expect(slot(page)).toHaveText('Updating…');
  await expect(slot(page)).not.toHaveAttribute('aria-live', /./);
  await expect(slot(page)).not.toHaveAttribute('role', /./);
  expect(await top()).toBe(before);
  expect(await page.locator('#session-list').evaluate((l) => l.previousElementSibling?.matches('#sign-in-banners, #needs-you, #machine-tabs') ?? true)).toBe(true);
});

test('the relay link down when the grace ends puts a strip in the title row at its height, said once politely', async ({ page }) => {
  await openDemo(page);
  const head = page.locator('.sessions-head');
  const height = (await head.boundingBox()).height;
  await comeBack(page);
  await page.evaluate(() => { window.agentBridgeDemo.connection.ready = false; window.agentBridgeDemo.page.refresh(); });
  await page.clock.runFor(2500);
  await expect(page.locator('#connection-strip')).toBeHidden();
  await page.clock.runFor(600);
  const strip = page.locator('#connection-strip');
  await expect(strip).toBeVisible();
  await expect(strip).toHaveAttribute('role', 'status');
  await expect(strip).toHaveAttribute('aria-live', 'polite');
  expect((await head.boundingBox()).height).toBe(height);
});

test('a question held for a return is aria-disabled at once, a press says Updating… that stays through 600 ms, and nothing is sent', async ({ page }) => {
  await openDemo(page);
  await open(page, ASK, LAPTOP);
  const v = sv(page, ASK);
  await comeBack(page);
  await setOnline(page, false);
  await page.clock.runFor(10);
  const choice = v.locator('.question .choice').first();
  await expect(choice).toHaveAttribute('aria-disabled', 'true');
  await expect(v.locator('.question .send')).toHaveAttribute('aria-disabled', 'true');
  expect(await choice.evaluate((b) => b.disabled)).toBe(false);
  await page.clock.runFor(290);
  await choice.evaluate((b) => b.click());
  await expect(v.locator('.answer-updating')).toHaveText('Updating…');
  await v.locator('.question .send').evaluate((b) => b.click());
  await page.clock.runFor(400);
  await expect(v.locator('.question')).toHaveAttribute('data-hold', 'drawn');
  await expect(v.locator('.answer-updating')).toHaveText('Updating…');
  await expect(page.getByRole('alert').filter({ hasText: /\S/ })).toHaveCount(0);
  expect(await page.evaluate(() => window.agentBridgeDemo.machines['demo-laptop'].answerLog.length)).toBe(0);
});

test('End session held for a return is aria-disabled at once, a press says Updating… and ends nothing', async ({ page }) => {
  await openDemo(page);
  await open(page, IDLE, LAPTOP);
  const v = sv(page, IDLE);
  await comeBack(page);
  await setOnline(page, false);
  await page.clock.runFor(10);
  const end = v.locator('.end-button');
  await expect(end).toHaveAttribute('aria-disabled', 'true');
  await end.evaluate((b) => b.click());
  await expect(v.locator('.stop-updating')).toHaveText('Updating…');
  await expect(page.getByRole('alert').filter({ hasText: /\S/ })).toHaveCount(0);
  expect(await page.evaluate(() => window.agentBridgeDemo.machines['demo-laptop'].stopLog.length)).toBe(0);
});

test('focus on the sort control when the strip takes the title row goes to the strip, never to the page', async ({ page }) => {
  await openDemo(page);
  await page.locator('.sessions-head select').first().focus();
  await comeBack(page);
  await page.evaluate(() => { window.agentBridgeDemo.connection.ready = false; window.agentBridgeDemo.page.refresh(); });
  await page.clock.runFor(3100);
  await expect(page.locator('#connection-strip')).toBeVisible();
  await expect(page.locator('#connection-strip')).toBeFocused();
});

test('a lane entry held for a return is aria-disabled at once, a press says Updating… and sends nothing, and answers again once the list is whole', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => {
    const m = window.agentBridgeDemo.machines['demo-laptop'];
    const t = new Date().toISOString();
    m.upsert({ ...m.get('laptop-ask-1'), status: 'waiting', statusSince: t, updatedAt: t, question: { kind: 'approval', askedAt: t, id: 'r1#0123456789abcdef', message: 'Allow **shell**?', fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'approve', label: 'Approve once' }, { value: 'deny', label: 'Deny' }] }] } });
  });
  const entry = page.locator(`#needs-you .needs-entry[data-host-id="${LAPTOP}"]`).filter({ has: page.locator('.needs-approve') }).first();
  await expect(entry).toBeVisible();
  const approve = entry.locator('.needs-approve');
  await comeBack(page);
  await setOnline(page, false);
  await page.clock.runFor(10);
  await expect(approve).toHaveAttribute('aria-disabled', 'true');
  expect(await approve.evaluate((b) => b.disabled)).toBe(false);
  await approve.evaluate((b) => b.click());
  await expect(entry.locator('.needs-updating')).toHaveText('Updating…');
  await expect(page.getByRole('alert').filter({ hasText: /\S/ })).toHaveCount(0);
  expect(await page.evaluate(() => window.agentBridgeDemo.machines['demo-laptop'].answerLog.length)).toBe(0);
  await page.clock.runFor(600);
  await expect(entry).toHaveAttribute('data-hold', 'drawn');
  await setOnline(page, true);
  await page.clock.runFor(4000);
  await expect(approve).not.toHaveAttribute('aria-disabled', 'true');
  await expect(entry.locator('.needs-updating')).toBeHidden();
});

test('a second event inside the grace leaves a held control drawn disabled, with no snap back to quiet', async ({ page }) => {
  await openDemo(page);
  await open(page, ASK, LAPTOP);
  const v = sv(page, ASK);
  await comeBack(page);
  await setOnline(page, false);
  await page.clock.runFor(800);
  await expect(v.locator('.question')).toHaveAttribute('data-hold', 'drawn');
  await page.evaluate(() => document.dispatchEvent(new Event('visibilitychange')));
  await page.clock.runFor(10);
  await expect(v.locator('.question')).toHaveAttribute('data-hold', 'drawn');
  await page.clock.runFor(2200);
  await expect(v.locator('.question')).not.toHaveAttribute('data-hold', /quiet|drawn/);
});

test('a hold that starts while the escaped-approval sheet is open stops its confirmation from being sent', async ({ page }) => {
  const escaped = JSON.parse(readFileSync(new URL('../../docs/protocol/vectors/sessions.json', import.meta.url), 'utf8')).records.Question.valid.find((v) => v.name === 'an-escaped-approval').json;
  await openDemo(page);
  await page.evaluate((question) => {
    const m = window.agentBridgeDemo.machines['demo-laptop'];
    const t = new Date(Date.now() + 1000).toISOString();
    m.upsert({ ...m.get('laptop-ask-1'), status: 'waiting', statusSince: t, updatedAt: t, question: { ...question, askedAt: t } });
  }, escaped);
  await open(page, ASK, LAPTOP);
  await sv(page, ASK).getByRole('button', { name: 'Approve once…' }).click();
  const sheet = page.getByRole('dialog', { name: 'Approve a command with hidden characters?' });
  const confirm = sheet.getByRole('button', { name: "Approve once: I've read the escaped command" });
  await expect(confirm).toBeEnabled({ timeout: 8000 });
  await page.evaluate(() => window.agentBridgeDemo.machines['demo-laptop'].holdSigning());
  await confirm.click();
  await comeBack(page);
  await setOnline(page, false);
  await page.clock.runFor(10);
  await page.evaluate(() => window.agentBridgeDemo.machines['demo-laptop'].releaseSigning());
  await expect(sheet.locator('.sheet-error')).toContainText('Updating…');
  await expect(sheet).toBeVisible();
  expect(await page.evaluate(() => window.agentBridgeDemo.machines['demo-laptop'].answerLog.length)).toBe(0);
});

test('an offline machine\'s line takes the place of the reply box\'s actions and focus on one goes to it', async ({ page }) => {
  await openDemo(page);
  await open(page, IDLE, LAPTOP);
  const v = sv(page, IDLE);
  await v.locator('.reply-input').fill('Push it when done');
  await v.locator('.reply .send').focus();
  await setOnline(page, false);
  const line = v.locator('.reply-held');
  await expect(line).toHaveText('Laptop is offline · you can answer once it\'s back');
  await expect(v.locator('.reply-tools')).toBeHidden();
  await expect(line).toHaveAttribute('tabindex', '-1');
  await expect(line).toBeFocused();
  await expect(page.locator('#host-state')).toBeHidden();
});

test('a question held through a return keeps its row and Needs you\'s count until the grace ends', async ({ page }) => {
  await openDemo(page);
  const entries = page.locator(`#needs-you .needs-entry[data-host-id="${LAPTOP}"]`);
  await expect(entries).toHaveCount(1);
  const head = await page.locator('#needs-you .needs-you-head').textContent();
  await comeBack(page);
  await setOnline(page, false);
  await page.clock.runFor(1500);
  await expect(entries).toHaveCount(1);
  await expect(page.locator('#needs-you .needs-you-head')).toHaveText(head);
  await expect(card(page, ASK)).not.toHaveAttribute('data-stale', /./);
  await page.clock.runFor(1600);
  await expect(entries).toHaveCount(0);
});
