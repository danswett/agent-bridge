import { test, expect } from '@playwright/test';
import { row, view, selectSort } from './nav.mjs';

const A = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const B = '6f1c2b7e-claude-example';
const C = '019a1b2c-codex-example';
const D = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const order = (page) => page.locator('.session').evaluateAll((els) => els.map((e) => e.dataset.id));
const approval = () => ({
  id: 'recent-approval', kind: 'approval', askedAt: new Date().toISOString(),
  message: 'Allow **shell**?\n\n```\npwd\n```',
  fields: [{ name: 'decision', type: 'choice', required: true,
    options: [{ value: 'approve', label: 'Approve once' }, { value: 'deny', label: 'Deny' }] }],
});
async function update(page, id, question = null) {
  await page.evaluate(({ id, question }) => {
    const d = window.agentBridgeDemo;
    const at = new Date(Date.now() + 60000).toISOString();
    d.upsert({ ...d.host.get(id), status: question ? 'waiting' : 'idle', statusSince: at, updatedAt: at,
      question, activity: { ...d.host.get(id)?.activity, userMessage: at, userMessageAt: at } });
  }, { id, question });
  await page.clock.runFor(1);
}
async function setup(page, width) {
  await page.setViewportSize({ width, height: 900 });
  await page.clock.install();
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await selectSort(page, 'recent');
  await page.mouse.move(width - 1, 0);
  await page.evaluate(() => document.activeElement?.blur());
  await page.clock.pauseAt(await page.evaluate(() => Date.now()) + 1000);
}

async function firstBatch(page, id) {
  await update(page, id);
  // A native layout scroll can acquire a timed hold after setup pauses the clock.
  // Pump animation frames until the batch is drawn, not for a guessed total delay.
  await expect.poll(async () => {
    await page.clock.runFor(16);
    return (await order(page))[0];
  }, { intervals: [0] }).toBe(id);
}

