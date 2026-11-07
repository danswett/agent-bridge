// The app in demo mode (?demo=1): a simulated host speaking the real protocol to the real
// client, so these tests cover event handling through to what is on screen.
import { test, expect } from '@playwright/test';
import { open, row, view } from './nav.mjs';
import { readFileSync } from 'node:fs';

const fixture = JSON.parse(readFileSync(new URL('../fixtures/sessions-list.json', import.meta.url), 'utf8'));
const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CLAUDE = '6f1c2b7e-claude-example';
const CODEX = '019a1b2c-codex-example';
const DELEGATING = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';

// A session's view in the session pane (#179); its row in the list is row().
const card = (page, id) => view(page, id);
const order = (page) => page.locator('.session').evaluateAll((els) => els.map((e) => e.dataset.id));
const calls = (page, op) => page.evaluate((o) => window.agentBridgeDemo.calls.filter((c) => c === o).length, op);
const later = () => new Date(Date.now() + 1000).toISOString();

async function openDemo(page, query = '') {
  // Content-Security-Policy violations are collected in the page: there must be none.
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

async function upsert(page, id, change) {
  await page.evaluate(({ id, change }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id) ?? { id };
    d.upsert({ ...s, ...change, activity: { ...(s.activity ?? {}), ...(change.activity ?? {}) } });
  }, { id, change });
}

test('demo mode shows every session from the worked example, with every field', async ({ page }) => {
  // A machine that takes no answers from the phone: the question is shown read-only.
  await openDemo(page, '&answer=0');
  await expect(page.locator('.session')).toHaveCount(4);
  await expect(page.locator('#sessions-h')).toHaveText('Sessions on Demo machine');

  const copilot = await open(page, COPILOT);
  await expect(copilot).toHaveAttribute('data-status', 'waiting');
  await expect(row(page, COPILOT)).toHaveAttribute('data-status', 'waiting');
  await expect(copilot.locator('.session-title')).toHaveText('Plan Migration to Windows App');
  await expect(row(page, COPILOT).locator('.row-title')).toHaveText('Plan Migration to Windows App');
  // The header's one meta line: status, machine, repository (ui.md §6.2); the rest is in Details.
  await expect(copilot.locator('.status-line')).toHaveText('Waiting on you · Demo machine · danswett/agent-ha-bridge');
  await expect(copilot.locator('.facts')).toContainText('Copilot');
  await expect(copilot.locator('.facts')).toContainText('danswett/agent-ha-bridge');
  await expect(copilot.locator('.facts')).toContainText('detached@5e59309');
  await expect(copilot.locator('.facts')).toContainText('claude-opus-5.5');
  await expect(copilot.locator('.status-text')).toHaveText('Waiting on you');
  await expect(copilot.locator('.fact-since dd')).toHaveText(/^for /);
  await expect(copilot.locator('.facts')).toContainText('412 turns');
  await expect(copilot.locator('.facts')).toContainText('1 compaction');
  await expect(copilot.locator('.facts')).toContainText('pid 39844');
  await expect(copilot.locator('details.reasoning')).toBeVisible();
  await expect(copilot.locator('.user-message')).toBeVisible();

  // The question, read-only, from the contract's fields.
  const q = copilot.locator('.question');
  await expect(q.locator('.question-head')).toHaveText('Main agent asks');
  await expect(q.locator('.question-message')).toContainText('Is that the right focus?');
  await expect(q.locator('.field')).toHaveCount(5);
  expect(await q.locator('.field').evaluateAll((els) => els.map((e) => e.dataset.type))).toEqual(['choice', 'multi', 'boolean', 'text', 'number']);
  const plan = q.locator('.field[data-field="plan"]');
  await expect(plan.locator('.field-title')).toHaveText("Today's plan");
  await expect(plan.locator('.badge.req')).toHaveText('required');
  await expect(plan.locator('.opt.chosen .label')).toHaveText('Go: protocol, read-only host, PWA cards in parallel (Recommended)');
  await expect(plan.locator('.opt.other')).toHaveText(/Other \(type your answer\)/);
  await expect(q.locator('.field[data-field="areas"] .opt .label')).toHaveText(['host', 'pwa']);
  await expect(q.locator('.field[data-field="areas"] .opt.other')).toHaveCount(0);
  await expect(q.locator('.field[data-field="urgent"] .opt.chosen .label')).toHaveText('No');
  await expect(q.locator('.field[data-field="notes"] .answer-box')).toHaveText('Free text');
  await expect(q.locator('.field[data-field="count"] .answer-box')).toHaveText('A number');
  await expect(q.locator('.read-only-why')).toHaveText("This machine doesn't take answers from the phone yet. Answer at the machine on Demo machine.");
  // Read-only: nothing in the question can be operated.
  await expect(q.locator('button, input, select, textarea')).toHaveCount(0);

  const claude = await open(page, CLAUDE);
  await expect(claude).toHaveAttribute('data-status', 'working');
  await expect(claude.locator('.status-text')).toHaveText('Working');
  await expect(claude.locator('.tool-line')).toBeVisible();
  await expect(claude.locator('.tool-line .tool-name')).toHaveText('Bash');
  await expect(claude.locator('.tool-line .tool-summary')).toHaveText('dotnet test');
  await expect(claude.locator('.question')).toHaveCount(0);

  const codex = await open(page, CODEX);
  await expect(codex).toHaveAttribute('data-status', 'idle');
  await expect(codex.locator('.status-text')).toHaveText('Idle');
  await expect(codex.locator('.tool-line')).toBeHidden();
  await expect(codex.locator('.fact-since')).toHaveCount(0);

  const delegating = card(page, DELEGATING);
  await expect(delegating).toHaveAttribute('data-status', 'idle');
  await expect(delegating).toHaveAttribute('data-view', 'delegating');
  await expect(delegating.locator('.status-text')).toHaveText('Delegating to 2 agents · details not reported');
  await expect(delegating.locator('.fact-since dd')).toHaveText(/^for /);
  await expect(delegating.locator('.fact-background dd')).toHaveText('2 background agents · 1 background command');
  await expect(delegating.locator('.fact-turns dd')).toHaveText('5 turns');

  expect(await order(page)).toEqual([COPILOT, CLAUDE, CODEX, DELEGATING]);
  expect(await page.evaluate(() => window.__cspViolations)).toEqual([]);
});

