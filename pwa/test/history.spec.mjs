// The conversation before the latest exchange (#1285 S2a): a session with turns shows the earlier
// ones read-only above its latest exchange, and opens at the latest.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

test('earlier turns are drawn above the latest exchange, which is drawn once', async ({ page }) => {
  await page.goto('?demo=1&static=1&signin=1&name=DEV-VM1#/sessions');
  await page.waitForSelector('html[data-demo-ready]');
  await open(page, 'signin-1');
  const v = view(page, 'signin-1');
  // signin-1 has three turns: two earlier, and the latest the card draws live.
  await expect(v.locator('.history-turn')).toHaveCount(2);
  await expect(v.locator('.history-turn').first()).toContainText('Demo prompt 1');
  await expect(v.getByText('Demo prompt 3')).toHaveCount(0);
  await expect(v.locator('.history-turn button, .history-turn textarea')).toHaveCount(0);
  const scroll = v.locator('.stream-scroll');
  await expect.poll(() => scroll.evaluate((el) => el.scrollHeight - el.scrollTop - el.clientHeight)).toBeLessThan(4);
});

test('a reply sent mid-turn stays below the turn that is still being written', async ({ page }) => {
  await page.goto('?demo=1&static=1&signin=1&name=DEV-VM1#/sessions');
  await page.waitForSelector('html[data-demo-ready]');
  await open(page, 'signin-1');
  const v = view(page, 'signin-1');
  // The host's latest turn is 4 (the owner's reply); the words on screen are still turn 3's until
  // responseTurn catches up.
  const latest = (responseTurn) => page.evaluate(({ responseTurn, t }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get('signin-1');
    d.upsert({ ...s, status: 'working', updatedAt: t, activity: { ...s.activity, turnCount: 4, turn: 'signin-1-turn-4', responseTurn, userMessage: 'Sent while it was working', response: 'Still writing the first answer.' } });
  }, { responseTurn, t: new Date(Date.now() + 1000).toISOString() });
  // True when the owner's bubble comes after the agent's block in the stream.
  const bubbleLast = () => v.locator('.stream').evaluate((s) => !!(s.querySelector('.agent-block').compareDocumentPosition(s.querySelector('.user-message')) & Node.DOCUMENT_POSITION_FOLLOWING));
  await latest('signin-1-turn-3');
  await expect(v.locator('.user-message')).toContainText('Sent while it was working');
  expect(await bubbleLast()).toBe(true);
  // Once the agent starts answering it, the order is by time again.
  await latest('signin-1-turn-4');
  await expect.poll(bubbleLast).toBe(false);
});