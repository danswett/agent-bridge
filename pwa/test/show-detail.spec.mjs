import { test, expect } from '@playwright/test';
import { go, open, view } from './nav.mjs';

const SESSION = 'demo-delegates';
const toggle = (page) => page.getByRole('switch', { name: 'Show reasoning and tool history', exact: true });
test.use({ viewport: { width: 1440, height: 900 } });

async function demo(page) {
  await page.goto('?demo=1&static=1&delegates=1');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate((id) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    const tool = { id: 'tool-detail', name: 'Read', status: 'done', summary: 'Read the fixture' };
    d.host.historyTurns.set(id, [{
      id: 'earlier-detail', kind: 'interactive', at: '2026-11-17T20:00:00Z',
      prompt: { text: 'Earlier message', from: 'person' },
      parts: [{ id: 'r', kind: 'reasoning', text: 'Earlier reasoning' }, { id: 't', kind: 'tool', tool }, { id: 'a', kind: 'response', text: 'Earlier response' }],
    }]);
    d.upsert({ ...s, activity: { ...s.activity, currentTool: tool, history: [tool] } });
  }, SESSION);
  await open(page, SESSION);
}

test('the device switch updates already open session, history and delegate views and survives reload', async ({ page }) => {
  await demo(page);
  const c = view(page, SESSION);
  await expect(c.locator('.agent-block > .reasoning')).toBeVisible();
  await expect(c.locator('.tool-line')).toBeVisible();
  await expect(c.locator('.history-reasoning')).toHaveCount(1);
  await expect(c.locator('.history-tool')).toHaveCount(1);
  await c.locator('.strip-item[data-id="demo-agent-suite"]').click();
  const pane = c.locator('.delegate-pane');
  await expect(pane.locator('.reasoning')).toBeVisible();
  await expect(pane.locator('.delegate-tools')).toBeVisible();
  const response = await pane.locator('.delegate-latest').textContent();
  await go(page, 'settings');
  await expect(toggle(page)).toBeChecked();
  await expect(toggle(page)).toBeVisible();
  await toggle(page).uncheck();
  // Inspect while Settings is still open: no route change is needed to apply the choice.
  await expect(c.locator('.reasoning, .tool-line, .tools, .history-reasoning, .history-tool, .delegate-tools')).toHaveCount(0);
  await expect(pane.locator('.delegate-latest')).toHaveText(response);
  await expect(c.locator('.history-turn')).toContainText('Earlier response');
  await expect(c.locator('.response')).toContainText('Started 2 background agents');
  await toggle(page).check();
  await expect(c.locator('.agent-block > .reasoning')).toHaveCount(1);
  await expect(c.locator('.history-reasoning')).toHaveCount(1);
  await expect(c.locator('.history-tool')).toHaveCount(1);
  await expect(pane.locator('.reasoning')).toHaveCount(1);
  await expect(pane.locator('.delegate-tools .tool')).not.toHaveCount(0);
  await toggle(page).uncheck();
  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  await go(page, 'settings');
  await expect(toggle(page)).not.toBeChecked();
  await open(page, SESSION);
  await expect(view(page, SESSION).locator('.reasoning, .tool-line, .tools')).toHaveCount(0);
});

test('a choice still applies for this page when storage cannot keep it', async ({ page }) => {
  await page.addInitScript(() => {
    const get = Storage.prototype.getItem;
    const set = Storage.prototype.setItem;
    Storage.prototype.getItem = function (key) { if (key === 'agent-bridge.app.showDetail') { throw new Error('blocked'); } return get.call(this, key); };
    Storage.prototype.setItem = function (key, value) { if (key === 'agent-bridge.app.showDetail') { throw new Error('full'); } return set.call(this, key, value); };
  });
  await demo(page);
  await go(page, 'settings');
  await expect(toggle(page)).toBeChecked();
  await toggle(page).uncheck();
  await expect(page.locator('#show-detail-status')).toContainText('not be kept');
  await expect(view(page, SESSION).locator('.reasoning, .tool-line')).toHaveCount(0);
  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  await go(page, 'settings');
  await expect(toggle(page)).toBeChecked();
});
