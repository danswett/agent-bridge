// Answering questions from the phone, in demo mode: the simulated host validates answers
// as a real one does, keeps each request's first outcome, takes the question away with an
// upsert when it types an answer, and plays any outcome a test scripts. Every field type,
// every outcome, resends, a question changing under the person, and the keyboard.
import { test, expect } from '@playwright/test';

const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CODEX = '019a1b2c-codex-example';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

const card = (page, id) => page.locator(`.session[data-id="${id}"]`);
const later = () => new Date(Date.now() + 1000).toISOString();
const log = (page) => page.evaluate(() => window.agentBridgeDemo.host.answerLog);
const typed = (page) => page.evaluate(() => window.agentBridgeDemo.host.typed);
// Keeps the host's responses back while a test looks at an answer on its way: the answer
// delay alone was shorter than a busy CI runner's WebKit took to check it.
const hold = (page) => page.evaluate(() => window.agentBridgeDemo.host.holdResponses());
const release = (page) => page.evaluate(() => window.agentBridgeDemo.host.releaseResponses());

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1&answerDelay=300${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

// The Codex session, asking `question`.
async function ask(page, question, id = CODEX) {
  await page.evaluate(({ id, question, t }) => {
    const d = window.agentBridgeDemo;
    d.upsert({ ...d.host.get(id), status: 'waiting', statusSince: t, updatedAt: t, question: { kind: 'form', askedAt: t, ...question } });
  }, { id, question, t: later() });
  const q = card(page, id).locator('.question');
  await expect(q).toHaveAttribute('data-question', question.id);
  return q;
}

const script = (page, ...outcomes) => page.evaluate((o) => window.agentBridgeDemo.script(...o), outcomes);

const choice = (extra = {}) => ({
  id: 'q-choice', message: 'Which **database**?',
  fields: [{ name: 'answer', title: 'Database', type: 'choice', required: true, options: [{ value: 'sqlite', label: 'SQLite' }, { value: 'pg', label: 'PostgreSQL (Recommended)' }, { value: 'none', label: 'Neither' }], ...extra }],
});

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });

  test('a single choice answers on a tap, with the recommended option first, and the question goes when delivered', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, choice());
    await expect(q).toHaveAttribute('data-answerable', 'true');
    await expect(q.locator('.choice[data-value]')).toHaveText(['PostgreSQL (Recommended)', 'SQLite', 'Neither']);
    await expect(q.locator('.choice.recommended')).toHaveCount(1);
    await expect(q.locator('button.send')).toBeHidden();

    await hold(page);
    await q.locator('.choice[data-value="sqlite"]').tap();
    // While it is on its way: progress, and nothing can be tapped again.
    await expect(q.locator('.answer-status')).toHaveText('Sending…');
    await expect(q.locator('.answer')).toHaveAttribute('aria-busy', 'true');
    await expect(q.locator('.choice[data-value="pg"]')).toBeDisabled();
    await expect(q.locator('.choice[data-value="sqlite"]')).toHaveAttribute('aria-pressed', 'true');
    await release(page);

    // Delivered: the host's upsert takes the question away, and the card says what happened.
    await expect(q).toHaveCount(0);
    const receipt = card(page, CODEX).locator('.answer-receipt');
    await expect(receipt).toHaveText('Your answer: Delivered. The session has your answer.');
    await expect(receipt).toHaveAttribute('data-answer', 'delivered');
    const [sent] = await log(page);
    expect(sent).toEqual({ id: CODEX, question: 'q-choice', request: expect.stringMatching(UUID), fields: { answer: 'sqlite' } });
    expect(await typed(page)).toHaveLength(1);
    await expect(card(page, CODEX)).toHaveAttribute('data-status', 'working');
  });

  test('Other… opens a box for an answer of your own, which must say something', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, choice({ allowOther: true }));
    const toggle = q.locator('.other-toggle');
    await expect(toggle).toHaveAttribute('aria-expanded', 'false');
    await toggle.tap();
    await expect(toggle).toHaveAttribute('aria-expanded', 'true');
    const box = q.getByRole('textbox', { name: 'Your own answer for Database' });
    await expect(box).toBeFocused();
    await q.locator('button.send').tap();
    await expect(q.locator('.answer-error')).toHaveText('"Database": the other answer is empty.');
    expect(await log(page)).toEqual([]);
    await box.fill('DuckDB, embedded');
    await expect(q.locator('.answer-error')).toBeHidden();
    await q.locator('button.send').tap();
    await expect(q).toHaveCount(0);
    expect((await log(page))[0].fields).toEqual({ answer: { other: 'DuckDB, embedded' } });
  });

  test('a multi choice is checkboxes, sent in the agent\'s order, with Other', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, {
      id: 'q-multi', message: 'Which platforms?',
      fields: [{ name: 'os', title: 'Platforms', type: 'multi', required: true, allowOther: true, options: [{ value: 'win', label: 'Windows' }, { value: 'mac', label: 'macOS' }, { value: 'linux', label: 'Linux' }] }],
    });
    await expect(q.getByRole('checkbox')).toHaveCount(4);
    await q.getByRole('checkbox', { name: 'Other…' }).check();
    await q.getByRole('button', { name: 'Send answer' }).tap();
    await expect(q.locator('.answer-error')).toHaveText('"Platforms": the other answer is empty.');
    await expect(q.locator('.field[data-field="os"]')).toHaveClass(/\binvalid\b/);
    await q.getByRole('checkbox', { name: 'Linux' }).check();
    await q.getByRole('checkbox', { name: 'Windows' }).check();
    // Typing an answer of your own ticks Other.
    await q.getByRole('textbox', { name: 'Your own answer for Platforms' }).fill('FreeBSD');
    await expect(q.getByRole('checkbox', { name: 'Other…' })).toBeChecked();
    await q.getByRole('button', { name: 'Send answer' }).tap();
    await expect(q).toHaveCount(0);
    expect((await log(page))[0].fields).toEqual({ os: ['win', 'linux', { other: 'FreeBSD' }] });
  });

  test('a required multi choice can be answered with nothing ticked, as an empty list', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, {
      id: 'q-none', message: 'Which of these broke?',
      fields: [{ name: 'broke', title: 'Broken', type: 'multi', required: true, options: [{ value: 'a', label: 'A' }, { value: 'b', label: 'B' }], default: ['a'] }],
    });
    await expect(q.getByRole('checkbox', { name: 'A' })).toBeChecked();
    await q.getByRole('checkbox', { name: 'A' }).uncheck();
    await q.getByRole('button', { name: 'Send answer' }).tap();
    await expect(q).toHaveCount(0);
    expect((await log(page))[0].fields).toEqual({ broke: [] });
  });

  test('a yes-or-no is two buttons, and required means one of them', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, { id: 'q-bool', message: 'Ship it?', fields: [{ name: 'ok', title: 'Ship', type: 'boolean', required: true }] });
    const yes = q.getByRole('button', { name: 'Yes' });
    const no = q.getByRole('button', { name: 'No' });
    await expect(yes).toHaveAttribute('aria-pressed', 'false');
    await q.getByRole('button', { name: 'Send answer' }).tap();
    await expect(q.locator('.answer-error')).toHaveText('"Ship" needs an answer.');
    await no.tap();
    await yes.tap();
    await expect(yes).toHaveAttribute('aria-pressed', 'true');
    await expect(no).toHaveAttribute('aria-pressed', 'false');
    await q.getByRole('button', { name: 'Send answer' }).tap();
    await expect(q).toHaveCount(0);
    expect((await log(page))[0].fields).toEqual({ ok: true });
  });

  test('free text is a textarea limited to 4 KiB of UTF-8, counted in bytes, not characters', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, { id: 'q-text', message: 'Anything to add?', fields: [{ name: 'notes', title: 'Notes', type: 'text', required: true }] });
    const box = q.getByRole('textbox', { name: 'Notes' });
    // 1,366 euro signs: 1,366 characters, but 4,098 bytes.
    await box.fill('€'.repeat(1366));
    await expect(q.locator('.bytes')).toHaveText('Too long: 4.0 KiB of 4 KiB');
    await expect(box).toHaveAttribute('aria-invalid', 'true');
    await q.getByRole('button', { name: 'Send answer' }).tap();
    await expect(q.locator('.answer-error')).toHaveText('"Notes": the text is longer than 4 KiB.');
    expect(await log(page)).toEqual([]);
    await box.fill('€'.repeat(1365));
    await expect(box).not.toHaveAttribute('aria-invalid', 'true');
    await q.getByRole('button', { name: 'Send answer' }).tap();
    await expect(q).toHaveCount(0);
    expect((await log(page))[0].fields.notes).toHaveLength(1365);
  });

  test('a whole answer over 16 KiB, as the machine would serialize it, is caught before it is sent', async ({ page }) => {
    await openDemo(page);
    const names = ['a', 'b'];
    const q = await ask(page, { id: 'q-big', message: 'Two notes?', fields: names.map((name) => ({ name, title: `Note ${name}`, type: 'text' })) });
    // 1,024 emoji each: 4 KiB of text, within the limit, but 12 KiB of the host's JSON.
    for (const name of names) { await q.getByRole('textbox', { name: `Note ${name}` }).fill('😀'.repeat(1024)); }
    await q.getByRole('button', { name: 'Send answer' }).tap();
    await expect(q.locator('.answer-error')).toHaveText('The answer is larger than 16 KiB.');
    expect(await log(page)).toEqual([]);
    await q.getByRole('textbox', { name: 'Note b' }).fill('short');
    await q.getByRole('button', { name: 'Send answer' }).tap();
    await expect(q).toHaveCount(0);
  });

  test('a number is a numeric input, pre-filled from its default', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, { id: 'q-num', message: 'How many runs?', fields: [{ name: 'runs', title: 'Runs', type: 'number', default: 20, required: true }] });
    const input = q.locator('input[type="number"]');
    await expect(input).toHaveValue('20');
    await expect(input).toHaveAttribute('inputmode', 'decimal');
    await input.fill('');
    await q.getByRole('button', { name: 'Send answer' }).tap();
    await expect(q.locator('.answer-error')).toHaveText('"Runs" needs an answer.');
    await input.fill('-2.5');
    await q.getByRole('button', { name: 'Send answer' }).tap();
    await expect(q).toHaveCount(0);
    expect((await log(page))[0].fields).toEqual({ runs: -2.5 });
  });

  test('the worked example\'s form is pre-filled from its defaults and sends them as they stand', async ({ page }) => {
    await openDemo(page);
    const q = card(page, COPILOT).locator('.question');
    await expect(q).toHaveAttribute('data-answerable', 'true');
    await expect(q.getByRole('radio', { name: 'Go: protocol, read-only host, PWA cards in parallel (Recommended)' })).toBeChecked();
    await expect(q.locator('.field[data-field="urgent"] .toggle[aria-pressed="true"]')).toHaveText('No');
    await expect(q.locator('.field')).toHaveCount(5);
    await q.getByRole('button', { name: 'Send answer' }).tap();
    await expect(card(page, COPILOT).locator('.answer-receipt')).toHaveText('Your answer: Delivered. The session has your answer.');
    expect((await log(page))[0].fields).toEqual({ plan: 'go', urgent: false });
  });

  test('a question without fields is answered with text, which must not be empty', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, { id: 'q-plain', message: 'What should the release notes lead with?', fields: [] });
    await q.getByRole('button', { name: 'Send answer' }).tap();
    await expect(q.locator('.answer-error')).toHaveText('The answer is empty.');
    await q.getByRole('textbox', { name: 'Your answer' }).fill('The phone can answer now.');
    await q.getByRole('button', { name: 'Send answer' }).tap();
    await expect(q).toHaveCount(0);
    const [sent] = await log(page);
    expect(sent).toEqual({ id: CODEX, question: 'q-plain', request: expect.stringMatching(UUID), text: 'The phone can answer now.' });
    expect(sent.fields).toBeUndefined();
  });

  test('an approval is big allow and deny buttons, answered on a tap', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, {
      id: 'q-approval', kind: 'approval', message: 'Run `rm -rf build`?',
      fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'yes', label: 'Yes' }, { value: 'no', label: 'No, and tell the agent what to do' }] }],
    });
    await expect(q.locator('.question-head')).toHaveText('Permission requested');
    const allow = q.getByRole('button', { name: 'Yes' });
    const deny = q.getByRole('button', { name: 'No, and tell the agent what to do' });
    await expect(allow).toHaveClass(/\btone-allow\b/);
    await expect(deny).toHaveClass(/\btone-deny\b/);
    expect((await allow.boundingBox()).height).toBeGreaterThanOrEqual(56);
    await deny.tap();
    await expect(q).toHaveCount(0);
    expect((await log(page))[0].fields).toEqual({ decision: 'no' });
  });

  // A Copilot permission prompt as a host with remote approval on sends it: what it would
  // allow in full, and only Approve once or Deny.
  const permission = {
    id: 'r1#0123456789abcdef', kind: 'approval', message: 'Allow **shell**?\n\nPush the branch\n\n```\ngit push origin main --force-with-lease\n```',
    fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'approve', label: 'Approve once' }, { value: 'deny', label: 'Deny' }] }],
  };

  test('a Copilot permission prompt shows what it would allow and is approved once on a tap', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, permission, COPILOT);
    await expect(q).toHaveAttribute('data-answerable', 'true');
    await expect(q.locator('.question-head')).toHaveText('Permission requested');
    await expect(q.locator('.question-message pre')).toHaveText('git push origin main --force-with-lease');
    // The command can be copied; the only answers are the two decisions.
    await expect(q.locator('.question-message').getByRole('button', { name: 'Copy code' })).toHaveCount(1);
    await expect(q.getByRole('button').and(page.locator('button:not(.md-copy)'))).toHaveText(['Approve once', 'Deny']);
    await expect(q.getByRole('button', { name: 'Approve once' })).toHaveClass(/\btone-allow\b/);
    await expect(q.getByRole('button', { name: 'Deny' })).toHaveClass(/\btone-deny\b/);

    await q.getByRole('button', { name: 'Approve once' }).tap();
    await expect(q).toHaveCount(0);
    expect((await log(page))[0]).toEqual({ id: COPILOT, question: 'r1#0123456789abcdef', request: expect.stringMatching(UUID), fields: { decision: 'approve' } });
  });

  test('a Copilot permission prompt at a machine with approving turned off says so and offers nothing', async ({ page }) => {
    await openDemo(page, '&approve=0');
    const q = await ask(page, { ...permission, answerAtMachine: true }, COPILOT);
    await expect(q).toHaveAttribute('data-answerable', 'false');
    await expect(q.locator('.question-message')).toContainText('git push origin main --force-with-lease');
    await expect(q.locator('.read-only-why')).toHaveText('Approving permission prompts from the phone is turned off on this machine. Answer at the machine on Demo machine.');
    // Nothing that answers: copying the command is all the card offers.
    await expect(q.locator(':is(button, input, select, textarea):not(.md-copy)')).toHaveCount(0);
  });

  test('every control is at least 44 px tall', async ({ page }) => {
    await openDemo(page);
    await ask(page, choice({ allowOther: true }));
    const sizes = await page.locator('.question.answerable').evaluateAll((qs) => qs.flatMap((q) =>
      [...q.querySelectorAll('button:not([hidden]), label.opt, textarea, input[type="number"]')]
        .filter((e) => e.offsetParent !== null)
        .map((e) => ({ what: e.className || e.tagName, h: e.getBoundingClientRect().height }))));
    expect(sizes.length).toBeGreaterThan(10);
    for (const s of sizes) { expect(s.h, s.what).toBeGreaterThanOrEqual(44); }
  });
});