// The shadow host beside agent-ha-bridge (docs/host-shadow.md), or any machine where it runs
// (design G5): read-only, saying so and why, with nothing offered that would only be refused.
test('a machine where agent-ha-bridge runs is shown read-only with its reason, and offers no actions', async ({ page }) => {
  await openDemo(page, '&ahb=1');
  const reason = 'agent-ha-bridge is running for this user (AgentBridgeDaemon); answer at the machine or through it';
  // On its row, a short fact (#213): the host's reason is the line's tooltip, and said to a screen reader.
  const status = page.locator('.machine .machine-status');
  await expect(status).toHaveText(`4 sessions · View only: ${reason}`);
  await expect(status.locator('.machine-flag .sr-only')).toHaveText(`: ${reason}`);
  await expect(status).toHaveAttribute('title', `4 sessions · 0.1.0 (dev) · view only: ${reason}`);
  const q = card(page, COPILOT).locator('.question');
  await expect(q.locator('.read-only-why')).toHaveText(`This machine is read-only from the phone: ${reason}.`);
  await expect(q.locator('button, input, select, textarea')).toHaveCount(0);
  await expect(page.locator('form.reply')).toHaveCount(0);
  await expect(page.locator('.end-session')).toHaveCount(0);
  // It is asked only whether a folder-trust question waits there (none does), to show read-only.
  await expect(page.locator('#launch')).toBeHidden();
  expect(await page.evaluate(() => window.agentBridgeDemo.calls)).not.toContain('session.launch');

  // A prompt only the machine could answer anyway says that, and why the machine is read-only.
  const prompt = (await page.evaluate((id) => window.agentBridgeDemo.host.get(id), COPILOT)).question;
  await upsert(page, COPILOT, { question: { ...prompt, id: 'q-at-machine', answerAtMachine: true }, updatedAt: later() });
  await expect(q.locator('.read-only-why')).toHaveText('This prompt can only be answered at the machine on Demo machine.');
  await expect(q.locator('.read-only-machine')).toHaveText(`This machine is read-only from the phone: ${reason}.`);
});

// The status glyph and words (docs/design/ui.md §2), which replaced the coloured edges and glows.
// A row's glyph: the one named for a screen reader, as the row reads (ui.md §9).
const glyphOf = (page, id) => row(page, id).locator('.row-main .glyph');
const role = (page, name) => page.evaluate((n) => {
  const probe = document.createElement('i');
  probe.style.color = `var(--ab-${n})`;
  document.body.append(probe);
  const colour = getComputedStyle(probe).color;
  probe.remove();
  return colour;
}, name);
const colourOf = (locator) => locator.evaluate((e) => getComputedStyle(e).color);
const turning = (locator) => locator.evaluate((e) => getComputedStyle(e).animationName);

test('each status has its glyph and no row has a status edge or glow: working turns, waiting is the needs disc, idle is still', async ({ page }) => {
  await openDemo(page);
  await expect(glyphOf(page, CLAUDE)).toHaveAttribute('data-glyph', 'working');
  await expect(glyphOf(page, COPILOT)).toHaveAttribute('data-glyph', 'needs');
  await expect(glyphOf(page, CODEX)).toHaveAttribute('data-glyph', 'idle');
  expect(await turning(glyphOf(page, CLAUDE).locator('.glyph-spin'))).toBe('glyph-turn');
  await expect(glyphOf(page, CODEX).locator('.glyph-spin, .glyph-orbit')).toHaveCount(0);
  expect(await colourOf(glyphOf(page, CLAUDE))).toBe(await role(page, 'active'));
  expect(await colourOf(glyphOf(page, COPILOT))).toBe(await role(page, 'needs'));
  expect(await colourOf(glyphOf(page, CODEX))).toBe(await role(page, 'neutral'));
  // Every row's edge is the same, whatever its status, and nothing breathes.
  await expect(page.locator('.glow')).toHaveCount(0);
  const edges = await page.locator('.session .row-main').evaluateAll((els) => els.map((e) => `${getComputedStyle(e).borderTopColor} ${getComputedStyle(e).borderTopStyle} ${getComputedStyle(e).boxShadow}`));
  expect(new Set(edges).size).toBe(1);
});

