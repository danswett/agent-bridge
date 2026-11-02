// The main agent and its delegates in a session's view (#176, ui.md §1 principle 3, "One voice
// per line"; §6.3), in demo mode at a desktop's width, where the inspector is a pane of its own:
// the conversation's response and reasoning are the main agent's own, and each delegate's words
// are under its own name in the Delegates block, never in its place.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const SESSION = 'demo-delegates';
const CODEX = '019a1b2c-codex-example';
const DELEGATING = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
// The demo's top-level texts are the delegates' (source names them); own holds the main agent's.
const MAIN_RESPONSE = 'Started 2 background agents: one runs the full suite, one reads the witness logic.';
const MAIN_REASONING = 'Two independent jobs: delegate both.';
const SUITE_LINE = 'Bisecting over the last 10 commits: 4 left to try.';
const WITNESS_LINE = 'Check CI before saying so.';

const card = (page, id = SESSION) => view(page, id);
const delegate = (page, id) => card(page).locator(`.delegate[data-id="${id}"]`);
const later = () => new Date(Date.now() + 1000).toISOString();

test.use({ viewport: { width: 1440, height: 900 } });

async function openDemo(page, query = '&delegates=1', id = SESSION) {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
  await open(page, id);
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
  await expect(c.locator('.agent-block > .reasoning .plain')).toHaveText(MAIN_REASONING);

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
  // And it fits beside the conversation.
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(1440);
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
  // The fold opened with it, so what was being read is still in view.
  await expect(card(page).locator('.delegates-finished')).toHaveAttribute('open', '');
  await expect(row.locator('.delegate-latest')).toBeVisible();
  await expect(row.locator('.delegate-line')).toHaveText('All 412 tests pass.');
});

const fetches = (page, agent) => page.evaluate((a) => window.agentBridgeDemo.host.getLog.filter((g) => g.agent === a).length, agent);
const say = (page, agent, response) => page.evaluate(({ a, r }) => { window.agentBridgeDemo.host.delegateWords[a] = { response: r }; }, { a: agent, r: response });
const change = (page, agent, fields) => page.evaluate(({ id, a, fields, at }) => {
  const d = window.agentBridgeDemo;
  const s = d.host.get(id);
  d.upsert({ ...s, updatedAt: at, subagents: s.subagents.map((x) => (x.id === a ? { ...x, ...fields } : x)) });
}, { id: SESSION, a: agent, fields, at: later() });

test('a delegate that changes while its words are on their way is fetched again once they are back', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => window.agentBridgeDemo.host.holdResponses());
  const row = delegate(page, 'demo-agent-suite');
  await row.locator('.delegate-summary').click();
  await expect(row.locator('.delegate-body [role="status"]')).toHaveText('Loading…');
  await say(page, 'demo-agent-suite', 'Found it: commit 4 of 10.');
  await change(page, 'demo-agent-suite', { line: 'Found it.' });
  // The change's fetch comes due while the first is still held.
  await page.waitForTimeout(2500);
  await page.evaluate(() => window.agentBridgeDemo.host.releaseResponses());
  await expect.poll(() => fetches(page, 'demo-agent-suite')).toBe(2);
  await expect(row.locator('.delegate-latest .md')).toHaveText('Found it: commit 4 of 10.');
});

test('an open delegate that is running is fetched again, as a host sends nothing when only its words change', async ({ page }) => {
  await openDemo(page);
  const row = delegate(page, 'demo-agent-suite');
  await row.locator('.delegate-summary').click();
  await expect(row.locator('.delegate-latest .md')).toContainText('The suite fails on HEAD');
  await say(page, 'demo-agent-suite', 'Commit 7 of 10 is the first to fail.');
  await expect(row.locator('.delegate-latest .md')).toHaveText('Commit 7 of 10 is the first to fail.', { timeout: 15000 });
});

