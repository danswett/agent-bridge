// Needs you · N (ui.md §6.1, #165), in demo mode: every pending question across machines, oldest
// first, above the list and never moving its rows; Approve once and Deny in the lane, behaving
// exactly as the card's buttons do; forms opening their session; prompts only the machine can
// answer saying so; delegates named; and the app badge carrying the count.
import { test, expect } from '@playwright/test';

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
const card = (page, id, host = 'demo') => page.locator(`.session[data-host-id="${host}"][data-id="${id}"]`);
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
  return entryFor(page, id, host);
}

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
    await expect(e.locator('.needs-prompt pre')).toHaveText('git push origin main --force-with-lease');
    // Copying the command aside, the entry's buttons are its title, Approve once and Deny.
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
    await expect(e.getByRole('button', { name: /^Deny/ })).toBeEnabled();
    await e.getByRole('button', { name: /^Deny/ }).click();
    await expect(entryFor(page, LAND)).toHaveCount(0);
    const [first, second] = await log(page);
    expect([first.fields.decision, second.fields.decision]).toEqual(['approve', 'deny']);
    expect(second.request).not.toBe(first.request);
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
    await e.getByRole('button', { name: 'Try again' }).click();
    await expect(entryFor(page, LAND)).toHaveCount(0);
    const [first, second] = await log(page);
    expect(second.request).toBe(first.request);
  });

  test('an answer sent from the card shows in the lane too', async ({ page }) => {
    await openDemo(page);
    await script(page, 'stale');
    const e = await ask(page, LAND, permission());
    await card(page, LAND).locator('.question .choice[data-value="approve"]').click();
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
    await card(page, LAND).locator('.fold').click();
    await expect(card(page, LAND).locator('.fold')).toHaveAttribute('aria-expanded', 'false');
    await e.getByRole('button', { name: /open the session/ }).tap();
    await expect(card(page, LAND).locator('.fold')).toHaveAttribute('aria-expanded', 'true');
    await expect(card(page, LAND).locator('.fold')).toBeFocused();
    await expect(card(page, LAND).locator('.question')).toBeInViewport();
  });

  test('an approval shows its whole prompt, the command as written, and one too long to show whole opens its session instead', async ({ page }) => {
    await openDemo(page);
    const e = await ask(page, LAND, permission({ message: 'Allow **shell**?\n\n```\nrm -rf build/**/cache && python __init__.py\n```' }));
    await expect(e).toHaveAttribute('data-mode', 'approve');
    await expect(e.locator('.needs-prompt pre')).toHaveText('rm -rf build/**/cache && python __init__.py');
    const diff = Array.from({ length: 20 }, (_, i) => `+line ${i}`).join('\n');
    const long = await ask(page, COPILOT, permission({ id: 'r2#long', message: `Allow **edit**?\n\n\`\`\`\nsrc/app.js\n\`\`\`\n\n\`\`\`diff\n${diff}\n\`\`\`` }));
    await expect(long).toHaveAttribute('data-mode', 'open');
    await expect(long.locator('.needs-line')).toHaveText('Allow edit?');
    await expect(long.locator('.needs-prompt, .needs-actions')).toHaveCount(0);
  });

  test('a Codex approval opens its session: its command may have reached the host cut short', async ({ page }) => {
    await openDemo(page);
    const e = await ask(page, CODEX, permission());
    await expect(e).toHaveAttribute('data-mode', 'open');
    await expect(e.locator('.needs-actions')).toHaveCount(0);
    await expect(card(page, CODEX).locator('.question')).toHaveAttribute('data-answerable', 'true');
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
    await expect(e.locator('.needs-delegate')).toHaveText('Read witness shell logic asks');
    await expect(e.locator('.needs-meta')).toHaveText(/^Copilot · Read witness shell logic asks · \d+s$/);
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
