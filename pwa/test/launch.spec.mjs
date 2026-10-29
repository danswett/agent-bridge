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
    await expect(field(page, 'resume')).toBeHidden();
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