test.describe('outcomes', () => {
  test.use({ viewport: { width: 390, height: 664 } });

  test('unconfirmed says to check at the machine, and nothing offers to send it again', async ({ page }) => {
    await openDemo(page);
    await script(page, 'unconfirmed');
    const q = await ask(page, choice());
    await q.locator('.choice[data-value="pg"]').click();
    const receipt = card(page, CODEX).locator('.answer-receipt');
    // The host typed it, so the question goes; the receipt carries the outcome.
    await expect(receipt).toHaveText('Your answer: Sent, but not confirmed. Check at the machine on Demo machine. (the session stopped answering)');
    await expect(receipt).toHaveAttribute('data-answer', 'unconfirmed');
    await page.waitForTimeout(500);
    expect(await log(page)).toHaveLength(1);
    await expect(card(page, CODEX).getByRole('button', { name: 'Try again' })).toHaveCount(0);
  });

  test('an outcome this app doesn\'t know reads as unconfirmed', async ({ page }) => {
    await openDemo(page);
    await script(page, { outcome: 'queued', reason: 'a newer host' });
    const q = await ask(page, choice());
    await q.locator('.choice[data-value="pg"]').click();
    await expect(card(page, CODEX).locator('.answer-receipt')).toHaveText('Your answer: Sent, but not confirmed. Check at the machine on Demo machine. (a newer host)');
  });

  test('stale says it was already answered or is gone, and holds the form', async ({ page }) => {
    await openDemo(page);
    await script(page, 'stale');
    const q = await ask(page, choice());
    await q.locator('.choice[data-value="pg"]').click();
    await expect(q.locator('.answer-status')).toHaveText('Already answered or gone: the session is no longer asking this.');
    await expect(q).toHaveAttribute('data-answer', 'stale');
    await expect(q.locator('.choice[data-value="sqlite"]')).toBeDisabled();
  });

  test('busy lets you try again after a moment, as a new request', async ({ page }) => {
    await openDemo(page);
    await script(page, 'busy');
    const q = await ask(page, { id: 'q-b', message: 'Ship it?', fields: [{ name: 'ok', type: 'boolean', default: true }] });
    const send = q.getByRole('button', { name: 'Send answer' });
    await send.click();
    await expect(q.locator('.answer-status')).toHaveText('Another answer to this session is being delivered. Try again in a moment.');
    await expect(send).toBeDisabled();
    await expect(send).toBeEnabled({ timeout: 4000 });
    await send.click();
    await expect(q).toHaveCount(0);
    const [first, second] = await log(page);
    expect(second.request).not.toBe(first.request);
    expect(await typed(page)).toHaveLength(1);
  });

  test('refused shows the host\'s reason, and the answer can be changed and sent again', async ({ page }) => {
    await openDemo(page);
    await script(page, { outcome: 'refused', reason: 'the session is running elevated' });
    const q = await ask(page, { id: 'q-r', message: 'Notes?', fields: [{ name: 'n', title: 'Notes', type: 'text', required: true }] });
    const box = q.getByRole('textbox', { name: 'Notes' });
    await box.fill('first try');
    await q.getByRole('button', { name: 'Send answer' }).click();
    await expect(q.locator('.answer-status')).toHaveText('The machine refused this answer: the session is running elevated.');
    await expect(q.locator('.answer-status')).toHaveClass(/\bbad\b/);
    await expect(box).toBeEnabled();
    await box.fill('second try');
    await q.getByRole('button', { name: 'Send answer' }).click();
    await expect(q).toHaveCount(0);
    const [first, second] = await log(page);
    expect([first.fields.n, second.fields.n]).toEqual(['first try', 'second try']);
    expect(second.request).not.toBe(first.request);
  });

  test('a lost response is resent with the same request, and the answer is typed once', async ({ page }) => {
    await openDemo(page, '&timeout=600&retry=100');
    await script(page, { outcome: 'delivered', drop: true });
    const q = await ask(page, choice());
    await q.locator('.choice[data-value="pg"]').click();
    await expect(q.locator('.answer-status')).toHaveText(/Sending|Waiting for the connection/);
    await expect(card(page, CODEX).locator('.answer-receipt')).toHaveText('Your answer: Delivered. The session has your answer.', { timeout: 10000 });
    const sent = await log(page);
    expect(sent.length).toBeGreaterThanOrEqual(2);
    expect(new Set(sent.map((r) => r.request)).size).toBe(1);
    expect(await typed(page)).toHaveLength(1);
  });
});

