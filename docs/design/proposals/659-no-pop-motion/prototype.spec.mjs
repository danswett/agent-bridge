import { test, expect } from '../../../../pwa/node_modules/@playwright/test/index.mjs';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

// The #659 prototype (prototype.html), in Chromium and WebKit: in Proposed nothing pops, a burst
// is one insertion, a list scrolled away stays where the reader is, and reduced motion is a
// cross-fade or nothing. Each sample freezes every animation at a moment, so it reads the frame
// that moment draws rather than racing the clock.
const here = path.dirname(fileURLToPath(import.meta.url));
const url = pathToFileURL(path.join(here, 'prototype.html')).href;

let errors;
test.beforeEach(async ({ page }) => {
  errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.emulateMedia({ reducedMotion: 'no-preference' });
  await page.goto(url);
  await page.evaluate(() => { window.proto.setMode('proposed'); window.proto.setReduced(false); });
});

test.afterEach(() => { expect(errors).toEqual([]); });

// Every animation on the page, paused at t ms from its start.
const freezeAt = (page, t) => page.evaluate((ms) => {
  for (const a of document.getAnimations()) { a.pause(); a.currentTime = ms; }
}, t);
// Clicks in the page and, a frame later (Tools flush on the next frame), freezes every
// animation at t ms, in one step: a slow engine can't let a short animation end in between.
const clickAndFreeze = (page, selector, t) => page.evaluate(([s, ms]) => new Promise((resolve) => {
  document.querySelector(s).click();
  requestAnimationFrame(() => { for (const a of document.getAnimations()) { a.pause(); a.currentTime = ms; } resolve(); });
}), [selector, t]);
const ty = (page, selector) => page.evaluate((s) => new DOMMatrix(getComputedStyle(document.querySelector(s)).transform).m42, selector);
const opacity = (page, selector) => page.evaluate((s) => Number(getComputedStyle(document.querySelector(s)).opacity), selector);
const tools = '#tools > .tool:not(.ghost)';
const firstToolId = (page) => page.locator(tools).first().getAttribute('data-id');
// Keeps every animation started from here on, since short ones can end before a slow engine looks.
const record = (page) => page.evaluate(() => {
  window.started = [];
  const animate = Element.prototype.animate;
  Element.prototype.animate = function (...args) { const a = animate.apply(this, args); window.started.push(a); return a; };
});

test('in Proposed a new Tools call fades in and the call below eases down; in Today both are there at once', async ({ page }) => {
  const was = await firstToolId(page);
  await clickAndFreeze(page, '#tool-one', 40);
  await expect(page.locator(tools)).toHaveCount(25);
  
  const fresh = await opacity(page, `${tools}:first-child`);
  expect(fresh).toBeGreaterThan(0);
  expect(fresh).toBeLessThan(1);
  expect(await ty(page, `#tools > [data-id="${was}"]`)).toBeLessThan(-1);

  await page.evaluate(() => { for (const a of document.getAnimations()) { a.finish(); } window.proto.setMode('today'); });
  const before = await firstToolId(page);
  await page.click('#tool-one');
  await expect(page.locator(tools)).toHaveCount(26);
  expect(await page.evaluate(() => document.getAnimations().length)).toBe(0);
  expect(await opacity(page, `${tools}:first-child`)).toBe(1);
  expect(await ty(page, `#tools > [data-id="${before}"]`)).toBe(0);
});

test('a burst of five calls is one insertion: the calls below move once, by five rows', async ({ page }) => {
  const was = await firstToolId(page);
  const shifts = await page.evaluate(() => window.proto.shiftCount);
  await record(page);
  await page.evaluate(() => window.proto.burst(5));
  await expect(page.locator(tools)).toHaveCount(29);
  expect(await page.evaluate(() => window.proto.shiftCount)).toBe(shifts + 1);
  const { moves, from, row } = await page.evaluate((id) => {
    const el = document.querySelector(`#tools > [data-id="${id}"]`);
    const list = window.started.filter((a) => a.effect.target === el);
    return { moves: list.length, from: new DOMMatrix(list[0].effect.getKeyframes()[0].transform).m42, row: el.offsetHeight };
  }, was);
  expect(moves).toBe(1);
  expect(Math.abs(from + 5 * row)).toBeLessThan(1);
});

test('a call arriving mid-move retargets it: the calls below keep moving down, never back or stopping short', async ({ page }) => {
  const was = await firstToolId(page);
  const { tops, row } = await page.evaluate((id) => new Promise((resolve) => {
    const el = document.querySelector(`#tools > [data-id="${id}"]`);
    const tops = [];
    const t0 = performance.now();
    window.proto.addTool();
    setTimeout(() => window.proto.addTool(), 70);
    const tick = () => {
      tops.push(el.getBoundingClientRect().top);
      if (performance.now() - t0 < 450) { requestAnimationFrame(tick); } else { resolve({ tops, row: el.offsetHeight }); }
    };
    requestAnimationFrame(tick);
  }), was);
  for (let i = 1; i < tops.length; i += 1) { expect(tops[i]).toBeGreaterThanOrEqual(tops[i - 1] - 0.5); }
  expect(Math.abs(tops.at(-1) - tops[0] - 2 * row)).toBeLessThan(1.5);
});

