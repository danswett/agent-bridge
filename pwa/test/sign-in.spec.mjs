// An agent that needs sign-in, in demo mode (#800, ui.md §6.1, §6.13 and §9.1), as Connection's
// producer contract has it: four Copilot sessions on DEV-VM1 stopped because its sign-in expired,
// rolled up into one banner; Sign in, and the sheet with the device code from signin.code and
// github.com, Copy and Open; then signed in and waiting for the sessions to confirm, recovered once
// the host clears them, recovered by itself, or why not. And
// what a spoofed host can't do: put another link, an odd code, or a token on the page.
import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

const SESSIONS = ['signin-1', 'signin-2', 'signin-3', 'signin-4'];
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

async function openDemo(page, extra = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
    // What Copy code put on the clipboard, without asking the browser for the permission.
    window.__copied = [];
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: async (t) => { window.__copied.push(t); } } });
  });
  await page.goto(`?demo=1&static=1&signin=1&name=DEV-VM1${extra}#/sessions`);
  await page.waitForSelector('html[data-demo-ready]');
}

const banner = (page) => page.locator('#sign-in-banners .sign-in-banner');
const sheet = (page) => page.locator('dialog.sign-in-sheet');
const row = (page, id) => page.locator(`#session-list > .session[data-id="${id}"]`);
const signIn = (page) => banner(page).getByRole('button', { name: 'Sign in to Copilot on DEV-VM1' });

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test('sessions that need sign-in say so on their rows and in Needs you, rolled up into one banner for the machine', async ({ page }) => {
  await openDemo(page);
  await expect(banner(page)).toHaveCount(1);
  await expect(banner(page).locator('.sign-in-head')).toHaveText('Copilot on DEV-VM1 needs sign-in \u00b7 @demo-user');
  await expect(banner(page).locator('.sign-in-line')).toHaveText('4 sessions affected');
  await expect(banner(page).locator('.sign-in-session')).toHaveText(['Coordinate the Shiproom', 'Fix the relay reconnect', 'Review the usage panel', 'Draft the release notes']);
  for (const id of SESSIONS) {
    await expect(row(page, id).locator('.row-status')).toHaveText('Needs sign-in');
    await expect(row(page, id)).toHaveAttribute('data-rule', '10');
  }
  await expect(page.locator('#needs-you .needs-entry[data-mode="signIn"] .needs-line')).toHaveText(Array(4).fill('Needs sign-in'));
  // The other ways a session can stop: never idle, never working, never counted in Needs you.
  await expect(row(page, 'error-quota').locator('.row-status')).toHaveText('Agent error');
  await expect(row(page, 'stalled').locator('.row-status')).toHaveText(/^Not responding$/);
  // Both are driven by an agent: the row says what is wrong, not "Driven by an agent", and the glyph
  // keeps the driven badge.
  for (const id of ['error-quota', 'stalled']) {
    await expect(row(page, id).locator('.row-status.driver')).toHaveCount(0);
    await expect(row(page, id).locator('.row-main .glyph')).toHaveAccessibleName(/driven by an agent/);
  }
  await expect(page.locator('#needs-you .needs-entry[data-id="error-quota"], #needs-you .needs-entry[data-id="stalled"]')).toHaveCount(0);
  // The host's message is in Details as it is, as plain text, and never on its row (6649775484).
  await row(page, 'error-quota').click();
  await expect(page.locator('#session-views .session-view[data-id="error-quota"] .fact-attention dt')).toHaveText('Message');
  await expect(page.locator('#session-views .session-view[data-id="error-quota"] .fact-attention dd')).toHaveText('Claude stopped with an error.');
  // Said once, politely, without the code.
  await expect(page.locator('#sign-in-said .sign-in-said-line').last()).toHaveText('Copilot on DEV-VM1 needs sign-in');
  // A session in the banner opens on a press.
  await banner(page).getByRole('button', { name: 'Draft the release notes' }).click();
  await expect(page.locator('#session-views .session-view[data-id="signin-4"]')).toBeVisible();
});

test('Sign in asks the machine once, and its sheet shows the code from signin.code with Copy and Open, nothing opened unpressed', async ({ page, context }) => {
  await openDemo(page);
  let opened = 0;
  context.on('page', () => { opened++; });
  await signIn(page).click();

  await expect(sheet(page)).toBeVisible();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  await expect(sheet(page).locator('.sign-in-where')).toHaveText('Open this page and enter the code.');
  await expect(sheet(page).locator('.sign-in-uri')).toHaveText('github.com/login/device');
  await expect(sheet(page).locator('.sign-in-sheet-title')).toHaveText('Sign in to Copilot on DEV-VM1');
  await expect(banner(page).locator('.sign-in-line')).toHaveText(/^Waiting for sign-in \u00b7 code expires \d{1,2}:\d{2}/);
  await expect(sheet(page).locator('.sign-in-expiry')).toHaveText(/^Expires \d{1,2}:\d{2}/);
  const log = await page.evaluate(() => window.agentBridgeDemo.signInLog);
  expect(log).toHaveLength(1);
  expect(log[0]).toMatchObject({ op: 'signin.start', agent: 'copilot', account: 'demo-user' });
  expect(log[0].request).toMatch(UUID);
  // The sheet starts on Cancel.
  await expect(sheet(page).getByRole('button', { name: 'Cancel sign-in for Copilot on DEV-VM1' })).toBeFocused();
  const open = sheet(page).getByRole('link', { name: 'Open github.com to sign in to Copilot on DEV-VM1' });
  await expect(open).toHaveAttribute('href', 'https://github.com/login/device');
  await expect(open).toHaveAttribute('target', '_blank');
  await expect(open).toHaveAttribute('rel', 'noopener noreferrer');
  expect(opened).toBe(0);
  // The code can be selected and copied as shown, and is read a character at a time.
  expect(await sheet(page).locator('.sign-in-code').evaluate((e) => { const c = getComputedStyle(e); return c.userSelect || c.webkitUserSelect; })).toBe('all');
  await expect(sheet(page).locator('.sign-in-code-spelled')).toHaveText('Code: W D J B, M J H T');
  await sheet(page).getByRole('button', { name: 'Copy code for Copilot on DEV-VM1' }).click();
  expect(await page.evaluate(() => window.__copied)).toEqual(['WDJB-MJHT']);
  await expect(sheet(page).locator('.sign-in-copied')).toHaveText('Code copied');
  // The code was never in a session or a snapshot, and the live region never said it.
  expect(JSON.stringify(await page.evaluate(() => [...window.agentBridgeDemo.host.sessions.values()]))).not.toContain('WDJB');
  await expect(page.locator('#sign-in-said')).not.toContainText('WDJB');
});

