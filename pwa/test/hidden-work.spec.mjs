import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';
import { assertLoopingAnimation } from './animation-assertions.mjs';
import { indicatorStyle, readFont } from './hidden-work-fixture.mjs';

async function indicator(locator, name, pseudo = null) {
  const style = await locator.evaluate(indicatorStyle, pseudo);
  assertLoopingAnimation(style, name);
  return style.animationPlayState;
}

async function surface(page) {
  await page.route('https://visibility.test/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/') {
      await route.fulfill({ contentType: 'text/html', body: '<link rel="stylesheet" href="/design/tokens.css"><link rel="stylesheet" href="/app.css"><section id="launch"><h1 class="launch-title"></h1><div class="launch-body"></div></section><div class="glyph"><span class="glyph-spin">Working</span></div><form class="reply"><button class="button send" aria-busy="true">Send</button></form>' });
      return;
    }
    if (path.startsWith('/design/fonts/')) {
      await route.fulfill({ contentType: 'font/woff2', body: await readFont(path) });
      return;
    }
    await route.fulfill({ contentType: path.endsWith('.js') ? 'text/javascript' : 'text/css', body: await readFile(new URL(`..${path}`, import.meta.url)) });
  });
  await page.clock.install();
  await page.goto('https://visibility.test/');
  expect(await page.evaluate(async () => (await document.fonts.load('400 15px "Slipgates Sans"')).length),
    'the fixture must load the shipped font, not silently use a fallback').toBeGreaterThan(0);
  await page.clock.pauseAt(await page.evaluate(() => Date.now()) + 100);
  await visibility(page, true);
  await page.evaluate(() => {
    const state = window.state = { wakes: 0 };
    for (const name of ['setTimeout', 'setInterval']) {
      const set = window[name];
      window[name] = (fn, ms, ...args) => set(() => { state.wakes++; fn(...args); }, ms);
    }
  });
}

async function visibility(page, visible) {
  await page.evaluate((visible) => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: visible ? 'visible' : 'hidden' });
    Object.defineProperty(document, 'hidden', { configurable: true, value: !visible });
    document.dispatchEvent(new Event('visibilitychange'));
  }, visible);
}

test('pagehide without visibilitychange suspends motion and clocks until a persisted pageshow', async ({ page }) => {
  await surface(page);
  await page.evaluate(async () => {
    const { whileVisible } = await import('/js/ui/ticker.js');
    const { watchPresses, enter } = await import('/js/ui/motion.js');
    state.ticks = 0;
    state.ticker = whileVisible(() => state.ticks++, 30000);
    watchPresses(document);
    document.documentElement.style.setProperty('--ab-motion-value-duration', '60000ms');
    state.motion = enter(document.querySelector('#launch'), 'value');
    window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true }));
  });
  expect(await page.evaluate(() => ({ visibility: document.visibilityState,
    pausedAttribute: document.documentElement.hasAttribute('data-motion-hidden'), motion: state.motion.playState,
  }))).toEqual({ visibility: 'visible', pausedAttribute: true, motion: 'paused' });
  await page.clock.runFor(30000);
  expect(await page.evaluate(() => ({ ticks: state.ticks, wakes: state.wakes }))).toEqual({ ticks: 0, wakes: 0 });
  await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true })));
  expect(await page.evaluate(() => ({ ticks: state.ticks, pausedAttribute: document.documentElement.hasAttribute('data-motion-hidden'),
    motion: state.motion.playState,
  }))).toEqual({ ticks: 1, pausedAttribute: false, motion: 'running' });
  await page.evaluate(() => state.ticker.stop());
});

test('hidden pages pause glyphs, pseudo-element spinners and running motion without replaying hidden changes', async ({ page }) => {
  await surface(page);
  await page.evaluate(async () => {
    const { watchPresses, enter } = await import('/js/ui/motion.js');
    watchPresses(document);
    document.documentElement.style.setProperty('--ab-motion-value-duration', '60000ms');
    state.enter = enter;
    state.motion = enter(document.querySelector('#launch'), 'value');
  });
  expect(await indicator(page.locator('.glyph-spin'), 'glyph-turn')).toBe('running');
  expect(await indicator(page.locator('.send'), 'spin', '::after')).toBe('running');
  expect(await page.evaluate(() => state.motion.playState)).toBe('running');
  await visibility(page, false);
  expect(await indicator(page.locator('.glyph-spin'), 'glyph-turn')).toBe('paused');
  expect(await indicator(page.locator('.send'), 'spin', '::after')).toBe('paused');
  expect(await page.evaluate(() => state.motion.playState)).toBe('paused');
  expect(await page.evaluate(() => state.enter(document.querySelector('#launch'), 'value'))).toBeNull();
  await page.evaluate(() => document.querySelector('.glyph').insertAdjacentHTML('beforeend', '<span class="glyph-orbit">Waiting</span>'));
  expect(await indicator(page.locator('.glyph-orbit'), 'glyph-turn')).toBe('paused');
  await visibility(page, true);
  expect(await page.evaluate(() => state.motion.playState)).toBe('running');
  expect(await indicator(page.locator('.glyph-spin'), 'glyph-turn')).toBe('running');
  expect(await indicator(page.locator('.send'), 'spin', '::after')).toBe('running');
  await visibility(page, false);
  await page.evaluate(() => state.motion.cancel());
  await visibility(page, true);
  expect(await page.evaluate(() => state.motion.playState)).toBe('idle');
});

test('a visible visibilitychange recovers pagehide without a persisted pageshow', async ({ page }) => {
  await surface(page);
  await page.evaluate(async () => {
    const { whileVisible } = await import('/js/ui/ticker.js');
    const { watchPresses } = await import('/js/ui/motion.js');
    state.ticks = 0;
    state.ticker = whileVisible(() => state.ticks++, 30000);
    watchPresses(document);
    window.dispatchEvent(new PageTransitionEvent('pagehide'));
  });
  expect(await indicator(page.locator('.glyph-spin'), 'glyph-turn')).toBe('paused');
  await page.clock.runFor(30000);
  expect(await page.evaluate(() => state.ticks)).toBe(0);
  await visibility(page, true);
  expect(await page.evaluate(() => state.ticks)).toBe(1);
  expect(await indicator(page.locator('.glyph-spin'), 'glyph-turn')).toBe('running');
  expect(await indicator(page.locator('.send'), 'spin', '::after')).toBe('running');
  await page.evaluate(() => state.ticker.stop());
});

test('motion reconnected while hidden resumes, even across a detached visible return', async ({ page }) => {
  await surface(page);
  await page.evaluate(async () => {
    const { watchPresses, enter } = await import('/js/ui/motion.js');
    watchPresses(document);
    document.documentElement.style.setProperty('--ab-motion-value-duration', '60000ms');
    state.target = document.querySelector('#launch');
    state.motion = enter(state.target, 'value');
  });
  await visibility(page, false);
  expect(await page.evaluate(() => state.motion.playState)).toBe('paused');
  await page.evaluate(() => { state.target.remove(); document.body.append(state.target); });
  await visibility(page, true);
  expect(await page.evaluate(() => state.motion.playState)).toBe('running');
  await visibility(page, false);
  await page.evaluate(() => state.target.remove());
  await visibility(page, true);
  expect(await page.evaluate(() => state.motion.playState)).toBe('paused');
  await visibility(page, false);
  await page.evaluate(() => document.body.append(state.target));
  expect(await page.evaluate(() => state.motion.playState)).toBe('paused');
  await visibility(page, true);
  expect(await page.evaluate(() => state.motion.playState)).toBe('running');
});
