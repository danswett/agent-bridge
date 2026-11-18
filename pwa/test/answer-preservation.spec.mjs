import { test, expect } from '@playwright/test';
import { open, motionsDone } from './nav.mjs';
import { loadEarlierTo } from './history-fixture.mjs';

const ID = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const ANSWER = 'The answer stays with the question. '.repeat(24).slice(0, 800);
const AT = Date.parse('2026-11-11T22:27:00Z');

test.use({
  viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true,
  reducedMotion: 'reduce',
  userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
});

async function say(page, turn, seconds, response, kind = 'user') {
  await page.evaluate(({ id, turn, at, response, kind }) => {
    const d = window.agentBridgeDemo, s = d.host.get(id);
    const activity = {
      ...s.activity, turn: `${id}-turn-${turn}`, responseTurn: `${id}-turn-${turn}`,
      turnCount: turn, kind, userMessage: `Prompt ${turn}`,
      userMessageFrom: kind === 'scheduled' ? 'schedule' : 'person',
      response, reasoning: null, currentTool: null, own: undefined, source: undefined,
    };
    if (kind === 'notification') {
      d.upsert({ ...s, updatedAt: at, activity: { ...s.activity, notice: {
        id: activity.turn, at, kind, prompt: { text: activity.userMessage, from: 'notification' },
        parts: [{ id: `${activity.turn}-reply`, kind: 'response', text: response }],
        partsMore: 0, truncated: false, unreadable: false,
      } } });
    } else { d.upsert({ ...s, status: 'working', question: null, updatedAt: at, activity }); }
  }, { id: ID, turn, at: new Date(AT + seconds * 1000).toISOString(), response, kind });
  await motionsDone(page);
}

test('installed phone keeps all four replies in turn order and announces deferred origins without moving the reader', async ({ page }) => {
  await page.addInitScript(() => Object.defineProperty(navigator, 'standalone', { value: true }));
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await expect(page.locator('html')).toHaveAttribute('data-standalone', 'true');
  await say(page, 12, 0, ANSWER);
  const view = await open(page, ID), pane = view.locator('.stream-scroll');
  await loadEarlierTo(pane, 11);
  await pane.evaluate((el) => { el.scrollTop = el.scrollHeight - el.clientHeight - 40; });
  await expect.poll(() => pane.evaluate((el) => el.scrollHeight - el.clientHeight - el.scrollTop)).toBe(40);
  await motionsDone(page);
  const held = await pane.evaluate((el) => el.scrollTop);
  const announce = view.locator('.conversation-unread-announce');
  await say(page, 13, 3, null, 'scheduled');
  await expect(announce).toHaveText('');
  await say(page, 13, 3, 'Scheduled reply', 'scheduled');
  await expect(announce).toHaveText('Scheduled run replied. 1 new reply.');
  await announce.evaluate((el) => {
    el.dataset.changes = '0';
    new MutationObserver(() => { el.dataset.changes = String(Number(el.dataset.changes) + 1); })
      .observe(el, { childList: true, subtree: true, characterData: true });
  });
  await say(page, 13, 4, 'Scheduled reply, completed', 'scheduled');
  await expect(announce).toHaveAttribute('data-changes', '0');
  await say(page, 14, 8, 'Notification reply', 'notification');
  await expect(announce).toHaveText('Scheduled run replied. 2 new replies.');
  await say(page, 15, 10, 'Fourth reply');
  await expect(announce).toHaveText('New reply. 3 new replies.');
  await expect(announce).toHaveAttribute('aria-live', 'polite');
  await expect(announce).toHaveAttribute('role', 'status');
  expect(await pane.evaluate((el) => el.scrollTop)).toBe(held);
  const replies = [ANSWER, 'Scheduled reply, completed', 'Notification reply', 'Fourth reply'];
  const text = await pane.innerText();
  for (const [i, reply] of replies.entries()) {
    const shown = pane.locator('.response, .history-reply').filter({ hasText: reply });
    await expect(shown).toHaveCount(1);
    await expect(shown).toBeVisible();
    expect(await shown.evaluate((el) => el.closest('details') === null)).toBe(true);
    if (i) { expect(text.indexOf(reply)).toBeGreaterThan(text.indexOf(replies[i - 1])); }
  }
  for (const [turn, from, label] of [[13, 'schedule', 'Scheduled'], [14, 'notification', 'Notification']]) {
    const row = pane.locator(`.history-turn[data-turn="${ID}-turn-${turn}"]`);
    await expect(row.locator(`.prompt-origin[data-from="${from}"] .prompt-label`)).toContainText(label);
    await expect(row.locator('.history-bubble')).toHaveCount(0);
    await expect(row.getByRole('button', { name: /Show prompt/ })).toHaveAttribute('aria-expanded', 'false');
  }
  await expect(pane.locator('[data-first-unread]')).toHaveAttribute('data-turn', `${ID}-turn-13`);
  await view.getByRole('button', { name: '3 new · Jump to first unread' }).tap();
  await expect.poll(() => pane.locator('[data-first-unread] .history-reply').evaluate((el) => {
    const box = el.getBoundingClientRect(), port = el.closest('.stream-scroll').getBoundingClientRect();
    return box.bottom > port.top && box.top < port.bottom;
  })).toBe(true);
  await expect(pane).toContainText(ANSWER);
});
