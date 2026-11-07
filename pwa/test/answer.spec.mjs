// Answering questions from the phone, in demo mode: the simulated host validates answers
// as a real one does, keeps each request's first outcome, takes the question away with an
// upsert when it types an answer, and plays any outcome a test scripts. Every field type,
// every outcome, resends, a question changing under the person, and the keyboard.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CODEX = '019a1b2c-codex-example';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

// A session's view (#179), where its question is answered.
const card = (page, id) => view(page, id);
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
  await open(page, id);
  return q;
}

const script = (page, ...outcomes) => page.evaluate((o) => window.agentBridgeDemo.script(...o), outcomes);

const choice = (extra = {}) => ({
  id: 'q-choice', message: 'Which **database**?',
  fields: [{ name: 'answer', title: 'Database', type: 'choice', required: true, options: [{ value: 'sqlite', label: 'SQLite' }, { value: 'pg', label: 'PostgreSQL (Recommended)' }, { value: 'none', label: 'Neither' }], ...extra }],
});

// #908: Other the CLI offers and the machine can't send yet, shown disabled with why.
const OTHER_NOT_SENDABLE = "Your own answer can't be sent from here yet. Pick an option, or answer at the machine.";

test('Other the machine cannot send yet is shown disabled, its label unchanged, with why under the choices and as their description', async ({ page }) => {
  await openDemo(page);
  const q = await ask(page, choice({ required: false, otherUnavailable: 'otherNotSendable' }));
  const other = q.locator('.choices .other-unavailable');
  await expect(other).toHaveText('Other…');
  await expect(other).toBeDisabled();
  const note = q.locator('.other-unavailable-note');
  await expect(note).toHaveText(OTHER_NOT_SENDABLE);
  await expect(note).toHaveClass(/field-desc/);
  // Read as the choices' description, and the disabled Other's own.
  const described = (loc) => loc.evaluate((el) => (el.getAttribute('aria-describedby') ?? '').split(' ').map((i) => document.getElementById(i)?.textContent ?? '').join(' '));
  expect(await described(q.locator('.choices'))).toBe(OTHER_NOT_SENDABLE);
  expect(await described(other)).toBe(OTHER_NOT_SENDABLE);
  // No box to type in, and the options still answer on a tap. Tapped as open() left it, with
  // the question's arrival wait over: nothing has moved it since.
  await expect(q.locator('.other-box')).toHaveCount(0);
  await q.locator('.choice[data-value="sqlite"]').click();
  await expect.poll(() => log(page)).toHaveLength(1);
  expect((await log(page))[0].fields).toEqual({ answer: 'sqlite' });
});

// The acceptance captures, apart from the tap above: resizing the page and scrolling the note
// into view move the question, which starts its arrival wait again (#491), and a tap after
// them was dropped on a slow runner, as it should be.
test('Other the machine cannot send yet is captured disabled with why under it, light and dark', async ({ page }, info) => {
  await openDemo(page);
  const q = await ask(page, choice({ required: false, otherUnavailable: 'otherNotSendable' }));
  // WebKit's screenshot adds an inline style of Playwright's own, which the page's CSP reports:
  // the page's own reports are kept around it.
  const reported = await page.evaluate(() => [...window.__cspViolations]);
  // Tall enough that the reply box under the conversation covers none of the question.
  await page.setViewportSize({ width: 900, height: 1600 });
  await q.locator('.other-unavailable-note').scrollIntoViewIfNeeded();
  for (const colorScheme of ['light', 'dark']) {
    await page.emulateMedia({ colorScheme });
    await expect(q.locator('.other-unavailable-note')).toHaveText(OTHER_NOT_SENDABLE);
    await page.screenshot({ path: info.outputPath(`other-unavailable-${colorScheme}.png`), caret: 'initial', animations: 'disabled', clip: await q.boundingBox() });
  }
  await page.evaluate((r) => { window.__cspViolations = r; }, reported);
  // Nothing was sent by looking.
  expect(await log(page)).toEqual([]);
});

