// A phone's session header collapses on scroll (#631, ui.md §6.2, §5.3 Collapse, §9). Below 760 px
// the header floats over the conversation; near the top it rides the scroll, mid-conversation it
// goes after 32 px down and comes back after 64 px up. The thresholds' arithmetic is unit-tested
// (test/unit/collapse-header.test.mjs); here the page: what a person scrolling sees, what they
// hear, and that from 760 px up nothing is different.
import { test, expect } from '@playwright/test';
import { open } from './nav.mjs';

const CODEX = '019a1b2c-codex-example';
const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const later = (ms = 1000) => new Date(Date.now() + ms).toISOString();
const LONG = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');

async function openDemo(page) {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto('?demo=1&static=1&delegates=1');
  await page.waitForSelector('html[data-demo-ready]');
}

// A test that captured may see the screenshot machinery's own style element refused (WebKit);
// the page's own violations were checked before it, by shot().
let captured = false;
const own = async (page) => {
  const seen = await page.evaluate(() => window.__cspViolations ?? []);
  return captured ? seen.filter((v) => v !== 'style-src-elem inline') : seen;
};
test.beforeEach(() => { captured = false; });
test.afterEach(async ({ page }) => {
  expect(await own(page)).toEqual([]);
});

const upsert = (page, id, change) => page.evaluate(({ id, change }) => {
  const d = window.agentBridgeDemo;
  const s = d.host.get(id) ?? { id };
  d.upsert({ ...s, ...change, activity: { ...(s.activity ?? {}), ...(change.activity ?? {}) } });
}, { id, change });

const choices = (id) => ({
  id, kind: 'form', message: 'Which?', askedAt: later(60_000),
  fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
});

// Codex's session, its conversation long enough to scroll with room to spare below, opened and
// measured by the header.
async function session(page) {
  await openDemo(page);
  await upsert(page, CODEX, { activity: { response: LONG, reasoning: null, userMessage: null, currentTool: null }, question: null, updatedAt: later() });
  const v = await open(page, CODEX);
  await v.locator('.stream').evaluate((el) => { el.style.paddingBottom = '1500px'; });
  await expect.poll(() => v.evaluate((el) => el.style.getPropertyValue('--head-h'))).not.toBe('');
  await settle(v);
  // A session opens at its latest message; these tests start from the top of the conversation.
  await expect(v.locator('.history-turn').first()).toBeAttached();
  await settle(v);
  await v.locator('.stream-scroll').evaluate((sc) => { sc.scrollTop = 0; });
  await settle(v);
  return v;
}

// The conversation not moving: an app-made scroll (to a question) has run its course, too.
const settle = (v) => v.locator('.stream-scroll').evaluate((sc) => new Promise((resolve) => {
  let at = sc.scrollTop;
  let still = 0;
  const tick = () => {
    still = sc.scrollTop === at ? still + 1 : 0;
    at = sc.scrollTop;
    if (still >= 15) { resolve(); } else { requestAnimationFrame(tick); }
  };
  requestAnimationFrame(tick);
}));

// What the header shows and covers now, and where the conversation is.
const read = (v) => v.evaluate((el) => {
  const sc = el.querySelector('.stream-scroll');
  const top = el.querySelector('.session-top');
  const bar = el.querySelector('.session-bar');
  const px = (name) => parseFloat(el.style.getPropertyValue(name) || '0');
  return {
    ty: px('--ty'), covered: px('--covered'), full: top.offsetHeight, bar: bar.offsetHeight, scroll: sc.scrollTop,
    room: top.offsetHeight - bar.offsetHeight, solid: el.hasAttribute('data-solid'), titled: el.hasAttribute('data-titled'),
  };
});

// A person's scroll: their touch or wheel first, then one scroll event, so the next move is the next.
const scrollTo = (v, y, input = true) => v.locator('.stream-scroll').evaluate((sc, [y, input]) => new Promise((resolve) => {
  if (sc.scrollTop === y) { resolve(); return; }
  if (input) { sc.dispatchEvent(new WheelEvent('wheel', { deltaY: y - sc.scrollTop, bubbles: true })); }
  sc.addEventListener('scroll', () => requestAnimationFrame(() => requestAnimationFrame(() => resolve())), { once: true });
  sc.scrollTo({ top: y, behavior: 'instant' });
}), [y, input]);

// Scrolled to y, then the header folded to the bar.
async function folded(v, y = 300) {
  await scrollTo(v, y);
  const m = await read(v);
  expect(m.ty).toBe(-m.room);
  return m;
}

// The header's motion run out (the slide or cross-fade, and the bar's fill and title fading), so
// what is read or captured is where it rests. A working glyph turns for ever; that is not waited on.
async function rested(v) {
  await expect(v).not.toHaveClass(/\b(animating|fading)\b/);
  await v.evaluate((el) => Promise.all(el.getAnimations({ subtree: true })
    .filter((a) => Number.isFinite(a.effect?.getComputedTiming().endTime))
    .map((a) => a.finished.catch(() => {}))));
}

// A capture for the review (#718). The caret left as it is: hiding it injects a style the page's
// CSP refuses in WebKit, which still adds one of its own; the page's violations are checked first.
async function shot(page) {
  expect(await own(page)).toEqual([]);
  captured = true;
  return page.screenshot({ caret: 'initial' });
}

