// Motion (docs/design/ui.md §5.3, #232): every motion the catalogue draws is timed by its
// generated token and none runs longer than 300 ms; reduced motion leaves only short fades;
// nothing moves as the app starts; a tap during a motion still lands; and a question that
// arrives is scrolled to only while the conversation is read to its end.
import { test, expect } from '@playwright/test';
import { open, row, view } from './nav.mjs';

const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CLAUDE = '6f1c2b7e-claude-example';
const CODEX = '019a1b2c-codex-example';
const later = (ms = 1000) => new Date(Date.now() + ms).toISOString();

// Every animation the page asks for, from the first script on: js/ui/motion.js names each one
// ab-motion-<entry> (Animation.id), set just after it is made, so it is read when asked for.
async function openDemo(page, { width = 1440, height = 900 } = {}) {
  await page.setViewportSize({ width, height });
  await page.addInitScript(() => {
    window.__motions = [];
    const animate = Element.prototype.animate;
    Element.prototype.animate = function (keyframes, options) {
      const a = animate.call(this, keyframes, options);
      window.__motions.push({ a, keyframes, duration: options?.duration ?? 0 });
      return a;
    };
  });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await expect(page.locator('.session')).not.toHaveCount(0);
}

const motions = (page) => page.evaluate(() => window.__motions.map((m) => ({
  id: m.a.id,
  duration: m.duration,
  travels: m.keyframes.some((k) => k.transform && k.transform !== 'none'),
  from: m.keyframes[0]?.transform ?? null,
})));

// What the tokens say an entry takes, going in and going out, in ms.
const tokens = (page) => page.evaluate(() => {
  const css = getComputedStyle(document.documentElement);
  const ms = (name) => parseFloat(css.getPropertyValue(name)) || 0;
  const out = {};
  for (const entry of ['push', 'pane', 'tab', 'disclosure', 'list-item', 'glyph', 'activity-dot', 'selection', 'press', 'acknowledge', 'toast', 'menu', 'scroll', 'reorder']) {
    out[entry.replace(/-(\w)/g, (_, c) => c.toUpperCase())] = [ms(`--ab-motion-${entry}-duration`), ms(`--ab-motion-${entry}-exit-duration`)].filter((v) => v > 0);
  }
  out.crossFade = ms('--ab-motion-cross-fade');
  return out;
});

async function upsert(page, id, change) {
  await page.evaluate(({ id, change }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id) ?? { id };
    d.upsert({ ...s, ...change, activity: { ...(s.activity ?? {}), ...(change.activity ?? {}) } });
  }, { id, change });
}

// The motions a person sets off on a desktop: choosing a row, its menu, a move, a disclosure,
// the inspector, and the news that comes (a glyph changing, a row arriving and going, a dot).
async function desktopTour(page) {
  await row(page, CLAUDE).locator('.row-main').click();
  await expect(row(page, CLAUDE)).toHaveClass(/selected/);
  await row(page, CLAUDE).locator('.row-main').press('Shift+F10');
  await expect(page.locator('.row-menu')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.locator('.row-menu')).toHaveCount(0);
  await row(page, CLAUDE).locator('.row-main').press('Alt+ArrowDown');
  const copilot = await open(page, COPILOT);
  await copilot.locator('details.reasoning summary').click();
  await expect(copilot.locator('details.reasoning')).toHaveAttribute('open', '');
  await copilot.locator('.inspector-button').click();
  await expect(copilot.locator('.inspector')).toBeHidden();
  await copilot.locator('.inspector-button').click();
  await expect(copilot.locator('.inspector')).toBeVisible();
  await upsert(page, CODEX, { status: 'working', statusSince: later(), updatedAt: later() });
  await expect(row(page, CODEX).locator('.glyph')).toHaveAttribute('data-glyph', 'working');
  await upsert(page, 'demo-motion', { agent: 'copilot', title: 'Arrives and goes', status: 'idle', startedAt: later(), updatedAt: later() });
  await expect(row(page, 'demo-motion')).toHaveCount(1);
  await page.evaluate(() => window.agentBridgeDemo.remove('demo-motion'));
  await expect(row(page, 'demo-motion')).toHaveCount(0);
}

test('nothing moves as the app starts, and every motion a desktop then draws is timed by its catalogue entry\'s token, none past 300 ms', async ({ page }) => {
  await openDemo(page);
  await page.waitForTimeout(400);
  expect(await motions(page)).toEqual([]);
  await desktopTour(page);
  const t = await tokens(page);
  const drawn = await motions(page);
  const ids = new Set(drawn.map((m) => m.id));
  for (const entry of ['selection', 'menu', 'reorder', 'disclosure', 'pane', 'glyph', 'listItem', 'activityDot']) {
    expect(ids, `drew ${entry}`).toContain(`ab-motion-${entry}`);
  }
  for (const m of drawn) {
    const entry = m.id.replace(/^ab-motion-/, '');
    expect(t[entry], m.id).toContain(m.duration);
    expect(m.duration, m.id).toBeLessThanOrEqual(300);
  }
});