test('a delegate\'s task and line the budget left out of the list are kept, not taken for silence', async ({ page }) => {
  await openDemo(page);
  await change(page, 'demo-agent-suite', { line: undefined, task: undefined, model: 'claude-haiku-5.5 (trimmed)' });
  const row = delegate(page, 'demo-agent-suite');
  await expect(row.locator('.delegate-facts')).toContainText('(trimmed)');
  await expect(row.locator('.delegate-line')).toHaveText(SUITE_LINE);
  await row.locator('.delegate-summary').click();
  await expect(row.locator('.delegate-task')).toContainText('Run the full test suite on HEAD');
});

test('nothing is fetched for a delegate nobody can see: its session left, or gone from the list', async ({ page }) => {
  test.slow();
  await openDemo(page);
  const suite = delegate(page, 'demo-agent-suite');
  await suite.locator('.delegate-summary').click();
  await expect(suite.locator('.delegate-latest .md')).toContainText('The suite fails on HEAD');
  // A delegate opened, then gone from the list while its words are on their way.
  await page.evaluate(() => window.agentBridgeDemo.host.holdResponses());
  await delegate(page, 'demo-agent-witness').locator('.delegate-summary').click();
  await expect.poll(() => fetches(page, 'demo-agent-witness')).toBe(1);
  await page.evaluate(({ id, at }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({ ...s, updatedAt: at, subagents: s.subagents.filter((a) => a.id !== 'demo-agent-witness') });
  }, { id: SESSION, at: later() });
  await expect(delegate(page, 'demo-agent-witness')).toHaveCount(0);
  await page.evaluate(() => window.agentBridgeDemo.host.releaseResponses());
  // Another session chosen, with a running delegate open in this one.
  await open(page, CODEX);
  await expect(card(page)).toBeHidden();
  await say(page, 'demo-agent-suite', 'Commit 7 of 10 is the first to fail.');
  const before = await fetches(page, 'demo-agent-suite');
  await page.waitForTimeout(11000);
  expect(await fetches(page, 'demo-agent-suite')).toBe(before);
  expect(await fetches(page, 'demo-agent-witness')).toBe(1);
  // Chosen again, it catches up.
  await open(page, SESSION);
  await expect(suite.locator('.delegate-latest .md')).toHaveText('Commit 7 of 10 is the first to fail.', { timeout: 15000 });
});

test('a fetch that brings the same words leaves what is shown alone', async ({ page }) => {
  await openDemo(page);
  const row = delegate(page, 'demo-agent-suite');
  await row.locator('.delegate-summary').click();
  await expect(row.locator('.delegate-latest .md')).toContainText('The suite fails on HEAD');
  await row.locator('.delegate-latest .md > *').first().evaluate((el) => { el.dataset.seen = '1'; });
  await change(page, 'demo-agent-suite', { currentTool: { id: 't9', name: 'powershell', summary: 'git bisect good', status: 'running', startedAt: later() }, line: 'Still bisecting.' });
  await expect.poll(() => fetches(page, 'demo-agent-suite')).toBe(2);
  await page.waitForTimeout(1000);
  await expect(row.locator('.delegate-latest .md > [data-seen="1"]')).toHaveCount(1);
});

test('a nested delegate names who started it: aloud under its parent, and in words where the parent is in the other list', async ({ page }) => {
  await openDemo(page);
  const grep = delegate(page, 'demo-agent-grep');
  await expect(grep.locator('.delegate-summary')).toContainText('under Read witness shell logic');
  await expect(grep.locator('.sr-only')).toHaveText('under Read witness shell logic');
  await card(page).locator('.delegates-finished > summary').click();
  await expect(delegate(page, 'demo-agent-review').locator('.delegate-under')).toBeVisible();
  await expect(delegate(page, 'demo-agent-review').locator('.delegate-under')).toHaveText('under Read witness shell logic');
});