test('a refusal for Other the machine cannot send yet is said in the same words, after Not sent', async ({ page }) => {
  await openDemo(page);
  const q = await ask(page, choice({ required: false, allowOther: true }));
  await script(page, { outcome: 'refused', reason: OTHER_NOT_SENDABLE, code: 'otherNotSendable' });
  await q.locator('.other-toggle').click();
  await q.locator('.other-box textarea').fill('DuckDB');
  await q.locator('button.send').click();
  await expect(q.locator('.answer-status')).toHaveText(`Not sent · ${OTHER_NOT_SENDABLE}`);
  // A refusal: nothing went, so the person may answer again.
  await expect(q.locator('button.send')).toBeEnabled();
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
    // While it is on its way: the ring on the option tapped, heard and not seen (ui.md §6.5), and
    // nothing can be tapped again.
    await expect(q.locator('.receipt-live')).toHaveText('Sending answer');
    await expect(q.locator('.answer-status')).toHaveText('');
    expect(await q.locator('.choice[data-value="sqlite"]').evaluate((b) => getComputedStyle(b, '::after').content)).not.toBe('none');
    await expect(q.locator('.answer')).toHaveAttribute('aria-busy', 'true');
    await expect(q.locator('.choice[data-value="pg"]')).toBeDisabled();
    await expect(q.locator('.choice[data-value="sqlite"]')).toHaveAttribute('aria-pressed', 'true');
    await release(page);

    // Delivered: the host's upsert takes the question away, and the card says what happened.
    await expect(q).toHaveCount(0);
    const receipt = card(page, CODEX).locator('.answer-receipt');
    await expect(receipt).toHaveText('You answered · SQLite');
    await expect(receipt).toHaveAttribute('data-answer', 'delivered');
    const [sent] = await log(page);
    expect(sent).toEqual({ id: CODEX, question: 'q-choice', request: expect.stringMatching(UUID), fields: { answer: 'sqlite' }, progress: true });
    expect(await typed(page)).toHaveLength(1);
    await expect(card(page, CODEX)).toHaveAttribute('data-status', 'working');
  });

  test('an option\'s ring has a slot of its own at its end, clear of its words and its badge, so the option never resizes as it comes', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, choice({ default: 'pg' }));
    const pg = q.locator('.choice[data-value="pg"]');
    const before = await pg.boundingBox();
    await hold(page);
    await pg.tap();
    await expect(q.locator('.answer')).toHaveAttribute('aria-busy', 'true');
    const fit = await pg.evaluate((b) => {
      const ring = getComputedStyle(b, '::after');
      const px = (v) => parseFloat(v) || 0;
      const reach = px(ring.right) + px(ring.width) + (ring.boxSizing === 'border-box' ? 0 : px(ring.borderLeftWidth) + px(ring.borderRightWidth));
      const end = Math.max(...[...b.querySelectorAll('*')].map((c) => c.getBoundingClientRect().right));
      return { reach, room: b.getBoundingClientRect().right - end, badges: b.querySelectorAll('.badge').length };
    });
    expect(fit.badges).toBeGreaterThan(0);
    expect(fit.room).toBeGreaterThanOrEqual(fit.reach);
    expect(await pg.boundingBox()).toEqual(before);
    await release(page);
  });
  test('an answer is heard as sent, keeps its ring on the option tapped once the host takes the question away, shows the check, then resolves to You answered with its value, with no badge (#600)', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, choice());
    await page.evaluate(() => { const h = window.agentBridgeDemo.host; h.holdBefore('typing'); h.holdBefore('result'); });
    await q.locator('.choice[data-value="pg"]').tap();
    await expect(q.locator('.receipt-live')).toHaveText('Answer sent');
    await expect(q.locator('.answer-status')).toHaveText('');
    await page.evaluate(() => window.agentBridgeDemo.host.releaseBefore('typing'));
    // Typed: the host's upsert takes the question away, but not the answer's ring (ui.md §6.5, tap to
    // answer): the panel stays while the answer is open, the option tapped pressed and nothing to tap.
    await expect(card(page, CODEX)).toHaveAttribute('data-status', 'working');
    const pg = q.locator('.choice[data-value="pg"]');
    await expect(pg).toHaveAttribute('aria-pressed', 'true');
    await expect(q.locator('.choice[data-value="sqlite"]')).toBeDisabled();
    expect(await pg.evaluate((b) => getComputedStyle(b, '::after').content)).not.toBe('none');
    await expect(q.locator('.answer-status')).toHaveText('');
    await page.evaluate(() => window.agentBridgeDemo.host.releaseBefore('result'));
    // Delivered: the check on the option, then the card resolves to what was answered.
    await expect(pg).toHaveClass(/\bchecked\b/);
    await expect(q).toHaveCount(0);
    const receipt = card(page, CODEX).locator('.answer-receipt');
    await expect(receipt).toHaveText('You answered · PostgreSQL (Recommended)');
    await expect(receipt.locator('svg')).toHaveCount(0);
    const [timing] = await page.evaluate(() => window.agentBridgeDemo.timings());
    expect(timing).toMatchObject({ kind: 'answer', outcome: 'delivered', host: { typed: expect.any(Number) } });
    expect(timing.client.map(([mark]) => mark)).toEqual(['sent', 'received', 'typed', 'result']);
  });
  test('with Other… open, an option tapped instead has the ring and Send answer has none', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, choice({ allowOther: true }));
    await q.locator('.other-toggle').tap();
    await expect(q.locator('button.send')).toBeVisible();
    await hold(page);
    await q.locator('.choice[data-value="sqlite"]').tap();
    await expect(q.locator('.answer')).toHaveAttribute('aria-busy', 'true');

    expect(await q.locator('.choice[data-value="sqlite"]').evaluate((b) => getComputedStyle(b, '::after').content)).not.toBe('none');
    expect(await q.locator('button.send').evaluate((b) => getComputedStyle(b, '::after').content)).toBe('none');
    await release(page);
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
    await open(page, COPILOT);
    const q = card(page, COPILOT).locator('.question');
    await expect(q).toHaveAttribute('data-answerable', 'true');
    await expect(q.getByRole('radio', { name: 'Go: protocol, read-only host, PWA cards in parallel (Recommended)' })).toBeChecked();
    await expect(q.locator('.field[data-field="urgent"] .toggle[aria-pressed="true"]')).toHaveText('No');
    await expect(q.locator('.field')).toHaveCount(5);
    await q.getByRole('button', { name: 'Send answer' }).tap();
    // A form of several fields has no one value to say.
    await expect(card(page, COPILOT).locator('.answer-receipt')).toHaveText('You answered');
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
    expect(sent).toEqual({ id: CODEX, question: 'q-plain', request: expect.stringMatching(UUID), text: 'The phone can answer now.', progress: true });
    expect(sent.fields).toBeUndefined();
  });

  test('an approval is big allow and deny buttons, answered on a tap', async ({ page }) => {
    await openDemo(page);
    // As a host sends a Codex approval (#209): an older host's prose is answered at the machine.
    const q = await ask(page, {
      id: 'q-approval', kind: 'approval', message: 'Allow **Bash**?\n\n```\nrm -rf build\n```',
      fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'yes', label: 'Yes' }, { value: 'no', label: 'No, and tell the agent what to do' }] }],
    });
    await expect(q.locator('.question-head')).toHaveText('Main agent asks');
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
    await expect(q.locator('.question-head')).toHaveText('Main agent asks');
    // Drawn exactly as written, fence markers included (#415): nothing in it is read as Markdown.
    expect(await q.locator('.question-message').textContent()).toBe(permission.message);
    // The only answers are the two decisions.
    await expect(q.locator('.question-message button')).toHaveCount(0);
    await expect(q.getByRole('button').and(page.locator('button:not(.md-copy)'))).toHaveText(['Approve once', 'Deny']);
    await expect(q.getByRole('button', { name: 'Approve once' })).toHaveClass(/\btone-allow\b/);
    await expect(q.getByRole('button', { name: 'Deny' })).toHaveClass(/\btone-deny\b/);

    await q.getByRole('button', { name: 'Approve once' }).tap();
    await expect(q).toHaveCount(0);
    expect((await log(page))[0]).toEqual({ id: COPILOT, question: 'r1#0123456789abcdef', request: expect.stringMatching(UUID), fields: { decision: 'approve' }, progress: true });
  });

  // Copilot asks a permission for each of a batch of parallel tool calls and shows them one at a
  // time (#690): the card says how many wait, and the next comes as its own question.
  test('permissions asked together are shown one at a time saying how many wait, each answered by its own id', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, { ...permission, waiting: 4 }, COPILOT);
    await expect(q.locator('.note.queue')).toHaveText('1 of 4 permissions waiting');
    await q.getByRole('button', { name: 'Approve once' }).tap();
    await expect(q).toHaveCount(0);

    const next = await ask(page, { ...permission, id: 'r2#fedcba9876543210', waiting: 3 }, COPILOT);
    await expect(next.locator('.note.queue')).toHaveText('1 of 3 permissions waiting');
    await next.getByRole('button', { name: 'Approve once' }).tap();
    await expect(next).toHaveCount(0);
    expect((await log(page)).map((a) => a.question)).toEqual(['r1#0123456789abcdef', 'r2#fedcba9876543210']);

    // The last one waiting says nothing of a queue.
    const last = await ask(page, { ...permission, id: 'r3#00112233aabbccdd' }, COPILOT);
    await expect(last.locator('.note.queue')).toHaveCount(0);
  });

  test('a Copilot permission prompt at a machine with approving turned off says so and offers nothing', async ({ page }) => {
    await openDemo(page, '&approve=0');
    const q = await ask(page, { ...permission, answerAtMachine: true }, COPILOT);
    await expect(q).toHaveAttribute('data-answerable', 'false');
    await expect(q.locator('.question-message')).toContainText('git push origin main --force-with-lease');
    await expect(q.locator('.read-only-why')).toHaveText('Approving permission prompts from the phone is turned off on this machine. Answer at the machine on Demo machine.');
    // Nothing that answers.
    await expect(q.locator(':is(button, input, select, textarea):not(.md-copy)')).toHaveCount(0);
  });

  test('every control is at least 44 px tall', async ({ page }) => {
    await openDemo(page);
    await ask(page, choice({ allowOther: true }));
    const measure = () => page.locator('.question.answerable').evaluateAll((qs) => qs.flatMap((q) =>
      [...q.querySelectorAll('button:not([hidden]), label.opt, textarea, input[type="number"]')]
        .filter((e) => e.offsetParent !== null)
        .map((e) => ({ what: e.className || e.tagName, h: e.getBoundingClientRect().height }))));
    // This question, then the worked example's form, each in its session's view.
    const sizes = await measure();
    await open(page, COPILOT);
    sizes.push(...await measure());
    expect(sizes.length).toBeGreaterThan(10);
    for (const s of sizes) { expect(s.h, s.what).toBeGreaterThanOrEqual(44); }
  });
});

