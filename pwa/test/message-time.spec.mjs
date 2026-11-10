// The time on each earlier message (#1619, ui.md §6.4 "Time on messages"): its exact time on
// hover, focus, tap and keyboard, and nothing moving when it refreshes. The page clock is the
// test's, so no test waits on a real timer.
import { test, expect } from '@playwright/test';
import { motionsDone, open, view } from './nav.mjs';

const EXACT = /\d{1,2}:\d{2}:\d{2}/;

async function turns(page) {
  await page.clock.install({ time: new Date() });
  await page.goto('?demo=1&static=1&signin=1&name=DEV-VM1#/sessions');
  await page.waitForSelector('html[data-demo-ready]');
  await open(page, 'signin-1');
  return view(page, 'signin-1').locator('.history-turn').first();
}

test('every message in an earlier turn shows a time whose accessible name is the words shown and then the exact time', async ({ page }) => {
  const turn = await turns(page);
  const owner = turn.locator('.history-prompt .msg-time');
  await expect(owner).toHaveCount(1);
  await expect(owner).toHaveAttribute('aria-label', EXACT);
  await expect(owner.locator('time')).toHaveAttribute('datetime', /\d{4}-\d{2}-\d{2}T/);
  await expect(owner.locator('time')).not.toBeEmpty();
  await expect(turn.locator('.history-block .msg-time')).toHaveCount(1);
  await expect(turn.locator('.history-tool .msg-time')).toHaveCount(1);
});

test('hovering, focusing, tapping or pressing a key on a time shows the exact time, and Escape closes it', async ({ page }) => {
  const turn = await turns(page);
  const time = turn.locator('.history-prompt .msg-time');
  const shown = await time.locator('time').innerText();
  const exact = (await time.getAttribute('aria-label')).slice(shown.length + 2);
  const pop = page.locator('.msg-time-pop');

  await time.hover();
  await expect(pop).toHaveText(exact);
  await page.mouse.move(0, 0);
  await expect(pop).toBeHidden();

  await time.focus();
  await expect(pop).toHaveText(exact);
  await time.blur();
  await expect(pop).toBeHidden();

  await time.dispatchEvent('click');
  await expect(pop).toHaveText(exact);
  await page.keyboard.press('Escape');
  await expect(pop).toBeHidden();

  await time.focus();
  await page.keyboard.press('Enter');
  await page.keyboard.press('Escape');
  await expect(pop).toBeHidden();
});

test('a refreshed time does not move or resize anything', async ({ page }) => {
  const turn = await turns(page);
  const box = () => turn.evaluate((el) => { const r = el.getBoundingClientRect(); return [r.height, r.width]; });
  const before = await box();
  const text = await turn.locator('.history-prompt .msg-time time').textContent();
  await page.clock.fastForward('02:00:00');
  await expect(turn.locator('.history-prompt .msg-time time')).not.toHaveText(text);
  expect(await box()).toEqual(before);
});

const LIVE_ID = '019a1b2c-codex-example';
const SENT = '2026-11-10T20:00:00Z';
const REPLIED = '2026-11-10T20:00:10Z';

async function liveCard(page, from = 'person', response = 'Done') {
  await page.clock.install({ time: new Date('2026-11-10T20:00:30Z') });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(({ id, from, response }) => {
    const d = window.agentBridgeDemo;
    d.host.historyTurns.set(id, []);
    d.upsert({
      ...d.host.get(id), status: 'idle', question: null, updatedAt: new Date().toISOString(),
      activity: { turn: 'live-time', responseTurn: 'live-time', userMessage: 'Again', userMessageFrom: from, response },
    });
  }, { id: LIVE_ID, from, response });
  await open(page, LIVE_ID);
  return view(page, LIVE_ID);
}

async function recordLive(page, from = 'person', turnId = 'live-time', response = 'Done', replied = REPLIED) {
  await page.evaluate(async ({ id, from, turnId, sent, replied, response }) => {
    const d = window.agentBridgeDemo;
    d.host.historyTurns.set(id, [{
      id: turnId, at: sent, prompt: { text: 'Again', from },
      parts: [{ id: 'reply-time', kind: 'response', text: response, at: replied }],
    }]);
    await d.view.history.open(d.host.machine.id, id);
  }, { id: LIVE_ID, from, turnId, sent: SENT, replied, response });
}

