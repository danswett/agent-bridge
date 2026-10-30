// Agent usage and the machine status line, in demo mode: the simulated host offers usage, and
// the tests hand it reports to send as usage.update, drawn as agent-ha-bridge's usage card
// draws them.
import { test, expect } from '@playwright/test';

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

// A report relative to now (the page shares this machine's clock), so pace and resets read
// the same on any day.
async function sendUsage(page, build) {
  const now = Date.now();
  const at = (ms) => new Date(now + ms).toISOString();
  await page.evaluate((r) => window.agentBridgeDemo.usage(r), build(at));
}
const report = (at) => ({
  agents: [
    {
      agent: 'claude', label: 'Claude Code', accountKey: '3f9a2c71b0de', plan: 'max', source: 'api', measuredAt: at(-4 * 60000),
      windows: [
        { key: 'session', label: 'Session (5h)', percent: 12, resetsAt: at(3 * 3600000) },
        { key: 'weekly_all', label: 'Weekly', percent: 41.3, startsAt: at(-2 * 86400000), resetsAt: at(5 * 86400000) },
      ],
    },
    {
      agent: 'copilot', label: 'GitHub Copilot', account: 'danswett', plan: 'enterprise', source: 'api', measuredAt: at(-60000),
      windows: [{ key: 'plan', label: 'Plan', percent: 78.9, used: 1183313, limit: 1500000, unit: 'AIC', startsAt: at(-27 * 86400000), resetsAt: at(4 * 86400000) }],
    },
    {
      agent: 'codex', label: 'Codex', plan: 'plus', source: 'transcript', measuredAt: at(-2 * 3600000),
      windows: [{ key: 'primary', label: '5h limit', percent: 23.5, resetsAt: at(2 * 3600000) }, { key: 'secondary', label: 'Weekly', percent: 61, resetsAt: at(3 * 86400000) }],
    },
  ],
});

test('each agent is a row with its windows, fullest first, read as agent-ha-bridge reads them', async ({ page }) => {
  await openDemo(page);
  await sendUsage(page, report);
  const usage = page.locator('#usage');
  await expect(usage).toBeVisible();

  // The folded line: each agent's fullest window, in its own colour.
  await expect(usage.locator('.usage-summary')).toHaveText('GitHub Copilot 79% · Codex 61% · Claude Code 41%');
  await expect(usage.locator('.usage-figure.warn')).toHaveText('GitHub Copilot 79%');
  await expect(usage.locator('.usage-group')).toHaveCount(3);
  expect(await usage.locator('.usage-group').evaluateAll((els) => els.map((e) => e.dataset.agent))).toEqual(['copilot', 'codex', 'claude']);

  const copilot = usage.locator('.usage-group[data-agent="copilot"]');
  await expect(copilot.locator('.usage-account')).toHaveText('danswett · enterprise');
  await expect(copilot.locator('.usage-age')).toHaveText('just now');
  await expect(copilot.locator('.usage-percent')).toHaveText('78.9%');
  await expect(copilot.locator('.usage-fill')).toHaveClass(/warn/);
  await expect(copilot.locator('.usage-amount')).toHaveText('1,183,313 / 1,500,000 AIC');
  await expect(copilot.locator('.usage-reset')).toHaveText('resets in 4d');
  // 27 days of 31 gone, 78.9% spent: within a tenth of even.
  await expect(copilot.locator('.usage-pacemark')).toHaveText('│ even pace 87%');
  await expect(copilot.locator('.usage-verdict')).toHaveText('on pace');
  await expect(copilot.locator('.usage-pace')).toHaveCount(1);
  const bar = copilot.locator('.usage-bar');
  await expect(bar).toHaveAttribute('aria-valuenow', '78.9');
  const fillWidth = await copilot.locator('.usage-fill').evaluate((e) => e.getBoundingClientRect().width / e.parentElement.getBoundingClientRect().width);
  expect(fillWidth).toBeCloseTo(0.789, 2);

  // Claude's week started two days ago of seven: 29% gone, 41.3% spent.
  const claude = usage.locator('.usage-group[data-agent="claude"]');
  await expect(claude.locator('.usage-account')).toHaveText('max');
  await expect(claude.locator('.usage-window[data-key="session"] .usage-reset')).toHaveText('resets in 3h');
  await expect(claude.locator('.usage-window[data-key="session"] .usage-pace')).toHaveCount(0);
  await expect(claude.locator('.usage-window[data-key="weekly_all"] .usage-verdict')).toHaveText('13% ahead of pace');
  await expect(claude.locator('.usage-window[data-key="weekly_all"] .usage-verdict')).toHaveClass(/ahead/);

  // Codex's figure is from its last transcript, two hours old: stale, and with no pace (its
  // windows roll).
  const codex = usage.locator('.usage-group[data-agent="codex"]');
  await expect(codex.locator('.usage-age')).toHaveText('2h ago');
  await expect(codex.locator('.usage-age')).toHaveClass(/stale/);
  await expect(codex.locator('.usage-pace')).toHaveCount(0);

  expect(await page.evaluate(() => window.__cspViolations)).toEqual([]);
});

