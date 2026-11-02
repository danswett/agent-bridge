// The shell (docs/design/ui.md §7, PWA; #179): on a desktop, the web app bar and the rail; on a
// phone, the page's title row and the bottom navigation. Every page is a route, so a reload stays
// on it and the browser's own back goes between them.
import { test, expect } from '@playwright/test';
import { go, open } from './nav.mjs';

const CODEX = '019a1b2c-codex-example';
const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

const overflow = (page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
const route = (page) => page.evaluate(() => document.body.dataset.page);

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

for (const width of [900, 1440]) {
  test.describe(`at ${width} px`, () => {
    test.use({ viewport: { width, height: 900 } });

    test('a 56 px app bar holds the mark and name, search, machines online and the connection, over a rail of every page', async ({ page }) => {
      await openDemo(page, '&machines=2');
      const bar = await page.locator('#appbar').boundingBox();
      expect(bar.height).toBe(56);
      expect(bar.y).toBe(0);
      await expect(page.locator('.brand')).toBeVisible();
      await expect(page.locator('.brand-name')).toHaveText('Agent Bridge');
      await expect(page.locator('#search')).toBeVisible();
      await expect(page.locator('#online')).toHaveText('2 machines');
      await expect(page.locator('#conn')).toHaveText('Demo');
      const nav = page.locator('#nav');
      await expect(nav.locator('.nav-item')).toHaveCount(5);
      for (const n of ['sessions', 'machines', 'launch', 'usage', 'settings']) { await expect(nav.locator(`[data-nav="${n}"]`)).toBeVisible(); }
      // A rail down the left, Settings at its foot.
      const sessions = await nav.locator('[data-nav="sessions"]').boundingBox();
      const settings = await nav.locator('[data-nav="settings"]').boundingBox();
      expect(sessions.x).toBeLessThan(48);
      expect(settings.x).toBe(sessions.x);
      expect(settings.y).toBeGreaterThan(800);
      await expect(nav.locator('[data-nav="sessions"]')).toHaveAttribute('aria-current', 'page');
      await expect(nav.locator('[data-nav="sessions"]')).toHaveAccessibleName('Sessions');
      expect(await overflow(page)).toBeLessThanOrEqual(0);
    });
  });
}

test('each page is a route: the rail goes there, a reload stays, and the browser\'s back returns', async ({ page }) => {
  await openDemo(page);
  await go(page, 'machines');
  expect(new URL(page.url()).hash).toBe('#/machines');
  await expect(page.locator('#machines')).toBeVisible();
  await expect(page.locator('#session-list')).toBeHidden();
  await go(page, 'launch');
  await expect(page.locator('#launch .launch-title')).toHaveText('Start a session on Demo machine');
  await go(page, 'settings');
  await expect(page.locator('#nav [data-nav="settings"]')).toHaveAttribute('aria-current', 'page');

  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  expect(await route(page)).toBe('settings');
  await page.goBack();
  await expect.poll(() => route(page)).toBe('launch');
  await page.goBack();
  await expect.poll(() => route(page)).toBe('machines');
  await page.goBack();
  await expect.poll(() => route(page)).toBe('sessions');
  await expect(page.locator('#session-list')).toBeVisible();
});

test('Ctrl+K focuses search, which narrows the list to the sessions that match, and Escape clears it', async ({ page }) => {
  await openDemo(page);
  await go(page, 'usage');
  await page.keyboard.press('Control+k');
  await expect(page.locator('#search')).toBeFocused();
  await page.keyboard.type('flaky');
  // Search is the sessions list's: typing takes you there.
  await expect.poll(() => route(page)).toBe('sessions');
  await expect(page.locator('.session')).toHaveCount(1);
  await expect(page.locator(`.session[data-id="${CODEX}"]`)).toBeVisible();
  // While it narrows the list, rows don't move.
  await expect(page.locator('#session-list')).toHaveAttribute('data-filtered', 'true');
  await page.keyboard.type('zzz');
  await expect(page.locator('.session')).toHaveCount(0);
  await expect(page.locator('#sessions-empty')).toHaveText('No sessions match your search.');
  await page.keyboard.press('Escape');
  await expect(page.locator('#search')).toHaveValue('');
  await expect(page.locator('.session')).toHaveCount(4);
  await expect(page.locator('#sessions-empty')).toBeHidden();
});

test('a machine renamed while a search is open brings its sessions into the results at once', async ({ page }) => {
  await openDemo(page);
  await page.keyboard.press('Control+k');
  await page.keyboard.type('workstation');
  await expect(page.locator('.session')).toHaveCount(0);
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const link = d.hosts.link('demo');
    link.machine = { ...link.machine, name: 'Workstation' };
    d.page.refresh();
  });
  await expect(page.locator('.session')).toHaveCount(4);
});

