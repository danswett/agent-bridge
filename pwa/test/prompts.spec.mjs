// A prompt the person didn't type (#489, ui.md §6.4): a schedule's or an agent's prompt is drawn
// as a collapsed line in agent colour at the column's start, never as the person's bubble; Show
// prompt opens it; a schedule's repeat folds; and a host that doesn't say who sent a prompt keeps
// today's bubble.
import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { readFileSync } from 'node:fs';
import { motionsDone, open, view } from './nav.mjs';

const tokens = JSON.parse(readFileSync(new URL('../../design/tokens.json', import.meta.url), 'utf8'));
const CODEX = '019a1b2c-codex-example';
const MONITOR = [
  'Check the deploy pipeline for agent-bridge. If the last run on main failed, find the failing job,',
  'read its log, and say which test failed and why. If it passed, say nothing new and stop.',
  'Then look at the open pull requests: for each one whose checks are red, name the check.',
  'Keep the answer under ten lines. Do not change any file.',
].join('\n');
let clock = Date.parse('2026-11-03T19:00:00Z');
const later = () => new Date((clock += 1000)).toISOString();

async function openDemo(page) {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
}

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

async function upsert(page, id, change) {
  await page.evaluate(({ id, change }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id) ?? { id };
    d.upsert({ ...s, ...change, activity: { ...(s.activity ?? {}), ...(change.activity ?? {}) } });
  }, { id, change });
}

// A schedule's prompt, as a host sends it (#489): who sent it, and the schedule's number.
const scheduled = (extra = {}) => ({ status: 'idle', updatedAt: later(), activity: { userMessage: MONITOR, userMessageFrom: 'schedule', userMessageLabel: '4', turnCount: 7, ...extra } });

async function role(page, name) {
  return page.evaluate((n) => {
    const probe = document.createElement('span');
    probe.style.color = `var(--ab-${n})`;
    document.body.append(probe);
    const c = getComputedStyle(probe).color;
    probe.remove();
    return c;
  }, name);
}
const style = (locator, prop) => locator.evaluate((e, p) => getComputedStyle(e)[p], prop);

test('a scheduled prompt is not a bubble: a clock and "Scheduled prompt · #4" in agent colour at the column\'s start, then two lines of it and Show prompt', async ({ page }) => {
  await openDemo(page);
  await upsert(page, CODEX, scheduled());
  const v = await open(page, CODEX);
  const p = v.locator('.prompt-origin');
  await expect(p).toBeVisible();
  await expect(v.locator('.user-message')).toBeHidden();
  await expect(p).toHaveAttribute('data-from', 'schedule');
  await expect(p.locator('.prompt-facts')).toHaveText('Scheduled prompt · #4');
  await expect(p.locator('.prompt-icon svg')).toHaveAttribute('data-icon', 'clock');
  expect(await style(p.locator('.prompt-facts'), 'color')).toBe(await role(page, 'agent-text'));
  expect(await style(p.locator('.prompt-icon'), 'color')).toBe(await role(page, 'agent'));
  // At the column's start and across it, never at the right with the person's.
  const column = await v.locator('.stream').boundingBox();
  const box = await p.boundingBox();
  expect(Math.abs(box.x - column.x)).toBeLessThan(1);
  expect(Math.abs(box.width - column.width)).toBeLessThan(1);
  expect(await style(p, 'backgroundColor')).toBe('rgba(0, 0, 0, 0)');
  // Two lines of it in secondary text, and the rest behind Show prompt.
  const preview = p.locator('.prompt-preview');
  await expect(preview).toContainText('Check the deploy pipeline for agent-bridge.');
  const line = parseFloat(await style(preview, 'lineHeight'));
  expect((await preview.boundingBox()).height).toBeLessThanOrEqual(line * 2 + 1);
  expect(await style(preview, 'color')).toBe(await role(page, 'text2'));
  await expect(p.locator('.prompt-full')).toBeHidden();
  const toggle = p.getByRole('button', { name: 'Scheduled prompt 4, Show prompt' });
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await expect(toggle).toHaveText('Show prompt');
});

