// The sessions page's panes (docs/design/ui.md §1 principle 4, §5.2, §6.1, §6.2; #179, #214):
// three panes from 1280 px (list 320 · session · inspector 336, which can be hidden), two from
// 760 with Conversation and Tools as tabs, and one column below with the session pushed over the
// list and the browser's own back. What a row and a header say, and how their facts wrap.
import { test, expect } from '@playwright/test';
import { open, row, view } from './nav.mjs';

const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CLAUDE = '6f1c2b7e-claude-example';
const CODEX = '019a1b2c-codex-example';
const later = () => new Date(Date.now() + 1000).toISOString();

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
  await expect(page.locator('.session')).not.toHaveCount(0);
}

async function upsert(page, id, change) {
  await page.evaluate(({ id, change }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id) ?? { id };
    d.upsert({ ...s, ...change, activity: { ...(s.activity ?? {}), ...(change.activity ?? {}) } });
  }, { id, change });
}

const overflow = (page) => page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
const box = (locator) => locator.boundingBox();
// A width the page has laid out and told its media queries about: their change events come in
// a rendering update, so two frames on, the page has acted on the new width.
async function resize(page, width, height) {
  await page.setViewportSize({ width, height });
  await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))));
}

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test('at 1440 px three panes: the list at 320, the session, and the inspector at 336, side by side', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openDemo(page);
  const v = await open(page, CLAUDE);
  const list = await box(page.locator('#list-pane'));
  const conversation = await box(v.locator('.conversation'));
  const inspector = await box(v.locator('.inspector'));
  expect(list.width).toBe(320);
  expect(Math.round(inspector.width)).toBe(336);
  expect(list.x + list.width).toBeLessThanOrEqual(conversation.x + 0.5);
  expect(conversation.x + conversation.width).toBeLessThanOrEqual(inspector.x + 0.5);
  expect(inspector.x + inspector.width).toBeLessThanOrEqual(1440.5);
  // Details and Tools both in the inspector; no tabs.
  await expect(v.locator('.details')).toBeVisible();
  await expect(v.locator('.tools-panel')).toBeVisible();
  await expect(v.locator('.view-tabs')).toBeHidden();
  expect(await overflow(page)).toBeLessThanOrEqual(0);
});

for (const width of [900, 1440]) {
  test(`at ${width} px the reply box is at the foot of the session pane, and the panes reach it too`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await openDemo(page);
    const v = await open(page, CLAUDE);
    const pane = await box(page.locator('#session-pane'));
    const reply = await box(v.locator('.reply-slot'));
    const conversation = await box(v.locator('.conversation'));
    expect(Math.abs(reply.y + reply.height - (pane.y + pane.height))).toBeLessThanOrEqual(1);
    expect(Math.abs(conversation.y + conversation.height - (pane.y + pane.height))).toBeLessThanOrEqual(1);
    if (width >= 1280) {
      const inspector = await box(v.locator('.inspector'));
      expect(Math.abs(inspector.y + inspector.height - (pane.y + pane.height))).toBeLessThanOrEqual(1);
    }
  });
}

