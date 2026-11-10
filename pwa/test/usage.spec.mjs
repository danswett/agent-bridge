// Agent usage and the machine status line, in demo mode: the simulated host offers usage, and
// the tests hand it reports to send as usage.update, drawn as agent-ha-bridge's usage card
// draws them: on the Usage page, and in one line under the sessions.
import { test, expect } from '@playwright/test';
import { go } from './nav.mjs';

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
  // The line under the sessions: each agent's fullest window, in its own colour.
  const line = page.locator('#usage-line');
  await expect(line).toBeVisible();
  await expect(line.locator('.usage-summary')).toHaveText('GitHub Copilot · danswett 79% · Codex 61% · Claude Code 41%');
  await expect(line.locator('.usage-figure.warn')).toHaveText('GitHub Copilot · danswett 79%');
  await go(page, 'usage');
  const usage = page.locator('#usage');
  await expect(usage).toBeVisible();
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

test('a window that has reset shows "Reset" with no figure, bar or pace, and a row of only such windows says Reset in the summary', async ({ page }) => {
  await openDemo(page);
  await sendUsage(page, (at) => ({
    agents: [
      { agent: 'claude', label: 'Claude Code', account: 'max', source: 'cache', measuredAt: at(-11 * 3600000), windows: [{ key: 'session', label: 'Session (5h)', percent: 96, startsAt: at(-5 * 3600000), resetsAt: at(-12 * 60000) }] },
      { agent: 'copilot', label: 'GitHub Copilot', account: 'danswett', source: 'api', measuredAt: at(-60000), windows: [{ key: 'plan', label: 'Plan', percent: 20, resetsAt: at(3 * 86400000) }] },
    ],
  }));
  await expect(page.locator('#usage-line .usage-summary')).toHaveText('GitHub Copilot \u00b7 danswett 20% \u00b7 Claude Code \u00b7 max Reset');
  await go(page, 'usage');
  const win = page.locator('#usage .usage-group[data-agent="claude"] .usage-window');
  await expect(win.locator('.usage-percent')).toHaveText('Reset');
  await expect(win.locator('.usage-foot')).toHaveText('Reset 12m ago \u00b7 no reading since');
  await expect(win.locator('.usage-bar, .usage-pace, .usage-pacefoot, .usage-amount')).toHaveCount(0);
  await expect(win).toHaveAttribute('aria-label', 'Claude Code \u00b7 max, Session (5h): reset 12 minutes ago, no reading since.');
  await expect(page.locator('#usage [aria-live]')).toHaveCount(0);
});

test('an update from the host redraws the bars, and an error shows only when there is no fresh reading', async ({ page }) => {
  await openDemo(page);
  await sendUsage(page, (at) => ({
    agents: [
      { agent: 'copilot', label: 'GitHub Copilot', account: 'danswett', source: 'cache', measuredAt: at(-60000), error: 'The Copilot quota could not be read: the request timed out', windows: [{ key: 'plan', label: 'Plan', percent: 93 }] },
      { agent: 'claude', label: 'Claude Code', source: 'none', measuredAt: at(0), error: 'The Claude usage could not be read: the server answered 500', windows: [] },
    ],
  }));
  await expect(page.locator('#usage-line .usage-figure.crit')).toHaveText('GitHub Copilot · danswett 93%');
  await go(page, 'usage');
  const usage = page.locator('#usage');
  const copilot = usage.locator('.usage-group[data-agent="copilot"]');
  await expect(copilot.locator('.usage-fill')).toHaveClass(/crit/);
  // A failed read beside a reading taken a minute ago is noise.
  await expect(copilot.locator('.usage-error')).toHaveCount(0);
  // With no reading at all, the failure is all there is to say.
  await expect(usage.locator('.usage-group[data-agent="claude"] .usage-error')).toHaveText('The Claude usage could not be read: the server answered 500');
  await expect(usage.locator('.usage-group[data-agent="claude"] .usage-window')).toHaveCount(0);
});

// Usage and Machines are pages of their own (#179), not panels folded over the sessions: the
// line under the sessions, and machines online in the app bar, are what is left of them there.
test('the usage line under the sessions opens the Usage page, which a reload keeps, and back returns to the sessions', async ({ page }) => {
  await openDemo(page);
  const line = page.locator('#usage-line');
  await expect(line.locator('.usage-summary')).toContainText('GitHub Copilot · danswett 79%');
  await expect(page.locator('#usage')).toBeHidden();

  await line.click();
  const usage = page.locator('#usage');
  await expect(usage.locator('.usage-group')).toHaveCount(3);
  await expect(page.locator('#nav [data-nav="usage"]')).toHaveAttribute('aria-current', 'page');

  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  await expect(usage.locator('.usage-group')).toHaveCount(3);
  await page.goBack();
  await expect(page.locator('.session').first()).toBeVisible();
  await expect(usage).toBeHidden();
});