test('Escape is Cancel: the flow is cancelled, the code goes, and focus is back on the banner', async ({ page }) => {
  await openDemo(page);
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(sheet(page)).toBeHidden();
  await expect(page.locator('.sign-in-code')).toHaveCount(0);
  await expect.poll(() => page.evaluate(() => window.agentBridgeDemo.signInLog.map((l) => l.op))).toEqual(['signin.start', 'signin.cancel']);
  expect((await page.evaluate(() => window.agentBridgeDemo.signInLog))[1].flow).toBe('flow-1');
  await expect(signIn(page)).toBeFocused();
});

test('a start cancelled before its result came, then pressed again, leaves the new sheet alone when the late result arrives', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => window.agentBridgeDemo.delaySignIns(800));
  await signIn(page).click();
  await expect(sheet(page)).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(sheet(page)).toBeHidden();
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  // The first press's result, late, joined the same flow: it neither closes this sheet nor cancels it.
  await page.waitForTimeout(1200);
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  expect((await page.evaluate(() => window.agentBridgeDemo.signInLog)).map((l) => l.op)).toEqual(['signin.start', 'signin.start']);
});

test('the abandoned start\'s result, coming before the next press\'s, cancels nothing until that press says which flow it is in', async ({ page }) => {
  await openDemo(page);
  // The first start answers at 0.8 s; the second, which joins the same flow, at 1.6 s.
  await page.evaluate(() => window.agentBridgeDemo.delaySignIns(800, 1600));
  await signIn(page).click();
  await expect(sheet(page)).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(sheet(page)).toBeHidden();
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT', { timeout: 5000 });
  // The flow both joined is this press's: not cancelled at the machine.
  expect((await page.evaluate(() => window.agentBridgeDemo.signInLog)).map((l) => l.op)).toEqual(['signin.start', 'signin.start']);
});

test('a code that came before a slow start\'s result is still there when the result comes, past the ten seconds', async ({ page }) => {
  await openDemo(page);
  // The code comes at once; the start's result 12 s later, within its timeout.
  await page.evaluate(() => window.agentBridgeDemo.delaySignIns(12000));
  await signIn(page).click();
  await expect.poll(() => page.evaluate(() => window.agentBridgeDemo.signInCodesHeld)).toBe(1);
  await page.waitForTimeout(10500);
  // Past the ten seconds, a change draws the banners again (and prunes what is held): the code stays.
  await page.evaluate(() => { const d = window.agentBridgeDemo; d.upsert({ ...d.host.get('error-quota') }); });
  await page.waitForTimeout(300);
  expect(await page.evaluate(() => window.agentBridgeDemo.signInCodesHeld)).toBe(1);
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT', { timeout: 5000 });
});

test('a held code that runs out before its start\'s result comes leaves only that it did: the sheet says so and offers Try again', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    d.nextCode = { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com/login/device', expiresAt: new Date(Date.now() + 1500).toISOString() };
    d.delaySignIns(4000);
  });
  await signIn(page).click();
  await expect.poll(() => page.evaluate(() => window.agentBridgeDemo.signInCodesHeld)).toBe(1);
  await page.waitForTimeout(2000);
  // Run out while held: a change draws the banners again, and the code goes.
  await page.evaluate(() => { const d = window.agentBridgeDemo; d.upsert({ ...d.host.get('error-quota') }); });
  await expect.poll(() => page.evaluate(() => window.agentBridgeDemo.signInCodesHeld)).toBe(0);
  await expect(sheet(page).locator('.sign-in-expired')).toHaveText('Code expired', { timeout: 6000 });
  await expect(sheet(page).getByRole('button', { name: 'Try again to sign in to Copilot on DEV-VM1' })).toBeVisible();
  expect(await page.evaluate(() => document.body.outerHTML)).not.toContain('WDJB');
});

test('a late code from a cancelled machine is never held while another machine starts', async ({ page }) => {
  await openDemo(page, '&machines=2');
  const laptop = page.locator('#sign-in-banners .sign-in-banner', { hasText: 'Copilot on Laptop' }).getByRole('button', { name: 'Sign in to Copilot on Laptop' });
  // Laptop: started, then cancelled.
  await laptop.click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  await page.keyboard.press('Escape');
  await expect(sheet(page)).toBeHidden();
  // The cancel reaches Laptop a moment after the sheet closes.
  const cancelled = () => page.evaluate(() => window.agentBridgeDemo.machines['demo-laptop'].signInLog.find((l) => l.op === 'signin.cancel')?.flow ?? null);
  await expect.poll(cancelled).not.toBeNull();
  const flow = await cancelled();
  // DEV-VM1 starts, its result slow: its own code is held for it.
  await page.evaluate(() => window.agentBridgeDemo.delaySignIns(4000));
  await signIn(page).click();
  await expect.poll(() => page.evaluate(() => window.agentBridgeDemo.signInCodesHeld)).toBe(1);
  // Laptop's code for the flow cancelled there comes late: nothing on Laptop can claim it.
  await page.evaluate((f) => {
    const l = window.agentBridgeDemo.machines['demo-laptop'];
    l.frame({ ev: 'signin.code', seq: l.seq + 1, data: { flow: f, agent: 'copilot', userCode: 'LATE-CODE', verificationUri: 'https://github.com/login/device', expiresAt: new Date(Date.now() + 600000).toISOString() } });
  }, flow);
  await page.waitForTimeout(500);
  expect(await page.evaluate(() => window.agentBridgeDemo.signInCodesHeld)).toBe(1);
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT', { timeout: 6000 });
  expect(await page.evaluate(() => document.body.outerHTML)).not.toContain('LATE-CODE');
});

test('a start refused with no flow drops a cancelled press\'s late code at once', async ({ page }) => {
  await openDemo(page);
  // Press A: started, then cancelled.
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  await page.keyboard.press('Escape');
  await expect(sheet(page)).toBeHidden();
  const cancelled = () => page.evaluate(() => window.agentBridgeDemo.signInLog.find((l) => l.op === 'signin.cancel')?.flow ?? null);
  await expect.poll(cancelled).not.toBeNull();
  const flow = await cancelled();
  // Press B: slow, and refused with no flow. A's code comes late, while B starts, and is held for it.
  await page.evaluate(() => { const d = window.agentBridgeDemo; d.delaySignIns(3000); d.scriptSignIns({ outcome: 'refused', reason: 'not now' }); });
  await signIn(page).click();
  await page.evaluate((f) => {
    const h = window.agentBridgeDemo.host;
    h.frame({ ev: 'signin.code', seq: h.seq + 1, data: { flow: f, agent: 'copilot', userCode: 'LATE-CODE', verificationUri: 'https://github.com/login/device', expiresAt: new Date(Date.now() + 600000).toISOString() } });
  }, flow);
  await expect.poll(() => page.evaluate(() => window.agentBridgeDemo.signInCodesHeld)).toBe(1);
  await expect(sheet(page)).toBeHidden({ timeout: 6000 });
  // At once, not after the ten seconds an early event is otherwise held.
  expect(await page.evaluate(() => window.agentBridgeDemo.signInCodesHeld)).toBe(0);
});