test('at 900 px two panes: the list and the session, with Conversation and Tools as tabs and Details in the header', async ({ page }) => {
  await page.setViewportSize({ width: 900, height: 900 });
  await openDemo(page);
  const v = await open(page, CLAUDE);
  const list = await box(page.locator('#list-pane'));
  const conversation = await box(v.locator('.conversation'));
  expect(list.width).toBeGreaterThanOrEqual(264);
  expect(list.width).toBeLessThanOrEqual(320);
  expect(list.x + list.width).toBeLessThanOrEqual(conversation.x + 0.5);
  await expect(v.locator('.inspector')).toBeHidden();
  await expect(v.locator('.inspector-button')).toBeHidden();
  const tabs = v.getByRole('tab');
  await expect(tabs).toHaveText(['Conversation', 'Tools']);
  await expect(v.getByRole('tab', { name: 'Conversation' })).toHaveAttribute('aria-selected', 'true');

  await v.getByRole('tab', { name: 'Tools' }).click();
  await expect(v.locator('.tools-panel li.tool')).toHaveCount(6);
  await expect(v.locator('.conversation')).toBeHidden();
  await expect(v.locator('.details')).toBeHidden();
  // Arrows go between the tabs.
  await page.keyboard.press('ArrowLeft');
  await expect(v.getByRole('tab', { name: 'Conversation' })).toBeFocused();
  await expect(v.locator('.conversation')).toBeVisible();

  // Details, from the header: in place of the conversation, and again goes back.
  await v.locator('.details-button').click();
  await expect(v.locator('.details-button')).toHaveAttribute('aria-pressed', 'true');
  await expect(v.locator('.details')).toBeVisible();
  await expect(v.locator('.details')).toContainText('7 turns');
  await expect(v.locator('.conversation')).toBeHidden();
  await v.locator('.details-button').click();
  await expect(v.locator('.conversation')).toBeVisible();
  expect(await overflow(page)).toBeLessThanOrEqual(0);
});

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

  test('one column: a session is pushed over the list, and the browser\'s own back returns to it', async ({ page }) => {
    await openDemo(page);
    await expect(page.locator('#list-pane')).toBeVisible();
    await expect(page.locator('.session-view:not([hidden])')).toHaveCount(0);
    expect(await overflow(page)).toBeLessThanOrEqual(0);

    await row(page, CODEX).locator('.row-main').tap();
    const v = view(page, CODEX);
    await expect(v).toBeVisible();
    await expect(page.locator('#list-pane')).toBeHidden();
    // Full width, the reply box at its foot, and the bottom navigation out of its way.
    const pane = await box(page.locator('#session-pane'));
    expect(pane.width).toBe(390);
    await expect(page.locator('#nav')).toBeHidden();
    const reply = await box(v.locator('.reply-slot'));
    expect(reply.y + reply.height).toBeGreaterThan(844 - 4);
    expect(await overflow(page)).toBeLessThanOrEqual(0);

    await page.goBack();
    await expect(v).toBeHidden();
    await expect(page.locator('#list-pane')).toBeVisible();
    await expect(page.locator('#nav')).toBeVisible();

    // And the header's Back does the same.
    await row(page, CLAUDE).locator('.row-main').tap();
    await expect(view(page, CLAUDE)).toBeVisible();
    await view(page, CLAUDE).getByRole('button', { name: 'Sessions' }).tap();
    await expect(view(page, CLAUDE)).toBeHidden();
    await expect(page.locator('#list-pane')).toBeVisible();
  });

  test('a pushed session\'s Back, overflow, tabs and reply buttons are at least 44 px to a finger', async ({ page }) => {
    await openDemo(page);
    const v = await open(page, CLAUDE);
    const controls = await v.locator('.session-header .back, .session-header .more button, .view-tab, .reply-tools button').filter({ visible: true }).all();
    expect(controls.length).toBeGreaterThanOrEqual(5);
    for (const c of controls) {
      const b = await c.boundingBox();
      expect(b.width).toBeGreaterThanOrEqual(44);
      expect(b.height).toBeGreaterThanOrEqual(44);
    }
  });

  // What is drawn at the middle of a control: the control, not a bar over it.
  const reachable = (locator) => locator.evaluate((el) => {
    const r = el.getBoundingClientRect();
    const top = document.elementFromPoint(r.left + r.width / 2, r.top + r.height / 2);
    return !!top && (el === top || el.contains(top));
  });

  test('a session pushed while the connection has news starts under the strip, with Back in reach', async ({ page }) => {
    await openDemo(page);
    const v = await open(page, CLAUDE);
    await page.evaluate(() => { document.body.dataset.conn = 'warn'; document.getElementById('conn').textContent = 'Reconnecting…'; });
    await expect(page.locator('#conn')).toBeVisible();
    const strip = await box(page.locator('#appbar'));
    const header = await box(v.locator('.session-header'));
    expect(header.y).toBeGreaterThanOrEqual(strip.y + strip.height - 0.5);
    expect(await reachable(v.locator('.back'))).toBe(true);
    expect(await reachable(v.locator('.session-header .more'))).toBe(true);
  });

  test('a session opened from a search is not under the search row, and back returns to what was searched', async ({ page }) => {
    await openDemo(page);
    await page.locator('.search-open').filter({ visible: true }).first().tap();
    await page.keyboard.type('relay');
    await expect(page.locator('.session')).toHaveCount(1);
    await row(page, CLAUDE).locator('.row-main').tap();
    const v = view(page, CLAUDE);
    await expect(v).toBeVisible();
    expect(await reachable(v.locator('.back'))).toBe(true);
    await page.goBack();
    await expect(v).toBeHidden();
    await expect(page.locator('#search')).toHaveValue('relay');
    await expect(page.locator('.session')).toHaveCount(1);
  });

  test('a session opened from Launch goes back to the list, not to Launch, and a second replaces the first', async ({ page }) => {
    await openDemo(page);
    await page.locator('.new-session').filter({ visible: true }).first().tap();
    await expect(page.locator('[data-route="launch"]')).toBeVisible();
    await page.evaluate((id) => window.agentBridgeDemo.page.showSession(id), CLAUDE);
    await expect(view(page, CLAUDE)).toBeVisible();
    await page.evaluate((id) => window.agentBridgeDemo.page.showSession(id), CODEX);
    await expect(view(page, CODEX)).toBeVisible();
    await page.goBack();
    await expect(view(page, CODEX)).toBeHidden();
    await expect(page.locator('[data-route="sessions"]')).toBeVisible();
    await expect(page.locator('#list-pane')).toBeVisible();
    await page.goBack();
    await expect(page.locator('[data-route="launch"]')).toBeVisible();
  });

  test('a session\'s address opened directly has the list beneath it, so back goes there, not out of the app', async ({ page }) => {
    await page.goto(`?demo=1&static=1#/sessions/demo/${CLAUDE}`);
    await page.waitForSelector('html[data-demo-ready]');
    await expect(view(page, CLAUDE)).toBeVisible();
    await page.goBack();
    await expect(page.locator('#list-pane')).toBeVisible();
    expect(new URL(page.url()).hash).toBe('#/sessions');
  });

  test('after a reload the header\'s Back still pops the session\'s entry, so forward returns to it', async ({ page }) => {
    await openDemo(page);
    await open(page, CLAUDE);
    await page.reload();
    await page.waitForSelector('html[data-demo-ready]');
    const v = view(page, CLAUDE);
    await expect(v).toBeVisible();
    await v.locator('.back').tap();
    await expect(page.locator('#list-pane')).toBeVisible();
    await page.goForward();
    await expect(v).toBeVisible();
  });

  test('a row opened takes focus to the session\'s title, as the row it was on is gone', async ({ page }) => {
    await openDemo(page);
    await row(page, CLAUDE).locator('.row-main').focus();
    await page.keyboard.press('Enter');
    await expect(view(page, CLAUDE).locator('.session-title')).toBeFocused();
  });

  test('Ctrl+K over a pushed session goes back to the list and focuses search', async ({ page }) => {
    await openDemo(page);
    const v = await open(page, CLAUDE);
    await page.keyboard.press('Control+k');
    await expect(v).toBeHidden();
    await expect(page.locator('#list-pane')).toBeVisible();
    await expect(page.locator('#search')).toBeFocused();
  });

  test('Back from a session gives focus back to the row it was opened from', async ({ page }) => {
    await openDemo(page);
    const r = row(page, CLAUDE).locator('.row-main');
    await r.focus();
    await page.keyboard.press('Enter');
    const v = view(page, CLAUDE);
    await expect(v).toBeVisible();
    await v.locator('.back').click();
    await expect(v).toBeHidden();
    await expect(r).toBeFocused();
  });

  test('the header\'s overflow menu holds Pin, Details and End session', async ({ page }) => {
    await openDemo(page);
    const v = await open(page, CODEX);
    await expect(v.locator('.pin')).toBeHidden();
    await v.getByRole('button', { name: 'More' }).tap();
    await expect(v.getByRole('menuitem')).toHaveText(['Pin', 'Details', 'End session']);
    await v.getByRole('menuitem', { name: 'Pin' }).tap();
    await expect(row(page, CODEX)).toHaveAttribute('data-pinned', 'true');
    await v.getByRole('button', { name: 'More' }).tap();
    await v.getByRole('menuitem', { name: 'Details' }).tap();
    await expect(v.locator('.details')).toBeVisible();
    await expect(v.locator('.details .end-button')).toBeVisible();
  });
});