test('the tab bar\'s indicator slides to the tab chosen, and what the tab shows fades in without sliding', async ({ page }) => {
  await openDemo(page, { width: 1000 });
  const v = await open(page, COPILOT);
  await v.getByRole('tab', { name: 'Tools' }).click();
  const drawn = await motions(page);
  const indicator = drawn.find((m) => m.id === 'ab-motion-selection');
  expect(indicator?.from).toMatch(/^translate\(-[\d.]+px, 0px\) scaleX\(/);
  const tab = drawn.find((m) => m.id === 'ab-motion-tab');
  expect(tab).toBeTruthy();
  expect(tab.travels).toBe(false);
});

test('on a phone a session\'s content slides in from the trailing side, and the list\'s from the leading side on Back', async ({ page }) => {
  await openDemo(page, { width: 390, height: 844 });
  const t = await tokens(page);
  await open(page, COPILOT);
  await view(page, COPILOT).locator('.session-header .back').click();
  await expect(row(page, COPILOT)).toBeVisible();
  const push = (await motions(page)).filter((m) => m.id === 'ab-motion-push');
  expect(push.map((m) => m.from)).toEqual(['translate(24px, 0px)', 'translate(-24px, 0px)']);
  expect(push.every((m) => m.duration === t.push[0])).toBe(true);
});

test('with reduced motion nothing travels: the only motions are fades no longer than the cross-fade, and presses change at once', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await openDemo(page, { width: 390, height: 844 });
  await open(page, COPILOT);
  await view(page, COPILOT).locator('.session-header .back').click();
  await expect(row(page, COPILOT)).toBeVisible();
  await page.setViewportSize({ width: 1440, height: 900 });
  await desktopTour(page);
  const t = await tokens(page);
  const drawn = await motions(page);
  expect(drawn.map((m) => m.id)).toContain('ab-motion-push');
  for (const m of drawn) {
    expect(m.travels, m.id).toBe(false);
    expect(m.duration, m.id).toBeLessThanOrEqual(t.crossFade);
  }
  const transitions = await page.evaluate(() => [...document.querySelectorAll('.button, .icon-button, .row-main')]
    .flatMap((el) => getComputedStyle(el).transitionDuration.split(',').map((d) => d.trim())));
  expect(new Set(transitions)).toEqual(new Set(['0s']));
});

test('a tap during a push lands: Back works while the session is still sliding in', async ({ page }) => {
  await openDemo(page, { width: 390, height: 844 });
  // Long enough to be sure the tap comes mid-motion.
  await page.evaluate(() => document.documentElement.style.setProperty('--ab-motion-push-duration', '5000ms'));
  await row(page, COPILOT).locator('.row-main').click();
  const back = view(page, COPILOT).locator('.session-header .back');
  await expect(back).toBeAttached();
  expect(await page.evaluate(() => document.getAnimations().some((a) => a.id === 'ab-motion-push' && a.playState === 'running'))).toBe(true);
  const b = await back.boundingBox();
  await page.mouse.click(b.x + b.width / 2, b.y + b.height / 2);
  await expect(row(page, COPILOT)).toBeVisible();
  await expect(view(page, COPILOT)).toBeHidden();
});

test('a tap during a menu\'s entrance lands on the item under it', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => document.documentElement.style.setProperty('--ab-motion-menu-duration', '5000ms'));
  await row(page, CLAUDE).locator('.row-main').click({ button: 'right' });
  const pin = page.locator('.row-menu').getByRole('menuitem', { name: 'Pin' });
  await expect(pin).toBeVisible();
  expect(await page.evaluate(() => document.getAnimations().some((a) => a.id === 'ab-motion-menu' && a.playState === 'running'))).toBe(true);
  const b = await pin.boundingBox();
  await page.mouse.click(b.x + b.width / 2, b.y + b.height / 2);
  await expect(row(page, CLAUDE)).toHaveAttribute('data-pinned', 'true');
});

test('a question that arrives is scrolled to while the conversation is read to its end, and never pulls one scrolled back', async ({ page }) => {
  await openDemo(page, { height: 600 });
  const long = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
  await upsert(page, CODEX, { activity: { response: long }, updatedAt: later() });
  const v = await open(page, CODEX);
  const scroller = v.locator('.stream-scroll');
  const ask = (id, ms) => page.evaluate(({ id, ms, from }) => {
    const d = window.agentBridgeDemo;
    const q = { ...d.host.get(from).question, id };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), question: q, status: 'waiting', updatedAt: new Date(Date.now() + ms).toISOString() });
  }, { id, ms, from: COPILOT });
  // Read back to the top: a question that comes leaves the place alone.
  await scroller.evaluate((el) => { el.scrollTop = 0; });
  await ask('q-motion-1', 2000);
  await expect(v.locator('.question-slot .question')).toHaveCount(1);
  await page.waitForTimeout(400);
  expect(await scroller.evaluate((el) => el.scrollTop)).toBe(0);
  // At the end: the next question comes into sight.
  await scroller.evaluate((el) => { el.scrollTop = el.scrollHeight; });
  await page.evaluate(() => window.agentBridgeDemo.upsert({ ...window.agentBridgeDemo.host.get('019a1b2c-codex-example'), question: null, updatedAt: new Date(Date.now() + 3000).toISOString() }));
  await expect(v.locator('.question-slot .question')).toHaveCount(0);
  await scroller.evaluate((el) => { el.scrollTop = el.scrollHeight; });
  await ask('q-motion-2', 4000);
  await expect(v.locator('.question-slot .question')).toBeInViewport();
});