test('a start that names another flow drops a cancelled press\'s late code once its own are replayed', async ({ page }) => {
  await openDemo(page);
  const held = () => page.evaluate(() => window.agentBridgeDemo.signInCodesHeld);
  // Press A: started, then cancelled.
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  await page.keyboard.press('Escape');
  await expect(sheet(page)).toBeHidden();
  const cancelled = () => page.evaluate(() => window.agentBridgeDemo.signInLog.find((l) => l.op === 'signin.cancel')?.flow ?? null);
  await expect.poll(cancelled).not.toBeNull();
  const flow = await cancelled();
  // Press B: slow, and started in a flow of its own. Its code and A's late one are both held for it.
  await page.evaluate(() => window.agentBridgeDemo.delaySignIns(3000));
  await signIn(page).click();
  await page.evaluate((f) => {
    const h = window.agentBridgeDemo.host;
    h.frame({ ev: 'signin.code', seq: h.seq + 1, data: { flow: f, agent: 'copilot', userCode: 'LATE-CODE', verificationUri: 'https://github.com/login/device', expiresAt: new Date(Date.now() + 600000).toISOString() } });
  }, flow);
  await expect.poll(held).toBe(2);
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT', { timeout: 6000 });
  // B's code is its flow's; A's, which nothing can claim now, is gone at once.
  expect(await held()).toBe(1);
});

test('a machine no longer shown takes its resolved banner with it at once', async ({ page }) => {
  await openDemo(page, '&machines=2');
  const laptop = page.locator('#sign-in-banners .sign-in-banner', { hasText: 'Copilot on Laptop' });
  await expect(laptop).toHaveCount(1);
  await page.evaluate(() => window.agentBridgeDemo.machines['demo-laptop'].recoverSignIn());
  await expect(laptop.locator('.sign-in-line')).toHaveText('Recovered by itself');
  // Only DEV-VM1 picked: Laptop's link goes, and nothing of it stays on screen.
  await page.evaluate(() => window.agentBridgeDemo.hosts.select('demo'));
  await expect(laptop).toHaveCount(0, { timeout: 3000 });
});

test('Try again goes, live, once the host no longer offers this device sign-in', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => { window.agentBridgeDemo.nextCode = { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com.evil.example/login/device', expiresAt: new Date(Date.now() + 600000).toISOString() }; });
  await signIn(page).click();
  const retry = sheet(page).getByRole('button', { name: 'Try again to sign in to Copilot on DEV-VM1' });
  await expect(retry).toBeVisible();
  // The newest session stops claiming phone sign-in: no claim (#696).
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const s = d.host.get('signin-1');
    d.upsert({ ...s, attention: { ...s.attention, signIn: 'unknown', since: new Date().toISOString() } });
  });
  await expect(retry).toHaveCount(0);
  await expect(sheet(page).locator('.sign-in-refused')).toBeVisible();
  expect((await page.evaluate(() => window.agentBridgeDemo.signInLog)).map((l) => l.op)).toEqual(['signin.start']);
});

test('Try again waits for the cancel before it starts again', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => { window.agentBridgeDemo.nextCode = { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com.evil.example/login/device', expiresAt: new Date(Date.now() + 600000).toISOString() }; });
  await signIn(page).click();
  const retry = sheet(page).getByRole('button', { name: 'Try again to sign in to Copilot on DEV-VM1' });
  await expect(retry).toBeVisible();
  await page.evaluate(() => window.agentBridgeDemo.delayCancels(1000));
  await retry.click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT', { timeout: 6000 });
  // The new start only once the old flow's cancel has finished: never joining the flow being stopped.
  expect((await page.evaluate(() => window.agentBridgeDemo.signInLog)).map((l) => l.op)).toEqual(['signin.start', 'signin.cancel', 'cancelled', 'signin.start']);
});

test('a press after Cancel waits for the cancel before it starts', async ({ page }) => {
  await openDemo(page);
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  await page.evaluate(() => window.agentBridgeDemo.delayCancels(1000));
  await page.keyboard.press('Escape');
  await expect(sheet(page)).toBeHidden();
  // Pressed again at once: the cancel is still stopping the old flow.
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT', { timeout: 6000 });
  // The new start only once the old flow's cancel has finished: never joining the flow being stopped.
  expect((await page.evaluate(() => window.agentBridgeDemo.signInLog)).map((l) => l.op)).toEqual(['signin.start', 'signin.cancel', 'cancelled', 'signin.start']);
  await page.waitForTimeout(300);
  await expect(sheet(page).locator('.sign-in-code')).toBeVisible();
});

test('a press whose wait was cancelled leaves the next press still waiting for the slow cancel', async ({ page }) => {
  await openDemo(page);
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  await page.evaluate(() => window.agentBridgeDemo.delayCancels(2000));
  await page.keyboard.press('Escape');
  await expect(sheet(page)).toBeHidden();
  // Pressed again, and cancelled while it waits for the slow cancel.
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-starting')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(sheet(page)).toBeHidden();
  // Pressed a third time: the old cancel still hasn't finished, and this press waits for it too.
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT', { timeout: 8000 });
  expect((await page.evaluate(() => window.agentBridgeDemo.signInLog)).map((l) => l.op)).toEqual(['signin.start', 'signin.cancel', 'cancelled', 'signin.start']);
});

test('Try again given up while its cancel finishes closes the sheet and sends nothing', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => { window.agentBridgeDemo.nextCode = { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com.evil.example/login/device', expiresAt: new Date(Date.now() + 600000).toISOString() }; });
  await signIn(page).click();
  const retry = sheet(page).getByRole('button', { name: 'Try again to sign in to Copilot on DEV-VM1' });
  await expect(retry).toBeVisible();
  await page.evaluate(() => window.agentBridgeDemo.delayCancels(1500));
  await retry.click();
  // The host stops offering this device sign-in while the cancel finishes.
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const s = d.host.get('signin-1');
    d.upsert({ ...s, attention: { ...s.attention, signIn: 'unknown', since: new Date().toISOString() } });
  });
  await expect(sheet(page)).toBeHidden({ timeout: 5000 });
  await page.waitForTimeout(300);
  expect((await page.evaluate(() => window.agentBridgeDemo.signInLog)).map((l) => l.op)).toEqual(['signin.start', 'signin.cancel', 'cancelled']);
});

