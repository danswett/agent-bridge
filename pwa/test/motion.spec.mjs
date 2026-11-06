// Motion (docs/design/ui.md §5.3, #232): every motion the catalogue draws is timed by its
// generated token and none runs longer than 300 ms; reduced motion leaves only short fades;
// nothing moves as the app starts; a tap during a motion still lands; and a question that
// arrives is scrolled to only while the conversation is read to its end.
import { test, expect } from '@playwright/test';
import { go, open, row, view } from './nav.mjs';

const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CLAUDE = '6f1c2b7e-claude-example';
const CODEX = '019a1b2c-codex-example';
const later = (ms = 1000) => new Date(Date.now() + ms).toISOString();

// Every animation the page asks for, from the first script on: js/ui/motion.js names each one
// ab-motion-<entry> (Animation.id), set just after it is made, so it is read when asked for.
async function openDemo(page, { width = 1440, height = 900, hash = '' } = {}) {
  await page.setViewportSize({ width, height });
  await page.addInitScript(() => {
    window.__motions = [];
    // What CSS moves by itself: every animation but the glyphs' loops (ui.md §2.1), and every
    // transition, with the duration each started with.
    window.__css = [];
    const heard = (e) => {
      if (e.animationName === 'glyph-turn' || e.animationName === 'spin') { return; }
      // A press letting go eases back by itself (ui.md §5.3, Press): that is the press, not news.
      if (e.type === 'transitionstart' && e.target.dataset?.press === 'released') { return; }
      const cs = getComputedStyle(e.target);
      window.__css.push({ name: e.animationName ?? e.propertyName, cls: e.target.className?.baseVal ?? e.target.className, duration: e.animationName ? cs.animationDuration : cs.transitionDuration });
    };
    addEventListener('animationstart', heard, true);
    addEventListener('transitionstart', heard, true);
    // The copies leave() fades are in closed shadow roots: kept here so a test can look inside.
    window.__shadows = [];
    const attach = Element.prototype.attachShadow;
    Element.prototype.attachShadow = function (init) {
      const root = attach.call(this, init);
      window.__shadows.push(root);
      return root;
    };
    const animate = Element.prototype.animate;
    Element.prototype.animate = function (keyframes, options) {
      const a = animate.call(this, keyframes, options);
      window.__motions.push({ a, el: this, keyframes, duration: options?.duration ?? 0 });
      return a;
    };
  });
  await page.goto(`?demo=1&static=1${hash}`);
  await page.waitForSelector('html[data-demo-ready]');
  await expect(page.locator('.session')).not.toHaveCount(0);
}

const motions = (page) => page.evaluate(() => window.__motions.map((m) => ({
  id: m.a.id,
  duration: m.duration,
  travels: m.keyframes.some((k) => k.transform && k.transform !== 'none'),
  from: m.keyframes[0]?.transform ?? null,
  inLane: !!m.el.closest?.('#needs-you') || !!m.el.querySelector?.('#needs-you'),
  cls: m.el.getAttribute?.('class') ?? null,
})));

// The CSS motions that moved: those that started with a duration.
const cssMotions = (page) => page.evaluate(() => window.__css.filter((m) => !/^0s(, 0s)*$/.test(m.duration)));

// What the tokens say an entry takes, going in and going out, in ms.
const tokens = (page) => page.evaluate(() => {
  const css = getComputedStyle(document.documentElement);
  // As motion.js reads them: a number with ms or s.
  const ms = (name) => {
    const m = /^(-?[\d.]+)(ms|s)$/.exec(css.getPropertyValue(name).trim());
    return m ? Number(m[1]) * (m[2] === 's' ? 1000 : 1) : 0;
  };
  const out = {};
  for (const entry of ['push', 'pane', 'tab', 'disclosure', 'list-item', 'glyph', 'activity-dot', 'selection', 'press', 'acknowledge', 'toast', 'menu', 'scroll', 'reorder']) {
    out[entry.replace(/-(\w)/g, (_, c) => c.toUpperCase())] = [ms(`--ab-motion-${entry}-duration`), ms(`--ab-motion-${entry}-exit-duration`)].filter((v) => v > 0);
  }
  out.crossFade = ms('--ab-motion-cross-fade');
  return out;
});

// The page's clock made the test's, from now: time moves on only as the test runs it on, so a slow
// runner can't let a short wait run out between two steps. Paused a moment ahead of the page's
// own time, which a busy runner may have moved on since the test last asked.
async function holdClock(page) {
  await page.clock.install();
  await page.clock.pauseAt(await page.evaluate(() => Date.now()) + 2000);
}

async function ask(page, id) {
  await page.evaluate((id) => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    d.upsert({ ...d.host.get(id), status: 'waiting', statusSince: t, updatedAt: t, question: { kind: 'form', askedAt: t, fields: [], prompt: 'Carry on?' } });
  }, id);
}

async function upsert(page, id, change) {
  await page.evaluate(({ id, change }) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get(id) ?? { id };
    d.upsert({ ...s, ...change, activity: { ...(s.activity ?? {}), ...(change.activity ?? {}) } });
  }, { id, change });
}

// The motions a person sets off on a desktop: choosing a row, its menu, a move, a disclosure,
// the inspector, and the news that comes (a glyph changing, a row arriving and going, a dot).
async function desktopTour(page) {
  await row(page, CLAUDE).locator('.row-main').click();
  await expect(row(page, CLAUDE)).toHaveClass(/selected/);
  await row(page, CLAUDE).locator('.row-main').press('Shift+F10');
  await expect(page.locator('.row-menu')).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.locator('.row-menu')).toHaveCount(0);
  await row(page, CLAUDE).locator('.row-main').press('Alt+ArrowDown');
  const copilot = await open(page, COPILOT);
  await copilot.locator('details.reasoning summary').click();
  await expect(copilot.locator('details.reasoning')).toHaveAttribute('open', '');
  await copilot.locator('.inspector-button').click();
  await expect(copilot.locator('.inspector')).toBeHidden();
  await copilot.locator('.inspector-button').click();
  await expect(copilot.locator('.inspector')).toBeVisible();
  await upsert(page, CODEX, { status: 'working', statusSince: later(), updatedAt: later() });
  await expect(row(page, CODEX).locator('.glyph')).toHaveAttribute('data-glyph', 'working');
  await upsert(page, 'demo-motion', { agent: 'copilot', title: 'Arrives and goes', status: 'idle', startedAt: later(), updatedAt: later() });
  await expect(row(page, 'demo-motion')).toHaveCount(1);
  await page.evaluate(() => window.agentBridgeDemo.remove('demo-motion'));
  await expect(row(page, 'demo-motion')).toHaveCount(0);
}

test('nothing moves as the app starts, and every motion a desktop then draws is timed by its catalogue entry\'s token, none past 300 ms', async ({ page }) => {
  await openDemo(page);
  await expect(page.locator('#needs-you .needs-entry')).not.toHaveCount(0);
  await page.waitForTimeout(400);
  expect(await motions(page)).toEqual([]);
  expect(await cssMotions(page)).toEqual([]);
  await desktopTour(page);
  const t = await tokens(page);
  const drawn = await motions(page);
  const ids = new Set(drawn.map((m) => m.id));
  for (const entry of ['selection', 'menu', 'reorder', 'disclosure', 'pane', 'glyph', 'listItem', 'activityDot']) {
    expect(ids, `drew ${entry}`).toContain(`ab-motion-${entry}`);
  }
  for (const m of drawn) {
    const entry = m.id.replace(/^ab-motion-/, '');
    expect(t[entry], m.id).toContain(m.duration);
    expect(m.duration, m.id).toBeLessThanOrEqual(300);
  }
});

test('the tab bar\'s indicator slides to the tab chosen, and what the tab shows fades in without sliding', async ({ page }) => {
  await openDemo(page, { width: 1000 });
  const v = await open(page, COPILOT);
  await v.getByRole('tab', { name: 'Tools' }).click();
  const drawn = await motions(page);
  const indicator = drawn.find((m) => m.id === 'ab-motion-selection');
  expect(indicator?.from).toMatch(/^translate\(-[\d.]+px, 0px\) scaleX\(/);
  const tab = drawn.find((m) => m.id === 'ab-motion-tab');
  expect(tab).toBeTruthy();
  expect(tab.travels).toBe(false);
});

test('on a phone a session\'s content slides in from the trailing side, and the list\'s from the leading side on Back', async ({ page }) => {
  await openDemo(page, { width: 390, height: 844 });
  const t = await tokens(page);
  await open(page, CLAUDE);
  await view(page, CLAUDE).locator('.session-header .back').click();
  await expect(row(page, CLAUDE)).toBeVisible();
  const push = (await motions(page)).filter((m) => m.id === 'ab-motion-push');
  expect(push[0].from).toBe('translate(24px, 0px)');
  expect(push.length).toBeGreaterThan(1);
  expect(push.slice(1).every((m) => m.from === 'translate(-24px, 0px)')).toBe(true);
  expect(push.every((m) => m.duration === t.push[0])).toBe(true);
});

test('an approval\'s command never moves: a session asking a question fades in where it is, and Needs you is back at once', async ({ page }) => {
  await openDemo(page, { width: 390, height: 844 });
  await expect(page.locator('#needs-you')).toBeVisible();
  await open(page, COPILOT);
  await view(page, COPILOT).locator('.session-header .back').click();
  await expect(row(page, COPILOT)).toBeVisible();
  const push = (await motions(page)).filter((m) => m.id === 'ab-motion-push');
  expect(push[0]).toMatchObject({ travels: false });
  expect(push.length).toBeGreaterThan(1);
  expect((await motions(page)).filter((m) => m.inLane)).toEqual([]);
  expect(await cssMotions(page)).toEqual([]);
});

test('a Needs you entry that arrives once the app is running has its mark fade in, and nothing else in the lane moves', async ({ page }) => {
  await openDemo(page);
  const t = await tokens(page);
  await page.waitForTimeout(400);
  await ask(page, CLAUDE);
  await expect(page.locator(`#needs-you .needs-entry[data-id="${CLAUDE}"]`)).toBeVisible();
  expect((await motions(page)).filter((m) => m.inLane)).toEqual([
    { id: 'ab-motion-listItem', duration: t.listItem[0], travels: false, from: null, inLane: true, cls: 'needs-mark' },
  ]);
  expect(await cssMotions(page)).toEqual([]);
});

test('on a phone, Back to the list shows Needs you as it was: no mark fades in again', async ({ page }) => {
  await openDemo(page, { width: 390, height: 844 });
  await page.waitForTimeout(400);
  await ask(page, CLAUDE);
  await expect(page.locator(`#needs-you .needs-entry[data-id="${CLAUDE}"]`)).toBeVisible();
  const before = (await motions(page)).filter((m) => m.inLane).length;
  await open(page, CODEX);
  await view(page, CODEX).locator('.session-header .back').click();
  await expect(page.locator(`#needs-you .needs-entry[data-id="${CLAUDE}"]`)).toBeVisible();
  await page.waitForTimeout(300);
  expect((await motions(page)).filter((m) => m.inLane)).toHaveLength(before);
  expect(await cssMotions(page)).toEqual([]);
});

test('with reduced motion nothing travels: the only motions are fades no longer than the cross-fade, and presses change at once', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await openDemo(page, { width: 390, height: 844 });
  await expect(page.locator('#needs-you .needs-entry')).not.toHaveCount(0);
  await page.waitForTimeout(400);
  expect(await cssMotions(page)).toEqual([]);
  await open(page, COPILOT);
  await view(page, COPILOT).locator('.session-header .back').click();
  await expect(row(page, COPILOT)).toBeVisible();
  await page.setViewportSize({ width: 1440, height: 900 });
  await desktopTour(page);
  const t = await tokens(page);
  const drawn = await motions(page);
  expect(drawn.map((m) => m.id)).toContain('ab-motion-push');
  for (const m of drawn) {
    expect(m.travels, m.id).toBe(false);
    expect(m.duration, m.id).toBeLessThanOrEqual(t.crossFade);
  }
  const transitions = await page.evaluate(() => [...document.querySelectorAll('.button, .icon-button, .row-main')]
    .flatMap((el) => getComputedStyle(el).transitionDuration.split(',').map((d) => d.trim())));
  expect(new Set(transitions)).toEqual(new Set(['0s']));
});

test('a tap during a push lands: Back works while the session is still sliding in', async ({ page }) => {
  await openDemo(page, { width: 390, height: 844 });
  // Long enough to be sure the tap comes mid-motion.
  await page.evaluate(() => document.documentElement.style.setProperty('--ab-motion-push-duration', '5000ms'));
  await row(page, CLAUDE).locator('.row-main').click();
  const back = view(page, CLAUDE).locator('.session-header .back');
  await expect(back).toBeAttached();
  expect(await page.evaluate(() => document.getAnimations().some((a) => a.id === 'ab-motion-push' && a.playState === 'running'))).toBe(true);
  const b = await back.boundingBox();
  await page.mouse.click(b.x + b.width / 2, b.y + b.height / 2);
  await expect(row(page, CLAUDE)).toBeVisible();
  await expect(view(page, CLAUDE)).toBeHidden();
});