test('an idle session still waiting on background work is delegating: its own glyph, whose satellites drift, and its words', async ({ page }) => {
  await openDemo(page);
  const c = card(page, DELEGATING);
  await expect(c).toHaveAttribute('data-view', 'delegating');
  await expect(glyphOf(page, DELEGATING)).toHaveAttribute('data-glyph', 'delegating');
  expect(await turning(glyphOf(page, DELEGATING).locator('.glyph-orbit'))).toBe('glyph-turn');
  expect(await colourOf(glyphOf(page, DELEGATING))).toBe(await role(page, 'active'));
  // The worked example counts its agents but lists none (rule 8).
  await expect(c.locator('.status-text')).toHaveText('Delegating to 2 agents · details not reported');

  // Listed, they are just counted (rule 4).
  await upsert(page, DELEGATING, { updatedAt: later(), subagents: [{ id: 'a1', kind: 'background', name: 'Build U4', status: 'working' }, { id: 'a2', kind: 'background', name: 'Build U5', status: 'working' }] });
  await expect(c.locator('.status-text')).toHaveText('Delegating to 2 agents');

  // Only shells left: it says commands.
  await upsert(page, DELEGATING, { updatedAt: later(), activity: { background: { agents: 0, shells: 1 } } });
  await expect(c.locator('.status-text')).toHaveText('1 background command');
  await expect(glyphOf(page, DELEGATING)).toHaveAttribute('data-glyph', 'delegating');
  await expect(c.locator('.facts')).toContainText('1 background command');
  await expect(c.locator('.facts')).not.toContainText('background agent');

  // Nothing left: plain idle again.
  await upsert(page, DELEGATING, { updatedAt: later(), activity: { background: null } });
  await expect(c).toHaveAttribute('data-view', 'idle');
  await expect(c.locator('.status-text')).toHaveText('Idle');
  await expect(glyphOf(page, DELEGATING)).toHaveAttribute('data-glyph', 'idle');
  await expect(c.locator('.fact-since')).toHaveCount(0);

  // A session working on its own turn is working, whatever it has in the background.
  await upsert(page, DELEGATING, { status: 'working', statusSince: later(), updatedAt: later(), activity: { background: { agents: 1, shells: 0 } } });
  await expect(c).toHaveAttribute('data-view', 'working');
  await expect(c.locator('.status-text')).toHaveText('Working');
  await expect(c.locator('.facts')).toContainText('1 background agent');
});

test('a delegate\'s question and a foreground task are named in the status words', async ({ page }) => {
  await openDemo(page);
  const c = card(page, CODEX);
  await upsert(page, CODEX, {
    status: 'working', statusSince: later(), updatedAt: later(),
    subagents: [{ id: 't1', kind: 'task', name: 'Bisect RestartAfterResume', status: 'working' }],
  });
  await expect(c.locator('.status-text')).toHaveText('Working · task: Delegate Bisect RestartAfterResume');
  await upsert(page, CODEX, {
    status: 'waiting', statusSince: later(), updatedAt: later(), waitingOn: 'b1',
    subagents: [{ id: 'b1', kind: 'background', name: 'Check ubuntu CI failure', status: 'waiting' }],
    question: { id: 'q-b1', kind: 'form', message: 'Which run?', fields: [], askedAt: later(), answerAtMachine: true, agent: 'b1' },
  });
  await expect(c.locator('.status-text')).toHaveText('Delegate Check ubuntu CI failure asks · answer at the machine');
  await expect(glyphOf(page, CODEX)).toHaveAttribute('data-glyph', 'needs');
  expect(await colourOf(c.locator('.status-text'))).toBe(await role(page, 'needs-text'));
});

test('with reduced motion every glyph holds still', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await openDemo(page);
  await upsert(page, CODEX, { driver: 'agent', status: 'working', statusSince: later(), updatedAt: later() });
  await expect(glyphOf(page, CODEX)).toHaveAttribute('data-glyph', 'working');
  const moving = await page.locator('.glyph-spin, .glyph-orbit').evaluateAll((els) => els.map((e) => getComputedStyle(e).animationName));
  // Working (twice: one driven), delegating, and the running tool's.
  expect(moving.length).toBeGreaterThanOrEqual(4);
  expect(new Set(moving)).toEqual(new Set(['none']));
  expect(await page.evaluate(() => document.getAnimations().filter((a) => a.playState === 'running').length)).toBe(0);
});