test('every way an attempt ends is said aloud, never in the machine\'s words', async ({ page }) => {
  await openDemo(page);
  const said = page.locator('#sign-in-said .sign-in-said-line').last();
  // Refused: the sheet closes, and the banner's new line isn't read on its own.
  await page.evaluate(() => window.agentBridgeDemo.scriptSignIns({ outcome: 'refused', reason: 'secret machine words' }));
  await signIn(page).click();
  await expect(sheet(page)).toBeHidden();
  await expect(said).toHaveText('Sign-in refused for Copilot on DEV-VM1');
  // A code that couldn't be shown.
  await page.evaluate(() => { window.agentBridgeDemo.nextCode = { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com.evil.example/login/device', expiresAt: new Date(Date.now() + 600000).toISOString() }; });
  await banner(page).getByRole('button', { name: 'Try again to sign in to Copilot on DEV-VM1' }).click();
  await expect(said).toHaveText('The sign-in code for Copilot on DEV-VM1 couldn\u2019t be shown');
  await page.keyboard.press('Escape');
  await expect(sheet(page)).toBeHidden();
  // A code that runs out.
  await page.evaluate(() => { window.agentBridgeDemo.nextCode = { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com/login/device', expiresAt: new Date(Date.now() + 1500).toISOString() }; });
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  await expect(said).toHaveText('Sign-in code expired for Copilot on DEV-VM1', { timeout: 8000 });
  expect(await page.locator('#sign-in-said').textContent()).not.toMatch(/secret|evil/);
});

test('the live region reads only the lines added, never again those already said, and keeps every line of a render (more than six)', async ({ page }) => {
  await openDemo(page);
  const region = page.locator('#sign-in-said');
  await expect(region).toHaveAttribute('aria-atomic', 'false');
  await expect(region).toHaveAttribute('aria-relevant', 'additions');
  await expect(region.locator('.sign-in-said-line')).toHaveText(['Copilot on DEV-VM1 needs sign-in']);
  await region.locator('.sign-in-said-line').first().evaluate((p) => { p.dataset.mark = 'first'; });
  // Seven more banners, one per account, drawn in one render: every one is said, none dropped by a cap.
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    d.page.signIns.render = () => {};
    const s = d.host.get('signin-1');
    for (let i = 1; i <= 7; i++) { d.upsert({ ...s, id: `more-${i}`, title: `More ${i}`, attention: { ...s.attention, account: `user${i}` } }); }
  });
  await expect(page.locator('#session-list > .session[data-id^="more-"]')).toHaveCount(7);
  await page.evaluate(() => { const v = window.agentBridgeDemo.page.signIns; delete v.render; v.render(); });
  await expect(banner(page)).toHaveCount(8);
  await expect(region.locator('.sign-in-said-line')).toHaveCount(8);
  // All seven came in one render (one time on every new line), more than the old cap of six.
  expect(new Set(await region.locator('.sign-in-said-line:not([data-mark])').evaluateAll((ps) => ps.map((p) => p.dataset.at))).size).toBe(1);
  // The line already said is the same element, never added again, so never read again.
  await expect(region.locator('.sign-in-said-line').first()).toHaveAttribute('data-mark', 'first');
});

test('Not responding\'s row age counts from when the host last heard from the agent, else since, and Details gives the time (ui.md §6.13, #878)', async ({ page }) => {
  await openDemo(page);
  const s = await page.evaluate(() => window.agentBridgeDemo.host.get('stalled'));
  const age = row(page, 'stalled').locator('.row-age');
  await expect(age).toHaveAttribute('datetime', s.attention.lastEventAt);
  await expect(age).toHaveText(/^10\s?m/);
  const clock = (iso) => page.evaluate((t) => new Date(t).toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' }), iso);
  await row(page, 'stalled').click();
  const silent = page.locator('#session-views .session-view[data-id="stalled"] .fact-silent dd');
  await expect(silent).toHaveText(await clock(s.attention.lastEventAt));
  await page.evaluate(() => { const d = window.agentBridgeDemo; const x = d.host.get('stalled'); const { lastEventAt, ...attention } = x.attention; d.upsert({ ...x, attention }); });
  await expect(age).toHaveAttribute('datetime', s.attention.since);
  await expect(silent).toHaveText(await clock(s.attention.since));
  // Any other row counts from its update.
  const quota = await page.evaluate(() => window.agentBridgeDemo.host.get('error-quota').updatedAt);
  await expect(row(page, 'error-quota').locator('.row-age')).toHaveAttribute('datetime', quota);
});

test('two machines\' changes in one update are both said, not the second over the first', async ({ page }) => {
  await openDemo(page, '&machines=2');
  await expect(banner(page)).toHaveCount(2);
  await page.evaluate(() => { const d = window.agentBridgeDemo; d.recoverSignIn(); d.machines['demo-laptop'].recoverSignIn(); });
  // Each is its own line added to the region; neither replaced the other.
  await expect.poll(() => page.locator('#sign-in-said .sign-in-said-line').allTextContents().then((l) => l.slice(-2)), { timeout: 5000 })
    .toEqual(['Copilot on DEV-VM1 recovered by itself', 'Copilot on Laptop recovered by itself']);
});

test('a start result the protocol doesn\'t define is refused: never "Sign in at the machine"', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => window.agentBridgeDemo.scriptSignIns({ outcome: 'machine', reason: 'no device flow' }, { outcome: 'recovered' }));
  await signIn(page).click();
  await expect(sheet(page)).toBeHidden();
  // Read as refused, it has no words of its own: the machine's reason isn't shown.
  await expect(banner(page).locator('.sign-in-line')).toHaveText('Sign-in refused');
  await banner(page).getByRole('button', { name: 'Try again to sign in to Copilot on DEV-VM1' }).click();
  await expect(sheet(page)).toBeHidden();
  await expect(banner(page).locator('.sign-in-line')).toHaveText('Sign-in refused');
  await expect(page.locator('#sign-in-banners')).not.toContainText('at the machine');
});

test('the code is read spelled out from its own text, never from an aria-label on <code>', async ({ page }) => {
  await openDemo(page);
  await signIn(page).click();
  const code = sheet(page).locator('.sign-in-code');
  await expect(code).toHaveText('WDJB-MJHT');
  expect(await code.getAttribute('aria-label')).toBeNull();
  expect(await code.getAttribute('aria-hidden')).toBe('true');
  await expect(sheet(page).locator('.sign-in-code-spelled')).toHaveText('Code: W D J B, M J H T');
});

test('Cancel on a flow joined from another device only closes the sheet, and never cancels that sign-in', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => window.agentBridgeDemo.host.signInElsewhere());
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  await page.keyboard.press('Escape');
  await expect(sheet(page)).toBeHidden();
  await page.waitForTimeout(500);
  expect((await page.evaluate(() => window.agentBridgeDemo.signInLog)).map((l) => l.op)).toEqual(['signin.start']);
});

test('sign-in is said where a phone hears it with a session open, outside the list pane it hides', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openDemo(page);
  await page.locator('#session-list > .session').first().click();
  await expect(page.locator('#list-pane')).toBeHidden();
  await page.evaluate(() => window.agentBridgeDemo.recoverSignIn());
  const said = page.locator('#sign-in-said .sign-in-said-line').last();
  await expect(said).toHaveText('Copilot on DEV-VM1 recovered by itself');
  expect(await said.evaluate((el) => el.checkVisibility() && !el.closest('#list-pane'))).toBe(true);
});