test('a tap during a menu\'s entrance lands on the item under it', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => document.documentElement.style.setProperty('--ab-motion-menu-duration', '5000ms'));
  await row(page, CLAUDE).locator('.row-main').click({ button: 'right' });
  const pin = page.locator('.row-menu').getByRole('menuitem', { name: 'Pin' });
  await expect(pin).toBeVisible();
  expect(await page.evaluate(() => document.getAnimations().some((a) => a.id === 'ab-motion-menu' && a.playState === 'running'))).toBe(true);
  const b = await pin.boundingBox();
  await page.mouse.click(b.x + b.width / 2, b.y + b.height / 2);
  await expect(row(page, CLAUDE)).toHaveAttribute('data-pinned', 'true');
});

test('a question that arrives is scrolled to while the conversation is read to its end, and never pulls one scrolled back', async ({ page }) => {
  await openDemo(page, { height: 600 });
  const long = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
  await upsert(page, CODEX, { activity: { response: long }, updatedAt: later() });
  const v = await open(page, CODEX);
  const scroller = v.locator('.stream-scroll');
  const ask = (id, ms) => page.evaluate(({ id, ms, from }) => {
    const d = window.agentBridgeDemo;
    const q = { ...d.host.get(from).question, id };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), question: q, status: 'waiting', updatedAt: new Date(Date.now() + ms).toISOString() });
  }, { id, ms, from: COPILOT });
  // Read back to the top: a question that comes leaves the place alone.
  await scroller.evaluate((el) => { el.scrollTop = 0; });
  await ask('q-motion-1', 2000);
  await expect(v.locator('.question-slot .question')).toHaveCount(1);
  await page.waitForTimeout(400);
  expect(await scroller.evaluate((el) => el.scrollTop)).toBe(0);
  // At the end: the next question comes into sight.
  await scroller.evaluate((el) => { el.scrollTop = el.scrollHeight; });
  await page.evaluate(() => window.agentBridgeDemo.upsert({ ...window.agentBridgeDemo.host.get('019a1b2c-codex-example'), question: null, updatedAt: new Date(Date.now() + 3000).toISOString() }));
  await expect(v.locator('.question-slot .question')).toHaveCount(0);
  await scroller.evaluate((el) => { el.scrollTop = el.scrollHeight; });
  await ask('q-motion-2', 4000);
  await expect(v.locator('.question-slot .question')).toBeInViewport();
});

test('a control eases back to its fill only when a press lets go: a row selected, or the theme changing, takes its fill at once', async ({ page }) => {
  await openDemo(page);
  const t = await tokens(page);
  const durations = () => page.evaluate(() => [...document.querySelectorAll('.button, .icon-button, .row-main, .view-tab')]
    .filter((el) => !el.dataset.press).flatMap((el) => getComputedStyle(el).transitionDuration.split(',').map((d) => d.trim())));
  expect(new Set(await durations())).toEqual(new Set(['0s']));
  // Selecting a row by keyboard is no press: its fill and its neighbours' change at once.
  await row(page, CLAUDE).locator('.row-main').focus();
  await page.keyboard.press('Enter');
  await expect(row(page, CLAUDE)).toHaveClass(/selected/);
  await page.emulateMedia({ colorScheme: 'dark' });
  expect(await cssMotions(page)).toEqual([]);
  const main = row(page, CODEX).locator('.row-main');
  const b = await main.boundingBox();
  await page.mouse.move(b.x + 20, b.y + b.height / 2);
  // The page's clock held while the release is read, so a slow runner can't let it run out first.
  await holdClock(page);
  await page.mouse.down();
  await page.mouse.up();
  expect(await main.evaluate((el) => [el.dataset.press, getComputedStyle(el).transitionDuration.split(',')[0].trim()]))
    .toEqual(['released', `${t.press[0] / 1000}s`]);
  await page.clock.runFor(t.press[0] + 100);
  await expect(main).not.toHaveAttribute('data-press', /./);
});

const luminance = (rgb) => {
  const [r, g, b] = rgb.map((v) => { const c = v / 255; return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4; });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
};
const contrast = (a, b) => { const [x, y] = [luminance(a), luminance(b)].sort((m, n) => n - m); return (x + 0.05) / (y + 0.05); };

for (const scheme of ['light', 'dark']) {
  test(`in ${scheme}, a pressed Approve once and a pressed Deny keep their labels at 4.5:1 and never fade`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme });
    await openDemo(page);
    await page.evaluate((id) => {
      const d = window.agentBridgeDemo;
      const t = new Date(Date.now() + 60_000).toISOString();
      const question = {
        id: 'r1#motion', kind: 'approval', message: 'Allow **shell**?\n\n```\ngit push origin main\n```', askedAt: t,
        fields: [{ name: 'decision', type: 'choice', required: true, options: [{ value: 'approve', label: 'Approve once' }, { value: 'deny', label: 'Deny' }] }],
      };
      d.upsert({ ...d.host.get(id), status: 'waiting', statusSince: t, updatedAt: t, question });
    }, COPILOT);
    for (const [name, cls] of [['Approve once', 'needs-approve'], ['Deny', 'needs-deny']]) {
      const button = page.locator(`#needs-you .needs-entry[data-id="${COPILOT}"] .${cls}`);
      await expect(button).toBeVisible();
      const b = await button.boundingBox();
      await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2);
      await page.mouse.down();
      // Painted colours, read off a canvas, so color-mix() and the card beneath both count.
      const pressed = await button.evaluate((el) => {
        const cs = getComputedStyle(el);
        const paint = (color) => {
          const c = document.createElement('canvas').getContext('2d');
          c.fillStyle = color; c.fillRect(0, 0, 1, 1);
          return [...c.getImageData(0, 0, 1, 1).data];
        };
        let under = 'rgb(255, 255, 255)';
        for (let p = el; p; p = p.parentElement) {
          const bg = getComputedStyle(p).backgroundColor;
          if (paint(bg)[3] === 255) { under = bg; break; }
        }
        const c = document.createElement('canvas').getContext('2d');
        c.fillStyle = under; c.fillRect(0, 0, 1, 1);
        c.fillStyle = cs.backgroundColor; c.fillRect(0, 0, 1, 1);
        return { opacity: cs.opacity, active: el.matches(':active'), fill: [...c.getImageData(0, 0, 1, 1).data].slice(0, 3), label: paint(cs.color).slice(0, 3) };
      });
      // Let go somewhere else: the press is all this measures, not an answer sent.
      await page.mouse.move(1, 1);
      await page.mouse.up();
      expect(pressed.active, name).toBe(true);
      expect(pressed.opacity, name).toBe('1');
      expect(contrast(pressed.fill, pressed.label), name).toBeGreaterThanOrEqual(4.5);
    }
  });
}

test('a glyph that changes fades out as it was, in its own colour, not in the colour of what it became', async ({ page }) => {
  await openDemo(page);
  await upsert(page, CODEX, { status: 'working', statusSince: later(), updatedAt: later() });
  const glyph = row(page, CODEX).locator('.row-glyph .glyph');
  await expect(glyph).toHaveAttribute('data-glyph', 'working');
  const working = await glyph.evaluate((el) => getComputedStyle(el).color);
  const before = await page.evaluate(() => window.__shadows.length);
  await upsert(page, CODEX, { status: 'idle', statusSince: later(2000), updatedAt: later(2000) });
  await expect(glyph).not.toHaveAttribute('data-glyph', 'working');
  const now = await glyph.evaluate((el) => getComputedStyle(el).color);
  expect(now).not.toBe(working);
  const copies = await page.evaluate((from) => window.__shadows.slice(from).map((root) => {
    const svg = root.querySelector('svg');
    return svg ? { color: svg.style.color, refs: [...svg.querySelectorAll('use')].map((u) => u.getAttribute('href')) } : null;
  }).filter(Boolean), before);
  expect(copies.length).toBeGreaterThan(0);
  for (const c of copies) {
    expect(c.color).toBe(working);
    expect(c.refs.some((r) => r.endsWith('#glyph-working--still'))).toBe(true);
  }
});

test('what leaves is a copy with nothing in it to find, use or run: text stays text, and no id, link, control or script is carried', async ({ page }) => {
  await openDemo(page);
  const copy = await page.evaluate(async () => {
    const { leave } = await import('./js/ui/motion.js');
    const el = document.createElement('div');
    el.id = 'leaving';
    el.className = 'session';
    el.dataset.id = 'x';
    const words = '<img src=x onerror="window.__ran=1"><b>bold</b>';
    el.append(
      document.createTextNode(words),
      Object.assign(document.createElement('a'), { href: 'https://example.com/', id: 'link', textContent: 'link' }),
      Object.assign(document.createElement('button'), { id: 'b', textContent: 'Approve once', onclick: () => { window.__ran = 2; } }),
      Object.assign(document.createElement('input'), { value: 'typed' }),
      Object.assign(document.createElement('script'), { textContent: 'window.__ran = 3' }),
    );
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.innerHTML = '<use href="design/sprite.svg#glyph-idle"></use><use href="https://example.com/x.svg#a"></use><foreignObject><p>hidden</p></foreignObject><a href="https://example.com/"><path d="M0 0"/></a>';
    el.append(svg);
    Object.assign(el.style, { position: 'fixed', top: '100px', left: '100px', width: '200px', height: '80px', backgroundImage: 'url("ghost-fetch.png")', cursor: 'url("ghost-cursor.png"), auto' });
    document.body.append(el);
    const from = window.__shadows.length;
    const a = leave(el, 'listItem');
    el.remove();
    const root = window.__shadows[from];
    const host = root.host;
    const all = [...root.querySelectorAll('*')];
    const out = {
      animated: !!a,
      host: { hidden: host.getAttribute('aria-hidden'), inert: host.inert, pointer: host.style.pointerEvents },
      text: root.textContent,
      tags: [...new Set(all.map((n) => n.localName))].sort(),
      attrs: [...new Set(all.flatMap((n) => [...n.attributes].map((x) => x.name)))].sort(),
      refs: all.filter((n) => n.localName === 'use').map((n) => n.getAttribute('href')),
      found: document.querySelectorAll('#leaving, #link, #b, .session[data-id="x"]').length,
      urls: all.filter((n) => (n.getAttribute('style') ?? '').includes('url(')).length,
    };
    // Long enough for anything in it to have run, and for its motion to end, when it goes: on a
    // busy runner a motion can start a while after it is asked for.
    await Promise.all([new Promise((r) => setTimeout(r, 300)), a.finished.catch(() => {})]);
    out.ran = window.__ran ?? null;
    out.gone = !host.isConnected;
    return out;
  });
  expect(copy.animated).toBe(true);
  expect(copy.host).toEqual({ hidden: 'true', inert: true, pointer: 'none' });
  expect(copy.text).toContain('<img src=x onerror="window.__ran=1"><b>bold</b>');
  expect(copy.text).toContain('Approve once');
  expect(copy.text).not.toContain('window.__ran = 3');
  expect(copy.tags).toEqual(['div', 'path', 'span', 'svg', 'use'].filter((t) => copy.tags.includes(t)));
  for (const tag of ['a', 'b', 'button', 'img', 'input', 'script', 'foreignObject', 'p']) { expect(copy.tags).not.toContain(tag); }
  expect(copy.attrs.filter((a) => !['style', 'href', 'd', 'width', 'height'].includes(a))).toEqual([]);
  expect(copy.refs).toEqual(['design/sprite.svg#glyph-idle']);
  expect(copy.found).toBe(0);
  // Nothing in it names a resource to fetch.
  expect(copy.urls).toBe(0);
  expect(copy.ran).toBe(null);
  expect(copy.gone).toBe(true);
});

// An app banner leaving (ui.md §5.3, Toast and banner; PWA app banners): its space closes at once,
// and a copy goes back up the way it came in, quicker; under reduced motion the copy only fades.
async function bannerLeaving(page) {
  return page.evaluate(async () => {
    const { conceal, reveal } = await import('./js/ui/motion.js');
    const banner = document.getElementById('replaced-banner');
    reveal(banner);
    await Promise.all(banner.getAnimations().map((a) => a.finished.catch(() => {})));
    const from = window.__motions.length;
    conceal(banner);
    const hiddenAtOnce = banner.hidden;
    const m = window.__motions.slice(from).find((x) => x.a.id === 'ab-motion-toast');
    const copy = m && window.__shadows.at(-1);
    const out = {
      hiddenAtOnce,
      moved: !!m,
      duration: m?.duration ?? 0,
      keyframes: m?.keyframes.map((k) => ({ opacity: k.opacity, transform: k.transform ?? null })) ?? [],
      text: copy?.textContent ?? '',
    };
    await m?.a.finished.catch(() => {});
    out.gone = m ? !m.el.isConnected : true;
    return out;
  });
}