test('Show prompt opens with Enter and closes with Space: the full text in reading style inside a thin agent rule, not a filled card', async ({ page }) => {
  await openDemo(page);
  await upsert(page, CODEX, scheduled());
  const v = await open(page, CODEX);
  const p = v.locator('.prompt-origin');
  const toggle = p.locator('.prompt-toggle');
  await toggle.focus();
  await page.keyboard.press('Enter');
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  await expect(toggle).toHaveAccessibleName('Scheduled prompt 4, Hide prompt');
  const full = p.locator('.prompt-full');
  await expect(full).toBeVisible();
  await expect(full.locator('.plain')).toHaveText(MONITOR);
  await expect(p.locator('.prompt-preview')).toBeHidden();
  expect(await style(full, 'borderLeftColor')).toBe(await role(page, 'agent'));
  expect(parseFloat(await style(full, 'borderLeftWidth'))).toBeLessThanOrEqual(2);
  expect(await style(full, 'backgroundColor')).toBe('rgba(0, 0, 0, 0)');
  expect(await style(full.locator('.plain'), 'fontSize')).toBe(await style(v.locator('.stream'), 'fontSize'));
  // An update to the same prompt leaves it as the person left it.
  await upsert(page, CODEX, { updatedAt: later(), activity: { response: 'All green.' } });
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  await toggle.focus();
  await page.keyboard.press(' ');
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await expect(full).toBeHidden();
  await expect(toggle).toBeFocused();
});

test('the label is read before the prompt, and nothing presents it as the person\'s', async ({ page }) => {
  await openDemo(page);
  await upsert(page, CODEX, scheduled());
  const v = await open(page, CODEX);
  const p = v.locator('.prompt-origin');
  // Document order is reading and focus order: the label line, the preview, then the button.
  expect(await p.evaluate((el) => Array.from(el.children, (c) => c.className))).toEqual(['prompt-label', 'prompt-preview', 'prompt-toggle', 'prompt-full']);
  await expect(p).not.toContainText('Your last message');
  await expect(v.locator('.user-message .plain')).toHaveText('');
  const snapshot = await p.ariaSnapshot();
  expect(snapshot.indexOf('Scheduled prompt')).toBeLessThan(snapshot.indexOf('Check the deploy pipeline'));
});

test('a schedule firing again with the same words folds to "same as last time" with no preview, and a new prompt starts collapsed', async ({ page }) => {
  await openDemo(page);
  await upsert(page, CODEX, scheduled());
  const v = await open(page, CODEX);
  const p = v.locator('.prompt-origin');
  await p.locator('.prompt-toggle').click();
  await expect(p.locator('.prompt-toggle')).toHaveAttribute('aria-expanded', 'true');
  // The schedule fires again: its turn starts with the same words, and once it is done the host
  // counts one prompt more.
  const since = later();
  await upsert(page, CODEX, { status: 'working', statusSince: since, updatedAt: since, activity: { userMessage: MONITOR, turnCount: 7 } });
  await expect(p.locator('.prompt-facts')).toHaveText('Scheduled prompt · #4');
  await upsert(page, CODEX, { status: 'idle', statusSince: later(), updatedAt: later(), activity: { userMessage: MONITOR, turnCount: 8 } });
  await expect(p.locator('.prompt-facts')).toHaveText('Scheduled prompt · #4 · same as last time');
  await expect(p).toHaveAttribute('data-repeat', '');
  await expect(p.locator('.prompt-preview')).toBeHidden();
  await expect(p.locator('.prompt-toggle')).toHaveAttribute('aria-expanded', 'false');
  // Dated by when its turn began, in meta style.
  await expect(p.locator('.prompt-age')).toHaveAttribute('datetime', since);
  await expect(p.locator('.prompt-age')).toBeVisible();
  // New words from the same schedule are not a repeat.
  await upsert(page, CODEX, { status: 'working', statusSince: later(), updatedAt: later(), activity: { userMessage: 'Check the relay instead.', turnCount: 8 } });
  await expect(p.locator('.prompt-facts')).toHaveText('Scheduled prompt · #4');
  await expect(p.locator('.prompt-preview')).toHaveText('Check the relay instead.');
});