// The header at rest, whole: its title, meta line and tabs each in a box of their own, and nothing
// of the bar drawn over them (#631 review: a return captured mid-fade overlapped).
async function wholeHeader(v) {
  const boxes = await v.evaluate((el) => ['.session-title', '.status-line', '.view-tabs'].map((sel) => {
    const r = el.querySelector(`.session-top ${sel}`).getBoundingClientRect();
    return { sel, top: r.top, bottom: r.bottom, left: r.left, right: r.right, height: r.height };
  }));
  for (const b of boxes) { expect(b.height, b.sel).toBeGreaterThan(0); }
  for (let i = 0; i < boxes.length; i += 1) {
    for (let j = i + 1; j < boxes.length; j += 1) {
      const [a, b] = [boxes[i], boxes[j]];
      const overlap = Math.min(a.bottom, b.bottom) - Math.max(a.top, b.top) > 0.5 && Math.min(a.right, b.right) - Math.max(a.left, b.left) > 0.5;
      expect(overlap, `${a.sel} and ${b.sel} overlap`).toBe(false);
    }
  }
  await expect(v.locator('.bar-title')).toBeHidden();
  expect(await v.locator('.session-bar').evaluate((bar) => getComputedStyle(bar, '::before').opacity)).toBe('0');
}

for (const scheme of ['light', 'dark']) {
  test.describe(`on a phone, ${scheme}`, () => {
    test.use({ viewport: { width: 390, height: 844 }, hasTouch: true, colorScheme: scheme });

    test('a session opens with the whole header, and a pinned bar behind it holds Back and More at a tap each', async ({ page }) => {
      const v = await session(page);
      const m = await read(v);
      expect(m).toMatchObject({ ty: 0, scroll: 0, solid: false, titled: false });
      expect(m.bar).toBeGreaterThanOrEqual(48);
      for (const control of ['.back', '.more']) {
        const box = await v.locator(`.session-bar ${control}`).boundingBox();
        expect(box.width).toBeGreaterThanOrEqual(44);
        expect(box.height).toBeGreaterThanOrEqual(44);
      }
      // The title and the meta are the header's own: not drawn twice, and the delegate strip not at all.
      await expect(v.locator('.session-top .session-title')).toBeVisible();
      await expect(v.locator('.delegate-strip')).toBeHidden();
      await expect(v.locator('.bar-title')).toBeHidden();
    });

    test('near the top the header rides the scroll one for one, and the bar fills and names the session once 40 px are under it', async ({ page }, info) => {
      const v = await session(page);
      for (const y of [10, 30, 39]) {
        await scrollTo(v, y);
        expect(await read(v), `at ${y}`).toMatchObject({ ty: -y, solid: false, titled: false });
        expect((await v.locator('.session-top').boundingBox()).y, `at ${y}`).toBeCloseTo(-y, 0);
      }
      await info.attach(`riding-${scheme}.png`, { body: await shot(page), contentType: 'image/png' });
      await scrollTo(v, 40);
      expect(await read(v)).toMatchObject({ ty: -40, solid: true, titled: true });
      await expect(v.locator('.bar-title')).toBeVisible();
      await expect(v.locator('.bar-title-text')).toHaveText(await v.locator('.session-title').textContent());
      // The bar stops the header where it is: the whole of what is above the bar.
      const m = await folded(v, 400);
      expect(m.covered).toBe(m.bar);
      expect(m.bar).toBeGreaterThanOrEqual(48);
      await rested(v);
      await info.attach(`folded-${scheme}.png`, { body: await shot(page), contentType: 'image/png' });
    });

    test('mid-conversation 64 px up brings the header back and 32 px down hides it, and a change of direction restarts the count', async ({ page }, info) => {
      const v = await session(page);
      const { room } = await folded(v, 300);
      const is = async (ty, why) => expect((await read(v)).ty, why).toBe(ty);
      for (const y of [270, 240, 237]) { await scrollTo(v, y); await is(-room, `up to ${y}`); }
      await scrollTo(v, 236);
      await is(0, '64 px up');
      await rested(v);
      await wholeHeader(v);
      await info.attach(`returned-${scheme}.png`, { body: await shot(page), contentType: 'image/png' });
      await scrollTo(v, 267);
      await is(0, '31 px down');
      await scrollTo(v, 268);
      await is(-room, '32 px down');
      // 40 up, 5 down, 40 up: 80 up in all, but never 64 running.
      await scrollTo(v, 228);
      await scrollTo(v, 233);
      await scrollTo(v, 193);
      await is(-room, 'a change of direction restarts the count');
      await scrollTo(v, 128);
      await is(0, '65 px up since the last change');
    });

    test('a tap on the bar\'s title brings the header back', async ({ page }) => {
      const v = await session(page);
      const { room } = await folded(v, 300);
      expect(room).toBeGreaterThan(40);
      await v.locator('.bar-title-text').tap();
      await expect.poll(async () => (await read(v)).ty).toBe(0);
      expect((await read(v)).scroll).toBe(300);
    });

    test('content growing below, and a scroll the app makes to a question, leave the header as it was', async ({ page }) => {
      const v = await session(page);
      const { room } = await folded(v, 300);
      await upsert(page, CODEX, { activity: { response: `${LONG}\n\nAnd more.` }, updatedAt: later(2000) });
      await expect(v.locator('.stream-scroll')).toContainText('And more.');
      await settle(v);
      expect((await read(v)).ty).toBe(-room);
      // At the end of the conversation a question arrives and is brought into sight, scrolled up
      // to by the app: 8 px below the bar, with the header still folded.
      await v.locator('.stream-scroll').evaluate((sc) => { sc.scrollTop = sc.scrollHeight; });
      await settle(v);
      const before = await read(v);
      expect(before.ty).toBe(-room);
      // Nothing under the pointer: someone reading holds a question where it arrives.
      await page.mouse.move(1, 1);
      await upsert(page, CODEX, { status: 'waiting', statusSince: later(3000), updatedAt: later(3000), question: choices('q-folded') });
      await expect(v.locator('.question-slot .question')).toBeVisible();
      await settle(v);
      const after = await read(v);
      expect(after.scroll).toBeLessThan(before.scroll);
      expect(after.ty).toBe(-room);
      const gap = await v.evaluate((el) => el.querySelector('.question-slot .question').getBoundingClientRect().top - el.querySelector('.stream-scroll').getBoundingClientRect().top);
      expect(gap).toBeGreaterThanOrEqual(after.covered + 8 - 1);
      expect(gap).toBeLessThanOrEqual(after.covered + 8 + 1);
    });

    test('the bar\'s glyph is the session\'s, and needs while it waits on you', async ({ page }) => {
      await openDemo(page);
      const v = await open(page, COPILOT);
      await expect(v.locator('.status-line .glyph')).toHaveAttribute('data-glyph', 'needs');
      await expect(v.locator('.bar-title .glyph')).toHaveAttribute('data-glyph', 'needs');
      const calm = await open(page, '6f1c2b7e-claude-example');
      const meta = await calm.locator('.status-line .glyph').getAttribute('data-glyph');
      expect(meta).not.toBe('needs');
      await expect(calm.locator('.bar-title .glyph')).toHaveAttribute('data-glyph', meta);
    });

    test('one heading, the bar\'s copy drawn only, and nothing of the folding announced', async ({ page }) => {
      const v = await session(page);
      await expect(v.locator('h2')).toHaveCount(1);
      await expect(v.locator('.session-bar :is(h1, h2, h3, h4, h5, h6, [role="heading"])')).toHaveCount(0);
      await expect(v.locator('.bar-title')).toHaveAttribute('aria-hidden', 'true');
      expect(await v.locator('.bar-title').evaluate((el) => ['aria-label', 'aria-labelledby', 'aria-describedby', 'title', 'role'].filter((a) => el.hasAttribute(a)))).toEqual([]);
      await expect(v.locator('.session-bar')).not.toHaveAttribute('aria-hidden', /.*/);
      // The status is read from the meta line; and nothing in the header is a live region.
      await expect(v.locator('.status-line')).not.toHaveAttribute('aria-hidden', /.*/);
      await expect(v.locator('.session-head :is([aria-live], [role="status"], [role="alert"]):not([aria-live="off"])')).toHaveCount(0);
      await folded(v);
      await expect(v.locator('h2')).toHaveCount(1);
      await expect(v.locator('.session-top .session-title')).toBeAttached();
      await expect(v.getByRole('heading', { level: 2 })).toHaveCount(1);
    });

    test('folded, the header is still read and tabbed to, Back and More, the tabs, then the conversation', async ({ page }) => {
      const v = await session(page);
      await v.locator('.stream-scroll').evaluate((scroll) => {
        const b = document.createElement('button');
        b.id = 'probe';
        b.textContent = 'Probe';
        scroll.prepend(b);
      });
      await folded(v);
      await expect(v.getByRole('tab', { name: 'Conversation' })).toBeAttached();
      await v.locator('.session-bar .back').focus();
      const seen = [];
      for (let i = 0; i < 4; i += 1) {
        seen.push(await page.evaluate(() => {
          const a = document.activeElement;
          if (a.id === 'probe') { return 'conversation'; }
          return [...a.classList].find((c) => ['back', 'more', 'view-tab'].includes(c)) ?? `${a.tagName}.${a.className}`;
        }));
        await page.keyboard.press('Tab');
      }
      expect(seen).toEqual(['back', 'more', 'view-tab', 'conversation']);
    });

    test('the Delegates tab says how many run, leads with needs while one asks, and changes in place', async ({ page }, info) => {
      await openDemo(page);
      const v = await open(page, 'demo-delegates');
      const tab = v.locator('.view-tab').nth(1);
      const parts = async () => ({
        name: await tab.evaluate((t) => t.getAttribute('aria-label') ?? t.textContent),
        count: (await tab.locator('.tab-count').textContent()).trim(),
        needs: await tab.locator('.glyph').isVisible(),
        height: (await tab.boundingBox()).height,
        row: (await v.locator('.session-top .view-tabs').boundingBox()).height,
      });
      const set = (statuses) => page.evaluate(({ statuses, at }) => {
        const d = window.agentBridgeDemo;
        const s = d.host.get('demo-delegates');
        d.upsert({ ...s, updatedAt: at, subagents: s.subagents.map((a) => (statuses[a.id] ? { ...a, ...statuses[a.id] } : a)) });
      }, { statuses, at: later() });
      // Two the main agent started are running; the one the witness started is the witness's.
      await expect(tab).toHaveAccessibleName('Delegates, 2 running');
      const at = await parts();
      expect(at).toMatchObject({ count: '2', needs: false });
      await set({ 'demo-agent-witness': { status: 'waiting' } });
      await expect(tab).toHaveAccessibleName('Delegates, 2 running, 1 asking');
      await expect(tab.locator('.glyph')).toHaveAttribute('data-glyph', 'needs');
      expect(await parts()).toMatchObject({ count: '2', needs: true, height: at.height, row: at.row });
      await info.attach(`delegates-tab-asking-${scheme}.png`, { body: await shot(page), contentType: 'image/png' });
      // A nested delegate's question waits on the person as much as a top one's.
      await set({ 'demo-agent-witness': { status: 'working' }, 'demo-agent-grep': { status: 'waiting' } });
      await expect(tab).toHaveAccessibleName('Delegates, 2 running, 1 asking');
      // None running: no count, and the tab just "Delegates", still the same height.
      await set(Object.fromEntries(['demo-agent-witness', 'demo-agent-suite', 'demo-agent-grep'].map((id) => [id, { status: 'ended', outcome: 'done' }])));
      await expect(tab).toHaveAccessibleName('Delegates');
      expect(await parts()).toMatchObject({ name: 'Delegates', count: '', needs: false, height: at.height, row: at.row });
      // From 760 the strip says it: the tab is plain "Delegates" however many run, and a phone's
      // width again brings the count back.
      await set({ 'demo-agent-witness': { status: 'waiting' }, 'demo-agent-suite': { status: 'working' } });
      await expect(tab).toHaveAccessibleName('Delegates, 2 running, 1 asking');
      await page.setViewportSize({ width: 900, height: 844 });
      await expect(tab).toHaveAccessibleName('Delegates');
      await expect(tab).toHaveText('Delegates');
      await expect(tab.locator('.glyph')).toBeHidden();
      await page.setViewportSize({ width: 390, height: 844 });
      await expect(tab).toHaveAccessibleName('Delegates, 2 running, 1 asking');
    });

    test('a tab\'s focus ring is whole: the header, which scrolls within itself, keeps room for it', async ({ page }, info) => {
      await openDemo(page);
      const v = await open(page, 'demo-delegates');
      await v.locator('.session-bar .more').focus();
      await page.keyboard.press('Tab');
      const tab = v.locator('.view-tab[aria-selected="true"]');
      await expect(tab).toBeFocused();
      await rested(v);
      const ring = await tab.evaluate((t) => {
        const cs = getComputedStyle(t);
        const reach = parseFloat(cs.outlineWidth) + parseFloat(cs.outlineOffset);
        const r = t.getBoundingClientRect();
        const clip = t.closest('.session-top').getBoundingClientRect();
        return { style: cs.outlineStyle, reach, below: clip.bottom - r.bottom, left: r.left - clip.left, right: clip.right - r.right, above: r.top - clip.top };
      });
      expect(ring.style).toBe('solid');
      expect(ring.reach).toBe(5);
      for (const side of ['below', 'left', 'right', 'above']) { expect(ring[side], side).toBeGreaterThanOrEqual(ring.reach); }
      await info.attach(`tab-focus-${scheme}.png`, { body: await shot(page), contentType: 'image/png' });
    });

    test('the tabs are one stop: Left and Right wrap, Home and End go to the first and last, and the conversation stays where it was', async ({ page }) => {
      const v = await session(page);
      await folded(v);
      const names = await v.getByRole('tab').evaluateAll((tabs) => tabs.filter((t) => !t.hidden).map((t) => t.textContent));
      expect(names.length).toBeGreaterThanOrEqual(2);
      const [first, last] = [names[0], names.at(-1)];
      await v.getByRole('tab', { name: first }).focus();
      const scroll = (await read(v)).scroll;
      await page.keyboard.press('End');
      await expect(v.getByRole('tab', { name: last })).toBeFocused();
      await expect(v.getByRole('tab', { name: last })).toHaveAttribute('aria-selected', 'true');
      await page.keyboard.press('ArrowRight');
      await expect(v.getByRole('tab', { name: first })).toBeFocused();
      await page.keyboard.press('ArrowLeft');
      await expect(v.getByRole('tab', { name: last })).toBeFocused();
      await page.keyboard.press('Home');
      await expect(v.getByRole('tab', { name: first })).toBeFocused();
      await expect(v.getByRole('tab', { name: first })).toHaveAttribute('aria-selected', 'true');
      await expect(v.locator('[role=tab][tabindex="0"]')).toHaveCount(1);
      expect((await read(v)).scroll).toBe(scroll);
    });

    test('focus coming into the header expands it and holds it there until focus leaves', async ({ page }) => {
      const v = await session(page);
      const { room } = await folded(v, 300);
      await v.locator('.session-bar .back').focus();
      await expect.poll(async () => (await read(v)).ty).toBe(0);
      // Scrolling with focus in the header leaves it whole.
      await scrollTo(v, 420, false);
      await scrollTo(v, 520, false);
      expect((await read(v)).ty).toBe(0);
      await v.locator('.stream-scroll').evaluate((sc) => { sc.tabIndex = -1; sc.focus(); });
      await scrollTo(v, 620);
      await scrollTo(v, 700);
      await expect.poll(async () => (await read(v)).ty).toBe(-room);
    });

    test('scroll-padding-top is what the header covers, plus 8', async ({ page }) => {
      const v = await session(page);
      const padding = () => v.locator('.stream-scroll').evaluate((sc) => parseFloat(getComputedStyle(sc).scrollPaddingTop));
      let m = await read(v);
      expect(await padding()).toBeCloseTo(m.full + 8, 0);
      m = await folded(v);
      expect(await padding()).toBeCloseTo(m.covered + 8, 0);
      expect(m.covered).toBe(m.bar);
      await scrollTo(v, 20);
      m = await read(v);
      expect(await padding()).toBeCloseTo(m.covered + 8, 0);
    });

    test('Home, End, Page Up and Page Down are scrolling, and Home brings the full header back', async ({ page }) => {
      const v = await session(page);
      const { room } = await read(v);
      const sc = v.locator('.stream-scroll');
      await sc.evaluate((el) => { el.tabIndex = -1; el.focus(); });
      // Each key's scroll run out before the next: WebKit animates these keys, and one pressed while
      // the last still runs starts from wherever that was. The positions it passed are kept, so a
      // scroll that stops short says where.
      const key = async (name) => {
        await sc.evaluate((el) => {
          const seen = [];
          window.__keyScroll = seen;
          const frame = () => { seen.push(Math.round(el.scrollTop)); if (seen.length < 600 && window.__keyScroll === seen) { requestAnimationFrame(frame); } };
          frame();
        });
        await page.keyboard.press(name);
        await settle(v);
        const path = await sc.evaluate(() => {
          const seen = window.__keyScroll;
          window.__keyScroll = null;
          return seen.filter((t, i) => t !== seen[i - 1]).join(' ');
        });
        return `${name} scrolled ${path}`;
      };
      const ty = async () => (await read(v)).ty;
      let path = await key('End');
      await expect.poll(ty, { message: path }).toBe(-room);
      path = await key('PageUp');
      await expect.poll(ty, { message: path }).toBe(0);
      path = await key('PageDown');
      await expect.poll(ty, { message: path }).toBe(-room);
      path = await key('PageUp');
      await expect.poll(ty, { message: path }).toBe(0);
      path = await key('End');
      await expect.poll(ty, { message: path }).toBe(-room);
      path = await key('Home');
      expect((await read(v)).scroll, path).toBe(0);
      await expect.poll(ty, { message: path }).toBe(0);
      expect(await read(v)).toMatchObject({ solid: false, titled: false });
    });

    test('the bar grows with the text, and what it covers with it', async ({ page }) => {
      const v = await session(page);
      await folded(v);
      const was = await read(v);
      await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
      await expect.poll(async () => (await read(v)).bar).toBeGreaterThan(was.bar * 1.5);
      await expect.poll(async () => { const m = await read(v); return m.covered === m.bar; }).toBe(true);
    });

    test('the bar\'s fill and hairline appear once content goes under it', async ({ page }) => {
      const v = await session(page);
      const bar = () => v.locator('.session-bar').evaluate((el) => {
        const s = getComputedStyle(el, '::before');
        return { fill: s.backgroundColor, opacity: parseFloat(s.opacity), line: s.borderBottomWidth };
      });
      expect(await bar()).toMatchObject({ opacity: 0 });
      await folded(v);
      await expect.poll(async () => (await bar()).opacity).toBe(1);
      const solid = await bar();
      expect(solid.line).toBe('1px');
      expect(solid.fill).not.toBe('rgba(0, 0, 0, 0)');
    });
  });
}