test('a session an agent drives has the violet diamond on its glyph, says so in agent colour, and goes back when the person takes it', async ({ page }) => {
  await openDemo(page);
  const c = card(page, CODEX);
  const agent = await role(page, 'agent');
  await expect(c).toHaveAttribute('data-driver', 'person');
  await expect(c.locator('.driver')).toBeHidden();
  await expect(glyphOf(page, CODEX).locator('.glyph-badge')).toHaveCount(0);

  await upsert(page, CODEX, { driver: 'agent', driverPrincipal: 'homeassistant:0f1e2d3c', updatedAt: later() });
  await expect(c).toHaveAttribute('data-driver', 'agent');
  await expect(c.locator('.driver')).toHaveText('Driven by an agent');
  await expect(c.locator('.driver')).toHaveAttribute('title', 'homeassistant:0f1e2d3c');
  // Idle: the idle glyph, still neutral, with the diamond in agent colour.
  await expect(glyphOf(page, CODEX)).toHaveAttribute('data-glyph', 'idle');
  await expect(glyphOf(page, CODEX).locator('.glyph-badge')).toHaveCount(1);
  expect(await colourOf(glyphOf(page, CODEX).locator('.glyph-badge'))).toBe(agent);
  expect(await colourOf(glyphOf(page, CODEX))).toBe(await role(page, 'neutral'));
  expect(await colourOf(c.locator('.status-text'))).toBe(await role(page, 'agent-text'));
  await expect(glyphOf(page, CODEX)).toHaveAccessibleName('Fix flaky test, idle, driven by an agent, Demo machine');

  // Working: the active colour turns to agent.
  await upsert(page, CODEX, { status: 'working', statusSince: later(), updatedAt: later() });
  await expect(glyphOf(page, CODEX)).toHaveAttribute('data-glyph', 'working');
  expect(await colourOf(glyphOf(page, CODEX))).toBe(agent);
  expect(agent).not.toBe(await colourOf(glyphOf(page, CLAUDE)));

  // Still the agent's while the machine ends it: the ended glyph, with the diamond.
  await upsert(page, CODEX, { status: 'ending', statusSince: later(), updatedAt: later() });
  await expect(glyphOf(page, CODEX)).toHaveAttribute('data-glyph', 'ended');
  await expect(glyphOf(page, CODEX).locator('.glyph-badge')).toHaveCount(1);
  await expect(c.locator('.status-text')).toHaveText('Ending session…');
  await upsert(page, CODEX, { status: 'working', statusSince: later(), updatedAt: later() });

  // The person answers or replies: the host sends the session as theirs again.
  await upsert(page, CODEX, { driver: null, driverPrincipal: null, updatedAt: later() });
  await expect(c).toHaveAttribute('data-driver', 'person');
  await expect(c.locator('.driver')).toBeHidden();
  await expect(glyphOf(page, CODEX).locator('.glyph-badge')).toHaveCount(0);
  expect(await colourOf(glyphOf(page, CODEX))).toBe(await role(page, 'active'));
});

test('every glyph is named with its state, as a row reads: title, status, machine', async ({ page }) => {
  await openDemo(page);
  await expect(glyphOf(page, DELEGATING)).toHaveAccessibleName('Land the P1 slices, delegating to 2 agents · details not reported, Demo machine');
  await expect(glyphOf(page, COPILOT)).toHaveAccessibleName('Plan Migration to Windows App, waiting on you, Demo machine');
  await expect(page.getByRole('img', { name: 'Refactor the relay client, working, Demo machine' })).toHaveCount(1);
  // The tool's words are its name and summary, so its glyph says whether it is running, done or failed.
  await open(page, CLAUDE);
  await expect(card(page, CLAUDE).locator('.tool-line .glyph')).toHaveAccessibleName('running');
});

// ui.md §9.1 A1 (#365): the words a row draws, in the order drawn and joined with commas (its
// title, its second line's facts, its age or the new activity dot in its place), then what else
// its glyph's name says.
test('a session row is named for the words it draws, in the order drawn, then for what else its glyph says', async ({ page }) => {
  await openDemo(page);
  const named = (id) => row(page, id).locator('.row-main');
  await expect(named(COPILOT)).toHaveAccessibleName(/^Plan Migration to Windows App, Waiting on you, danswett\/agent-ha-bridge, \d+d ago, Demo machine$/);
  await expect(named(DELEGATING)).toHaveAccessibleName(/^Land the P1 slices, Delegating to 2 agents, danswett\/agent-bridge, \d+d ago, details not reported, Demo machine$/);
  // Driven: "Driven by an agent" in the status's place; and, updated while not open, the dot in the age's.
  await upsert(page, CODEX, { driver: 'agent', updatedAt: later() });
  await expect(named(CODEX)).toHaveAccessibleName('Fix flaky test, Driven by an agent, New activity, idle, Demo machine');
  // With every machine shown, the row draws its machine where the workspace was.
  await openDemo(page, '&machines=3');
  await expect(named(COPILOT)).toHaveAccessibleName(/^Plan Migration to Windows App, Waiting on you, Demo machine, \d+d ago$/);
});

