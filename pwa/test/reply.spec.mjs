// Replying to a session from the phone, in demo mode: the simulated host checks replies as
// a real one does (Replies.Check), refuses them while the session asks a question and for
// agents it can't reply to yet, keeps each request's first outcome, and shows a typed reply
// as the session's latest message. Every outcome, the text kept or taken out, a resend
// after a lost response, the box held while a question is pending, and the keyboard.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const ASKING = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const CODEX = '019a1b2c-codex-example';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

// A session's view (#179), where its reply box is.
const card = (page, id) => view(page, id);
const box = (page, id) => card(page, id).locator('form.reply');
const later = () => new Date(Date.now() + 1000).toISOString();
const log = (page) => page.evaluate(() => window.agentBridgeDemo.host.replyLog);
const typed = (page) => page.evaluate(() => window.agentBridgeDemo.host.typedReplies);
const script = (page, ...outcomes) => page.evaluate((o) => window.agentBridgeDemo.scriptReplies(...o), outcomes);
// Keeps the host's responses back while a test looks at a reply on its way: the answer delay
// alone was shorter than a busy CI runner's WebKit took to check it.
const hold = (page) => page.evaluate(() => window.agentBridgeDemo.host.holdResponses());
const release = (page) => page.evaluate(() => window.agentBridgeDemo.host.releaseResponses());

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1&answerDelay=300${query}`);
  await page.waitForSelector('html[data-demo-ready]');
  // These are about the idle session's reply box: its session is opened, as a person would.
  await open(page, IDLE);
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
    await hold(page);
    await reply.getByRole('button', { name: 'Send' }).tap();
    // On its way: heard, and Send's ring; nothing on the line (ui.md §6.5).
    await expect(reply.locator('.receipt-live')).toHaveText('Sending reply');
    await expect(reply.locator('.reply-status')).toHaveText('');
    await expect(reply.getByRole('button', { name: 'Send' })).toHaveAttribute('aria-busy', 'true');
    await expect(reply.getByRole('button', { name: 'Send' })).toBeDisabled();
    await release(page);

    await expect(reply.locator('.receipt-live')).toHaveText('Reply delivered');
    await expect(reply).toHaveAttribute('data-reply', 'delivered');
    await expect(input).toHaveValue('');
    expect(await log(page)).toEqual([{ id: IDLE, request: expect.stringMatching(UUID), text: 'Looks good.\nNow open the PR – please 🚀', progress: true }]);
    expect((await typed(page)).map((t) => t.said)).toEqual(['Looks good. Now open the PR – please 🚀']);
    // The host's upsert: the session is working on it, and it is your latest message.
    await expect(card(page, IDLE)).toHaveAttribute('data-status', 'working');
    await expect(card(page, IDLE).locator('.user-message .plain')).toHaveText('Looks good. Now open the PR – please 🚀');
  });

  test('words typed while a reply is on its way stay in the box once it is delivered', async ({ page }) => {
    await openDemo(page);
    const reply = box(page, IDLE);
    const input = reply.getByRole('textbox', { name: 'Reply' });
    await input.fill('first');
    await hold(page);
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply.getByRole('button', { name: 'Send' })).toHaveAttribute('aria-busy', 'true');
    // The caret to the end by hand: WebKit on Windows emptied the box on End.
    await input.evaluate((el) => el.setSelectionRange(el.value.length, el.value.length));
    await input.pressSequentially(' and then');
    await release(page);
    await expect(reply).toHaveAttribute('data-reply', 'delivered');
    await expect(input).toHaveValue('and then');
    expect((await log(page)).map((r) => r.text)).toEqual(['first']);
  });

  test('a refusal keeps the words and says why: an agent the machine cannot reply to yet', async ({ page }) => {
    await openDemo(page);
    await open(page, CODEX);
    const reply = box(page, CODEX);
    await reply.getByRole('textbox', { name: 'Reply' }).fill('please rerun');
    await reply.getByRole('button', { name: 'Send' }).tap();
    await expect(reply.locator('.reply-status')).toHaveText('The machine refused this reply: replying is not supported for codex yet; reply at the machine.');
    await expect(reply).toHaveAttribute('data-reply', 'refused');
    // A failure stops what the person is doing: said assertively, on the page's alert line, and only
    // there (ui.md §9.1 A6).
    await expect(page.locator('.receipt-alert')).toHaveText('Reply: The machine refused this reply: replying is not supported for codex yet; reply at the machine.');
    await expect(page.locator('.receipt-alert')).toHaveAttribute('role', 'alert');
    await expect(reply.locator('.receipt-live')).not.toHaveText(/refused/);
    await expect(reply.getByRole('textbox', { name: 'Reply' })).toHaveValue('please rerun');
    await expect(reply.getByRole('button', { name: 'Send' })).toBeEnabled();
    expect(await typed(page)).toEqual([]);
  });

  test('while the session asks a question the box is held and says to answer first, keeping the words', async ({ page }) => {
    await openDemo(page);
    await open(page, ASKING);
    const reply = box(page, ASKING);
    const input = reply.getByRole('textbox', { name: 'Reply' });
    await expect(input).toBeDisabled();
    // On a phone the notice is one line, with a link to the question (ui.md §6.5).
    await expect(reply.locator('.reply-held')).toHaveText('Answer its question first. Go to the question');
    await expect(reply.getByRole('button', { name: 'Send' })).toBeDisabled();

    // A question arriving takes the box away from words already typed, and gives it back.
    await open(page, IDLE);
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
    // The box, Send and Attach.
    expect(sizes.length).toBe(3);
    for (const s of sizes) { expect(s.h, s.what).toBeGreaterThanOrEqual(44); }
  });
});

test.describe('outcomes', () => {
  test.use({ viewport: { width: 390, height: 664 } });

  test('unconfirmed keeps the words and says to check the conversation before sending again', async ({ page }) => {
    await openDemo(page);
    await script(page, { outcome: 'unconfirmed', reason: 'the session did not record the reply within 60 seconds' });
    const reply = box(page, IDLE);
    await reply.getByRole('textbox', { name: 'Reply' }).fill('did this land?');
    await reply.getByRole('button', { name: 'Send' }).click();
    await expect(reply.locator('.reply-status')).toHaveText('Sent, but not confirmed. Check the conversation before sending it again. (the session did not record the reply within 60 seconds)');
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
    await expect(reply).toHaveAttribute('data-reply', 'delivered', { timeout: 10000 });
    const sent = await log(page);
    expect(sent.length).toBeGreaterThanOrEqual(2);
    expect(new Set(sent.map((r) => r.request)).size).toBe(1);
    expect(await typed(page)).toHaveLength(1);
  });
});

// Where the machine says how far a reply has got (sessions.progress, #600; ui.md §6.5 as decided on
// #617: quiet unless something's wrong): Sent once it has it, Passed to the agent once the keys
// have gone, Up next when they went into a working session, all heard and none on the line; Send's
// ring says it is on its way, and its check that it got there. The demo host holds before typing
// and before its outcome, so each stage stays to be heard.
test.describe('progress', () => {
  test.use({ viewport: { width: 390, height: 664 } });
  const holdBefore = (page, step) => page.evaluate((s) => window.agentBridgeDemo.host.holdBefore(s), step);
  const releaseBefore = (page, step) => page.evaluate((s) => window.agentBridgeDemo.host.releaseBefore(s), step);

  test('a reply\'s stages are heard and not seen, Delivered turns Send\'s ring to its check for a moment, and the timing marks stay on this device', async ({ page }) => {
    await openDemo(page);
    const reply = box(page, IDLE);
    const status = reply.locator('.reply-status');
    const live = reply.locator('.receipt-live');
    const send = reply.getByRole('button', { name: 'Send' });
    await reply.getByRole('textbox', { name: 'Reply' }).fill('carry on');
    await holdBefore(page, 'typing');
    await holdBefore(page, 'result');
    await send.click();
    await expect(live).toHaveText('Reply sent');
    await expect(send).toHaveAttribute('aria-busy', 'true');
    // Disabled while it is on its way, but never dimmed: the ring keeps its contrast.
    await expect(send).toBeDisabled();
    expect(await send.evaluate((b) => getComputedStyle(b).opacity)).toBe('1');
    await expect(status).toHaveText('');
    await releaseBefore(page, 'typing');
    await expect(live).toHaveText('Reply passed to the agent');
    await expect(status).toHaveText('');
    await releaseBefore(page, 'result');
    await expect(live).toHaveText('Reply delivered');
    await expect(send).toHaveClass(/\bchecked\b/);
    await expect(send.locator('.send-check use')).toHaveAttribute('href', 'design/sprite.svg#icon-check');
    // Its box emptied, Send is disabled under its check, which is not dimmed either.
    await expect(send).toBeDisabled();
    expect(await send.evaluate((b) => getComputedStyle(b).opacity)).toBe('1');
    // Its name stays its own: the receipt says it was delivered.
    await expect(send).toHaveAccessibleName('Send');
    await expect(status).toHaveText('');
    // Then Send's own glyph again.
    await expect(send).not.toHaveClass(/\bchecked\b/);
    expect((await log(page))[0].progress).toBe(true);
    // Tap to outcome, each stage between, and the machine's own timings beside them.
    const [timing] = await page.evaluate(() => window.agentBridgeDemo.timings());
    expect(timing).toMatchObject({ kind: 'reply', outcome: 'delivered', host: { typed: expect.any(Number), confirmed: expect.any(Number) } });
    expect(timing.client.map(([mark]) => mark)).toEqual(['sent', 'received', 'typed', 'result']);
  });

  test('Send shows its own ring from the tap, and the receipt keeps its room from Send until the box is typed in again', async ({ page }) => {
    await openDemo(page);
    const reply = box(page, IDLE);
    const status = reply.locator('.reply-status');
    const send = reply.getByRole('button', { name: 'Send' });
    const input = reply.getByRole('textbox', { name: 'Reply' });
    await input.fill('carry on');
    await hold(page);
    // Inside the first 400 ms: nothing on the line, which already has its room, and Send's ring.
    const early = await reply.evaluate(async (form) => {
      const button = form.querySelector('.button.send');
      button.click();
      await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
      const line = form.querySelector('.reply-status');
      return { ring: getComputedStyle(button, '::after').content !== 'none', words: line.textContent, height: line.getBoundingClientRect().height };
    });
    expect(early).toMatchObject({ ring: true, words: '' });
    expect(early.height).toBeGreaterThan(0);
    await release(page);
    await expect(reply).toHaveAttribute('data-reply', 'delivered');
    await expect(status).toHaveText('');
    expect((await status.boundingBox()).height).toBeCloseTo(early.height, 0);
    expect(await send.evaluate((b) => getComputedStyle(b, '::after').content)).toBe('none');
    await input.fill('next');
    await expect(status).toBeHidden();
  });

  test('a stall puts its words on the line, and the line goes once the outcome comes', async ({ page }) => {
    await openDemo(page);
    const reply = box(page, IDLE);
    const status = reply.locator('.reply-status');
    await reply.getByRole('textbox', { name: 'Reply' }).fill('carry on');
    await hold(page);
    await reply.getByRole('button', { name: 'Send' }).click();
    await expect(status).toHaveText('Still sending…');
    await expect(reply.locator('.receipt-live')).toHaveText('Reply still sending');
    await release(page);
    await expect(reply).toHaveAttribute('data-reply', 'delivered');
    await expect(status).toHaveText('');
  });

  test('a reply typed into a working session is heard as up next: the agent takes it after its current step', async ({ page }) => {
    await openDemo(page);
    await page.evaluate(({ id, t }) => {
      const d = window.agentBridgeDemo;
      d.upsert({ ...d.host.get(id), status: 'working', statusSince: t, updatedAt: t });
    }, { id: IDLE, t: later() });
    await expect(card(page, IDLE)).toHaveAttribute('data-status', 'working');
    const reply = box(page, IDLE);
    await reply.getByRole('textbox', { name: 'Reply' }).fill('and add a test');
    await holdBefore(page, 'result');
    await reply.getByRole('button', { name: 'Send' }).click();
    await expect(reply.locator('.receipt-live')).toHaveText("Reply up next, after the agent's current step");
    await expect(reply.locator('.reply-status')).toHaveText('');
    await releaseBefore(page, 'result');
    await expect(reply.locator('.receipt-live')).toHaveText('Reply delivered');
  });

  test('in forced colours Send\'s ring and check take Send\'s own colours, and the line\'s words are CanvasText', async ({ page, browserName }) => {
    test.skip(browserName !== 'chromium', 'forced colours are emulated in Chromium only');
    await page.emulateMedia({ forcedColors: 'active' });
    await openDemo(page);
    const reply = box(page, IDLE);
    const status = reply.locator('.reply-status');
    const send = reply.getByRole('button', { name: 'Send' });
    const canvasText = await page.evaluate(() => {
      const i = document.createElement('i');
      i.style.forcedColorAdjust = 'none';
      i.style.color = 'CanvasText';
      document.body.append(i);
      const c = getComputedStyle(i).color;
      i.remove();
      return c;
    });
    await reply.getByRole('textbox', { name: 'Reply' }).fill('carry on');
    await hold(page);
    await send.click();
    await expect(status).toHaveText('Still sending…');
    expect(await send.evaluate((el) => getComputedStyle(el, '::after').borderTopColor === getComputedStyle(el).color)).toBe(true);
    expect(await status.evaluate((el) => getComputedStyle(el).color)).toBe(canvasText);
    await release(page);
    await expect(send).toHaveClass(/\bchecked\b/);
    expect(await send.evaluate((el) => getComputedStyle(el.querySelector('.send-check')).color === getComputedStyle(el).color)).toBe(true);
  });

  test('a machine from before progress is asked for none: the reply is heard as sending until its outcome, and never as sent', async ({ page }) => {
    await openDemo(page, '&progress=0');
    const reply = box(page, IDLE);
    const live = reply.locator('.receipt-live');
    await reply.getByRole('textbox', { name: 'Reply' }).fill('carry on');
    await holdBefore(page, 'typing');
    await reply.getByRole('button', { name: 'Send' }).click();
    await expect(live).toHaveText('Sending reply');
    await releaseBefore(page, 'typing');
    await expect(live).toHaveText('Reply delivered');
    await expect(reply.getByRole('button', { name: 'Send' })).toHaveClass(/\bchecked\b/);
    expect(await log(page)).toEqual([{ id: IDLE, request: expect.stringMatching(UUID), text: 'carry on' }]);
    const [timing] = await page.evaluate(() => window.agentBridgeDemo.timings());
    expect(timing).toMatchObject({ kind: 'reply', outcome: 'delivered', host: null });
    expect(timing.client.map(([mark]) => mark)).toEqual(['sent', 'result']);
  });
  test('a reply sent during the last one\'s check shows its own ring at once, never the last one\'s check', async ({ page }) => {
    await openDemo(page);
    const reply = box(page, IDLE);
    const send = reply.getByRole('button', { name: 'Send' });
    await reply.getByRole('textbox', { name: 'Reply' }).fill('first');
    await send.click();
    await expect(send).toHaveClass(/\bchecked\b/);
    await hold(page);
    await reply.getByRole('textbox', { name: 'Reply' }).fill('second');
    await send.click();
    await expect(send).toHaveAttribute('aria-busy', 'true');
    await expect(send).not.toHaveClass(/\bchecked\b/);
    await release(page);
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

  test('Send keeps focus while its reply goes, a second press of any kind sends nothing more, and delivered gives focus back to the box', async ({ page }) => {
    await openDemo(page);
    const reply = box(page, IDLE);
    const input = reply.getByRole('textbox', { name: 'Reply' });
    const send = reply.getByRole('button', { name: 'Send' });
    await input.fill('exactly once');
    await hold(page);
    await send.focus();
    await page.keyboard.press('Enter');
    await expect(reply).toHaveAttribute('data-reply', 'sending');
    await expect(send).toBeFocused();
    await expect(send).toHaveAttribute('aria-disabled', 'true');
    // Every way of pressing it again while it goes: the keys on Send, a click, a submit, and
    // the shortcut from the box.
    await page.keyboard.press('Enter');
    await page.keyboard.press('Space');
    await send.click({ force: true });
    await reply.evaluate((form) => form.requestSubmit());
    await input.focus();
    await page.keyboard.press('ControlOrMeta+Enter');
    await send.focus();
    await expect(send).toBeFocused();
    await release(page);
    await expect(reply).toHaveAttribute('data-reply', 'delivered');
    // The box is empty, so Send is off; focus goes back to the box rather than the page.
    await expect(send).toBeDisabled();
    await expect(input).toBeFocused();
    const sent = await log(page);
    expect(new Set(sent.map((r) => r.request)).size).toBe(1);
    expect(sent.map((r) => r.text)).toEqual(sent.map(() => 'exactly once'));
    expect(await typed(page)).toHaveLength(1);
  });
});

test('a machine that does not take replies shows no reply box', async ({ page }) => {
  await openDemo(page, '&reply=0');
  await expect(card(page, IDLE)).toBeVisible();
  await expect(page.locator('form.reply')).toHaveCount(0);
  // It still takes answers, and ends sessions.
  const capabilities = await page.evaluate(() => window.agentBridgeDemo.host.capabilities);
  expect(capabilities).toEqual(expect.arrayContaining(['sessions.answer', 'sessions.stop']));
  expect(capabilities).not.toContain('sessions.reply');
});
