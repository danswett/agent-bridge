// Reduced motion (#356, ui.md §5.3 and §9): with `prefers-reduced-motion: reduce` nothing moves.
// Every view and every change the motion catalogue animates is walked through, and at each step
// every element and its ::before and ::after are read: no animation runs, no transition takes
// longer than nothing, and none but an opacity cross-fade (`crossFade`, 83 ms) takes any time at
// all; nothing scrolls smoothly; and nothing is left running. With motion allowed the same walk
// finds the glyphs turning, the progress ring and the usage bars, so the check can see them.
import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';
import { go, open, row } from './nav.mjs';

const tokens = JSON.parse(readFileSync(new URL('../../design/tokens.json', import.meta.url), 'utf8'));
// The PWA inherits Windows' timings (ui.md §5.3).
const CROSS_FADE_MS = tokens.motion.platform.windows.duration.crossFade;
const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CLAUDE = '6f1c2b7e-claude-example';
const DELEGATES = 'demo-delegates';

// The elements the catalogue animates in the PWA today, each of which the walk shows on screen:
// the moving part of a working and a delegating glyph, the in-flight progress ring, a usage bar, a
// delegate's chevron (Disclosure), a session row (Arriving and leaving, Press), the pushed session
// (Push and pop), a tab (on a phone; a desktop has none), the row menu (Menu and flyout), and the
// inspector (Pane and inspector; on a phone it holds the Tools tab).
const ANIMATED = [
  '.glyph .glyph-spin', '.glyph .glyph-orbit', '.answer-status.progress', '.usage-fill', '.delegate-chevron',
  '.session .row-main', '.session-view', '.view-tab', '.row-menu', '.inspector', '.needs-you-list > li', 'details.reasoning > summary',
];

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1&delegates=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

// What on the page moves, or could: each animation that runs for any time, each transition but a
// short cross-fade, each smooth scroll, and each animation running now.
function moving({ crossFade, animated }) {
  const ms = (v) => (v.endsWith('ms') ? parseFloat(v) : parseFloat(v) * 1000);
  const list = (v) => v.split(/,\s*/);
  const name = (e, pseudo) => {
    const cls = (e.getAttribute('class') ?? '').trim();
    return `${e.tagName.toLowerCase()}${cls ? `.${cls.split(/\s+/).join('.')}` : ''}${pseudo ?? ''}`;
  };
  const found = new Set();
  for (const e of document.querySelectorAll('*')) {
    for (const pseudo of [null, '::before', '::after']) {
      const s = getComputedStyle(e, pseudo);
      if (pseudo && s.content === 'none') { continue; }
      const durations = list(s.animationDuration).map(ms);
      list(s.animationName).forEach((n, i) => {
        if (n !== 'none' && durations[i % durations.length] > 0) { found.add(`${name(e, pseudo)}: animation ${n} ${durations[i % durations.length]}ms`); }
      });
      const tDurations = list(s.transitionDuration).map(ms);
      list(s.transitionProperty).forEach((p, i) => {
        const d = tDurations[i % tDurations.length];
        if (d > 0 && !(p === 'opacity' && d <= crossFade)) { found.add(`${name(e, pseudo)}: transition ${p} ${d}ms`); }
      });
      if (!pseudo && s.scrollBehavior === 'smooth') { found.add(`${name(e)}: smooth scroll`); }
    }
  }
  for (const a of document.getAnimations()) {
    if (a.playState !== 'running' || !a.effect?.target) { continue; }
    const t = a.effect.getTiming();
    // What it animates, from its keyframes: every engine has those, not every one names a
    // transition's property on the animation.
    const props = new Set(a.effect.getKeyframes().flatMap((k) => Object.keys(k)).filter((p) => !['offset', 'computedOffset', 'easing', 'composite'].includes(p)));
    const opacityOnly = props.size === 1 && props.has('opacity') && typeof t.duration === 'number' && t.duration <= crossFade;
    if (!opacityOnly) { found.add(`${name(a.effect.target, a.effect.pseudoElement)}: running ${a.animationName ?? a.transitionProperty ?? [...props].join(' ')}`); }
  }
  // Shown means drawn: in the layout, not hidden and not transparent. Below the fold counts: what is
  // read is computed style, which scrolling doesn't change.
  const drawn = (e) => {
    if (e.getClientRects().length === 0) { return false; }
    const s = getComputedStyle(e);
    return s.visibility !== 'hidden' && s.opacity !== '0';
  };
  return { found: [...found], shown: animated.filter((sel) => [...document.querySelectorAll(sel)].some(drawn)) };
}

