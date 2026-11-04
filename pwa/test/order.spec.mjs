// The person's order (#164, ui.md §1 principle 2 and §6.1), in demo mode: rows never move on
// their own; Alt+Up/Down, a card lifted by its grip and the row menu (a long press on a phone, a
// right-click) move them and pin them, and every move survives a reload; a new session lands at
// the top once; a row not open with news gets the dot; ended sessions fold into Ended · N; and the
// sort menu offers Most recent and Machine.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CLAUDE = '6f1c2b7e-claude-example';
const CODEX = '019a1b2c-codex-example';
const DELEGATING = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const FIRST = [COPILOT, CLAUDE, CODEX, DELEGATING];

// A session's row in the list (#179: rows, and the session's view beside them).
const card = (page, id) => page.locator(`.session[data-id="${id}"]`);
const order = (page) => page.locator('.session').evaluateAll((els) => els.map((e) => e.dataset.id));
let tick = 0;
const later = () => new Date(Date.now() + 1000 + 1000 * ++tick).toISOString();

async function openDemo(page, query = '', first = FIRST) {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
  if (first) { await expect.poll(() => order(page)).toEqual(first); }
}

async function reload(page) {
  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
}

async function upsert(page, id, change) {
  await page.evaluate(({ id, change }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id) ?? { id };
    d.upsert({ ...s, ...change, activity: { ...(s.activity ?? {}), ...(change.activity ?? {}) } });
  }, { id, change });
}

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test('three sessions updating in turn never move a row', async ({ page }) => {
  await openDemo(page);
  for (const id of [DELEGATING, CODEX, CLAUDE, DELEGATING]) {
    await upsert(page, id, { updatedAt: later(), activity: { response: `News from ${id}` } });
    await expect(view(page, id).locator('.response')).toContainText(`News from ${id}`);
    expect(await order(page)).toEqual(FIRST);
  }
});

test('a new session lands at the top once, and stays there as others update', async ({ page }) => {
  await openDemo(page);
  await upsert(page, 'demo-fresh', { agent: 'copilot', title: 'Fresh session', status: 'idle', startedAt: later(), updatedAt: later() });
  await expect.poll(() => order(page)).toEqual(['demo-fresh', ...FIRST]);
  await upsert(page, DELEGATING, { updatedAt: later() });
  await upsert(page, 'demo-fresh', { updatedAt: later(), status: 'working' });
  await expect(card(page, 'demo-fresh')).toHaveAttribute('data-status', 'working');
  expect(await order(page)).toEqual(['demo-fresh', ...FIRST]);
  // Once: moved down, it stays where it was put however often it updates.
  await card(page, 'demo-fresh').locator('.row-main').press('Alt+ArrowDown');
  await upsert(page, 'demo-fresh', { updatedAt: later() });
  await reload(page);
  await expect.poll(() => order(page)).toEqual([COPILOT, CLAUDE, CODEX, DELEGATING]);
});

test('Alt+Up and Alt+Down move a row from its title, keep focus on it, and the order survives a reload', async ({ page }) => {
  await openDemo(page);
  const fold = card(page, COPILOT).locator('.row-main');
  await fold.focus();
  await page.keyboard.press('Alt+ArrowDown');
  await page.keyboard.press('Alt+ArrowDown');
  expect(await order(page)).toEqual([CLAUDE, CODEX, COPILOT, DELEGATING]);
  await expect(fold).toBeFocused();
  // Not past either end: a move there moves nothing, and says so.
  await card(page, CLAUDE).locator('.row-main').press('Alt+ArrowUp');
  await expect(page.locator('#session-list + [role="status"]')).toHaveText('Top of Sessions.');
  await card(page, DELEGATING).locator('.row-main').press('Alt+ArrowUp');
  expect(await order(page)).toEqual([CLAUDE, CODEX, DELEGATING, COPILOT]);
  await expect(page.locator('#session-list + [role="status"]')).toHaveText('Moved to position 3 of 4 in Sessions.');
  await reload(page);
  await expect.poll(() => order(page)).toEqual([CLAUDE, CODEX, DELEGATING, COPILOT]);
});

