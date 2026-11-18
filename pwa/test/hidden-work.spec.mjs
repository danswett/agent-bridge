import { test, expect } from '@playwright/test';
import { readFile } from 'node:fs/promises';

async function surface(page, hidden = false) {
  await page.route('https://visibility.test/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (path === '/') {
      await route.fulfill({ contentType: 'text/html', body: '<link rel="stylesheet" href="/app.css"><section id="launch"><h1 class="launch-title"></h1><div class="launch-body"></div></section><div class="glyph"><span class="glyph-spin">Working</span></div><form class="reply" aria-busy="true"><button class="button send">Send</button></form>' });
      return;
    }
    await route.fulfill({ contentType: path.endsWith('.js') ? 'text/javascript' : 'text/css', body: await readFile(new URL(`..${path}`, import.meta.url)) });
  });
  await page.clock.install();
  await page.goto('https://visibility.test/');
  await page.clock.pauseAt(await page.evaluate(() => Date.now()) + 100);
  await visibility(page, !hidden);
  await page.evaluate(async () => {
    const { LaunchPanel } = await import('/js/ui/launch-view.js');
    const state = window.state = { wakes: 0, reads: 0, jumps: 0, arrived: false, entry: null };
    for (const name of ['setTimeout', 'setInterval']) {
      const set = window[name];
      window[name] = (fn, ms, ...args) => set(() => { state.wakes++; fn(...args); }, ms);
    }
    state.panel = new LaunchPanel(document.querySelector('#launch'), {
      service: { watch: (_id, fn) => { state.settle = fn; return () => {}; }, get: () => state.entry },
      jump: () => { state.jumps++; return state.arrived; },
    });
    state.panel.setTarget({ hostId: 'target', machineName: 'Test machine', load: () => new Promise(() => {}) });
  });
}

async function visibility(page, visible) {
  await page.evaluate((visible) => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: visible ? 'visible' : 'hidden' });
    Object.defineProperty(document, 'hidden', { configurable: true, value: !visible });
    document.dispatchEvent(new Event('visibilitychange'));
  }, visible);
}

for (const hidden of [false, true]) {
  test(`launch probes stop their clock while hidden, including when started ${hidden ? 'hidden' : 'visible'}`, async ({ page }) => {
    await surface(page, hidden);
    await page.evaluate(() => state.panel.probeOthers([{ hostId: 'other', load: async () => { state.reads++; return { trust: [] }; } }]));
    if (!hidden) {
      await page.clock.runFor(30000);
      expect(await page.evaluate(() => state.reads)).toBe(2);
      await visibility(page, false);
    }
    const before = await page.evaluate(() => ({ wakes: state.wakes, reads: state.reads }));
    await page.clock.runFor(60000);
    expect(await page.evaluate(() => ({ wakes: state.wakes, reads: state.reads }))).toEqual(before);
    await visibility(page, true);
    expect(await page.evaluate(() => state.reads)).toBe(before.reads + 1);
    await visibility(page, true);
    expect(await page.evaluate(() => state.reads)).toBe(before.reads + 1);
    await page.clock.runFor(30000);
    expect(await page.evaluate(() => state.reads)).toBe(before.reads + 2);
    await page.evaluate(() => state.panel.clear());
    const cleared = await page.evaluate(() => state.wakes);
    await visibility(page, false);
    await visibility(page, true);
    await page.clock.runFor(30000);
    expect(await page.evaluate(() => state.wakes)).toBe(cleared);
  });

  test(`launch jump retries stop their clock while hidden, including when started ${hidden ? 'hidden' : 'visible'}`, async ({ page }) => {
    await surface(page, hidden);
    await page.evaluate(() => {
      state.panel.setOpen(true);
      state.entry = { state: 'done', outcome: 'started', hostId: 'target', request: 'request', detail: { session: 'session' } };
      state.settle(state.entry);
    });
    if (!hidden) {
      await page.clock.runFor(250);
      expect(await page.evaluate(() => state.jumps)).toBe(2);
      await visibility(page, false);
    }
    const before = await page.evaluate(() => ({ wakes: state.wakes, jumps: state.jumps }));
    await page.clock.runFor(1000);
    expect(await page.evaluate(() => ({ wakes: state.wakes, jumps: state.jumps }))).toEqual(before);
    await page.evaluate(() => { state.arrived = true; });
    await visibility(page, true);
    expect(await page.evaluate(() => state.jumps)).toBe(before.jumps + 1);
    const finished = await page.evaluate(() => state.wakes);
    await visibility(page, false);
    await visibility(page, true);
    await page.clock.runFor(1000);
    expect(await page.evaluate(() => state.wakes)).toBe(finished);
  });
}

test('a launch jump that expires while hidden does not jump or restart on return', async ({ page }) => {
  await surface(page, true);
  await page.evaluate(() => {
    state.panel.setOpen(true);
    state.entry = { state: 'done', outcome: 'started', hostId: 'target', request: 'request', detail: { session: 'session' } };
    state.settle(state.entry);
  });
  await page.clock.runFor(16000);
  expect(await page.evaluate(() => state.wakes)).toBe(0);
  await visibility(page, true);
  await page.clock.runFor(1000);
  expect(await page.evaluate(() => ({ wakes: state.wakes, jumps: state.jumps }))).toEqual({ wakes: 0, jumps: 0 });
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
  expect(await page.evaluate(() => state.motion.playState)).toBe('running');
  await visibility(page, false);
  expect(await page.locator('.glyph-spin').evaluate((el) => getComputedStyle(el).animationPlayState)).toBe('paused');
  expect(await page.locator('.send').evaluate((el) => getComputedStyle(el, '::after').animationPlayState)).toBe('paused');
  expect(await page.evaluate(() => state.motion.playState)).toBe('paused');
  expect(await page.evaluate(() => state.enter(document.querySelector('#launch'), 'value'))).toBeNull();
  await page.evaluate(() => document.querySelector('.glyph').insertAdjacentHTML('beforeend', '<span class="glyph-orbit">Waiting</span>'));
  expect(await page.locator('.glyph-orbit').evaluate((el) => getComputedStyle(el).animationPlayState)).toBe('paused');
  await visibility(page, true);
  expect(await page.evaluate(() => state.motion.playState)).toBe('running');
  expect(await page.locator('.glyph-spin').evaluate((el) => getComputedStyle(el).animationPlayState)).toBe('running');
  expect(await page.locator('.send').evaluate((el) => getComputedStyle(el, '::after').animationPlayState)).toBe('running');
  await visibility(page, false);
  await page.evaluate(() => state.motion.cancel());
  await visibility(page, true);
  expect(await page.evaluate(() => state.motion.playState)).toBe('idle');
});