test('a start that fails is said politely once, never as an alert redrawn with every change', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => window.agentBridgeDemo.failSignIns('internal: stack trace'));
  await signIn(page).click();
  await expect(sheet(page)).toBeHidden();
  await expect(page.locator('#sign-in-said .sign-in-said-line').last()).toHaveText('Couldn\u2019t start sign-in for Copilot on DEV-VM1. Try again.');
  const problem = banner(page).locator('.sign-in-problem');
  await expect(problem).toHaveText('Couldn\u2019t start sign-in. Try again.');
  expect(await problem.getAttribute('role')).toBeNull();
  await expect(page.locator('#sign-in-banners [role="alert"]')).toHaveCount(0);
});

test('a sheet closed by its machine going away leaves focus on the Sessions heading, never nowhere', async ({ page }) => {
  await openDemo(page);
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  await page.evaluate(() => window.agentBridgeDemo.setOnline('demo', false));
  await expect(sheet(page)).toBeHidden();
  await expect(page.locator('#sessions-h')).toBeFocused();
});

test('nothing in the page still reaches a code once its machine has gone: not the closed sheet, nor the banner that opened it', async ({ page, browserName }) => {
  test.skip(browserName !== 'chromium', 'a heap snapshot is Chromium\'s (CDP)');
  await openDemo(page);
  // A code no script holds as a literal, so only the page's own copies of it can be found.
  const code = await page.evaluate(() => {
    const pick = () => 'ABCDEFGHJKLMNPQRSTUVWXYZ'[Math.floor(Math.random() * 24)];
    const half = () => Array.from({ length: 4 }, pick).join('');
    const c = `${half()}-${half()}`;
    window.agentBridgeDemo.nextCode = { userCode: c, verificationUri: 'https://github.com/login/device', expiresAt: new Date(Date.now() + 600000).toISOString() };
    return c;
  });
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText(code);
  await page.evaluate(() => window.agentBridgeDemo.setOnline('demo', false));
  await expect(sheet(page)).toBeHidden();
  // The demo machine's own copy goes too: its flow ends, with no channel left to tell.
  await page.evaluate(() => window.agentBridgeDemo.finishSignIn('failed'));
  expect(await page.evaluate(() => window.agentBridgeDemo.signInCodesHeld)).toBe(0);
  const cdp = await page.context().newCDPSession(page);
  const chunks = [];
  cdp.on('HeapProfiler.addHeapSnapshotChunk', (e) => chunks.push(e.chunk));
  await cdp.send('HeapProfiler.collectGarbage');
  await cdp.send('HeapProfiler.takeHeapSnapshot', { reportProgress: false });
  expect(chunks.join('').includes(code), 'the code is still reachable in the page').toBe(false);
});

test('a session button made while a code is shown keeps nothing that reaches it once the code is cancelled', async ({ page, browserName }) => {
  test.skip(browserName !== 'chromium', 'a heap snapshot is Chromium\'s (CDP)');
  await openDemo(page);
  const code = await page.evaluate(() => {
    const pick = () => 'ABCDEFGHJKLMNPQRSTUVWXYZ'[Math.floor(Math.random() * 24)];
    const half = () => Array.from({ length: 4 }, pick).join('');
    const c = `${half()}-${half()}`;
    window.agentBridgeDemo.nextCode = { userCode: c, verificationUri: 'https://github.com/login/device', expiresAt: new Date(Date.now() + 600000).toISOString() };
    return c;
  });
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText(code);
  // A title changes while the code is up: the banner's session buttons are made again.
  await page.evaluate(() => { const d = window.agentBridgeDemo; d.upsert({ ...d.host.get('signin-1'), title: 'Coordinate the Shiproom again' }); });
  await expect(banner(page).getByRole('button', { name: 'Coordinate the Shiproom again' })).toHaveCount(1);
  await page.keyboard.press('Escape');
  await expect(sheet(page)).toBeHidden();
  await page.evaluate(() => window.agentBridgeDemo.finishSignIn('failed'));
  expect(await page.evaluate(() => window.agentBridgeDemo.signInCodesHeld)).toBe(0);
  // The buttons are still the ones made while the code was shown.
  await expect(banner(page).getByRole('button', { name: 'Coordinate the Shiproom again' })).toHaveCount(1);
  const cdp = await page.context().newCDPSession(page);
  const chunks = [];
  cdp.on('HeapProfiler.addHeapSnapshotChunk', (e) => chunks.push(e.chunk));
  await cdp.send('HeapProfiler.collectGarbage');
  await cdp.send('HeapProfiler.takeHeapSnapshot', { reportProgress: false });
  expect(chunks.join('').includes(code), 'the code is still reachable through a session button').toBe(false);
});

test('banners for several machines scroll with the session list, so neither the list nor a lower Sign in is ever off screen', async ({ page }) => {
  await page.setViewportSize({ width: 420, height: 560 });
  await openDemo(page, '&machines=2');
  await expect(banner(page)).toHaveCount(2);
  // One scrolling pane holds the banners, Needs you and the list.
  expect(await page.evaluate(() => document.querySelector('#sign-in-banners').closest('.list-pane') === document.querySelector('#session-list').closest('.list-pane'))).toBe(true);
  const laptop = banner(page).filter({ hasText: 'Copilot on Laptop' }).getByRole('button', { name: 'Sign in to Copilot on Laptop' });
  await laptop.scrollIntoViewIfNeeded();
  await expect(laptop).toBeInViewport();
  const last = page.locator('#session-list > .session').last();
  await last.scrollIntoViewIfNeeded();
  await expect(last).toBeInViewport();
});