test('a page retired with its account no longer answers the routes', async ({ page }) => {
  await openDemo(page);
  // A relay that stops accepting the sign-in retires the page (clear()); the next sign-in in the
  // same tab makes a new one, and the old must not go on drawing into the shared list.
  const errors = [];
  page.on('pageerror', (e) => errors.push(e.message));
  await page.evaluate(() => {
    const { view, launchPanel } = window.agentBridgeDemo.page;
    window.agentBridgeDemo.page.clear();
    window.__heard = 0;
    for (const [o, f] of [[view, 'schedule'], [launchPanel, 'setOpen']]) {
      const was = o[f].bind(o);
      o[f] = (...a) => { window.__heard += 1; return was(...a); };
    }
  });
  await go(page, 'machines');
  await go(page, 'sessions');
  await page.waitForTimeout(200);
  expect(await page.evaluate(() => window.__heard)).toBe(0);
  expect(errors).toEqual([]);
});

test('a reply being written outlives a search that hides its session, with its text when search lets it back', async ({ page }) => {
  await openDemo(page);
  await open(page, IDLE);
  const input = page.locator(`.session-view[data-id="${IDLE}"] form.reply`).getByRole('textbox', { name: 'Reply' });
  await input.fill('Half a thought');
  await page.keyboard.press('Control+k');
  await page.keyboard.type('flaky');
  await expect(page.locator(`.session[data-id="${IDLE}"]`)).toHaveCount(0);
  await page.keyboard.press('Escape');
  await expect(input).toHaveValue('Half a thought');
});

test('a launch whose new session the search would hide clears the search and opens it', async ({ page }) => {
  await openDemo(page);
  await page.keyboard.press('Control+k');
  await page.keyboard.type('zzz');
  await expect(page.locator('.session')).toHaveCount(0);
  await go(page, 'launch');
  await page.locator('#launch [name="prompt"]').fill('Look at the relay');
  await page.locator('#launch').getByRole('button', { name: 'Launch' }).click();
  await expect(page.locator('#launch')).toHaveAttribute('data-launch', 'started');
  const id = await page.evaluate(() => window.agentBridgeDemo.launches.get('demo').detail.session);
  await expect(page.locator(`.session[data-id="${id}"]`)).toBeInViewport();
  await expect(page.locator('#search')).toHaveValue('');
  await expect(page.locator('.session')).toHaveCount(5);
});

test('Skip to content skips to the page it is on, and leaves the address alone', async ({ page }) => {
  await openDemo(page);
  await go(page, 'machines');
  await page.locator('a.skip').focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('#main')).toBeFocused();
  expect(await route(page)).toBe('machines');
  expect(await page.evaluate(() => location.hash)).toBe('#/machines');
});