test('an agent\'s prompt has the driven diamond and "Sent by an agent", with the agent driving as secondary text', async ({ page }) => {
  await openDemo(page);
  await upsert(page, CODEX, { driver: 'agent', driverPrincipal: 'homeassistant:0f1e2d3c', updatedAt: later(), activity: { userMessage: 'Run the nightly check.', userMessageFrom: 'agent', turnCount: 2 } });
  const v = await open(page, CODEX);
  const p = v.locator('.prompt-origin');
  await expect(p.locator('.prompt-facts')).toHaveText('Sent by an agent');
  await expect(p.locator('.prompt-icon .driven-diamond use')).toHaveAttribute('href', /#glyph-driven-badge$/);
  await expect(p.locator('.prompt-by')).toHaveText('homeassistant:0f1e2d3c');
  expect(await style(p.locator('.prompt-by'), 'color')).toBe(await role(page, 'text2'));
  await expect(p.getByRole('button', { name: 'Prompt sent by an agent, Show prompt' })).toHaveAttribute('aria-expanded', 'false');
  await expect(v.locator('.user-message')).toBeHidden();
});

test('a prompt from a host that doesn\'t say who sent it, or that says the person, is the person\'s bubble as before', async ({ page }) => {
  await openDemo(page);
  await upsert(page, CODEX, { updatedAt: later(), activity: { userMessage: 'Looks good, ship it.', turnCount: 2 } });
  const v = await open(page, CODEX);
  const bubble = v.locator('.user-message');
  await expect(bubble).toBeVisible();
  await expect(bubble.locator('.plain')).toHaveText('Looks good, ship it.');
  await expect(bubble.locator('.sr-only')).toHaveText('Your last message: ');
  await expect(v.locator('.prompt-origin')).toBeHidden();
  const column = await v.locator('.stream').boundingBox();
  const box = await bubble.boundingBox();
  expect(Math.abs(box.x + box.width - (column.x + column.width))).toBeLessThan(1);
  // A scheduled prompt, then the person's again: the bubble comes back, and only the person's rises
  // as their message acknowledged (ui.md §5.3); a prompt they didn't type never does.
  await bubble.evaluate((el) => {
    el.rises = [];
    const animate = el.animate.bind(el);
    el.animate = (...args) => { const a = animate(...args); el.rises.push(a); return a; };
  });
  // motion.js names each animation just after making it, so the names are read here, later.
  const rises = () => bubble.evaluate((el) => el.rises.filter((a) => a.id === 'ab-motion-acknowledge').length);
  await upsert(page, CODEX, scheduled({ turnCount: 3 }));
  await expect(v.locator('.prompt-origin')).toBeVisible();
  await expect(bubble).toBeHidden();
  expect(await rises()).toBe(0);
  await upsert(page, CODEX, { status: 'working', statusSince: later(), updatedAt: later(), activity: { userMessage: 'Stop that.', userMessageFrom: 'person', userMessageLabel: null, turnCount: 3 } });
  await expect(bubble).toBeVisible();
  await expect(bubble.locator('.plain')).toHaveText('Stop that.');
  await expect(v.locator('.prompt-origin')).toBeHidden();
  expect(await rises()).toBe(1);
});

test('a label is text: no markup, no hidden characters, no fact of its own, and never read as the main agent', async ({ page }) => {
  await openDemo(page);
  await upsert(page, CODEX, scheduled({ userMessageLabel: '<img src=x onerror="window.__xss=1">\u202E', userMessage: '<script>window.__xss=2</script>\u200B' }));
  const v = await open(page, CODEX);
  const p = v.locator('.prompt-origin');
  await expect(p.locator('img, script')).toHaveCount(0);
  await expect(p.locator('.prompt-facts')).toHaveText('Scheduled prompt · \u201c<img src=x onerror=\'window.__xss=1\'>\u201d');
  await expect(p.locator('.prompt-preview')).toHaveText('<script>window.__xss=2</script>\\u200B');
  await upsert(page, CODEX, scheduled({ userMessageLabel: 'Main agent', turnCount: 8, userMessage: 'x' }));
  await expect(p.locator('.prompt-facts')).toHaveText('Scheduled prompt · \u201cMain agent\u201d');
  await upsert(page, CODEX, scheduled({ userMessageLabel: '4 · same as last time', turnCount: 9, userMessage: 'y' }));
  await expect(p.locator('.prompt-facts')).toHaveText('Scheduled prompt · \u201c4 - same as last time\u201d');
  expect(await page.evaluate(() => window.__xss)).toBeUndefined();
});

test('on a phone the prompt is a full-width row', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openDemo(page);
  await upsert(page, CODEX, scheduled());
  const v = await open(page, CODEX);
  const p = v.locator('.prompt-origin');
  const column = await v.locator('.stream').boundingBox();
  const box = await p.boundingBox();
  expect(Math.abs(box.x - column.x)).toBeLessThan(1);
  expect(Math.abs(box.width - column.width)).toBeLessThan(1);
  // A touch target, on a phone (ui.md §9).
  expect((await p.locator('.prompt-toggle').boundingBox()).height).toBeGreaterThanOrEqual(32);
});