test('no code outlives a cancel, its expiry or its machine going away, and a machine going away never reads as recovered', async ({ page }) => {
  await openDemo(page);
  const held = () => page.evaluate(() => window.agentBridgeDemo.signInCodesHeld);
  // Cancelled while the start's result is on its way: the code that came before it goes too.
  await page.evaluate(() => window.agentBridgeDemo.delaySignIns(1500));
  await signIn(page).click();
  await expect.poll(held).toBe(1);
  await page.keyboard.press('Escape');
  await expect(sheet(page)).toBeHidden();
  expect(await held()).toBe(0);
  await page.waitForTimeout(1800);
  expect(await held()).toBe(0);

  // Run out: the sheet says so, and the code is gone from the page and from memory.
  await page.evaluate(() => { window.agentBridgeDemo.nextCode = { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com/login/device', expiresAt: new Date(Date.now() + 2000).toISOString() }; });
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  await expect(sheet(page).locator('.sign-in-expired')).toHaveText('Code expired', { timeout: 10000 });
  await expect(sheet(page).locator('.sign-in-code')).toHaveCount(0);
  await expect(sheet(page).getByRole('button', { name: 'Try again to sign in to Copilot on DEV-VM1' })).toBeVisible();
  expect(await held()).toBe(0);
  await page.keyboard.press('Escape');
  await expect(sheet(page)).toBeHidden();

  // Its machine goes away mid-flow: the sheet closes, the flow and its code go, and nothing says it
  // recovered. Back, the banner offers a press again rather than waiting on a sheet that isn't open.
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  await page.evaluate(() => window.agentBridgeDemo.setOnline('demo', false));
  await expect(sheet(page)).toBeHidden();
  expect(await held()).toBe(0);
  await page.waitForTimeout(500);
  await expect(page.locator('#sign-in-said')).not.toContainText('recovered');
  await page.evaluate(() => window.agentBridgeDemo.setOnline('demo', true));
  await expect(banner(page).locator('.sign-in-start')).toBeVisible({ timeout: 10000 });
  await expect(page.locator('#sign-in-said')).not.toContainText('recovered');
});

test('signed in before the host says pending, focus goes to the banner\'s line and stays there as Sign in goes (#878)', async ({ page }) => {
  await openDemo(page);
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toBeVisible();
  // signin.done alone, the host's pending not yet come.
  await page.evaluate(() => {
    const h = window.agentBridgeDemo.host;
    const f = h.signInFlow;
    h.signInFlow = null;
    h.frame({ ev: 'signin.done', seq: h.seq + 1, data: { flow: f.id, outcome: 'signedIn', resumed: [], continue: [] } });
  });
  await expect(sheet(page)).toBeHidden();
  await expect(banner(page).locator('.sign-in-line')).toBeFocused();
  await setAttention(page, { recovery: 'pending' });
  await expect(banner(page).locator('.sign-in-line')).toHaveText('Signed in \u00b7 waiting for 4 sessions to confirm');
  await expect(banner(page).locator('.sign-in-start')).toHaveCount(0);
  await expect(banner(page).locator('.sign-in-line')).toBeFocused();
});

test('a status line that refuses focus as the sheet closes, as WebKit\'s may, is focused on the next frame, not left for the heading (#878)', async ({ page }) => {
  await openDemo(page);
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toBeVisible();
  // The first focus asked of a status line does nothing, as when WebKit refuses it on an element
  // drawn in the task the modal sheet closed in (CI's pwa-ui webkit at 663c560e and d5342f0c).
  await page.evaluate(() => {
    const focus = HTMLElement.prototype.focus;
    let refused = false;
    HTMLElement.prototype.focus = function (o) {
      if (!refused && this.classList.contains('sign-in-line')) { refused = true; return; }
      return focus.call(this, o);
    };
  });
  await page.evaluate(() => window.agentBridgeDemo.finishSignIn('signedIn'));
  await expect(sheet(page)).toBeHidden();
  await expect(banner(page).locator('.sign-in-line')).toHaveText('Signed in \u00b7 waiting for 4 sessions to confirm');
  await expect(banner(page).locator('.sign-in-line')).toBeFocused();
});

test('signed out, nothing said about the account\'s machines stays in the live region (#878)', async ({ page }) => {
  await openDemo(page);
  await expect(page.locator('#sign-in-said .sign-in-said-line')).toHaveText(['Copilot on DEV-VM1 needs sign-in']);
  await page.evaluate(() => window.agentBridgeDemo.page.signIns.stop());
  await expect(page.locator('#sign-in-said')).toBeEmpty();
});

test('once the person signs in, the sessions wait to confirm, never recovered until the host clears them', async ({ page }) => {
  await openDemo(page);
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toBeVisible();
  await page.evaluate(() => window.agentBridgeDemo.finishSignIn('signedIn'));

  // signin.done alone: the sheet closes, and the host's recovery pending says the rest. Focus goes
  // to that line, as in the app, Sign in having gone with the sheet (#878).
  await expect(sheet(page)).toBeHidden();
  await expect(banner(page).locator('.sign-in-line')).toHaveText('Signed in \u00b7 waiting for 4 sessions to confirm');
  await expect(banner(page).locator('.sign-in-line')).toBeFocused();
  await expect(page.locator('#sign-in-said .sign-in-said-line').last()).toHaveText('Copilot on DEV-VM1: Signed in \u00b7 waiting for 4 sessions to confirm');
  await expect(banner(page).locator('.sign-in-start')).toHaveCount(0);
  for (const id of SESSIONS) { await expect(row(page, id).locator('.row-status')).toHaveText('Signed in \u00b7 waiting for the session to confirm'); }
  await expect(page.locator('#needs-you .needs-entry[data-mode="signIn"]')).toHaveCount(0);
  await expect(banner(page)).not.toContainText('recovered');

  // The model answered: the host clears them, and only now is it recovered.
  await page.evaluate(() => window.agentBridgeDemo.confirmSignIn());
  await expect(banner(page).locator('.sign-in-line')).toHaveText('Signed in \u00b7 4 sessions recovered');
  await expect(page.locator('#sign-in-said .sign-in-said-line').last()).toHaveText('Copilot on DEV-VM1: Signed in \u00b7 4 sessions recovered');
  for (const id of SESSIONS) { await expect(row(page, id).locator('.row-status')).toHaveText('Idle'); }
  // A minute on, with nothing from the machine, it has gone.
  await page.evaluate(() => { const real = Date.now; Date.now = () => real() + 61000; window.agentBridgeDemo.page.signIns.render(); });
  await expect(banner(page)).toHaveCount(0);
});

test('a session that fails sign-in again while confirming goes straight back to Needs sign-in', async ({ page }) => {
  await openDemo(page);
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toBeVisible();
  await page.evaluate(() => window.agentBridgeDemo.finishSignIn('signedIn'));
  await expect(banner(page).locator('.sign-in-line')).toHaveText('Signed in \u00b7 waiting for 4 sessions to confirm');
  await page.evaluate(() => window.agentBridgeDemo.failSignInAgain());
  await expect(banner(page).locator('.sign-in-head')).toHaveText('Copilot on DEV-VM1 needs sign-in \u00b7 @demo-user');
  await expect(banner(page).locator('.sign-in-line')).toHaveText('4 sessions affected');
  await expect(signIn(page)).toBeVisible();
  for (const id of SESSIONS) { await expect(row(page, id).locator('.row-status')).toHaveText('Needs sign-in'); }
  await expect(page.locator('#needs-you .needs-entry[data-mode="signIn"]')).toHaveCount(4);
});

test('a stored sign-in that still worked reads "recovered by itself", with nobody pressing anything', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => window.agentBridgeDemo.recoverSignIn());
  await expect(banner(page).locator('.sign-in-line')).toHaveText('Recovered by itself');
  await expect(page.locator('#sign-in-said .sign-in-said-line').last()).toHaveText('Copilot on DEV-VM1 recovered by itself');
  expect(await page.evaluate(() => window.agentBridgeDemo.signInLog)).toEqual([]);
});

test('a second recovery within the minute gets its own words and count, not the first one\'s', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => window.agentBridgeDemo.recoverSignIn());
  await expect(banner(page).locator('.sign-in-line')).toHaveText('Recovered by itself');
  // A new episode: needed again, then a person here signs in and the host clears them.
  await page.evaluate(() => window.agentBridgeDemo.needSignInAgain());
  await expect(banner(page).locator('.sign-in-line')).toHaveText('4 sessions affected');
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toBeVisible();
  await page.evaluate(() => window.agentBridgeDemo.finishSignIn('signedIn'));
  await expect(banner(page).locator('.sign-in-line')).toHaveText('Signed in \u00b7 waiting for 4 sessions to confirm');
  await page.evaluate(() => window.agentBridgeDemo.confirmSignIn());
  await expect(banner(page).locator('.sign-in-line')).toHaveText('Signed in \u00b7 4 sessions recovered');
});