test('narrowing to a phone on another page stays on that page', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openDemo(page);
  await open(page, CLAUDE);
  await page.locator('#nav [data-nav="settings"]').click();
  await expect(page.locator('[data-route="settings"]')).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.waitForTimeout(100);
  await expect(page.locator('[data-route="settings"]')).toBeVisible();
  expect(await page.evaluate(() => location.hash)).toBe('#/settings');
});

test('Tools are not fetched while another page shows, and are fetched once on coming back', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openDemo(page);
  const v = await open(page, CLAUDE);
  await expect(v.locator('.tools-panel li.tool')).toHaveCount(6);
  const calls = () => page.evaluate(() => window.agentBridgeDemo.calls.filter((c) => c === 'session.get').length);
  const before = await calls();
  await page.locator('#nav [data-nav="settings"]').click();
  for (let k = 1; k <= 3; k++) {
    await upsert(page, CLAUDE, { updatedAt: new Date(Date.now() + 1000 * k).toISOString() });
    await page.waitForTimeout(1000);
  }
  await page.waitForTimeout(1500);
  expect(await calls()).toBe(before);
  await page.locator('#nav [data-nav="sessions"]').click();
  await expect.poll(calls).toBe(before + 1);
});

test('a Needs you entry opens its session on Conversation, where its question is, even if Tools was left open', async ({ page }) => {
  await page.setViewportSize({ width: 900, height: 900 });
  await openDemo(page);
  const v = await open(page, COPILOT);
  await v.locator('.view-tab', { hasText: 'Tools' }).click();
  await open(page, CLAUDE);
  await page.locator(`#needs-you .needs-entry[data-id="${COPILOT}"] .needs-title`).click();
  await expect(v).toBeVisible();
  await expect(v.locator('.view-tab', { hasText: 'Conversation' })).toHaveAttribute('aria-selected', 'true');
  await expect(v.locator('.question')).toBeVisible();
});