test('an update from the host redraws the bars, and an error shows only when there is no fresh reading', async ({ page }) => {
  await openDemo(page);
  await sendUsage(page, (at) => ({
    agents: [
      { agent: 'copilot', label: 'GitHub Copilot', account: 'danswett', source: 'cache', measuredAt: at(-60000), error: 'The Copilot quota could not be read: the request timed out', windows: [{ key: 'plan', label: 'Plan', percent: 93 }] },
      { agent: 'claude', label: 'Claude Code', source: 'none', measuredAt: at(0), error: 'The Claude usage could not be read: the server answered 500', windows: [] },
    ],
  }));
  const usage = page.locator('#usage');
  const copilot = usage.locator('.usage-group[data-agent="copilot"]');
  await expect(copilot.locator('.usage-fill')).toHaveClass(/crit/);
  await expect(usage.locator('.usage-figure.crit')).toHaveText('GitHub Copilot 93%');
  // A failed read beside a reading taken a minute ago is noise.
  await expect(copilot.locator('.usage-error')).toHaveCount(0);
  // With no reading at all, the failure is all there is to say.
  await expect(usage.locator('.usage-group[data-agent="claude"] .usage-error')).toHaveText('The Claude usage could not be read: the server answered 500');
  await expect(usage.locator('.usage-group[data-agent="claude"] .usage-window')).toHaveCount(0);
});

test('folding the usage panel is remembered across a reload', async ({ page }) => {
  await openDemo(page);
  const usage = page.locator('#usage');
  const toggle = usage.locator('.usage-toggle');
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  await expect(usage.locator('.usage-group')).toHaveCount(3);

  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await expect(usage.locator('.usage-body')).toBeHidden();
  await expect(usage.locator('.usage-summary')).toContainText('GitHub Copilot 79%');

  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  await expect(usage.locator('.usage-toggle')).toHaveAttribute('aria-expanded', 'false');
  await expect(usage.locator('.usage-body')).toBeHidden();
});

test('a machine that does not offer usage shows no usage panel', async ({ page }) => {
  await openDemo(page, '&usage=0');
  await expect(page.locator('.session').first()).toBeVisible();
  await expect(page.locator('#usage')).toBeHidden();
  expect(await page.evaluate(() => window.agentBridgeDemo.calls.filter((c) => c.startsWith('usage.')))).toEqual([]);
});

test('the machine line says how many sessions are live, the version and that it is a dev build', async ({ page }) => {
  await openDemo(page);
  const status = page.locator('.machine .machine-status');
  await expect(status).toHaveText(' · 4 sessions · 0.1.0 (dev)');
  await page.evaluate(() => window.agentBridgeDemo.remove('019a1b2c-codex-example'));
  await expect(status).toHaveText(' · 3 sessions · 0.1.0 (dev)');
});

test('the machine line says what holds its discovery until it clears, as the HA status line did', async ({ page }) => {
  await openDemo(page);
  const status = page.locator('.machine .machine-status');
  await page.evaluate(() => window.agentBridgeDemo.hold({ reason: 'an unrecognised copilot process (pid 4120)', agents: ['copilot'] }));
  await expect(status).toHaveText(' · 4 sessions · 0.1.0 (dev) · discovery held: an unrecognised copilot process (pid 4120)');
  await page.evaluate(() => window.agentBridgeDemo.hold(null));
  await expect(status).toHaveText(' · 4 sessions · 0.1.0 (dev)');
});