test.describe('when the question changes', () => {
  test('text typed for a question that is replaced is kept, and the change is said', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, { id: 'q-old', message: 'Notes?', fields: [{ name: 'n', title: 'Notes', type: 'text' }] });
    await q.getByRole('textbox', { name: 'Notes' }).fill('half a thought');
    const next = await ask(page, { id: 'q-new', message: 'Something else?', fields: [] });
    const changed = card(page, CODEX).locator('.question-changed');
    await expect(changed).toContainText('The question changed before you sent your answer.');
    await expect(changed.getByRole('textbox', { name: 'What you typed' })).toHaveValue('half a thought');
    await expect(next).toHaveAttribute('data-question', 'q-new');
    // Dismissed, it stays gone through later updates.
    await changed.getByRole('button', { name: 'Dismiss' }).click();
    await expect(changed).toHaveCount(0);
    await ask(page, { id: 'q-new', message: 'Something else, reworded?', fields: [] });
    await expect(card(page, CODEX).locator('.question-changed')).toHaveCount(0);
  });

  test('text typed for a question that goes away is kept too', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, { id: 'q-gone', message: 'Anything?', fields: [] });
    await q.getByRole('textbox', { name: 'Your answer' }).fill('wait, one more thing');
    await page.evaluate((id) => { const d = window.agentBridgeDemo; d.upsert({ ...d.host.get(id), status: 'idle', question: null }); }, CODEX);
    await expect(card(page, CODEX).locator('.question')).toHaveCount(0);
    await expect(card(page, CODEX).locator('.question-changed textarea')).toHaveValue('wait, one more thing');
  });

  test('an update to the same question keeps what was entered', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, { id: 'q-same', message: 'Notes?', fields: [{ name: 'n', title: 'Notes', type: 'text' }, { name: 'b', title: 'Flag', type: 'boolean' }] });
    await q.getByRole('textbox', { name: 'Notes' }).fill('kept');
    await q.getByRole('button', { name: 'Yes' }).click();
    await ask(page, { id: 'q-same', message: 'Notes, please?', fields: [{ name: 'n', title: 'Notes', type: 'text' }, { name: 'b', title: 'Flag', type: 'boolean' }] });
    await expect(q.locator('.question-message')).toHaveText('Notes, please?');
    await expect(q.getByRole('textbox', { name: 'Notes' })).toHaveValue('kept');
    await expect(q.getByRole('button', { name: 'Yes' })).toHaveAttribute('aria-pressed', 'true');
    await expect(card(page, CODEX).locator('.question-changed')).toHaveCount(0);
  });
});

