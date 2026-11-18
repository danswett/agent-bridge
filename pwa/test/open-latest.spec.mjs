import { test, expect } from '@playwright/test';
import { go, open, motionsDone } from './nav.mjs';
import { loadEarlierTo } from './history-fixture.mjs';

const ID = 'open-latest-long';
const TURNS = 80;
const reply = (turn) => Array.from({ length: 30 }, (_, i) => `Reply ${turn}, paragraph ${i + 1}.`).join('\n\n');
const gap = (pane) => pane.evaluate((el) => el.scrollHeight - el.clientHeight - el.scrollTop);

test.use({
  viewport: { width: 390, height: 844 },
  deviceScaleFactor: 1,
  isMobile: true,
  hasTouch: true,
  reducedMotion: 'reduce',
  userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
});

async function arrive(page, turn) {
  await page.evaluate(({ id, turn, response }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({
      ...s, status: 'working', updatedAt: new Date().toISOString(),
      activity: {
        ...s.activity, turnCount: turn, turn: `${id}-turn-${turn}`, responseTurn: `${id}-turn-${turn}`,
        userMessage: `Message ${turn}`, response,
      },
    });
  }, { id: ID, turn, response: reply(turn) });
  await expect(page.locator(`.session-view[data-id="${ID}"] .stream-scroll`)).toContainText(`Reply ${turn}, paragraph 30.`);
  await motionsDone(page);
}

for (const arriving of [false, true]) {
  test(`opening and reopening a long session shows the latest reply${arriving ? ', including a row arriving after open' : ''} (#1646)`, async ({ page }) => {
    await page.addInitScript(() => { Object.defineProperty(navigator, 'standalone', { value: true }); });
    await page.goto('?demo=1&static=1&delegates=1#/sessions');
    await page.waitForSelector('html[data-demo-ready]');
    await page.evaluate(({ id, turns, response, arriving }) => {
      const at = new Date().toISOString();
      window.agentBridgeDemo.host.historyDelayMs = 100;
      window.agentBridgeDemo.upsert({
        id, agent: 'copilot', title: 'Long conversation', cwd: 'C:\\demo', status: arriving ? 'working' : 'idle',
        statusSince: at, startedAt: at, updatedAt: at, processId: 4300, question: null,
        activity: {
          turnCount: turns, longReplies: true, turn: `${id}-turn-${turns}`, responseTurn: `${id}-turn-${turns}`,
          userMessage: `Message ${turns}`, response,
        },
      });
    }, { id: ID, turns: TURNS, response: reply(TURNS), arriving });
    const v = await open(page, ID);
    const pane = v.locator('.stream-scroll');
    await loadEarlierTo(pane, TURNS - 1);
    await expect.poll(() => pane.locator('.history-turn').count()).toBeGreaterThan(20);
    await expect.poll(() => gap(pane)).toBeLessThan(4);
    await expect(v.locator('.response p').last()).toBeInViewport();
    await expect(pane.locator('.history-turn')).toHaveCount(TURNS - 1);
    await expect.poll(() => gap(pane)).toBeLessThan(4);

    await pane.evaluate((el) => { el.dispatchEvent(new Event('touchmove')); el.scrollTop = 0; });
    await expect.poll(() => gap(pane)).toBeGreaterThan(400);
    const restored = await pane.evaluate((el) => el.scrollTop);
    await page.goBack();
    await expect(v).toBeHidden();
    await page.goForward();
    await expect(v).toBeVisible();
    await motionsDone(page);
    expect(await pane.evaluate((el) => el.scrollTop)).toBe(restored);
    await go(page, 'sessions');
    if (arriving) {
      const other = await open(page, 'demo-delegates');
      await other.getByRole('tab', { name: 'Delegates' }).click();
      await other.locator('.delegates-panel .delegate[data-id="demo-agent-suite"] .delegate-summary').click();
      await expect.poll(() => page.evaluate(() => history.state?.delegate)).toBe('demo-agent-suite');
      // Row navigation must wait for the delegate's history entry to be popped first.
      const row = page.locator(`.session[data-id="${ID}"] .row-main`);
      expect(await row.evaluate((el) => { el.click(); return el.getAttribute('aria-current'); })).toBe(null);
      await expect(v).toBeVisible();
      await expect(other).toBeHidden();
      await expect(page.locator('.session[data-id="demo-delegates"] .row-main')).not.toHaveAttribute('aria-current', 'true');
    } else {
      await open(page, ID);
    }
    if (arriving) { await arrive(page, TURNS + 1); }
    await expect.poll(() => gap(pane)).toBeLessThan(4);
    await expect(v.locator('.response p').last()).toBeInViewport();

    // Opening starts a new follow interval; scrolling up still ends it.
    await pane.evaluate((el) => { el.dispatchEvent(new Event('touchmove')); el.scrollTop -= 80; });
    await expect.poll(() => gap(pane)).toBeGreaterThan(40);
    await motionsDone(page);
    const held = await pane.evaluate((el) => el.scrollTop);
    await arrive(page, TURNS + (arriving ? 2 : 1));
    expect(await pane.evaluate((el) => el.scrollTop)).toBe(held);
  });
}

