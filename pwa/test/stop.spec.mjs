// Ending a session from the phone, in demo mode: the simulated host ends a session as a real
// one does (session.stop): an idle session on one press; one that is working, waiting on you
// or waiting for background work it started only on a second press that carries the token
// the first was given, within the window. Then it shows as ending, and its card goes.
import { test, expect } from '@playwright/test';

const CLAUDE = '6f1c2b7e-claude-example';
const CODEX = '019a1b2c-codex-example';
const DELEGATING = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

const card = (page, id) => page.locator(`.session[data-id="${id}"]`);
const footer = (page, id) => card(page, id).locator('.end-session');
const end = (page, id) => footer(page, id).getByRole('button', { name: 'End session' });
const log = (page) => page.evaluate(() => window.agentBridgeDemo.host.stopLog);
const script = (page, ...outcomes) => page.evaluate((o) => window.agentBridgeDemo.scriptStops(...o), outcomes);

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1&answerDelay=300${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });

  test('an idle session ends on one press: it shows as ending, then its card goes', async ({ page }) => {
    await openDemo(page, '&answerDelay=800');
    await end(page, CODEX).tap();
    await expect(card(page, CODEX)).toHaveAttribute('data-status', 'ending');
    await expect(card(page, CODEX).locator('.status-text')).toHaveText('Ending session…');
    await expect(end(page, CODEX)).toBeDisabled();
    await expect(card(page, CODEX)).toHaveCount(0);
    const sent = await log(page);
    expect(sent).toEqual([{ id: CODEX, request: expect.stringMatching(UUID) }]);
  });

  test('a working session takes two presses: the first asks for the second and ends nothing', async ({ page }) => {
    await openDemo(page);
    await end(page, CLAUDE).tap();
    await expect(footer(page, CLAUDE).locator('.stop-status')).toHaveText(/^Press End session again to end it: this session is still working; the confirmation lapses in 10 s\. (10|9|8) s left\.$/);
    await expect(footer(page, CLAUDE)).toHaveAttribute('data-stop', 'confirm');
    await expect(card(page, CLAUDE)).toHaveAttribute('data-status', 'working');

    await end(page, CLAUDE).tap();
    await expect(card(page, CLAUDE)).toHaveCount(0);
    const [first, second] = await log(page);
    expect(first.confirm).toBeUndefined();
    expect(second.confirm).toMatch(/^[0-9a-f]{32}$/);
    expect(second.request).not.toBe(first.request);
  });

  test('a second press after the window lapsed is a first press again, and the card says it was not confirmed', async ({ page }) => {
    await openDemo(page, '&stopWindow=1500');
    await end(page, CLAUDE).tap();
    await expect(footer(page, CLAUDE)).toHaveAttribute('data-stop', 'confirm');
    await expect(footer(page, CLAUDE).locator('.stop-status')).toHaveText('End session NOT confirmed: no second press within 2 s. The session is still running.', { timeout: 5000 });
    await expect(footer(page, CLAUDE)).toHaveAttribute('data-stop', 'lapsed');

    await end(page, CLAUDE).tap();
    await expect(footer(page, CLAUDE)).toHaveAttribute('data-stop', 'confirm');
    await expect(card(page, CLAUDE)).toHaveAttribute('data-status', 'working');
    expect((await log(page)).map((s) => s.confirm)).toEqual([undefined, undefined]);
  });

  test('a session waiting for background agents it started takes two presses too', async ({ page }) => {
    await openDemo(page);
    await end(page, DELEGATING).tap();
    await expect(footer(page, DELEGATING).locator('.stop-status')).toHaveText(/^Press End session again to end it: this session is waiting for background agents it started;/);
    await expect(card(page, DELEGATING)).toBeVisible();
  });

  test('End session sits last on the card, apart from Send, and is at least 44 px tall', async ({ page }) => {
    await openDemo(page);
    const last = await card(page, CODEX).locator('.body').evaluate((b) => b.lastElementChild.className);
    expect(last).toBe('stop-slot');
    const box = await end(page, CODEX).boundingBox();
    expect(box.height).toBeGreaterThanOrEqual(44);
  });
});

test.describe('outcomes', () => {
  test.use({ viewport: { width: 390, height: 664 } });

  test('a refusal or a failure says why and the card stays', async ({ page }) => {
    await openDemo(page);
    await script(page, 'refused', 'failed');
    await end(page, CODEX).click();
    await expect(footer(page, CODEX).locator('.stop-status')).toHaveText('The machine refused to end this session: the session runs as administrator, and the host does not end elevated sessions; end it at the machine.');
    await expect(footer(page, CODEX).locator('.stop-status')).toHaveClass(/\bbad\b/);
    await end(page, CODEX).click();
    await expect(footer(page, CODEX).locator('.stop-status')).toHaveText("Couldn't end the session: the host could not terminate the session's process (Access is denied.).");
    await expect(card(page, CODEX)).toHaveAttribute('data-status', 'idle');
  });

  test('an outcome from a newer host reads as failed, never as ended', async ({ page }) => {
    await openDemo(page);
    await script(page, { outcome: 'paused', reason: 'later' });
    await end(page, CODEX).click();
    await expect(footer(page, CODEX)).toHaveAttribute('data-stop', 'failed');
    await expect(card(page, CODEX)).toBeVisible();
  });

  test('a press whose response is lost is resent with the same request, and the session ends once', async ({ page }) => {
    await openDemo(page, '&timeout=800&retry=100');
    await script(page, { outcome: 'ended', drop: true });
    await end(page, CODEX).click();
    await expect(card(page, CODEX)).toHaveCount(0, { timeout: 10000 });
    const sent = await log(page);
    expect(sent.length).toBeGreaterThanOrEqual(1);
    expect(new Set(sent.map((s) => s.request)).size).toBe(1);
  });
});

test('a machine that cannot end sessions shows no End session', async ({ page }) => {
  await openDemo(page, '&stop=0');
  await expect(card(page, CODEX)).toBeVisible();
  await expect(page.locator('.end-session')).toHaveCount(0);
});