test('an app banner leaves as it came, quicker: up and out over the toast exit time, its space closing at once', async ({ page }) => {
  await openDemo(page);
  const t = await tokens(page);
  const out = await bannerLeaving(page);
  expect(out.hiddenAtOnce).toBe(true);
  expect(out.moved).toBe(true);
  expect(out.text).toContain('This app is open in another window or tab.');
  expect(out.duration).toBe(t.toast[1]);
  expect(out.duration).toBeLessThan(t.toast[0]);
  expect(out.keyframes).toEqual([{ opacity: 1, transform: 'none' }, { opacity: 0, transform: 'translate(0px, -12px)' }]);
  expect(out.gone).toBe(true);
});

test('with reduced motion an app banner leaving only cross-fades, and its space still closes at once', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await openDemo(page);
  const t = await tokens(page);
  const out = await bannerLeaving(page);
  expect(out.hiddenAtOnce).toBe(true);
  expect(out.moved).toBe(true);
  expect(out.duration).toBeGreaterThan(0);
  expect(out.keyframes).toEqual([{ opacity: 1, transform: null }, { opacity: 0, transform: null }]);
  expect(out.duration).toBeLessThanOrEqual(t.crossFade);
  expect(out.gone).toBe(true);
});

test('a copy shows only what showed of what left: clipped by its scroller, and drawn where it was even mid-motion', async ({ page }) => {
  await openDemo(page);
  const out = await page.evaluate(async () => {
    const { leave, enter } = await import('./js/ui/motion.js');
    const scroller = document.createElement('div');
    Object.assign(scroller.style, { position: 'fixed', top: '100px', left: '100px', width: '200px', height: '100px', overflow: 'auto' });
    const pad = document.createElement('div');
    pad.style.height = '80px';
    const item = document.createElement('div');
    Object.assign(item.style, { height: '60px', background: 'red' });
    scroller.append(pad, item, Object.assign(document.createElement('div'), { style: 'height: 200px' }));
    document.body.append(scroller);
    // Mid-entrance: 12 px down, as a menu or a row is while it settles.
    document.documentElement.style.setProperty('--ab-motion-menu-duration', '5000ms');
    const moving = enter(item, 'menu', { dy: 1 });
    moving.pause();
    moving.currentTime = 0;
    const r = item.getBoundingClientRect();
    const from = window.__shadows.length;
    leave(item, 'listItem');
    const root = window.__shadows[from];
    const host = root.host;
    return {
      rect: { top: r.top, left: r.left },
      host: { top: parseFloat(host.style.top), left: parseFloat(host.style.left), clip: host.style.clipPath, z: Number(host.style.zIndex) },
      transform: root.firstElementChild.style.transform,
      translate: root.firstElementChild.style.translate,
    };
  });
  expect(out.host.top).toBeCloseTo(out.rect.top, 0);
  expect(out.host.left).toBeCloseTo(out.rect.left, 0);
  expect(out.transform).toBe('none');
  expect(out.translate).toBe('none');
  // The item runs from 80 px (plus the motion's distance) into a scroller 100 px tall: what
  // showed is its top, and the rest is cut. (inset() as CSS shortens it: top, right, bottom, left.)
  expect(out.host.clip).toMatch(/^inset\(/);
  const [t, r = t, b = t] = [...out.host.clip.matchAll(/([\d.]+)px/g)].map((m) => Number(m[1]));
  expect(t).toBe(0);
  expect(r).toBe(0);
  expect(b).toBeGreaterThan(0);
  // Under the app bar and the bottom bar.
  expect(out.host.z).toBeLessThan(20);
});

test('a finger held on the conversation keeps it where it is when a question arrives, even after the browser takes the touch over to scroll', async ({ page }) => {
  await openDemo(page, { height: 600 });
  const long = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
  await upsert(page, CODEX, { activity: { response: long }, updatedAt: later() });
  const v = await open(page, CODEX);
  const scroller = v.locator('.stream-scroll');
  const ask = (id, ms) => page.evaluate(({ id, ms, from }) => {
    const d = window.agentBridgeDemo;
    const q = { ...d.host.get(from).question, id };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), question: q, status: 'waiting', updatedAt: new Date(Date.now() + ms).toISOString() });
  }, { id, ms, from: COPILOT });
  await scroller.evaluate((el) => { el.scrollTop = el.scrollHeight; });
  const top = await scroller.evaluate((el) => el.scrollTop);
  // A finger goes down; the browser takes the gesture to scroll, which cancels its pointer.
  await scroller.evaluate((el) => {
    el.dispatchEvent(new Event('touchstart', { bubbles: true }));
    el.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, pointerType: 'touch' }));
    el.dispatchEvent(new PointerEvent('pointercancel', { bubbles: true, pointerType: 'touch' }));
    el.dispatchEvent(new PointerEvent('pointerleave', { pointerType: 'touch' }));
  });
  await ask('q-held', 2000);
  await expect(v.locator('.question-slot .question')).toHaveCount(1);
  await page.waitForTimeout(400);
  expect(await scroller.evaluate((el) => el.scrollTop)).toBe(top);
  await expect(v.locator('.question-slot .question')).not.toBeInViewport();
});

test('a question that arrives with a longer response is brought into sight below all of it', async ({ page }) => {
  await openDemo(page, { height: 900 });
  await upsert(page, CODEX, { activity: { response: 'Short.', userMessage: null, reasoning: null, currentTool: null }, question: null, updatedAt: later() });
  const v = await open(page, CODEX);
  const scroller = v.locator('.stream-scroll');
  // The whole conversation fits: it is read to its end without scrolling, so the question is in
  // sight when it is first drawn, until the response drawn with it pushes it down.
  expect(await scroller.evaluate((el) => el.scrollHeight <= el.clientHeight)).toBe(true);
  const long = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
  await page.evaluate((long) => {
    const d = window.agentBridgeDemo;
    const s = d.host.get('019a1b2c-codex-example');
    const question = { id: 'q-with-long', kind: 'form', message: 'Carry on?', askedAt: new Date().toISOString(), fields: [] };
    d.upsert({ ...s, activity: { ...s.activity, response: long }, question, status: 'waiting', updatedAt: new Date(Date.now() + 5000).toISOString() });
  }, long);
  await expect(v.locator('.question-slot .question')).toBeInViewport();
});

test('a question that arrives after the response has grown, in an update of its own, is brought into sight: nobody scrolled away from the end', async ({ page }) => {
  await openDemo(page, { height: 900 });
  await upsert(page, CODEX, { activity: { response: 'Short.', userMessage: null, reasoning: null, currentTool: null }, question: null, updatedAt: later() });
  const v = await open(page, CODEX);
  const scroller = v.locator('.stream-scroll');
  expect(await scroller.evaluate((el) => el.scrollHeight <= el.clientHeight)).toBe(true);
  // The response grows below, on its own: the conversation isn't followed, and stays where it is.
  const long = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
  await upsert(page, CODEX, { activity: { response: long }, updatedAt: later(3000) });
  await expect(scroller).toContainText('Line 60 of a long answer.');
  expect(await scroller.evaluate((el) => ({ top: el.scrollTop, longer: el.scrollHeight > el.clientHeight + 100 }))).toEqual({ top: 0, longer: true });
  // Then a question, in an update of its own: followed as if both had come together.
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const question = { id: 'q-after-long', kind: 'form', message: 'Carry on?', askedAt: new Date().toISOString(), fields: [] };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), question, status: 'waiting', updatedAt: new Date(Date.now() + 6000).toISOString() });
  });
  await expect(v.locator('.question-slot .question')).toBeInViewport();
});

test('a question that arrives after the reader opened the reasoning leaves the conversation where they are reading', async ({ page }) => {
  await openDemo(page, { height: 900 });
  const long = Array.from({ length: 60 }, (_, i) => `Thought ${i + 1}.`).join('\n\n');
  await upsert(page, CODEX, { activity: { response: 'Short.', reasoning: long, userMessage: null, currentTool: null }, question: null, updatedAt: later() });
  const v = await open(page, CODEX);
  const scroller = v.locator('.stream-scroll');
  // Read to its end, then grown by an update of its own: still at its end.
  expect(await scroller.evaluate((el) => el.scrollHeight <= el.clientHeight)).toBe(true);
  await upsert(page, CODEX, { activity: { response: 'Short, and then a little more.' }, updatedAt: later(3000) });
  await expect(scroller).toContainText('a little more');
  // The reader opens the reasoning, which grows the conversation with no scroll, and reads it.
  await v.locator('details.reasoning').evaluate((d) => { d.open = true; });
  await expect(scroller).toContainText('Hide reasoning');
  expect(await scroller.evaluate((el) => ({ top: el.scrollTop, longer: el.scrollHeight > el.clientHeight + 100 }))).toEqual({ top: 0, longer: true });
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const question = { id: 'q-after-reasoning', kind: 'form', message: 'Carry on?', askedAt: new Date().toISOString(), fields: [] };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), question, status: 'waiting', updatedAt: new Date(Date.now() + 6000).toISOString() });
  });
  await expect(v.locator('.question-slot .question')).toHaveCount(1);
  await page.waitForTimeout(400);
  expect(await scroller.evaluate((el) => el.scrollTop)).toBe(0);
});

test('where the reader is is read when a question comes, though the scroll that took them there has yet to send its event', async ({ page }) => {
  await openDemo(page, { height: 600 });
  const long = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
  await upsert(page, CODEX, { activity: { response: long, reasoning: null, userMessage: null, currentTool: null }, question: null, updatedAt: later() });
  const v = await open(page, CODEX);
  const scroller = v.locator('.stream-scroll');
  const settle = () => page.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))));
  // A scroll's event comes at the browser's next frame: held back here, as if it hadn't come yet.
  await page.evaluate(() => document.addEventListener('scroll', (e) => e.stopPropagation(), true));
  const ask = (id, top, ms) => page.evaluate(({ id, top, ms }) => {
    const el = document.querySelector('.session-view[data-id="019a1b2c-codex-example"] .stream-scroll');
    el.scrollTop = top === 'end' ? el.scrollHeight : top;
    const d = window.agentBridgeDemo;
    const question = { id, kind: 'form', message: 'Carry on?', askedAt: new Date().toISOString(), fields: [] };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), question, status: 'waiting', updatedAt: new Date(Date.now() + ms).toISOString() });
  }, { id, top, ms });
  const clear = (ms) => page.evaluate((ms) => window.agentBridgeDemo.upsert({ ...window.agentBridgeDemo.host.get('019a1b2c-codex-example'), question: null, updatedAt: new Date(Date.now() + ms).toISOString() }), ms);
  // At the top when last measured; at the end, just now, when a question comes: brought into sight.
  await scroller.evaluate((el) => { el.scrollTop = 0; });
  await upsert(page, CODEX, { activity: { response: `${long}\n\nOne more.` }, updatedAt: later(2000) });
  await expect(scroller).toContainText('One more.');
  await ask('q-race-end', 'end', 3000);
  await expect(v.locator('.question-slot .question')).toBeInViewport();
  // At the end when last measured; back at the top, just now, when a question comes: left there.
  await clear(4000);
  await expect(v.locator('.question-slot .question')).toHaveCount(0);
  await scroller.evaluate((el) => { el.scrollTop = el.scrollHeight; });
  await upsert(page, CODEX, { activity: { response: `${long}\n\nOne more, and another.` }, updatedAt: later(5000) });
  await expect(scroller).toContainText('and another.');
  await settle();
  await ask('q-race-top', 0, 6000);
  await expect(v.locator('.question-slot .question')).toHaveCount(1);
  await page.waitForTimeout(400);
  expect(await scroller.evaluate((el) => el.scrollTop)).toBe(0);
});