test('a sign-in that fails says so, never why in the machine\'s words, and offers Try again', async ({ page }) => {
  await openDemo(page);
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toBeVisible();
  await page.evaluate(() => window.agentBridgeDemo.finishSignIn('failed', 'The code was declined.'));

  await expect(sheet(page)).toBeHidden();
  // signin.done carries no reason in the protocol: the host's words are never shown.
  await expect(banner(page).locator('.sign-in-line')).toHaveText('Sign-in failed');
  await expect(banner(page).getByRole('button', { name: 'Try again to sign in to Copilot on DEV-VM1' })).toBeVisible();
  await expect(page.locator('#sign-in-said .sign-in-said-line').last()).toHaveText('Sign-in failed for Copilot on DEV-VM1');
});

test('only the host saying signIn: machine makes it "sign in at the machine"', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const s = d.host.get('signin-1');
    d.upsert({ ...s, attention: { ...s.attention, signIn: 'machine', since: new Date().toISOString() } });
  });
  await expect(banner(page).locator('.sign-in-line')).toHaveText('Sign in at the machine');
  await expect(banner(page).locator('.sign-in-start')).toHaveCount(0);
});

test('a machine that fails to start the sign-in is not quoted, and Sign in stays', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => window.agentBridgeDemo.failSignIns('<a href="https://evil.example">click</a>'));
  await signIn(page).click();
  await expect(banner(page).locator('.sign-in-problem')).toHaveText('Couldn\u2019t start sign-in. Try again.');
  await expect(page.locator('a[href^="https://evil"]')).toHaveCount(0);
  await expect(signIn(page)).toBeVisible();
});

