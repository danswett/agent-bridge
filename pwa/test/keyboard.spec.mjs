// The keyboard alone (#356, quality/README.md §3.4, ui.md §9): every primary action reached with
// Tab and Shift+Tab and done with Enter, Space and Escape, focus always shown, and Tab's order
// following the layout. In demo mode, at a desktop's width and a phone's.
import { test, expect } from '@playwright/test';
import { row, view } from './nav.mjs';

const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CLAUDE = '6f1c2b7e-claude-example';

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1&answerDelay=200${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

// The parts of the page, in the order they are laid out at a desktop's width: the skip link, the
// app bar, the rail, the banners, the list, then the open session's header, conversation, reply
// box and inspector, which sits to their right.
const REGIONS = [
  ['skip link', '.skip'], ['app bar', '#appbar'], ['navigation', '#nav'], ['banner', '.banner'],
  ['list', '#list-pane'], ['session header', '.session-header'], ['session tabs', '.view-tabs'],
  ['conversation', '.stream-scroll'], ['reply box', '.reply-slot'], ['inspector', '.inspector'],
  ['page', '.route.page'],
];

// Pairs of stops known to go against the layout, each tracked by an issue: a selector for the
// earlier stop, then one for the later.
const KNOWN_ORDER = [];

// Where focus is: which element (an id kept for it), its part of the page, where it sits in that
// part (scrolled panes counted back, so a pane scrolling to it doesn't reorder it), which of
// KNOWN_ORDER's selectors it matches, and whether its focus is shown: an outline or a shadow on
// it, or a box around it drawn in the focus colour (the reply box's border).
function where({ regions, known }) {
  const el = document.activeElement;
  window.__ids ??= new WeakMap();
  window.__n ??= 0;
  if (!window.__ids.has(el)) { window.__ids.set(el, ++window.__n); }
  const r = el.getBoundingClientRect();
  let top = r.top;
  let left = r.left;
  // Every scrolled ancestor, the page's own (<html>) included.
  for (let p = el.parentElement; p; p = p.parentElement) { top += p.scrollTop; left += p.scrollLeft; }
  // The focus colour, read once per page.
  if (!window.__focusColour) {
    const probe = document.createElement('i');
    probe.style.color = 'var(--ab-bridge)';
    document.body.append(probe);
    window.__focusColour = getComputedStyle(probe).color;
    probe.remove();
  }
  const focus = window.__focusColour;
  // A colour that shows: not transparent, and not of alpha 0.
  const opaque = (c) => !!c && c !== 'transparent' && !/[,/]\s*0(\.0*)?\s*\)$/.test(c);
  const s = getComputedStyle(el);
  const outline = s.outlineStyle !== 'none' && parseFloat(s.outlineWidth) >= 1 && opaque(s.outlineColor);
  const shadow = s.boxShadow !== 'none' && (s.boxShadow.match(/rgba?\([^)]*\)/g) ?? []).some(opaque);
  let box = false;
  for (let p = el.parentElement, i = 0; p && i < 3 && !box; p = p.parentElement, i += 1) {
    box = p.matches(':focus-within') && getComputedStyle(p).borderTopColor === focus;
  }
  const label = el.getAttribute('aria-label') || el.textContent.trim().slice(0, 40) || el.getAttribute('name') || '';
  return {
    id: window.__ids.get(el),
    name: `${el.tagName.toLowerCase()}${el.className && typeof el.className === 'string' ? `.${el.className.trim().split(/\s+/).join('.')}` : ''} "${label}"`,
    region: regions.find(([, sel]) => el.closest(sel))?.[0] ?? 'elsewhere',
    matches: known.filter((sel) => el.matches(sel)),
    link: el.matches('a[href]'),
    top, left, height: r.height,
    visible: el.matches(':focus-visible'),
    shown: outline || shadow || box,
  };
}

const focused = (page) => page.evaluate(where, { regions: REGIONS, known: KNOWN_ORDER.flatMap((k) => [k.from, k.to]) });

// Presses `key` until `target` has focus, at most `max` times, and returns each stop on the way,
// every one with its focus shown. Past the last control focus leaves the page for the browser's
// own, and comes back to the first.
async function tabTo(page, target, { key = 'Tab', max = 120 } = {}) {
  const stops = [];
  for (let i = 0; i < max; i += 1) {
    await page.keyboard.press(key);
    if (await page.evaluate(() => !document.activeElement || document.activeElement === document.body || document.activeElement === document.documentElement)) { continue; }
    const stop = await focused(page);
    expect.soft(stop.visible && stop.shown, `focus shown on ${stop.name}`).toBe(true);
    stops.push(stop);
    if (await target.evaluate((e) => e === document.activeElement)) { return stops; }
  }
  throw new Error(`${key} never reached ${target.toString()}: stopped on ${stops.slice(-5).map((s) => s.name).join(', ')}`);
}

