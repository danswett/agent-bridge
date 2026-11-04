// Moving a session by hand (#592, #593; ui.md §5.3 Lift and Reorder, §6.1 Moving, §9), in demo
// mode: on touch a long press lifts a card (at the platform's own time) and a quick swipe still
// scrolls; the cards it passes ease aside and the gap is where it lands; the list scrolls near its
// edges; the system taking the touch, or Escape, puts it back; crossing the Sessions header pins
// it; reduced motion draws no scale, shadow or easing; the order is kept on this device; nothing
// the release lands on is pressed; and the page passes axe with a card lifted.
import { test, expect } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';
import { view } from './nav.mjs';

const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CLAUDE = '6f1c2b7e-claude-example';
const CODEX = '019a1b2c-codex-example';
const DELEGATING = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const FIRST = [COPILOT, CLAUDE, CODEX, DELEGATING];
const UA = {
  android: 'Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36',
  iphone: 'Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1',
};

const card = (page, id) => page.locator(`.session[data-id="${id}"]`);
const order = (page) => page.locator('#session-list > .session').evaluateAll((els) => els.map((e) => e.dataset.id));
const said = (page) => page.locator('#session-list + [role="status"]');
// How far a card has been moved aside, in px.
const shifted = (page, id) => card(page, id).evaluate((e) => new DOMMatrix(getComputedStyle(e).transform).m42);
// Every motion running: a CSS transition, or one js/ui/motion.js drew.
const running = (page) => page.evaluate(() => document.getAnimations()
  .filter((a) => a.playState === 'running' && (a.constructor.name === 'CSSTransition' || /^ab-motion-/.test(a.id ?? '')))
  .map((a) => a.id || a.transitionProperty));
const settled = (page) => page.waitForFunction(() => !document.getAnimations().some((a) => a.id === 'ab-motion-reorder' && a.playState === 'running'));