test('the app bar names how many machines are online and leads to the Machines page, which a reload keeps', async ({ page }) => {
  await openDemo(page);
  const online = page.locator('#online');
  await expect(online).toHaveText('1 machine');
  await expect(online).toHaveAccessibleName('1 machine online');
  const machines = page.locator('#machines');
  await expect(machines).toBeHidden();
  // The sessions are where they were.
  await expect(page.locator('.session').first()).toBeVisible();

  await online.click();
  await expect(machines.locator('#machine-list .machine')).toHaveCount(1);
  await expect(machines.locator('#machine-list .machine-button')).toHaveAttribute('aria-current', 'true');
  await expect(page.locator('.session').first()).toBeHidden();

  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  await expect(machines.locator('#machine-list .machine')).toHaveCount(1);
  await go(page, 'sessions');
  await expect(page.locator('.session').first()).toBeVisible();
});

test('a reading kept from a machine that has gone shows with its age and the machine that read it, after a reload too', async ({ page }) => {
  await openDemo(page, '&machines=2&keep=1');
  await go(page, 'usage');
  const usage = page.locator('#usage');
  // Laptop is the only machine signed in to a second Claude account, read three hours ago.
  await page.evaluate(() => {
    const report = window.agentBridgeDemo.machines['demo-laptop'].usage;
    const at = new Date(Date.now() - 3 * 3600000).toISOString();
    window.agentBridgeDemo.machines['demo-laptop'].setUsage({
      agents: [...report.agents, { agent: 'claude', label: 'Claude Code', accountKey: 'aaaabbbbcccc', plan: 'pro', source: 'api', measuredAt: at, windows: [{ key: 'session', label: 'Session (5h)', percent: 64 }] }],
    });
  });
  const second = usage.locator('.usage-group[data-agent="claude"]').filter({ hasText: '#aaaa' });
  await expect(second.locator('.usage-by')).toHaveText('read on Laptop');
  // Said to a screen reader and in a tooltip, never on the line (#225).
  await expect(second.locator('.usage-head')).toHaveAttribute('title', 'Read on Laptop');
  expect((await second.locator('.usage-by').boundingBox()).width).toBeLessThanOrEqual(1);

  await page.evaluate(() => window.agentBridgeDemo.setOnline('demo-laptop', false));
  await expect(second.locator('.usage-percent')).toHaveText('64%');
  await expect(second.locator('.usage-age')).toHaveText('3h ago');
  await expect(second.locator('.usage-age')).toHaveClass(/stale/);
  await expect(second.locator('.usage-by')).toHaveText('read on Laptop');

  // A later visit: Laptop is back but no longer offers that account; what was kept still shows.
  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  await expect(second.locator('.usage-percent')).toHaveText('64%');
  await expect(second.locator('.usage-by')).toHaveText('read on Laptop');
  expect(await page.evaluate(() => window.__cspViolations)).toEqual([]);
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 844 } });

  test('two Copilot accounts with long logins each show their login in full, in the groups and the summary, and no machine (#225)', async ({ page }) => {
    await openDemo(page);
    const one = 'a-remarkably-long-github-login-number-one';
    const two = 'another-quite-long-github-login-number-two';
    await sendUsage(page, (at) => ({
      agents: [one, two].map((account, i) => ({
        agent: 'copilot', label: 'GitHub Copilot', account, plan: 'enterprise', source: 'api', measuredAt: at(-60000),
        windows: [{ key: 'plan', label: 'Plan', percent: i ? 0.3 : 11.7, startsAt: at(-10 * 86400000), resetsAt: at(20 * 86400000) }],
      })),
    }));
    const line = page.locator('#usage-line .usage-summary');
    await expect(line).toHaveText(`GitHub Copilot · ${one} 12% · GitHub Copilot · ${two} 0%`);
    for (const who of await line.locator('.usage-who').all()) { expect(await who.evaluate((e) => e.scrollWidth <= e.clientWidth + 1)).toBe(true); }
    await go(page, 'usage');
    const groups = page.locator('#usage .usage-group[data-agent="copilot"]');
    await expect(groups).toHaveCount(2);
    await expect(groups.locator('.usage-account')).toHaveText([`${one} · enterprise`, `${two} · enterprise`]);
    for (const a of await groups.locator('.usage-account').all()) {
      const box = await a.boundingBox();
      expect(box.x + box.width).toBeLessThanOrEqual(390);
      expect(await a.evaluate((e) => e.scrollWidth <= e.clientWidth + 1 && getComputedStyle(e).textOverflow !== 'ellipsis')).toBe(true);
    }
    // No "read on <machine>" anywhere a person sees it.
    for (const by of await page.locator('#usage .usage-by').all()) { expect((await by.boundingBox()).width).toBeLessThanOrEqual(1); }
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true);
  });
});

