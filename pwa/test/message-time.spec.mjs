// The time on each earlier message (#1619, ui.md §6.4 "Time on messages"): its exact time on
// hover, focus, tap and keyboard, and nothing moving when it refreshes. The page clock is the
// test's, so no test waits on a real timer.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

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
