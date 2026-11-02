// The app in demo mode (?demo=1): a simulated host speaking the real protocol to the real
// client, so these tests cover event handling through to what is on screen.
import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';

const fixture = JSON.parse(readFileSync(new URL('../fixtures/sessions-list.json', import.meta.url), 'utf8'));
const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CLAUDE = '6f1c2b7e-claude-example';
const CODEX = '019a1b2c-codex-example';
const DELEGATING = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';

const card = (page, id) => page.locator(`.session[data-id="${id}"]`);
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

  const copilot = card(page, COPILOT);
  await expect(copilot).toHaveAttribute('data-status', 'waiting');
  await expect(copilot.locator('.title-text')).toHaveText('Plan Migration to Windows App');
  await expect(copilot.locator('.meta')).toContainText('Copilot');
  await expect(copilot.locator('.meta')).toContainText('danswett/agent-ha-bridge');
  await expect(copilot.locator('.meta')).toContainText('detached@5e59309');
  await expect(copilot.locator('.meta')).toContainText('claude-opus-5.5');
  await expect(copilot.locator('.status-text')).toHaveText('Waiting on you');
  await expect(copilot.locator('.since')).toHaveText(/^for /);
  await expect(copilot.locator('.facts')).toContainText('412 turns');
  await expect(copilot.locator('.facts')).toContainText('1 compaction');
  await expect(copilot.locator('.facts')).toContainText('pid 39844');
  await expect(copilot.locator('details.reasoning')).toBeVisible();
  await expect(copilot.locator('details.user-message')).toBeVisible();

  // The question, read-only, from the contract's fields.
  const q = copilot.locator('.question');
  await expect(q.locator('.question-head')).toHaveText('Waiting on you');
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

  const claude = card(page, CLAUDE);
  await expect(claude).toHaveAttribute('data-status', 'working');
  await expect(claude.locator('.status-text')).toHaveText('Working');
  await expect(claude.locator('.tool-line')).toBeVisible();
  await expect(claude.locator('.tool-line .tool-name')).toHaveText('Bash');
  await expect(claude.locator('.tool-line .tool-summary')).toHaveText('dotnet test');
  await expect(claude.locator('.question')).toHaveCount(0);

  const codex = card(page, CODEX);
  await expect(codex).toHaveAttribute('data-status', 'idle');
  await expect(codex.locator('.status-text')).toHaveText('Idle');
  await expect(codex.locator('.tool-line')).toBeHidden();
  await expect(codex.locator('.since')).toHaveText('');

  const delegating = card(page, DELEGATING);
  await expect(delegating).toHaveAttribute('data-status', 'idle');
  await expect(delegating).toHaveAttribute('data-view', 'delegating');
  await expect(delegating.locator('.status-text')).toHaveText('Waiting for background agents');
  await expect(delegating.locator('.since')).toHaveText(/^for /);
  await expect(delegating.locator('.facts')).toContainText('2 background agents · 1 background command · 5 turns');

  expect(await order(page)).toEqual([COPILOT, CLAUDE, CODEX, DELEGATING]);
  expect(await page.evaluate(() => window.__cspViolations)).toEqual([]);
});