test('a card that moves to the top while you type keeps your place', async ({ page }) => {
  await openDemo(page);
  // Cards move on an update only in Most recent; in your order they never do (#164).
  await page.locator('#session-sort').selectOption('recent');
  const q = await ask(page, { id: 'q-move', message: 'Notes?', fields: [{ name: 'n', title: 'Notes', type: 'text' }] });
  // Other sessions update, so this one is no longer first.
  for (const id of ['6f1c2b7e-claude-example', COPILOT]) {
    await page.evaluate(({ id, t }) => { const d = window.agentBridgeDemo; d.upsert({ ...d.host.get(id), updatedAt: t }); }, { id, t: later() });
  }
  await expect(page.locator('.session').first()).not.toHaveAttribute('data-id', CODEX);
  const box = q.getByRole('textbox', { name: 'Notes' });
  await box.click();
  await page.keyboard.type('abc');
  await page.evaluate(({ id, t }) => { const d = window.agentBridgeDemo; d.upsert({ ...d.host.get(id), updatedAt: t, activity: { ...d.host.get(id).activity, response: 'Still waiting on you.' } }); }, { id: CODEX, t: new Date(Date.now() + 5000).toISOString() });
  await expect(page.locator('.session').first()).toHaveAttribute('data-id', CODEX);
  await expect(box).toBeFocused();
  await page.keyboard.type('def');
  await expect(box).toHaveValue('abcdef');
});

