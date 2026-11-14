import { test, expect } from '@playwright/test';
import { open, motionsDone } from './nav.mjs';

const ID = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const gap = (pane) => pane.evaluate((el) => el.scrollHeight - el.clientHeight - el.scrollTop);
test.use({
  viewport: { width: 390, height: 844 }, isMobile: false, hasTouch: true,
  userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
});
async function say(page, turn = 12, lines = 40) {
  await page.evaluate(({ id, turn, lines }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    d.upsert({
      ...s, status: 'working', question: null, updatedAt: new Date().toISOString(),
      activity: {
        ...s.activity, turn: `${id}-turn-${turn}`, responseTurn: `${id}-turn-${turn}`,
        userMessage: `Message ${turn}`, reasoning: null, currentTool: null,
        response: Array.from({ length: lines }, (_, i) => `Latest ${turn}, paragraph ${i + 1}.`).join('\n\n'),
      },
    });
  }, { id: ID, turn, lines });
  await motionsDone(page);
}
async function start(page, { short = false, standalone = true } = {}) {
  if (standalone) { await page.addInitScript(() => { Object.defineProperty(navigator, 'standalone', { value: true }); }); }
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  if (short) { await page.evaluate((id) => window.agentBridgeDemo.host.historyTurns.set(id, []), ID); }
  await say(page, 12, short ? 1 : 40);
  const view = await open(page, ID);
  const pane = view.locator('.stream-scroll');
  await motionsDone(page);
  return { view, pane, pill: view.getByRole('button', { name: 'Jump to latest', exact: true }) };
}
async function scroll(page, pane, delta) {
  const box = await pane.boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.wheel(0, delta);
  await page.mouse.move(1, 1);
  await motionsDone(page);
}

for (const reducedMotion of ['reduce', 'no-preference']) {
  test(`jumping by click and Enter rejoins following (${reducedMotion})`, async ({ page }) => {
    await page.emulateMedia({ reducedMotion });
    const { view, pane, pill } = await start(page);
    await expect(pill).toBeHidden();
    await scroll(page, pane, -100000);
    await expect(pill).toBeVisible();
    const held = await pane.evaluate((el) => el.scrollTop);
    await say(page, 13);
    expect(await pane.evaluate((el) => el.scrollTop)).toBe(held);
    await pill.click();
    await expect.poll(() => gap(pane)).toBeLessThan(4);
    await expect(pill).toBeHidden();
    await say(page, 14, 50);
    await expect.poll(() => gap(pane)).toBeLessThan(4);
    await scroll(page, pane, -100000);
    await expect(pill).toBeVisible();
    await pill.focus();
    await page.keyboard.press('Enter');
    await page.setViewportSize({ width: 390, height: 1000 });
    await expect.poll(() => gap(pane)).toBeLessThan(4);
    await expect(pill).toBeHidden();
    await scroll(page, pane, -100000);
    await expect(pill).toBeVisible();
    await scroll(page, pane, 100000);
    await expect.poll(() => gap(pane)).toBeLessThan(4);
    await expect(pill).toBeHidden();
    expect(await view.locator('.jump-latest').getAttribute('aria-live')).toBeNull();
  });
}

test('less than one screen from the end does not show the pill', async ({ page }) => {
  const { pane, pill } = await start(page);
  await scroll(page, pane, -40);
  await expect.poll(() => gap(pane)).toBeGreaterThan(30);
  await expect(pill).toBeHidden();
});
test('a conversation shorter than a screen never shows the pill', async ({ page }) => {
  const { pane, pill } = await start(page, { short: true });
  await expect.poll(() => pane.evaluate((el) => el.scrollHeight - el.clientHeight)).toBe(0);
  await expect(pill).toBeHidden();
});

async function storedQuestion(page, fields = 1, options = {}) {
  const result = await start(page, options);
  await page.evaluate(async ({ id, fields }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    const at = new Date().toISOString();
    const question = { id: 'jump-question', kind: 'form', askedAt: at, message: 'Choose a database',
      fields: Array.from({ length: fields }, (_, i) => ({ name: `db${i}`, title: `Database ${i + 1}`, type: 'text' })) };
    d.host.historyTurns.set(id, [
      { id: `${id}-turn-1`, at, prompt: { text: 'Earlier turn' }, parts: [{ kind: 'response', id: 'part-r', at, text: 'Earlier response.\n\n'.repeat(60) }] },
      { id: `${id}-turn-2`, at, prompt: { text: 'Question turn' }, parts: [{ kind: 'question', id: 'part-q', at, question }] },
    ]);
    d.upsert({ ...s, question, status: 'waiting', updatedAt: at });
    await d.view.history.open('demo', id, { session: d.host.get(id) });
  }, { id: ID, fields });
  await motionsDone(page);
  return result;
}