test('a Needs you entry scrolls its question into sight, however far its conversation was scrolled', async ({ page }) => {
  await page.setViewportSize({ width: 900, height: 600 });
  await openDemo(page);
  // A long response above the question, and the conversation scrolled to its top.
  await upsert(page, COPILOT, { updatedAt: later(), activity: { response: Array.from({ length: 60 }, (_, i) => `Paragraph ${i} of a long answer.`).join('\n\n') } });
  const v = await open(page, COPILOT);
  await v.locator('.stream-scroll').evaluate((e) => { e.scrollTop = 0; });
  await open(page, CLAUDE);
  await page.locator(`#needs-you .needs-entry[data-id="${COPILOT}"] .needs-title`).click();
  await expect(v.locator('.question-slot .question')).toBeInViewport();
});

test('a Tools refresh queued just before leaving Sessions is not sent', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openDemo(page);
  const v = await open(page, CLAUDE);
  await expect(v.locator('.tools-panel li.tool')).toHaveCount(6);
  const calls = () => page.evaluate(() => window.agentBridgeDemo.calls.filter((c) => c === 'session.get').length);
  const before = await calls();
  await upsert(page, CLAUDE, { updatedAt: new Date(Date.now() + 1000).toISOString() });
  await page.locator('#nav [data-nav="settings"]').click();
  await page.waitForTimeout(2600);
  expect(await calls()).toBe(before);
});

test('narrowed to a phone on Settings with a session chosen, Ctrl+K and typing keep focus in search', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openDemo(page);
  await open(page, CLAUDE);
  await page.locator('#nav [data-nav="settings"]').click();
  await expect(page.locator('[data-route="settings"]')).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))));
  await page.keyboard.press('Control+k');
  await page.keyboard.type('relay');
  await expect(page.locator('#search')).toHaveValue('relay');
  await expect(page.locator('#search')).toBeFocused();
});

test('Tools left while the session changed are fetched again when they show', async ({ page }) => {
  await page.setViewportSize({ width: 900, height: 900 });
  await openDemo(page);
  const v = await open(page, CLAUDE);
  await v.locator('.view-tab', { hasText: 'Tools' }).click();
  await expect(v.locator('.tools-panel li.tool')).toHaveCount(6);
  await v.locator('.view-tab', { hasText: 'Conversation' }).click();
  await page.evaluate((id) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    // Seven calls of its own, where the demo makes up six.
    const history = Array.from({ length: 7 }, (_, k) => ({ name: 'grep', summary: `call ${k}`, status: 'succeeded', at: new Date().toISOString() }));
    d.upsert({ ...s, updatedAt: new Date(Date.now() + 1000).toISOString(), activity: { ...s.activity, history } });
  }, CLAUDE);
  await page.waitForTimeout(100);
  await v.locator('.view-tab', { hasText: 'Tools' }).click();
  await expect(v.locator('.tools-panel li.tool')).toHaveCount(7);
});

