// The person's order (#164, ui.md §1 principle 2 and §6.1), in demo mode: rows never move on
// their own; Alt+Up/Down, a drag by the handle and the row menu (a long press on a phone, a
// right-click) move them and pin them, and every move survives a reload; a new session lands at
// the top once; a folded row with news gets the dot; ended sessions fold into Ended · N; and the
// sort menu offers Most recent and Machine.
import { test, expect } from '@playwright/test';

const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CLAUDE = '6f1c2b7e-claude-example';
const CODEX = '019a1b2c-codex-example';
const DELEGATING = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const FIRST = [COPILOT, CLAUDE, CODEX, DELEGATING];

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
    await expect(card(page, id).locator('.response')).toContainText(`News from ${id}`);
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
  await card(page, 'demo-fresh').locator('.fold').press('Alt+ArrowDown');
  await upsert(page, 'demo-fresh', { updatedAt: later() });
  await reload(page);
  await expect.poll(() => order(page)).toEqual([COPILOT, CLAUDE, CODEX, DELEGATING]);
});

test('Alt+Up and Alt+Down move a row from its title, keep focus on it, and the order survives a reload', async ({ page }) => {
  await openDemo(page);
  const fold = card(page, COPILOT).locator('.fold');
  await fold.focus();
  await page.keyboard.press('Alt+ArrowDown');
  await page.keyboard.press('Alt+ArrowDown');
  expect(await order(page)).toEqual([CLAUDE, CODEX, COPILOT, DELEGATING]);
  await expect(fold).toBeFocused();
  // Not past either end.
  await card(page, CLAUDE).locator('.fold').press('Alt+ArrowUp');
  await card(page, DELEGATING).locator('.fold').press('Alt+ArrowUp');
  expect(await order(page)).toEqual([CLAUDE, CODEX, DELEGATING, COPILOT]);
  await expect(page.locator('#session-list + [role="status"]')).toHaveText('Moved up');
  await reload(page);
  await expect.poll(() => order(page)).toEqual([CLAUDE, CODEX, DELEGATING, COPILOT]);
});