for (const reduced of [true, false]) {
  test(`Show prompt ${reduced ? 'is instant under reduced motion' : 'turns its chevron and settles the text in by the Disclosure token'}`, async ({ page }) => {
    await page.emulateMedia({ reducedMotion: reduced ? 'reduce' : 'no-preference' });
    await openDemo(page);
    await upsert(page, CODEX, scheduled());
    const v = await open(page, CODEX);
    const p = v.locator('.prompt-origin');
    await p.locator('.prompt-toggle').click();
    const ms = (s) => (s.endsWith('ms') ? parseFloat(s) : parseFloat(s) * 1000);
    const chevron = ms(await style(p.locator('.prompt-chevron'), 'transitionDuration'));
    const settle = ms(await style(p.locator('.prompt-full'), 'animationDuration'));
    const disclosure = tokens.motion.platform.windows.duration[tokens.motion.catalogue.disclosure.duration];
    expect([chevron, settle]).toEqual(reduced ? [0, 0] : [disclosure, disclosure]);
    await expect(p.locator('.prompt-full')).toBeVisible();
    expect(await style(p.locator('.prompt-chevron'), 'transform')).not.toBe('none');
  });
}

for (const colorScheme of ['light', 'dark']) {
  test(`a scheduled prompt, collapsed and open, has no accessibility violation in ${colorScheme}`, async ({ page }) => {
    await page.emulateMedia({ colorScheme });
    await openDemo(page);
    await upsert(page, CODEX, scheduled());
    const v = await open(page, CODEX);
    // At rest, as axe.spec's scan is: the open prompt settles in from opacity 0 (prompt-settle),
    // and read partway through that fade its text is paler than anyone reads it (#880).
    const scan = async () => {
      await motionsDone(page);
      return (await new AxeBuilder({ page }).include('.session-view:not([hidden]) .stream')
        .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22a', 'wcag22aa', 'best-practice']).analyze()).violations
        .filter((x) => x.impact === 'serious' || x.impact === 'critical').map((x) => `${x.id}: ${x.nodes.map((n) => n.target.join(' ')).join(', ')}`);
    };
    expect(await scan()).toEqual([]);
    await v.locator('.prompt-toggle').click();
    expect(await scan()).toEqual([]);
  });
}