test('the plain pill moves above a stored question instead of covering its answer buttons', async ({ page }) => {
  const { view, pane, pill } = await storedQuestion(page);
  for (const selector of ['.answer-controls', '.answer-actions']) {
    const controls = view.locator(`.question:not(.resolved) ${selector}`);
    await expect(controls).toBeVisible();
    await pane.evaluate((el, selector) => {
      const controls = el.querySelector(selector).getBoundingClientRect();
      const reply = el.parentElement.querySelector('.reply-slot').getBoundingClientRect();
      el.scrollTop += controls.top - reply.top + 30;
    }, selector);
    await expect(pill).toBeVisible();
    await expect.poll(async () => {
      const p = await pill.boundingBox();
      const c = await controls.boundingBox();
      return c.y - p.y - p.height;
    }).toBeGreaterThanOrEqual(8);
  }
});

for (const standalone of [true, false]) {
  test(`a tall answer form keeps the pill in view, tappable and clear of actual controls (${standalone ? 'installed' : 'browser'})`, async ({ page }) => {
    const { view, pane, pill } = await storedQuestion(page, 8, { standalone });
    const held = await pane.evaluate((el) => {
      el.scrollTop += el.querySelector('.answer-controls').getBoundingClientRect().top + 100;
      return el.scrollTop;
    });
    await expect.poll(() => gap(pane)).toBeGreaterThan(await pane.evaluate((el) => el.clientHeight));
    await expect(pill).toBeInViewport({ ratio: 1 });
    expect(await pane.evaluate((el) => el.scrollTop)).toBe(held);
    const tappable = () => pill.evaluate((el) => {
      const r = el.getBoundingClientRect();
      return el.contains(document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2));
    });
    expect(await tappable()).toBe(true);
    expect(await view.evaluate((el) => {
      const pill = el.querySelector('.jump-latest').getBoundingClientRect();
      const pane = el.querySelector('.stream-scroll').getBoundingClientRect();
      return [...el.querySelectorAll('.question:not(.resolved) input, .question:not(.resolved) textarea, .question:not(.resolved) button, .question:not(.resolved) select')].some((control) => {
        const r = control.getBoundingClientRect();
        return r.width && r.height && r.top < pane.bottom && r.bottom > pane.top
          && r.left < pill.right && r.right > pill.left && Math.min(r.bottom, pane.bottom) > pill.top && Math.max(r.top, pane.top) < pill.bottom;
      });
    })).toBe(false);
    await page.setViewportSize({ width: 390, height: 400 });
    await expect(pill).toBeInViewport({ ratio: 1 });
    await motionsDone(page);
    await expect.poll(async () => {
      const p = await pill.boundingBox();
      const reply = await view.locator('.reply-slot').boundingBox();
      return Math.abs(reply.y - p.y - p.height - 8);
    }).toBeLessThanOrEqual(1);
    expect(await tappable()).toBe(true);
    await pill.click();
    await expect.poll(() => gap(pane)).toBeLessThan(4);
    await expect(pill).toBeHidden();
  });
}

for (const width of [390, 820, 1440]) {
  test(`the 44 px pill stays above the composer at ${width}px, through keyboard and draft growth`, async ({ page }) => {
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await page.setViewportSize({ width, height: 844 });
    const { view, pane, pill } = await start(page);
    await page.evaluate(() => {
      const root = document.documentElement;
      root.dataset.platform = 'ios';
      root.dataset.standalone = 'true';
      root.style.setProperty('--env-safe-top', '47px');
      root.style.setProperty('--env-safe-bottom', '34px');
    });
    await scroll(page, pane, -100000);
    const placement = async () => {
      await expect(pill).toBeVisible();
      await expect.poll(async () => {
        const p = await pill.boundingBox();
        const reply = await view.locator('.reply-slot').boundingBox();
        return Math.abs(reply.y - (p.y + p.height) - 8);
      }).toBeLessThanOrEqual(1);
      const p = await pill.boundingBox();
      const column = await pane.boundingBox();
      expect(p.height).toBe(44);
      expect(Math.abs(p.x + p.width / 2 - column.x - column.width / 2)).toBeLessThanOrEqual(1);
      expect(p.x).toBeGreaterThanOrEqual(12);
      expect(p.y + p.height).toBeLessThan(844 - 34);
    };
    await placement();
    await page.setViewportSize({ width, height: 500 });
    await page.evaluate(() => document.documentElement.style.setProperty('--env-safe-bottom', '0px'));
    await view.getByRole('textbox', { name: 'Reply', exact: true }).fill('First line\nSecond line\nThird line');
    await placement();
    await pill.focus();
    await page.keyboard.press('Tab');
    expect(await view.locator('.reply-slot').evaluate((el) => el.contains(document.activeElement))).toBe(true);
    await view.getByRole('button', { name: 'Send', exact: true }).click();
    await expect.poll(() => gap(pane)).toBeLessThan(4);
    await expect(pill).toBeHidden();
  });
}