test('a session that changes while its Tools are first on their way has them fetched again', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openDemo(page);
  // Every Tools fetch from here held, with what the machine sent, until the test lets it go.
  await page.evaluate(() => {
    const { view } = window.agentBridgeDemo;
    const was = view.fetchDetail.bind(view);
    window.__held = [];
    view.fetchDetail = (hostId, id) => was(hostId, id).then((r) => (window.__holding ? new Promise((go) => { window.__held.push(() => go(r)); }) : r));
    window.__holding = true;
  });
  await row(page, CLAUDE).locator('.row-main').click();
  const v = view(page, CLAUDE);
  await expect.poll(() => page.evaluate(() => window.__held.length)).toBeGreaterThan(0);
  await page.evaluate((id) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id);
    const history = Array.from({ length: 7 }, (_, k) => ({ name: 'grep', summary: `call ${k}`, status: 'succeeded', at: new Date().toISOString() }));
    d.upsert({ ...s, updatedAt: new Date(Date.now() + 1000).toISOString(), activity: { ...s.activity, history } });
  }, CLAUDE);
  await page.waitForTimeout(100);
  // The earlier list arrives; the change it missed brings the later one.
  await page.evaluate(() => { window.__holding = false; for (const go of window.__held.splice(0)) { go(); } });
  await expect(v.locator('.tools-panel li.tool')).toHaveCount(7, { timeout: 8000 });
});

test('picking another machine in the tabs shows its first session, not a pane waiting for the last one', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openDemo(page, '&machines=2');
  await open(page, CLAUDE, 'demo');
  await expect.poll(() => page.evaluate(() => location.hash)).toContain('/demo/');
  await page.locator('#machine-tabs [role="tab"]').nth(2).click();
  await expect(page.locator('.session-view:not([hidden])')).toHaveCount(1);
  await expect(page.locator('.session-view:not([hidden])')).not.toHaveAttribute('data-host-id', 'demo');
  await expect(page.locator('#no-session')).toBeHidden();
});

test('a session address on a machine not shown shows that machine', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openDemo(page, '&machines=2');
  await page.locator('#machine-tabs [data-device-id="demo"]').click();
  await expect(page.locator('.session[data-host-id="demo-laptop"]')).toHaveCount(0);
  const id = await page.evaluate(() => [...window.agentBridgeDemo.machines['demo-laptop'].sessions.keys()][0]);
  await page.evaluate((id) => { location.hash = `#/sessions/demo-laptop/${id}`; }, id);
  await expect(view(page, id, 'demo-laptop')).toBeVisible();
  await expect(row(page, id, 'demo-laptop')).toHaveClass(/selected/);
});

test('an address naming a session that has not arrived yet shows no other session meanwhile', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await openDemo(page);
  await open(page, CLAUDE);
  // Its machine resyncing (its list not all in yet), and the session not here.
  await page.evaluate(() => {
    const { client } = window.agentBridgeDemo.hosts.link('demo');
    Object.defineProperty(client, 'synced', { get: () => false, configurable: true });
  });
  await page.evaluate(() => { location.hash = '#/sessions/demo/demo-arriving'; });
  await expect.poll(() => page.evaluate(() => location.hash)).toBe('#/sessions/demo/demo-arriving');
  await expect(view(page, CLAUDE)).toBeHidden();
  await expect(page.locator('.session-view:not([hidden])')).toHaveCount(0);
  // Once it's there, it is the one shown.
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    delete d.hosts.link('demo').client.synced;
    const t = new Date().toISOString();
    d.upsert({ id: 'demo-arriving', agent: 'copilot', title: 'Arriving', status: 'idle', startedAt: t, updatedAt: t, activity: {} });
  });
  await expect(view(page, 'demo-arriving')).toBeVisible();
});

test('news a session\'s view showed while search hid its row gets no dot once search lets the row back', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openDemo(page);
  await open(page, CODEX);
  await page.keyboard.press('Control+k');
  await page.keyboard.type('relay');
  await expect(row(page, CODEX)).toHaveCount(0);
  await expect(view(page, CODEX)).toBeVisible();
  // It changes while its view is the one on screen.
  await upsert(page, CODEX, { updatedAt: later(), activity: { response: 'Shown as it came.' } });
  await expect(view(page, CODEX).locator('.response')).toContainText('Shown as it came.');
  // Another chosen from the search, then search cleared.
  await page.locator('.session .row-main').first().click();
  await expect(view(page, CODEX)).toBeHidden();
  await page.locator('#search').fill('');
  await expect(row(page, CODEX)).toBeVisible();
  await expect(row(page, CODEX).locator('.unread-dot')).toHaveCount(0);
});