test('a delegate whose words could not be fetched is tried again, finished or not', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => { window.agentBridgeDemo.host.failDelegateGets = 1; });
  await card(page).locator('.delegates-finished > summary').click();
  const row = delegate(page, 'demo-agent-review');
  await row.locator('.delegate-summary').click();
  await expect(row.locator('.delegate-body [role="status"]')).toHaveText(/^Couldn't load the delegate: .*reconnecting/);
  await expect(row.locator('.delegate-body [role="status"]')).toBeHidden({ timeout: 15000 });
  await expect(row.locator('.delegate-latest .md')).toHaveText('No regressions: the flag now follows the latest reply.');
});

test('the main agent\'s reasoning alone is labelled as its own', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(({ id, at }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({ ...s, updatedAt: at, activity: { ...s.activity, own: { reasoning: 'Two independent jobs: delegate both.' } } });
  }, { id: SESSION, at: later() });
  const c = card(page);
  await expect(c.locator('.response')).toBeHidden();
  await expect(c.locator('.agent-block > .reasoning')).toBeVisible();
  await expect(c.locator('.voice-label')).toBeVisible();
});

test('a delegate starting does not take keyboard focus from the row being read', async ({ page }) => {
  await openDemo(page);
  const summary = delegate(page, 'demo-agent-suite').locator('.delegate-summary');
  await summary.focus();
  await page.evaluate(({ id, at }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({ ...s, updatedAt: at, subagents: [{ id: 'demo-agent-new', kind: 'background', name: 'Write the fix', status: 'working', startedAt: at, shells: 0 }, ...s.subagents] });
  }, { id: SESSION, at: later() });
  await expect(delegate(page, 'demo-agent-new')).toBeVisible();
  const ids = await card(page).locator('.delegate-list').first().locator(':scope > .delegate').evaluateAll((els) => els.map((e) => e.dataset.id));
  expect(ids.indexOf('demo-agent-new')).toBeLessThan(ids.indexOf('demo-agent-suite'));
  await expect(summary).toBeFocused();
});

test('a main-agent response the machine left out says so, and never shows the delegate\'s in its place', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(({ id, at }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({ ...s, updatedAt: at, activity: { ...s.activity, own: { responseTruncated: true } } });
  }, { id: SESSION, at: later() });
  const c = card(page);
  await expect(c.locator('.agent-block > .dropped')).toHaveText(/left out/);
  await expect(c.locator('.response')).toBeHidden();
  await expect(c.locator('.agent-block > .reasoning')).toBeHidden();
  await expect(c.getByText(SUITE_LINE)).toHaveCount(1);
});

test('a host that sends no attribution or delegates shows its sessions as before', async ({ page }) => {
  await openDemo(page, '', CODEX);
  const codex = card(page, CODEX);
  await expect(codex.locator('.response')).toHaveText('Done. The test now waits for the socket to drain.');
  await expect(codex.locator('.voice-label')).toBeHidden();
  await expect(codex.locator('.delegates')).toBeHidden();
  // Background agents it can count but not list: status rule 8 says so, and nothing new appears.
  await open(page, DELEGATING);
  const delegating = card(page, DELEGATING);
  await expect(delegating.locator('.status-text')).toHaveText('Delegating to 2 agents · details not reported');
  await expect(delegating.locator('.delegates')).toBeHidden();
  await expect(delegating.locator('.response')).toHaveText('Three delegates are building the slices; I\'ll review each PR as it lands.');
});