test('a machine that does not offer usage shows no usage panel', async ({ page }) => {
  await openDemo(page, '&usage=0');
  await expect(page.locator('.session').first()).toBeVisible();
  await expect(page.locator('#usage-line')).toBeHidden();
  await go(page, 'usage');
  await expect(page.locator('#usage')).toBeHidden();
  await expect(page.locator('.usage-none')).toBeVisible();
  expect(await page.evaluate(() => window.agentBridgeDemo.calls.filter((c) => c.startsWith('usage.')))).toEqual([]);
});

test('the machine line says how many sessions are live, the version and that it is a dev build', async ({ page }) => {
  await openDemo(page);
  const status = page.locator('.machine .machine-status');
  await expect(status).toHaveText('4 sessions · 0.1.0 (dev)');
  await page.evaluate(() => window.agentBridgeDemo.remove('019a1b2c-codex-example'));
  await expect(status).toHaveText('3 sessions · 0.1.0 (dev)');
});

const REASON = 'an unrecognised copilot process (pid 4120)';

// The page's clock, taken over once the demo is up, so a hold's two minutes pass at once.
async function pausedClock(page) {
  await page.clock.install();
  await page.clock.pauseAt(await page.evaluate(() => Date.now()) + 1000);
}

test('the machine line says what holds its discovery once the hold has lasted two minutes, until it clears', async ({ page }) => {
  await openDemo(page);
  await pausedClock(page);
  const status = page.locator('.machine .machine-status');
  await page.evaluate((reason) => window.agentBridgeDemo.hold({ reason, agents: ['copilot'] }), REASON);
  await page.clock.runFor(2 * 60000 + 100);
  // A short fact on the row (#213), in the needs colour; the reason in its tooltip and to a screen reader.
  await expect(status).toHaveText(`4 sessions · Discovery held: ${REASON}`);
  await expect(status.locator('.machine-flag.held .sr-only')).toHaveText(`: ${REASON}`);
  await expect(status).toHaveAttribute('title', `4 sessions · 0.1.0 (dev) · discovery held: ${REASON}`);
  await page.evaluate(() => window.agentBridgeDemo.hold(null));
  await page.clock.runFor(100);
  await expect(status).toHaveText('4 sessions · 0.1.0 (dev)');
});

test('a discovery hold shorter than two minutes changes nothing on the machine row', async ({ page }) => {
  await openDemo(page);
  await pausedClock(page);
  const status = page.locator('.machine .machine-status');
  await page.evaluate((reason) => window.agentBridgeDemo.hold({ reason, agents: ['copilot'] }), REASON);
  await page.clock.runFor(2 * 60000 - 1000);
  await expect(status).toHaveText('4 sessions · 0.1.0 (dev)');
  await page.evaluate(() => window.agentBridgeDemo.hold(null));
  await page.clock.runFor(60000);
  await expect(status).toHaveText('4 sessions · 0.1.0 (dev)');
});

test('a discovery hold that returns counts its two minutes from its own start', async ({ page }) => {
  await openDemo(page);
  await pausedClock(page);
  const status = page.locator('.machine .machine-status');
  const hold = () => page.evaluate((reason) => window.agentBridgeDemo.hold({ reason, agents: ['copilot'] }), REASON);
  await hold();
  await page.clock.runFor(100000);
  await page.evaluate(() => window.agentBridgeDemo.hold(null));
  await page.clock.runFor(100);
  await hold();
  await page.clock.runFor(100000);
  await expect(status).toHaveText('4 sessions · 0.1.0 (dev)');
  await page.clock.runFor(21000);
  await expect(status).toContainText('Discovery held');
});

