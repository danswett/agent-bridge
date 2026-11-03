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
    await new Promise((r) => setTimeout(r, 300));
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