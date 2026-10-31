// Starting and resuming a session from the phone, in demo mode: the simulated host launches as
// a real one does (session.launch): it refuses what it does not offer, in its words, and a
// started session arrives as any other, with the panel jumping to its card.
import { test, expect } from '@playwright/test';

const RESUMABLE = '7d3c1a2e-5b6f-4c8d-9e0a-1b2c3d4e5f60';
const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

const panel = (page) => page.locator('#launch');
const field = (page, name) => panel(page).locator(`[name="${name}"]`);
const status = (page) => panel(page).locator('.launch-status');
const launchButton = (page) => panel(page).getByRole('button', { name: 'Launch' });
const log = (page) => page.evaluate(() => window.agentBridgeDemo.host.launchLog);
const script = (page, ...outcomes) => page.evaluate((o) => window.agentBridgeDemo.scriptLaunches(...o), outcomes);

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1&answerDelay=300${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

async function openPanel(page) {
  await panel(page).getByRole('button', { name: 'Start a session on Demo machine' }).click();
  await expect(field(page, 'agent')).toBeVisible();
}

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });

  test('a launch sends every setting and the whole prompt, then jumps to the new session\'s card', async ({ page }) => {
    await openDemo(page);
    await openPanel(page);
    // The machine's defaults are what is picked to begin with.
    await expect(field(page, 'agent')).toHaveValue('copilot');
    await expect(field(page, 'workspace')).toHaveValue('agent-bridge');
    await expect(field(page, 'model')).toHaveValue('claude-opus-5.5');
    await expect(field(page, 'effort')).toHaveValue('');
    await expect(field(page, 'permissions')).toHaveValue('ask');
    await expect(field(page, 'resume')).toHaveValue('');

    await field(page, 'model').selectOption('gpt-6-sol');
    await field(page, 'effort').selectOption('high');
    await field(page, 'permissions').selectOption('allowAll');
    // Far past the old card's 255 characters, with a line break the machine flattens.
    const prompt = `Read docs/design.md first.\n${'Then implement the next slice carefully. '.repeat(20)}`;
    await field(page, 'prompt').fill(prompt);
    await launchButton(page).tap();

    await expect(status(page)).toHaveText('Session started in a new worktree.');
    await expect(panel(page)).toHaveAttribute('data-launch', 'started');
    const [sent] = await log(page);
    expect(sent).toEqual({
      request: expect.stringMatching(UUID), agent: 'copilot', workspace: 'agent-bridge',
      model: 'gpt-6-sol', effort: 'high', context: '', permissions: 'allowAll', prompt,
    });
    const id = await page.evaluate(() => window.agentBridgeDemo.launches.get('demo').detail.session);
    const card = page.locator(`.session[data-id="${id}"]`);
    await expect(card).toBeInViewport();
    await expect(card).toHaveAttribute('data-status', 'working');
    await expect(field(page, 'prompt')).toHaveValue('');
  });

  test('resuming a session reopens it in its own folder: no workspace is sent, and it leaves the list', async ({ page }) => {
    await openDemo(page);
    await openPanel(page);
    await field(page, 'resume').selectOption(RESUMABLE);
    await expect(field(page, 'workspace')).toBeHidden();
    await expect(panel(page).locator('.launch-note')).toHaveText('A resumed session reopens in its own folder.');
    // A Copilot session resumes only in a launcher for Copilot sessions.
    await expect(field(page, 'agent').locator('option')).toHaveText(['Copilot']);
    await launchButton(page).tap();

    await expect(status(page)).toHaveText('Session started.');
    const [sent] = await log(page);
    expect(sent.resume).toBe(RESUMABLE);
    expect(sent.workspace).toBeUndefined();
    await expect(page.locator(`.session[data-id="${RESUMABLE}"]`)).toBeInViewport();
    await expect(field(page, 'resume').locator(`option[value="${RESUMABLE}"]`)).toHaveCount(0);
  });

  test('an agent that starts only on a first message is not launched without one, and nothing is sent', async ({ page }) => {
    await openDemo(page);
    await openPanel(page);
    await field(page, 'agent').selectOption('codex');
    // Codex offers no context sizes, so there is nothing to pick.
    await expect(field(page, 'context')).toBeHidden();
    await expect(field(page, 'model')).toHaveValue('');
    await launchButton(page).tap();
    await expect(status(page)).toHaveText('Not launched: Codex starts its session only on a first message; give it a prompt.');
    await field(page, 'prompt').fill('/clear');
    await launchButton(page).tap();
    await expect(status(page)).toHaveText(/^Not launched: a prompt cannot start with \//);
    expect(await log(page)).toEqual([]);
  });

  test('busy says why and starts nothing, and the next press is a new launch', async ({ page }) => {
    await openDemo(page);
    await openPanel(page);
    await script(page, 'busy');
    await launchButton(page).tap();
    await expect(status(page)).toHaveText(/^Not launched: a session this machine launched is waiting at its folder trust prompt; answer it at the machine\. Try again in a moment\.$/);
    await expect(launchButton(page)).toBeEnabled();
    await launchButton(page).tap();
    await expect(status(page)).toHaveText('Session started in a new worktree.');
    const [first, second] = await log(page);
    expect(second.request).not.toBe(first.request);
  });

  test('a launch whose response is lost is resent with the same request, and starts once', async ({ page }) => {
    await openDemo(page, '&timeout=1000&retry=200');
    await openPanel(page);
    await script(page, { outcome: 'started', drop: true });
    await launchButton(page).tap();
    await expect(status(page)).toHaveText('Session started in a new worktree.', { timeout: 10000 });
    const sent = await log(page);
    expect(sent.length).toBeGreaterThan(1);
    expect(new Set(sent.map((s) => s.request)).size).toBe(1);
    expect(await page.evaluate(() => window.agentBridgeDemo.host.launches.size)).toBe(1);
  });

  test('a machine with no approved folder says how to approve one at the machine', async ({ page }) => {
    await openDemo(page);
    await page.evaluate(() => { const h = window.agentBridgeDemo.host; h.launchOptions = { ...h.launchOptions, workspaces: [], resumable: [] }; });
    await openPanel(page);
    await expect(panel(page).locator('.launch-note')).toHaveText('No folder on Demo machine is approved for launching yet. At the machine, run: agent-bridge-host workspace add <folder>');
    await launchButton(page).tap();
    await expect(status(page)).toHaveText('Not launched: no workspace is approved on this machine yet; approve one at the machine (agent-bridge-host workspace add <folder>).');
    expect(await log(page)).toEqual([]);
  });

  test('a machine that cannot launch shows no Launch at all', async ({ page }) => {
    await openDemo(page, '&launch=0');
    await expect(panel(page)).toBeHidden();
    expect(await page.evaluate(() => window.agentBridgeDemo.calls)).not.toContain('launch.options');
  });
});

