import { test, expect } from '@playwright/test';
import { row, view } from './nav.mjs';

const A = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const B = '6f1c2b7e-claude-example';
const C = '019a1b2c-codex-example';
const D = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const order = (page) => page.locator('.session').evaluateAll((els) => els.map((e) => e.dataset.id));
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
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await page.locator('#session-sort').selectOption('recent');
  await page.mouse.move(width - 1, 0);
  await page.evaluate(() => document.activeElement?.blur());
  await page.clock.install();
  await page.clock.pauseAt(await page.evaluate(() => Date.now()) + 1000);
}

for (const width of [390, 1440]) {
  test(`Most recent batches busy rows and releases overlapping holds at ${width}`, async ({ page }) => {
    await setup(page, width);
    await update(page, C);
    await expect.poll(async () => (await order(page))[0]).toBe(C);
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
  });

  test(`typing holds activity but a question and new session bypass alone at ${width}`, async ({ page }) => {
    await setup(page, width);
    await page.evaluate((id) => window.agentBridgeDemo.page.showSession(id), B);
    const input = view(page, B).locator('.reply-input');
    await input.fill('Keep this draft');
    await page.mouse.move(width - 1, 0);
    const shown = await order(page);
    await update(page, D);
    await page.clock.runFor(6000);
    expect(await order(page)).toEqual(shown);
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
    await row(page, A).dispatchEvent('pointerdown', { pointerType: 'touch', pointerId: 7 });
    await update(page, C);
    await page.clock.runFor(6000);
    expect(await order(page)).toEqual(shown);
    await page.locator('#list-pane').dispatchEvent('scroll');
    await row(page, A).dispatchEvent('pointercancel', { pointerType: 'touch', pointerId: 7 });
    expect(await order(page)).toEqual(shown);
    await page.clock.runFor(200);
    await expect.poll(async () => (await order(page))[0]).toBe(C);
    expect(await row(page, C).evaluate((el) => el.getAnimations().filter((a) => /ab-motion-(reorder|shift)/.test(a.id)).length)).toBe(0);
  });
}