test('right-click, or Shift+F10, opens the row menu: pin and unpin, with pinned rows above the rest', async ({ page }) => {
  await openDemo(page);
  await card(page, CODEX).locator('.fold').click({ button: 'right' });
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
  await card(page, CODEX).locator('.fold').focus();
  await page.keyboard.press('Shift+F10');
  const again = card(page, CODEX).getByRole('menu');
  await expect(again).toBeVisible();
  // A pinned row alone in its group can't move; only Unpin is offered.
  await expect(again.getByRole('menuitem', { name: 'Unpin' })).toBeFocused();
  await page.keyboard.press('Enter');
  await expect.poll(() => order(page)).toEqual([CODEX, COPILOT, CLAUDE, DELEGATING]);
  await expect(card(page, CODEX)).not.toHaveAttribute('data-pinned', 'true');
  await expect(page.locator('.list-group')).toHaveCount(0);
  await expect(card(page, CODEX).locator('.fold')).toBeFocused();

  // Escape closes the menu and gives focus back.
  await page.keyboard.press('Shift+F10');
  await expect(card(page, CODEX).getByRole('menu')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(card(page, CODEX).getByRole('menu')).toHaveCount(0);
  await expect(card(page, CODEX).locator('.fold')).toBeFocused();
});

test('a folded row gets the new activity dot when its session updates, and opening it clears the dot', async ({ page }) => {
  await openDemo(page);
  const codex = card(page, CODEX);
  await codex.locator('.fold').click();
  await expect(codex.locator('.fold')).toHaveAttribute('aria-expanded', 'false');
  await expect(codex.locator('.unread-dot')).toHaveCount(0);
  await upsert(page, CODEX, { updatedAt: later(), status: 'working' });
  await expect(codex.locator('.unread-dot')).toBeVisible();
  await expect(codex.locator('.fold')).toHaveAccessibleName(/New activity/);
  // An open row's news is seen as it comes.
  await upsert(page, CLAUDE, { updatedAt: later() });
  await expect(card(page, CLAUDE).locator('.unread-dot')).toHaveCount(0);
  // It goes once the row is opened, by its title or from outside (a notification's link).
  await codex.locator('.fold').click();
  await expect(codex.locator('.unread-dot')).toHaveCount(0);
  await codex.locator('.fold').click();
  await upsert(page, CODEX, { updatedAt: later() });
  await expect(codex.locator('.unread-dot')).toBeVisible();
  await page.evaluate((id) => window.agentBridgeDemo.page.showSession(id), CODEX);
  await expect(codex.locator('.fold')).toHaveAttribute('aria-expanded', 'true');
  await expect(codex.locator('.unread-dot')).toHaveCount(0);
  expect(await order(page)).toEqual(FIRST);
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
  await card(page, CODEX).locator('.fold').click({ button: 'right' });
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

test('storage that fills up while a card is drawn turns the list to Most recent at once', async ({ page }) => {
  await page.addInitScript(() => {
    window.__full = false;
    const set = Storage.prototype.setItem;
    Storage.prototype.setItem = function setItem(k, v) {
      if (window.__full && String(k).endsWith('agent-bridge.app.order')) { throw new DOMException('full', 'QuotaExceededError'); }
      return set.call(this, k, v);
    };
  });
  await openDemo(page);
  await page.evaluate(() => { window.__full = true; });
  // An open card's update marks it seen, and that write is the one that fails.
  await upsert(page, CODEX, { updatedAt: later() });
  await expect(page.locator('#session-sort')).toHaveValue('recent');
  await expect.poll(() => order(page)).toEqual([CODEX, COPILOT, CLAUDE, DELEGATING]);
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });

  async function longPress(page, id, holdMs = 650) {
    const fold = card(page, id).locator('.fold');
    const box = await fold.boundingBox();
    const at = { clientX: box.x + box.width / 2, clientY: box.y + box.height / 2, pointerId: 7, pointerType: 'touch', isPrimary: true, bubbles: true };
    await fold.dispatchEvent('pointerdown', at);
    await page.waitForTimeout(holdMs);
    await fold.dispatchEvent('pointerup', at);
  }

  test('a press held long after the menu opens still does not fold the card when the finger lifts', async ({ page }) => {
    await openDemo(page);
    await longPress(page, CODEX, 1800);
    await expect(card(page, CODEX).getByRole('menu')).toBeVisible();
    // The click a real finger's release brings.
    await card(page, CODEX).locator('.fold').dispatchEvent('click');
    await expect(card(page, CODEX).locator('.fold')).toHaveAttribute('aria-expanded', 'true');
  });

  test('a long press opens Move up, Move down and Pin, and the moves survive a reload', async ({ page }) => {
    await openDemo(page);
    await longPress(page, CODEX);
    const menu = card(page, CODEX).getByRole('menu');
    await expect(menu.getByRole('menuitem')).toHaveText(['Move up', 'Move down', 'Pin']);
    // The press doesn't fold the card, and the menu fits on the screen with a finger-sized row.
    await expect(card(page, CODEX).locator('.fold')).toHaveAttribute('aria-expanded', 'true');
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
    // A short tap still folds.
    await card(page, CODEX).locator('.fold').tap();
    await expect(card(page, CODEX).locator('.fold')).toHaveAttribute('aria-expanded', 'false');
    await expect(card(page, CODEX).getByRole('menu')).toHaveCount(0);
  });

  test('the sort menu sits in the sessions header without crowding it', async ({ page }) => {
    await openDemo(page);
    const head = await page.locator('.sessions-head').boundingBox();
    const sort = await page.locator('#session-sort').boundingBox();
    expect(sort.x + sort.width).toBeLessThanOrEqual(head.x + head.width + 0.5);
    expect(sort.height).toBeGreaterThanOrEqual(44);
    await expect(page.locator('.drag-handle').first()).toBeHidden();
  });
});

for (const width of [900, 1440]) {
  test.describe(`at ${width} px`, () => {
    test.use({ viewport: { width, height: 900 } });

    test('a row dragged by its handle goes where it is dropped, and stays there after a reload', async ({ page }) => {
      await openDemo(page);
      // Folded, every row fits on the screen at once.
      for (const id of FIRST) { await card(page, id).locator('.fold').click(); }
      const handle = card(page, DELEGATING).locator('.drag-handle');
      await card(page, DELEGATING).hover();
      await expect(handle).toBeVisible();
      const from = await handle.boundingBox();
      const target = await card(page, CLAUDE).boundingBox();
      await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
      await page.mouse.down();
      await page.mouse.move(target.x + target.width / 2, target.y + 10, { steps: 6 });
      await expect(card(page, CLAUDE)).toHaveClass(/\bdrop-before\b/);
      await page.mouse.up();
      await expect.poll(() => order(page)).toEqual([COPILOT, DELEGATING, CLAUDE, CODEX]);
      await expect(page.locator('.drop-before, .drop-after, .dragging')).toHaveCount(0);

      // Escape puts a drag back.
      await card(page, CLAUDE).hover();
      const again = await card(page, CLAUDE).locator('.drag-handle').boundingBox();
      const onto = await card(page, DELEGATING).boundingBox();
      await page.mouse.move(again.x + again.width / 2, again.y + again.height / 2);
      await page.mouse.down();
      await page.mouse.move(onto.x + onto.width / 2, onto.y + 10, { steps: 6 });
      await expect(card(page, DELEGATING)).toHaveClass(/\bdrop-before\b/);
      await page.keyboard.press('Escape');
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

    test('in high contrast the grip and the drop line keep system colours', async ({ page, browserName }) => {
      test.skip(browserName !== 'chromium', 'forced colours are emulated in Chromium only');
      await page.emulateMedia({ forcedColors: 'active' });
      await openDemo(page);
      await card(page, CLAUDE).hover();
      const grip = card(page, CLAUDE).locator('.drag-handle');
      expect(await grip.evaluate((e) => getComputedStyle(e).forcedColorAdjust)).toBe('none');
      await card(page, CLAUDE).evaluate((e) => e.classList.add('drop-before'));
      expect(await card(page, CLAUDE).evaluate((e) => getComputedStyle(e, '::before').forcedColorAdjust)).toBe('none');
    });

    test('group headings and Ended span the whole list, never sharing a row with a card', async ({ page }) => {
      await openDemo(page);
      await card(page, CODEX).locator('.fold').click({ button: 'right' });
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
