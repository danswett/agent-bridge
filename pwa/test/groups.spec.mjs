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

for (const width of [1440, 390]) {
  test.describe(`group row menu at ${width}px`, () => {
    test.use({ viewport: { width, height: 900 } });
    const menuAction = async (page, id, name) => {
      await row(page, id).locator('.row-main').press('Shift+F10');
      await page.getByRole('menuitem', { name, exact: true }).click();
    };
    const chooseGroup = async (page, id) => menuAction(page, id, 'Move to group');
    const reload = async (page) => {
      await page.reload();
      await page.waitForSelector('html[data-demo-ready]');
    };
    const groupRows = (page, name) => page.locator('#session-list > *').evaluateAll((els, name) => {
      const start = els.findIndex((e) => e.matches('.session-group-toggle') && e.querySelector('.group-name').textContent === name);
      const ids = [];
      for (let i = start + 1; start >= 0 && i < els.length && els[i].matches('.session'); i++) { ids.push(els[i].dataset.id); }
      return ids;
    }, name);

    test('Move to group checks membership, moves to the top, survives reload and focuses a collapsed destination', async ({ page }) => {
      await setup(page);
      await page.evaluate((id) => {
        const o = window.agentBridgeDemo.page.view.order;
        const group = o.createGroup('Personal');
        o.setGroup(`demo\n${id}`, group);
        window.agentBridgeDemo.page.view.render();
      }, COPILOT);
      await chooseGroup(page, CLAUDE);
      await expect(page.getByRole('menuitemradio', { name: 'Team', exact: true })).toBeChecked();
      if (width === 390) { await expect(page.getByRole('dialog', { name: 'Move to group', exact: true })).toBeVisible(); }
      await page.getByRole('menuitemradio', { name: 'Personal', exact: true }).click();
      await expect.poll(() => groupRows(page, 'Personal')).toEqual([CLAUDE, COPILOT]);
      await reload(page);
      await expect.poll(() => groupRows(page, 'Personal')).toEqual([CLAUDE, COPILOT]);
      await page.getByRole('button', { name: /^Team, 0 sessions/ }).click();
      await chooseGroup(page, CLAUDE);
      await page.evaluate(() => window.scrollTo(0, 0));
      await page.evaluate(() => {
        window.groupMotions = [];
        const animate = Element.prototype.animate;
        Element.prototype.animate = function (...args) { const a = animate.apply(this, args); window.groupMotions.push(a); return a; };
      });
      await page.getByRole('menuitemradio', { name: 'Team', exact: true }).click();
      await expect(row(page, CLAUDE)).toHaveCount(0);
      await expect(page.getByRole('button', { name: /^Team, 1 session/ })).toBeFocused();
      expect(await page.evaluate(() => window.groupMotions.some((a) => a.id === 'ab-motion-listItem'))).toBe(true);
      expect(await page.evaluate((id) => window.groupMotions.some((a) => a.id === 'ab-motion-shift' && a.effect.target.dataset.id === id), COPILOT)).toBe(true);
    });

    test('New group validates names inline, creates expanded at the foot and retains the row after reload', async ({ page }) => {
      await setup(page);
      await chooseGroup(page, CLAUDE);
      await page.getByRole('menuitem', { name: 'New group\u2026', exact: true }).click();
      const name = page.getByRole('textbox', { name: 'Group name', exact: true });
      await expect(name).toBeFocused();
      await name.fill(' team ');
      await name.press('Enter');
      await expect(page.locator('.group-editor').getByRole('alert')).toHaveText('A group with that name already exists.');
      await expect(name).toHaveAttribute('aria-invalid', 'true');
      await name.fill(' Personal ');
      await name.press('Enter');
      await expect(page.locator('.group-name')).toHaveText(['Team', 'Personal']);
      await expect(page.getByRole('button', { name: /^Personal, 1 session/ })).toHaveAttribute('aria-expanded', 'true');
      await expect(row(page, CLAUDE).locator('.row-main')).toBeFocused();
      await reload(page);
      await expect.poll(() => groupRows(page, 'Personal')).toEqual([CLAUDE]);
    });

    test('Pin and Unpin keep membership and Remove from group returns to the top of Sessions after reload', async ({ page }) => {
      await setup(page);
      await menuAction(page, CLAUDE, 'Pin');
      await reload(page);
      await expect(row(page, CLAUDE)).toHaveAttribute('data-pinned', 'true');
      await chooseGroup(page, CLAUDE);
      await expect(page.getByRole('menuitemradio', { name: 'Team', exact: true })).toBeChecked();
      await page.keyboard.press('Escape');
      await menuAction(page, CLAUDE, 'Unpin');
      await expect.poll(() => groupRows(page, 'Team')).toEqual([CLAUDE, COPILOT]);
      await menuAction(page, CLAUDE, 'Remove from group');
      await expect(page.locator('.list-group[data-group="sessions"] + .session')).toHaveAttribute('data-id', CLAUDE);
      await reload(page);
      await expect(page.locator('.list-group[data-group="sessions"] + .session')).toHaveAttribute('data-id', CLAUDE);
      await row(page, CLAUDE).locator('.row-main').press('Shift+F10');
      await expect(page.getByRole('menuitem', { name: 'Remove from group', exact: true })).toHaveCount(0);
    });

    test('a chooser closes and returns focus to the group when its source becomes hidden or ends', async ({ page }) => {
      await setup(page);
      await header(page).click();
      await chooseGroup(page, COPILOT);
      await page.evaluate((id) => {
        const d = window.agentBridgeDemo;
        d.upsert({ ...d.host.get(id), status: 'idle', question: null });
      }, COPILOT);
      await expect(page.getByRole('menu')).toHaveCount(0);
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(header(page)).toBeFocused();
      await header(page).click();
      await chooseGroup(page, CLAUDE);
      await page.evaluate((id) => window.agentBridgeDemo.remove(id), CLAUDE);
      await expect(page.getByRole('menu')).toHaveCount(0);
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await expect(page.getByRole('button', { name: /^Team, 1 session/ })).toBeFocused();
    });

    test('account cleanup dismisses the group chooser and new-group editor', async ({ page }) => {
      await setup(page);
      await chooseGroup(page, CLAUDE);
      await page.evaluate(() => window.agentBridgeDemo.page.clear());
      await expect(page.getByRole('menu')).toHaveCount(0);
      await expect(page.getByRole('dialog')).toHaveCount(0);
      await reload(page);
      await chooseGroup(page, CLAUDE);
      await page.getByRole('menuitem', { name: 'New group\u2026', exact: true }).click();
      await page.evaluate(() => window.agentBridgeDemo.page.clear());
      await expect(page.getByRole('textbox', { name: 'Group name', exact: true })).toHaveCount(0);
      await expect(page.getByRole('dialog')).toHaveCount(0);
    });
  });
}
