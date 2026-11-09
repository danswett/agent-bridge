// A phone's reply area (#935, ui.md §6.5, §6.2, §9). Below 760 px the box is one line at rest, 52
// tall, with Attach before the text and Send after it, and grows a line at a time to six lines; a
// question waiting puts one line above it, whose link goes to the question; and while the person
// reads, the area floats over the conversation's foot and mirrors the collapsing header (#631):
// near the end it rides the scroll, away from it 32 up sends it away and 64 down brings it back, a
// pinned bar holding a drawn copy of the draft's first line. The fold's arithmetic is unit-tested
// (test/unit/collapse-foot.test.mjs); here the page. From 760 nothing is different.
import { test, expect } from '@playwright/test';
import { open, view } from './nav.mjs';

const IDLE = '8c4e1f20-5a7b-4d3c-9e61-0b2f7d9a1c35';
const later = (ms = 1000) => new Date(Date.now() + ms).toISOString();
const LONG = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');

async function openDemo(page, query = '') {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto(`?demo=1&static=1${query}`);
  await page.waitForSelector('html[data-demo-ready]');
}

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
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

const box = (v) => v.locator('form.reply');
const input = (v) => box(v).getByRole('textbox', { name: 'Reply' });
const height = (l) => l.evaluate((el) => el.getBoundingClientRect().height);

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

// Where the header is: its slide, and the room it folds by.
const head = (v) => v.evaluate((el) => ({
  ty: parseFloat(el.style.getPropertyValue('--ty') || '0'),
  room: el.querySelector('.session-top').offsetHeight - el.querySelector('.session-bar').offsetHeight,
}));

// A person's scroll: their touch or wheel first, then one scroll event, so the next move is the next.
const scrollTo = (v, y, input = true) => v.locator('.stream-scroll').evaluate((sc, [y, input]) => new Promise((resolve) => {
  if (sc.scrollTop === y) { resolve(); return; }
  if (input) { sc.dispatchEvent(new WheelEvent('wheel', { deltaY: y - sc.scrollTop, bubbles: true })); }
  sc.addEventListener('scroll', () => requestAnimationFrame(() => requestAnimationFrame(() => resolve())), { once: true });
  sc.scrollTo({ top: y, behavior: 'instant' });
}), [y, input]);

// Where the reply area is: its slide (--fy), the room it folds by, and what the view says of it.
const foot = (v) => v.evaluate((el) => ({
  fy: parseFloat(el.style.getPropertyValue('--fy') || '0'),
  room: el.querySelector('.reply-slot').offsetHeight - el.querySelector('.reply-bar').offsetHeight,
  state: el.dataset.foot ?? null,
  solid: el.hasAttribute('data-fsolid'),
}));
const end = (v) => v.locator('.stream-scroll').evaluate((sc) => sc.scrollHeight - sc.clientHeight);

// The idle session, its answer long enough to scroll.
async function session(page, { long = true, query = '' } = {}) {
  await openDemo(page, query);
  if (long) { await upsert(page, IDLE, { activity: { response: LONG, reasoning: null, userMessage: null, currentTool: null }, question: null, updatedAt: later() }); }
  const v = await open(page, IDLE);
  await expect(input(v)).toBeVisible();
  return v;
}