test('with Tools scrolled away from the top, a new call keeps what is read in place and the pill says 1 new', async ({ page }) => {
  await page.evaluate(() => { document.getElementById('tools').scrollTop = 200; });
  const read = await page.evaluate(() => {
    const list = document.getElementById('tools');
    const top = list.getBoundingClientRect().top;
    const el = [...list.querySelectorAll(':scope > .tool')].find((li) => li.getBoundingClientRect().top >= top);
    return { id: el.dataset.id, top: el.getBoundingClientRect().top };
  });
  await clickAndFreeze(page, '#tool-one', 40);
  await expect(page.locator(tools)).toHaveCount(25);
  
  const now = await page.evaluate((id) => document.querySelector(`#tools > [data-id="${id}"]`).getBoundingClientRect().top, read.id);
  expect(Math.abs(now - read.top)).toBeLessThan(1);
  await page.evaluate(() => { for (const a of document.getAnimations()) { a.finish(); } });
  await expect(page.locator('#pill')).toBeVisible();
  await expect(page.locator('#pill')).toHaveText(/1\s*new/);
  await page.click('#pill');
  await expect.poll(() => page.evaluate(() => document.getElementById('tools').scrollTop)).toBeLessThanOrEqual(2);
  await expect(page.locator('#pill')).toBeHidden();
});

test('with Tools full and scrolled to its end, new calls never move what is read', async ({ page }) => {
  await page.evaluate(() => window.proto.burst(30));
  await expect(page.locator(tools)).toHaveCount(50);
  await page.evaluate(() => { for (const a of document.getAnimations()) { a.finish(); } });
  const list = page.locator('#tools');
  await list.evaluate((l) => { l.scrollTop = l.scrollHeight; });
  const read = await list.evaluate((l) => {
    const bottom = l.getBoundingClientRect().bottom;
    const el = [...l.querySelectorAll(':scope > .tool')].filter((li) => li.getBoundingClientRect().bottom <= bottom).at(-1);
    return { id: el.dataset.id, top: el.getBoundingClientRect().top };
  });
  for (let i = 0; i < 3; i += 1) {
    await clickAndFreeze(page, '#tool-one', 40);
    const now = await page.evaluate((id) => document.querySelector(`#tools > [data-id="${id}"]`).getBoundingClientRect().top, read.id);
    expect(Math.abs(now - read.top)).toBeLessThan(1);
    await page.evaluate(() => { for (const a of document.getAnimations()) { a.finish(); } });
  }
  // Back at the top, the list keeps the newest 50 again.
  await page.click('#pill');
  await expect(page.locator(tools)).toHaveCount(50);
});

test('under reduced motion nothing travels: arrivals and values cross-fade over 83 ms, and the rest is at once', async ({ page }) => {
  await page.evaluate(() => window.proto.setReduced(true));
  await record(page);
  await page.click('#tool-one');
  await expect(page.locator(tools)).toHaveCount(25);
  await page.click('#row-add');
  await page.click('#row-end');
  await page.click('#values');
  await page.click('#banner-toggle');
  await page.click('#disc');
  const all = await page.evaluate(() => window.started.map((a) => ({
    moves: a.effect.getKeyframes().some((k) => k.transform && new DOMMatrix(k.transform).m42 !== 0),
    clips: a.effect.getKeyframes().some((k) => k.clipPath),
    ms: Number(a.effect.getTiming().duration),
  })));
  expect(all.length).toBeGreaterThan(0);
  for (const a of all) {
    expect(a.moves).toBe(false);
    expect(a.clips).toBe(false);
    expect(a.ms).toBeLessThanOrEqual(83);
  }
  // The reasoning's space opened at once: nothing moved below it.
  expect(await ty(page, '#ev-first')).toBe(0);
});

test('a session row leaving fades where it was as the row below eases up into its place', async ({ page }) => {
  const below = await page.locator('#rows > .row').nth(2).getAttribute('data-row');
  await clickAndFreeze(page, '#row-end', 40);
  
  const ghost = await opacity(page, '#rows > .ghost');
  expect(ghost).toBeGreaterThan(0);
  expect(ghost).toBeLessThan(1);
  expect(await page.locator('#rows > .ghost').getAttribute('aria-hidden')).toBe('true');
  expect(await ty(page, `#rows > [data-row="${below}"]`)).toBeGreaterThan(1);
});