test('a reader who scrolls back and returns to the same place, with the response grown below, is left there when a question comes', async ({ page }) => {
  await openDemo(page, { height: 600 });
  const long = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
  await upsert(page, CODEX, { activity: { response: long, reasoning: null, userMessage: null, currentTool: null }, question: null, updatedAt: later() });
  const v = await open(page, CODEX);
  const scroller = v.locator('.stream-scroll');
  const settle = () => page.evaluate(() => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r))));
  // At the end, as an update reads it; then the response grows below, with no scroll.
  await scroller.evaluate((el) => { el.scrollTop = el.scrollHeight; });
  await settle();
  await upsert(page, CODEX, { activity: { response: `${long}\n\nOne more.` }, updatedAt: later(2000) });
  await expect(scroller).toContainText('One more.');
  const more = Array.from({ length: 20 }, (_, i) => `Later line ${i + 1}.`).join('\n\n');
  await upsert(page, CODEX, { activity: { response: `${long}\n\nOne more.\n\n${more}` }, updatedAt: later(3000) });
  await expect(scroller).toContainText('Later line 20.');
  // A page back and a page forward, as keys do: the same place, now well above the end.
  const at = await scroller.evaluate((el) => el.scrollTop);
  await scroller.evaluate((el) => { el.scrollTop -= 300; });
  await settle();
  await scroller.evaluate((el, at) => { el.scrollTop = at; }, at);
  await settle();
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const question = { id: 'q-same-place', kind: 'form', message: 'Carry on?', askedAt: new Date().toISOString(), fields: [] };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), question, status: 'waiting', updatedAt: new Date(Date.now() + 6000).toISOString() });
  });
  await expect(v.locator('.question-slot .question')).toHaveCount(1);
  await page.waitForTimeout(400);
  expect(await scroller.evaluate((el) => el.scrollTop)).toBe(at);
});

test('the same question redrawn while its wait runs says so on the new drawing, for what is left of the wait and no longer', async ({ page }) => {
  await openDemo(page);
  const v = await open(page, CODEX);
  // A pointer resting on the conversation: the question arriving isn't scrolled to, so no scroll
  // (and no wait for it to come to rest) runs on behind the test's own clock.
  await v.locator('.stream-scroll').hover();
  await holdClock(page);
  const draw = (message) => page.evaluate((message) => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-redrawn', kind: 'form', message, askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), status: 'waiting', statusSince: t, updatedAt: new Date(Date.now() + 60_000 + message.length).toISOString(), question });
  }, message);
  const marked = () => v.locator('.question-slot .question').evaluate((q) => q.hasAttribute('data-arriving'));
  // The fake clock drives timers and animation frames, not native ResizeObserver delivery.
  // Let watchMoves read each drawing before running the clock beyond its arrival wait.
  const laidOut = () => v.locator('.question-slot .question').evaluate((q) => new Promise((resolve) => {
    const observer = new ResizeObserver(() => { observer.disconnect(); resolve(); });
    observer.observe(q);
  }));
  await draw('Which?');
  await page.clock.runFor(20);
  await laidOut();
  expect(await marked()).toBe(true);
  const first = await v.locator('.question-slot .question').elementHandle();
  const top = await v.locator('.question-slot .choice').first().evaluate((c) => c.getBoundingClientRect().top);
  // 300 ms in, drawn again (asked again a moment later, in the same words): a new drawing of the
  // same question, its answers where they were. One that moves them waits afresh (#491).
  await page.clock.runFor(280);
  await draw('Which?');
  await page.clock.runFor(20);
  await laidOut();
  expect(await first.evaluate((q) => q.isConnected)).toBe(false);
  expect(await v.locator('.question-slot .choice').first().evaluate((c) => c.getBoundingClientRect().top)).toBe(top);
  expect(await marked()).toBe(true);
  // 550 ms in: the wait ran out at 500, and the mark with it; a click counts.
  await page.clock.runFor(230);
  expect(await marked()).toBe(false);
  const before = await page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
  await v.locator('.question-slot .choice').first().evaluate((c) => c.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 })));
  await page.clock.runFor(50);
  expect(await page.evaluate(() => window.agentBridgeDemo.host.answerLog.length)).toBeGreaterThan(before);
});

// Until nothing scrolls the scroller on its own (a smooth scroll to a question the app started
// as the session opened, say): two looks a tenth of a second apart find it where it was.
async function atRest(page, scroller) {
  await expect.poll(async () => {
    const was = await scroller.evaluate((el) => el.scrollTop);
    await page.waitForTimeout(100);
    return (await scroller.evaluate((el) => el.scrollTop)) === was;
  }).toBe(true);
}

// A question with two choices, asked of Codex's session; extra: anything else to set with it.
const choices = (id, message = 'Which?') => ({
  id, kind: 'form', message, askedAt: new Date(Date.now() + 60_000).toISOString(),
  fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
});
const askCodex = (page, question, extra = {}, ms = 60_000) => page.evaluate(({ question, extra, ms }) => {
  const d = window.agentBridgeDemo;
  const t = new Date(Date.now() + ms).toISOString();
  d.upsert({ ...d.host.get('019a1b2c-codex-example'), status: 'waiting', statusSince: t, updatedAt: t, question, ...extra });
}, { question, extra, ms });
const answered = (page) => page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);

test('a question that stays takes no click for a moment when something above it moves it, as its title wrapping does', async ({ page }) => {
  await openDemo(page);
  const question = choices('q-moved');
  await askCodex(page, question);
  const v = await open(page, CODEX);
  const choice = v.locator('.question-slot .choice').first();
  await holdClock(page);
  const top = await choice.evaluate((c) => c.getBoundingClientRect().top);
  const before = await answered(page);
  // The same question, untouched; the session's title grows long enough to wrap in the header.
  await askCodex(page, question, { title: Array.from({ length: 24 }, () => 'a title long enough to wrap').join(' ') }, 61_000);
  await page.clock.runFor(20);
  await expect.poll(() => v.locator('.question-slot .question').evaluate((q) => q.hasAttribute('data-arriving'))).toBe(true);
  expect(await choice.evaluate((c, top) => c.getBoundingClientRect().top - top, top)).toBeGreaterThan(1);
  // A click already on its way to where the choice was before: dropped.
  await choice.evaluate((c) => c.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 })));
  await page.clock.runFor(50);
  expect(await answered(page)).toBe(before);
  // Once the wait has run out, a click counts.
  await page.clock.runFor(500);
  await choice.evaluate((c) => c.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 })));
  await page.clock.runFor(50);
  expect(await answered(page)).toBeGreaterThan(before);
});

test('a click in the same task as an update that moves the question, before any frame, doesn\'t answer it', async ({ page }) => {
  await openDemo(page);
  const question = choices('q-moved-now');
  await askCodex(page, question);
  const v = await open(page, CODEX);
  await expect(v.locator('.question-slot .question[data-arriving]')).toHaveCount(0);
  const before = await answered(page);
  const after = await page.evaluate((question) => new Promise((resolve) => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 61_000).toISOString();
    const view = document.querySelector('.session-view[data-id="019a1b2c-codex-example"]');
    const top = view.querySelector('.question-slot .choice').getBoundingClientRect().top;
    // A click on its way lands as soon as the update is drawn into the page, before the browser
    // has drawn a frame of it.
    const seen = new MutationObserver(() => {
      seen.disconnect();
      const choice = view.querySelector('.question-slot .choice');
      const by = choice.getBoundingClientRect().top - top;
      choice.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
      resolve({ moved: by > 1, armed: view.querySelector('.question-slot .question').hasAttribute('data-arriving') });
    });
    seen.observe(view.querySelector('.session-title'), { childList: true, subtree: true, characterData: true });
    // The same question, untouched; the session's title grows long enough to wrap.
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), status: 'waiting', statusSince: t, updatedAt: t, question, title: Array.from({ length: 24 }, () => 'a title long enough to wrap').join(' ') });
  }), question);
  expect(after).toEqual({ moved: true, armed: true });
  await page.waitForTimeout(100);
  expect(await answered(page)).toBe(before);
});

test('a click whose read of where the answers are fails is dropped; a key still answers', async ({ page }) => {
  await openDemo(page);
  await askCodex(page, choices('q-read-fails'));
  const v = await open(page, CODEX);
  await expect(v.locator('.question-slot .question[data-arriving]')).toHaveCount(0);
  const before = await answered(page);
  const press = (detail) => v.locator('.question-slot .choice').first().evaluate((c, detail) => {
    // Where it is can't be read (a broken layout read, say): the guard can't tell it stayed put.
    c.getClientRects = () => { throw new Error('no layout'); };
    c.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail }));
  }, detail);
  await press(1);
  await page.waitForTimeout(100);
  expect(await answered(page)).toBe(before);
  // A key's activation (detail 0) never reads where the answers are: it answers.
  await press(0);
  await expect.poll(() => answered(page)).toBeGreaterThan(before);
});

test('typing in a question doesn\'t hide a move above it in the same moment: the question still takes no click', async ({ page }) => {
  await openDemo(page);
  await askCodex(page, { id: 'q-typing', kind: 'form', message: 'Notes?', askedAt: new Date().toISOString(), fields: [{ name: 'n', title: 'Notes', type: 'text' }] });
  const v = await open(page, CODEX);
  const after = await page.evaluate(async () => {
    const view = document.querySelector('.session-view[data-id="019a1b2c-codex-example"]');
    const box = view.querySelector('.question-slot textarea');
    const send = view.querySelector('.question-slot button.send');
    const top = send.getBoundingClientRect().top;
    // A key typed in the question, and in the same moment the title above grows to wrap.
    box.value = 'a note';
    box.dispatchEvent(new InputEvent('input', { bubbles: true }));
    view.querySelector('.session-title').textContent = Array.from({ length: 24 }, () => 'a title long enough to wrap').join(' ');
    await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
    return { moved: send.getBoundingClientRect().top - top > 1, armed: view.querySelector('.question-slot .question').hasAttribute('data-arriving') };
  });
  expect(after).toEqual({ moved: true, armed: true });
  await expect(v.locator('.question-slot .question')).toHaveCount(1);
});

// In the same task as the first change to what the slot shows after `change` runs (no frame drawn
// between), fires the person's typing in the question's box; then waits two frames and says
// whether Send moved and the question took no click.
const typedAsItChanged = (page, change) => page.evaluate(async (change) => {
  const view = document.querySelector('.session-view[data-id="019a1b2c-codex-example"]');
  const slot = view.querySelector('.question-slot');
  const top = slot.querySelector('button.send').getBoundingClientRect().top;
  await new Promise((resolve) => {
    const seen = new MutationObserver(() => {
      seen.disconnect();
      const box = slot.querySelector('textarea');
      box.value += 'x';
      box.dispatchEvent(new InputEvent('input', { bubbles: true }));
      resolve();
    });
    seen.observe(slot, { childList: true, subtree: true, characterData: true });
    if (change === 'reword') {
      const d = window.agentBridgeDemo;
      const s = d.host.get('019a1b2c-codex-example');
      const message = Array.from({ length: 12 }, () => 'The question, reworded at length so it takes more lines.').join(' ');
      d.upsert({ ...s, question: { ...s.question, message }, updatedAt: new Date(Date.now() + 70_000).toISOString() });
    } else {
      window.agentBridgeDemo.host.releaseResponses();
    }
  });
  await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
  const question = slot.querySelector('.question');
  return { moved: question.querySelector('button.send').getBoundingClientRect().top - top > 1, armed: question.hasAttribute('data-arriving') };
}, change);
const notes = (id) => ({ id, kind: 'form', message: 'Notes?', askedAt: new Date().toISOString(), fields: [{ name: 'n', title: 'Notes', type: 'text' }] });

test('the same question drawn again, longer, as the person types in it still takes no click', async ({ page }) => {
  await openDemo(page);
  await askCodex(page, notes('q-reworded'));
  await open(page, CODEX);
  expect(await typedAsItChanged(page, 'reword')).toEqual({ moved: true, armed: true });
});

test('a receipt above a question whose words grow as the person types below it still holds the question\'s click', async ({ page }) => {
  await openDemo(page, { width: 390, height: 844 });
  await askCodex(page, choices('q-answered'));
  const v = await open(page, CODEX);
  await page.evaluate(() => window.agentBridgeDemo.host.holdResponses());
  await v.locator('.question-slot .choice').first().click();
  await askCodex(page, notes('q-typed-under'), {}, 62_000);
  await expect(v.locator('.answer-receipt')).toHaveCount(1);
  await expect(v.locator('.question-slot .question[data-question="q-typed-under"]')).toHaveCount(1);
  await expect(v.locator('.question-slot .question[data-arriving]')).toHaveCount(0);
  // Its answer comes back: the receipt says more than "Sending…", on more lines.
  expect(await typedAsItChanged(page, 'receipt')).toEqual({ moved: true, armed: true });
});