test('a session chosen stays chosen across 1440, 900 and 390 px, pushed on the phone', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openDemo(page);
  await open(page, CODEX);
  for (const width of [900, 390, 900, 1440]) {
    await resize(page, width, 844);
    await expect(view(page, CODEX)).toBeVisible();
    await expect(page.locator('.session-view:not([hidden])')).toHaveCount(1);
    await expect(row(page, CODEX)).toHaveClass(/\bselected\b/);
    expect(await overflow(page)).toBeLessThanOrEqual(0);
  }
  // Pushed when the window became a phone's: the browser's back goes to the list, not away.
  await resize(page, 390, 844);
  await expect(view(page, CODEX)).toBeVisible();
  await page.goBack();
  await expect(page.locator('#list-pane')).toBeVisible();
  await expect(view(page, CODEX)).toBeHidden();
  expect(new URL(page.url()).hash).toBe('#/sessions');
});

test('a desktop-wide window puts the list and the session side by side, a phone keeps one column', async ({ page }) => {
  const columns = () => page.locator('#list-pane, #session-pane').evaluateAll((els) => new Set(els.filter((e) => e.getBoundingClientRect().width > 0).map((e) => Math.round(e.getBoundingClientRect().left))).size);
  await page.setViewportSize({ width: 1440, height: 900 });
  await openDemo(page);
  expect(await columns()).toBe(2);
  expect(await overflow(page)).toBeLessThanOrEqual(0);
  await page.setViewportSize({ width: 390, height: 844 });
  await expect.poll(columns).toBe(1);
  expect(await overflow(page)).toBeLessThanOrEqual(0);
});

test('the inspector can be hidden at three panes, Tools then a tab, and it stays hidden after a reload', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openDemo(page);
  const v = await open(page, CLAUDE);
  const toggle = v.locator('.inspector-button');
  await expect(toggle).toHaveAccessibleName('Hide inspector');
  await toggle.click();
  await expect(v.locator('.inspector')).toBeHidden();
  await expect(v.getByRole('tab', { name: 'Tools' })).toBeVisible();
  await expect(toggle).toHaveAccessibleName('Show inspector');
  const conversation = await box(v.locator('.conversation'));
  const pane = await box(page.locator('#session-pane'));
  expect(Math.abs(conversation.width - pane.width)).toBeLessThan(1);

  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  await expect(view(page, CLAUDE)).toBeVisible();
  await expect(view(page, CLAUDE).locator('.inspector')).toBeHidden();
  await view(page, CLAUDE).locator('.inspector-button').click();
  await expect(view(page, CLAUDE).locator('.inspector')).toBeVisible();
  await expect(view(page, CLAUDE).locator('.view-tabs')).toBeHidden();
});

test('a row is two lines: the title, then the status and the repository, never the model, SHA, counts or pid', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openDemo(page);
  const r = row(page, COPILOT);
  await expect(r.locator('.row-title')).toHaveText('Plan Migration to Windows App');
  await expect(r.locator('.row-facts')).toHaveText('Waiting on you · danswett/agent-ha-bridge');
  await expect(r.locator('.row-age')).toHaveText(/ago$/);
  for (const never of ['claude-opus-5.5', 'detached@5e59309', '412', 'compaction', 'pid', '39844']) {
    await expect(r).not.toContainText(never);
  }
  // Two lines, the glyph on the first.
  const title = await box(r.locator('.row-title'));
  const facts = await box(r.locator('.row-facts'));
  expect(facts.y).toBeGreaterThanOrEqual(title.y + title.height - 0.5);
  const main = await box(r.locator('.row-main'));
  expect(main.height).toBeLessThanOrEqual(title.height + facts.height + 16 + 1);
  // The header carries one meta line: status, machine, repository; the rest is in Details.
  const v = view(page, COPILOT);
  await expect(v.locator('.status-line')).toHaveText('Waiting on you · Demo machine · danswett/agent-ha-bridge');
  await expect(v.locator('.fact-model dd')).toHaveText('claude-opus-5.5');
  await expect(v.locator('.fact-branch dd')).toHaveText('detached@5e59309');
  await expect(v.locator('.fact-pid dd')).toHaveText('pid 39844');
});