// The shadow host beside agent-ha-bridge (docs/host-shadow.md), or any machine where it runs
// (design G5): read-only, saying so and why, with nothing offered that would only be refused.
test('a machine where agent-ha-bridge runs is shown read-only with its reason, and offers no actions', async ({ page }) => {
  await openDemo(page, '&ahb=1');
  const reason = 'agent-ha-bridge is running for this user (AgentBridgeDaemon); answer at the machine or through it';
  await expect(page.locator('.machine .machine-status')).toHaveText(` · 4 sessions · 0.1.0 (dev) · read-only: ${reason}`);
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

test('each status has its glow: working and waiting breathe, idle is calm', async ({ page }) => {
  await openDemo(page);
  const glow = (id) => card(page, id).locator('.glow').evaluate((e) => getComputedStyle(e).animationName);
  await expect(card(page, CLAUDE).locator('.frame')).toHaveClass(/\bworking\b/);
  await expect(card(page, COPILOT).locator('.frame')).toHaveClass(/\bwaiting\b/);
  await expect(card(page, CODEX).locator('.frame')).toHaveClass(/\bidle\b/);
  expect(await glow(CLAUDE)).toBe('glow');
  expect(await glow(COPILOT)).toBe('glow');
  expect(await glow(CODEX)).toBe('none');
  const border = (id) => card(page, id).locator('.frame').evaluate((e) => getComputedStyle(e).borderTopColor);
  expect(await border(CLAUDE)).not.toBe(await border(COPILOT));
  expect(await border(CODEX)).not.toBe(await border(CLAUDE));
});

test('an idle session still waiting on background work is delegating: its own still, dashed edge and text', async ({ page }) => {
  await openDemo(page);
  const c = card(page, DELEGATING);
  const frame = c.locator('.frame');
  const style = () => frame.evaluate((e) => ({ border: getComputedStyle(e).borderTopStyle, color: getComputedStyle(e).borderTopColor }));
  const working = await card(page, CLAUDE).locator('.frame').evaluate((e) => getComputedStyle(e).borderTopColor);
  const idle = await card(page, CODEX).locator('.frame').evaluate((e) => ({ border: getComputedStyle(e).borderTopStyle, color: getComputedStyle(e).borderTopColor }));

  await expect(frame).toHaveClass(/\bdelegating\b/);
  await expect(frame).not.toHaveClass(/\bidle\b/);
  expect(await style()).toEqual({ border: 'dashed', color: working });
  expect(idle.border).toBe('solid');
  // Still: delegating is not working, so it does not breathe.
  expect(await c.locator('.glow').evaluate((e) => getComputedStyle(e).animationName)).toBe('none');

  // Only shells left: it says commands.
  await upsert(page, DELEGATING, { updatedAt: later(), activity: { background: { agents: 0, shells: 1 } } });
  await expect(c.locator('.status-text')).toHaveText('Waiting for background commands');
  await expect(c.locator('.facts')).toContainText('1 background command');
  await expect(c.locator('.facts')).not.toContainText('background agent');

  // Nothing left: plain idle again.
  await upsert(page, DELEGATING, { updatedAt: later(), activity: { background: null } });
  await expect(c).toHaveAttribute('data-view', 'idle');
  await expect(c.locator('.status-text')).toHaveText('Idle');
  await expect(frame).toHaveClass(/\bidle\b/);
  await expect(frame).not.toHaveClass(/\bdelegating\b/);
  await expect(c.locator('.since')).toHaveText('');
  expect((await style()).border).toBe('solid');

  // A session working on its own turn is working, whatever it has in the background.
  await upsert(page, DELEGATING, { status: 'working', statusSince: later(), updatedAt: later(), activity: { background: { agents: 1, shells: 0 } } });
  await expect(c).toHaveAttribute('data-view', 'working');
  await expect(c.locator('.status-text')).toHaveText('Working');
  await expect(c.locator('.facts')).toContainText('1 background agent');
});

test('with reduced motion the glow holds still and the spinner stops', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await openDemo(page);
  const style = (id, sel) => card(page, id).locator(sel).evaluate((e) => ({ animation: getComputedStyle(e).animationName, opacity: getComputedStyle(e).opacity }));
  expect(await style(CLAUDE, '.glow')).toEqual({ animation: 'none', opacity: '0.6' });
  expect(await style(COPILOT, '.glow')).toEqual({ animation: 'none', opacity: '0.6' });
  expect((await style(CLAUDE, '.tool-line .spinner')).animation).toBe('none');
});

test('a session an agent drives has a purple edge, steady while idle and breathing while it works, says so, and goes back when the person takes it', async ({ page }) => {
  await openDemo(page);
  const c = card(page, CODEX);
  const frame = c.locator('.frame');
  // The edge's colour eases over 0.4 s, so it is read once the frame's transitions have finished.
  const border = (id) => card(page, id).locator('.frame').evaluate(async (e) => {
    await Promise.all(e.getAnimations().map((a) => a.finished));
    return getComputedStyle(e).borderTopColor;
  });
  const glow = () => c.locator('.glow').evaluate((e) => ({ animation: getComputedStyle(e).animationName, shadow: getComputedStyle(e).boxShadow }));
  // The agent role's colour.
  const purple = await page.evaluate(() => {
    const probe = document.createElement('i');
    probe.style.color = 'var(--ab-agent)';
    document.body.append(probe);
    const colour = getComputedStyle(probe).color;
    probe.remove();
    return colour;
  });
  const calm = await border(CODEX);
  const working = await border(CLAUDE);
  const workingGlow = await card(page, CLAUDE).locator('.glow').evaluate((e) => getComputedStyle(e).boxShadow);
  await expect(c).toHaveAttribute('data-driver', 'person');
  await expect(c.locator('.driver')).toBeHidden();

  await upsert(page, CODEX, { driver: 'agent', driverPrincipal: 'homeassistant:0f1e2d3c', updatedAt: later() });
  await expect(c).toHaveAttribute('data-driver', 'agent');
  await expect(frame).toHaveClass(/\bagent\b/);
  await expect(c.locator('.driver')).toHaveText('Driven by an agent');
  await expect(c.locator('.driver')).toHaveAttribute('title', 'homeassistant:0f1e2d3c');
  expect(purple).not.toBe(calm);
  expect(purple).not.toBe(working);
  expect(await border(CODEX)).toBe(purple);
  // Idle: a steady edge, no breathing.
  expect((await glow()).animation).toBe('none');

  await upsert(page, CODEX, { status: 'working', statusSince: later(), updatedAt: later() });
  await expect(frame).toHaveClass(/\bworking\b/);
  expect(await border(CODEX)).toBe(purple);
  const breathing = await glow();
  expect(breathing.animation).toBe('glow');
  expect(breathing.shadow).not.toBe(workingGlow);

  // Still the agent's while the machine ends it: dotted, in purple.
  await upsert(page, CODEX, { status: 'ending', statusSince: later(), updatedAt: later() });
  await expect(frame).toHaveClass(/\bending\b/);
  expect(await border(CODEX)).toBe(purple);
  expect(await frame.evaluate((e) => getComputedStyle(e).borderTopStyle)).toBe('dotted');
  await upsert(page, CODEX, { status: 'working', statusSince: later(), updatedAt: later() });

  // The person answers or replies: the host sends the session as theirs again.
  await upsert(page, CODEX, { driver: null, driverPrincipal: null, updatedAt: later() });
  await expect(c).toHaveAttribute('data-driver', 'person');
  await expect(frame).not.toHaveClass(/\bagent\b/);
  await expect(c.locator('.driver')).toBeHidden();
  expect(await border(CODEX)).toBe(working);
});

test('with reduced motion an agent\'s purple glow holds still too', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await openDemo(page);
  await upsert(page, CLAUDE, { driver: 'agent', updatedAt: later() });
  await expect(card(page, CLAUDE).locator('.frame')).toHaveClass(/\bagent\b/);
  const glow = await card(page, CLAUDE).locator('.glow').evaluate((e) => ({ animation: getComputedStyle(e).animationName, opacity: getComputedStyle(e).opacity }));
  expect(glow).toEqual({ animation: 'none', opacity: '0.6' });
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

  const list = page.locator('#session-list');
  await expect(list.locator('.session')).toHaveCount(4);
  await expect(list.locator('img, script, iframe, svg[onload], b, i, object, embed')).toHaveCount(0);
  const hrefs = await list.locator('a[href]').evaluateAll((as) => as.map((a) => a.getAttribute('href')));
  expect(hrefs).toEqual(['https://example.com/']);
  // The text is shown as written, so a reader can see what the agent sent.
  await expect(card(page, COPILOT).locator('.title-text')).toContainText('<img src=x onerror=');
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
  await expect(c.locator('.frame')).toHaveClass(/\bidle\b/);
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
  await expect(card(page, CODEX).locator('.title-text')).toHaveText('After the unknown event');
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
  await expect(page.locator('#session-count')).toHaveText('3 live');
  await expect(page.locator('.ended-group > summary')).toHaveText('Ended \u00b7 1');
});