test('the person\'s own typing laid out as a read owed since a scroll comes is still theirs: the question takes their click', async ({ page }) => {
  await openDemo(page);
  await askCodex(page, { ...notes('q-owed'), fields: [{ name: 'n', title: 'Notes', type: 'text', required: true }] });
  const v = await open(page, CODEX);
  await expect(v.locator('.question-slot .question[data-arriving]')).toHaveCount(0);
  await v.locator('.question-slot button.send').click();
  await expect(v.locator('.question-slot .answer-error')).toBeVisible();
  await expect(v.locator('.question-slot .question[data-arriving]')).toHaveCount(0);
  const after = await page.evaluate(async () => {
    const view = document.querySelector('.session-view[data-id="019a1b2c-codex-example"]');
    const scroller = view.querySelector('.stream-scroll');
    const send = view.querySelector('.question-slot button.send');
    const top = send.getBoundingClientRect().top;
    // A scroll seen as what it scrolls changes size, with nothing resized by the frame: a read
    // is owed two frames on.
    const tall = document.createElement('div');
    tall.style.height = '2000px';
    view.querySelector('.agent-block').append(tall);
    scroller.dispatchEvent(new Event('scroll'));
    tall.remove();
    // Between the next frame and the one the owed read comes in, the person types, and the
    // error under the box goes.
    await new Promise((resolve) => requestAnimationFrame(() => setTimeout(() => {
      const box = view.querySelector('.question-slot textarea');
      box.value = 'a note';
      box.dispatchEvent(new InputEvent('input', { bubbles: true }));
      resolve();
    }, 0)));
    await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(() => requestAnimationFrame(r))));
    return { moved: Math.abs(send.getBoundingClientRect().top - top) > 1, armed: view.querySelector('.question-slot .question').hasAttribute('data-arriving') };
  });
  expect(after).toEqual({ moved: true, armed: false });
});

test('a scroll in the same frame as a title wrapping doesn\'t hide the move: the question still takes no click', async ({ page }) => {
  await openDemo(page, { height: 700 });
  const long = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
  await askCodex(page, choices('q-scrolled'), { activity: { response: long } });
  const v = await open(page, CODEX);
  await v.locator('.question-slot .question').evaluate((q) => q.scrollIntoView({ block: 'end' }));
  await expect(v.locator('.question-slot .question[data-arriving]')).toHaveCount(0);
  const after = await page.evaluate(async () => {
    const view = document.querySelector('.session-view[data-id="019a1b2c-codex-example"]');
    const choice = view.querySelector('.question-slot .choice');
    const top = choice.getBoundingClientRect().top;
    // The conversation scrolls a little, and the title above it wraps, before a frame is drawn.
    view.querySelector('.stream-scroll').scrollTop -= 20;
    view.querySelector('.session-title').textContent = Array.from({ length: 24 }, () => 'a title long enough to wrap').join(' ');
    await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
    return { by: Math.round(choice.getBoundingClientRect().top - top - 20), armed: view.querySelector('.question-slot .question').hasAttribute('data-arriving') };
  });
  expect(after.by).toBeGreaterThan(1);
  expect(after.armed).toBe(true);
});

test('content growing above what is in sight, which the browser keeps in place, holds no click', async ({ page, browserName }) => {
  await openDemo(page, { height: 700 });
  const long = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
  await askCodex(page, choices('q-anchored'), { activity: { response: long, userMessage: 'Short.' } });
  const v = await open(page, CODEX);
  const scroller = v.locator('.stream-scroll');
  // Read to near the end, the question in sight; your message far above, out of sight.
  await scroller.evaluate((el) => { el.scrollTop = el.scrollHeight - el.clientHeight - 100; });
  await atRest(page, scroller);
  await expect(v.locator('.question-slot .choice').first()).toBeInViewport();
  await expect(v.locator('.question-slot .question[data-arriving]')).toHaveCount(0);
  // Any wait at all from here on is seen, though a slow runner lets it run out before the look.
  await v.locator('.question-slot').evaluate((slot) => {
    window.__armed = false;
    new MutationObserver(() => { if (slot.querySelector('.question[data-arriving]')) { window.__armed = true; } }).observe(slot, { subtree: true, attributes: true, attributeFilter: ['data-arriving'] });
  });
  const top = await v.locator('.question-slot .choice').first().evaluate((c) => c.getBoundingClientRect().top);
  // Your message, out of sight above, grows: the browser keeps what's in sight where it is.
  const message = Array.from({ length: 30 }, () => 'A much longer message than before.').join('\n\n');
  await askCodex(page, choices('q-anchored'), { activity: { response: long, userMessage: message } }, 61_000);
  await expect(v.locator('.user-message')).toContainText('A much longer message');
  const after = await page.evaluate(async (top) => {
    await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
    const view = document.querySelector('.session-view[data-id="019a1b2c-codex-example"]');
    return { by: Math.abs(view.querySelector('.question-slot .choice').getBoundingClientRect().top - top), armed: window.__armed };
  }, top);
  // WebKit keeps nothing in place (no scroll anchoring): there the question really moves, and waits.
  if (browserName !== 'webkit') { expect(after.by).toBeLessThanOrEqual(1); }
  expect(after.armed).toBe(after.by > 1);
});

// Scrolls the conversation in the same moment as grow lays out something in it, and says how far
// the first choice moved and whether the question then took no click.
const scrolledAsItGrew = (page, grow) => page.evaluate(async (grow) => {
  const view = document.querySelector('.session-view[data-id="019a1b2c-codex-example"]');
  const scroller = view.querySelector('.stream-scroll');
  const choice = view.querySelector('.question-slot .choice');
  const top = choice.getBoundingClientRect().top;
  const more = (el) => el.append(' More words, growing as they stream in.'.repeat(10));
  if (grow === 'scrolled up') {
    // Scrolled back by 100 px as a line just above what was in sight (now in sight) grows.
    const edge = scroller.getBoundingClientRect().top;
    const line = [...view.querySelectorAll('.response p')].filter((p) => { const r = p.getBoundingClientRect(); return r.bottom <= edge && r.top >= edge - 100; }).at(-1);
    scroller.scrollTop -= 100;
    more(line);
  } else {
    if (grow === 'out of sight') {
      // Your message, far above, grows: the browser keeps what's in sight in place.
      more(view.querySelector('.user-message .bubble'));
    } else if (grow === 'unanchored') {
      // A browser that keeps nothing in place, as WebKit: your message far above grows.
      scroller.style.overflowAnchor = 'none';
      more(view.querySelector('.user-message .bubble'));
    } else {
      // The response's last lines, in sight between the top of the view and the question, grow.
      more([...view.querySelectorAll('.response p')].at(-1));
    }
    scroller.scrollTop += 20;
  }
  await new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
  return { by: Math.round(choice.getBoundingClientRect().top - top), armed: view.querySelector('.question-slot .question').hasAttribute('data-arriving') };
}, grow);

const growing = {
  'out of sight': 'a scroll in the same moment as content out of sight above grows takes the click away for a moment: what the person scrolled can\'t be told from the browser keeping things in place',
  unanchored: 'a scroll in the same moment as content above the question grows where nothing is kept in place still finds the move: the question takes no click',
  'in sight': 'a scroll in the same moment as content above the question grows in sight still finds the move: the question takes no click',
  'scrolled up': 'a scroll back in the same moment as content just above what was in sight grows still finds the move: the question takes no click',
};
for (const [grow, title] of Object.entries(growing)) {
  test(title, async ({ page, browserName }) => {
    await openDemo(page, { height: 700 });
    const long = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
    await askCodex(page, choices('q-grown'), { activity: { response: long, userMessage: 'Short.' } });
    const v = await open(page, CODEX);
    await v.locator('.stream-scroll').evaluate((el) => { el.scrollTop = el.scrollHeight - el.clientHeight - 100; });
    await expect(v.locator('.question-slot .choice').first()).toBeInViewport();
    await expect(v.locator('.question-slot .question[data-arriving]')).toHaveCount(0);
    const after = await scrolledAsItGrew(page, grow);
    if (grow === 'out of sight') {
      // Kept in place, the question moved by the scroll alone (WebKit keeps nothing in place).
      if (browserName !== 'webkit') { expect(after.by).toBe(-20); }
    } else if (grow === 'scrolled up') {
      expect(after.by).toBeGreaterThan(101);
    } else {
      expect(after.by).toBeGreaterThan(1);
    }
    expect(after.armed).toBe(true);
  });
}
test('a slow scroll, a pixel a frame, leaves nothing over: a move of the same size after it still takes the click away', async ({ page }) => {
  await openDemo(page, { height: 700 });
  const long = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
  await askCodex(page, choices('q-slow'), { activity: { response: long, userMessage: 'Short.' } });
  const v = await open(page, CODEX);
  await v.locator('.stream-scroll').evaluate((el) => { el.scrollTop = el.scrollHeight - el.clientHeight - 100; });
  await expect(v.locator('.question-slot .choice').first()).toBeInViewport();
  await expect(v.locator('.question-slot .question[data-arriving]')).toHaveCount(0);
  const after = await page.evaluate(async () => {
    const view = document.querySelector('.session-view[data-id="019a1b2c-codex-example"]');
    const scroller = view.querySelector('.stream-scroll');
    const choice = view.querySelector('.question-slot .choice');
    const frames = () => new Promise((r) => requestAnimationFrame(() => requestAnimationFrame(r)));
    for (let i = 0; i < 6; i += 1) {
      scroller.scrollTop += 1;
      await frames();
    }
    const top = choice.getBoundingClientRect().top;
    // The response's last line, in sight just above the question, grows by as much.
    [...view.querySelectorAll('.response p')].at(-1).style.paddingTop = '6px';
    await frames();
    return { by: Math.round(choice.getBoundingClientRect().top - top), armed: view.querySelector('.question-slot .question').hasAttribute('data-arriving') };
  });
  expect(after).toEqual({ by: 6, armed: true });
});

test('a session not shown reads and watches nothing; shown again, it watches its question', async ({ page }) => {
  await page.addInitScript(() => {
    const watched = window.__watched = new Set();
    const { observe, unobserve, disconnect } = ResizeObserver.prototype;
    const mine = new WeakMap();
    const of = (ro) => { if (!mine.has(ro)) { mine.set(ro, new Set()); } return mine.get(ro); };
    ResizeObserver.prototype.observe = function (el, o) { of(this).add(el); watched.add([this, el]); return observe.call(this, el, o); };
    ResizeObserver.prototype.unobserve = function (el) { of(this).delete(el); return unobserve.call(this, el); };
    ResizeObserver.prototype.disconnect = function () { of(this).clear(); return disconnect.call(this); };
    window.__stillWatched = (el) => [...watched].some(([ro, x]) => x === el && of(ro).has(el));
  });
  await openDemo(page);
  await askCodex(page, choices('q-paused'));
  await open(page, CODEX);
  const watching = () => page.evaluate(() => {
    const view = document.querySelector('.session-view[data-id="019a1b2c-codex-example"]');
    return [view.querySelector('.question-slot'), view.querySelector('.question-slot .question')].map((el) => window.__stillWatched(el));
  });
  expect(await watching()).toEqual([true, true]);
  await open(page, CLAUDE);
  expect(await watching()).toEqual([false, false]);
  await open(page, CODEX);
  expect(await watching()).toEqual([true, true]);
});

test('a window narrowed within one layout moves the question across, and it takes no click for a moment', async ({ page }) => {
  await openDemo(page, { width: 1900, height: 900 });
  await askCodex(page, choices('q-across'));
  const v = await open(page, CODEX);
  await expect(v.locator('.question-slot .question[data-arriving]')).toHaveCount(0);
  const where = () => v.locator('.question-slot .choice').first().evaluate((c) => { const r = c.getBoundingClientRect(); return { top: r.top, left: r.left }; });
  const was = await where();
  await holdClock(page);
  await page.setViewportSize({ width: 1800, height: 900 });
  await expect.poll(async () => {
    await page.clock.runFor(20);
    return v.locator('.question-slot .question').evaluate((q) => q.hasAttribute('data-arriving'));
  }).toBe(true);
  const now = await where();
  // Only across: nothing above it changed height.
  expect(Math.abs(now.top - was.top)).toBeLessThanOrEqual(1);
  expect(was.left - now.left).toBeGreaterThan(1);
});

test('a taller window pulling a conversation at its end back moves the question down, and it takes no click for a moment', async ({ page }) => {
  await openDemo(page, { height: 700 });
  const long = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
  await askCodex(page, choices('q-taller'), { activity: { response: long } });
  const v = await open(page, CODEX);
  await v.locator('.stream-scroll').evaluate((el) => { el.scrollTop = el.scrollHeight; });
  await atRest(page, v.locator('.stream-scroll'));
  await expect(v.locator('.question-slot .question[data-arriving]')).toHaveCount(0);
  const top = await v.locator('.question-slot .choice').first().evaluate((c) => c.getBoundingClientRect().top);
  // The wait can't run out on a slow runner before it is looked at.
  await holdClock(page);
  await page.setViewportSize({ width: 1440, height: 820 });
  await expect.poll(async () => {
    await page.clock.runFor(20);
    return v.locator('.question-slot .question').evaluate((q) => q.hasAttribute('data-arriving'));
  }).toBe(true);
  expect(await v.locator('.question-slot .choice').first().evaluate((c, top) => c.getBoundingClientRect().top - top, top)).toBeGreaterThan(1);
});

