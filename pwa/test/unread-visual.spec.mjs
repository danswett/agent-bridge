import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { motionsDone, row } from './nav.mjs';

const READ = '6f1c2b7e-claude-example';
const NEW = '019a1b2c-codex-example';
const NEEDS = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const DONE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const LONG = 'Quarterly release readiness review team';

test.use({
  viewport: { width: 390, height: 844 }, deviceScaleFactor: 3,
  isMobile: true, hasTouch: true,
  userAgent: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
});

async function setup(page) {
  await page.addInitScript(() => Object.defineProperty(navigator, 'standalone', { value: true }));
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(({ NEW, DONE }) => {
    const d = window.agentBridgeDemo;
    for (const id of [NEW, DONE]) {
      const s = d.host.get(id);
      d.upsert({ ...s, status: 'idle', question: null,
        updatedAt: new Date(Date.now() - (47 * 60 + 45) * 60000).toISOString(),
        activity: { ...s.activity, turn: 'visual-reply', responseTurn: 'visual-reply', response: 'A new reply' } });
    }
    const h = document.documentElement;
    h.dataset.platform = 'ios';
    h.dataset.standalone = 'true';
    h.style.setProperty('--env-safe-top', '47px');
    h.style.setProperty('--env-safe-bottom', '34px');
  }, { NEW, DONE });
  await expect(row(page, NEW)).toHaveClass(/unread/);
  await motionsDone(page);
}

const geometry = (page) => page.locator('.session > .row-main').evaluateAll((els) => els.map((el) => {
  const age = el.querySelector('.row-age');
  const signal = el.querySelector('.unread-dot') ?? age;
  const r = el.getBoundingClientRect(), a = signal.getBoundingClientRect(), cs = getComputedStyle(el);
  const ink = document.createRange();
  ink.selectNodeContents(age);
  const text = ink.getBoundingClientRect(), slot = age.getBoundingClientRect();
  return { edge: r.right - parseFloat(cs.paddingRight), age: a.right, width: a.width,
    text: el.querySelector('.row-text').getBoundingClientRect().right, height: r.height,
    overflow: Math.max(0, slot.left - text.left, text.right - slot.right) };
}));

for (const width of [390, 820]) {
  for (const colorScheme of ['dark', 'light']) {
    test(`at ${width}px in ${colorScheme}, unread stays in the age column and only read titles recede`, async ({ page }) => {
      await page.setViewportSize({ width, height: 844 });
      await page.emulateMedia({ colorScheme, reducedMotion: 'reduce' });
      await setup(page);
      const before = await geometry(page);
      for (const r of before) {
        expect(Math.abs(r.age - r.edge)).toBeLessThan(0.5);
        expect(Math.abs(r.age - before[0].age)).toBeLessThan(0.5);
        expect(r.height).toBeGreaterThanOrEqual(44);
        expect(r.overflow).toBeLessThan(0.5);
      }
      await expect(page.locator('.unread-dot')).toHaveCount(0);
      const style = (id, selector) => row(page, id).locator(selector).evaluate((el) => {
        const s = getComputedStyle(el); return { color: s.color, weight: s.fontWeight, opacity: s.opacity };
      });
      const read = await style(READ, '.row-title'), unread = await style(NEW, '.row-title');
      expect(read.weight).toBe('400');
      expect(unread.weight).toBe('600');
      expect(read.color).not.toBe(unread.color);
      expect((await style(NEEDS, '.row-title')).color).toBe(unread.color);
      expect((await style(READ, '.row-age')).weight).toBe('400');
      expect((await style(NEW, '.row-age')).weight).toBe('600');
      expect((await style(NEW, '.row-age')).color).not.toBe(read.color);
      const glyph = await style(NEW, '.row-glyph');
      await page.evaluate((ids) => {
        const d = window.agentBridgeDemo, v = d.page.view;
        for (const id of ids) { v.order.markSeen(`demo\n${id}`, d.host.get(id).activity.responseTurn); }
        v.render();
      }, [NEW, DONE]);
      await expect(row(page, NEW)).not.toHaveClass(/unread/);
      await motionsDone(page);
      expect(await style(NEW, '.row-title')).toEqual(read);
      expect(await style(NEW, '.row-glyph')).toEqual(glyph);
      expect(await geometry(page)).toEqual(before);
      expect(await style(DONE, '.row-title')).toEqual(read);
    });
  }
}

test('selected unread ages keep AA contrast on the light selected tint', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'light', reducedMotion: 'reduce' });
  await setup(page);
  await row(page, NEW).evaluate((el) => el.classList.add('selected'));
  const result = await new AxeBuilder({ page })
    .include('.session.unread.selected > .row-main').withRules(['color-contrast']).analyze();
  expect(result.violations).toEqual([]);
});

test('high contrast shows New in the age slot and speaks both new message and age', async ({ page, browserName }) => {
  test.skip(browserName === 'webkit', 'WebKit does not emulate forced colours');
  await page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' });
  await setup(page);
  await expect(row(page, NEW).locator('.row-new')).toBeVisible();
  await expect(row(page, NEW).locator('.row-new')).toHaveText('New');
  await expect(row(page, NEW).locator('.row-age')).toBeHidden();
  await expect(row(page, NEW).locator('.row-main')).toHaveAccessibleName(/New message, .+ago/);
  const at = await row(page, NEW).locator('.row-main').evaluate((el) => {
    const slot = el.querySelector('.row-new'), cs = getComputedStyle(el);
    return { right: slot.getBoundingClientRect().right,
      edge: el.getBoundingClientRect().right - parseFloat(cs.paddingRight),
      color: getComputedStyle(slot).color, title: getComputedStyle(el.querySelector('.row-title')).color };
  });
  expect(Math.abs(at.right - at.edge)).toBeLessThan(0.5);
  expect(at.color).not.toBe(at.title);
});

test('collapsed groups count unread sessions beside a truncating name, never over Needs you', async ({ page }) => {
  await setup(page);
  await page.evaluate(({ READ, NEW, DONE, LONG }) => {
    const v = window.agentBridgeDemo.page.view, o = v.order;
    const group = o.createGroup(LONG);
    for (const id of [READ, NEW, DONE]) { o.setGroup(`demo\n${id}`, group); }
    o.setGroupCollapsed(group, true);
    v.render();
  }, { READ, NEW, DONE, LONG });
  const head = page.locator('.session-group-toggle');
  await expect(head.locator('.group-unread')).toHaveText('2 new');
  await expect(head).toHaveAccessibleName(`${LONG} group, collapsed, 2 sessions with new messages, 3 sessions`);
  const g = await head.evaluate((el) => {
    const name = el.querySelector('.group-name'), count = el.querySelector('.group-unread');
    return { gap: count.getBoundingClientRect().left - name.getBoundingClientRect().right,
      clipped: name.scrollWidth > name.clientWidth, countClipped: count.scrollWidth > count.clientWidth };
  });
  expect(g.gap).toBeCloseTo(8, 0);
  expect(g.clipped).toBe(true);
  expect(g.countClipped).toBe(false);
  await head.click();
  await expect(head.locator('.group-unread')).toBeHidden();
  await head.click();
  await page.evaluate((id) => {
    const d = window.agentBridgeDemo, s = d.host.get(id);
    const question = d.host.get('02d9e5d5-6b35-49bd-b083-39572fbff648').question;
    d.upsert({ ...s, status: 'waiting', question });
  }, NEW);
  await expect(head.locator('.group-needs')).toBeVisible();
  await expect(head.locator('.group-unread')).toBeHidden();
});