test('right-click, or Shift+F10, opens the row menu: pin and unpin, with pinned rows above the rest', async ({ page }) => {
  await openDemo(page);
  await card(page, CODEX).locator('.row-main').click({ button: 'right' });
  const menu = card(page, CODEX).getByRole('menu');
  await expect(menu.getByRole('menuitem')).toHaveText(['Move up', 'Move down', 'Pin']);
  await expect(menu.getByRole('menuitem', { name: 'Move up' })).toBeFocused();
  await menu.getByRole('menuitem', { name: 'Pin' }).click();
  await expect(menu).toHaveCount(0);
  await expect.poll(() => order(page)).toEqual([CODEX, COPILOT, CLAUDE, DELEGATING]);
  await expect(page.locator('.list-group')).toHaveText(['Pinned', 'Sessions']);
  await expect(card(page, CODEX)).toHaveAttribute('data-pinned', 'true');

  await reload(page);
  await expect.poll(() => order(page)).toEqual([CODEX, COPILOT, CLAUDE, DELEGATING]);
  await expect(card(page, CODEX)).toHaveAttribute('data-pinned', 'true');

  // From the keyboard: the context-menu key, arrows, Enter.
  await card(page, CODEX).locator('.row-main').focus();
  await page.keyboard.press('Shift+F10');
  const again = card(page, CODEX).getByRole('menu');
  await expect(again).toBeVisible();
  // A pinned row alone in its group can't move; only Unpin is offered.
  await expect(again.getByRole('menuitem', { name: 'Unpin' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect.poll(() => order(page)).toEqual([CODEX, COPILOT, CLAUDE, DELEGATING]);
  await expect(card(page, CODEX)).not.toHaveAttribute('data-pinned', 'true');
  await expect(page.locator('.list-group')).toHaveCount(0);
  await expect(card(page, CODEX).locator('.row-main')).toBeFocused();

  // Escape closes the menu and gives focus back.
  await page.keyboard.press('Shift+F10');
  await expect(card(page, CODEX).getByRole('menu')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(card(page, CODEX).getByRole('menu')).toHaveCount(0);
  await expect(card(page, CODEX).locator('.row-main')).toBeFocused();
});

test('a row not open gets the new activity dot when its session updates, and opening it clears the dot', async ({ page }) => {
  await openDemo(page);
  const codex = card(page, CODEX);
  await expect(view(page, CODEX)).toBeHidden();
  await expect(codex.locator('.unread-dot')).toHaveCount(0);
  await upsert(page, CODEX, { updatedAt: later(), status: 'working' });
  await expect(codex.locator('.unread-dot')).toBeVisible();
  await expect(codex.locator('.row-main')).toHaveAccessibleName(/New activity/);
  // In place of its age, and its title in semibold.
  await expect(codex.locator('.row-age')).toBeHidden();
  expect(await codex.locator('.row-title').evaluate((e) => getComputedStyle(e).fontWeight)).toBe('600');
  // An open session's news is seen as it comes.
  await open(page, CLAUDE);
  await upsert(page, CLAUDE, { updatedAt: later() });
  await expect(card(page, CLAUDE).locator('.unread-dot')).toHaveCount(0);
  // It goes once the session is opened, from its row or from outside (a notification's link).
  await codex.locator('.row-main').click();
  await expect(codex.locator('.unread-dot')).toHaveCount(0);
  await open(page, CLAUDE);
  await upsert(page, CODEX, { updatedAt: later() });
  await expect(codex.locator('.unread-dot')).toBeVisible();
  await page.evaluate((id) => window.agentBridgeDemo.page.showSession(id), CODEX);
  await expect(view(page, CODEX)).toBeVisible();
  await expect(codex.locator('.unread-dot')).toHaveCount(0);
  expect(await order(page)).toEqual(FIRST);
});

test('news that comes while another page shows is not marked seen until Sessions shows again', async ({ page }) => {
  await openDemo(page);
  await open(page, CLAUDE);
  const seen = () => page.evaluate((k) => JSON.parse(localStorage.getItem('demo:agent-bridge.app.order') ?? '{}').seen?.[k], `demo\n${CLAUDE}`);
  await page.locator('#nav [data-nav="machines"]').click();
  await expect(page.locator('#sessions')).toBeHidden();
  const t = later();
  await upsert(page, CLAUDE, { updatedAt: t });
  await page.waitForTimeout(100);
  expect(await seen()).not.toBe(t);
  await page.locator('#nav [data-nav="sessions"]').click();
  await expect.poll(seen).toBe(t);
});

test('ended sessions fold into Ended · N at the bottom', async ({ page }) => {
  await openDemo(page);
  await page.evaluate((ids) => { for (const id of ids) { window.agentBridgeDemo.remove(id); } }, [CLAUDE, DELEGATING]);
  await expect(page.locator('.session')).toHaveCount(2);
  const ended = page.locator('#session-list > details.ended-group');
  await expect(page.locator('#session-list > *').last()).toHaveClass(/ended-group/);
  await expect(ended.locator('summary')).toHaveText('Ended \u00b7 2');
  await expect(ended).not.toHaveAttribute('open', '');
  await expect(ended.locator('.ended-title').first()).toBeHidden();
  await ended.locator('summary').click();
  await expect(ended.locator('.ended-title')).toHaveText(['Land the P1 slices', 'Refactor the relay client']);
  expect((await ended.locator('.ended').evaluateAll((els) => els.map((e) => e.dataset.id)))).toEqual([DELEGATING, CLAUDE]);
  await expect(ended.locator('.ended-meta').first()).toHaveText(/^ended /);
});

test('the sort menu offers Most recent and Machine, and remembers the choice', async ({ page }) => {
  await openDemo(page, '&machines=2', null);
  const sort = page.locator('#session-sort');
  await expect(sort.locator('option')).toHaveText(['Your order', 'Most recent', 'Machine']);
  await expect(sort).toHaveValue('yours');
  await sort.selectOption('machine');
  const hosts = await page.locator('.session').evaluateAll((els) => els.map((e) => e.dataset.hostId));
  // Demo machine, then Laptop: by name, each machine's rows together.
  expect(hosts).toEqual([...hosts].sort((a, b) => (a === 'demo' ? 0 : 1) - (b === 'demo' ? 0 : 1)));
  // Rows move only in your order: no handle, and the menu's moves are off.
  await card(page, CODEX).locator('.row-main').click({ button: 'right' });
  await expect(card(page, CODEX).getByRole('menuitem', { name: 'Move up' })).toBeDisabled();
  await page.keyboard.press('Escape');
  await reload(page);
  await expect(sort).toHaveValue('machine');
});

test('blocked storage lists sessions most recent first, and Your order cannot be picked', async ({ page }) => {
  await page.addInitScript(() => {
    const get = Storage.prototype.getItem;
    Storage.prototype.getItem = function getItem(k) {
      if (String(k).endsWith('agent-bridge.app.order')) { throw new DOMException('blocked', 'SecurityError'); }
      return get.call(this, k);
    };
  });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  const sort = page.locator('#session-sort');
  await expect(sort).toHaveValue('recent');
  await expect(sort.locator('option[value="yours"]')).toBeDisabled();
  await upsert(page, CODEX, { updatedAt: later() });
  await expect.poll(() => order(page)).toEqual([CODEX, COPILOT, CLAUDE, DELEGATING]);
});

test('storage that fills up while a row is drawn turns the list to Most recent at once', async ({ page }) => {
  await page.addInitScript(() => {
    window.__full = false;
    const set = Storage.prototype.setItem;
    Storage.prototype.setItem = function setItem(k, v) {
      if (window.__full && String(k).endsWith('agent-bridge.app.order')) { throw new DOMException('full', 'QuotaExceededError'); }
      return set.call(this, k, v);
    };
  });
  await openDemo(page);
  await open(page, CODEX);
  await page.evaluate(() => { window.__full = true; });
  // An open session's update marks it seen, and that write is the one that fails.
  await upsert(page, CODEX, { updatedAt: later() });
  await expect(page.locator('#session-sort')).toHaveValue('recent');
  await expect.poll(() => order(page)).toEqual([CODEX, COPILOT, CLAUDE, DELEGATING]);
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });

  async function longPress(page, id, holdMs = 650) {
    const fold = card(page, id).locator('.row-main');
    const box = await fold.boundingBox();
    const at = { clientX: box.x + box.width / 2, clientY: box.y + box.height / 2, pointerId: 7, pointerType: 'touch', isPrimary: true, bubbles: true };
    await fold.dispatchEvent('pointerdown', at);
    await page.waitForTimeout(holdMs);
    await fold.dispatchEvent('pointerup', at);
  }

  test('a press held long after the menu opens still does not open the session when the finger lifts', async ({ page }) => {
    await openDemo(page);
    await longPress(page, CODEX, 1800);
    await expect(card(page, CODEX).getByRole('menu')).toBeVisible();
    // The click a real finger's release brings.
    await card(page, CODEX).locator('.row-main').dispatchEvent('click', { detail: 1 });
    await expect(view(page, CODEX)).toBeHidden();
    await expect(card(page, CODEX).getByRole('menu')).toBeVisible();
  });

  test('a long press opens Move up, Move down and Pin, and the moves survive a reload', async ({ page }) => {
    await openDemo(page);
    await longPress(page, CODEX);
    const menu = card(page, CODEX).getByRole('menu');
    await expect(menu.getByRole('menuitem')).toHaveText(['Move up', 'Move down', 'Pin']);
    // The press doesn't open the session, and the menu fits on the screen with a finger-sized row.
    await expect(view(page, CODEX)).toBeHidden();
    const box = await menu.boundingBox();
    expect(box.x).toBeGreaterThanOrEqual(0);
    expect(box.x + box.width).toBeLessThanOrEqual(390);
    expect((await menu.getByRole('menuitem').first().boundingBox()).height).toBeGreaterThanOrEqual(44);
    await menu.getByRole('menuitem', { name: 'Move up' }).tap();
    await expect.poll(() => order(page)).toEqual([COPILOT, CODEX, CLAUDE, DELEGATING]);
    await longPress(page, DELEGATING);
    await card(page, DELEGATING).getByRole('menuitem', { name: 'Pin' }).tap();
    await expect.poll(() => order(page)).toEqual([DELEGATING, COPILOT, CODEX, CLAUDE]);
    await reload(page);
    await expect.poll(() => order(page)).toEqual([DELEGATING, COPILOT, CODEX, CLAUDE]);
    // A short tap still opens the session.
    await card(page, CODEX).locator('.row-main').tap();
    await expect(view(page, CODEX)).toBeVisible();
    await expect(card(page, CODEX).getByRole('menu')).toHaveCount(0);
  });

  test('a row menu by the bottom navigation opens above its row, clear of the bar, and Pin is pressed there', async ({ page }) => {
    await openDemo(page);
    const fold = card(page, DELEGATING).locator('.row-main');
    // The last row's title just above the bar, where a menu below it would be under the bar.
    await fold.evaluate((el) => { window.scrollBy(0, el.getBoundingClientRect().bottom - document.getElementById('nav').getBoundingClientRect().top + 4); });
    const barTop = await page.locator('#nav').evaluate((el) => el.getBoundingClientRect().top);
    expect((await fold.boundingBox()).y + (await fold.boundingBox()).height).toBeLessThanOrEqual(barTop);
    await longPress(page, DELEGATING);
    const menu = card(page, DELEGATING).getByRole('menu');
    await expect(menu.getByRole('menuitem', { name: 'Pin' })).toBeVisible();
    const box = await menu.boundingBox();
    expect(box.y).toBeGreaterThanOrEqual(0);
    expect(box.y + box.height).toBeLessThanOrEqual(barTop);
    await menu.getByRole('menuitem', { name: 'Pin' }).tap();
    await expect.poll(() => order(page)).toEqual([DELEGATING, COPILOT, CLAUDE, CODEX]);
  });

  test('the sort menu sits in the sessions header without crowding it', async ({ page }) => {
    await openDemo(page);
    const head = await page.locator('.sessions-head').boundingBox();
    const sort = await page.locator('#session-sort').boundingBox();
    expect(sort.x + sort.width).toBeLessThanOrEqual(head.x + head.width + 0.5);
    expect(sort.height).toBeGreaterThanOrEqual(44);
    // On touch the grip shows on every card in Your order (#592, D5).
    await expect(page.locator('.drag-handle').first()).toBeVisible();
  });
});

for (const width of [900, 1440]) {
  test.describe(`at ${width} px`, () => {
    test.use({ viewport: { width, height: 900 } });

    test('a card dragged by its grip is lifted, the cards it passes make room, and it lands in the gap, staying there after a reload', async ({ page }) => {
      await openDemo(page);
      // Rows are two lines (#179): every one fits on the screen at once.
      const handle = card(page, DELEGATING).locator('.drag-handle');
      await card(page, DELEGATING).hover();
      await expect(handle).toBeVisible();
      const from = await handle.boundingBox();
      const target = await card(page, CLAUDE).boundingBox();
      await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
      await page.mouse.down();
      // The grip lifts it at once, with no insertion line anywhere.
      await expect(card(page, DELEGATING)).toHaveClass(/\blifted\b/);
      await page.mouse.move(target.x + target.width / 2, target.y + 10, { steps: 6 });
      // Claude and Codex eased down a place: the gap they leave is where it lands.
      await expect.poll(() => card(page, CLAUDE).evaluate((e) => new DOMMatrix(getComputedStyle(e).transform).m42)).toBeGreaterThan(target.height - 1);
      await expect(page.locator('.drag-gap')).toHaveCount(1);
      await page.mouse.up();
      await expect.poll(() => order(page)).toEqual([COPILOT, DELEGATING, CLAUDE, CODEX]);
      await expect(page.locator('.lifted, .drag-gap')).toHaveCount(0);
      await expect(page.locator('#session-list + [role="status"]')).toHaveText('Moved to position 2 of 4 in Sessions.');
      // The row dropped settles into its place (ui.md §5.3): measured once it is there.
      await page.waitForFunction(() => !document.getAnimations().some((a) => a.id === 'ab-motion-reorder' && a.playState === 'running'));

      // Escape puts a drag back.
      await card(page, CLAUDE).hover();
      const again = await card(page, CLAUDE).locator('.drag-handle').boundingBox();
      const onto = await card(page, DELEGATING).boundingBox();
      await page.mouse.move(again.x + again.width / 2, again.y + again.height / 2);
      await page.mouse.down();
      await page.mouse.move(onto.x + onto.width / 2, onto.y + 10, { steps: 6 });
      await expect.poll(() => card(page, DELEGATING).evaluate((e) => new DOMMatrix(getComputedStyle(e).transform).m42)).toBeGreaterThan(onto.height - 1);
      await page.keyboard.press('Escape');
      await expect(page.locator('#session-list + [role="status"]')).toHaveText('Cancelled. Back at position 3 of 4.');
      await page.mouse.up();
      expect(await order(page)).toEqual([COPILOT, DELEGATING, CLAUDE, CODEX]);

      await reload(page);
      await expect.poll(() => order(page)).toEqual([COPILOT, DELEGATING, CLAUDE, CODEX]);
    });

    test('the sort menu sits at the end of the sessions header', async ({ page }) => {
      await openDemo(page);
      const head = await page.locator('.sessions-head').boundingBox();
      const sort = await page.locator('#session-sort').boundingBox();
      expect(sort.x + sort.width).toBeLessThanOrEqual(head.x + head.width + 0.5);
      expect(sort.x).toBeGreaterThan(head.x + head.width / 2);
    });

    test('in high contrast the grip, the lifted card and the gap keep system colours', async ({ page, browserName }) => {
      test.skip(browserName !== 'chromium', 'forced colours are emulated in Chromium only');
      await page.emulateMedia({ forcedColors: 'active' });
      await openDemo(page);
      await card(page, CLAUDE).hover();
      const grip = card(page, CLAUDE).locator('.drag-handle');
      expect(await grip.evaluate((e) => getComputedStyle(e).forcedColorAdjust)).toBe('none');
      expect(await grip.evaluate((e) => getComputedStyle(e, '::before').forcedColorAdjust)).toBe('none');
      const box = await grip.boundingBox();
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      await page.mouse.down();
      await expect(card(page, CLAUDE)).toHaveClass(/\blifted\b/);
      const lifted = await card(page, CLAUDE).evaluate((e) => { const s = getComputedStyle(e); return [s.outlineStyle, s.outlineWidth, s.boxShadow]; });
      expect(lifted).toEqual(['solid', '2px', 'none']);
      expect(await page.locator('.drag-gap').evaluate((e) => getComputedStyle(e).outlineStyle)).toBe('dashed');
      await page.keyboard.press('Escape');
      await page.mouse.up();
    });

    test('group headings and Ended span the whole list, never sharing a line with a row', async ({ page }) => {
      await openDemo(page);
      await card(page, CODEX).locator('.row-main').click({ button: 'right' });
      await card(page, CODEX).getByRole('menuitem', { name: 'Pin' }).click();
      await page.evaluate((id) => window.agentBridgeDemo.remove(id), CLAUDE);
      await expect(page.locator('.ended-group')).toBeVisible();
      const list = await page.locator('#session-list').boundingBox();
      for (const el of [page.locator('.list-group').first(), page.locator('.list-group').last(), page.locator('.ended-group')]) {
        const box = await el.boundingBox();
        expect(Math.abs(box.width - list.width)).toBeLessThan(1);
      }
    });
  });
}