test('a search that matches only an ended session shows it in Ended, and does not say nothing matches', async ({ page }) => {
  await openDemo(page);
  await page.evaluate((id) => window.agentBridgeDemo.remove(id), CODEX);
  await expect(page.locator('.ended-group > summary')).toHaveText('Ended \u00b7 1');
  await page.keyboard.press('Control+k');
  await page.keyboard.type('flaky');
  await expect(page.locator('.session')).toHaveCount(0);
  await expect(page.locator('.ended-group > summary')).toHaveText('Ended \u00b7 1');
  await expect(page.locator('#sessions-empty')).toBeHidden();
});

test('a link that leaves its page takes focus to the new page, not to the document', async ({ page }) => {
  await openDemo(page);
  await page.locator('.new-session').focus();
  await page.keyboard.press('Enter');
  await expect.poll(() => route(page)).toBe('launch');
  expect(await page.evaluate(() => document.getElementById('main').contains(document.activeElement))).toBe(true);
  // The rail keeps focus: it is still there.
  await page.locator('#nav [data-nav="usage"]').focus();
  await page.keyboard.press('Enter');
  await expect.poll(() => route(page)).toBe('usage');
  await expect(page.locator('#nav [data-nav="usage"]')).toBeFocused();
});

test('a startup that fails on another page says why on Sessions', async ({ page }) => {
  await page.route('**/fixtures/sessions-list.json', (r) => r.abort());
  await page.goto('?demo=1&static=1#/settings');
  await expect(page.locator('#host-state')).toBeVisible();
  await expect(page.locator('#host-state')).not.toHaveText('');
  expect(await route(page)).toBe('sessions');
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

  test('a 24/32 title row with Search and New, and a bottom bar of Sessions, Machines, Usage and Settings with a Bridge indicator', async ({ page }) => {
    await openDemo(page, '&machines=2');
    // The app bar gives way to the page's own title row while all is well.
    await expect(page.locator('#appbar')).toBeHidden();
    const title = page.locator('#sessions-h');
    expect(await title.evaluate((e) => [getComputedStyle(e).fontSize, getComputedStyle(e).lineHeight, getComputedStyle(e).fontWeight])).toEqual(['24px', '32px', '600']);
    await expect(page.locator('.search-open')).toBeVisible();
    await expect(page.locator('.new-session')).toHaveAccessibleName('New session');
    const head = await page.locator('.sessions-head').boundingBox();
    for (const b of [page.locator('.search-open'), page.locator('.new-session')]) {
      const r = await b.boundingBox();
      expect(r.height).toBeGreaterThanOrEqual(44);
      expect(r.x + r.width).toBeLessThanOrEqual(head.x + head.width + 0.5);
    }
    // Machine tabs under it.
    await expect(page.locator('#machine-tabs [role="tab"]')).toHaveText(['All', 'Demo machine', 'Laptop']);

    const nav = page.locator('#nav');
    const bar = await nav.boundingBox();
    expect(bar.y + bar.height).toBe(844);
    await expect(nav.locator('.nav-item:visible')).toHaveText(['Sessions', 'Machines', 'Usage', 'Settings']);
    const current = nav.locator('[aria-current="page"]');
    await expect(current).toHaveText('Sessions');
    const indicator = await current.evaluate((e) => getComputedStyle(e, '::before').backgroundColor);
    const bridge = await page.evaluate(() => { const p = document.createElement('i'); p.style.color = 'var(--ab-bridge)'; document.body.append(p); const c = getComputedStyle(p).color; p.remove(); return c; });
    expect(indicator).toBe(bridge);
    for (const item of await nav.locator('.nav-item:visible').all()) { expect((await item.boundingBox()).height).toBeGreaterThanOrEqual(44); }
    expect(await overflow(page)).toBeLessThanOrEqual(0);

    // New is the way to Launch.
    await page.locator('.new-session').tap();
    await expect(page.locator('#launch .launch-title')).toHaveText('Start a session on Demo machine');
    await page.goBack();
    await expect.poll(() => route(page)).toBe('sessions');
  });

  test('Search opens a search row over the title, and closing it brings back the whole list', async ({ page }) => {
    await openDemo(page);
    await page.locator('.search-open').tap();
    await expect(page.locator('#appbar')).toBeVisible();
    await expect(page.locator('#search')).toBeFocused();
    await page.keyboard.type('relay');
    await expect(page.locator('.session')).toHaveCount(1);
    await page.locator('#search-close').tap();
    await expect(page.locator('#appbar')).toBeHidden();
    await expect(page.locator('.session')).toHaveCount(4);
  });

  test('the search glyph sits in the box past Close search, not over it', async ({ page }) => {
    await openDemo(page);
    await page.locator('.search-open').tap();
    const glyph = await page.locator('.search-icon').boundingBox();
    const close = await page.locator('#search-close').boundingBox();
    const box = await page.locator('#search').boundingBox();
    expect(glyph.x).toBeGreaterThanOrEqual(close.x + close.width);
    expect(glyph.x).toBeGreaterThanOrEqual(box.x);
    expect(glyph.x + glyph.width).toBeLessThanOrEqual(box.x + box.width);
  });

  test('every machine tab is at least 44 px wide and tall, All included', async ({ page }) => {
    await openDemo(page, '&machines=2');
    for (const t of await page.locator('#machine-tabs [role="tab"]').all()) {
      const b = await t.boundingBox();
      expect(b.width).toBeGreaterThanOrEqual(44);
      expect(b.height).toBeGreaterThanOrEqual(44);
    }
  });

  test('closing search, by Close search or Escape, gives focus back to Search rather than losing it', async ({ page }) => {
    await openDemo(page);
    const open = page.locator('.search-open').filter({ visible: true }).first();
    await open.tap();
    await expect(page.locator('#search')).toBeFocused();
    await page.locator('#search-close').focus();
    await page.keyboard.press('Enter');
    await expect(page.locator('#appbar')).toBeHidden();
    await expect(open).toBeFocused();
    await open.tap();
    await expect(page.locator('#search')).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(open).toBeFocused();
  });

  test('a new page starts at its top, not where the last one was scrolled to', async ({ page }) => {
    await page.setViewportSize({ width: 390, height: 420 });
    await openDemo(page, '&machines=3');
    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    expect(await page.evaluate(() => window.scrollY)).toBeGreaterThan(100);
    await page.locator('#nav [data-nav="settings"]').tap();
    await expect.poll(() => route(page)).toBe('settings');
    expect(await page.evaluate(() => window.scrollY)).toBe(0);
  });

  test('the strip shows while connecting, as it does for the other news', async ({ page }) => {
    await openDemo(page);
    await expect(page.locator('#appbar')).toBeHidden();
    await page.evaluate(() => { document.body.dataset.conn = 'busy'; });
    await expect(page.locator('#appbar')).toBeVisible();
  });

  test('the app bar comes back as a strip while the connection has news', async ({ page }) => {
    await openDemo(page);
    await expect(page.locator('#appbar')).toBeHidden();
    await page.evaluate(() => { document.body.dataset.conn = 'warn'; document.getElementById('conn').textContent = 'Reconnecting…'; });
    await expect(page.locator('#conn')).toBeVisible();
    await expect(page.locator('.brand')).toBeHidden();
  });
});