// Rules 10-12 over rule 7 (#800): the row draws what is wrong, not "Driven by an agent", so its
// name starts with that too, and says it is driven only after its age (A1).
test('a driven session that needs sign-in or is not responding is named for the status its row draws, with its driver after its age', async ({ page }) => {
  await openDemo(page, '&signin=1');
  const named = (id) => row(page, id).locator('.row-main');
  await upsert(page, 'signin-1', { driver: 'agent', driverPrincipal: 'homeassistant:demo' });
  await expect(row(page, 'signin-1').locator('.row-status')).toHaveText('Needs sign-in');
  await expect(named('signin-1')).toHaveAccessibleName(/^Coordinate the Shiproom, Needs sign-in, danswett\/agent-bridge, [^,]+, Copilot · @demo-user, driven by an agent, Demo machine$/);
  await expect(row(page, 'stalled').locator('.row-status')).toHaveText('Not responding');
  await expect(named('stalled')).toHaveAccessibleName(/^Re-run the flaky suite, Not responding, [^,]+, since [^,]+, driven by an agent, Demo machine$/);
});

test('the latest response is rendered as Markdown', async ({ page }) => {
  await openDemo(page);
  const response = card(page, COPILOT).locator('.response');
  await expect(response.locator('li')).toHaveCount(2);
  await expect(response.locator('li strong').first()).toHaveText('Safe to read:');
  await expect(card(page, CLAUDE).locator('.response code')).toHaveText('RelayClient');
  await upsert(page, CODEX, { updatedAt: later(), activity: { response: '## Result\n\n| run | ok |\n|---|---|\n| 1 | yes |\n\n```\nnpm test\n```\nSee [the PR](https://github.com/danswett/agent-bridge/pull/1).' } });
  const codex = card(page, CODEX).locator('.response');
  await expect(codex.locator('h5')).toHaveText('Result');
  await expect(codex.locator('td')).toHaveText(['1', 'yes']);
  await expect(codex.locator('pre code')).toHaveText('npm test');
  await expect(codex.locator('a')).toHaveAttribute('href', 'https://github.com/danswett/agent-bridge/pull/1');
  await expect(codex.locator('a')).toHaveAttribute('rel', 'noopener noreferrer nofollow');
});

test('raw HTML and script links from a host are shown as text and never run', async ({ page }) => {
  const evil = '<img src=x onerror="window.__xss=1"><script>window.__xss=2</script><iframe src="javascript:window.__xss=3"></iframe>';
  const hostile = structuredClone(fixture);
  const s = hostile.sessions[0];
  s.title = `${evil} title`;
  s.repository = '<b>repo</b>';
  s.model = '<svg onload="window.__xss=4">';
  s.activity.response = `${evil}\n\n[click me](javascript:window.__xss=5) [data](data:text/html,<script>window.__xss=6</script>) <javascript:window.__xss=7> [ok](https://example.com)`;
  s.activity.reasoning = evil;
  s.activity.userMessage = evil;
  s.question.message = `${evil} [q](JaVaScRiPt:window.__xss=8)`;
  s.question.fields[0].title = evil;
  s.question.fields[0].description = evil;
  s.question.fields[0].options[0].label = evil;
  s.question.fields[3].default = evil;
  hostile.sessions[1].activity.currentTool.summary = evil;
  hostile.sessions[1].activity.currentTool.name = '<i>tool</i>';
  await page.route('**/fixtures/sessions-list.json', (route) => route.fulfill({ json: hostile }));
  await openDemo(page);

  // The rows and the sessions' views: everything a host's words reach.
  const list = page.locator('#session-list, #session-views');
  await expect(list.locator('.session')).toHaveCount(4);
  await expect(list.locator('img, script, iframe, svg[onload], b, i, object, embed')).toHaveCount(0);
  const hrefs = await list.locator('a[href]').evaluateAll((as) => as.map((a) => a.getAttribute('href')));
  expect(hrefs).toEqual(['https://example.com/']);
  // The text is shown as written, so a reader can see what the agent sent.
  await open(page, COPILOT);
  await expect(card(page, COPILOT).locator('.session-title')).toContainText('<img src=x onerror=');
  await expect(row(page, COPILOT).locator('.row-title')).toContainText('<img src=x onerror=');
  await expect(card(page, COPILOT).locator('.response')).toContainText('<script>window.__xss=2</script>');
  await expect(card(page, COPILOT).locator('.response')).toContainText('click me');
  await card(page, COPILOT).locator('.response').getByText('click me').click();
  await card(page, COPILOT).locator('details.reasoning summary').click();
  await page.waitForTimeout(300);
  expect(await page.evaluate(() => window.__xss)).toBeUndefined();
  expect(await page.evaluate(() => window.__cspViolations)).toEqual([]);
});

test('an unknown status shows as idle, an unknown agent generically, and unknown fields are ignored', async ({ page }) => {
  await openDemo(page);
  await page.evaluate((t) => window.agentBridgeDemo.upsert({
    id: 'from-the-future', agent: 'gemini', title: 'From a newer host', status: 'paused', statusSince: t, updatedAt: t,
    sparkle: { anything: true }, activity: { response: 'Hi', mood: 'great', currentTool: { id: 't', name: 'think', status: 'pondering', startedAt: t } },
    question: null,
  }), later());
  const c = card(page, 'from-the-future');
  await expect(c).toHaveAttribute('data-status', 'idle');
  await expect(c.locator('.status-text')).toHaveText('Idle');
  await expect(c.locator('.status-line .glyph')).toHaveAttribute('data-glyph', 'idle');
  await expect(c).toHaveAttribute('data-agent', 'unknown');
  await expect(c.locator('.agent-name')).toHaveText('Agent');
  await expect(c.locator('.tool-line')).toHaveAttribute('data-status', 'done');
  await expect(c.locator('.response')).toHaveText('Hi');
});