// Bringing a session that ended on another machine (docs/protocol/v1-transfer.md): Studio PC, the
// demo's second machine, offers two; the launch carries a command this page signed for it.
test.describe('bringing a session from another machine', () => {
  test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });

  const AWAY = '2b4e6f80-1a3c-4d5e-8f70-9a0b1c2d3e4f';
  const CODEX_AWAY = '5c6d7e8f-9a0b-4c1d-8e2f-3a4b5c6d7e8f';
  const body = (b64) => JSON.parse(Buffer.from(b64, 'base64url').toString('utf8'));

  test('its sessions are listed by machine, and one opens here as a copy in the workspace picked', async ({ page }) => {
    await openDemo(page);
    await openPanel(page);
    await expect(field(page, 'resume').locator('option')).toHaveText(['New session', 'Fix the flaky relay test · agent-bridge', 'Draft the transfer docs · on Studio PC', 'Profile the relay under load · on Studio PC']);
    await field(page, 'resume').selectOption(`@demo-studio/${AWAY}`);
    // Its folder is on Studio PC: it opens in a workspace here, picked like a new session's.
    await expect(field(page, 'workspace')).toBeVisible();
    await expect(field(page, 'agent').locator('option')).toHaveText(['Copilot']);
    await expect(panel(page).locator('.launch-note')).toContainText('from Studio PC to Demo machine');
    await expect(panel(page).locator('.launch-note')).toContainText('It stays on Studio PC as it was.');
    await field(page, 'workspace').selectOption('notes');
    await launchButton(page).tap();

    await expect(status(page)).toHaveText('Session brought over and started. The original is still on its machine.');
    const [sent] = await log(page);
    expect(sent).toMatchObject({ agent: 'copilot', workspace: 'notes', transfer: { from: 'demo-studio', session: AWAY } });
    expect(sent.resume).toBeUndefined();
    const command = body(sent.transfer.command.body);
    expect(command).toMatchObject({ v: 1, action: 'session.transfer', request: expect.stringMatching(UUID), principal: { kind: 'user' }, target: { session: AWAY }, to: 'demo' });
    expect(command.expiresAt - command.issuedAt).toBe(300);
    expect(sent.transfer.command.mac).toMatch(/^[A-Za-z0-9_-]{43}$/);
    // A new session, under a new id, with the original's title.
    const id = await page.evaluate(() => window.agentBridgeDemo.launches.get('demo').detail.session);
    expect(id).not.toBe(AWAY);
    const card = page.locator(`.session[data-id="${id}"]`);
    await expect(card).toBeInViewport();
    await expect(card).toContainText('Draft the transfer docs');
  });

  test("a machine slow to say what it offers does not hold up this one's choices", async ({ page }) => {
    await openDemo(page);
    await page.evaluate(() => { window.agentBridgeDemo.host.remoteDelayMs = 2500; });
    await openPanel(page);
    await expect(field(page, 'resume').locator('option')).toHaveText(['New session', 'Fix the flaky relay test · agent-bridge']);
    await field(page, 'resume').selectOption(RESUMABLE);
    // A presence update for the same machine meanwhile does not lose them.
    await page.evaluate(() => window.agentBridgeDemo.retarget());
    // They join the list when they come, and what was picked stays picked.
    await expect(field(page, 'resume').locator('option')).toHaveCount(4, { timeout: 10000 });
    await expect(field(page, 'resume')).toHaveValue(RESUMABLE);
  });
  test('a resend after a lost response carries the same signed command', async ({ page }) => {
    await openDemo(page, '&timeout=1000&retry=200');
    await openPanel(page);
    await script(page, { outcome: 'started', drop: true });
    await field(page, 'resume').selectOption(`@demo-studio/${CODEX_AWAY}`);
    await field(page, 'prompt').fill('Carry on from the last profile.');
    await launchButton(page).tap();
    await expect(status(page)).toHaveText(/^Session brought over and started in a new worktree\./, { timeout: 10000 });
    const sent = await log(page);
    expect(sent.length).toBeGreaterThan(1);
    expect(new Set(sent.map((s) => JSON.stringify(s.transfer))).size).toBe(1);
    expect(sent[0].agent).toBe('codex');
  });

  test("the other machine's refusal is shown in its words", async ({ page }) => {
    await openDemo(page);
    await openPanel(page);
    await field(page, 'resume').selectOption(`@demo-studio/${AWAY}`);
    // It ended up running again on Studio PC while the panel was open.
    await page.evaluate((id) => { const h = window.agentBridgeDemo.host; h.remote.resumable = h.remote.resumable.filter((r) => r.id !== id); }, AWAY);
    await launchButton(page).tap();
    await expect(status(page)).toHaveText('Not launched: the session could not be brought from the other machine: Studio PC refused: that session is not one its machine offers (it is not on its resume list).');
  });

  test('a machine that takes no sessions from others lists only its own', async ({ page }) => {
    await openDemo(page, '&transfer=0');
    await openPanel(page);
    await expect(field(page, 'resume').locator('option')).toHaveText(['New session', 'Fix the flaky relay test · agent-bridge']);
  });
});

