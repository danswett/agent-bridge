// Replying to a session from the phone, in demo mode: the simulated host checks replies as
// a real one does (Replies.Check), refuses them while the session asks a question and for
// agents it can't reply to yet, keeps each request's first outcome, and shows a typed reply
// as the session's latest message. Every outcome, the text kept or taken out, a resend
// after a lost response, the box held while a question is pending, and the keyboard.
import { test, expect } from '@playwright/test';

const ASKING = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const CODEX = '019a1b2c-codex-example';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

const card = (page, id) => page.locator(`.session[data-id="${id}"]`);
const box = (page, id) => card(page, id).locator('form.reply');
const later = () => new Date(Date.now() + 1000).toISOString();
const log = (page) => page.evaluate(() => window.agentBridgeDemo.host.replyLog);
const typed = (page) => page.evaluate(() => window.agentBridgeDemo.host.typedReplies);
const script = (page, ...outcomes) => page.evaluate((o) => window.agentBridgeDemo.scriptReplies(...o), outcomes);

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

  test('a reply is sent, shows its progress, is delivered as one line, and leaves the box empty', async ({ page }) => {
    await openDemo(page);
    const reply = box(page, IDLE);
    const input = reply.getByRole('textbox', { name: 'Reply' });
    await expect(input).toBeEnabled();
    await expect(reply.getByRole('button', { name: 'Send' })).toBeDisabled();

    await input.fill('Looks good.\nNow open the PR – please 🚀');
    // Several lines are allowed in the box, and it says how they arrive.
    await expect(reply.locator('.reply-hint')).toHaveText('Line breaks are sent as spaces: the session gets your reply as one line.');
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply.locator('.reply-status')).toHaveText('Sending…');
    await expect(reply).toHaveAttribute('aria-busy', 'true');
    await expect(reply.getByRole('button', { name: 'Send' })).toBeDisabled();

    await expect(reply.locator('.reply-status')).toHaveText('Delivered. The session has your reply.');
    await expect(reply).toHaveAttribute('data-reply', 'delivered');
    await expect(input).toHaveValue('');
    expect(await log(page)).toEqual([{ id: IDLE, request: expect.stringMatching(UUID), text: 'Looks good.\nNow open the PR – please 🚀' }]);
    expect((await typed(page)).map((t) => t.said)).toEqual(['Looks good. Now open the PR – please 🚀']);
    // The host's upsert: the session is working on it, and it is your latest message.
    await expect(card(page, IDLE)).toHaveAttribute('data-status', 'working');
    await expect(card(page, IDLE).locator('.user-message .plain')).toHaveText('Looks good. Now open the PR – please 🚀');
  });

  test('words typed while a reply is on its way stay in the box once it is delivered', async ({ page }) => {
    await openDemo(page, '&answerDelay=1500');
    const reply = box(page, IDLE);
    const input = reply.getByRole('textbox', { name: 'Reply' });
    await input.fill('first');
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply.locator('.reply-status')).toHaveText(/^Sending…/);
    await input.press('End');
    await input.pressSequentially(' and then');
    await expect(reply.locator('.reply-status')).toHaveText('Delivered. The session has your reply.');
    await expect(input).toHaveValue('and then');
    expect((await log(page)).map((r) => r.text)).toEqual(['first']);
  });

  test('a refusal keeps the words and says why: an agent the machine cannot reply to yet', async ({ page }) => {
    await openDemo(page);
    const reply = box(page, CODEX);
    await reply.getByRole('textbox', { name: 'Reply' }).fill('please rerun');
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply.locator('.reply-status')).toHaveText('The machine refused this reply: replying is not supported for codex yet; reply at the machine.');
    await expect(reply).toHaveAttribute('data-reply', 'refused');
    await expect(reply.getByRole('textbox', { name: 'Reply' })).toHaveValue('please rerun');
    await expect(reply.getByRole('button', { name: 'Send' })).toBeEnabled();
    expect(await typed(page)).toEqual([]);
  });

  test('while the session asks a question the box is held and says to answer first, keeping the words', async ({ page }) => {
    await openDemo(page);
    const reply = box(page, ASKING);
    const input = reply.getByRole('textbox', { name: 'Reply' });
    await expect(input).toBeDisabled();
    await expect(reply.locator('.reply-held')).toHaveText('The session is waiting for an answer to its question. Answer that first, then reply.');
    await expect(reply.getByRole('button', { name: 'Send' })).toBeDisabled();

    // A question arriving takes the box away from words already typed, and gives it back.
    const other = box(page, IDLE);
    await other.getByRole('textbox', { name: 'Reply' }).fill('half a thought');
    await page.evaluate(({ id, t }) => {
      const d = window.agentBridgeDemo;
      d.upsert({ ...d.host.get(id), status: 'waiting', statusSince: t, updatedAt: t, question: { id: 'q-new', kind: 'form', message: 'Which?', fields: [], askedAt: t } });
    }, { id: IDLE, t: later() });
    await expect(other.getByRole('textbox', { name: 'Reply' })).toBeDisabled();
    await expect(other.getByRole('textbox', { name: 'Reply' })).toHaveValue('half a thought');
    await page.evaluate(({ id, t }) => {
      const d = window.agentBridgeDemo;
      d.upsert({ ...d.host.get(id), status: 'idle', statusSince: t, updatedAt: t, question: null });
    }, { id: IDLE, t: later() });
    await expect(other.getByRole('textbox', { name: 'Reply' })).toBeEnabled();
    await expect(other.getByRole('button', { name: 'Send' })).toBeEnabled();
    await expect(other.locator('.reply-held')).toBeHidden();
    expect(await log(page)).toEqual([]);
  });

  test('a command, an over-long reply or an empty one is caught before it is sent', async ({ page }) => {
    await openDemo(page);
    const reply = box(page, IDLE);
    const input = reply.getByRole('textbox', { name: 'Reply' });
    await input.fill('  /clear');
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply.getByRole('alert')).toHaveText('A reply cannot start with /, which the agent would run as a command; type commands at the machine.');
    await expect(input).toBeFocused();
    await input.fill('界'.repeat(5462));
    await expect(reply.locator('.bytes')).toHaveText('Too long: 16.0 KiB of 16 KiB');
    await expect(input).toHaveAttribute('aria-invalid', 'true');
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply.getByRole('alert')).toHaveText('The reply is longer than 16 KiB.');
    await input.fill('   ');
    await expect(reply.getByRole('button', { name: 'Send' })).toBeDisabled();
    expect(await log(page)).toEqual([]);
  });

  test('every reply control is at least 44 px tall', async ({ page }) => {
    await openDemo(page);
    await box(page, IDLE).getByRole('textbox', { name: 'Reply' }).fill('x');
    const sizes = await box(page, IDLE).evaluate((f) => [...f.querySelectorAll('button:not([hidden]), textarea')]
      .map((e) => ({ what: e.className || e.tagName, h: e.getBoundingClientRect().height })));
    expect(sizes.length).toBe(2);
    for (const s of sizes) { expect(s.h, s.what).toBeGreaterThanOrEqual(44); }
  });
});