test('a session that goes leaves nothing of its view watched by the others', async ({ page }) => {
  await page.addInitScript(() => {
    const watched = window.__watched = new Set();
    const { observe, unobserve, disconnect } = ResizeObserver.prototype;
    const mine = new WeakMap();
    const of = (ro) => { if (!mine.has(ro)) { mine.set(ro, new Set()); } return mine.get(ro); };
    ResizeObserver.prototype.observe = function (el, o) { of(this).add(el); watched.add([this, el]); return observe.call(this, el, o); };
    ResizeObserver.prototype.unobserve = function (el) { of(this).delete(el); return unobserve.call(this, el); };
    ResizeObserver.prototype.disconnect = function () { of(this).clear(); return disconnect.call(this); };
    window.__stillWatched = (el) => [...watched].some(([ro, x]) => x === el && of(ro).has(el));
  });
  await openDemo(page);
  await open(page, CODEX);
  await open(page, CLAUDE);
  await open(page, CODEX);
  // Claude's session goes, its view with it.
  const kept = await page.evaluate(async () => {
    const view = document.querySelector('.session-view[data-id="6f1c2b7e-claude-example"]');
    window.agentBridgeDemo.remove('6f1c2b7e-claude-example');
    for (let i = 0; i < 60 && view.isConnected; i += 1) { await new Promise((r) => requestAnimationFrame(r)); }
    return { gone: !view.isConnected, watched: window.__stillWatched(view) };
  });
  expect(kept).toEqual({ gone: true, watched: false });
});

test('dismissing a kept draft above a question moves the question up, and the second click of a double-click doesn\'t answer it', async ({ page }) => {
  await openDemo(page);
  await askCodex(page, { id: 'q-notes', kind: 'form', message: 'Notes?', askedAt: new Date().toISOString(), fields: [{ name: 'n', title: 'Notes', type: 'text' }] });
  const v = await open(page, CODEX);
  await v.getByRole('textbox', { name: 'Notes' }).fill('half a thought');
  await askCodex(page, choices('q-after-notes'), {}, 62_000);
  await expect(v.locator('.question-changed')).toHaveCount(1);
  await expect(v.locator('.question-slot .question[data-question="q-after-notes"]')).toHaveCount(1);
  await expect(v.locator('.question-slot .question[data-arriving]')).toHaveCount(0);
  const before = await answered(page);
  // Dismiss, and the second click of the same double-click lands where Dismiss was.
  const after = await page.evaluate(() => {
    const slot = document.querySelector('.session-view[data-id="019a1b2c-codex-example"] .question-slot');
    const dismiss = [...slot.querySelectorAll('.question-changed button')].find((b) => b.textContent === 'Dismiss');
    dismiss.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
    const question = slot.querySelector('.question');
    const armed = question.hasAttribute('data-arriving');
    question.querySelector('.choice').dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 2 }));
    return { gone: !slot.querySelector('.question-changed'), armed };
  });
  expect(after).toEqual({ gone: true, armed: true });
  await page.waitForTimeout(100);
  expect(await answered(page)).toBe(before);
});

test('a receipt that goes from above a question that stays moves it up, and a click on its way doesn\'t answer it', async ({ page }) => {
  await openDemo(page);
  await askCodex(page, choices('q-first'));
  const v = await open(page, CODEX);
  // The first answered, its response held back; a second question comes while it is on its way,
  // under the first's receipt.
  await page.evaluate(() => window.agentBridgeDemo.host.holdResponses());
  await v.locator('.question-slot .choice').first().click();
  await askCodex(page, choices('q-second'), {}, 62_000);
  const receipt = v.locator('.answer-receipt');
  await expect(receipt).toHaveCount(1);
  await expect(v.locator('.question-slot .question[data-question="q-second"]')).toHaveCount(1);
  // The first's answer comes back: the receipt has said its last word.
  await page.evaluate(() => window.agentBridgeDemo.host.releaseResponses());
  await expect(receipt).not.toHaveAttribute('data-answer', /sending|waiting|progress/);
  await expect(v.locator('.question-slot .question[data-arriving]')).toHaveCount(0);
  const before = await answered(page);
  // The second, drawn again as it stays (reworded): the receipt goes and the question moves up
  // into its place, as a click is on its way.
  // The click lands as soon as the redraw is in the page, before any frame: the guard the redraw
  // set is looked at then, not at a later frame. That frame can start after the guard's 500 ms
  // have run out on a slow runner, ahead of the same frame's ResizeObserver setting it again (#864).
  const after = await page.evaluate(() => new Promise((resolve) => {
    const slot = document.querySelector('.session-view[data-id="019a1b2c-codex-example"] .question-slot');
    const d = window.agentBridgeDemo;
    const s = d.host.get('019a1b2c-codex-example');
    const seen = new MutationObserver(() => {
      if (slot.querySelector('.answer-receipt')) { return; }
      seen.disconnect();
      const question = slot.querySelector('.question');
      const armed = question.hasAttribute('data-arriving');
      question.querySelector('.choice').dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
      resolve({ gone: true, same: question.dataset.question, armed });
    });
    seen.observe(slot, { childList: true, subtree: true });
    d.upsert({ ...s, question: { ...s.question, message: 'Which, now?' }, updatedAt: new Date(Date.now() + 63_000).toISOString() });
  }));
  expect(after).toEqual({ gone: true, same: 'q-second', armed: true });
  await page.waitForTimeout(100);
  expect(await answered(page)).toBe(before);
});
test('a session the address names at startup, or after a reload, is there at once on a phone: nothing slides in', async ({ page }) => {
  await openDemo(page, { width: 390, height: 844 });
  const host = await row(page, CLAUDE).getAttribute('data-host-id');
  const hash = `#/sessions/${encodeURIComponent(host)}/${encodeURIComponent(CLAUDE)}`;
  // A fresh load with the address, as a link or a bookmark opens it (not a hash change in a
  // running app, which is a move).
  await page.goto('about:blank');
  await page.goto(`?demo=1&static=1${hash}`);
  await page.waitForSelector('html[data-demo-ready]');
  await expect(view(page, CLAUDE)).toBeVisible();
  await page.waitForTimeout(300);
  expect((await motions(page)).filter((m) => m.id === 'ab-motion-push')).toEqual([]);
  await page.reload();
  await page.waitForSelector('html[data-demo-ready]');
  await expect(view(page, CLAUDE)).toBeVisible();
  await page.waitForTimeout(300);
  expect((await motions(page)).filter((m) => m.id === 'ab-motion-push')).toEqual([]);
});

test('a question that arrives while its session is still sliding in on a phone stops the slide: its command only fades', async ({ page }) => {
  await openDemo(page, { width: 390, height: 844 });
  await page.evaluate(() => document.documentElement.style.setProperty('--ab-motion-push-duration', '5000ms'));
  await row(page, CLAUDE).locator('.row-main').click();
  await expect(view(page, CLAUDE)).toBeVisible();
  const travelling = () => page.evaluate(() => document.getAnimations().filter((a) => a.id === 'ab-motion-push' && a.playState === 'running')
    .map((a) => a.effect.getKeyframes().some((k) => k.transform && k.transform !== 'none')));
  expect(await travelling()).toEqual([true]);
  await ask(page, CLAUDE);
  await expect(view(page, CLAUDE).locator('.question-slot .question')).toHaveCount(1);
  expect(await travelling()).toEqual([false]);
});

// Enough sessions that the list scrolls.
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

test('a second press inside the last one\'s release shows its fill at once', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => document.documentElement.style.setProperty('--ab-motion-press-duration', '5000ms'));
  const main = row(page, CODEX).locator('.row-main');
  const b = await main.boundingBox();
  await page.mouse.move(b.x + 20, b.y + b.height / 2);
  await page.mouse.down();
  await page.mouse.up();
  await expect(main).toHaveAttribute('data-press', 'released');
  await page.mouse.down();
  expect(await main.evaluate((el) => [el.dataset.press ?? null, getComputedStyle(el).transitionDuration])).toEqual([null, '0s']);
  await page.mouse.up();
});

test('a press the browser takes over to scroll is not a release: its fill goes at once, with no easing', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => document.documentElement.style.setProperty('--ab-motion-press-duration', '5000ms'));
  const main = row(page, CODEX).locator('.row-main');
  // A finger on the row the browser then takes for a scroll, a pointercancel with no pointerup;
  // and the same after an earlier release still easing.
  const after = await main.evaluate((el) => {
    const send = (type) => el.dispatchEvent(new PointerEvent(type, { bubbles: true, pointerType: 'touch', isPrimary: true }));
    send('pointerdown');
    send('pointercancel');
    const cancelled = el.dataset.press ?? null;
    send('pointerdown');
    send('pointerup');
    const released = el.dataset.press ?? null;
    send('pointerdown');
    send('pointercancel');
    return [cancelled, released, el.dataset.press ?? null];
  });
  expect(after).toEqual([null, 'released', null]);
});

test('a row moved past the edge of the list by Alt+Down ends in full view', async ({ page }) => {
  await openDemo(page, { height: 520 });
  await many(page);
  // The last row wholly in sight, with a row below it out of sight.
  const id = await page.evaluate(() => {
    const rows = [...document.querySelectorAll('#session-list > .session')];
    const visible = (el) => {
      const r = el.getBoundingClientRect();
      for (let p = el.parentElement; p; p = p.parentElement) {
        const cs = getComputedStyle(p);
        if (cs.overflowY === 'visible') { continue; }
        const b = p.getBoundingClientRect();
        if (r.top < b.top || r.bottom > b.top + p.clientHeight) { return false; }
      }
      return r.top >= 0 && r.bottom <= innerHeight;
    };
    const i = rows.findIndex((el, k) => visible(el) && rows[k + 1] && !visible(rows[k + 1]));
    return rows[i]?.dataset.id ?? null;
  });
  expect(id).not.toBeNull();
  await row(page, id).locator('.row-main').press('Alt+ArrowDown');
  await page.waitForFunction(() => !document.getAnimations().some((a) => a.id === 'ab-motion-reorder' && a.playState === 'running'));
  // In view to the pixel's rounding.
  await expect(row(page, id)).toBeInViewport({ ratio: 0.95 });
});

test('the selection pill fades in on the row chosen when the row it left is scrolled out of sight, and slides only when both are in sight', async ({ page }) => {
  await openDemo(page, { height: 520 });
  await many(page);
  const first = page.locator('#session-list > .session').first();
  await first.locator('.row-main').click();
  await expect(first).toHaveClass(/selected/);
  const last = page.locator('#session-list > .session').last();
  await last.scrollIntoViewIfNeeded();
  await expect(first).not.toBeInViewport();
  const before = (await motions(page)).length;
  await last.locator('.row-main').click();
  await expect(last).toHaveClass(/selected/);
  const pill = (await motions(page)).slice(before).filter((m) => m.id === 'ab-motion-selection');
  expect(pill).toHaveLength(1);
  expect(pill[0].travels).toBe(false);
  // Its neighbour, in sight with it: the pill slides.
  const next = page.locator('#session-list > .session').nth(-2);
  const now = (await motions(page)).length;
  await next.locator('.row-main').click();
  const slid = (await motions(page)).slice(now).filter((m) => m.id === 'ab-motion-selection');
  expect(slid).toHaveLength(1);
  expect(slid[0].travels).toBe(true);
});
test('choosing an answer shows its new colours at once: a choice\'s release never eases', async ({ page }) => {
  await openDemo(page);
  const v = await open(page, COPILOT);
  await page.evaluate((id) => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-choice-motion', kind: 'form', message: 'Which?', askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    d.upsert({ ...d.host.get(id), status: 'waiting', statusSince: t, updatedAt: t, question });
  }, COPILOT);
  const choice = v.locator('.question-slot .choice').first();
  await expect(choice).toBeVisible();
  const b = await choice.boundingBox();
  await page.mouse.move(b.x + b.width / 2, b.y + b.height / 2);
  await page.mouse.down();
  await page.mouse.up();
  expect(await choice.evaluate((el) => [el.dataset.press ?? null, getComputedStyle(el).transitionDuration])).toEqual([null, '0s']);
});

