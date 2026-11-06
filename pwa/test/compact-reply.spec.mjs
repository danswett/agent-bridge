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

async function openDemo(page) {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto('?demo=1&static=1');
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

// The idle session, its answer long enough to scroll, opened at its end as a person finds it.
async function session(page, { long = true } = {}) {
  await openDemo(page);
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
    // The text is still the box's first stop, before Attach and Send (the desktop's order).
    await input(v).focus();
    await page.keyboard.press('Tab');
    await expect(b.getByRole('button', { name: 'Attach a file or image' })).toBeFocused();
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