// A launched agent asking whether to trust its folder (launch.trust): shown in full, and answered
// from here with a command this device signs, only where the machine allows it.
test.describe('a folder-trust question', () => {
  test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });

  const LONG = 'C:\\Users\\demo\\repos\\a-rather-long-folder-name-that-will-not-fit-on-one-phone-line\\and-a-subfolder\\web';
  const QUESTION = {
    id: '5d0c9f3e-2b1a-4c8d-9e7f-6a5b4c3d2e1f', agent: 'codex', kind: 'codex', label: 'Codex', workspace: 'agent-bridge', folder: LONG,
    root: 'c:\\users\\demo\\repos\\a-rather-long-folder-name-that-will-not-fit-on-one-phone-line',
    rootNote: 'Note: You’re in a subdirectory of a Git project. Trusting will apply to the repository root:',
    warning: 'Trust this folder? Codex can read, edit, and run files here, subject to your permission settings. Folder settings can run code automatically, even without a model request. Continue only if you trust these files. Your trust decision will be saved.',
    answerable: true,
  };
  const card = (page) => panel(page).locator('.trust-question');
  const trustLog = (page) => page.evaluate(() => window.agentBridgeDemo.host.trustLog);
  const ask = (page, q) => page.evaluate((x) => window.agentBridgeDemo.askTrust(x), q);

  test('a launch that meets one shows the agent, the whole folder, its repository root and its own words, and Trust starts the session', async ({ page }) => {
    await openDemo(page);
    await openPanel(page);
    await script(page, { outcome: 'unconfirmed', reason: 'Codex started, and is asking whether to trust agent-bridge. A person can answer that here or at the machine; the session appears here once they do', trust: QUESTION });
    await field(page, 'agent').selectOption('codex');
    await field(page, 'prompt').fill('Add pagination.');
    await launchButton(page).tap();

    await expect(card(page)).toHaveCount(1);
    await expect(card(page).locator('h3')).toHaveText('Codex on Demo machine is asking whether to trust a folder');
    await expect(card(page).locator('.trust-folder')).toHaveText(LONG);
    await expect(card(page).locator('.trust-root')).toHaveText(QUESTION.root);
    await expect(card(page).locator('.trust-root-note')).toHaveText(QUESTION.rootNote);
    await expect(card(page).locator('.trust-warning')).toHaveText(QUESTION.warning);
    // The whole path is on screen, wrapped, never cut short.
    const fits = await card(page).locator('.trust-folder').evaluate((e) => e.scrollWidth <= e.clientWidth + 1);
    expect(fits).toBe(true);

    await card(page).getByRole('button', { name: 'Trust' }).tap();
    await expect(card(page).locator('.trust-status')).toHaveText('Trusted. Codex started.');
    await expect(card(page).getByRole('button', { name: 'Trust' })).toHaveCount(0);
    const [sent] = await trustLog(page);
    expect(sent.question).toBe(QUESTION.id);
    expect(sent.choice).toBe('trust');
    const body = JSON.parse(Buffer.from(sent.command.body, 'base64url').toString('utf8'));
    expect(body).toMatchObject({ v: 1, action: 'launch.trust:trust', principal: { kind: 'user' }, target: { session: QUESTION.id } });
  });

  test('Quit says the agent was not trusted and closed', async ({ page }) => {
    await openDemo(page);
    await ask(page, { ...QUESTION, warning: null, root: null, rootNote: null });
    await openPanel(page);
    await expect(card(page).locator('.trust-root')).toHaveCount(0);
    await card(page).getByRole('button', { name: 'Quit' }).tap();
    await expect(card(page).locator('.trust-status')).toHaveText('Not trusted. Codex was not trusted with agent-bridge, and closed; nothing was launched.');
    expect((await trustLog(page))[0].choice).toBe('quit');
  });

  test('where the machine does not allow it, it is shown to answer at the machine, with how to allow it', async ({ page }) => {
    await openDemo(page);
    const atMachine = 'Answer at the machine. To answer from your devices, turn on "Answer folder-trust questions from my devices" in the Agent Bridge app\'s Settings on that machine, or run agent-bridge-host config set launch.remoteFolderTrust true there and restart the host.';
    await ask(page, { ...QUESTION, answerable: false, atMachine });
    await openPanel(page);
    await expect(card(page).locator('.trust-folder')).toHaveText(LONG);
    await expect(card(page).locator('.trust-at-machine')).toHaveText(atMachine);
    await expect(card(page).getByRole('button')).toHaveCount(0);
  });

  test('a machine from before launch.trust shows it read-only', async ({ page }) => {
    await openDemo(page, '&trust=0');
    await ask(page, QUESTION);
    await openPanel(page);
    await expect(card(page).locator('.trust-at-machine')).toHaveText('Answer at the machine: this app cannot answer it on Demo machine.');
    await expect(card(page).getByRole('button')).toHaveCount(0);
  });

  test('an answer whose effect the machine could not see says to answer at the machine, and is not sent again', async ({ page }) => {
    await openDemo(page);
    await ask(page, QUESTION);
    await page.evaluate(() => window.agentBridgeDemo.scriptTrust('unconfirmed'));
    await openPanel(page);
    await card(page).getByRole('button', { name: 'Trust' }).tap();
    await expect(card(page).locator('.trust-status')).toHaveText('Not confirmed: answer at the machine. The question is still in Codex\'s window.');
    await expect(card(page).getByRole('button')).toHaveCount(0);
    expect(await trustLog(page)).toHaveLength(1);
    // Shown again after another machine, or none, was picked: still not offered.
    await page.evaluate(() => window.agentBridgeDemo.launchPanel.setTarget(null));
    await page.evaluate(() => window.agentBridgeDemo.retarget());
    await expect(card(page).locator('.trust-status')).toHaveText('Not confirmed: answer at the machine. The question is still in Codex\'s window.');
    await expect(card(page).getByRole('button')).toHaveCount(0);
  });

  test('an answer whose response was lost offers only that same command again, which gets the first outcome', async ({ page }) => {
    await openDemo(page, '&timeout=1000');
    await ask(page, QUESTION);
    await page.evaluate(() => window.agentBridgeDemo.scriptTrust({ outcome: 'started', drop: true }));
    await openPanel(page);
    await card(page).getByRole('button', { name: 'Trust' }).tap();
    await expect(card(page).getByRole('button', { name: 'Try again' })).toBeVisible({ timeout: 10000 });
    await expect(card(page).getByRole('button', { name: 'Trust' })).toHaveCount(0);
    await expect(card(page).getByRole('button', { name: 'Quit' })).toHaveCount(0);
    await card(page).getByRole('button', { name: 'Try again' }).tap();
    await expect(card(page).locator('.trust-status')).toHaveText('Trusted. Codex started.');
    const sent = await trustLog(page);
    expect(sent).toHaveLength(2);
    expect(sent[1]).toEqual(sent[0]);
  });

  test('a lost answer keeps its Try again while the machine is asked for its choices again', async ({ page }) => {
    await openDemo(page, '&timeout=1000');
    await ask(page, QUESTION);
    await page.evaluate(() => window.agentBridgeDemo.scriptTrust({ outcome: 'started', drop: true }));
    await openPanel(page);
    await card(page).getByRole('button', { name: 'Trust' }).tap();
    await expect(card(page).getByRole('button', { name: 'Try again' })).toBeVisible({ timeout: 10000 });
    // Another machine picked and this one again, whose choices are slow to come.
    await page.evaluate(() => {
      const d = window.agentBridgeDemo;
      d.launchPanel.setTarget(null);
      d.client.launchOptions = () => new Promise(() => {});
      d.retarget();
    });
    await expect(panel(page).locator('.launch-note')).toHaveText('Getting the choices from Demo machine…');
    await expect(card(page).getByRole('button', { name: 'Try again' })).toBeVisible();
  });

  test('a resend refused because its command expired is not confirmed, and Trust is not offered again', async ({ page }) => {
    await openDemo(page, '&timeout=1000');
    await ask(page, QUESTION);
    await page.evaluate(() => window.agentBridgeDemo.scriptTrust({ outcome: 'started', drop: true, expire: true }));
    await openPanel(page);
    await card(page).getByRole('button', { name: 'Trust' }).tap();
    await card(page).getByRole('button', { name: 'Try again' }).tap({ timeout: 10000 });
    await expect(card(page).locator('.trust-status')).toHaveText('Not confirmed: answer at the machine. The first answer may have reached it, and the resend was refused (the command has expired).');
    await expect(card(page).getByRole('button')).toHaveCount(0);
  });

  test('a refused answer to a question that has since gone is not offered again', async ({ page }) => {
    await openDemo(page);
    await ask(page, QUESTION);
    await page.evaluate(() => window.agentBridgeDemo.scriptTrust({ outcome: 'refused', reason: 'that question is no longer waiting: it was answered, or the agent closed' }));
    await openPanel(page);
    // Answered at the machine meanwhile: the machine no longer lists it.
    await page.evaluate(() => { const h = window.agentBridgeDemo.host; h.launchOptions = { ...h.launchOptions, trust: [] }; });
    await card(page).getByRole('button', { name: 'Trust' }).tap();
    await expect(card(page).locator('.trust-status')).toHaveText(/^Not answered/);
    await expect(card(page).locator('.trust-at-machine')).toHaveText('Demo machine no longer lists this question as waiting.');
    await expect(card(page).getByRole('button')).toHaveCount(0);
  });

  test('a Trust button with focus keeps it when the machine is redrawn', async ({ page }) => {
    await openDemo(page);
    await ask(page, QUESTION);
    await openPanel(page);
    await card(page).getByRole('button', { name: 'Trust' }).focus();
    await page.evaluate(() => window.agentBridgeDemo.retarget());
    await expect(card(page).getByRole('button', { name: 'Trust' })).toBeFocused();
  });

  test('a machine that stops being read-only is asked again, and Trust is offered at once', async ({ page }) => {
    await openDemo(page);
    const reason = 'agent-ha-bridge is running for this user (AgentBridgeDaemon)';
    await ask(page, { ...QUESTION, answerable: false, atMachine: `Answer at the machine: ${reason}.` });
    await page.evaluate((why) => { const d = window.agentBridgeDemo; d.launchPanel.setTarget({ ...d.target(), transfers: null, trust: null, inspectOnly: true, readOnly: why }); }, reason);
    await panel(page).getByRole('button', { name: 'Waiting on Demo machine' }).click();
    await expect(card(page).locator('.trust-at-machine')).toHaveText(`Answer at the machine: ${reason}.`);

    // agent-ha-bridge stopped: the machine answers again, and says the question can be answered.
    await ask(page, QUESTION);
    await page.evaluate(() => window.agentBridgeDemo.retarget());
    await expect(card(page).getByRole('button', { name: 'Trust' })).toBeVisible();
  });

  test('a busy launch that names a question asks again, which may offer Trust now', async ({ page }) => {
    await openDemo(page);
    await ask(page, { ...QUESTION, answerable: false, atMachine: 'Answer at the machine. No device is approved yet.' });
    await openPanel(page);
    await expect(card(page).locator('.trust-at-machine')).toBeVisible();
    // Approved at the machine since: the busy launch's question, and the machine's list, say so.
    await script(page, { outcome: 'busy', reason: 'a session this machine launched is waiting at its folder trust prompt', trust: QUESTION });
    await field(page, 'prompt').fill('Go.');
    await launchButton(page).tap();
    await expect(card(page).getByRole('button', { name: 'Trust' })).toBeVisible();
  });

  test('a machine where agent-ha-bridge runs shows a waiting question read-only, and nothing to launch', async ({ page }) => {
    await page.addInitScript(() => { window.__cspViolations = []; });
    await page.goto('?demo=1&static=1&ahb=1');
    await page.waitForSelector('html[data-demo-ready]');
    await expect(panel(page)).toBeHidden();
    const atMachine = 'Answer at the machine: agent-ha-bridge is running for this user (AgentBridgeDaemon).';
    await ask(page, { ...QUESTION, answerable: false, atMachine });
    await page.evaluate(() => window.agentBridgeDemo.retarget());
    await page.evaluate(() => window.agentBridgeDemo.launchPanel.refresh());
    await panel(page).getByRole('button', { name: 'Waiting on Demo machine' }).click();
    await expect(card(page).locator('.trust-folder')).toHaveText(LONG);
    await expect(card(page).locator('.trust-at-machine')).toHaveText(atMachine);
    await expect(card(page).getByRole('button')).toHaveCount(0);
    await expect(panel(page).locator('.launch-note')).toHaveText(/^Sessions can't be started on Demo machine now: agent-ha-bridge is running/);
    await expect(launchButton(page)).toBeHidden();
  });
});