test('a second Alt+Down while the first move still slides goes on from where the row is seen', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => document.documentElement.style.setProperty('--ab-motion-reorder-duration', '5000ms'));
  const main = row(page, CLAUDE).locator('.row-main');
  await main.press('Alt+ArrowDown');
  await page.waitForTimeout(1500);
  const [seenBefore, seenAfter] = await main.evaluate((el) => {
    const card = el.closest('.session');
    const top = () => card.getBoundingClientRect().top;
    const a = top();
    el.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', altKey: true, bubbles: true, cancelable: true }));
    return [a, top()];
  });
  expect(Math.abs(seenAfter - seenBefore)).toBeLessThan(3);
});
test('a question that arrives while a mouse rests on the conversation waits: the view never moves under it', async ({ page }) => {
  await openDemo(page, { height: 600 });
  const long = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
  await upsert(page, CODEX, { activity: { response: long }, updatedAt: later() });
  const v = await open(page, CODEX);
  const scroller = v.locator('.stream-scroll');
  await scroller.evaluate((el) => { el.scrollTop = el.scrollHeight; });
  const top = await scroller.evaluate((el) => el.scrollTop);
  // Reading, the pointer over the agent's last lines, about to click.
  const b = await scroller.boundingBox();
  await page.mouse.move(b.x + b.width / 2, b.y + b.height - 40);
  await page.evaluate((from) => {
    const d = window.agentBridgeDemo;
    const question = { ...d.host.get(from).question, id: 'q-hovered' };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), question, status: 'waiting', updatedAt: new Date(Date.now() + 2000).toISOString() });
  }, COPILOT);
  await expect(v.locator('.question-slot .question')).toHaveCount(1);
  await page.waitForTimeout(400);
  expect(await scroller.evaluate((el) => el.scrollTop)).toBe(top);
  await expect(v.locator('.question-slot .question')).not.toBeInViewport();
});

test('a click that lands on a question\'s choice as the question appears is ignored; a moment later it answers, and a key answers at once', async ({ page }) => {
  await openDemo(page);
  const v = await open(page, CODEX);
  const answers = () => page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
  const before = await answers();
  // The question and a click already on its way, in one go: the click lands the moment the
  // question's first choice is there.
  const clicked = await page.evaluate(async () => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-guard-1', kind: 'form', message: 'Which?', askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), status: 'waiting', statusSince: t, updatedAt: t, question });
    let choice = null;
    for (let i = 0; i < 50 && !choice; i += 1) {
      await new Promise((r) => setTimeout(r, 0));
      choice = document.querySelector('.session-view[data-id="019a1b2c-codex-example"] .question-slot .choice');
    }
    if (!choice) { return false; }
    choice.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
    return true;
  });
  expect(clicked).toBe(true);
  await page.waitForTimeout(100);
  expect(await answers()).toBe(before);
  // Past the guard, the same click answers.
  await page.waitForTimeout(600);
  await v.locator('.question-slot .choice').first().click();
  await expect.poll(answers).toBe(before + 1);
  // A key needs no wait: nothing moves the focus to a question, so a key is meant for it.
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 120_000).toISOString();
    const question = {
      id: 'q-guard-2', kind: 'form', message: 'And?', askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'Third' }, { value: 'b', label: 'Fourth' }] }],
    };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), status: 'waiting', statusSince: t, updatedAt: t, question });
  });
  const third = v.locator('.question-slot .choice', { hasText: 'Third' });
  await third.focus();
  await page.keyboard.press('Enter');
  await expect.poll(answers).toBe(before + 2);
});
test('a question already there takes no click for a moment when its session is opened on a phone, or brought into sight from Needs you', async ({ page }) => {
  await openDemo(page, { width: 390, height: 844 });
  const answers = () => page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
  const before = await answers();
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-guard-open', kind: 'form', message: 'Which?', askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), status: 'waiting', statusSince: t, updatedAt: t, question });
  });
  await expect(page.locator('#needs-you .needs-entry[data-id="019a1b2c-codex-example"]')).toBeVisible();
  // The tap that opens it, and a second tap where its first choice now is.
  const tapThrough = (opener) => page.evaluate(async (opener) => {
    const tap = (el) => el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
    tap(document.querySelector(opener));
    await new Promise((r) => setTimeout(r, 0));
    const choice = document.querySelector('.session-view[data-id="019a1b2c-codex-example"] .question-slot .choice');
    if (!choice) { return false; }
    tap(choice);
    return true;
  }, opener);
  expect(await tapThrough('.session[data-id="019a1b2c-codex-example"] .row-main')).toBe(true);
  await page.waitForTimeout(100);
  expect(await answers()).toBe(before);
  await view(page, CODEX).locator('.session-header .back').click();
  await expect(row(page, CODEX)).toBeVisible();
  expect(await tapThrough('#needs-you .needs-entry[data-id="019a1b2c-codex-example"] .needs-title')).toBe(true);
  await page.waitForTimeout(100);
  expect(await answers()).toBe(before);
});
test('a question scrolled into sight from Needs you in a session already open takes no click for a moment', async ({ page }) => {
  await openDemo(page, { height: 600 });
  const long = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
  await page.evaluate((long) => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-guard-reveal', kind: 'form', message: 'Which?', askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    const s = d.host.get('019a1b2c-codex-example');
    d.upsert({ ...s, activity: { ...s.activity, response: long }, status: 'waiting', statusSince: t, updatedAt: t, question });
  }, long);
  const v = await open(page, CODEX);
  await v.locator('.stream-scroll').evaluate((el) => { el.scrollTop = 0; });
  await expect(v.locator('.question-slot .question')).not.toBeInViewport();
  const answers = () => page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
  const before = await answers();
  const tapped = await page.evaluate(async () => {
    const tap = (el) => el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
    tap(document.querySelector('#needs-you .needs-entry[data-id="019a1b2c-codex-example"] .needs-title'));
    await new Promise((r) => setTimeout(r, 0));
    const choice = document.querySelector('.session-view[data-id="019a1b2c-codex-example"] .question-slot .choice');
    if (!choice) { return false; }
    tap(choice);
    return true;
  });
  expect(tapped).toBe(true);
  await page.waitForTimeout(100);
  expect(await answers()).toBe(before);
});
test('a question that came while another tab was shown takes no click for a moment when Conversation is chosen again', async ({ page }) => {
  await openDemo(page, { width: 1000 });
  const v = await open(page, CODEX);
  await v.getByRole('tab', { name: 'Tools' }).click();
  await expect(v.locator('.conversation')).toBeHidden();
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-guard-tab', kind: 'form', message: 'Which?', askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), status: 'waiting', statusSince: t, updatedAt: t, question });
  });
  await expect(v.locator('.question-slot .question')).toHaveCount(1);
  await page.waitForTimeout(600);
  const answers = () => page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
  const before = await answers();
  const tapped = await page.evaluate(() => {
    const tap = (el) => el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
    const view = document.querySelector('.session-view[data-id="019a1b2c-codex-example"]');
    tap([...view.querySelectorAll('[role="tab"]')].find((t) => t.textContent.trim() === 'Conversation'));
    const choice = view.querySelector('.question-slot .choice');
    if (!choice || !choice.getClientRects().length) { return false; }
    tap(choice);
    return true;
  });
  expect(tapped).toBe(true);
  await page.waitForTimeout(100);
  expect(await answers()).toBe(before);
});