test.describe('on a phone, with the person\'s settings', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

  test('focused content stays visible after a rotation: scrolled into sight, clear of the header', async ({ page }) => {
    const v = await session(page);
    const { room } = await folded(v);
    // A control at the foot of what the person sees, which a shorter screen then cuts off.
    await v.locator('.stream').evaluate((stream) => {
      const sc = stream.parentElement;
      const b = document.createElement('button');
      b.id = 'probe';
      b.textContent = 'Probe';
      b.style.cssText = `position:absolute;left:16px;height:44px;top:${sc.scrollTop + sc.clientHeight - 60}px`;
      stream.style.position = 'relative';
      stream.append(b);
      b.focus({ preventScroll: true });
    });
    await page.setViewportSize({ width: 667, height: 375 });
    await expect.poll(() => v.evaluate((el) => {
      const sc = el.querySelector('.stream-scroll').getBoundingClientRect();
      const b = el.querySelector('#probe').getBoundingClientRect();
      const covered = parseFloat(el.style.getPropertyValue('--covered'));
      return b.top >= sc.top + covered - 1 && b.bottom <= sc.bottom + 1;
    })).toBe(true);
    expect((await read(v)).ty).toBe(-room);
  });

  test('focused content stays visible when the bar grows under it', async ({ page }) => {
    const v = await session(page);
    await folded(v);
    await v.locator('.stream').evaluate((stream) => {
      const sc = stream.parentElement;
      const b = document.createElement('button');
      b.id = 'probe';
      b.textContent = 'Probe';
      b.style.cssText = `position:absolute;left:16px;height:44px;top:${sc.scrollTop + 56}px`;
      stream.style.position = 'relative';
      stream.append(b);
      b.focus({ preventScroll: true });
    });
    await page.evaluate(() => { document.documentElement.style.fontSize = '200%'; });
    await expect.poll(() => v.evaluate((el) => {
      const sc = el.querySelector('.stream-scroll').getBoundingClientRect();
      const b = el.querySelector('#probe').getBoundingClientRect();
      return b.top >= sc.top + parseFloat(el.style.getPropertyValue('--covered')) - 1;
    })).toBe(true);
  });

  // #1040: Smaller text shrank a rem-sized bar under the fold's 48 px, and the chosen tab's line
  // showed beneath it.
  for (const size of ['smaller', 'larger']) {
    test(`at ${size} text the header folds wholly behind the bar`, async ({ page }) => {
      await page.addInitScript((s) => localStorage.setItem('agent-bridge.app.textSize', s), size);
      const v = await session(page);
      const m = await folded(v, 400);
      expect(m.bar).toBeGreaterThanOrEqual(48);
      expect(m.covered).toBe(m.bar);
      const tabs = await v.locator('.session-top .view-tabs').boundingBox();
      const bar = await v.locator('.session-bar').boundingBox();
      expect(tabs.y + tabs.height).toBeLessThanOrEqual(bar.y + bar.height + 0.5);
    });
  }

  test('a wheel on the header scrolls the conversation, and the title\'s opening focus no longer holds the header whole', async ({ page }) => {
    const v = await session(page);
    // Opened, focus is on the title (#707), inside the header: the hold.
    expect(await v.evaluate((el) => el.querySelector('.session-head').contains(document.activeElement))).toBe(true);
    const { room } = await read(v);
    const box = await v.locator('.session-top .session-title').boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
    await page.mouse.wheel(0, 400);
    await expect.poll(async () => (await read(v)).scroll).toBeGreaterThan(room);
    await expect.poll(async () => (await read(v)).ty).toBe(-room);
  });

  test('on another tab the header is still measured: its growth moves what that tab puts under it', async ({ page }) => {
    const v = await session(page);
    await v.getByRole('tab', { name: 'Tools' }).click();
    const was = await read(v);
    // The header grows by 40 px, as a wrapped title or a longer meta line would.
    await v.locator('.session-top').evaluate((top) => { top.style.paddingBottom = `${parseFloat(getComputedStyle(top).paddingBottom) + 40}px`; });
    expect((await read(v)).full).toBe(was.full + 40);
    await expect.poll(() => v.evaluate((el) => parseFloat(el.style.getPropertyValue('--head-h')))).toBe(was.full + 40);
  });

  test('focus a person moved to a tab holds the header whole through a wheel on the conversation', async ({ page }) => {
    const v = await session(page);
    await v.getByRole('tab', { name: 'Conversation' }).focus();
    await page.keyboard.press('ArrowRight');
    await page.keyboard.press('ArrowLeft');
    const box = await v.locator('.stream-scroll').boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height - 100);
    for (let i = 0; i < 4; i += 1) { await page.mouse.wheel(0, 120); }
    await expect.poll(async () => (await read(v)).scroll).toBeGreaterThan(300);
    await settle(v);
    expect((await read(v)).ty).toBe(0);
    await expect(v.getByRole('tab', { name: 'Conversation' })).toBeFocused();
  });

  test('a tab a tap chose does not hold the header: the conversation\'s scroll still folds it', async ({ page }) => {
    const v = await session(page);
    const { room } = await read(v);
    await v.getByRole('tab', { name: 'Tools' }).click();
    await v.getByRole('tab', { name: 'Conversation' }).click();
    const box = await v.locator('.stream-scroll').boundingBox();
    await page.mouse.move(box.x + box.width / 2, box.y + box.height - 100);
    for (let i = 0; i < 4; i += 1) { await page.mouse.wheel(0, 120); }
    await expect.poll(async () => (await read(v)).scroll).toBeGreaterThan(300);
    await expect.poll(async () => (await read(v)).ty).toBe(-room);
  });

  test('Shift+Tab from the conversation onto a folded header\'s tab brings the header back', async ({ page }) => {
    const v = await session(page);
    const { room } = await folded(v);
    await v.locator('.stream-scroll').evaluate((sc) => {
      const b = document.createElement('button');
      b.id = 'probe';
      b.textContent = 'Probe';
      b.style.cssText = `position:absolute;left:16px;height:44px;top:${sc.scrollTop + 300}px`;
      sc.style.position = 'relative';
      sc.prepend(b);
      b.focus({ preventScroll: true });
    });
    expect((await read(v)).ty).toBe(-room);
    await page.keyboard.press('Shift+Tab');
    await expect(v.locator('.view-tab:focus')).toHaveCount(1);
    await expect.poll(async () => (await read(v)).ty).toBe(0);
  });

  for (const reduced of [false, true]) {
    test(`focus coming into a folded header brings it back with the collapse motion${reduced ? ', under reduced motion its cross-fade, not the slide' : ''}`, async ({ page }) => {
      const v = await session(page);
      if (reduced) { await page.emulateMedia({ reducedMotion: 'reduce' }); }
      await folded(v, 300);
      await rested(v);
      // In the first frame after focus comes in.
      const back = await v.evaluate((el) => new Promise((resolve) => {
        const top = el.querySelector('.session-top');
        el.querySelector('.session-bar .back').focus();
        requestAnimationFrame(() => {
          const s = getComputedStyle(top);
          resolve({
            animating: el.classList.contains('animating'),
            slides: /transform|all/.test(s.transitionProperty) && parseFloat(s.transitionDuration) > 0,
            fades: s.animationName === 'head-cross-fade',
            time: s.animationName === 'head-cross-fade' ? s.animationDuration : s.transitionDuration,
          });
        });
      }));
      expect(back).toEqual(reduced
        ? { animating: true, slides: false, fades: true, time: '0.083s' }
        : { animating: true, slides: true, fades: false, time: '0.167s' });
      await expect.poll(async () => (await read(v)).ty).toBe(0);
    });
  }

  test('under reduced motion, focus coming into the header as it fades out stops the fade and fades it back in', async ({ page }) => {
    const v = await session(page);
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await folded(v, 300);
    await rested(v);
    await scrollTo(v, 230);
    await rested(v);
    const at = await v.evaluate((el) => new Promise((resolve) => {
      const sc = el.querySelector('.stream-scroll');
      sc.addEventListener('scroll', () => requestAnimationFrame(() => {
        const fading = el.classList.contains('fading');
        el.querySelector('.session-bar .back').focus();
        resolve({ fading, after: { fading: el.classList.contains('fading'), animating: el.classList.contains('animating') } });
      }), { once: true });
      sc.scrollTo({ top: 262, behavior: 'instant' });
    }));
    expect(at).toEqual({ fading: true, after: { fading: false, animating: true } });
    await rested(v);
    expect((await read(v)).ty).toBe(0);
    await expect(v.locator('.session-top')).toHaveCSS('opacity', '1');
  });

  test('a session the browser\'s Back and Forward show again has its whole header, where it was left', async ({ page }) => {
    const v = await session(page);
    await folded(v, 300);
    await page.goBack();
    await expect(v).toBeHidden();
    await page.goForward();
    await expect(v).toBeVisible();
    // Shown again, the view is measured at its next frame.
    await expect.poll(async () => (await read(v)).ty).toBe(0);
    await settle(v);
    const m = await read(v);
    expect(m.scroll).toBe(300);
    expect(m.ty).toBe(0);
    await wholeHeader(v);
    // Back, and Forward straight from Back's popstate, before a frame need have drawn it hidden:
    // whole again all the same (#941 review: it was inferred from a hidden header measured by a
    // ResizeObserver frame, which a quick Back and Forward can fall between).
    await folded(v, 400);
    await page.evaluate(() => new Promise((resolve) => {
      addEventListener('popstate', () => {
        addEventListener('popstate', () => resolve(), { once: true });
        history.forward();
      }, { once: true });
      history.back();
    }));
    await expect(v).toBeVisible();
    await expect.poll(async () => (await read(v)).ty).toBe(0);
    await settle(v);
    const again = await read(v);
    expect(again.scroll).toBe(400);
    expect(again.ty).toBe(0);
    await wholeHeader(v);
  });

  test('under reduced motion, a session Back and Forward show again while its header fades out stays whole once the fade would have ended', async ({ page }) => {
    const v = await session(page);
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await folded(v, 300);
    await rested(v);
    await scrollTo(v, 230);
    await rested(v);
    // Start the fade out, then Back and Forward inside its 83 ms (#941 review: the fade's end folded
    // the header shown again).
    const fading = await v.evaluate((el) => new Promise((resolve) => {
      const sc = el.querySelector('.stream-scroll');
      sc.addEventListener('scroll', () => requestAnimationFrame(() => {
        const was = el.classList.contains('fading');
        addEventListener('popstate', () => {
          addEventListener('popstate', () => resolve(was), { once: true });
          history.forward();
        }, { once: true });
        history.back();
      }), { once: true });
      sc.scrollTo({ top: 262, behavior: 'instant' });
    }));
    expect(fading).toBe(true);
    await expect(v).toBeVisible();
    await expect.poll(async () => (await read(v)).ty).toBe(0);
    await page.waitForTimeout(300);
    await rested(v);
    const m = await read(v);
    expect(m.scroll).toBe(262);
    expect(m.ty).toBe(0);
  });

  test('on another tab, what focus scrolls to stays clear of the whole header', async ({ page }) => {
    const v = await session(page);
    await v.getByRole('tab', { name: 'Tools' }).click();
    const inspector = v.locator('.inspector:visible').first();
    await inspector.evaluate((el) => {
      const b = document.createElement('button');
      b.id = 'probe';
      b.textContent = 'Probe';
      el.prepend(b);
      const pad = document.createElement('div');
      pad.style.height = '2000px';
      el.append(pad);
      el.scrollTop = 600;
    });
    await v.locator('#probe').focus();
    const { probe, full } = await v.evaluate((el) => ({
      probe: el.querySelector('#probe').getBoundingClientRect().top - el.getBoundingClientRect().top,
      full: el.querySelector('.session-top').offsetHeight,
    }));
    expect(probe).toBeGreaterThanOrEqual(full);
  });

  test('a window widened from a phone\'s leaves nothing of the collapse behind', async ({ page }) => {
    const v = await session(page);
    await folded(v);
    await page.setViewportSize({ width: 900, height: 800 });
    await expect.poll(() => v.evaluate((el) => el.style.getPropertyValue('--head-h'))).toBe('');
    expect(await v.evaluate((el) => ({ ty: el.style.getPropertyValue('--ty'), state: el.hasAttribute('data-state') }))).toEqual({ ty: '', state: false });
  });

  test('under reduced motion the header leaves and returns with the 83 ms cross-fade, and does not slide', async ({ page }) => {
    const v = await session(page);
    await page.emulateMedia({ reducedMotion: 'reduce' });
    const { room } = await folded(v, 300);
    // The return is the fade alone: in its first frame the header is already where it ends, not sliding.
    const back = await v.evaluate((el) => new Promise((resolve) => {
      const sc = el.querySelector('.stream-scroll');
      const top = el.querySelector('.session-top');
      sc.addEventListener('scroll', () => requestAnimationFrame(() => {
        const s = getComputedStyle(top);
        resolve({ slides: /transform|all/.test(s.transitionProperty) && parseFloat(s.transitionDuration) > 0, y: new DOMMatrix(s.transform).m42 });
      }), { once: true });
      sc.scrollTo({ top: 230, behavior: 'instant' });
    }));
    expect(back).toEqual({ slides: false, y: 0 });
    expect((await read(v)).ty).toBe(0);
    const fade = await v.evaluate((el) => new Promise((resolve) => {
      const sc = el.querySelector('.stream-scroll');
      const top = el.querySelector('.session-top');
      sc.addEventListener('scroll', () => requestAnimationFrame(() => {
        const s = getComputedStyle(top);
        resolve({ fading: el.classList.contains('fading'), time: s.transitionDuration, props: s.transitionProperty });
      }), { once: true });
      sc.scrollTo({ top: 262, behavior: 'instant' });
    }));
    expect(fade).toEqual({ fading: true, time: '0.083s', props: 'opacity' });
    await expect.poll(async () => (await read(v)).ty).toBe(-room);
    await expect(v.locator('.session-top')).not.toHaveCSS('opacity', '0');
  });
});