test('the header\'s Pin pins the session, and its row goes to Pinned', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openDemo(page);
  const v = await open(page, CODEX);
  await v.getByRole('button', { name: 'Pin' }).click();
  await expect(row(page, CODEX)).toHaveAttribute('data-pinned', 'true');
  await expect(page.locator('.list-group').first()).toHaveText('Pinned');
  await expect(v.locator('.pin')).toHaveAttribute('aria-pressed', 'true');
  await expect(v.locator('.pin')).toBeFocused();
  await v.getByRole('button', { name: 'Unpin' }).click();
  await expect(row(page, CODEX)).not.toHaveAttribute('data-pinned', 'true');
});

// #214: facts wrap as words do, a separator with the fact before it, and never starting a line;
// a long repository and machine name at a phone's width.
for (const width of [390, 900, 1440]) {
  test(`at ${width} px no line of a row or a header starts with a separator, even with a long repository and machine name`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await openDemo(page, `&machines=2&name=${encodeURIComponent('A demo machine with a remarkably long name in the lab')}`);
    await upsert(page, CODEX, {
      updatedAt: later(), driver: 'agent', status: 'waiting', statusSince: later(),
      repository: 'danswett/an-unusually-long-repository-name-that-has-to-wrap-somewhere',
      subagents: [{ id: 'b1', kind: 'background', name: 'Check the Ubuntu CI failure', status: 'working' }],
      question: { id: 'q-long', kind: 'form', message: 'Which?', fields: [], askedAt: later() },
    });
    const v = await open(page, CODEX);
    const starts = await page.evaluate(() => {
      const bad = [];
      const check = (tag) => {
        for (const host of document.querySelectorAll('.session-view:not([hidden]) .meta-facts, .session .row-facts')) {
          const left = host.getBoundingClientRect().left;
          // Every piece of each separator, on whichever line it fell.
          for (const s of host.querySelectorAll('.sep')) {
            for (const r of s.getClientRects()) {
              if (r.width > 1 && r.left - left < 1) { bad.push(`${tag} ${host.className}: ${host.textContent}`); }
            }
          }
        }
      };
      check('as laid out');
      // And at every width the header's text could have, 4 px apart, so a break falls before
      // each separator in turn if it ever could.
      const main = document.querySelector('.session-view:not([hidden]) .header-main');
      const widest = main.getBoundingClientRect().width;
      for (let w = 120; w <= widest; w += 4) {
        main.style.width = `${w}px`;
        check(`at ${w} px`);
      }
      main.style.width = '';
      return bad;
    });
    expect(starts).toEqual([]);
    // It did wrap at a phone's width: the test is of something.
    if (width === 390) {
      const lines = await v.locator('.meta-facts').evaluate((e) => {
        const r = document.createRange();
        r.selectNodeContents(e);
        return new Set([...r.getClientRects()].map((x) => Math.round(x.top))).size;
      });
      expect(lines).toBeGreaterThan(1);
    }
    // The machine is in text colour, not Bridge (#214).
    const colours = await v.locator('.session-machine').evaluate((e) => [getComputedStyle(e).color, getComputedStyle(document.body).color]);
    expect(colours[0]).toBe(colours[1]);
    expect(await overflow(page)).toBeLessThanOrEqual(0);
  });
}

test('on a phone a short repository in the header moves to the next line whole, not split at its hyphen', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await openDemo(page);
  const v = await open(page, CLAUDE);
  // "Working · Demo machine · agent-bridge" is a little wider than the header at 390.
  const repo = v.locator('.meta-facts .repo');
  await expect(repo).toHaveText('agent-bridge');
  const lines = await repo.evaluate((e) => {
    const r = document.createRange();
    r.selectNodeContents(e);
    return new Set([...r.getClientRects()].map((x) => Math.round(x.top))).size;
  });
  expect(lines).toBe(1);
});