test('a late recorded live time preserves message order, focus, layout and reading position through refresh', async ({ page }) => {
  const response = Array.from({ length: 30 }, (_, i) => `Paragraph ${i}. Keep this reading position.`).join('\n\n');
  const v = await liveCard(page, 'person', response);
  const stream = v.locator('.stream');
  await expect(stream.locator('.msg-time')).toHaveCount(0);
  await motionsDone(page);
  await v.locator('.reply-slot textarea').focus();
  await v.locator('.stream-scroll').evaluate((el) => { el.scrollTop = 100; el.dispatchEvent(new Event('scroll')); });
  const geometry = () => v.evaluate((el) => {
    const sc = el.querySelector('.stream-scroll');
    return [sc.scrollTop, sc.scrollHeight, ...['.user-message', '.speaker', '.response'].flatMap((s) => {
      const r = el.querySelector(s).getBoundingClientRect();
      return [r.x, r.y, r.width, r.height];
    })];
  });
  const before = await geometry();
  await recordLive(page, 'person', 'live-time', response);
  await expect(stream.locator('.user-message .msg-time time')).toHaveAttribute('datetime', SENT);
  const reply = stream.locator('.speaker .msg-time');
  await expect(reply.locator('time')).toHaveAttribute('datetime', REPLIED);
  await expect(v.locator('.reply-slot textarea')).toBeFocused();
  expect(await geometry()).toEqual(before);
  await expect(v.locator('.history-turn')).toHaveCount(0);
  await expect(stream.locator('.response')).toContainText('Paragraph 0.');
  await reply.focus();
  const focused = await geometry();
  await recordLive(page, 'person', 'live-time', response);
  await expect(reply).toBeFocused();
  const corrected = '2026-11-10T20:00:11Z';
  await recordLive(page, 'person', 'live-time', response, corrected);
  await expect(reply.locator('time')).toHaveAttribute('datetime', corrected);
  await expect(reply).toBeFocused();
  await page.keyboard.press('Enter');
  const shown = await reply.locator('time').innerText();
  await expect(page.locator('.msg-time-pop')).toHaveText((await reply.getAttribute('aria-label')).slice(shown.length + 2));
  await page.keyboard.press('Escape');
  await expect(page.locator('.msg-time-pop')).toBeHidden();
  await page.clock.fastForward('02:00:00');
  expect(await geometry()).toEqual(focused);
  await expect(reply).toBeFocused();
});

test('a collapsed live agent prompt ignores same-text other turns and reveals its recorded time without expanding', async ({ page }) => {
  const v = await liveCard(page, 'agent');
  const prompt = v.locator('.stream > .prompt-origin');
  await recordLive(page, 'agent', 'older-time');
  await expect(prompt.locator('.msg-time')).toHaveCount(0);
  const height = await prompt.evaluate((el) => el.offsetHeight);
  await recordLive(page, 'agent');
  const time = prompt.locator('.msg-time');
  await expect(time.locator('time')).toHaveAttribute('datetime', SENT);
  await time.click();
  await expect(page.locator('.msg-time-pop')).toBeVisible();
  await expect(prompt.locator('.prompt-toggle')).toHaveAttribute('aria-expanded', 'false');
  expect(await prompt.evaluate((el) => el.offsetHeight)).toBe(height);
});

test('invalidating a focused recorded time keeps focus local without scrolling, then restores its time control', async ({ page }) => {
  const response = Array.from({ length: 30 }, (_, i) => `First paragraph ${i}. Keep reading here.`).join('\n\n');
  const next = response.replaceAll('First', 'Later');
  const v = await liveCard(page, 'person', response);
  await recordLive(page, 'person', 'live-time', response);
  await motionsDone(page);
  const slot = v.locator('.speaker-age');
  await slot.locator('.msg-time').focus();
  const sc = v.locator('.stream-scroll');
  await sc.evaluate((el) => { el.scrollTop = 100; el.dispatchEvent(new Event('scroll')); });
  const before = await sc.evaluate((el) => el.scrollTop);
  expect(before).toBe(100);
  await page.evaluate(({ id, next }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({ ...s, activity: { ...s.activity, response: next } });
  }, { id: LIVE_ID, next });
  await expect(slot.locator('.msg-time')).toHaveCount(0);
  await expect(slot).toBeFocused();
  await expect(slot).toHaveAccessibleName('Sent time unavailable');
  expect(await sc.evaluate((el) => el.scrollTop)).toBe(before);
  const recorded = '2026-11-10T20:00:20Z';
  await recordLive(page, 'person', 'live-time', next, recorded);
  await expect(slot.locator('.msg-time')).toBeFocused();
  await expect(slot.locator('time')).toHaveAttribute('datetime', recorded);
  expect(await sc.evaluate((el) => el.scrollTop)).toBe(before);
});