test('an unknown event is ignored and does not count as a gap', async ({ page }) => {
  await openDemo(page);
  const before = await calls(page, 'sessions.subscribe');
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    d.frame({ ev: 'session.sparkles', seq: d.host.seq + 1, data: { id: '6f1c2b7e-claude-example', status: 'idle' } });
  });
  await upsert(page, CODEX, { title: 'After the unknown event', updatedAt: later() });
  await expect(card(page, CODEX).locator('.session-title')).toHaveText('After the unknown event');
  await expect(card(page, CLAUDE)).toHaveAttribute('data-status', 'working');
  expect(await calls(page, 'sessions.subscribe')).toBe(before);
});

test('a gap in seq subscribes again, and the new snapshot replaces the list', async ({ page }) => {
  await openDemo(page);
  expect(await calls(page, 'sessions.subscribe')).toBe(1);
  // The host loses a session without telling us; only a fresh snapshot can show it.
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    d.host.sessions.delete('019a1b2c-codex-example');
    d.frame({ ev: 'session.upsert', seq: d.host.seq + 5, data: d.host.get('6f1c2b7e-claude-example') });
  });
  await expect.poll(() => calls(page, 'sessions.subscribe')).toBe(2);
  await expect(page.locator('.session')).toHaveCount(3);
  await expect(card(page, CODEX)).toHaveCount(0);
  expect(await calls(page, 'sessions.list')).toBe(0);
});

test('an upsert leaves every row where it is, and a remove takes the session to Ended', async ({ page }) => {
  await openDemo(page);
  await upsert(page, CODEX, { status: 'working', statusSince: later(), updatedAt: later() });
  await expect(card(page, CODEX)).toHaveAttribute('data-status', 'working');
  expect(await order(page)).toEqual([COPILOT, CLAUDE, CODEX, DELEGATING]);
  await page.evaluate((id) => window.agentBridgeDemo.remove(id), CLAUDE);
  await expect(card(page, CLAUDE)).toHaveCount(0);
  expect(await order(page)).toEqual([COPILOT, CODEX, DELEGATING]);
  await expect(page.locator('#session-count')).toHaveText('3 live on Demo machine');
  await expect(page.locator('.ended-group > summary')).toHaveText('Ended \u00b7 1');
});

test('in Most recent, an upsert moves a session to the top', async ({ page }) => {
  await openDemo(page);
  await page.locator('#session-sort').selectOption('recent');
  await upsert(page, CODEX, { status: 'working', statusSince: later(), updatedAt: later() });
  await expect.poll(() => order(page)).toEqual([CODEX, COPILOT, CLAUDE, DELEGATING]);
});

// Panes, not expanding cards (ui.md §1.4, #179): what folding a card did, a row and its view do.
test('the session chosen is remembered across a reload, and a new question shows in its view at once', async ({ page }) => {
  await openDemo(page);
  const codex = await open(page, CODEX);
  await expect(row(page, CODEX)).toHaveClass(/\bselected\b/);
  await expect(card(page, CLAUDE)).toBeHidden();

  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  await expect(card(page, CODEX)).toBeVisible();
  await expect(row(page, CODEX).locator('.row-main')).toHaveAttribute('aria-current', 'true');
  await expect(card(page, CLAUDE)).toBeHidden();

  const question = { id: 'toolu_new', kind: 'form', message: 'Ship it?', fields: [{ name: 'ok', type: 'boolean' }], askedAt: later() };
  await upsert(page, CODEX, { status: 'waiting', statusSince: later(), updatedAt: later(), question });
  await expect(codex.locator('.question')).toBeVisible();
  // Its row says so, and stays where it is.
  await expect(row(page, CODEX).locator('.row-status')).toHaveText('Waiting on you');
  expect(await order(page)).toEqual([COPILOT, CLAUDE, CODEX, DELEGATING]);
});

test('a session not chosen keeps its row through ordinary updates, and one tap on the row opens it', async ({ page }) => {
  await openDemo(page);
  const codex = card(page, CODEX);
  await upsert(page, CODEX, { updatedAt: later(), activity: { response: 'A plain update.' } });
  await expect(codex.locator('.response')).toHaveText('A plain update.');
  await expect(codex).toBeHidden();
  await expect(row(page, CODEX).locator('.row-main .glyph')).toHaveAttribute('data-glyph', 'idle');
  await expect(row(page, CODEX)).toHaveClass(/\bunread\b/);
  await row(page, CODEX).locator('.row-main').click();
  await expect(codex).toBeVisible();
  await expect(codex.locator('.response')).toBeVisible();
  // Seen now: its row loses the dot.
  await expect(row(page, CODEX)).not.toHaveClass(/\bunread\b/);
});

