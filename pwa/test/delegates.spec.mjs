// The main agent and its delegates on a card (#176, ui.md §1 principle 3, "One voice per line"),
// in demo mode at a phone's width: the card's response and reasoning are the main agent's own,
// and each delegate's words are under its own name in the Delegates block, never in its place.
import { test, expect } from '@playwright/test';

const SESSION = 'demo-delegates';
const CODEX = '019a1b2c-codex-example';
const DELEGATING = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
// The demo's top-level texts are the delegates' (source names them); own holds the main agent's.
const MAIN_RESPONSE = 'Started 2 background agents: one runs the full suite, one reads the witness logic.';
const MAIN_REASONING = 'Two independent jobs: delegate both.';
const SUITE_LINE = 'Bisecting over the last 10 commits: 4 left to try.';
const WITNESS_LINE = 'Check CI before saying so.';

const card = (page, id = SESSION) => page.locator(`.session[data-id="${id}"]`);
const delegate = (page, id) => card(page).locator(`.delegate[data-id="${id}"]`);
const later = () => new Date(Date.now() + 1000).toISOString();

test.use({ viewport: { width: 390, height: 844 } });

async function openDemo(page, query = '&delegates=1') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations)).toEqual([]);
});

test('the card shows the main agent\'s own words, and each delegate\'s only under its name', async ({ page }) => {
  await openDemo(page);
  const c = card(page);
  await expect(c.locator('.voice-label')).toHaveText('Main agent');
  await expect(c.locator('.response')).toContainText(MAIN_RESPONSE);
  await expect(c.locator('.response')).not.toContainText(SUITE_LINE);
  await expect(c.locator('.body > .reasoning .plain')).toHaveText(MAIN_REASONING);

  // Each delegate's words appear once on the card, in its own row.
  await expect(c.getByText(SUITE_LINE)).toHaveCount(1);
  await expect(delegate(page, 'demo-agent-suite').locator('.delegate-line')).toHaveText(SUITE_LINE);
  await expect(c.getByText(WITNESS_LINE)).toHaveCount(1);
  await expect(delegate(page, 'demo-agent-witness').locator('.delegate-line')).toHaveText(WITNESS_LINE);

  // Running first, a nested one under its parent, and the finished folded into a count.
  const block = c.locator('.delegates');
  await expect(block.getByRole('heading', { name: 'Delegates' })).toBeVisible();
  const active = block.locator('.delegate-list').first().locator(':scope > .delegate');
  expect(await active.evaluateAll((els) => els.map((e) => `${e.dataset.depth}:${e.dataset.id}`))).toEqual(['0:demo-agent-witness', '1:demo-agent-grep', '0:demo-agent-suite']);
  await expect(delegate(page, 'demo-agent-suite').locator('.delegate-name')).toHaveText('Run full test suite');
  await expect(delegate(page, 'demo-agent-suite').locator('.glyph')).toHaveAttribute('data-glyph', 'working');
  await expect(delegate(page, 'demo-agent-suite').locator('.glyph')).toHaveAccessibleName('Working');
  await expect(delegate(page, 'demo-agent-suite').locator('.delegate-time')).toHaveText(/^\d+m$/);
  const finished = block.locator('.delegates-finished');
  await expect(finished.locator(':scope > summary')).toHaveText('2 finished');
  await expect(delegate(page, 'demo-agent-review')).toBeHidden();
  await finished.locator(':scope > summary').click();
  await expect(delegate(page, 'demo-agent-review').locator('.glyph')).toHaveAttribute('data-glyph', 'done');
  await expect(delegate(page, 'demo-agent-lint').locator('.glyph')).toHaveAttribute('data-glyph', 'failed');
  await expect(delegate(page, 'demo-agent-lint').locator('.delegate-time')).toHaveText(/^finished \d+m ago$/);

  // Nothing in the block replies to, types to or stops a delegate.
  await expect(block.locator('button, textarea, input')).toHaveCount(0);
  // And it fits a phone.
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
});

test('opening a delegate asks the machine for that delegate and shows its own response and reasoning', async ({ page }) => {
  await openDemo(page);
  const row = delegate(page, 'demo-agent-suite');
  await row.locator('.delegate-summary').click();
  await expect(row.locator('.delegate-task')).toContainText('Run the full test suite on HEAD');
  await expect(row.locator('.delegate-latest .md')).toContainText('The suite fails on HEAD: RestartAfterResume times out.');
  await expect(row.locator('.delegate-facts')).toHaveText('Background · claude-haiku-5.5 · 1 background command');
  await row.locator('.reasoning summary').click();
  await expect(row.locator('.reasoning .plain')).toHaveText('A timing failure that only shows after a resume. Bisect first, then read the timer.');
  const asked = await page.evaluate(() => window.agentBridgeDemo.host.getLog);
  expect(asked).toContainEqual({ id: SESSION, agent: 'demo-agent-suite' });
  // The card's own response is still the main agent's.
  await expect(card(page).locator('.response')).toContainText(MAIN_RESPONSE);
});

test('an open delegate stays open as it finishes, and moves into the finished fold', async ({ page }) => {
  await openDemo(page);
  const row = delegate(page, 'demo-agent-suite');
  await row.locator('.delegate-summary').click();
  await expect(row.locator('.delegate-detail')).toHaveAttribute('open', '');
  await page.evaluate(({ id, at }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({ ...s, updatedAt: at, subagents: s.subagents.map((a) => (a.id === 'demo-agent-suite' ? { ...a, status: 'ended', outcome: 'done', endedAt: at, line: 'All 412 tests pass.' } : a)) });
  }, { id: SESSION, at: later() });
  await expect(card(page).locator('.delegates-finished > summary')).toHaveText('3 finished');
  await expect(row.locator('.glyph')).toHaveAttribute('data-glyph', 'done');
  await expect(row.locator('.delegate-detail')).toHaveAttribute('open', '');
  await expect(row.locator('.delegate-line')).toHaveText('All 412 tests pass.');
});

test('a main-agent response the machine left out says so, and never shows the delegate\'s in its place', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(({ id, at }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({ ...s, updatedAt: at, activity: { ...s.activity, own: { responseTruncated: true } } });
  }, { id: SESSION, at: later() });
  const c = card(page);
  await expect(c.locator('.body > .dropped')).toHaveText(/left out/);
  await expect(c.locator('.response')).toBeHidden();
  await expect(c.locator('.body > .reasoning')).toBeHidden();
  await expect(c.getByText(SUITE_LINE)).toHaveCount(1);
});

test('a host that sends no attribution or delegates shows its cards as before', async ({ page }) => {
  await openDemo(page, '');
  const codex = card(page, CODEX);
  await expect(codex.locator('.response')).toHaveText('Done. The test now waits for the socket to drain.');
  await expect(codex.locator('.voice-label')).toBeHidden();
  await expect(codex.locator('.delegates')).toBeHidden();
  // Background agents it can count but not list: status rule 8 says so, and nothing new appears.
  const delegating = card(page, DELEGATING);
  await expect(delegating.locator('.status-text')).toHaveText('Delegating to 2 agents · details not reported');
  await expect(delegating.locator('.delegates')).toBeHidden();
  await expect(delegating.locator('.response')).toHaveText('Three delegates are building the slices; I\'ll review each PR as it lands.');
});
