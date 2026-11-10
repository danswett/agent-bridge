// An approval the host escaped anything in (#803, ui.md §6.4 "Escaped approvals"), in demo mode:
// Deny on a tap, as for any approval; Approve only from the confirmation sheet's own button, at
// the end of the escaped command, signed when it is pressed for this question and what was read.
import { readFileSync } from 'node:fs';
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

// The shared vector, so the message's digest and marks are exactly a host's.
const vectors = JSON.parse(readFileSync(new URL('../../docs/protocol/vectors/sessions.json', import.meta.url), 'utf8'));
const ESCAPED = vectors.records.Question.valid.find((v) => v.name === 'an-escaped-approval').json;

const log = (page) => page.evaluate(() => window.agentBridgeDemo.host.answerLog);
const confirms = (page) => page.evaluate(() => window.agentBridgeDemo.host.confirmLog);
const later = () => new Date(Date.now() + 1000).toISOString();

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1&answerDelay=300${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

async function ask(page, question) {
  await page.evaluate(({ id, question, t }) => {
    const d = window.agentBridgeDemo;
    d.upsert({ ...d.host.get(id), status: 'waiting', statusSince: t, updatedAt: t, question: { ...question, askedAt: t } });
  }, { id: COPILOT, question, t: later() });
  const q = view(page, COPILOT).locator('.question:not(.resolved)');
  await expect(q).toHaveAttribute('data-question', question.id);
  await open(page, COPILOT);
  return q;
}

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });

test('a confirmation pressed as its machine goes is not taken for sent: the sheet says so, and a press once it is back sends it', async ({ page }) => {
  // The route gone while the confirmation was signed (#269's gate): nothing is queued, so the sheet
  // stays open saying so, its button the Try again (#836 review 5780082443).
  await openDemo(page);
  const q = await ask(page, ESCAPED);
  await q.getByRole('button', { name: 'Approve once…' }).click();
  const sheet = page.getByRole('dialog', { name: 'Approve a command with hidden characters?' });
  const confirm = sheet.getByRole('button', { name: "Approve once: I've read the escaped command" });
  await expect(confirm).toBeEnabled();
  await page.evaluate(() => window.agentBridgeDemo.host.holdSigning());
  await confirm.click();
  await page.evaluate(() => window.agentBridgeDemo.setOnline(window.agentBridgeDemo.host.machine.id, false));
  await page.evaluate(() => window.agentBridgeDemo.host.releaseSigning());

  await expect(sheet.locator('.sheet-error')).toContainText("Not sent:");
  await expect(sheet.locator('.sheet-error')).toContainText("isn't connected now. Press again once it's back.");
  await expect(sheet).toBeVisible();
  expect(await log(page)).toEqual([]);
  // Said directly under the button it asks to be pressed again, not at the sheet's foot (the
  // creative director's review of #836).
  const button = await confirm.boundingBox();
  const said = await sheet.locator('.sheet-error').boundingBox();
  expect(said.y - (button.y + button.height)).toBeGreaterThanOrEqual(0);
  expect(said.y - (button.y + button.height)).toBeLessThan(40);

  await page.evaluate(() => window.agentBridgeDemo.setOnline(window.agentBridgeDemo.host.machine.id, true));
  await expect(confirm).toBeEnabled();
  await expect(async () => {
    await confirm.click();
    await expect(sheet).toBeHidden({ timeout: 1000 });
  }).toPass();
  await expect.poll(async () => (await log(page)).length).toBe(1);
  const [sent] = await log(page);
  expect(sent.fields.decision).toBe('approve');
  expect(sent.confirm).toBeTruthy();
});

// The page's clock, moved on while signing is held: a tab suspended after the press, resumed later.
const later_ = (page, ms) => page.evaluate((ms) => { window.__now ??= Date.now; Date.now = () => window.__now() + ms; }, ms);