test('a row is a labelled button that opens its session from the keyboard, and Up and Down go between rows', async ({ page }) => {
  await openDemo(page);
  const open1 = row(page, CLAUDE).locator('.row-main');
  await expect(open1).toHaveAccessibleName(/Refactor the relay client/);
  await expect(card(page, CLAUDE)).toBeHidden();
  await open1.focus();
  await page.keyboard.press('Enter');
  await expect(card(page, CLAUDE)).toBeVisible();
  await page.keyboard.press('ArrowDown');
  await expect(row(page, CODEX).locator('.row-main')).toBeFocused();
  await page.keyboard.press(' ');
  await expect(card(page, CODEX)).toBeVisible();
  await expect(card(page, CLAUDE)).toBeHidden();
  await page.keyboard.press('ArrowUp');
  await expect(open1).toBeFocused();
});

test('a truncated question and response say so, and point to the machine', async ({ page }) => {
  await openDemo(page);
  await upsert(page, COPILOT, {
    updatedAt: later(),
    activity: { response: '…the end of a long answer.', responseTruncated: true },
    question: { ...fixture.sessions[0].question, fields: [], truncated: true },
  });
  const c = await open(page, COPILOT);
  await expect(c.locator('.note.truncated')).toBeVisible();
  // The machine takes answers, but not to a question it couldn't send whole.
  await expect(c.locator('.question')).toHaveAttribute('data-answerable', 'false');
  await expect(c.locator('.question .note.warn')).toHaveText("Part of this question didn't fit in the message from the machine, so it can't be answered from here. Answer at the machine on Demo machine.");
  await expect(c.locator('.question .field')).toHaveCount(0);
  await expect(c.locator('.question').locator('button, input, select, textarea')).toHaveCount(0);
});

test('a permission prompt only the machine can answer shows no buttons and says it is waiting at the machine', async ({ page }) => {
  // The machine takes answers, just not to this prompt.
  await openDemo(page);
  await upsert(page, COPILOT, {
    updatedAt: later(),
    question: {
      id: 'perm-1', kind: 'approval', message: 'Allow **Run a command**?\n\n`rm -rf build`', askedAt: later(), answerAtMachine: true,
      fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'approve', label: 'Approve' }, { value: 'deny', label: 'Deny' }] }],
    },
  });
  const c = card(page, COPILOT);
  await expect(c.locator('.status-text')).toHaveText('Waiting on you · answer at the machine');
  await expect(c.locator('.question')).toHaveAttribute('data-answerable', 'false');
  // Who asks, in the stream (ui.md §6.4).
  await expect(c.locator('.question .question-head')).toHaveText('Main agent asks');
  await expect(c.locator('.question .question-message')).toContainText('rm -rf build');
  await expect(c.locator('.question .read-only-why')).toHaveText('This prompt can only be answered at the machine on Demo machine.');
  await expect(c.locator('.question .field')).toHaveCount(0);
  await expect(c.locator('.question').locator('button, input, select, textarea')).toHaveCount(0);
  // Typed words would answer it, so the reply box is held, and says where to go.
  await expect(c.locator('.reply-held')).toHaveText('The session is waiting at the machine for a prompt only it can answer. Answer it there first, then reply.');
  await expect(c.locator('.reply-input')).toBeDisabled();
});

test('opening a session\'s tools fetches the session with session.get, once', async ({ page }) => {
  await openDemo(page);
  const history = card(page, CLAUDE).locator('.tools-panel');
  const before = await calls(page, 'session.get');
  await expect(history.locator('li.tool')).toHaveCount(0);
  // At the three-pane width the inspector shows them as the session opens.
  await open(page, CLAUDE);
  await expect(history.locator('li.tool')).toHaveCount(6);
  expect(await calls(page, 'session.get')).toBe(before + 1);
  await expect(history.locator('li.tool').first()).toContainText('Bash');
  await expect(history.locator('li.tool.failed')).toHaveCount(1);
});

test('the demo host keeps changing things on its own', async ({ page }) => {
  await page.goto('?demo=1&tick=150');
  await page.waitForSelector('html[data-demo-ready]');
  await expect(card(page, 'demo-new-session')).toHaveCount(1, { timeout: 10000 });
  await expect(card(page, 'demo-new-session')).toHaveCount(0, { timeout: 10000 });
});