// WebKit on Windows and macOS, as Safari does unless "Press Tab to highlight each item" is on, tabs
// to form controls and not to links, which Safari reaches with Option+Tab and Playwright's WebKit
// there not at all; WebKit on Linux (CI) tabs to links. In WebKit a link (the rail, New, Leave
// demo) is focused directly, as Option+Tab would, and operated as everywhere else, with Enter.
async function reachLink(page, browserName, link) {
  if (browserName !== 'webkit') { await tabTo(page, link); return; }
  // Option, as Option+Tab starts with: a key, so the focus that follows is shown (A12).
  await page.keyboard.press('Alt');
  await link.focus();
  await expect(link).toBeFocused();
  // Focused by script, so :focus-visible is the engine's guess; the outline drawn is what counts.
  const stop = await focused(page);
  expect.soft(stop.shown, `focus shown on ${stop.name}`).toBe(true);
}

// Reading order: each stop on the same line further right, or on a lower line.
function readingOrder(stops) {
  const wrong = [];
  for (let i = 1; i < stops.length; i += 1) {
    const [a, b] = [stops[i - 1], stops[i]];
    if (a.region !== b.region) { continue; }
    if (KNOWN_ORDER.some((k) => a.matches.includes(k.from) && b.matches.includes(k.to))) { continue; }
    const overlap = Math.min(a.top + a.height, b.top + b.height) - Math.max(a.top, b.top);
    const sameLine = overlap > Math.min(a.height, b.height) / 2;
    if (sameLine ? b.left <= a.left : b.top < a.top) { wrong.push(`${a.name} then ${b.name}`); }
  }
  return wrong;
}