test('bringing a question already in sight into view starts no wait for a later scroll: a click after the person scrolls still counts', async ({ page }) => {
  await openDemo(page, { height: 900 });
  const long = Array.from({ length: 60 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
  await page.evaluate((long) => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-guard-inview', kind: 'form', message: 'Which?', askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    const s = d.host.get('019a1b2c-codex-example');
    d.upsert({ ...s, activity: { ...s.activity, response: long }, status: 'waiting', statusSince: t, updatedAt: t, question });
  }, long);
  const v = await open(page, CODEX);
  const scroller = v.locator('.stream-scroll');
  // The whole question in sight, so bringing it into view has nowhere to go.
  await v.locator('.question-slot .question').evaluate((el) => el.scrollIntoView({ block: 'nearest' }));
  await expect(v.locator('.question-slot .question')).toBeInViewport({ ratio: 0.99 });
  const answers = () => page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
  const before = await answers();
  // In one go, so the page's own clock times it: from Needs you, with the question in sight
  // already (nothing scrolls); past the guard, the person scrolls a little, and clicks a choice
  // once that scroll has ended.
  const arriving = await page.evaluate(async (id) => {
    const tap = (el) => el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
    const view = document.querySelector(`.session-view[data-id="${id}"]`);
    const el = view.querySelector('.stream-scroll');
    tap(document.querySelector(`#needs-you .needs-entry[data-id="${id}"] .needs-title`));
    await new Promise((r) => setTimeout(r, 600));
    const ended = new Promise((r) => el.addEventListener('scrollend', r, { once: true }));
    el.scrollTop -= 20;
    await Promise.race([ended, new Promise((r) => setTimeout(r, 500))]);
    const question = view.querySelector('.question-slot .question');
    const was = question.hasAttribute('data-arriving');
    tap(question.querySelector('.choice'));
    return was;
  }, CODEX);
  expect(arriving).toBe(false);
  await expect.poll(answers).toBe(before + 1);
});
test('where the browser sends no scrollend, as Safari doesn\'t, a question scrolled into sight takes no click until the scroll comes to rest, and the person\'s own scroll after that holds none back', async ({ page }) => {
  await page.addInitScript(() => {
    const add = EventTarget.prototype.addEventListener;
    EventTarget.prototype.addEventListener = function (type, ...rest) { return type === 'scrollend' ? undefined : add.call(this, type, ...rest); };
  });
  await openDemo(page, { height: 600 });
  const long = Array.from({ length: 200 }, (_, i) => `Line ${i + 1} of a long answer.`).join('\n\n');
  await page.evaluate((long) => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-guard-noend', kind: 'form', message: 'Which?', askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    const s = d.host.get('019a1b2c-codex-example');
    d.upsert({ ...s, activity: { ...s.activity, response: long }, status: 'waiting', statusSince: t, updatedAt: t, question });
  }, long);
  const v = await open(page, CODEX);
  await v.locator('.stream-scroll').evaluate((el) => { el.scrollTop = 0; });
  await expect(v.locator('.question-slot .question')).not.toBeInViewport();
  // From Needs you, with the scroll's steps sent by the test, as Safari's smooth scroll sends
  // them, and nothing else scrolling: the browser's own scroll doesn't move (a smooth one in
  // headed or CI Chromium would send steps of its own), and the page's clock is the test's, so a
  // slow runner can't stretch a step past the scroll's rest. Steps 30 ms apart over 450 ms; 300 ms
  // after the last, past 500 ms from the start but within 500 ms of where it came to rest, the
  // question still holds its clicks back. Once that has run out, a scroll the person makes (well
  // inside the 1.5 s the listening may last) starts no new wait.
  await holdClock(page);
  const scroller = `.session-view[data-id="${CODEX}"] .stream-scroll`;
  const question = () => v.locator('.question-slot .question').evaluate((q) => q.hasAttribute('data-arriving'));
  const step = () => page.evaluate((s) => document.querySelector(s).dispatchEvent(new Event('scroll')), scroller);
  await page.evaluate((id) => {
    Element.prototype.scrollIntoView = function () {};
    document.querySelector(`#needs-you .needs-entry[data-id="${id}"] .needs-title`).dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
  }, CODEX);
  for (let i = 0; i < 15; i += 1) {
    await page.clock.runFor(30);
    await step();
  }
  await page.clock.runFor(300);
  const held = await question();
  await page.clock.runFor(300);
  const after = await question();
  await step();
  await page.clock.runFor(50);
  expect({ held, after, mine: await question() }).toEqual({ held: true, after: false, mine: false });
});
test('a question that arrives while another page is shown takes no click for a moment when Sessions is shown again', async ({ page }) => {
  await openDemo(page);
  await open(page, CODEX);
  await go(page, 'settings');
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-guard-route', kind: 'form', message: 'Which?', askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), status: 'waiting', statusSince: t, updatedAt: t, question });
  });
  await page.waitForTimeout(600);
  const answers = () => page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
  const before = await answers();
  // Back to Sessions and a click where the question's first choice now is, in one go.
  const tapped = await page.evaluate(() => {
    const tap = (el) => el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
    tap(document.querySelector('#nav [data-nav="sessions"]'));
    const choice = document.querySelector('.session-view[data-id="019a1b2c-codex-example"] .question-slot .choice');
    if (!choice || !choice.getClientRects().length) { return false; }
    tap(choice);
    return true;
  });
  expect(tapped).toBe(true);
  await page.waitForTimeout(100);
  expect(await answers()).toBe(before);
});
test('a question that arrives while another page is shown takes no click when Sessions comes back, though Tools was picked before the inspector was shown beside the conversation', async ({ page }) => {
  await openDemo(page);
  const v = await open(page, CODEX);
  // Tools picked as a tab, then the inspector shown: Tools moves into it, and the conversation
  // is on screen again though Tools is still the tab picked.
  await v.getByRole('button', { name: 'Hide inspector' }).click();
  await v.getByRole('tab', { name: 'Tools' }).click();
  await v.getByRole('button', { name: 'Show inspector' }).click();
  await expect(v.locator('.stream-scroll')).toBeVisible();
  await go(page, 'settings');
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-guard-inspector', kind: 'form', message: 'Which?', askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), status: 'waiting', statusSince: t, updatedAt: t, question });
  });
  await page.waitForTimeout(600);
  const answers = () => page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
  const before = await answers();
  const tapped = await page.evaluate(() => {
    const tap = (el) => el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
    tap(document.querySelector('#nav [data-nav="sessions"]'));
    const choice = document.querySelector('.session-view[data-id="019a1b2c-codex-example"] .question-slot .choice');
    if (!choice || !choice.getClientRects().length) { return false; }
    tap(choice);
    return true;
  });
  expect(tapped).toBe(true);
  await page.waitForTimeout(100);
  expect(await answers()).toBe(before);
});
test('a question that came while Tools was shown takes no click for a moment when Show inspector brings the conversation back', async ({ page }) => {
  await openDemo(page);
  const v = await open(page, CODEX);
  await v.getByRole('button', { name: 'Hide inspector' }).click();
  await v.getByRole('tab', { name: 'Tools' }).click();
  await expect(v.locator('.stream-scroll')).toBeHidden();
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-guard-show-inspector', kind: 'form', message: 'Which?', askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), status: 'waiting', statusSince: t, updatedAt: t, question });
  });
  await expect(v.locator('.question-slot .question')).toHaveCount(1);
  await page.waitForTimeout(600);
  const answers = () => page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
  const before = await answers();
  // Show inspector, and a click where the question's first choice now is, in one go.
  const tapped = await page.evaluate(() => {
    const tap = (el) => el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
    const view = document.querySelector('.session-view[data-id="019a1b2c-codex-example"]');
    tap([...view.querySelectorAll('button')].find((b) => b.getAttribute('aria-label') === 'Show inspector'));
    const choice = view.querySelector('.question-slot .choice');
    if (!choice || !choice.getClientRects().length) { return false; }
    tap(choice);
    return true;
  });
  expect(tapped).toBe(true);
  await page.waitForTimeout(100);
  expect(await answers()).toBe(before);
});
test('a question that came while Tools was a tab takes no click for a moment when a wider window brings the conversation back beside the inspector', async ({ page }) => {
  await openDemo(page, { width: 1000 });
  const v = await open(page, CODEX);
  await v.getByRole('tab', { name: 'Tools' }).click();
  await expect(v.locator('.stream-scroll')).toBeHidden();
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-guard-wider', kind: 'form', message: 'Which?', askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), status: 'waiting', statusSince: t, updatedAt: t, question });
  });
  await expect(v.locator('.question-slot .question')).toHaveCount(1);
  await page.waitForTimeout(600);
  const answers = () => page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
  const before = await answers();
  await page.setViewportSize({ width: 1440, height: 900 });
  // A click where the question's first choice now is, on the first frame drawn at the new width
  // (what a person could see and aim at; the width's change is told before that frame).
  const tapped = await page.evaluate(async () => {
    const view = document.querySelector('.session-view[data-id="019a1b2c-codex-example"]');
    let choice = null;
    for (let i = 0; i < 100 && !choice; i += 1) {
      await new Promise((r) => requestAnimationFrame(r));
      const c = view.querySelector('.question-slot .choice');
      if (c?.getClientRects().length) { choice = c; }
    }
    if (!choice) { return false; }
    choice.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
    return true;
  });
  expect(tapped).toBe(true);
  await page.waitForTimeout(100);
  expect(await answers()).toBe(before);
});
test('a question that arrives while the page is in the background takes no click for a moment when the page is shown again', async ({ page }) => {
  await openDemo(page);
  await open(page, CODEX);
  const visibility = (state) => page.evaluate((state) => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => state });
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => state === 'hidden' });
    document.dispatchEvent(new Event('visibilitychange'));
  }, state);
  await visibility('hidden');
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-guard-hidden', kind: 'form', message: 'Which?', askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), status: 'waiting', statusSince: t, updatedAt: t, question });
  });
  await expect(view(page, CODEX).locator('.question-slot .choice').first()).toBeAttached();
  // Long past a wait spent while nobody could see it.
  await page.waitForTimeout(600);
  const answers = () => page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
  const before = await answers();
  // Shown again, and a click where the question's first choice is, in one go.
  const tapped = await page.evaluate(() => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'visible' });
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => false });
    document.dispatchEvent(new Event('visibilitychange'));
    const choice = document.querySelector('.session-view[data-id="019a1b2c-codex-example"] .question-slot .choice');
    if (!choice || !choice.getClientRects().length) { return false; }
    choice.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
    return true;
  });
  expect(tapped).toBe(true);
  await page.waitForTimeout(100);
  expect(await answers()).toBe(before);
});
test('a question that arrives while another window has the focus takes no click for a moment when this one has it again, though the page stayed visible', async ({ page }) => {
  await openDemo(page);
  await open(page, CODEX);
  // Another window over a browser still partly in sight: focus goes, visibility doesn't.
  await page.evaluate(() => window.dispatchEvent(new FocusEvent('blur')));
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-guard-blur', kind: 'form', message: 'Which?', askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), status: 'waiting', statusSince: t, updatedAt: t, question });
  });
  await expect(view(page, CODEX).locator('.question-slot .choice').first()).toBeAttached();
  // Long past the wait, spent while this window didn't have the focus.
  await page.waitForTimeout(600);
  const answers = () => page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
  const before = await answers();
  // The click that gives this window the focus lands where the question's first choice is.
  const tapped = await page.evaluate(() => {
    const visible = document.visibilityState === 'visible';
    window.dispatchEvent(new FocusEvent('focus'));
    const lane = document.querySelector('#needs-you .needs-you-list').hasAttribute('data-arriving');
    const choice = document.querySelector('.session-view[data-id="019a1b2c-codex-example"] .question-slot .choice');
    if (!choice || !choice.getClientRects().length) { return null; }
    choice.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
    return { visible, lane };
  });
  expect(tapped).toEqual({ visible: true, lane: true });
  await page.waitForTimeout(100);
  expect(await answers()).toBe(before);
});
test('a question held while its machine sends its list again takes no click for a moment once it can be answered', async ({ page }) => {
  await openDemo(page);
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-guard-held', kind: 'form', message: 'Which?', askedAt: t,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), status: 'waiting', statusSince: t, updatedAt: t, question });
  });
  const v = await open(page, CODEX);
  // Away, and back with its list held: the question stays, held, as the same question (#269).
  await page.evaluate(() => { const d = window.agentBridgeDemo; d.host.holdSnapshot(); d.setOnline('demo', false); });
  await expect(v).toHaveAttribute('data-stale', 'offline');
  await page.evaluate(() => window.agentBridgeDemo.setOnline('demo', true));
  await expect(v).toHaveAttribute('data-stale', 'confirming');
  await expect(v.locator('.question-slot .question')).toHaveAttribute('data-held', 'true');
  await page.waitForTimeout(600);
  const answers = () => page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
  const before = await answers();
  // The list whole again, and a click on the first choice the moment it can be answered.
  const clicked = await page.evaluate(async () => {
    window.agentBridgeDemo.host.releaseSnapshot();
    const view = document.querySelector('.session-view[data-id="019a1b2c-codex-example"]');
    let choice = null;
    for (let i = 0; i < 500 && !choice; i += 1) {
      await new Promise((r) => setTimeout(r, 0));
      choice = view.querySelector('.question-slot .question:not([data-held="true"]) .choice:not([disabled])');
    }
    if (!choice) { return false; }
    choice.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
    return true;
  });
  expect(clicked).toBe(true);
  await page.waitForTimeout(100);
  expect(await answers()).toBe(before);
});
test('a question shown to be answered at the machine takes no click for a moment once the same question can be answered here', async ({ page }) => {
  await openDemo(page);
  const ask = (truncated) => page.evaluate((truncated) => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const question = {
      id: 'q-guard-opened', kind: 'form', message: 'Which?', askedAt: t, truncated,
      fields: [{ name: 'answer', type: 'choice', required: true, options: [{ value: 'a', label: 'First' }, { value: 'b', label: 'Second' }] }],
    };
    d.upsert({ ...d.host.get('019a1b2c-codex-example'), status: 'waiting', statusSince: t, updatedAt: t, question });
  }, truncated);
  // Cut short on its way here, so it is answered at the machine; then sent whole, the same question.
  await ask(true);
  const v = await open(page, CODEX);
  await expect(v.locator('.question-slot .question')).not.toHaveAttribute('data-answerable', 'true');
  await page.waitForTimeout(600);
  const answers = () => page.evaluate(() => window.agentBridgeDemo.host.answerLog.length);
  const before = await answers();
  // Sent whole, and a click the moment its first choice can take one, in one go.
  const clicked = await page.evaluate(async () => {
    const d = window.agentBridgeDemo;
    const t = new Date(Date.now() + 60_000).toISOString();
    const s = d.host.get('019a1b2c-codex-example');
    d.upsert({ ...s, updatedAt: t, question: { ...s.question, truncated: false } });
    const view = document.querySelector('.session-view[data-id="019a1b2c-codex-example"]');
    let choice = null;
    for (let i = 0; i < 500 && !choice; i += 1) {
      choice = view.querySelector('.question-slot .question[data-answerable="true"] .choice:not([disabled])');
      if (!choice) { await new Promise((r) => setTimeout(r, 0)); }
    }
    if (!choice) { return false; }
    choice.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, detail: 1 }));
    return true;
  });
  expect(clicked).toBe(true);
  await page.waitForTimeout(100);
  expect(await answers()).toBe(before);
});
test('a session that goes while its machine is still sending its list leaves at once, with no copy fading', async ({ page }) => {
  await openDemo(page);
  await upsert(page, 'demo-unsettled', { agent: 'copilot', title: 'Goes while unsettled', status: 'idle', startedAt: later(), updatedAt: later() });
  await expect(row(page, 'demo-unsettled')).toHaveCount(1);
  const before = (await motions(page)).filter((m) => m.id === 'ab-motion-listItem').length;
  await page.evaluate(() => {
    const d = window.agentBridgeDemo;
    d.client.synced = false;
    d.remove('demo-unsettled');
  });
  await expect(row(page, 'demo-unsettled')).toHaveCount(0);
  expect((await motions(page)).filter((m) => m.id === 'ab-motion-listItem')).toHaveLength(before);
  await page.evaluate(() => { window.agentBridgeDemo.client.synced = true; });
});

test('a copy of a row that leaves draws its words where they were, in their own colour and font', async ({ page }) => {
  await openDemo(page);
  const r = await page.evaluate(async () => {
    const { leave } = await import('./js/ui/motion.js');
    const texts = (root) => {
      const out = [];
      const w = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
      for (let n = w.nextNode(); n; n = w.nextNode()) {
        const range = document.createRange();
        range.selectNodeContents(n);
        const b = range.getBoundingClientRect();
        if (!b.width || !n.data.trim()) { continue; }
        const cs = getComputedStyle(n.parentElement);
        out.push({ text: n.data, x: Math.round(b.left), y: Math.round(b.top), w: Math.round(b.width), color: cs.color, font: cs.font });
      }
      return out;
    };
    const el = document.querySelector('.session');
    const before = texts(el);
    leave(el, 'listItem').pause();
    return { before, copy: texts(window.__shadows.at(-1)) };
  });
  expect(r.before.length).toBeGreaterThan(1);
  expect(r.copy).toEqual(r.before);
});

test('search hides rows without fading copies of them, and a session that ends fades once, not again as the list updates', async ({ page }) => {
  await openDemo(page);
  await upsert(page, 'demo-hidden', { agent: 'copilot', title: 'Zebra quartz', status: 'idle', startedAt: later(), updatedAt: later() });
  await expect(row(page, 'demo-hidden')).toHaveCount(1);
  const fades = async () => (await motions(page)).filter((m) => m.id === 'ab-motion-listItem').length;
  const before = await fades();
  const others = await page.locator('.session').filter({ visible: true }).count();
  await page.keyboard.press('Control+k');
  await page.locator('#search').fill('zebra quartz');
  await expect(page.locator('.session').filter({ visible: true })).toHaveCount(1);
  await page.locator('#search').fill('');
  await expect(page.locator('.session').filter({ visible: true })).toHaveCount(others);
  expect(await fades()).toBe(before);
  // Gone, it is listed under Ended, which has no card of its own to fade.
  await page.evaluate(() => window.agentBridgeDemo.remove('demo-hidden'));
  await expect(row(page, 'demo-hidden')).toHaveCount(0);
  await expect(page.locator('.ended[data-id="demo-hidden"]')).toHaveCount(1);
  expect(await fades()).toBe(before + 1);
  await upsert(page, CODEX, { updatedAt: later(2000) });
  await upsert(page, CODEX, { updatedAt: later(3000) });
  await page.waitForTimeout(100);
  expect(await fades()).toBe(before + 1);
});