test.describe('at a desktop\'s width on a touch screen', () => {
  test.use({ viewport: { width: 1280, height: 800 }, hasTouch: true });

  test('the rail, the machine tabs, search, Account and New are at least 44 px to a finger', async ({ page }) => {
    await openDemo(page, '&machines=2');
    test.skip(!(await page.evaluate(() => matchMedia('(pointer: coarse)').matches)), 'this engine does not emulate a coarse pointer');
    const all = [
      ...await page.locator('#nav .nav-item').filter({ visible: true }).all(),
      ...await page.locator('#machine-tabs [role="tab"]').all(),
      page.locator('.new-session').filter({ visible: true }).first(),
      page.locator('.appbar .brand'), page.locator('#online'),
    ];
    // Account shows only signed in, with a login; demo mode has none, so give it one.
    await page.evaluate(() => { const a = document.getElementById('avatar'); a.hidden = false; a.textContent = 'D'; });
    all.push(page.locator('#avatar'));
    expect(all.length).toBeGreaterThan(6);
    for (const c of all) {
      const b = await c.boundingBox();
      expect(b.width, await c.evaluate((e) => e.outerHTML.slice(0, 80))).toBeGreaterThanOrEqual(44);
      expect(b.height).toBeGreaterThanOrEqual(44);
    }
    expect((await page.locator('#search').boundingBox()).height).toBeGreaterThanOrEqual(44);
  });
});