test.describe('outcomes', () => {
  test.use({ viewport: { width: 390, height: 664 } });

  // The machine's result can reach the phone before its update taking the question away: the card
  // ends as it does the other way round. The demo host's question-clearing upsert is held back here
  // until the result is in.
  const resultFirst = (page) => page.evaluate(() => {
    const h = window.agentBridgeDemo.host;
    const upsert = h.upsert.bind(h);
    const held = [];
    h.upsert = (s) => { if (s.question === null) { held.push(s); } else { upsert(s); } };
    window.__questionGoes = () => { h.upsert = upsert; for (const s of held) { upsert(s); } };
  });

  test('a delivered result before its question goes keeps the panel for the rest of its check, then resolves to You answered', async ({ page }) => {
    await openDemo(page);
    const q = await ask(page, choice());
    await resultFirst(page);
    // When the check went on, and when the panel went: a second apart, as when the question goes first.
    await q.evaluate((el) => {
      const option = el.querySelector('.choice[data-value="sqlite"]');
      const at = window.__panel = {};
      new MutationObserver(() => {
        if (at.checked === undefined && option.classList.contains('checked')) { at.checked = performance.now(); }
        if (at.gone === undefined && !el.isConnected) { at.gone = performance.now(); }
      }).observe(document.body, { subtree: true, childList: true, attributes: true, attributeFilter: ['class'] });
    });
    const sqlite = q.locator('.choice[data-value="sqlite"]');
    await sqlite.click();
    await expect(sqlite).toHaveClass(/\bchecked\b/);
    await page.evaluate(() => window.__questionGoes());
    await expect(card(page, CODEX)).toHaveAttribute('data-status', 'working');
    await expect(q).toHaveCount(0);
    await expect(card(page, CODEX).locator('.answer-receipt')).toHaveText('You answered · SQLite');
    const at = await page.evaluate(() => window.__panel);
    expect(at.gone - at.checked).toBeGreaterThanOrEqual(900);
  });

  test('an unconfirmed result before its question goes keeps the panel as the usual order does: the option pressed, the rest disabled, saying why', async ({ page }) => {
    await openDemo(page);
    await script(page, 'unconfirmed');
    const q = await ask(page, choice());
    await resultFirst(page);
    await q.locator('.choice[data-value="pg"]').click();
    await expect(q).toHaveAttribute('data-answer', 'unconfirmed');
    await page.evaluate(() => window.__questionGoes());
    await expect(card(page, CODEX)).toHaveAttribute('data-status', 'working');
    // Past any check's hold: the panel is there to stay, not on its way out.
    await page.waitForTimeout(1500);
    await expect(q).toHaveCount(1);
    await expect(q.locator('.answer-status')).toHaveText('Sent, but not confirmed. Check the conversation. (the session stopped answering)');
    await expect(q.locator('.choice[data-value="pg"]')).toHaveAttribute('aria-pressed', 'true');
    await expect(q.locator('.choice[data-value="sqlite"]')).toBeDisabled();
    await expect(card(page, CODEX).locator('.answer-receipt')).toHaveCount(0);
  });

  test('unconfirmed says to check the conversation, keeps the option that went pressed and the rest disabled, and nothing offers to send it again', async ({ page }) => {
    await openDemo(page);
    await script(page, 'unconfirmed');
    const q = await ask(page, choice());
    await q.locator('.choice[data-value="pg"]').click();
    // The host typed it and took the question away, but it may not have gone in: one attempt per
    // prompt (ui.md §6.5), so the question stays as it went, saying so.
    await expect(q.locator('.answer-status')).toHaveText('Sent, but not confirmed. Check the conversation. (the session stopped answering)');
    await expect(q).toHaveAttribute('data-answer', 'unconfirmed');
    await expect(q.locator('.choice[data-value="pg"]')).toHaveAttribute('aria-pressed', 'true');
    expect(await q.locator('.choice[data-value="pg"]').evaluate((b) => getComputedStyle(b).opacity)).toBe('1');
    await expect(q.locator('.choice[data-value="sqlite"]')).toBeDisabled();
    await page.waitForTimeout(500);
    expect(await log(page)).toHaveLength(1);
    await expect(card(page, CODEX).getByRole('button', { name: 'Try again' })).toHaveCount(0);
  });

  test('an outcome this app doesn\'t know reads as unconfirmed', async ({ page }) => {
    await openDemo(page);
    await script(page, { outcome: 'queued', reason: 'a newer host' });
    const q = await ask(page, choice());
    await q.locator('.choice[data-value="pg"]').click();
    await expect(q.locator('.answer-status')).toHaveText('Sent, but not confirmed. Check the conversation. (a newer host)');
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
    await expect(q).toHaveAttribute('data-answer', /^(sending|waiting)$/);
    await expect(card(page, CODEX).locator('.answer-receipt')).toHaveText('You answered · PostgreSQL (Recommended)', { timeout: 10000 });
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
    await expect(q.locator('.answer')).toHaveAccessibleName('Main agent asks');
    await expect(q.getByRole('group', { name: 'Mode required Choose one' })).toBeVisible();
    // Recommended first, so the first radio Tab reaches from the question's heading is Full.
    await q.locator('.question-head').evaluate((e) => { e.tabIndex = -1; e.focus(); });
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