for (const width of [390, 1440]) {
  test(`initial scrolling settles before the first Most recent batch at ${width}`, async ({ page }) => {
    await setup(page, width);
    await page.locator('#list-pane').dispatchEvent('scroll');
    await firstBatch(page, C);
  });

  test(`Most recent batches busy rows and releases overlapping holds at ${width}`, async ({ page }) => {
    await setup(page, width);
    await firstBatch(page, C);
    const shown = await order(page);
    await page.clock.runFor(1000);
    await update(page, B);
    expect(await order(page)).toEqual(shown);
    await row(page, A).hover();
    await row(page, A).locator('.row-main').focus();
    await page.clock.runFor(5000);
    expect(await order(page)).toEqual(shown);
    await page.mouse.move(width - 1, 0);
    expect(await order(page)).toEqual(shown);
    await expect(row(page, A).locator('.row-main')).toBeFocused();
    await page.evaluate(() => document.activeElement.blur());
    await expect.poll(async () => (await order(page))[0]).toBe(B);
    await page.locator('#needs-you button').first().focus();
    await update(page, C);
    await page.clock.runFor(5000);
    expect((await order(page))[0]).toBe(B);
    await page.evaluate(() => document.activeElement.blur());
    await expect.poll(async () => (await order(page))[0]).toBe(C);
  });

  test(`real Tab between rows never releases a pending batch at ${width}`, async ({ page }) => {
    await setup(page, width);
    await row(page, A).locator('.row-main').focus();
    const shown = await order(page);
    await update(page, B);
    await page.clock.runFor(6000);
    await page.keyboard.press('Tab');
    expect(await order(page)).toEqual(shown);
    await expect(page.locator('#session-list .row-main:focus')).toHaveCount(1);
    await page.keyboard.press('Shift+Tab');
    expect(await order(page)).toEqual(shown);
    await expect(row(page, A).locator('.row-main')).toBeFocused();
  });

  test(`typing holds activity but a question and new session bypass alone at ${width}`, async ({ page }) => {
    await setup(page, width);
    await page.evaluate((id) => window.agentBridgeDemo.page.showSession(id), D);
    const input = view(page, D).locator('.reply-input');
    await input.fill('Keep this draft');
    await page.mouse.move(width - 1, 0);
    const shown = await order(page);
    await update(page, B);
    await page.clock.runFor(6000);
    expect(await order(page)).toEqual(shown);
    await page.keyboard.press('Tab');
    expect(await order(page)).toEqual(shown);
    await page.keyboard.press('Shift+Tab');
    await update(page, C, { id: 'recent-question', kind: 'form', askedAt: new Date().toISOString(), fields: [], prompt: 'Continue?' });
    await expect.poll(() => order(page)).toEqual([C, ...shown.filter((id) => id !== C)]);
    await page.evaluate(() => {
      const d = window.agentBridgeDemo;
      d.upsert({ ...d.host.get('019a1b2c-codex-example'), id: 'fresh', question: null });
    });
    await page.clock.runFor(1);
    await expect.poll(() => order(page)).toEqual(['fresh', C, ...shown.filter((id) => id !== C)]);
    await expect(input).toBeFocused();
    await expect(input).toHaveValue('Keep this draft');
  });

  test(`touch and scrolling hold the batch; reduced motion is instant at ${width}`, async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await setup(page, width);
    const shown = await order(page);
    await row(page, A).evaluate((el) => el.dispatchEvent(new Event('touchstart', { bubbles: true })));
    await row(page, A).dispatchEvent('pointerdown', { pointerType: 'touch', pointerId: 7 });
    await update(page, C);
    await page.clock.runFor(6000);
    expect(await order(page)).toEqual(shown);
    await page.locator('#list-pane').dispatchEvent('scroll');
    await row(page, A).dispatchEvent('pointercancel', { pointerType: 'touch', pointerId: 7 });
    expect(await order(page)).toEqual(shown);
    await page.clock.runFor(200);
    expect(await order(page)).toEqual(shown);
    await row(page, A).evaluate((el) => el.dispatchEvent(new Event('touchend', { bubbles: true })));
    await expect.poll(async () => (await order(page))[0]).toBe(C);
    expect(await row(page, C).evaluate((el) => el.getAnimations().filter((a) => /ab-motion-(reorder|shift)/.test(a.id)).length)).toBe(0);
  });

  test(`ending a touch on a removed row releases the batch at ${width}`, async ({ page }) => {
    await setup(page, width);
    const shown = await order(page);
    const promoted = shown.filter((id) => id !== A).at(-1);
    const touched = await row(page, A).elementHandle();
    await touched.evaluate((el) => el.dispatchEvent(new Event('touchstart', { bubbles: true })));
    await update(page, promoted);
    await page.evaluate((id) => window.agentBridgeDemo.remove(id), A);
    await page.clock.runFor(6000);
    await expect(row(page, A)).toHaveCount(0);
    expect(await touched.evaluate((el) => el.isConnected)).toBe(false);
    expect(await order(page)).toEqual(shown.filter((id) => id !== A));
    await touched.evaluate((el) => el.dispatchEvent(new Event('touchend', { bubbles: true })));
    await expect.poll(async () => (await order(page))[0]).toBe(promoted);
    await touched.dispose();
  });

  test(`attention bypass preserves row focus and the approval stability wait at ${width}`, async ({ page }) => {
    await setup(page, width);
    await row(page, A).locator('.row-main').focus();
    const shown = await order(page);
    await update(page, B);
    await update(page, D, approval());
    await expect.poll(() => order(page)).toEqual([D, ...shown.filter((id) => id !== D)]);
    await expect(row(page, A).locator('.row-main')).toBeFocused();
    await page.clock.runFor(400);
    const answers = () => page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
    const count = await answers();
    const lane = page.locator('#needs-you .needs-you-list');
    await expect(lane).toHaveAttribute('data-arriving', '');
    await page.locator(`#needs-you .needs-entry[data-id="${D}"]`).evaluate((el) => {
      for (const button of el.querySelectorAll('.needs-approve, .needs-deny')) {
        button.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
      }

    });
    await page.clock.runFor(1);
    expect(await answers()).toBe(count);
    await page.clock.runFor(600);
    await expect(lane).not.toHaveAttribute('data-arriving', '');
  });
}