test.describe('from a keyboard', () => {
  test('every control is labelled, reachable in order, and Ctrl+Enter sends', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, {
      id: 'q-kb', message: 'Configure the run',
      fields: [
        { name: 'mode', title: 'Mode', type: 'choice', required: true, options: [{ value: 'fast', label: 'Fast' }, { value: 'full', label: 'Full (Recommended)' }] },
        { name: 'notes', title: 'Notes', type: 'text' },
      ],
    });
    await expect(q.locator('.answer')).toHaveAccessibleName('Waiting on you');
    await expect(q.getByRole('group', { name: 'Mode required Choose one' })).toBeVisible();
    // Recommended first, so the first radio Tab reaches from the card's title is Full.
    await card(page, CODEX).locator('button.fold').focus();
    await page.keyboard.press('Tab');
    await expect(q.getByRole('radio', { name: 'Full (Recommended)' })).toBeFocused();
    await page.keyboard.press('ArrowDown');
    await expect(q.getByRole('radio', { name: 'Fast' })).toBeChecked();
    await page.keyboard.press('Tab');
    await expect(q.getByRole('textbox', { name: 'Notes' })).toBeFocused();
    await page.keyboard.type('from the keyboard');
    await page.keyboard.press('Tab');
    await expect(q.getByRole('button', { name: 'Send answer' })).toBeFocused();
    await q.getByRole('textbox', { name: 'Notes' }).focus();
    await page.keyboard.press('Control+Enter');
    await expect(q).toHaveCount(0);
    expect((await log(page))[0].fields).toEqual({ mode: 'fast', notes: 'from the keyboard' });
  });

  test('a problem is announced and focus goes to the field it is about', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, { id: 'q-focus', message: 'Count?', fields: [{ name: 'a', title: 'Note', type: 'text' }, { name: 'n', title: 'Count', type: 'number', required: true }] });
    await q.getByRole('button', { name: 'Send answer' }).click();
    await expect(q.getByRole('alert')).toHaveText('"Count" needs an answer.');
    await expect(q.locator('input[type="number"]')).toBeFocused();
  });
});