test('Needs you arrives as one insertion with its first entry, and its count cross-fades as the next comes', async ({ page }) => {
  await clickAndFreeze(page, '#ask', 40);
  
  const head = await opacity(page, '#needs-head');
  expect(head).toBeGreaterThan(0);
  expect(head).toBeLessThan(1);
  expect(await ty(page, '#rows > .group:not(.needs)')).toBeLessThan(-1);
  // The approval fades where it lands: no travel of its own.
  const entryMoves = await page.evaluate(() => document.querySelector('#rows > .entry').getAnimations()
    .some((a) => a.effect.getKeyframes().some((k) => k.transform)));
  expect(entryMoves).toBe(false);
  await page.evaluate(() => { for (const a of document.getAnimations()) { a.finish(); } });
  await page.waitForTimeout(1050);
  const count = await page.evaluate(() => {
    document.getElementById('ask').click();
    const v = [...document.querySelectorAll('#needs-n > .v')];
    return { copies: v.length, now: document.querySelector('#needs-n > .v:not(.old)').textContent };
  });
  expect(count).toEqual({ copies: 2, now: '2' });
});

test('Show reasoning eases its space open and shut, with the lines below moving each way', async ({ page }) => {
  await clickAndFreeze(page, '#disc', 60);
  
  expect(await ty(page, '#ev-first')).toBeLessThan(-1);
  const content = await opacity(page, '#reasoning');
  expect(content).toBeGreaterThan(0);
  expect(content).toBeLessThan(1);
  await page.evaluate(() => { for (const a of document.getAnimations()) { a.finish(); } });

  await page.click('#disc');
  await page.waitForFunction(() => document.getElementById('reasoning').hidden);
  await freezeAt(page, 60);
  expect(await ty(page, '#ev-first')).toBeGreaterThan(1);
});

test('closing Show reasoning while it is still opening fades from where it is drawn, clip and all', async ({ page }) => {
  await clickAndFreeze(page, '#disc', 60);
  const mid = await page.evaluate(() => getComputedStyle(document.getElementById('reasoning')).clipPath);
  expect(mid).not.toBe('none');
  const after = await page.evaluate(() => {
    document.getElementById('disc').click();
    for (const a of document.getAnimations()) { a.pause(); a.currentTime = 0; }
    const cs = getComputedStyle(document.getElementById('reasoning'));
    return { clip: cs.clipPath, opacity: Number(cs.opacity) };
  });
  expect(after.clip).toBe(mid);
  expect(after.opacity).toBeGreaterThan(0);
  expect(after.opacity).toBeLessThan(1);
});

test('a Details value cross-fades in place, and a second change within a second is at once', async ({ page }) => {
  const cell = '#details .val[data-k="turns"]';
  const width = await page.locator('#details .val[data-k="status"]').evaluate((e) => e.getBoundingClientRect().width);
  await clickAndFreeze(page, '#values', 40);
  
  await expect(page.locator(`${cell} > .v`)).toHaveCount(2);
  const old = await opacity(page, `${cell} > .old`);
  expect(old).toBeGreaterThan(0);
  expect(old).toBeLessThan(1);
  // Mid-fade the cell holds the wider of the two, never less than it was.
  expect(await page.locator('#details .val[data-k="status"]').evaluate((e) => e.getBoundingClientRect().width)).toBeGreaterThanOrEqual(width - 0.5);
  await page.evaluate(() => { for (const a of document.getAnimations()) { a.play(); } });
  // Checked in the same task as the change, before any time passes: a cross-fade would show two
  // copies and a running fade here.
  const second = await page.evaluate((s) => {
    document.getElementById('values').click();
    const e = document.querySelector(s);
    return { copies: e.querySelectorAll('.v').length, text: e.textContent, fading: e.querySelector('.v').getAnimations().length };
  }, cell);
  expect(second).toEqual({ copies: 1, text: '14', fading: 0 });
});

test('a banner slides 12 in and pushes the page down with an ease, and leaves the same way', async ({ page }) => {
  await clickAndFreeze(page, '#banner-toggle', 60);
  
  const b = await opacity(page, '#banner');
  expect(b).toBeGreaterThan(0);
  expect(b).toBeLessThan(1);
  expect(await ty(page, '#banner')).toBeLessThan(0);
  expect(await ty(page, '#page-h')).toBeLessThan(-1);
  await page.evaluate(() => { for (const a of document.getAnimations()) { a.finish(); } });
  const was = await page.locator('#banner').boundingBox();
  await clickAndFreeze(page, '#banner-toggle', 0);
  // The leaving copy starts exactly where the banner was, its margin not added twice.
  const copy = await page.locator('#page > .ghost').boundingBox();
  expect(Math.abs(copy.x - was.x)).toBeLessThan(0.5);
  expect(Math.abs(copy.y - was.y)).toBeLessThan(0.5);
  await freezeAt(page, 40);
  
  expect(await ty(page, '#page-h')).toBeGreaterThan(1);
  const ghost = await opacity(page, '#page > .ghost');
  expect(ghost).toBeGreaterThan(0);
  expect(ghost).toBeLessThan(1);
});

test('nothing runs past 300 ms', async ({ page }) => {
  await record(page);
  for (const id of ['#tool-burst', '#row-add', '#row-end', '#ask', '#disc', '#values', '#banner-toggle', '#event-add']) { await page.click(id); }
  const longest = await page.evaluate(() => Math.max(...window.started.map((a) => Number(a.effect.getTiming().duration) + Number(a.effect.getTiming().delay || 0))));
  expect(longest).toBeLessThanOrEqual(300);
});