test.describe('where the inspector is not a pane of its own', () => {
  test.use({ viewport: { width: 900, height: 800 } });

  test('the delegates are a tab of their own, beside the conversation and the tools, and arrows walk all three', async ({ page }) => {
    await openDemo(page);
    const c = card(page);
    const tabs = c.getByRole('tab');
    await expect(tabs).toHaveText(['Conversation', 'Delegates', 'Tools']);
    await expect(c.locator('.delegates')).toBeHidden();
    await c.getByRole('tab', { name: 'Delegates' }).click();
    await expect(c.getByRole('tab', { name: 'Delegates' })).toHaveAttribute('aria-selected', 'true');
    await expect(c.locator('.delegates')).toBeVisible();
    await expect(c.locator('.conversation')).toBeHidden();
    await expect(c.locator('.tools-panel')).toBeHidden();
    await expect(c.locator('.details')).toBeHidden();
    await page.keyboard.press('ArrowRight');
    await expect(c.getByRole('tab', { name: 'Tools' })).toBeFocused();
    await expect(c.locator('.delegates')).toBeHidden();
    await page.keyboard.press('ArrowRight');
    await expect(c.getByRole('tab', { name: 'Conversation' })).toBeFocused();
    await page.keyboard.press('ArrowLeft');
    await expect(c.getByRole('tab', { name: 'Tools' })).toBeFocused();
  });

  test('a session whose host lists no delegates has no Delegates tab, and arrows skip it', async ({ page }) => {
    await openDemo(page, '', CODEX);
    const c = card(page, CODEX);
    await expect(c.getByRole('tab', { name: 'Delegates' })).toBeHidden();
    await c.getByRole('tab', { name: 'Conversation' }).focus();
    await page.keyboard.press('ArrowRight');
    await expect(c.getByRole('tab', { name: 'Tools' })).toBeFocused();
  });

  test('the Delegates tab gives way to the conversation when the last delegate leaves the list', async ({ page }) => {
    await openDemo(page);
    const c = card(page);
    await c.getByRole('tab', { name: 'Delegates' }).click();
    await page.evaluate(({ id, at }) => {
      const d = window.agentBridgeDemo;
      const s = d.host.get(id);
      d.upsert({ ...s, updatedAt: at, subagents: [] });
    }, { id: SESSION, at: later() });
    await expect(c.getByRole('tab', { name: 'Delegates' })).toBeHidden();
    await expect(c.getByRole('tab', { name: 'Conversation' })).toHaveAttribute('aria-selected', 'true');
    await expect(c.locator('.conversation')).toBeVisible();
  });
});

test('on a phone the Delegates tab holds the block, and it fits', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openDemo(page);
  const c = card(page);
  await c.getByRole('tab', { name: 'Delegates' }).click();
  await expect(c.locator('.delegates')).toBeVisible();
  await expect(delegate(page, 'demo-agent-suite').locator('.delegate-line')).toHaveText(SUITE_LINE);
  expect(await page.evaluate(() => document.documentElement.scrollWidth)).toBeLessThanOrEqual(390);
});

test('at a desktop\'s width the delegates sit in the inspector, between Details and Tools, with the conversation beside them', async ({ page }) => {
  await openDemo(page);
  const c = card(page);
  await expect(c.locator('.conversation')).toBeVisible();
  await expect(c.locator('.inspector .delegates')).toBeVisible();
  expect(await c.locator('.inspector > section:not([hidden])').evaluateAll((els) => els.map((e) => e.className))).toEqual(['details', 'delegates-panel', 'tools-panel']);
});

test('in the inspector a delegate\'s name, parent and time wrap as words, and no line starts with a separator', async ({ page }) => {
  await openDemo(page);
  await card(page).locator('.delegates-finished > summary').click();
  const review = delegate(page, 'demo-agent-review');
  await expect(review.locator('.delegate-under')).toBeVisible();
  // "Review the witness change" wraps at most once, whatever the system face: squeezed beside
  // its parent and time it took a line a word (four lines of the head's 20 px).
  expect((await review.locator('.delegate-name').boundingBox()).height).toBeLessThanOrEqual(44);
  const starts = await card(page).locator('.inspector .delegate-words').evaluateAll((els) => els.flatMap((w) => {
    const left = w.getBoundingClientRect().left;
    return [...w.querySelectorAll('.delegate-under, .delegate-time:not(:empty)')].map((sep) => sep.getClientRects()[0].left - left);
  }));
  expect(starts.length).toBeGreaterThan(4);
  for (const x of starts) { expect(x).toBeGreaterThan(2); }
});