async function pressHeld(page) {
  await openDemo(page);
  const q = await ask(page, ESCAPED);
  await q.getByRole('button', { name: 'Approve once…' }).click();
  const sheet = page.getByRole('dialog', { name: 'Approve a command with hidden characters?' });
  const confirm = sheet.getByRole('button', { name: "Approve once: I've read the escaped command" });
  await expect(confirm).toBeEnabled();
  await page.evaluate(() => window.agentBridgeDemo.host.holdSigning());
  const before = await page.evaluate(() => Date.now());
  await confirm.click();
  return { sheet, confirm, before };
}

test('a confirmation is signed as of its press, however late its signing comes', async ({ page }) => {
  // Signed when the tab resumed, it had a fresh lifetime (#836 review 5780636755).
  const { sheet, before } = await pressHeld(page);
  await later_(page, 2 * 60 * 1000);
  await page.evaluate(() => window.agentBridgeDemo.host.releaseSigning());
  await expect(sheet).toBeHidden();

  await expect.poll(async () => (await confirms(page)).length).toBe(1);
  expect(await log(page)).toHaveLength(1);
  const [body] = await confirms(page);
  expect(body.issuedAt).toBeGreaterThanOrEqual(Math.floor(before / 1000));
  expect(body.issuedAt).toBeLessThanOrEqual(Math.floor(before / 1000) + 5);
  expect(body.expiresAt - body.issuedAt).toBe(300);
});

test('a confirmation already past its lifetime when it would be signed is never sent, and the sheet asks for the press again', async ({ page }) => {
  const { sheet, confirm } = await pressHeld(page);
  await later_(page, 5 * 60 * 1000 + 1000);
  await page.evaluate(() => window.agentBridgeDemo.host.releaseSigning());

  await expect(sheet.locator('.sheet-error')).toHaveText('Not sent: this confirmation is more than 5 minutes old. Press again.');
  await expect(sheet).toBeVisible();
  await expect(confirm).toBeEnabled();
  expect(await log(page)).toEqual([]);
  expect(await confirms(page)).toEqual([]);

  await confirm.click();
  await expect(sheet).toBeHidden();
  await expect.poll(async () => (await log(page)).length).toBe(1);
});

test('wide, the sheet is as tall as a short command, and its held confirm in forced colours is the system disabled cue', async ({ page, browserName }) => {
  // A short command left an empty panel at 1440, and the held confirm in high contrast was a darker
  // accent fill, too close to the enabled one (the creative director's review of #836).
  test.skip(browserName !== 'chromium', 'forced colours are emulated in Chromium only');
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.emulateMedia({ forcedColors: 'active' });
  await openDemo(page);
  const q = await ask(page, ESCAPED);
  await q.getByRole('button', { name: 'Approve once…' }).click();
  const sheet = page.getByRole('dialog', { name: 'Approve a command with hidden characters?' });
  const confirm = sheet.getByRole('button', { name: "Approve once: I've read the escaped command" });
  await expect(confirm).toBeDisabled();

  expect((await sheet.boundingBox()).height).toBeLessThan(900 * 0.6);
  const [held, system] = await confirm.evaluate((b) => {
    const i = document.createElement('i');
    i.style.cssText = 'forced-color-adjust: none; color: GrayText; background: Canvas';
    document.body.append(i);
    const s = getComputedStyle(i);
    const out = [[getComputedStyle(b).color, getComputedStyle(b).backgroundColor, getComputedStyle(b).borderTopColor], [s.color, s.backgroundColor, s.color]];
    i.remove();
    return out;
  });
  expect(held).toEqual(system);
  await expect(confirm).toBeEnabled({ timeout: 5000 });
  expect(await confirm.evaluate((b) => getComputedStyle(b).color)).not.toBe(system[0]);
});