// The page on a platform (what js/platform.js reads), with navigator.vibrate counted.
async function openDemo(page, { ua = null, hint = null, query = '' } = {}) {
  await page.addInitScript(({ ua, hint }) => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
    window.__ticks = [];
    Navigator.prototype.vibrate = function vibrate(p) { window.__ticks.push(p); return true; };
    if (ua) {
      Object.defineProperty(Navigator.prototype, 'userAgent', { get: () => ua, configurable: true });
      Object.defineProperty(Navigator.prototype, 'userAgentData', { get: () => (hint ? { platform: hint } : undefined), configurable: true });
    }
  }, { ua, hint });
  await page.goto(`?demo=1&static=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
  await expect.poll(() => order(page)).toEqual(FIRST);
}

async function many(page, n = 24) {
  await page.evaluate((n) => {
    const d = window.agentBridgeDemo;
    for (let i = 0; i < n; i += 1) {
      const t = new Date(Date.now() - (i + 10) * 60_000).toISOString();
      d.upsert({ id: `demo-many-${i}`, agent: 'copilot', title: `Session ${i + 1}`, status: 'idle', startedAt: t, updatedAt: t, statusSince: t, activity: {} });
    }
  }, n);
  await expect(page.locator('.session[data-id^="demo-many-"]')).toHaveCount(n);
}

// A finger, through Chromium's own input pipeline: real pointer and touch events, real scrolling.
async function finger(page) {
  const cdp = await page.context().newCDPSession(page);
  const send = (type, points) => cdp.send('Input.dispatchTouchEvent', { type, touchPoints: points.map(([x, y]) => ({ x, y, id: 1 })) });
  const f = {
    at: null,
    down: async (x, y) => { f.at = [x, y]; await send('touchStart', [[x, y]]); },
    to: async (x, y, steps = 8) => {
      const [x0, y0] = f.at;
      for (let i = 1; i <= steps; i += 1) { await send('touchMove', [[x0 + ((x - x0) * i) / steps, y0 + ((y - y0) * i) / steps]]); }
      f.at = [x, y];
    },
    up: () => send('touchEnd', []),
    cancel: () => send('touchCancel', []),
  };
  return f;
}

const middle = async (locator) => {
  const b = await locator.boundingBox();
  return [b.x + b.width / 2, b.y + b.height / 2, b];
};

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

test.describe('on an Android phone, by touch', () => {
  test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });
  test.skip(({ browserName }) => browserName !== 'chromium', 'touch is driven through Chromium\'s input pipeline');

  test('a long press lifts a card with one haptic tick, the cards it passes ease aside, and the drop lands it in the gap, kept after a reload', async ({ page }) => {
    await openDemo(page, { ua: UA.android, hint: 'Android' });
    await card(page, COPILOT).scrollIntoViewIfNeeded();
    const [x, y, box] = await middle(card(page, COPILOT).locator('.row-main'));
    const f = await finger(page);
    await f.down(x, y);
    await expect(card(page, COPILOT)).toHaveClass(/\blifted\b/);
    expect(await page.evaluate(() => window.__ticks)).toEqual([10]);
    await expect(said(page)).toHaveText(/^Picked up .+, position 1 of 4 in Sessions\.$/);
    // Two places down: Claude and Codex each ease up a place.
    const step = box.height + 2;
    await f.to(x, y + 2 * step + 4);
    await expect.poll(() => shifted(page, CLAUDE)).toBeCloseTo(-step, 0);
    await expect.poll(() => shifted(page, CODEX)).toBeCloseTo(-step, 0);
    expect(await shifted(page, DELEGATING)).toBe(0);
    await expect(page.locator('.drag-gap')).toHaveCount(1);
    await f.up();
    await expect.poll(() => order(page)).toEqual([CLAUDE, CODEX, COPILOT, DELEGATING]);
    await expect(said(page)).toHaveText('Moved to position 3 of 4 in Sessions.');
    // One tick, at the lift; and the release opened nothing.
    expect(await page.evaluate(() => window.__ticks)).toEqual([10]);
    await expect(view(page, COPILOT)).toBeHidden();
    await expect(page.locator('.lifted, .drag-gap')).toHaveCount(0);
    await page.reload();
    await page.waitForSelector('html[data-demo-ready]');
    await expect.poll(() => order(page)).toEqual([CLAUDE, CODEX, COPILOT, DELEGATING]);
  });

  test('a quick swipe scrolls the list and lifts nothing', async ({ page }) => {
    await openDemo(page, { ua: UA.android, hint: 'Android' });
    await many(page);
    const before = await order(page);
    const target = card(page, 'demo-many-3');
    await target.scrollIntoViewIfNeeded();
    const top = await page.evaluate(() => scrollY);
    const [x, y] = await middle(target.locator('.row-main'));
    const f = await finger(page);
    await f.down(x, y);
    await f.to(x, y - 240, 6);
    await f.up();
    await expect.poll(() => page.evaluate(() => scrollY)).toBeGreaterThan(top + 100);
    await page.waitForTimeout(500);
    await expect(page.locator('.lifted, .holding, .drag-gap')).toHaveCount(0);
    expect(await order(page)).toEqual(before);
    expect(await page.evaluate(() => window.__ticks)).toEqual([]);
  });

  test('the system taking the touch puts the card back, and so does Escape', async ({ page }) => {
    await openDemo(page, { ua: UA.android, hint: 'Android' });
    await card(page, COPILOT).scrollIntoViewIfNeeded();
    const [x, y, box] = await middle(card(page, COPILOT).locator('.row-main'));
    const f = await finger(page);
    await f.down(x, y);
    await expect(card(page, COPILOT)).toHaveClass(/\blifted\b/);
    await f.to(x, y + box.height + 6);
    await expect.poll(() => shifted(page, CLAUDE)).toBeLessThan(-box.height + 1);
    // A touchcancel: the pointer and its touches both went.
    await f.cancel();
    await expect(page.locator('.lifted, .drag-gap')).toHaveCount(0);
    await expect(said(page)).toHaveText('Cancelled. Back at position 1 of 4.');
    expect(await order(page)).toEqual(FIRST);
    await settled(page);
    expect(await shifted(page, CLAUDE)).toBe(0);

    await f.down(x, y);
    await expect(card(page, COPILOT)).toHaveClass(/\blifted\b/);
    await f.to(x, y + box.height + 6);
    await page.keyboard.press('Escape');
    await expect(page.locator('.lifted, .drag-gap')).toHaveCount(0);
    await f.up();
    expect(await order(page)).toEqual(FIRST);
    await expect(view(page, COPILOT)).toBeHidden();
  });

  test('a browser that takes the pointer while a card is held hands the drag to that finger\'s touches', async ({ page }) => {
    await openDemo(page, { ua: UA.android, hint: 'Android' });
    await card(page, COPILOT).scrollIntoViewIfNeeded();
    const [x, y, box] = await middle(card(page, COPILOT).locator('.row-main'));
    // As iOS and Android browsers may: pointercancel once the hold is a drag, the touches going on.
    const fire = (type, dy) => card(page, COPILOT).locator('.row-main').evaluate((el, { type, x, y, dy }) => {
      const t = new Touch({ identifier: 5, target: el, clientX: x, clientY: y + dy });
      const touches = type === 'touchend' ? [] : [t];
      el.dispatchEvent(new TouchEvent(type, { bubbles: true, cancelable: true, touches, targetTouches: touches, changedTouches: [t] }));
    }, { type, x, y, dy });
    const pointer = (type) => card(page, COPILOT).locator('.row-main').dispatchEvent(type, { pointerId: 9, pointerType: 'touch', isPrimary: true, clientX: x, clientY: y, bubbles: true });
    await pointer('pointerdown');
    await fire('touchstart', 0);
    await expect(card(page, COPILOT)).toHaveClass(/\blifted\b/);
    await pointer('pointercancel');
    for (const dy of [10, 30, box.height + 4, 2 * box.height + 8]) { await fire('touchmove', dy); }
    await expect(card(page, COPILOT)).toHaveClass(/\blifted\b/);
    await expect.poll(() => shifted(page, CODEX)).toBeLessThan(-box.height + 1);
    await fire('touchend', 2 * box.height + 8);
    await expect.poll(() => order(page)).toEqual([CLAUDE, CODEX, COPILOT, DELEGATING]);
  });

  test('held near the bottom edge the list scrolls under the card, and stops as it leaves the edge', async ({ page }) => {
    await openDemo(page, { ua: UA.android, hint: 'Android' });
    await many(page);
    // The first card, with the rest of the list running on below the bottom bar.
    await page.evaluate(() => scrollTo(0, 0));
    const first = (await order(page))[0];
    const held = card(page, first);
    await held.scrollIntoViewIfNeeded();
    const [x, y] = await middle(held.locator('.row-main'));
    const f = await finger(page);
    const bar = await page.locator('#nav').boundingBox();
    // Where the finger will end up, clear of the edge: the card there now is as far as the held
    // one could go without a scroll.
    const rest = Math.min(y + 120, bar.y - 120);
    const reach = await page.evaluate(({ x, rest }) => document.elementFromPoint(x, rest)?.closest('.session')?.dataset.id, { x, rest });
    const unscrolled = (await order(page)).indexOf(reach);
    expect(unscrolled).toBeGreaterThan(0);
    await f.down(x, y);
    await expect(held).toHaveClass(/\blifted\b/);
    const top = await page.evaluate(() => scrollY);
    // Into the zone above the bottom bar, near its edge.
    await f.to(x, bar.y - 6, 12);
    await expect.poll(() => page.evaluate(() => scrollY), { timeout: 3000 }).toBeGreaterThan(top + 150);
    // Out of the zone: it stops at once.
    await f.to(x, rest, 4);
    await page.waitForTimeout(100);
    const stopped = await page.evaluate(() => scrollY);
    await page.waitForTimeout(400);
    expect(await page.evaluate(() => scrollY)).toBe(stopped);
    await f.up();
    // It landed further down than any place it could reach without the scroll.
    await expect.poll(async () => (await order(page)).indexOf(first)).toBeGreaterThan(unscrolled + 1);
  });

  test('a long press released without moving opens the row menu, not the session', async ({ page }) => {
    await openDemo(page, { ua: UA.android, hint: 'Android' });
    await card(page, CODEX).scrollIntoViewIfNeeded();
    const [x, y] = await middle(card(page, CODEX).locator('.row-main'));
    const f = await finger(page);
    await f.down(x, y);
    await expect(card(page, CODEX)).toHaveClass(/\blifted\b/);
    await f.up();
    await expect(card(page, CODEX).getByRole('menu')).toBeVisible();
    await expect(view(page, CODEX)).toBeHidden();
    expect(await order(page)).toEqual(FIRST);
  });

  test('a held card let go over Needs you puts the card back and never presses its answers', async ({ page }) => {
    await openDemo(page, { ua: UA.android, hint: 'Android', query: '&answerDelay=300' });
    await page.evaluate(() => {
      const d = window.agentBridgeDemo;
      const t = new Date(Date.now() + 1000).toISOString();
      const question = {
        id: 'r1#0123456789abcdef', kind: 'approval', message: 'Allow **shell**?', askedAt: t,
        fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'approve', label: 'Approve once' }, { value: 'deny', label: 'Deny' }] }],
      };
      d.host.upsert({ ...d.host.get('8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35'), status: 'waiting', statusSince: t, updatedAt: t, question });
    });
    const approve = page.locator('#needs-you .needs-approve').first();
    await expect(approve).toBeVisible();
    await expect(page.locator('#needs-you .needs-you-list[data-arriving]')).toHaveCount(0);
    await approve.evaluate((b) => { b.addEventListener('click', () => { window.__pressed = true; }); });
    await card(page, COPILOT).scrollIntoViewIfNeeded();
    const [ax, ay] = await middle(approve);
    const [x, y] = await middle(card(page, COPILOT).locator('.row-main'));
    const f = await finger(page);
    await f.down(x, y);
    await expect(card(page, COPILOT)).toHaveClass(/\blifted\b/);
    await f.to(ax, ay, 10);
    await expect(card(page, COPILOT)).toHaveClass(/\boutside\b/);
    await f.up();
    await expect(page.locator('.lifted')).toHaveCount(0);
    await page.waitForTimeout(300);
    expect(await page.evaluate(() => window.__pressed ?? false)).toBe(false);
    expect(await page.evaluate(() => window.agentBridgeDemo.host.answerLog)).toEqual([]);
    expect(await order(page)).toEqual(FIRST);
  });

  test('a held card whose session ends goes, and the release presses nothing that moved up into its place', async ({ page }) => {
    await openDemo(page, { ua: UA.android, hint: 'Android' });
    await card(page, COPILOT).scrollIntoViewIfNeeded();
    const [x, y] = await middle(card(page, COPILOT).locator('.row-main'));
    const f = await finger(page);
    await f.down(x, y);
    await expect(card(page, COPILOT)).toHaveClass(/\blifted\b/);
    await page.evaluate((id) => window.agentBridgeDemo.remove(id), COPILOT);
    await expect(card(page, COPILOT)).toHaveCount(0);
    await expect(page.locator('.lifted, .drag-gap')).toHaveCount(0);
    expect(await order(page)).toEqual([CLAUDE, CODEX, DELEGATING]);
    await f.up();
    await page.waitForTimeout(300);
    await expect(view(page, CLAUDE)).toBeHidden();
  });

  test('a tap on the grip says how to move a session, and opens nothing', async ({ page }) => {
    await openDemo(page, { ua: UA.android, hint: 'Android' });
    const grip = card(page, CLAUDE).locator('.drag-handle');
    await expect(grip).toBeVisible();
    const b = await grip.boundingBox();
    expect(b.width).toBe(24);
    await grip.tap();
    await expect(page.locator('.grip-hint')).toHaveText('Press and hold a session to move it. Or use Move up and Move down in its menu.');
    await expect(view(page, CLAUDE)).toBeHidden();
    await expect(page.locator('.lifted')).toHaveCount(0);
  });
});

test.describe('the long press, on each platform', () => {
  test.use({ viewport: { width: 390, height: 664 }, hasTouch: true });

  for (const [name, ua, hint, ms] of [['Android', UA.android, 'Android', 400], ['iOS', UA.iphone, null, 500]]) {
    test(`on ${name} a card lifts after ${ms} ms held, not before, and the system's own long press is held back`, async ({ page }) => {
      await openDemo(page, { ua, hint });
      await page.clock.install();
      const main = card(page, CODEX).locator('.row-main');
      const [x, y] = await middle(main);
      await main.dispatchEvent('pointerdown', { pointerId: 3, pointerType: 'touch', isPrimary: true, clientX: x, clientY: y, bubbles: true });
      await page.clock.runFor(ms - 20);
      await expect(card(page, CODEX)).not.toHaveClass(/\blifted\b/);
      await page.clock.runFor(40);
      await expect(card(page, CODEX)).toHaveClass(/\blifted\b/);
      await page.keyboard.press('Escape');
      // No text selection, and the browser may only scroll it. (iOS's callout, loupe and link
      // preview are held back by -webkit-touch-callout, which only iOS's WebKit reads: a real
      // iPhone checks that.)
      const css = await card(page, CODEX).evaluate((e) => {
        const s = getComputedStyle(e);
        return { select: s.userSelect || s.webkitUserSelect, action: getComputedStyle(e.querySelector('.row-main')).touchAction };
      });
      expect(css.select).toBe('none');
      expect(css.action).toMatch(/pan-y/);
    });
  }
});

