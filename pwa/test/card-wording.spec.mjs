import { test, expect } from '@playwright/test';
import { open } from './nav.mjs';

const CLAUDE = '6f1c2b7e-claude-example';

async function workingCard(page) {
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  return open(page, CLAUDE);
}

async function change(page, activity, status = 'working') {
  await page.evaluate(({ activity, status }) => {
    const d = window.agentBridgeDemo, s = d.host.get('6f1c2b7e-claude-example');
    d.upsert({ ...s, status, activity: { ...s.activity, ...activity } });
  }, { activity, status });
}

test('the working status shows a stable verb immediately, even without a current tool', async ({ page }) => {
  const card = await workingCard(page);
  const status = card.locator('.status-text');
  const verb = (await status.innerText()).split(' · ')[0];
  expect(verb).toBe('Cogitating');
  await change(page, { currentTool: null });
  await expect(status).toHaveText(verb);
  await change(page, {}, 'idle');
  await expect(status).not.toHaveText(verb);
  await change(page, {});
  await expect(status).toHaveText(verb);
});

test('compacted once and compacted N times sit beside status, zero is absent, and Details stays unchanged', async ({ page }) => {
  const card = await workingCard(page);
  for (const [count, label] of [[1, 'compacted once'], [4, 'compacted 4 times'], [0, null]]) {
    await change(page, { compactions: count });
    if (label) { await expect(card.locator('.status-line')).toContainText(label); }
    else { await expect(card.locator('.status-line')).not.toContainText('compacted'); }
    if (count) {
      await expect(card.locator('.fact-compactions dd')).toHaveText(`${count} compaction${count === 1 ? '' : 's'}`);
    } else { await expect(card.locator('.fact-compactions')).toHaveCount(0); }
  }
  await expect(card.locator('.fact-status dd')).toHaveText('Working');
});

test('working spinners pause while the tab is hidden and resume with the same verb', async ({ page }) => {
  const card = await workingCard(page);
  const spinner = card.locator('.status-line .glyph-spin');
  const verb = await card.locator('.status-text').innerText();
  await expect(spinner).toHaveCSS('animation-play-state', 'running');
  await page.evaluate(() => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, value: 'hidden' });
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await expect(spinner).toHaveCSS('animation-play-state', 'paused');
  await page.evaluate(() => {
    delete document.visibilityState;
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await expect(spinner).toHaveCSS('animation-play-state', 'running');
  await expect(card.locator('.status-text')).toHaveText(verb);
});

test('reduced motion keeps the working verb visible and its spinner still', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  const card = await workingCard(page);
  await expect(card.locator('.status-text')).toHaveText('Cogitating');
  await expect(card.locator('.status-line .glyph-spin')).toHaveCSS('animation-name', 'none');
});