test('with one machine and no machine tabs, the page still names the machine its sessions are on', async ({ page }) => {
  await openDemo(page);
  await expect(page.locator('#machine-tabs')).toBeHidden();
  await expect(page.locator('#session-count')).toBeVisible();
  await expect(page.locator('#session-count')).toContainText('on Demo machine');
});

test('a machine tab with focus that takes the tabs away leaves focus on the sessions, not the document', async ({ page }) => {
  await openDemo(page, '&machines=2');
  await page.locator('#machine-tabs [data-device-id="demo-laptop"]').click();
  await page.evaluate(() => window.agentBridgeDemo.setOnline('demo-laptop', false));
  // The laptop is remembered offline; the demo machine is the only one online.
  const demo = page.locator('#machine-tabs [data-device-id="demo"]');
  await demo.focus();
  await page.keyboard.press('Enter');
  await expect(page.locator('#machine-tabs')).toBeHidden();
  await expect(page.locator('#sessions')).toBeFocused();
});

test('at 760 px signed in, search sits between the name and the connection, machines online and account, overlapping neither', async ({ page }) => {
  await page.setViewportSize({ width: 760, height: 800 });
  await openDemo(page, '&machines=3');
  await page.evaluate(() => { const a = document.getElementById('avatar'); a.hidden = false; a.textContent = 'D'; document.getElementById('conn').textContent = 'Connected'; });
  const brand = await page.locator('.appbar .brand').boundingBox();
  const search = await page.locator('.appbar .search').boundingBox();
  const end = await page.locator('.appbar .appbar-end').boundingBox();
  const avatar = await page.locator('#avatar').boundingBox();
  expect(brand.x + brand.width).toBeLessThanOrEqual(search.x);
  expect(search.x + search.width).toBeLessThanOrEqual(end.x);
  expect(avatar.x + avatar.width).toBeLessThanOrEqual(760);
  expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(0);
});

test('the name and machines online in the app bar are at least 32 px tall to a mouse', async ({ page }) => {
  await openDemo(page);
  for (const c of [page.locator('.appbar .brand'), page.locator('#online')]) { expect((await c.boundingBox()).height).toBeGreaterThanOrEqual(32); }
});

test('on a desktop, searching or a connection with news never moves the page under the bar', async ({ page }) => {
  await openDemo(page);
  const top = () => page.locator('#main > :not([hidden])').first().evaluate((e) => e.getBoundingClientRect().top);
  const before = await top();
  await page.keyboard.press('Control+k');
  await expect(page.locator('#search')).toBeFocused();
  expect(await top()).toBe(before);
  await page.evaluate(() => { document.body.dataset.conn = 'warn'; });
  expect(await top()).toBe(before);
});

test('signed out, there is only the sign-in page: no navigation, no search', async ({ page }) => {
  await page.goto('./');
  await expect(page.locator('#signin')).toBeVisible();
  await expect(page.locator('#nav')).toBeHidden();
  await expect(page.locator('#search')).toBeHidden();
  await expect(page.locator('#online')).toBeHidden();
  await expect(page.locator('#avatar')).toBeHidden();
  await expect(page.locator('[data-route]:visible')).toHaveCount(0);
});