for (const disclosure of [false, true]) {
  test(disclosure ? 'reasoning opened before the opening frame stays put through a machine refresh, then response growth follows' : 'choosing an already selected row leaves its reading position alone', async ({ page }) => {
    await page.setViewportSize({ width: 1100, height: 900 });
    await page.goto('?demo=1&static=1#/sessions');
    await page.waitForSelector('html[data-demo-ready]');
    const id = '019a1b2c-codex-example';
    await page.evaluate(({ id, disclosure }) => {
      const d = window.agentBridgeDemo;
      const s = d.host.get(id);
      const long = Array.from({ length: 60 }, (_, i) => `Paragraph ${i + 1}.`).join('\n\n');
      d.upsert({
        ...s, status: 'working', question: null, updatedAt: new Date(Date.now() + 1000).toISOString(),
        activity: { ...s.activity, response: disclosure ? 'Short.' : long, reasoning: disclosure ? long : null, userMessage: null, currentTool: null },
      });
    }, { id, disclosure });
    const row = page.locator(`.session[data-id="${id}"] .row-main`);
    const pane = page.locator(`.session-view[data-id="${id}"] .stream-scroll`);
    if (disclosure) {
      // The disclosure grows in the same task as opening, before any opening frame can run.
      await row.evaluate((el) => {
        el.click();
        document.querySelector('.session-view[data-id="019a1b2c-codex-example"] details.reasoning').open = true;
      });
      await expect(pane).toContainText('Hide reasoning');
      await motionsDone(page);
      expect(await pane.evaluate((el) => el.scrollTop)).toBe(0);
      await page.evaluate(() => {
        const d = window.agentBridgeDemo;
        const link = d.hosts.link('demo');
        link.machine = { ...link.machine, name: 'Workstation' };
        d.page.refresh();
      });
      await expect(page.locator(`.session-view[data-id="${id}"]`)).toContainText('Workstation');
      await motionsDone(page);
      expect(await pane.evaluate((el) => el.scrollTop)).toBe(0);
      await page.evaluate((id) => {
        const d = window.agentBridgeDemo;
        const s = d.host.get(id);
        d.upsert({ ...s, activity: { ...s.activity, response: 'A new streamed response.\n\nAnother streamed paragraph.' }, updatedAt: new Date(Date.now() + 3000).toISOString() });
      }, id);
      await expect(pane).toContainText('A new streamed response.');
      await expect.poll(() => gap(pane)).toBeLessThan(4);
      await pane.locator('.stream > .agent-block > .response').evaluate((el) => { el.style.height = getComputedStyle(el).height; el.style.boxSizing = 'content-box'; el.style.paddingBlockStart = '40px'; });
      await expect.poll(() => gap(pane)).toBeLessThan(4);
    } else {
      await row.click();
      await expect.poll(() => gap(pane)).toBeLessThan(4);
      await pane.evaluate((el) => { el.scrollTop -= 80; });
      await motionsDone(page);
      const held = await pane.evaluate((el) => el.scrollTop);
      await row.click();
      await motionsDone(page);
      expect(await pane.evaluate((el) => el.scrollTop)).toBe(held);
    }
  });
}
