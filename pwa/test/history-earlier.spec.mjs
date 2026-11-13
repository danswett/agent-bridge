// Earlier messages (#1285 S2b): the button and the scroll near the top load the page before the
// oldest turn, what is read stays where it is, and the start of the history is said.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';
import { loadEarlierTo } from './history-fixture.mjs';

const ID = 'signin-1';

async function start(page, count, width) {
  await page.setViewportSize({ width, height: 800 });
  await page.goto('?demo=1&static=1&signin=1&name=DEV-VM1#/sessions');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(({ ID, count }) => {
    const turns = Array.from({ length: count }, (_, i) => ({
      id: `${ID}-t${i + 1}`, at: new Date(Date.now() - (count - i) * 60000).toISOString(),
      prompt: { text: `Earlier prompt ${i + 1}`, from: 'person' },
      parts: [{ kind: 'response', text: `Earlier reply ${i + 1}\n\nA second paragraph so the turn has some height.` }],
      partsMore: 0, truncated: false, unreadable: false,
    }));
    window.agentBridgeDemo.host.historyTurns.set(ID, turns);
  }, { ID, count });
  await open(page, ID);
  await loadEarlierTo(view(page, ID), Math.min(count, 105));
}

const held = (v) => v.locator('.history-turn').count();

for (const width of [390, 1440]) test(`a short history says where the session starts at ${width}`, async ({ page }) => {
  await start(page, 30, width);
  const v = view(page, ID);
  await expect(v.locator('.history-top')).toContainText('Start of session \u00b7 ');
  await expect(v.getByRole('button', { name: 'Load earlier messages' })).toHaveCount(0);
});

for (const width of [390, 1440]) test(`Load earlier messages brings the page before and keeps the turn being read in place at ${width}`, async ({ page }) => {
  await start(page, 160, width);
  const v = view(page, ID);
  const scroll = v.locator('.stream-scroll');
  const button = v.getByRole('button', { name: 'Load earlier messages' });
  // The fixture manually preloaded 105 turns; the button is what asks for more.
  await expect(button).toBeVisible({ timeout: 20000 });
  const before = await held(v);
  // Reading a little below the top, outside the near-top range.
  await scroll.evaluate((el) => { el.scrollTop = el.clientHeight * 2; });
  // The first turn on screen is the one being read.
  const markerId = await scroll.evaluate((el) => [...el.querySelectorAll('.history-turn')].find((t) => t.getBoundingClientRect().top >= el.getBoundingClientRect().top)?.dataset.turn);
  const top = () => v.locator(`.history-turn[data-turn="${markerId}"]`).evaluate((el) => el.getBoundingClientRect().top);
  const was = await top();
  // The button sits at the top, where scrolling to it already loads; it is pressed in place here.
  await button.evaluate((b) => b.click());
  await expect.poll(() => held(v)).toBeGreaterThan(before);
  await expect.poll(async () => Math.abs((await top()) - was)).toBeLessThan(3);
  // Prepending must not re-engage following: later live growth also leaves this turn in place.
  await page.evaluate((id) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({ ...s, activity: { ...s.activity, response: Array.from({ length: 60 }, (_, i) => `Still streaming ${i + 1}.`).join('\n\n') } });
  }, ID);
  await expect(scroll).toContainText('Still streaming 60.');
  await page.evaluate(() => new Promise((resolve) => requestAnimationFrame(() => requestAnimationFrame(resolve))));
  await expect.poll(async () => Math.abs((await top()) - was)).toBeLessThan(3);
});

for (const width of [390, 1440]) test(`scrolling to the top loads earlier turns without the button at ${width}`, async ({ page }) => {
  await start(page, 160, width);
  const v = view(page, ID);
  await expect(v.getByRole('button', { name: 'Load earlier messages' })).toBeVisible({ timeout: 20000 });
  const before = await held(v);
  const scroll = v.locator('.stream-scroll');
  await scroll.evaluate((el) => { el.scrollTop = el.clientHeight * 3; });
  await scroll.hover();
  await page.mouse.wheel(0, -100000);
  await expect.poll(() => held(v)).toBeGreaterThan(before);
});