test('in Most recent, an upsert moves a session to the top', async ({ page }) => {
  await openDemo(page);
  await page.locator('#session-sort').selectOption('recent');
  await upsert(page, CODEX, { status: 'working', statusSince: later(), updatedAt: later() });
  await expect.poll(() => order(page)).toEqual([CODEX, COPILOT, CLAUDE, DELEGATING]);
});

test('folding is remembered across a reload, and a new question unfolds the card', async ({ page }) => {
  await openDemo(page);
  const codex = card(page, CODEX);
  const fold = codex.locator('button.fold');
  await expect(fold).toHaveAttribute('aria-expanded', 'true');
  await fold.click();
  await expect(fold).toHaveAttribute('aria-expanded', 'false');
  await expect(codex.locator('.body')).toBeHidden();
  // Folded, the header still says what the session is doing.
  await expect(codex.locator('.status-text')).toBeVisible();

  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  await expect(card(page, CODEX).locator('button.fold')).toHaveAttribute('aria-expanded', 'false');
  await expect(card(page, CODEX).locator('.body')).toBeHidden();
  await expect(card(page, CLAUDE).locator('button.fold')).toHaveAttribute('aria-expanded', 'true');

  const question = { id: 'toolu_new', kind: 'form', message: 'Ship it?', fields: [{ name: 'ok', type: 'boolean' }], askedAt: later() };
  await upsert(page, CODEX, { status: 'waiting', statusSince: later(), updatedAt: later(), question });
  await expect(card(page, CODEX).locator('button.fold')).toHaveAttribute('aria-expanded', 'true');
  await expect(card(page, CODEX).locator('.question')).toBeVisible();

  // Folded again while the same question waits, it stays folded.
  await card(page, CODEX).locator('button.fold').click();
  await upsert(page, CODEX, { updatedAt: later(), activity: { response: 'Still waiting.' } });
  await expect(card(page, CODEX).locator('.response')).toHaveText('Still waiting.');
  await expect(card(page, CODEX).locator('button.fold')).toHaveAttribute('aria-expanded', 'false');
});