test.describe('outcomes', () => {
  test.use({ viewport: { width: 390, height: 664 } });

  test('unconfirmed keeps the words and says to check at the machine before sending again', async ({ page }) => {
    await openDemo(page);
    await script(page, { outcome: 'unconfirmed', reason: 'the session did not record the reply within 60 seconds' });
    const reply = box(page, IDLE);
    await reply.getByRole('textbox', { name: 'Reply' }).fill('did this land?');
    await reply.getByRole('button', { name: 'Send' }).click();
    await expect(reply.locator('.reply-status')).toHaveText('Sent, but not confirmed. Check at the machine on Demo machine before sending it again. (the session did not record the reply within 60 seconds)');
    await expect(reply.locator('.reply-status')).toHaveClass(/\bwarn\b/);
    await expect(reply.getByRole('textbox', { name: 'Reply' })).toHaveValue('did this land?');
  });

  test('an outcome from a newer host reads as unconfirmed', async ({ page }) => {
    await openDemo(page);
    await script(page, { outcome: 'queued', reason: 'later' });
    const reply = box(page, IDLE);
    await reply.getByRole('textbox', { name: 'Reply' }).fill('go');
    await reply.getByRole('button', { name: 'Send' }).click();
    await expect(reply).toHaveAttribute('data-reply', 'unconfirmed');
  });

  test('busy and stale keep the words; after busy Send comes back in a moment', async ({ page }) => {
    await openDemo(page);
    await script(page, 'busy', 'stale');
    const reply = box(page, IDLE);
    await reply.getByRole('textbox', { name: 'Reply' }).fill('next step');
    await reply.getByRole('button', { name: 'Send' }).click();
    await expect(reply.locator('.reply-status')).toHaveText('Not sent: an answer or another reply to this session is being delivered. Try again in a moment.');
    await expect(reply.getByRole('button', { name: 'Send' })).toBeDisabled();
    await expect(reply.getByRole('button', { name: 'Send' })).toBeEnabled({ timeout: 4000 });
    await reply.getByRole('button', { name: 'Send' }).click();
    await expect(reply.locator('.reply-status')).toHaveText('Not sent: the session has ended.');
    await expect(reply.getByRole('textbox', { name: 'Reply' })).toHaveValue('next step');
    const sent = await log(page);
    expect(sent).toHaveLength(2);
    expect(sent[1].request).not.toBe(sent[0].request);
  });

  test('a reply whose response is lost is resent with the same request and typed once', async ({ page }) => {
    await openDemo(page, '&timeout=800&retry=100');
    await script(page, { outcome: 'delivered', drop: true });
    const reply = box(page, IDLE);
    await reply.getByRole('textbox', { name: 'Reply' }).fill('only once');
    await reply.getByRole('button', { name: 'Send' }).click();
    await expect(reply.locator('.reply-status')).toHaveText('Delivered. The session has your reply.', { timeout: 10000 });
    const sent = await log(page);
    expect(sent.length).toBeGreaterThanOrEqual(2);
    expect(new Set(sent.map((r) => r.request)).size).toBe(1);
    expect(await typed(page)).toHaveLength(1);
  });
});

test.describe('from a keyboard', () => {
  test('Ctrl+Enter or Cmd+Enter sends, and Enter alone is a new line', async ({ page }) => {
    await openDemo(page);
    const input = box(page, IDLE).getByRole('textbox', { name: 'Reply' });
    await input.focus();
    await page.keyboard.type('line one');
    await page.keyboard.press('Enter');
    await page.keyboard.type('line two');
    expect(await log(page)).toEqual([]);
    await page.keyboard.press('ControlOrMeta+Enter');
    await expect(box(page, IDLE)).toHaveAttribute('data-reply', 'delivered');
    expect((await log(page)).map((r) => r.text)).toEqual(['line one\nline two']);
  });
});

test('a machine that does not take replies shows no reply box', async ({ page }) => {
  await openDemo(page, '&reply=0');
  await expect(card(page, IDLE)).toBeVisible();
  await expect(page.locator('form.reply')).toHaveCount(0);
  // It still takes answers.
  expect(await page.evaluate(() => window.agentBridgeDemo.host.capabilities)).toEqual(['sessions.read', 'sessions.answer']);
});