test('a folder\'s path in Details is mono, on one line, and gives up its middle rather than a word', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await openDemo(page);
  const cwd = 'C:\\Users\\demo\\repos\\wt\\agent-bridge-a-worktree-with-a-long-name\\src\\AgentBridge.Host.Sessions\\Copilot';
  await upsert(page, CODEX, { updatedAt: later(), cwd });
  const v = await open(page, CODEX);
  const path = v.locator('.fact-folder .path');
  await expect(path).toHaveAttribute('title', cwd);
  expect(await path.evaluate((e) => getComputedStyle(e).fontFamily)).toMatch(/^"?Agent Bridge Mono"?/);
  const shape = await path.evaluate((e) => {
    const head = e.querySelector('.path-head');
    const tail = e.querySelector('.path-tail');
    return { lines: e.getClientRects().length, height: e.getBoundingClientRect().height, line: parseFloat(getComputedStyle(e).lineHeight), cut: head.scrollWidth > head.clientWidth, tail: tail.textContent, whole: tail.scrollWidth <= tail.clientWidth + 1 };
  });
  expect(shape.lines).toBe(1);
  expect(shape.height).toBeLessThanOrEqual(shape.line + 1);
  expect(shape.cut).toBe(true);
  expect(shape.tail).toBe('\\Copilot');
  expect(shape.whole).toBe(true);
  // A trailing separator still leaves the last name whole, and the middle the part given up.
  await upsert(page, CODEX, { updatedAt: new Date(Date.now() + 5000).toISOString(), cwd: `${cwd}\\` });
  await expect(path).toHaveAttribute('title', `${cwd}\\`);
  expect(await v.locator('.fact-folder .path-tail').textContent()).toBe('\\Copilot\\');
  expect(await v.locator('.fact-folder .path-head').evaluate((e) => e.scrollWidth > e.clientWidth)).toBe(true);
});

// Secondary text is text2 over whatever it sits on: 4.5:1 or better, on a row selected or not,
// in the header, the conversation and Details, light and dark.
for (const scheme of ['light', 'dark']) {
  test(`in ${scheme}, secondary text on the panes reads at 4.5:1 or better`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme });
    await page.setViewportSize({ width: 1440, height: 900 });
    await openDemo(page);
    await open(page, CODEX);
    const ratios = await page.evaluate(() => {
      const parse = (c) => { const m = c.match(/[\d.]+/g).map(Number); return { r: m[0], g: m[1], b: m[2], a: m.length > 3 ? m[3] : 1 }; };
      const over = (fg, bg) => ({ r: fg.r * fg.a + bg.r * (1 - fg.a), g: fg.g * fg.a + bg.g * (1 - fg.a), b: fg.b * fg.a + bg.b * (1 - fg.a), a: 1 });
      const background = (el) => {
        const layers = [];
        for (let e = el; e; e = e.parentElement) {
          const c = parse(getComputedStyle(e).backgroundColor);
          if (c.a > 0) { layers.push(c); if (c.a === 1) { break; } }
        }
        return layers.reverse().reduce((acc, c) => over(c, acc), { r: 255, g: 255, b: 255, a: 1 });
      };
      const lum = ({ r, g, b }) => [r, g, b].map((v) => { const s = v / 255; return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4; }).reduce((a, v, i) => a + v * [0.2126, 0.7152, 0.0722][i], 0);
      const ratio = (el) => {
        const bg = background(el);
        const fg = over(parse(getComputedStyle(el).color), bg);
        const [a, b] = [lum(fg), lum(bg)].sort((x, y) => y - x);
        return (a + 0.05) / (b + 0.05);
      };
      const pick = ['.session.selected .row-facts', '.session:not(.selected) .row-facts', '.session:not(.selected) .row-age', '.session-view:not([hidden]) .status-line', '.session-view:not([hidden]) .speaker', '.session-view:not([hidden]) .fact dt', '.session-view:not([hidden]) .section-head', '#session-count'];
      return Object.fromEntries(pick.map((s) => [s, document.querySelector(s) ? Math.round(ratio(document.querySelector(s)) * 100) / 100 : null]));
    });
    for (const [what, r] of Object.entries(ratios)) {
      expect(r, what).not.toBeNull();
      expect(r, what).toBeGreaterThanOrEqual(4.5);
    }
  });
}

test('in forced colours the selected row and the tabs keep a system colour for their marks', async ({ page, browserName }) => {
  test.skip(browserName !== 'chromium', 'forced colours are emulated in Chromium only');
  await page.emulateMedia({ forcedColors: 'active' });
  await page.setViewportSize({ width: 900, height: 900 });
  await openDemo(page);
  const v = await open(page, CODEX);
  const pill = await row(page, CODEX).locator('.row-main').evaluate((e) => [getComputedStyle(e, '::before').forcedColorAdjust, getComputedStyle(e, '::before').content]);
  expect(pill).toEqual(['none', '""']);
  const tab = await v.getByRole('tab', { name: 'Conversation' }).evaluate((e) => getComputedStyle(e, '::after').forcedColorAdjust);
  expect(tab).toBe('none');
});