// Every view and change the catalogue animates, with what moves at each step.
async function walk(page, { phone }) {
  const steps = [];
  const seen = new Set();
  const look = async (label) => {
    const { found, shown } = await page.evaluate(moving, { crossFade: CROSS_FADE_MS, animated: ANIMATED });
    steps.push({ label, found });
    for (const s of shown) { seen.add(s); }
  };
  await openDemo(page, '&answerDelay=200');
  // A working session (the turning arc) and a delegating one (the orbit) are in the list.
  await look('the list');

  // Push: a session opened over the list on a phone, beside it on a desktop.
  const copilot = await open(page, COPILOT);
  await look('a session opened');

  // Disclosure: reasoning shown.
  const reasoning = copilot.locator('details.reasoning > summary');
  if (await reasoning.isVisible()) { await reasoning.click(); await look('reasoning shown'); }

  // Send and Answer: a press in flight shows the progress ring.
  await page.evaluate(() => window.agentBridgeDemo.host.holdResponses());
  const claude = await open(page, CLAUDE);
  await claude.getByRole('textbox', { name: 'Reply' }).fill('Ship it.');
  await claude.getByRole('button', { name: 'Send' }).click();
  await expect(claude.locator('.answer-status.progress, .answer-receipt.progress').first()).toBeVisible();
  await look('a reply in flight');
  await page.evaluate(() => window.agentBridgeDemo.host.releaseResponses());

  // Menu and flyout: the row menu.
  if (phone) { await go(page, 'sessions'); }
  await row(page, CLAUDE).locator('.row-main').focus();
  await page.keyboard.press('Shift+F10');
  await expect(page.locator('.row-menu')).toBeVisible();
  await look('a row menu open');
  await page.keyboard.press('Escape');

  // Tab switch and Disclosure: the delegates, a delegate opened, the finished fold opened.
  const delegating = await open(page, DELEGATES);
  if (phone) { await delegating.getByRole('tab', { name: 'Delegates' }).click(); }
  const suite = delegating.locator('.delegate[data-id="demo-agent-suite"]');
  await suite.locator('.delegate-summary').click();
  await expect(suite.locator('.delegate-task')).toBeVisible();
  await delegating.locator('.delegates-finished > summary').click();
  await look('delegates opened');
  if (phone) { await delegating.getByRole('tab', { name: 'Tools' }).click(); await look('the tools tab'); }

  // Pop: back to the list on a phone.
  if (phone) { await go(page, 'sessions'); await look('back to the list'); }

  // Usage bars, and the pages.
  await go(page, 'usage');
  await expect(page.locator('.usage-fill').first()).toBeAttached();
  await look('usage');
  for (const route of ['machines', 'launch', 'settings']) { await go(page, route); await look(route); }
  return { steps, seen: [...seen] };
}

for (const layout of [
  { name: 'on a phone', viewport: { width: 390, height: 664 }, phone: true, absent: [] },
  { name: 'on a desktop', viewport: { width: 1440, height: 900 }, phone: false, absent: ['.view-tab'] },
]) {
  test.describe(layout.name, () => {
    test.use({ viewport: layout.viewport, hasTouch: layout.phone });

    test('with reduced motion nothing animates and no transition but a short cross-fade takes any time, in every view the catalogue animates', async ({ page }) => {
      await page.emulateMedia({ reducedMotion: 'reduce' });
      const { steps, seen } = await walk(page, layout);
      for (const { label, found } of steps) { expect.soft(found, label).toEqual([]); }
      // The walk showed everything the catalogue animates here, so each of them was read.
      expect(seen.sort()).toEqual(ANIMATED.filter((s) => !layout.absent.includes(s)).sort());
    });

    test('with motion allowed the same walk finds the glyphs turning, the progress ring and the usage bars easing', async ({ page }) => {
      await page.emulateMedia({ reducedMotion: 'no-preference' });
      const { steps } = await walk(page, layout);
      const all = steps.flatMap((s) => s.found).join('\n');
      expect(all).toMatch(/glyph-spin: animation glyph-turn 1200ms/);
      expect(all).toMatch(/glyph-orbit: animation glyph-turn 8000ms/);
      expect(all).toMatch(/progress::before: animation spin 1000ms/);
      expect(all).toMatch(/usage-fill: transition width 400ms/);
      expect(all).toMatch(/delegate-chevron: transition transform 200ms/);
    });
  });
}