test('a timed anchoring scroll protects unchanged visible approval controls for 500 ms', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await setup(page, 1440);
  await page.evaluate((id) => {
    const d = window.agentBridgeDemo;
    for (let i = 0; i < 12; i++) { d.upsert({ ...d.host.get(id), id: `guard-${i}` }); }
  }, D);
  await page.clock.runFor(6000);
  await update(page, D, approval());
  await page.clock.runFor(6000);
  await update(page, C);
  await page.clock.runFor(1000);
  const pane = page.locator('#list-pane');
  await pane.evaluate((el) => {
    el.style.overflowAnchor = 'none';
    const top = el.querySelector('.session').getBoundingClientRect().top - el.getBoundingClientRect().top;
    el.scrollTop = Math.max(40, top - el.clientHeight * 0.6);
  });
  await pane.dispatchEvent('scroll');
  await page.clock.runFor(200);
  const button = page.locator(`#needs-you .needs-entry[data-id="${D}"] .needs-approve`);
  const before = await button.evaluate((el) => {
    const pane = document.querySelector('#list-pane'), rect = el.getBoundingClientRect(), bounds = pane.getBoundingClientRect();
    return { top: rect.top, scroll: pane.scrollTop, visible: rect.top >= bounds.top && rect.bottom <= bounds.bottom };
  });
  expect(before.scroll).toBeGreaterThan(0);
  expect(before.visible).toBe(true);
  await expect(page.locator('#needs-you .needs-you-list')).not.toHaveAttribute('data-arriving', '');
  const count = await page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
  await update(page, B);
  expect((await order(page))[0]).toBe(C);
  await page.clock.runFor(3800);
  expect((await order(page))[0]).toBe(B);
  const after = await button.evaluate((el) => {
    const top = el.getBoundingClientRect().top;
    el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
    return { top, guarded: el.closest('.needs-you-list').hasAttribute('data-arriving') };
  });
  expect(Math.abs(after.top - before.top)).toBeGreaterThan(1);
  expect(after.guarded).toBe(true);
  await page.clock.runFor(1);
  expect(await page.evaluate(() => window.agentBridgeDemo.host.answerLog.length)).toBe(count);
  await page.clock.runFor(501);
  await expect(page.locator('#needs-you .needs-you-list')).not.toHaveAttribute('data-arriving', '');
});

async function rowMotionsDone(page) {
  // The fake clock does not advance native Web Animations.
  await expect.poll(() => page.locator('.session').evaluateAll((els) =>
    els.every((el) => el.getAnimations().length === 0))).toBe(true);
}

async function normalMotionBatch(page, initialBatch = false) {
  await setup(page, 1440);
  if (initialBatch) {
    await page.evaluate(() => {
      const animate = Element.prototype.animate;
      Element.prototype.animate = function (...args) {
        const a = animate.apply(this, args);
        if (this.matches('.session')) { a.playbackRate = 0.1; }
        return a;
      };
    });
  }
  await page.evaluate((id) => {
    const d = window.agentBridgeDemo;
    for (let i = 0; i < 12; i++) { d.upsert({ ...d.host.get(id), id: `batch-${String(i).padStart(2, '0')}` }); }
  }, D);
  await page.clock.runFor(6000);
  if (initialBatch) { await firstBatch(page, C); }
  const pane = page.locator('#list-pane');
  await pane.evaluate((el) => { el.scrollTop = 300; });
  await pane.dispatchEvent('scroll');
  await page.clock.runFor(200);
  await rowMotionsDone(page);
  const anchor = await pane.evaluate((el) => {
    const row = [...el.querySelectorAll('.session')].find((r) => r.getBoundingClientRect().bottom > el.getBoundingClientRect().top);
    window.__recentMoves = [];
    const animate = Element.prototype.animate;
    Element.prototype.animate = function (...args) { const a = animate.apply(this, args); window.__recentMoves.push(a); return a; };
    return { id: row.dataset.id, top: row.getBoundingClientRect().top, scroll: el.scrollTop };
  });
  expect(anchor.scroll).toBeGreaterThan(0);
  if (initialBatch) {
    await page.evaluate(() => {
      for (const a of document.getAnimations()) {
        if (/^ab-motion-(reorder|shift)$/.test(a.id)) { a.finish(); }
      }
    });
  }
  await update(page, 'batch-11');
  await page.clock.runFor(5000);
  await rowMotionsDone(page);
  expect(Math.abs(await row(page, anchor.id).evaluate((el) => el.getBoundingClientRect().top) - anchor.top)).toBeLessThan(2);
  expect(await page.evaluate(() => window.__recentMoves.some((a) => a.id === 'ab-motion-reorder'))).toBe(true);
}

test('a normal-motion batch glides and anchors the row being read', async ({ page }) => {
  await normalMotionBatch(page);
});

test('a normal-motion batch anchors the row after an initial glide finishes', async ({ page }) => {
  await normalMotionBatch(page, true);
});