test('pressing Load earlier messages from the keyboard keeps focus on it through loading and the next page', async ({ page }) => {
  await start(page, 160, 1440);
  const v = view(page, ID);
  const button = v.getByRole('button', { name: 'Load earlier messages' });
  await expect(button).toBeVisible({ timeout: 20000 });
  const before = await held(v);
  await button.focus();
  await page.keyboard.press('Enter');
  await expect.poll(() => held(v)).toBeGreaterThan(before);
  await expect(button).toBeVisible();
  await expect(button).toBeFocused();
  // And again: the same button is still the one with focus.
  const second = await held(v);
  await page.keyboard.press('Enter');
  await expect.poll(() => held(v)).toBeGreaterThan(second);
  await expect(button).toBeFocused();
});

test('new live turns held below the reader do not offer a second earlier-page control', async ({ page }) => {
  await start(page, 160, 1440);
  const v = view(page, ID);
  const scroll = v.locator('.stream-scroll');
  await expect(v.getByRole('button', { name: 'Load earlier messages' })).toBeVisible();
  await scroll.evaluate((el) => { el.scrollTop = el.clientHeight * 2; });
  for (const n of [1, 2]) {
    await page.evaluate(({ id, n }) => {
      const d = window.agentBridgeDemo;
      const s = d.host.get(id);
      d.upsert({ ...s, activity: { ...s.activity, turn: `live-${n}`, responseTurn: `live-${n}`, response: `New live reply ${n}` } });
    }, { id: ID, n });
    await expect(scroll).toContainText(`New live reply ${n}`);
  }
  await expect(v.getByRole('button', { name: 'Load earlier messages' })).toHaveCount(1);
});

test('live turns held below the reader count toward the earlier-history limit', async ({ page }) => {
  test.setTimeout(120000);
  await start(page, 700, 1440);
  const v = view(page, ID);
  const scroll = v.locator('.stream-scroll');
  const button = v.getByRole('button', { name: 'Load earlier messages' });
  while (await held(v) < 445) {
    const before = await held(v);
    await button.evaluate((b) => b.click());
    await expect.poll(() => held(v)).toBeGreaterThan(before);
  }
  expect(await held(v)).toBe(445);
  await scroll.evaluate((el) => { el.scrollTop = el.clientHeight * 2; });
  for (let n = 1; n <= 6; n += 1) {
    await page.evaluate(({ id, n }) => {
      const d = window.agentBridgeDemo;
      const s = d.host.get(id);
      d.upsert({ ...s, activity: { ...s.activity, turn: `cap-live-${n}`, responseTurn: `cap-live-${n}`, response: `New cap reply ${n}` } });
    }, { id: ID, n });
    await expect(scroll).toContainText(`New cap reply ${n}`);
  }
  await expect(v.locator('.history-top:visible')).toContainText('Start of available history');
  await expect(button).toHaveCount(0);
  await expect(v.locator('.history-turn', { hasText: 'Earlier prompt 700' })).toHaveCount(1);
});

test('past the 500-turn limit a person asking for more is told history stops there and keeps the newest turns', async ({ page }) => {
  test.setTimeout(120000);
  await start(page, 700, 1440);
  const v = view(page, ID);
  const top = v.locator('.history-top');
  const button = v.getByRole('button', { name: 'Load earlier messages' });
  await expect(button).toBeVisible({ timeout: 20000 });
  const load = v.locator('.history-top .history-load');
  for (let i = 0; i < 40 && !(await top.innerText()).includes('Start of available history'); i += 1) {
    await expect(button).toBeVisible();
    const n = await held(v);
    await button.evaluate((b) => b.click());
    await expect.poll(async () => (await held(v)) > n || await load.isHidden(), { timeout: 15000 }).toBe(true);
    await expect(load).not.toHaveAttribute('aria-disabled', 'true');
  }
  await expect(top).toContainText('Start of available history');
  expect(await held(v)).toBeLessThanOrEqual(500);
  // The newest end is still held, so live turns still merge: the newest seeded turn is still drawn.
  await expect(v.locator('.history-turn', { hasText: 'Earlier prompt 700' })).toHaveCount(1);
});