test.describe('installed', () => {
  test.use({ serviceWorkers: 'allow' });

  test('the service worker controls only the app path', async ({ page }) => {
    await openDemo(page);
    const scope = await page.evaluate(async () => (await navigator.serviceWorker.ready).scope);
    expect(new URL(scope).pathname).toBe('/agent-bridge/app/');
  });

  test('an app whose new releases keep failing to install says so after a grace, and goes on with the one it has', async ({ page }) => {
    await openDemo(page);
    await page.waitForFunction(() => navigator.serviceWorker.controller !== null);
    await expect(page.locator('#stuck-banner')).toBeHidden();
    // Three starts' tries under the release in control, each failed (js/release-watch.js;
    // test/unit/release-watch.test.mjs).
    const release = () => page.evaluate(async () => {
      const record = await caches.match(new URL('releases.json', (await navigator.serviceWorker.ready).scope).href);
      return (await record?.json())?.active ?? null;
    });
    // The worker writes its record as it takes over, alongside taking the page.
    await expect.poll(release).toMatch(/^agent-bridge-app-/);
    const active = await release();
    await page.evaluate((r) => localStorage.setItem('agent-bridge.app.installFailures', JSON.stringify({ release: r, failures: 3 })), active);
    await page.reload();
    await page.waitForSelector('html[data-demo-ready]');
    // Only a lasting state gets a banner (ui.md §7, #1064): none until it has stood three seconds.
    await expect(page.locator('#stuck-banner')).toBeHidden();
    await expect(page.locator('#stuck-banner')).toBeVisible({ timeout: 10000 });
    await expect(page.locator('#stuck-banner')).toContainText("A new version of the app keeps failing to install. You're still on the version you had.");
    await expect(page.locator('#stuck-banner .button.secondary')).toHaveText('Try again');
    // Said through the live region already on the page, which one unhidden with its words is not.
    await expect(page.locator('#banner-said')).toHaveText("A new version of the app keeps failing to install. You're still on the version you had.");
    await expect(page.locator('.session').first()).toBeVisible();
  });
});

test('a long unbroken path never makes a row or a session wider than a phone screen', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openDemo(page);
  const path = 'C:\\Users\\AdminUser\\AppData\\Local\\AgentBridge\\default\\attachments\\5900c7370ce946f98d9cc5a6a7e669b4-IMG_3499.jpeg';
  await upsert(page, CLAUDE, {
    status: 'working',
    title: `Read${'_'.repeat(20)}${path}`,
    activity: { currentTool: { id: 'toolu_wide', name: 'Read', summary: path, status: 'running', startedAt: new Date().toISOString() } },
  });
  const rows = () => page.evaluate(() => ({
    page: document.documentElement.scrollWidth,
    view: document.documentElement.clientWidth,
    cards: [...document.querySelectorAll('.session, .session-view:not([hidden]), .session-view:not([hidden]) .tool-line')].map((e) => Math.ceil(e.getBoundingClientRect().right)),
  }));
  let widths = await rows();
  expect(widths.page).toBeLessThanOrEqual(widths.view);
  for (const right of widths.cards) expect(right).toBeLessThanOrEqual(widths.view);
  // And its view, pushed over the list.
  await open(page, CLAUDE);
  await expect(card(page, CLAUDE).locator('.tool-line')).toBeVisible();
  widths = await rows();
  expect(widths.page).toBeLessThanOrEqual(widths.view);
  for (const right of widths.cards) expect(right).toBeLessThanOrEqual(widths.view);
});

// A13 (#373): chrome's words aren't selected by a press, a long press or a double tap, and
// everything a person may want to copy still is. Whether a part is selectable is the first
// user-select set on it or around it, as the browser uses it.
test('chrome\'s words are not selectable, and the conversation, code, a question and every field are', async ({ page }) => {
  await openDemo(page);
  await open(page, CODEX);
  await upsert(page, CODEX, { updatedAt: later(), activity: { response: 'Run this once:\n\n```\nnpm run build\n```' } });
  const v = card(page, CODEX);
  await expect(v.locator('.response pre code')).toHaveText('npm run build');
  const select = (locator) => locator.first().evaluate((el) => {
    for (let e = el; e; e = e.parentElement) {
      const s = getComputedStyle(e);
      const used = s.userSelect || s.webkitUserSelect;
      if (used && used !== 'auto') { return used; }
    }
    return 'auto';
  });
  const chrome = {
    'a row\'s title': row(page, CODEX).locator('.row-title'),
    'a row\'s facts': row(page, CODEX).locator('.row-facts'),
    'the session\'s title': v.locator('.session-title'),
    'the session\'s status line': v.locator('.status-line'),
    'a tab': v.locator('.view-tab'),
    'a button\'s words': v.locator('.end-session button'),
    'the page\'s title': page.locator('#sessions-h'),
    'the app bar\'s title': page.locator('.brand-name'),
    'the navigation': page.locator('.nav-item .nav-label'),
    'a banner\'s words': page.locator('#demo-banner span'),
  };
  for (const [name, locator] of Object.entries(chrome)) { expect(await select(locator), name).toBe('none'); }
  const content = {
    'the response': v.locator('.response p'),
    'code': v.locator('.response pre code'),
    'a question': card(page, COPILOT).locator('.question-message'),
    'the reply box': v.getByRole('textbox', { name: 'Reply' }),
    'search': page.locator('#search'),
  };
  for (const [name, locator] of Object.entries(content)) { expect(await select(locator), name).not.toBe('none'); }
  // As a double tap or a double click finds them: the title's words aren't taken, the response's
  // are. Each on its first word: a block's middle may be past its words.
  const picked = async (locator) => {
    await page.evaluate(() => getSelection().removeAllRanges());
    const box = await locator.first().boundingBox();
    await locator.first().dblclick({ position: { x: 8, y: box.height / 2 } });
    return page.evaluate(() => getSelection().toString().trim());
  };
  expect(await picked(v.locator('.session-title'))).toBe('');
  expect(await picked(v.locator('.response p'))).not.toBe('');
});