test.describe('on a phone', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

  test('the box is one line at rest, 52 tall, Attach before the text and Send after it, in one row', async ({ page }) => {
    const v = await session(page, { long: false });
    const b = box(v);
    expect(await height(b.locator('.reply-box'))).toBe(52);
    // The reply area at rest: the box, and the slot's padding and edge.
    expect(await height(v.locator('.reply-slot'))).toBe(73);
    const at = (name) => b.getByRole(name === 'text' ? 'textbox' : 'button', { name: name === 'text' ? 'Reply' : name }).boundingBox();
    const [attach, text, send] = [await at('Attach a file or image'), await at('text'), await at('Send')];
    expect(attach.x + attach.width).toBeLessThanOrEqual(text.x);
    expect(text.x + text.width).toBeLessThanOrEqual(send.x);
    for (const r of [attach, send]) { expect(r.y + r.height).toBeCloseTo(text.y + text.height, 0); }
    // Tab meets them in the order they are drawn: Attach, the text, then Send (ui.md §9).
    await b.getByRole('button', { name: 'Attach a file or image' }).focus();
    await page.keyboard.press('Tab');
    await expect(input(v)).toBeFocused();
    await page.keyboard.type('Hi');
    await page.keyboard.press('Tab');
    await expect(b.getByRole('button', { name: 'Send' })).toBeFocused();
  });

  test('it grows a line at a time to six lines, then scrolls inside', async ({ page }) => {
    const v = await session(page, { long: false });
    const t = input(v);
    const heights = [];
    for (let n = 1; n <= 8; n += 1) {
      await t.fill(Array.from({ length: n }, (_, i) => `line ${i + 1}`).join('\n'));
      heights.push(await height(t));
    }
    expect(heights).toEqual([44, 68, 92, 116, 140, 164, 164, 164]);
    expect(await t.evaluate((el) => getComputedStyle(el).overflowY)).toBe('auto');
    expect(await t.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);
    // Emptied, it is one line again.
    await t.fill('');
    expect(await height(t)).toBe(44);
    expect(await height(box(v).locator('.reply-box'))).toBe(52);
  });

  test('a question waiting puts one line above the box, whose link focuses its first choice and leaves the header as it was', async ({ page }) => {
    const v = await session(page);
    await upsert(page, IDLE, { status: 'waiting', statusSince: later(), updatedAt: later(), question: choices('q-935') });
    const notice = box(v).locator('.reply-held');
    await expect(notice).toHaveText('Answer its question first. Go to the question');
    await expect(notice).toHaveCSS('color', await v.evaluate((el) => {
      const p = el.ownerDocument.createElement('p');
      p.style.color = 'var(--ab-needs-text)';
      el.append(p);
      const c = getComputedStyle(p).color;
      p.remove();
      return c;
    }));
    expect(await height(notice)).toBe(18);
    // One line of notice, and the box: 99 at rest.
    expect(await height(v.locator('.reply-slot'))).toBe(99);
    await expect(input(v)).toBeDisabled();
    // Read back up, the question out of sight, with the header folded to its bar.
    await settle(v);
    await scrollTo(v, 0);
    await scrollTo(v, 400);
    const before = await head(v);
    expect(before.ty).toBe(-before.room);
    await expect(v.locator('.question-slot .answer')).not.toBeInViewport();
    await box(v).getByRole('button', { name: 'Go to the question' }).click();
    await expect(v.locator('.question-slot').getByRole('button', { name: 'First' })).toBeFocused();
    await settle(v);
    await expect(v.locator('.question-slot .answer')).toBeInViewport();
    expect((await head(v)).ty).toBe(before.ty);
  });

  test('tapping the compact bar brings the area back, and a link in it still works from there', async ({ page }) => {
    const v = await session(page);
    await upsert(page, IDLE, { status: 'waiting', statusSince: later(), updatedAt: later(), question: choices('q-935-bar') });
    await expect(box(v).locator('.reply-held')).toBeVisible();
    await settle(v);
    const max = await end(v);
    await scrollTo(v, max);
    await scrollTo(v, max - 400);
    expect(await foot(v)).toMatchObject({ state: 'compact', solid: true });
    await v.locator('.reply-copy').click();
    await expect(box(v).getByRole('button', { name: 'Go to the question' })).toBeFocused();
    expect(await foot(v)).toMatchObject({ fy: 0, solid: false });
  });

  test('a question only the machine can answer disables every control, and tapping the compact bar still brings the notice back', async ({ page }) => {
    const v = await session(page);
    const question = { id: 'q-935-machine', kind: 'form', message: 'Which run?', fields: [], askedAt: later(60_000), answerAtMachine: true };
    await upsert(page, IDLE, { status: 'waiting', statusSince: later(), updatedAt: later(), question });
    await expect(box(v).locator('.reply-held')).toBeVisible();
    await expect(input(v)).toBeDisabled();
    await settle(v);
    const max = await end(v);
    await scrollTo(v, max);
    await scrollTo(v, max - 400);
    expect(await foot(v)).toMatchObject({ state: 'compact', solid: true });
    await v.locator('.reply-copy').click();
    await expect.poll(() => foot(v)).toMatchObject({ fy: 0, solid: false });
    await expect(box(v).locator('.reply-held')).toBeInViewport();
  });

  // #1078: an app installed on an iPhone keeps the conversation clear of the home indicator (the
  // session pane's padding), and the compact reply area slides its box down by its room, past
  // the conversation's foot. Unclipped, the box's lower edge showed in that band under the bar: a
  // second reply-box outline stacked under the real one.
  for (const status of ['idle', 'working']) {
    test(`installed on an iPhone, the ${status} session's compact reply area shows one outline, with the keyboard shut again, and its last line scrolls clear`, async ({ page }) => {
      const v = await session(page);
      if (status === 'working') { await upsert(page, IDLE, { status: 'working', statusSince: later(), updatedAt: later(), activity: { currentTool: 'Reading files' } }); }
      await settle(v);
      const root = (bottom) => page.evaluate((px) => {
        const h = document.documentElement;
        h.dataset.platform = 'ios';
        h.dataset.standalone = 'true';
        h.style.setProperty('--env-safe-bottom', px);
      }, bottom);
      await root('34px');
      const sc = v.locator('.stream-scroll');
      // The keyboard open (it takes the home indicator's band with it), then dismissed.
      await input(v).click();
      await root('0px');
      await page.setViewportSize({ width: 390, height: 500 });
      await expect(input(v)).toBeFocused();
      await input(v).blur();
      await page.setViewportSize({ width: 390, height: 844 });
      await root('34px');
      await settle(v);
      const max = await end(v);
      await scrollTo(v, max);
      await scrollTo(v, max - 400);
      expect(await foot(v)).toMatchObject({ state: 'compact', solid: true });
      // Compact: the bar's copy is the one outline; nothing of the box itself shows below the conversation.
      const strays = () => v.evaluate((el) => {
        const conv = el.querySelector('.conversation').getBoundingClientRect();
        const slot = el.querySelector('.reply-slot');
        const hits = [];
        for (let y = Math.ceil(conv.bottom) + 1; y < innerHeight; y += 2) {
          for (const x of [30, innerWidth / 2, innerWidth - 40]) {
            if (slot.contains(document.elementFromPoint(x, y))) { hits.push([x, y]); }
          }
        }
        return { below: innerHeight - conv.bottom, hits: hits.length };
      });
      expect(await strays()).toEqual({ below: 34, hits: 0 });
      await expect(v.locator('.reply-copy')).toBeVisible();
      // Scrolling down to the end: the area comes back whole, the copy goes, still one outline, and
      // the last line ends above the box.
      for (const at of [max - 300, max - 200, max - 100, max - 40, max]) { await scrollTo(v, at); }
      await settle(v);
      expect(await foot(v)).toMatchObject({ fy: 0, state: 'rest', solid: false });
      await expect(v.locator('.reply-copy')).toBeHidden();
      expect(await strays()).toEqual({ below: 34, hits: 0 });
      const last = await v.locator('.stream').evaluate((el) => el.lastElementChild.getBoundingClientRect().bottom);
      expect(last).toBeLessThanOrEqual((await v.locator('.reply-box').boundingBox()).y);
      expect(await sc.evaluate((el) => Math.round(el.scrollTop) >= el.scrollHeight - el.clientHeight - 1)).toBe(true);
    });
  }

  test('a session opens with the reply area in full; reading back up it rides down, then is a pinned 48 bar', async ({ page }) => {
    const v = await session(page);
    await settle(v);
    const max = await end(v);
    expect(max).toBeGreaterThan(600);
    // Opened at the conversation's top, far from its end, the area is whole.
    expect(await foot(v)).toEqual({ fy: 0, room: 25, state: 'returned', solid: false });
    await scrollTo(v, max);
    expect(await foot(v)).toMatchObject({ fy: 0, state: 'rest' });
    // The conversation keeps room for the whole area at its end: its last words clear of it.
    const last = await v.locator('.stream').evaluate((el) => el.lastElementChild.getBoundingClientRect().bottom);
    expect(last).toBeLessThanOrEqual((await v.locator('.reply-slot').boundingBox()).y);
    await scrollTo(v, max - 10);
    expect(await foot(v)).toMatchObject({ fy: 10, state: 'riding', solid: false });
    await scrollTo(v, max - 400);
    expect(await foot(v)).toMatchObject({ fy: 25, state: 'compact', solid: true });
    // The bar: 48, pinned to the conversation's foot, over the box's top, holding the placeholder.
    const bar = await v.locator('.reply-bar').boundingBox();
    const conv = await v.locator('.conversation').boundingBox();
    expect(bar.height).toBe(48);
    expect(bar.y + bar.height).toBeCloseTo(conv.y + conv.height, 0);
    expect((await v.locator('.reply-slot').boundingBox()).y).toBeCloseTo(bar.y, 0);
    const copy = v.locator('.reply-copy');
    await expect(copy).toBeVisible();
    await expect(copy).toHaveText(await input(v).getAttribute('placeholder'));
    await expect(copy).not.toHaveClass(/draft/);
  });

  test('away from the end 64 down brings it back and 32 up sends it away; the end always shows it in full', async ({ page }) => {
    const v = await session(page);
    await settle(v);
    const max = await end(v);
    await scrollTo(v, max);
    await scrollTo(v, max - 400);
    expect((await foot(v)).fy).toBe(25);
    await scrollTo(v, max - 337);
    expect((await foot(v)).fy).toBe(25);
    await scrollTo(v, max - 336);
    expect(await foot(v)).toMatchObject({ fy: 0, state: 'returned', solid: false });
    await scrollTo(v, max - 367);
    expect((await foot(v)).fy).toBe(0);
    await scrollTo(v, max - 368);
    expect(await foot(v)).toMatchObject({ fy: 25, state: 'compact' });
    await scrollTo(v, max);
    expect(await foot(v)).toMatchObject({ fy: 0, state: 'rest' });
  });

  test('compact, the bar shows the draft\'s first line, drawn and not read; a tap brings the box back in full, focused', async ({ page }) => {
    const v = await session(page);
    await settle(v);
    const t = input(v);
    await t.fill('Ship it on Friday\nand tell the team');
    await t.blur();
    await settle(v);
    const max = await end(v);
    await scrollTo(v, max);
    await scrollTo(v, max - 400);
    const f = await foot(v);
    expect(f.room).toBeGreaterThan(25);
    expect(f).toMatchObject({ fy: f.room, state: 'compact', solid: true });
    const copy = v.locator('.reply-copy');
    await expect(copy).toHaveText('Ship it on Friday');
    await expect(copy).toHaveClass(/draft/);
    // Drawn: hidden from assistive technology, and no stop for Tab.
    await expect(copy).toHaveAttribute('aria-hidden', 'true');
    expect(await copy.evaluate((el) => [el.tabIndex, el.querySelectorAll('a, button, input, textarea, [tabindex]').length])).toEqual([-1, 0]);
    // Typing elsewhere is not needed: the copy follows the words as they change.
    await t.evaluate((el) => { el.value = 'Ship it on Monday'; el.dispatchEvent(new Event('input', { bubbles: true })); });
    await expect(copy).toHaveText('Ship it on Monday');
    await copy.tap();
    await expect(t).toBeFocused();
    expect((await foot(v)).fy).toBe(0);
    const slot = await v.locator('.reply-slot').boundingBox();
    const conv = await v.locator('.conversation').boundingBox();
    expect(slot.y + slot.height).toBeCloseTo(conv.y + conv.height, 0);
  });

  test('focus coming into the reply area brings it back at once and holds it while the person reads', async ({ page }) => {
    const v = await session(page);
    await settle(v);
    const max = await end(v);
    await scrollTo(v, max);
    await scrollTo(v, max - 400);
    expect((await foot(v)).fy).toBe(25);
    // At once: no slide, the box whole in the same frame.
    const at = await v.evaluate((el) => {
      el.querySelector('form.reply textarea').focus();
      return { fy: el.style.getPropertyValue('--fy'), sliding: el.classList.contains('fanimating') };
    });
    expect(at).toEqual({ fy: '0px', sliding: false });
    await scrollTo(v, max - 600);
    expect((await foot(v)).fy).toBe(0);
    await expect(input(v)).toBeInViewport({ ratio: 1 });
    // Let go, it stays whole until 32 up sends it away.
    await input(v).blur();
    await scrollTo(v, max - 500);
    expect((await foot(v)).fy).toBe(0);
    await scrollTo(v, max - 531);
    expect((await foot(v)).fy).toBe(0);
    await scrollTo(v, max - 532);
    expect((await foot(v)).fy).toBe(25);
  });

  test('a question waiting at the end is clear of the reply area, its notice and all', async ({ page }) => {
    const v = await session(page);
    await upsert(page, IDLE, { status: 'waiting', statusSince: later(), updatedAt: later(), question: choices('q-935') });
    await expect(box(v).locator('.reply-held')).toBeVisible();
    await settle(v);
    await scrollTo(v, await end(v));
    expect(await foot(v)).toMatchObject({ fy: 0, room: 51 });
    const answer = await v.locator('.question-slot .answer').boundingBox();
    expect(answer.y + answer.height).toBeLessThanOrEqual((await v.locator('.reply-slot').boundingBox()).y);
  });

  test('the session changing while the person is on "Go to the question" leaves them on it, and the area whole', async ({ page }) => {
    const v = await session(page);
    await upsert(page, IDLE, { status: 'waiting', statusSince: later(), updatedAt: later(), question: choices('q-935') });
    const link = box(v).getByRole('button', { name: 'Go to the question' });
    await expect(link).toBeVisible();
    await settle(v);
    const max = await end(v);
    await scrollTo(v, max);
    await link.focus();
    await expect(link).toBeFocused();
    // Its words and its status moving on, the question still waiting: the notice is drawn again.
    await upsert(page, IDLE, { activity: { response: `${LONG}\n\nStill here.` }, updatedAt: later(2000) });
    await upsert(page, IDLE, { status: 'waiting', statusSince: later(3000), updatedAt: later(3000) });
    await expect(v.locator('.stream')).toContainText('Still here.');
    await expect(link).toBeFocused();
    // Focus still inside it, reading back up does not fold the area the person is in.
    await scrollTo(v, max - 400);
    expect((await foot(v)).fy).toBe(0);
  });

  test('under reduced motion it leaves with the cross-fade, and does not slide', async ({ page }) => {
    const v = await session(page);
    await page.emulateMedia({ reducedMotion: 'reduce' });
    await settle(v);
    const max = await end(v);
    await scrollTo(v, max);
    await scrollTo(v, max - 400);
    await scrollTo(v, max - 300);
    expect((await foot(v)).fy).toBe(0);
    await expect(v).not.toHaveClass(/fanimating/);
    const fade = await v.evaluate((el) => new Promise((resolve) => {
      const sc = el.querySelector('.stream-scroll');
      const slot = el.querySelector('.reply-slot');
      sc.addEventListener('scroll', () => requestAnimationFrame(() => {
        const s = getComputedStyle(slot);
        resolve({ fading: el.classList.contains('ffading'), props: s.transitionProperty, fy: el.style.getPropertyValue('--fy') });
      }), { once: true });
      sc.scrollTo({ top: sc.scrollTop - 40, behavior: 'instant' });
    }));
    expect(fade).toEqual({ fading: true, props: 'opacity', fy: '0px' });
    await expect.poll(async () => (await foot(v)).fy).toBe(25);
    await expect(v.locator('.reply-slot')).not.toHaveCSS('opacity', '0');
  });

  test('a machine that takes no replies has no reply area, and no bar', async ({ page }) => {
    await openDemo(page, '&reply=0');
    const v = await open(page, IDLE);
    await expect(v.locator('.conversation')).toBeVisible();
    await expect(v.locator('form.reply')).toHaveCount(0);
    await expect(v.locator('.reply-bar')).toBeHidden();
    expect(await v.evaluate((el) => el.style.getPropertyValue('--foot-h'))).toBe('0px');
  });

  test('the link is not drawn from 760: the notice says it in full', async ({ page }) => {
    await page.setViewportSize({ width: 1024, height: 768 });
    const v = await session(page, { long: false });
    await upsert(page, IDLE, { status: 'waiting', statusSince: later(), updatedAt: later(), question: choices('q-935') });
    await expect(box(v).locator('.reply-held')).toHaveText('The session is waiting for an answer to its question. Answer that first, then reply.');
    // A phone's notice comes back when the window narrows.
    await page.setViewportSize({ width: 390, height: 844 });
    await expect(box(v).locator('.reply-held')).toHaveText('Answer its question first. Go to the question');
  });
});