test('a folded card stays folded through ordinary updates, and one tap opens it', async ({ page }) => {
  await openDemo(page);
  const codex = card(page, CODEX);
  await codex.locator('button.fold').click();
  await upsert(page, CODEX, { updatedAt: later(), activity: { response: 'A plain update.' } });
  await expect(codex.locator('.response')).toHaveText('A plain update.');
  await expect(codex.locator('.frame')).toHaveClass(/\bcollapsed\b/);
  await expect(codex.locator('.frame')).toHaveClass(/\bidle\b/);
  await codex.locator('button.fold').click();
  await expect(codex.locator('button.fold')).toHaveAttribute('aria-expanded', 'true');
  await expect(codex.locator('.body')).toBeVisible();
});

test('the fold button is a labelled disclosure that works from the keyboard', async ({ page }) => {
  await openDemo(page);
  const fold = card(page, CLAUDE).locator('button.fold');
  const controls = await fold.getAttribute('aria-controls');
  await expect(page.locator(`#${controls}`)).toBeVisible();
  await expect(fold).toHaveAccessibleName(/Refactor the relay client/);
  await fold.focus();
  await page.keyboard.press('Enter');
  await expect(fold).toHaveAttribute('aria-expanded', 'false');
  await expect(page.locator(`#${controls}`)).toBeHidden();
  await page.keyboard.press(' ');
  await expect(fold).toHaveAttribute('aria-expanded', 'true');
});

test('a truncated question and response say so, and point to the machine', async ({ page }) => {
  await openDemo(page);
  await upsert(page, COPILOT, {
    updatedAt: later(),
    activity: { response: '…the end of a long answer.', responseTruncated: true },
    question: { ...fixture.sessions[0].question, fields: [], truncated: true },
  });
  const c = card(page, COPILOT);
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
  await expect(c.locator('.status-text')).toHaveText('Waiting at the machine');
  await expect(c.locator('.question')).toHaveAttribute('data-answerable', 'false');
  await expect(c.locator('.question .question-head')).toHaveText('Permission requested');
  await expect(c.locator('.question .question-message')).toContainText('rm -rf build');
  await expect(c.locator('.question .read-only-why')).toHaveText('This prompt can only be answered at the machine on Demo machine.');
  await expect(c.locator('.question .field')).toHaveCount(0);
  await expect(c.locator('.question').locator('button, input, select, textarea')).toHaveCount(0);
  // Typed words would answer it, so the reply box is held, and says where to go.
  await expect(c.locator('.reply-held')).toHaveText('The session is waiting at the machine for a prompt only it can answer. Answer it there first, then reply.');
  await expect(c.locator('.reply-input')).toBeDisabled();
});

test('opening the tool history fetches the session with session.get', async ({ page }) => {
  await openDemo(page);
  const history = card(page, CLAUDE).locator('details.history');
  expect(await calls(page, 'session.get')).toBe(0);
  await history.locator('summary').click();
  await expect(history.locator('li.tool')).toHaveCount(6);
  expect(await calls(page, 'session.get')).toBe(1);
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
});

test('a long unbroken path never makes a card wider than a phone screen', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openDemo(page);
  const path = 'C:\\Users\\AdminUser\\AppData\\Local\\AgentBridge\\default\\attachments\\5900c7370ce946f98d9cc5a6a7e669b4-IMG_3499.jpeg';
  await upsert(page, CLAUDE, {
    status: 'working',
    title: `Read${'_'.repeat(20)}${path}`,
    activity: { currentTool: { id: 'toolu_wide', name: 'Read', summary: path, status: 'running', startedAt: new Date().toISOString() } },
  });
  await expect(card(page, CLAUDE).locator('.tool-line')).toBeVisible();

  const widths = await page.evaluate(() => ({
    page: document.documentElement.scrollWidth,
    view: document.documentElement.clientWidth,
    cards: [...document.querySelectorAll('.session')].map((e) => Math.ceil(e.getBoundingClientRect().right)),
  }));
  expect(widths.page).toBeLessThanOrEqual(widths.view);
  for (const right of widths.cards) expect(right).toBeLessThanOrEqual(widths.view);
});
test('a desktop-wide window puts the sessions side by side, a phone keeps one column', async ({ page }) => {
  const columns = () => page.locator('.session').evaluateAll((els) => new Set(els.map((e) => Math.round(e.getBoundingClientRect().left))).size);
  const overflow = () => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);

  await page.setViewportSize({ width: 1440, height: 900 });
  await openDemo(page);
  await expect(page.locator('.session')).toHaveCount(4);
  expect(await columns()).toBeGreaterThanOrEqual(2);
  expect(await overflow()).toBeLessThanOrEqual(0);

  await page.setViewportSize({ width: 390, height: 844 });
  await expect.poll(columns).toBe(1);
  expect(await overflow()).toBeLessThanOrEqual(0);
});