test.describe('on a phone, in forced colours', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

  test('the header and the bar are Canvas and CanvasText', async ({ page, browserName }) => {
    test.skip(browserName === 'webkit', 'WebKit has no forced-colors emulation');
    await page.emulateMedia({ forcedColors: 'active' });
    const v = await session(page);
    await folded(v);
    const probe = await page.evaluate(() => {
      const p = document.createElement('div');
      p.style.cssText = 'background:Canvas;color:CanvasText;border-color:CanvasText;border-style:solid';
      document.body.append(p);
      const s = getComputedStyle(p);
      const r = { canvas: s.backgroundColor, text: s.color };
      p.remove();
      return r;
    });
    expect(await v.locator('.session-top').evaluate((el) => getComputedStyle(el).backgroundColor)).toBe(probe.canvas);
    expect(await v.locator('.session-bar').evaluate((el) => getComputedStyle(el, '::before').backgroundColor)).toBe(probe.canvas);
    await expect(v.locator('.bar-title')).toHaveCSS('color', probe.text);
    // Only the bar's fill opts out: the header's text, its tab labels and its focus ring are the system's.
    expect(await v.locator('.session-top').evaluate((el) => getComputedStyle(el).forcedColorAdjust)).toBe('auto');
  });
});

test.describe('from 760 px up', () => {
  test.use({ viewport: { width: 900, height: 800 } });

  test('nothing is different: no bar, no listeners, no padding, no offset', async ({ page }) => {
    await openDemo(page);
    await upsert(page, CODEX, { activity: { response: LONG, reasoning: null, userMessage: null, currentTool: null }, question: null, updatedAt: later() });
    const v = await open(page, CODEX);
    const sc = v.locator('.stream-scroll');
    await expect(v.locator('.session-bar')).toBeHidden();
    await expect(v.locator('.session-head')).toHaveCSS('display', 'contents');
    const padding = await sc.evaluate((el) => { const s = getComputedStyle(el); return [s.paddingTop, s.paddingLeft, s.scrollPaddingTop]; });
    expect(padding[0]).toBe(padding[1]);
    expect(padding[2]).toBe('auto');
    await sc.evaluate((el) => { el.scrollTop = 600; });
    await page.waitForTimeout(150);
    expect(await v.evaluate((el) => ({
      style: ['--ty', '--covered', '--head-h', '--stage-h'].map((p) => el.style.getPropertyValue(p)),
      attributes: ['data-state', 'data-solid', 'data-titled'].filter((a) => el.hasAttribute(a)),
      classes: [...el.classList].filter((c) => c === 'animating' || c === 'fading'),
    }))).toEqual({ style: ['', '', '', ''], attributes: [], classes: [] });
    expect((await v.locator('.session-header').boundingBox()).y).toBeGreaterThanOrEqual(0);
  });
});