for (const [what, code] of [
  ['an http link', { verificationUri: 'http://github.com/login/device' }],
  ['another host', { verificationUri: 'https://evil.example/login/device' }],
  ['a lookalike', { verificationUri: 'https://github.com.evil.example/login/device' }],
  ['a userinfo', { verificationUri: 'https://github.com@evil.example/login/device' }],
  ['an IDN', { verificationUri: 'https://g\u0456thub.com/login/device' }],
  ['a code in another shape', { userCode: 'wdjb mjht' }],
  ['a code with a hidden character', { userCode: 'WDJB-\u200BMJHT' }],
  // Date.parse would take these; the protocol's timestamps are RFC 3339 alone.
  ['an expiry with a comment', { expiresAt: `Fri, 01 Jan 2099 00:00:00 GMT (${['gho', '16C7e42F292c6912E7710c838347Ae178B4a'].join('_')})` }],
  ['an expiry that isn\'t a time', { expiresAt: 'soon' }],
]) {
  test(`a signin.code with ${what} is refused: no link, no code, and Open is not offered`, async ({ page }) => {
    await openDemo(page);
    await page.evaluate((c) => { window.agentBridgeDemo.nextCode = { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com/login/device', expiresAt: new Date(Date.now() + 600000).toISOString(), ...c }; }, code);
    await signIn(page).click();
    await expect(sheet(page).locator('.sign-in-refused')).toHaveText('This sign-in code couldn\u2019t be shown.');
    await expect(sheet(page).getByRole('button', { name: 'Try again to sign in to Copilot on DEV-VM1' })).toBeVisible();
    await expect(page.locator('.sign-in-code, .sign-in-open, .sign-in-copy')).toHaveCount(0);
    const html = await page.evaluate(() => document.body.outerHTML);
    expect(html).not.toContain('evil.example');
    expect(html).not.toContain('g\u0456thub');
    expect(html).not.toContain('WDJB');
    expect(html).not.toContain('gho_');
    expect(await page.locator('dialog.sign-in-sheet a, #sign-in-banners a').count()).toBe(0);
  });
}

test('no token the host sends beside the code, in an account or in a field the client doesn\'t read, ever reaches the page', async ({ page }) => {
  await openDemo(page);
  const token = ['gho', '16C7e42F292c6912E7710c838347Ae178B4a'].join('_');
  await page.evaluate((t) => {
    const d = window.agentBridgeDemo;
    d.nextCode = { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com/login/device', expiresAt: new Date(Date.now() + 600000).toISOString(), accessToken: t, deviceCode: t, refreshToken: t };
    const s = d.host.get('error-quota');
    d.upsert({ ...s, attention: { ...s.attention, account: t, token: t }, token: t });
  }, token);
  await signIn(page).click();
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  await page.keyboard.press('Escape');
  await row(page, 'error-quota').click();
  await expect(page.locator('#session-views .session-view[data-id="error-quota"]')).toBeVisible();
  const html = await page.evaluate(() => document.documentElement.outerHTML);
  expect(html).not.toContain('gho_');
  expect(html).not.toContain(token);
});

test('the banner\'s and the sheet\'s names are the words shown, and a tap draws no focus ring', async ({ browser, browserName }) => {
  const context = await browser.newContext({ hasTouch: true });
  const page = await context.newPage();
  await openDemo(page);
  const rules = ['label-content-name-mismatch', 'button-name', 'link-name', 'region'];
  expect((await new AxeBuilder({ page }).include('#sign-in-banners').withRules(rules).analyze()).violations).toEqual([]);
  await signIn(page).tap();
  const cancel = sheet(page).getByRole('button', { name: 'Cancel sign-in for Copilot on DEV-VM1' });
  await expect(cancel).toBeFocused();
  // Focused by script with focusVisible: false. WebKit's :focus-visible for a dialog's script focus
  // is the engine's own guess (keyboard.spec.mjs), so it is held to this only elsewhere.
  if (browserName !== 'webkit') { expect(await cancel.evaluate((e) => e.matches(':focus-visible'))).toBe(false); }
  await expect(sheet(page).locator('.sign-in-code')).toBeVisible();
  expect((await new AxeBuilder({ page }).include('dialog.sign-in-sheet').withRules(['label-content-name-mismatch', 'button-name', 'link-name', 'aria-dialog-name']).analyze()).violations).toEqual([]);
  await context.close();
});

// The captures owed before sessions.signin is turned on (#800, Creative 6655285995, and #895
// 6660406957), the app's beside them in its self-test: every state of the banner and the sheet, in
// Light, Dark and forced colours. They are the banner as it first comes, a Needs sign-in row, a
// signIn this client doesn't know (no claim, nothing to press), a sign-in under way from another
// device (Show code, the sheet closed), the sheet with its code and the banner behind it, Failed,
// an expired code, and signed in waiting for the sessions to confirm. Each is checked to be what it
// says before it is taken, and attached to the test's results. In Chromium: WebKit draws a
// screenshot with a style the CSP refuses, and the states are proven in every browser above.
async function capture(locator, name, testInfo) {
  const path = testInfo.outputPath(name);
  await locator.screenshot({ path });
  await testInfo.attach(name, { path, contentType: 'image/png' });
}

// The banner while its sheet shows the code: the modal sheet's backdrop covers it (opaque in forced
// colours), so for the picture the sheet is made modeless and unseen, never cancelled (close() fires
// no cancel), then modal again.
async function captureUnderSheet(page, name, testInfo) {
  await sheet(page).evaluate((d) => { d.close(); d.show(); d.style.visibility = 'hidden'; });
  await capture(banner(page), name, testInfo);
  await sheet(page).evaluate((d) => { d.style.visibility = ''; d.close(); d.showModal(); });
  await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
  expect(await page.evaluate(() => window.agentBridgeDemo.signInLog.filter((l) => l.op === 'signin.cancel'))).toEqual([]);
}

async function setAttention(page, change) {
  await page.evaluate(({ ids, change }) => {
    const d = window.agentBridgeDemo;
    for (const id of ids) {
      const s = d.host.get(id);
      const attention = { ...s.attention, ...change };
      for (const [k, v] of Object.entries(change)) { if (v === null) { delete attention[k]; } }
      d.upsert({ ...s, attention });
    }
  }, { ids: SESSIONS, change });
}

for (const look of [
  { name: 'light', suffix: '', media: { colorScheme: 'light' } },
  { name: 'dark', suffix: '.dark', media: { colorScheme: 'dark' } },
  { name: 'forced colours', suffix: '.forced-colors', media: { colorScheme: 'light', forcedColors: 'active' } },
]) {
  test(`captures every state of the banner and the sheet, and a Needs sign-in row, in ${look.name} (#800)`, async ({ page, browserName }, testInfo) => {
    test.skip(browserName !== 'chromium', 'captured in Chromium: WebKit draws a screenshot with a style the CSP refuses');
    const name = (what) => `signin-${what}${look.suffix}.png`;
    await page.emulateMedia(look.media);
    await openDemo(page);
    await expect(signIn(page)).toBeVisible();
    await capture(banner(page), name('banner'), testInfo);
    await expect(row(page, 'signin-1').locator('.row-status')).toHaveText('Needs sign-in');
    await capture(row(page, 'signin-1'), name('row'), testInfo);

    // A way to sign in this client doesn't know: no claim, so nothing to press, never "at the machine".
    await setAttention(page, { signIn: 'rdp' });
    await expect(banner(page).locator('.sign-in-start')).toHaveCount(0);
    await expect(banner(page).locator('.sign-in-line')).toHaveText('4 sessions affected');
    await expect(banner(page)).not.toContainText('at the machine');
    await capture(banner(page), name('no-claim'), testInfo);
    await setAttention(page, { signIn: 'phone' });

    // A sign-in another device runs: Show code, the sheet closed here.
    await setAttention(page, { recovery: 'signingIn' });
    await expect(banner(page).getByRole('button', { name: 'Show code for Copilot on DEV-VM1' })).toBeVisible();
    await expect(banner(page).locator('.sign-in-line')).toHaveText('Waiting for sign-in');
    await expect(sheet(page)).toBeHidden();
    await capture(banner(page), name('show-code'), testInfo);
    await setAttention(page, { recovery: null });

    await signIn(page).click();
    await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
    await expect(banner(page).locator('.sign-in-line')).toHaveText(/^Waiting for sign-in · code expires/);
    await capture(sheet(page), name('sheet'), testInfo);
    await captureUnderSheet(page, name('code-shown'), testInfo);

    // The demo's done carries no reason, as the protocol's signin.done has none (#834, #841).
    await page.evaluate(() => window.agentBridgeDemo.finishSignIn('failed'));
    await expect(sheet(page)).toBeHidden();
    await expect(banner(page).locator('.sign-in-line')).toHaveText('Sign-in failed');
    const again = banner(page).getByRole('button', { name: 'Try again to sign in to Copilot on DEV-VM1' });
    await expect(again).toBeVisible();
    await capture(banner(page), name('failed'), testInfo);

    await page.evaluate(() => { window.agentBridgeDemo.nextCode = { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com/login/device', expiresAt: new Date(Date.now() + 1500).toISOString() }; });
    await again.click();
    await expect(sheet(page).locator('.sign-in-expired')).toHaveText('Code expired', { timeout: 8000 });
    await expect(sheet(page)).not.toContainText('WDJB');
    await capture(sheet(page), name('expired'), testInfo);
    await page.keyboard.press('Escape');
    await expect(sheet(page)).toBeHidden();

    // Cancelled, the expired attempt is gone: Sign in again.
    await page.evaluate(() => { window.agentBridgeDemo.nextCode = { userCode: 'WDJB-MJHT', verificationUri: 'https://github.com/login/device', expiresAt: new Date(Date.now() + 600000).toISOString() }; });
    await signIn(page).click();
    await expect(sheet(page).locator('.sign-in-code')).toHaveText('WDJB-MJHT');
    await page.evaluate(() => window.agentBridgeDemo.finishSignIn('signedIn'));
    await expect(banner(page).locator('.sign-in-line')).toHaveText('Signed in \u00b7 waiting for 4 sessions to confirm');
    await capture(banner(page), name('confirming'), testInfo);
  });
}
