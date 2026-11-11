import { test, expect } from '@playwright/test';

const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CLAUDE = '6f1c2b7e-claude-example';
const CODEX = '019a1b2c-codex-example';
const row = (page, id) => page.locator(`#session-list > .session[data-id="${id}"]`);
const control = (page) => page.getByRole('button', { name: /^Needs you \u00b7 \d+$/ });
const group = (page) => page.getByRole('button', { name: /^Team, \d+ sessions?/ });
const savedCollapse = (page) => page.evaluate(() => JSON.parse(localStorage.getItem('demo:agent-bridge.app.order')).collapsedGroups);

async function open(page) {
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
}

async function ask(page, id) {
  await page.evaluate(({ id, source }) => {
    const d = window.agentBridgeDemo;
    d.upsert({ ...d.host.get(id), status: 'waiting', question: { ...d.host.get(source).question, id: `filter-${id}` } });
  }, { id, source: COPILOT });
}

for (const width of [390, 1440]) {
  test.describe(`Needs you filter at ${width}px`, () => {
    test.use({ viewport: { width, height: 900 } });

    test('the visible toggle opens a collapsed group, counts its pinned member once and restores collapse after reload', async ({ page }) => {
      await open(page);
      await ask(page, CODEX);
      await page.evaluate((ids) => {
        const o = window.agentBridgeDemo.page.order;
        const team = o.createGroup('Team');
        for (const id of ids) { o.setGroup(`demo\n${id}`, team); }
        o.setPinned(`demo\n${ids[2]}`, true);
        window.agentBridgeDemo.page.view.render();
      }, [COPILOT, CLAUDE, CODEX]);
      await group(page).click();
      const saved = await savedCollapse(page);
      await expect(control(page)).toHaveAccessibleName('Needs you \u00b7 2');
      await expect(control(page)).toHaveAttribute('aria-controls', 'session-list');
      await expect(control(page)).toHaveAttribute('aria-pressed', 'false');
      const box = await control(page).evaluate((el) => ({
        right: el.getBoundingClientRect().right, height: el.getBoundingClientRect().height,
        paneRight: el.closest('#list-pane').getBoundingClientRect().right,
      }));
      expect(box.right).toBeLessThanOrEqual(box.paneRight);
      expect(box.height).toBeGreaterThanOrEqual(width < 760 ? 44 : 36);
      await expect(page.locator('#needs-you')).toBeVisible();
      await control(page).click();
      await expect(control(page)).toHaveAttribute('aria-pressed', 'true');
      await expect(page.locator('#needs-you')).toBeHidden();
      await expect(group(page)).toHaveAttribute('aria-expanded', 'true');
      await expect(row(page, COPILOT)).toBeVisible();
      await expect(row(page, CODEX)).toBeVisible();
      await expect(row(page, CLAUDE)).toHaveCount(0);
      await expect(page.locator('.needs-filter-summary')).toHaveText('Showing 2 of 4');
      await group(page).press('ArrowLeft');
      await expect(group(page)).toHaveAttribute('aria-expanded', 'true');
      expect(await savedCollapse(page)).toEqual(saved);
      await control(page).click();
      await expect(page.locator('#needs-you')).toBeVisible();
      await expect(group(page)).toHaveAttribute('aria-expanded', 'false');
      expect(await savedCollapse(page)).toEqual(saved);
      await page.reload();
      await page.waitForSelector('html[data-demo-ready]');
      await expect(control(page)).toHaveAttribute('aria-pressed', 'false');
      await expect(group(page)).toHaveAttribute('aria-expanded', 'false');
      expect(await savedCollapse(page)).toEqual(saved);
    });

    test('keyboard activation retains answered rows at zero and returns focus safely when the toggle disappears', async ({ page }) => {
      await open(page);
      await control(page).focus();
      await control(page).press('Enter');
      await expect(control(page)).toHaveAttribute('aria-pressed', 'true');
      await page.evaluate(({ waiting, signIn }) => {
        const d = window.agentBridgeDemo;
        d.upsert({ ...d.host.get(waiting), status: 'idle', question: null });
        d.upsert({ ...d.host.get(signIn), status: 'idle', question: null, attention: { kind: 'signInNeeded', message: 'Sign in needed' } });
      }, { waiting: COPILOT, signIn: CLAUDE });
      await expect(control(page)).toHaveAccessibleName('Needs you \u00b7 0');
      await expect(row(page, COPILOT)).toBeVisible();
      await expect(row(page, CLAUDE)).toHaveCount(0);
      await expect(page.getByText('Nothing else needs you on connected machines.', { exact: true })).toBeVisible();
      await expect(control(page)).toBeFocused();
      await control(page).press('Space');
      await expect(control(page)).toHaveCount(0);
      await expect(page.locator('#sessions-h')).toBeFocused();
      await expect(row(page, CLAUDE)).toBeVisible();
    });

    test('search intersects the filter without changing its count and questions arriving join in their own place', async ({ page }) => {
      await open(page);
      await control(page).click();
      await page.evaluate(() => window.agentBridgeDemo.page.view.setFilter('Refactor the'));
      await expect(control(page)).toHaveAccessibleName('Needs you \u00b7 1');
      await expect(row(page, COPILOT)).toHaveCount(0);
      await expect(row(page, CLAUDE)).toHaveCount(0);
      await expect(page.locator('#sessions-empty')).toHaveText('No sessions match your search.');
      await ask(page, CLAUDE);
      await expect(control(page)).toHaveAccessibleName('Needs you \u00b7 2');
      await expect(row(page, CLAUDE)).toBeVisible();
      await page.evaluate(() => window.agentBridgeDemo.page.view.setFilter(''));
      await expect(row(page, COPILOT)).toBeVisible();
      await expect(row(page, CLAUDE)).toBeVisible();
      await control(page).press('Space');
      await expect(control(page)).toHaveAttribute('aria-pressed', 'false');
      await expect(page.locator('#session-list > .session')).toHaveCount(4);
    });

    test('an offline question leaves the connected count and active rows, even at zero', async ({ page }) => {
      await page.goto('?demo=1&static=1&machines=3');
      await page.waitForSelector('html[data-demo-ready]');
      await expect(control(page)).toHaveAccessibleName('Needs you \u00b7 3');
      await control(page).click();
      await page.evaluate(() => {
        const d = window.agentBridgeDemo;
        d.setOnline(d.host.machine.id, false);
      });
      await expect(control(page)).toHaveAccessibleName('Needs you \u00b7 2');
      await expect(row(page, COPILOT)).toHaveCount(0);
      expect(await page.evaluate((id) => !!window.agentBridgeDemo.host.get(id).question, COPILOT)).toBe(true);
      await expect(page.getByText('Nothing else needs you on connected machines.', { exact: true })).toBeHidden();
      await page.evaluate(() => {
        const d = window.agentBridgeDemo;
        for (const { hostId, session } of d.page.hosts.sessions.list()) {
          if (hostId !== d.host.machine.id && session.question) {
            d.machines[hostId].upsert({ ...session, status: 'idle', question: null });
          }
        }
      });
      await expect(control(page)).toHaveAccessibleName('Needs you \u00b7 0');
      await expect(control(page)).toHaveAttribute('title', 'Pending questions on connected machines');
      await expect(row(page, COPILOT)).toHaveCount(0);
      await expect(page.getByText('Nothing else needs you on connected machines.', { exact: true })).toBeVisible();
    });
  });
}
