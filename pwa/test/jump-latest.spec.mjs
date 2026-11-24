// "Jump to latest" (ui.md §6.4 H3 and H3a, #1720). More than a screen above the end of a conversation, a
// pill sits above the reply box. A message that arrives meanwhile is added below without moving the view,
// and the pill counts it; a question waiting makes it say so. Choosing it scrolls smoothly to the end, where
// the view follows again. The pill is in no layout: it never changes what the person is reading, so a
// wheel moves the conversation exactly as far as it says (#4243).
import { test, expect } from '@playwright/test';
import { open } from './nav.mjs';

const ID = 'jump-latest';
const N = 12; // turns; only the latest few are drawn at first, and they are tall enough that nothing loads above the reader
const later = (ms = 1000) => new Date(Date.now() + ms).toISOString();

const turn = (i) => ({
  id: `${ID}-${i}`, at: new Date(Date.UTC(2026, 0, 1, 0, i)).toISOString(),
  prompt: { text: `Prompt ${i}`, from: 'person' },
  parts: [{ kind: 'response', text: `Reply ${i}.\n\n${'A paragraph of the answer, long enough to take a line or two.\n\n'.repeat(40)}` }],
  partsMore: 0, truncated: false, unreadable: false,
});

async function session(page, { count = N } = {}) {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
  await page.evaluate(({ ID, turns }) => {
    const demo = window.agentBridgeDemo;
    const latest = turns.at(-1);
    demo.host.historyTurns.set(ID, turns);
    demo.upsert({
      id: ID, agent: 'copilot', title: 'Jump to latest fixture', status: 'idle',
      startedAt: turns[0].at, updatedAt: latest.at, statusSince: latest.at,
      activity: { turn: latest.id, responseTurn: latest.id, turnCount: turns.length, userMessage: latest.prompt.text, response: latest.parts[0].text },
    });
  }, { ID, turns: Array.from({ length: count }, (_, i) => turn(i + 1)) });
  const v = await open(page, ID);
  await expect(v.locator('.history-turn').nth(2)).toBeVisible();
  await expect.poll(() => gap(v)).toBeLessThan(4);
  await settle(v);
  return v;
}

// A turn arrives: the host's history gains it and the session's latest turn is it.
const arrive = (page, n) => page.evaluate(({ ID, t }) => {
  const demo = window.agentBridgeDemo;
  const turns = demo.host.historyTurns.get(ID);
  turns.push(t);
  const s = demo.host.get(ID);
  demo.upsert({ ...s, updatedAt: new Date(Date.now() + 1000).toISOString(), activity: { ...s.activity, turn: t.id, responseTurn: t.id, turnCount: turns.length, userMessage: t.prompt.text, response: t.parts[0].text } });
}, { ID, t: turn(n) });

const choices = {
  id: 'q-jump', kind: 'form', message: 'Which?', askedAt: later(60_000),
  fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
};

const pane = (v) => v.locator('.stream-scroll');
const pill = (v) => v.locator('.jump-latest');
const top = (v) => pane(v).evaluate((el) => el.scrollTop);
const gap = (v) => pane(v).evaluate((el) => el.scrollHeight - el.clientHeight - el.scrollTop);
const client = (v) => pane(v).evaluate((el) => el.clientHeight);

// The conversation not moving.
const settle = (v) => pane(v).evaluate((sc) => new Promise((resolve) => {
  let at = sc.scrollTop;
  let still = 0;
  const tick = () => {
    still = sc.scrollTop === at ? still + 1 : 0;
    at = sc.scrollTop;
    if (still >= 15) { resolve(); } else { requestAnimationFrame(tick); }
  };
  requestAnimationFrame(tick);
}));

// The scrolls the app asks the conversation for.
const recordScrolls = (v) => pane(v).evaluate((el) => {
  const scrollTo = el.scrollTo.bind(el);
  window.__scrolls = [];
  el.scrollTo = (o) => { window.__scrolls.push(o); return scrollTo(o); };
});

// A person's wheel over the conversation.
async function wheel(page, v, dy) {
  const box = await pane(v).boundingBox();
  await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
  await page.mouse.wheel(0, dy);
  await settle(v);
}

test.afterEach(async ({ page }) => {
  expect(await page.evaluate(() => window.__cspViolations ?? [])).toEqual([]);
});