test.describe('on a desktop', () => {
  test.use({ viewport: { width: 1440, height: 900 } });

  test('Tab walks the app bar, the rail, the list and the open session in the order they are laid out, with focus shown, and Shift+Tab walks back', async ({ page, browserName }) => {
    await openDemo(page);
    // The worked example, open at first: its question holds the reply box back, so the walk goes
    // from the conversation to the inspector.
    const v = view(page, COPILOT);
    await expect(v).toBeVisible();
    const last = v.locator('.end-session').getByRole('button', { name: 'End session' });
    const stops = await tabTo(page, last, { max: 200 });
    // The parts in the order they are laid out, each once: focus never leaves one and comes back.
    // Where WebKit doesn't tab to links (reachLink), there is no skip link, rail or banner.
    const parts = stops.map((s) => s.region).filter((r, i, all) => r !== all[i - 1]);
    const links = ['skip link', 'navigation', 'banner'];
    const tabsToLinks = browserName !== 'webkit' || stops.some((s) => s.link);
    expect(parts).toEqual(['skip link', 'app bar', 'navigation', 'banner', 'list', 'session header', 'conversation', 'inspector']
      .filter((p) => tabsToLinks || !links.includes(p)));
    expect(readingOrder(stops)).toEqual([]);
    // Shift+Tab walks the same stops in reverse.
    const back = [];
    for (let i = stops.length - 2; i >= 0; i -= 1) {
      await page.keyboard.press('Shift+Tab');
      back.push((await focused(page)).id);
    }
    expect(back).toEqual(stops.slice(0, -1).map((s) => s.id).reverse());
  });

  test('a session a notification opens as the app starts has focus on its title, with no ring around it, its header or its row', async ({ page }) => {
    await openDemo(page);
    await page.evaluate((id) => window.agentBridgeDemo.page.showSession(id), CLAUDE);
    const v = view(page, CLAUDE);
    await expect(row(page, CLAUDE)).toHaveClass(/linked/);
    await expect(v.locator('.session-title')).toBeFocused();
    expect((await focused(page)).shown, 'a ring around the title').toBe(false);
    expect(await v.locator('.session-header').evaluate((e) => getComputedStyle(e).boxShadow), 'a ring around the header').toBe('none');
    expect(await row(page, CLAUDE).locator('.row-main').evaluate((e) => getComputedStyle(e).boxShadow), 'a ring around the row').toBe('none');
  });

  test('focus the browser brings in from its address bar, with the page on screen all along, keeps the browser\'s ring', async ({ page }) => {
    await openDemo(page);
    // Focus out to the browser's own bar and back, with no key this page sees: the browser's Tab
    // or Shift+Tab, which lands on the first or last control as a script's focus() does here.
    await page.evaluate(() => { window.dispatchEvent(new Event('blur')); window.dispatchEvent(new Event('focus')); });
    const open = row(page, CLAUDE).locator('.row-main');
    await open.focus();
    await expect(open).toBeFocused();
    expect((await focused(page)).shown, 'a ring on the row').toBe(true);
  });

  test('the list head draws Sort on its first line, before New, as Tab reaches them (ui.md §9.1 A3)', async ({ page, browserName }) => {
    await openDemo(page);
    const sort = page.locator('#session-sort');
    const fresh = page.locator('#list-pane .new-session');
    const [s, n, title] = await Promise.all([sort, fresh, page.locator('#sessions-h')].map((l) => l.boundingBox()));
    // On one line: each one's middle within the other's height.
    const sameLine = (a, b) => Math.abs((a.y + a.height / 2) - (b.y + b.height / 2)) < Math.min(a.height, b.height) / 2;
    // Sort on the title's line, and New after it as a page reads: to its right, or on a line below.
    expect({ sortOnFirstLine: sameLine(s, title), newAfterSort: sameLine(s, n) ? s.x + s.width <= n.x : n.y >= s.y + s.height })
      .toEqual({ sortOnFirstLine: true, newAfterSort: true });
    await sort.focus();
    // WebKit's Tab here skips links, New among them (reachLink).
    if (browserName !== 'webkit') {
      await page.keyboard.press('Tab');
      await expect(fresh).toBeFocused();
    }
  });

  test('a session opens from its row with Enter, and its question is answered with Tab, Space and Enter', async ({ page }) => {
    await openDemo(page);
    const at = new Date(Date.now() + 1000).toISOString();
    await page.evaluate(({ id, at }) => {
      const h = window.agentBridgeDemo.host;
      h.upsert({ ...h.get(id), status: 'waiting', statusSince: at, updatedAt: at, question: {
        kind: 'form', id: 'q-keys', askedAt: at, message: 'Configure the run',
        fields: [
          { name: 'mode', title: 'Mode', type: 'choice', required: true, options: [{ value: 'fast', label: 'Fast' }, { value: 'full', label: 'Full' }] },
          { name: 'notes', title: 'Notes', type: 'text' },
        ],
      } });
    }, { id: CLAUDE, at });
    await tabTo(page, row(page, CLAUDE).locator('.row-main'));
    await page.keyboard.press('Enter');
    const v = view(page, CLAUDE);
    await expect(v).toBeVisible();
    const q = v.locator('.question');
    await tabTo(page, q.getByRole('radio', { name: 'Fast' }));
    await page.keyboard.press('Space');
    await expect(q.getByRole('radio', { name: 'Fast' })).toBeChecked();
    await tabTo(page, q.getByRole('textbox', { name: 'Notes' }));
    await page.keyboard.type('by keyboard');
    await tabTo(page, q.getByRole('button', { name: 'Send answer' }));
    await page.keyboard.press('Enter');
    await expect.poll(() => page.evaluate(() => window.agentBridgeDemo.host.answerLog.map((a) => a.fields))).toEqual([{ mode: 'fast', notes: 'by keyboard' }]);
  });

  test('a reply is typed and sent with Tab and Space, and End session is pressed twice with Space', async ({ page }) => {
    await openDemo(page);
    await tabTo(page, row(page, CLAUDE).locator('.row-main'));
    await page.keyboard.press('Enter');
    const v = view(page, CLAUDE);
    await expect(v).toBeVisible();
    await tabTo(page, v.getByRole('textbox', { name: 'Reply' }));
    await page.keyboard.type('Ship it.');
    await tabTo(page, v.getByRole('button', { name: 'Send' }));
    await page.keyboard.press('Space');
    await expect.poll(() => page.evaluate(() => window.agentBridgeDemo.host.replyLog.map((r) => r.text))).toEqual(['Ship it.']);
    // A working session ends on a second press.
    const end = v.locator('.end-session').getByRole('button', { name: /^End session/ });
    await tabTo(page, end);
    await page.keyboard.press('Space');
    await expect(v.locator('.end-session')).toHaveAttribute('data-stop', 'confirm');
    // The press disabled it while it went, which took its focus (#369): Tab finds it again.
    await tabTo(page, end);
    await page.keyboard.press('Space');
    await expect.poll(() => page.evaluate(() => window.agentBridgeDemo.host.stopLog.length)).toBe(2);
    await expect(row(page, CLAUDE)).toHaveCount(0);
  });

  test('Launch is reached on the rail and opened with Enter, and a session starts with Tab, typing and Enter', async ({ page, browserName }) => {
    await openDemo(page);
    await reachLink(page, browserName, page.locator('#nav [data-nav="launch"]'));
    await page.keyboard.press('Enter');
    const panel = page.locator('#launch');
    await expect(panel).toBeVisible();
    await tabTo(page, panel.locator('[name="prompt"]'));
    await page.keyboard.type('Add pagination.');
    await tabTo(page, panel.getByRole('button', { name: 'Launch' }));
    await page.keyboard.press('Enter');
    await expect(panel.locator('.launch-status')).toHaveText('Session started in a new worktree.');
    expect((await page.evaluate(() => window.agentBridgeDemo.host.launchLog)).map((l) => l.prompt)).toEqual(['Add pagination.']);
  });
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });

  test('a session opens from its row with Enter, Shift+Tab reaches Back, and Space returns to the list with focus on the row', async ({ page }) => {
    await openDemo(page);
    const open = row(page, CLAUDE).locator('.row-main');
    await tabTo(page, open);
    await page.keyboard.press('Enter');
    const v = view(page, CLAUDE);
    await expect(v).toBeInViewport();
    // Focus goes with the session pushed over the list: to its title, just after Back.
    await expect(v.locator('.session-title')).toBeFocused();
    await tabTo(page, v.getByRole('button', { name: 'Sessions' }), { key: 'Shift+Tab', max: 3 });
    await page.keyboard.press('Space');
    await expect(v).toBeHidden();
    await expect(open).toBeFocused();
  });

  test('Tab walks a pushed session\'s header, tabs and reply box in the order they are laid out, with focus shown', async ({ page }) => {
    await openDemo(page);
    await tabTo(page, row(page, CLAUDE).locator('.row-main'));
    await page.keyboard.press('Enter');
    const v = view(page, CLAUDE);
    await expect(v.locator('.session-title')).toBeFocused();
    // The box's last control while it is empty: Send waits for something to send.
    const stops = await tabTo(page, v.getByRole('button', { name: 'Attach a file or image' }));
    const parts = stops.map((s) => s.region).filter((r, i, all) => r !== all[i - 1]);
    // Nothing in this session's conversation takes focus, so Tab goes from the tabs to the reply box.
    expect(parts).toEqual(['session header', 'session tabs', 'reply box']);
    expect(readingOrder(stops)).toEqual([]);
  });

  // A12 (#373): focus moved by a press, a touch or the app coming back still lands, for a screen
  // reader, but only focus a key moved draws a ring.
  test('a session opened with a touch has focus on its title with no ring drawn, though keys were pressed before, and one opened with Enter draws it', async ({ page }) => {
    await openDemo(page);
    // As on iOS, where a tap focuses no button: focus stays where it was, for the browser to guess from.
    await page.evaluate(() => document.addEventListener('mousedown', (e) => e.preventDefault(), true));
    const v = view(page, CLAUDE);
    const open = row(page, CLAUDE).locator('.row-main');
    await tabTo(page, open);
    await open.tap();
    await expect(v.locator('.session-title')).toBeFocused();
    expect((await focused(page)).shown, 'a ring around the title after a touch').toBe(false);
    await v.getByRole('button', { name: 'Sessions' }).tap();
    await expect(v).toBeHidden();
    await tabTo(page, open);
    await page.keyboard.press('Enter');
    await expect(v.locator('.session-title')).toBeFocused();
    expect((await focused(page)).shown, 'a ring around the title after Enter').toBe(true);
  });

  for (const [how, back] of [
    // What the worker tells an open app when one of its notifications is tapped (main.js).
    ['a notification tapped while the app is open', () => navigator.serviceWorker.dispatchEvent(new MessageEvent('message', { data: { type: 'agent-bridge.open', url: location.href } }))],
    // An app in the background brought back by its notification.
    ['the app brought back from the background', () => document.dispatchEvent(new Event('visibilitychange'))],
  ]) {
    test(`the session ${how} opens has focus on its title with no ring around it or its header, though keys were pressed before, and the next key shows focus`, async ({ page }) => {
      await openDemo(page);
      // Keys pressed earlier, which would have the browser ring whatever a script focuses next.
      await tabTo(page, row(page, CLAUDE).locator('.row-main'));
      await page.evaluate(back);
      // The session shown as a notification's link shows it (main.js focusLinked).
      await page.evaluate((id) => window.agentBridgeDemo.page.showSession(id), CLAUDE);
      const v = view(page, CLAUDE);
      await expect(v).toHaveClass(/linked/);
      await expect(v.locator('.session-title')).toBeFocused();
      expect((await focused(page)).shown, 'a ring around the title').toBe(false);
      expect(await v.locator('.session-header').evaluate((e) => getComputedStyle(e).boxShadow), 'a ring around the header').toBe('none');
      // A key pressed where it is shows it there, as browsers do once a key is pressed.
      await page.keyboard.press('ArrowDown');
      await expect(v.locator('.session-title')).toBeFocused();
      expect((await focused(page)).shown, 'a ring around the title after a key').toBe(true);
      // Shift+Tab to Back: shown.
      await tabTo(page, v.getByRole('button', { name: 'Sessions' }), { key: 'Shift+Tab', max: 3 });
    });
  }

  test('a notification tapped while the app is on screen but another window has focus draws no ring when its window takes focus after', async ({ page }) => {
    await openDemo(page);
    await tabTo(page, row(page, CLAUDE).locator('.row-main'));
    // The worker posts its message, then focuses the window, which sends focus again to what has it.
    await page.evaluate((id) => {
      window.dispatchEvent(new Event('blur'));
      navigator.serviceWorker.dispatchEvent(new MessageEvent('message', { data: { type: 'agent-bridge.open', url: location.href } }));
      window.agentBridgeDemo.page.showSession(id);
      window.dispatchEvent(new Event('focus'));
      document.activeElement.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    }, CLAUDE);
    const v = view(page, CLAUDE);
    await expect(v.locator('.session-title')).toBeFocused();
    expect((await focused(page)).shown, 'a ring around the title').toBe(false);
  });

  test('a click that brings the window back keeps a touch\'s focus on the title without a ring, though the window took focus first', async ({ page }) => {
    await openDemo(page);
    await page.evaluate(() => document.addEventListener('mousedown', (e) => e.preventDefault(), true));
    const open = row(page, CLAUDE).locator('.row-main');
    await tabTo(page, open);
    await open.tap();
    const title = view(page, CLAUDE).locator('.session-title');
    await expect(title).toBeFocused();
    // A while later, past the moment a press counts as the one that brought the window back.
    await page.waitForTimeout(600);
    // Chromium gives the window its focus, and focus back to the title, before the click's press.
    await page.evaluate(() => {
      window.dispatchEvent(new Event('blur'));
      window.dispatchEvent(new Event('focus'));
      document.activeElement.dispatchEvent(new FocusEvent('focusin', { bubbles: true }));
    });
    await title.click();
    await expect(title).toBeFocused();
    expect((await focused(page)).shown, 'a ring around the title').toBe(false);
  });

  test('a notification for the session already open, its title focused with a key, takes the title\'s ring away', async ({ page }) => {
    await openDemo(page);
    await tabTo(page, row(page, CLAUDE).locator('.row-main'));
    await page.keyboard.press('Enter');
    const v = view(page, CLAUDE);
    await expect(v.locator('.session-title')).toBeFocused();
    expect((await focused(page)).shown, 'a ring around the title after Enter').toBe(true);
    // Tapped while the app is open, about the session shown: focus has nowhere to move.
    await page.evaluate(() => navigator.serviceWorker.dispatchEvent(new MessageEvent('message', { data: { type: 'agent-bridge.open', url: location.href } })));
    await page.evaluate((id) => window.agentBridgeDemo.page.showSession(id), CLAUDE);
    await expect(v.locator('.session-title')).toBeFocused();
    expect((await focused(page)).shown, 'a ring around the title').toBe(false);
  });

  test('the overflow menu opens with Enter, and Escape closes it with focus back on More', async ({ page }) => {
    await openDemo(page);
    await tabTo(page, row(page, CLAUDE).locator('.row-main'));
    await page.keyboard.press('Enter');
    const v = view(page, CLAUDE);
    const more = v.getByRole('button', { name: 'More' });
    await tabTo(page, more, { max: 6 });
    await page.keyboard.press('Enter');
    const menu = page.getByRole('menu');
    await expect(menu).toBeVisible();
    await expect(menu.getByRole('menuitem').first()).toBeFocused();
    await page.keyboard.press('Escape');
    await expect(menu).toHaveCount(0);
    await expect(more).toBeFocused();
    // And Details from it: the menu's arrows, then Enter.
    await page.keyboard.press('Enter');
    await expect(menu).toBeVisible();
    const details = menu.getByRole('menuitem', { name: 'Details' });
    for (let i = 0; i < 4 && !(await details.evaluate((e) => e === document.activeElement)); i += 1) { await page.keyboard.press('ArrowDown'); }
    await expect(details).toBeFocused();
    await page.keyboard.press('Enter');
    await expect(v.locator('.details')).toBeVisible();
  });
});