for (const width of [1440]) {
  test.describe(`at ${width} px, with a mouse`, () => {
    test.use({ viewport: { width, height: 900 } });

    async function grab(page, id) {
      await card(page, id).hover();
      const [x, y] = await middle(card(page, id).locator('.drag-handle'));
      await page.mouse.move(x, y);
      await page.mouse.down();
      await expect(card(page, id)).toHaveClass(/\blifted\b/);
      return [x, y];
    }

    test('the card itself lifts once it has moved 4 px, and a click still opens it', async ({ page }) => {
      await openDemo(page);
      const [x, y, box] = await middle(card(page, CODEX).locator('.row-main'));
      await page.mouse.move(x, y);
      await page.mouse.down();
      await page.mouse.move(x, y + 3);
      await expect(card(page, CODEX)).not.toHaveClass(/\blifted\b/);
      await page.mouse.move(x, y + 8);
      await expect(card(page, CODEX)).toHaveClass(/\blifted\b/);
      await page.mouse.move(x, y + box.height + 10, { steps: 4 });
      await page.mouse.up();
      await expect.poll(() => order(page)).toEqual([COPILOT, CLAUDE, DELEGATING, CODEX]);
      // The drag's release opened nothing; a click does.
      await expect(view(page, CODEX)).toBeHidden();
      await settled(page);
      await card(page, CLAUDE).locator('.row-main').click();
      await expect(view(page, CLAUDE)).toBeVisible();
    });

    test('the system taking the pointer puts the card back', async ({ page }) => {
      await openDemo(page);
      const [x, y] = await grab(page, CLAUDE);
      const h = (await card(page, CLAUDE).boundingBox()).height;
      await page.mouse.move(x, y + h + 10, { steps: 4 });
      await expect.poll(() => shifted(page, CODEX)).toBeLessThan(-h + 2);
      await page.evaluate(() => document.dispatchEvent(new PointerEvent('pointercancel', { pointerId: 1, pointerType: 'mouse', isPrimary: true, bubbles: true })));
      await expect(page.locator('.lifted, .drag-gap')).toHaveCount(0);
      await page.mouse.up();
      expect(await order(page)).toEqual(FIRST);
      await expect(said(page)).toHaveText('Cancelled. Back at position 2 of 4.');
    });

    test('Alt+Down moves the card and the card it passes together', async ({ page }) => {
      await openDemo(page);
      await page.evaluate(() => document.documentElement.style.setProperty('--ab-motion-reorder-duration', '5000ms'));
      await card(page, CLAUDE).locator('.row-main').press('Alt+ArrowDown');
      expect(await order(page)).toEqual([COPILOT, CODEX, CLAUDE, DELEGATING]);
      const sliding = await page.evaluate(() => document.getAnimations()
        .filter((a) => a.id === 'ab-motion-reorder' && a.playState === 'running').map((a) => a.effect.target.dataset.id).sort());
      expect(sliding).toEqual([CLAUDE, CODEX].sort());
    });

    test('a card dragged across the Sessions header is pinned where it is dropped, and the header and a chip say so first', async ({ page }) => {
      await openDemo(page);
      await card(page, CODEX).locator('.row-main').click({ button: 'right' });
      await card(page, CODEX).getByRole('menuitem', { name: 'Pin' }).click();
      await expect.poll(() => order(page)).toEqual([CODEX, COPILOT, CLAUDE, DELEGATING]);
      await settled(page);
      const [x] = await grab(page, DELEGATING);
      const pinned = await card(page, CODEX).boundingBox();
      // Just under Codex: the end of Pinned.
      await page.mouse.move(x, pinned.y + pinned.height + 14, { steps: 8 });
      await expect(page.locator('.list-group[data-group="pinned"]')).toHaveText('Pinned \u00b7 drop to pin');
      await expect(page.locator('.list-group[data-group="pinned"]')).toHaveClass(/\bdrop-target\b/);
      await expect(card(page, DELEGATING).locator('.drag-chip')).toHaveText('Pin');
      await expect(card(page, DELEGATING).locator('.drag-chip')).toBeVisible();
      await expect(said(page)).toHaveText('Pinned, position 2 of 2. Drops as pinned.');
      await page.mouse.up();
      await expect.poll(() => order(page)).toEqual([CODEX, DELEGATING, COPILOT, CLAUDE]);
      await expect(card(page, DELEGATING)).toHaveAttribute('data-pinned', 'true');
      await expect(said(page)).toHaveText('Pinned. Position 2 of 2 in Pinned.');
      await expect(page.locator('.list-group')).toHaveText(['Pinned', 'Sessions']);
      await settled(page);

      // The last pinned card held out of Pinned leaves its place open to come back to.
      await card(page, DELEGATING).locator('.row-main').click({ button: 'right' });
      await card(page, DELEGATING).getByRole('menuitem', { name: 'Unpin' }).click();
      await settled(page);
      await grab(page, CODEX);
      const claude = await card(page, CLAUDE).boundingBox();
      await page.mouse.move(x, claude.y + claude.height / 2, { steps: 10 });
      await expect(page.locator('.drag-keep')).toHaveText('Drop here to keep it pinned');
      await expect(card(page, CODEX).locator('.drag-chip')).toHaveText('Unpin');
      await page.mouse.up();
      await expect(page.locator('.drag-keep')).toHaveCount(0);
      await expect(card(page, CODEX)).not.toHaveAttribute('data-pinned', 'true');
      await expect(said(page)).toHaveText(/^Unpinned\. Position \d of 4 in Sessions\.$/);
      await expect(page.locator('.list-group')).toHaveCount(0);
    });

    test('a key during a drop\'s settle moves the card on from its new place, and a press on the gap opens the card landing there', async ({ page }) => {
      await openDemo(page);
      await page.evaluate(() => document.documentElement.style.setProperty('--ab-motion-reorder-duration', '5000ms'));
      const claude = await card(page, CLAUDE).boundingBox();
      const [x, y] = await grab(page, DELEGATING);
      const top = (await card(page, DELEGATING).boundingBox()).y;
      // Let go 20 px above Claude's place, which is where it lands: it settles from there.
      await page.mouse.move(x, y + (claude.y - 20 - top), { steps: 8 });
      await page.mouse.up();
      expect(await order(page)).toEqual([COPILOT, DELEGATING, CLAUDE, CODEX]);
      // The foot of the gap, which the card settling from above doesn't cover yet.
      await page.mouse.click(claude.x + claude.width / 2, claude.y + claude.height - 6);
      await expect(view(page, DELEGATING)).toBeVisible();
      await expect(card(page, DELEGATING)).toHaveClass(/\bselected\b/);
      await card(page, DELEGATING).locator('.row-main').press('Alt+ArrowDown');
      expect(await order(page)).toEqual([COPILOT, CLAUDE, DELEGATING, CODEX]);
      await page.evaluate(() => document.documentElement.style.removeProperty('--ab-motion-reorder-duration'));
      await page.waitForTimeout(300);
      expect(await order(page)).toEqual([COPILOT, CLAUDE, DELEGATING, CODEX]);
    });

    test('a drag let go over Needs you never presses its answers', async ({ page }) => {
      await openDemo(page, { query: '&answerDelay=300' });
      await page.evaluate(() => {
        const d = window.agentBridgeDemo;
        const t = new Date(Date.now() + 1000).toISOString();
        const question = {
          id: 'r1#0123456789abcdef', kind: 'approval', message: 'Allow **shell**?', askedAt: t,
          fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'approve', label: 'Approve once' }, { value: 'deny', label: 'Deny' }] }],
        };
        d.host.upsert({ ...d.host.get('8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35'), status: 'waiting', statusSince: t, updatedAt: t, question });
      });
      const approve = page.locator('#needs-you .needs-approve').first();
      await expect(approve).toBeVisible();
      await expect(page.locator('#needs-you .needs-you-list[data-arriving]')).toHaveCount(0);
      await approve.evaluate((b) => { b.addEventListener('click', () => { window.__pressed = true; }); });
      const [x, y] = await grab(page, CODEX);
      const [ax, ay] = await middle(approve);
      await page.mouse.move(x, y - 40, { steps: 4 });
      await page.mouse.move(ax, ay, { steps: 8 });
      await expect(card(page, CODEX)).toHaveClass(/\boutside\b/);
      await expect(card(page, CODEX).locator('.drag-chip')).toHaveText('Cancel');
      await page.mouse.up();
      await expect(page.locator('.lifted')).toHaveCount(0);
      expect(await page.evaluate(() => window.__pressed ?? false)).toBe(false);
      expect(await page.evaluate(() => window.agentBridgeDemo.host.answerLog)).toEqual([]);
      expect(await order(page)).toEqual(FIRST);
    });

    test('with reduced motion a lifted card has no scale, shadow or tint, and the cards it passes jump', async ({ page }) => {
      await page.emulateMedia({ reducedMotion: 'reduce' });
      await openDemo(page);
      const [x, y] = await grab(page, DELEGATING);
      const lifted = await card(page, DELEGATING).evaluate((e) => {
        const s = getComputedStyle(e);
        return { scale: s.scale, shadow: s.boxShadow, outline: `${s.outlineStyle} ${s.outlineWidth}` };
      });
      expect(lifted).toEqual({ scale: 'none', shadow: 'none', outline: 'solid 2px' });
      // A card held moves the others by its own height and the gap.
      const h = (await card(page, DELEGATING).boundingBox()).height;
      await page.mouse.move(x, y - 2 * (h + 2) - 4, { steps: 6 });
      // In place at once, with nothing easing there.
      expect(await shifted(page, CLAUDE)).toBeCloseTo(h + 2, 0);
      expect(await running(page)).toEqual([]);
      await page.mouse.up();
      expect(await order(page)).toEqual([COPILOT, DELEGATING, CLAUDE, CODEX]);
      expect(await running(page)).toEqual([]);
    });

    for (const colorScheme of ['light', 'dark']) {
    test(`with a card lifted and crossing into Pinned the list passes axe, in ${colorScheme}`, async ({ page }) => {
      await page.emulateMedia({ colorScheme });
      await openDemo(page);
      await card(page, CODEX).locator('.row-main').click({ button: 'right' });
      await card(page, CODEX).getByRole('menuitem', { name: 'Pin' }).click();
      await settled(page);
      const [x] = await grab(page, DELEGATING);
      const pinned = await card(page, CODEX).boundingBox();
      await page.mouse.move(x, pinned.y + pinned.height + 14, { steps: 8 });
      await expect(card(page, DELEGATING).locator('.drag-chip')).toHaveText('Pin');
      await page.waitForTimeout(300);
      const { violations } = await new AxeBuilder({ page }).include('#list-pane')
        .withTags(['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22a', 'wcag22aa', 'best-practice']).analyze();
      // A row's name is its glyph's "title, status, machine" (ui.md §9; #365, known to axe.spec).
      const found = violations.filter((v) => ['serious', 'critical'].includes(v.impact))
        .flatMap((v) => v.nodes.filter((n) => !(v.id === 'label-content-name-mismatch' && n.html.includes('row-main'))).map((n) => `${v.id}: ${n.target.join(' ')}`));
      expect(found).toEqual([]);
      await page.keyboard.press('Escape');
      await page.mouse.up();
    });
    }
  });
}