for (const [name, viewport] of [['on a phone', { width: 390, height: 844 }], ['from 760 px up', { width: 1280, height: 800 }]]) {
  test.describe(name, () => {
    test.use({ viewport, hasTouch: name === 'on a phone' });

    test('it shows only more than a screen above the end, saying "Jump to latest", above the reply box, in no layout', async ({ page }) => {
      const v = await session(page);
      await expect(pill(v)).toBeHidden();
      const screen = await client(v);
      await wheel(page, v, -Math.round(screen / 2));
      await expect(pill(v)).toBeHidden();
      await wheel(page, v, -Math.round(screen * 1.5));
      await expect(v.getByRole('button', { name: 'Jump to latest' })).toBeVisible();
      await expect(pill(v)).toHaveText('Jump to latest');
      expect(await client(v), 'the pill takes no room from the conversation').toBe(screen);
      const p = await pill(v).boundingBox();
      const reply = await v.locator('.reply-slot').boundingBox();
      const at = await pane(v).boundingBox();
      expect(p.y + p.height).toBeLessThanOrEqual(reply.y + 1);
      expect(Math.abs(p.x + p.width / 2 - (at.x + at.width / 2))).toBeLessThan(2);
      expect(p.height).toBeGreaterThanOrEqual(36);
      await wheel(page, v, Math.round(screen * 1.5));
      await expect(pill(v)).toBeHidden();
    });

    test('choosing it scrolls smoothly to the end, the pill goes, and the view follows the end again', async ({ page }) => {
      const v = await session(page);
      await wheel(page, v, -Math.round((await client(v)) * 3));
      await expect(pill(v)).toBeVisible();
      // The scroll it asks for (headless Chromium does not animate a smooth one, so the call is what is seen).
      await recordScrolls(v);
      await pill(v).click();
      await expect.poll(() => gap(v)).toBeLessThan(4);
      await expect(pill(v)).toBeHidden();
      expect(await page.evaluate(() => window.__scrolls)).toMatchObject([{ behavior: 'smooth' }]);
      await arrive(page, N + 1);
      await expect(pane(v).getByText(`Reply ${N + 1}.`)).toBeVisible();
      await expect.poll(() => gap(v)).toBeLessThan(4);
    });

    test('a message arriving meanwhile is counted without moving the view, and choosing the pill takes it in', async ({ page }) => {
      const v = await session(page);
      await wheel(page, v, -Math.round((await client(v)) * 3));
      const at = await top(v);
      await arrive(page, N + 1);
      await expect(pill(v)).toHaveText('1 new · Jump to latest');
      await arrive(page, N + 2);
      await expect(pill(v)).toHaveText('2 new · Jump to latest');
      expect(await top(v), 'nothing moved under the reader').toBe(at);
      await pill(v).click();
      await expect.poll(() => gap(v)).toBeLessThan(4);
      await expect(pane(v).getByText(`Reply ${N + 2}.`)).toBeVisible();
      await wheel(page, v, -Math.round((await client(v)) * 3));
      await expect(pill(v)).toHaveText('Jump to latest');
    });

    test('a question arriving while reading back makes it say so, and nothing scrolls on its own', async ({ page }) => {
      const v = await session(page);
      await wheel(page, v, -Math.round((await client(v)) * 3));
      const at = await top(v);
      await page.evaluate(({ ID, q, until }) => {
        const d = window.agentBridgeDemo;
        d.upsert({ ...d.host.get(ID), status: 'waiting', statusSince: until, updatedAt: until, question: q });
      }, { ID, q: choices, until: later() });
      await expect(pill(v)).toHaveText('Question · Jump to latest');
      await expect(pill(v)).toHaveAttribute('data-question', '');
      await expect(v.locator('.question-waiting'), 'one pill, not two').toBeHidden();
      expect(await top(v)).toBe(at);
      await pill(v).click();
      await expect.poll(() => gap(v)).toBeLessThan(4);
      await expect(v.locator('.question-slot .answer')).toBeVisible();
    });

    test('a wheel moves the conversation exactly as far as it says, the pill appearing or not (#4243)', async ({ page }) => {
      const v = await session(page);
      const box = await pane(v).boundingBox();
      await page.mouse.move(box.x + box.width / 2, box.y + box.height / 2);
      const shown = [];
      // Eight steps: past the pill's threshold, and short of the top, where an older page of history draws above and moves the view.
      for (const dy of [...Array(8).fill(-120), ...Array(8).fill(120)]) {
        const before = await top(v);
        await page.mouse.wheel(0, dy);
        await settle(v);
        expect((await top(v)) - before).toBe(dy);
        shown.push(await pill(v).isVisible());
      }
      expect(shown.includes(true) && shown.includes(false), 'it came and went during the run').toBe(true);
    });
  });
}