test('an escaped approval is never approved on a tap: Approve once opens the sheet, and Cancel sends nothing', async ({ page }) => {
  await openDemo(page);
  const q = await ask(page, ESCAPED);
  await expect(q.getByRole('button').and(page.locator('button:not(.md-copy)'))).toHaveText(['Approve once…', 'Deny']);
  await q.getByRole('button', { name: 'Approve once…' }).tap();

  const sheet = page.getByRole('dialog', { name: 'Approve a command with hidden characters?' });
  await expect(sheet).toBeVisible();
  await expect(sheet.getByRole('button', { name: 'Cancel' })).toBeFocused();
  // The host's escape, boxed and named; the text around it as sent.
  await expect(sheet.getByRole('region', { name: 'The command, escaped' }).getByRole('img', { name: 'hidden character, right-to-left override' })).toHaveText('\\u202E');
  await expect(sheet.locator('pre.escaped-text')).toHaveText(ESCAPED.message);
  await expect(sheet.locator('pre.escaped-text')).toHaveCSS('direction', 'ltr');
  await expect(sheet.locator('.sheet-warning')).toContainText('reorder what you see');

  await sheet.getByRole('button', { name: 'Cancel' }).tap();
  await expect(sheet).toHaveCount(0);
  expect(await log(page)).toEqual([]);
  await expect(q).toHaveAttribute('data-question', ESCAPED.id);
});

test('the confirm waits until the end of the command has been seen, then sends Approve once with a confirmation for exactly this question', async ({ page }) => {
  await openDemo(page);
  const q = await ask(page, ESCAPED);
  await q.getByRole('button', { name: 'Approve once…' }).tap();
  const sheet = page.getByRole('dialog', { name: 'Approve a command with hidden characters?' });
  const confirm = sheet.getByRole('button', { name: "Approve once: I've read the escaped command" });
  await expect(confirm).toBeDisabled();
  // Its words are drawn, not the accessible name alone: they were once the colour of the button.
  await expect(confirm).toHaveText("I've read it. Approve once");
  const { color, background } = await confirm.evaluate((b) => ({ color: getComputedStyle(b).color, background: getComputedStyle(b).backgroundColor }));
  expect(color).not.toBe(background);
  await confirm.scrollIntoViewIfNeeded();
  await expect(confirm).toBeEnabled({ timeout: 5000 });
  await expect(sheet.locator('.sheet-announce')).toHaveText('Approve is now available');
  await confirm.tap();

  await expect(sheet).toHaveCount(0);
  await expect(q).toHaveCount(0);
  const [sent] = await log(page);
  expect(sent).toEqual({ id: COPILOT, question: ESCAPED.id, request: expect.stringMatching(UUID), fields: { decision: 'approve' }, confirm: expect.objectContaining({ body: expect.any(String), mac: expect.any(String) }), progress: true });
  const [body] = await confirms(page);
  expect(body).toMatchObject({ action: 'session.approve:escaped', request: sent.request, target: { session: COPILOT, question: ESCAPED.id, shown: ESCAPED.escaped.shown } });
});

test('a message that does not hash to what the host sent is not confirmed', async ({ page }) => {
  await openDemo(page);
  const q = await ask(page, { ...ESCAPED, escaped: { ...ESCAPED.escaped, shown: '0'.repeat(64) } });
  await q.getByRole('button', { name: 'Approve once…' }).tap();
  const sheet = page.getByRole('dialog', { name: 'Approve a command with hidden characters?' });
  const confirm = sheet.getByRole('button', { name: "Approve once: I've read the escaped command" });
  await confirm.scrollIntoViewIfNeeded();
  await expect(confirm).toBeEnabled({ timeout: 5000 });
  await confirm.tap();
  await expect(sheet.getByRole('alert')).toContainText("didn't arrive as the machine sent it");
  expect(await log(page)).toEqual([]);
});

test('a mark that covers more than one escape is never offered the sheet, and the message is drawn whole', async ({ page }) => {
  await openDemo(page);
  // "\u202E; rm -rf work" under one mark: drawn as the token alone, it hid what it approved.
  const message = ESCAPED.message.replace('\\u202E ;hs', '\\u202E; rm -rf work ;hs');
  const at = message.indexOf('\\u202E');
  const q = await ask(page, { ...ESCAPED, message, escaped: { ...ESCAPED.escaped, marks: [[at, '\\u202E; rm -rf work'.length]] } });
  await expect(q.getByRole('button').and(page.locator('button:not(.md-copy)'))).toHaveText(['Deny']);
  await expect(q.locator('.question-message')).toContainText('rm -rf work');
});

