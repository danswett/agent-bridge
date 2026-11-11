import { test, expect } from '@playwright/test';

const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CLAUDE = '6f1c2b7e-claude-example';
const CODEX = '019a1b2c-codex-example';
const row = (page, id) => page.locator(`.session[data-id="${id}"]`);
const header = (page) => page.getByRole('button', { name: /^Team, 2 sessions/ });

async function setup(page) {
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(async (ids) => {
    const { SessionOrder, orderKey } = await import('/js/session-order.js');
    const storage = {
      getItem: (k) => localStorage.getItem(`demo:${k}`),
      setItem: (k, v) => localStorage.setItem(`demo:${k}`, v),
    };
    const o = new SessionOrder({ storage });
    const id = o.createGroup('Team');
    o.setGroup(orderKey('demo', ids[0]), id);
    o.setGroup(orderKey('demo', ids[1]), id);
    o.setGroup(orderKey('demo', ids[2]), id);
    o.setPinned(orderKey('demo', ids[2]), true);
  }, [COPILOT, CLAUDE, CODEX]);
  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
}

test('a collapsed group keeps its needs-you row visible, counts it, and remembers collapse', async ({ page }) => {
  await setup(page);
  const toggle = header(page);
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  const top = await page.locator('#session-list > *').evaluateAll((els) => els.map((e) => e.textContent));
  expect(top.findIndex((t) => t === 'Pinned')).toBeLessThan(top.findIndex((t) => t.includes('Team')));
  await toggle.click();
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await expect(toggle).toHaveAccessibleName(/1 needs you/);
  await expect(row(page, COPILOT)).toBeVisible();
  await expect(row(page, CLAUDE)).toHaveCount(0);
  await expect(row(page, CODEX)).toBeVisible();
  await expect(page.locator('.needs-you-head')).toContainText('1');
  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  await expect(header(page)).toHaveAttribute('aria-expanded', 'false');
});

test('group disclosure keys toggle and search opens without changing the saved collapse', async ({ page }) => {
  await setup(page);
  const toggle = header(page);
  await toggle.focus();
  for (const [key, expanded] of [['ArrowLeft', 'false'], ['ArrowRight', 'true'], ['Enter', 'false'], ['Space', 'true']]) {
    await toggle.press(key);
    await expect(toggle).toHaveAttribute('aria-expanded', expanded);
    await expect(toggle).toBeFocused();
  }
  await toggle.press('ArrowLeft');
  await page.evaluate(() => window.agentBridgeDemo.page.view.setFilter('Refactor the'));
  const found = page.getByRole('button', { name: /^Team, 1 session/ });
  await expect(found).toHaveAttribute('aria-expanded', 'true');
  await found.evaluate((el) => el.click());
  await expect(found).toHaveAttribute('aria-expanded', 'true');
  await expect(row(page, CLAUDE)).toBeVisible();
  await page.evaluate(() => window.agentBridgeDemo.page.view.setFilter(''));
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await expect(row(page, CLAUDE)).toHaveCount(0);
});

test('Needs you opens the group temporarily and unread is marked only while collapsed', async ({ page }) => {
  await setup(page);
  const toggle = header(page);
  await toggle.click();
  await page.evaluate(() => window.agentBridgeDemo.page.view.setNeedsOnly(true));
  const needs = page.getByRole('button', { name: /^Team, 1 session/ });
  await expect(needs).toHaveAttribute('aria-expanded', 'true');
  await needs.press('ArrowLeft');
  await expect(needs).toHaveAttribute('aria-expanded', 'true');
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    d.page.view.setNeedsOnly(false);
    const s = d.host.get('02d9e5d5-6b35-49bd-b083-39572fbff648');
    d.upsert({ ...s, status: 'idle', question: null });
    const other = d.host.get('6f1c2b7e-claude-example');
    d.upsert({ ...other, updatedAt: new Date(Date.now() + 1000).toISOString(), activity: { ...other.activity, responseTurn: 'group-unread' } });
  });
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await expect(toggle.locator('.unread-dot')).toBeVisible();
  await toggle.click();
  await expect(toggle.locator('.unread-dot')).toBeHidden();
});

test('focus returns to the group when its visible needs-you row becomes hidden', async ({ page }) => {
  await setup(page);
  const toggle = header(page);
  await toggle.click();
  const waiting = row(page, COPILOT).locator('.row-main');
  await waiting.focus();
  await expect(waiting).toBeFocused();
  await page.evaluate((id) => {
    const d = window.agentBridgeDemo;
    d.upsert({ ...d.host.get(id), status: 'idle', question: null });
  }, COPILOT);
  await expect(row(page, COPILOT)).toHaveCount(0);
  await expect(toggle).toBeFocused();
});

test('arrows traverse group headers and visible rows in displayed order', async ({ page }) => {
  await setup(page);
  const toggle = header(page);
  const [firstId, secondId] = await toggle.evaluate((el) => [
    el.nextElementSibling.dataset.id, el.nextElementSibling.nextElementSibling.dataset.id,
  ]);
  const first = row(page, firstId).locator('.row-main');
  const second = row(page, secondId).locator('.row-main');
  const pinned = row(page, CODEX).locator('.row-main');
  await toggle.focus();
  await toggle.press('ArrowRight');
  await expect(first).toBeFocused();
  await first.press('ArrowLeft');
  await expect(toggle).toBeFocused();
  await toggle.press('ArrowDown');
  await expect(first).toBeFocused();
  await first.press('ArrowDown');
  await expect(second).toBeFocused();
  await second.press('ArrowUp');
  await expect(first).toBeFocused();
  await first.press('ArrowUp');
  await expect(toggle).toBeFocused();
  await toggle.press('ArrowUp');
  await expect(pinned).toBeFocused();
  await pinned.press('ArrowLeft');
  await expect(pinned).toBeFocused();
  await pinned.press('ArrowDown');
  await expect(toggle).toBeFocused();
  await toggle.press('ArrowLeft');
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await toggle.press('ArrowDown');
  const waiting = row(page, COPILOT).locator('.row-main');
  await expect(waiting).toBeFocused();
  await waiting.press('ArrowLeft');
  await expect(toggle).toBeFocused();
  await toggle.press('ArrowRight');
  await expect(toggle).toHaveAttribute('aria-expanded', 'true');
  await expect(toggle).toBeFocused();
  await toggle.press('ArrowRight');
  await expect(first).toBeFocused();
});