test.describe('on a phone, in forced colours', () => {
  test.use({ viewport: { width: 390, height: 844 }, hasTouch: true });

  test('the compact bar is Canvas with a CanvasText hairline', async ({ page, browserName }) => {
    test.skip(browserName === 'webkit', 'WebKit has no forced-colors emulation');
    await page.emulateMedia({ forcedColors: 'active' });
    const v = await session(page);
    await settle(v);
    await scrollTo(v, await end(v));
    await scrollTo(v, (await end(v)) - 400);
    expect((await foot(v)).solid).toBe(true);
    const probe = await page.evaluate(() => {
      const p = document.createElement('div');
      p.style.cssText = 'background:Canvas;color:CanvasText';
      document.body.append(p);
      const s = getComputedStyle(p);
      const r = { canvas: s.backgroundColor, text: s.color };
      p.remove();
      return r;
    });
    const bar = await v.locator('.reply-bar').evaluate((el) => { const s = getComputedStyle(el, '::before'); return [s.backgroundColor, s.borderTopColor, s.borderTopWidth]; });
    expect(bar).toEqual([probe.canvas, probe.text, '1px']);
  });
});

test.describe('from 760 px up', () => {
  test.use({ viewport: { width: 900, height: 800 } });

  test('nothing is different: the reply area is in the column, never folded, with no bar', async ({ page }) => {
    const v = await session(page);
    await settle(v);
    await scrollTo(v, (await end(v)) - 400);
    expect(await v.evaluate((el) => [el.style.getPropertyValue('--fy'), el.style.getPropertyValue('--foot-h'), el.dataset.foot ?? null])).toEqual(['', '', null]);
    await expect(v.locator('.reply-bar')).toBeHidden();
    await expect(v.locator('.reply-slot')).toHaveCSS('position', 'static');
    const slot = await v.locator('.reply-slot').boundingBox();
    const sc = await v.locator('.stream-scroll').boundingBox();
    expect(slot.y).toBeCloseTo(sc.y + sc.height, 0);
    // Attach is beside Send again, after the text, as before.
    expect(await v.locator('form.reply .attach').evaluate((el) => el.parentElement.className)).toBe('reply-tools');
  });
});