test('Back closes the sheet as cancelled, sends nothing, and leaves the session open', async ({ page }) => {
  await openDemo(page);
  const q = await ask(page, ESCAPED);
  const url = page.url();
  await q.getByRole('button', { name: 'Approve once…' }).tap();
  const sheet = page.getByRole('dialog', { name: 'Approve a command with hidden characters?' });
  await expect(sheet).toBeVisible();
  await page.goBack();
  await expect(sheet).toHaveCount(0);
  expect(page.url()).toBe(url);
  await expect(q).toHaveAttribute('data-question', ESCAPED.id);
  expect(await log(page)).toEqual([]);
  // And closing it with Cancel takes its Back step with it.
  await q.getByRole('button', { name: 'Approve once…' }).tap();
  await sheet.getByRole('button', { name: 'Cancel' }).tap();
  await expect(sheet).toHaveCount(0);
  await expect(q).toBeVisible();
});

test('Cancel pressed while the confirmation is being signed sends nothing', async ({ page }) => {
  await openDemo(page);
  const q = await ask(page, ESCAPED);
  await page.evaluate(() => window.agentBridgeDemo.host.holdSigning());
  await q.getByRole('button', { name: 'Approve once…' }).tap();
  const sheet = page.getByRole('dialog', { name: 'Approve a command with hidden characters?' });
  const confirm = sheet.getByRole('button', { name: "Approve once: I've read the escaped command" });
  await confirm.scrollIntoViewIfNeeded();
  await expect(confirm).toBeEnabled({ timeout: 5000 });
  await confirm.tap();
  await sheet.getByRole('button', { name: 'Cancel' }).tap();
  await expect(sheet).toHaveCount(0);
  await page.evaluate(() => window.agentBridgeDemo.host.releaseSigning());
  await page.waitForTimeout(500);
  expect(await log(page)).toEqual([]);
  await expect(q).toHaveAttribute('data-question', ESCAPED.id);
});

test('Deny goes on a tap, as for any approval', async ({ page }) => {
  await openDemo(page);
  const q = await ask(page, ESCAPED);
  await q.getByRole('button', { name: 'Deny' }).tap();
  await expect(q).toHaveCount(0);
  expect((await log(page))[0].fields).toEqual({ decision: 'deny' });
  expect((await log(page))[0].confirm).toBeUndefined();
});

test('from a device the machine has not approved for these, only Deny is offered, with how to approve one', async ({ page }) => {
  await openDemo(page, '&confirm=0');
  const q = await ask(page, ESCAPED);
  await expect(q.getByRole('button').and(page.locator('button:not(.md-copy)'))).toHaveText(['Deny']);
  await expect(q.locator('.escaped-why')).toContainText('Approve this at Demo machine, or from a device approved there.');
  await expect(q.locator('.escaped-help summary')).toHaveText('How to approve a device');
});

test('an escaped approval the machine keeps for itself offers only Deny, and says so', async ({ page }) => {
  await openDemo(page);
  const q = await ask(page, { ...ESCAPED, escaped: { ...ESCAPED.escaped, approve: 'machine' } });
  await expect(q.getByRole('button').and(page.locator('button:not(.md-copy)'))).toHaveText(['Deny']);
  await expect(q.locator('.escaped-why')).toHaveText('Demo machine takes this approval only at the machine.');
});

test('an escaped approval with too many escapes to mark still denies from here, and sends Approve to the machine', async ({ page }) => {
  await openDemo(page);
  const q = await ask(page, { ...ESCAPED, escaped: { ...ESCAPED.escaped, approve: 'tooMany' } });
  await expect(q.getByRole('button').and(page.locator('button:not(.md-copy)'))).toHaveText(['Deny']);
  await expect(q.locator('.escaped-why')).toHaveText('Too many hidden characters to show here. Approve this at Demo machine.');
  await q.getByRole('button', { name: 'Deny' }).tap();
  await expect(q).toHaveCount(0);
  expect((await log(page))[0].fields).toEqual({ decision: 'deny' });
});
