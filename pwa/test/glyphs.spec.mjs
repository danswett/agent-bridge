// The status glyphs (docs/design/ui.md §2.1, design/README.md, #178): drawn from the generated
// sprite at every size in light, dark and forced colours, told apart in greyscale, still under
// reduced motion, and at the head of every card's status line at a phone's, a tablet's and a
// desktop's width.
import { test, expect } from '@playwright/test';
import { appendFileSync } from 'node:fs';
import { open, view } from './nav.mjs';

const GLYPHS = ['idle', 'working', 'delegating', 'needs', 'done', 'failed', 'ended'];
const SIZES = [12, 14, 16, 20, 24];
const CLAUDE = '6f1c2b7e-claude-example';

async function openDemo(page) {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
}

// The app's own glyph module draws them, on a card, as a card holds them. Each row is one glyph
// at the five sizes, then driven at the largest.
async function gallery(page, names = GLYPHS, { sizes = SIZES, driven = true } = {}) {
  await page.evaluate(async ({ names, sizes, driven }) => {
    const { glyph } = await import('./js/ui/glyph.js');
    document.getElementById('glyph-gallery')?.remove();
    const box = document.createElement('div');
    box.id = 'glyph-gallery';
    // On the card surface, spaced as the old card's status line spaced them, so the snapshots
    // are of the glyphs alone and not of whichever layout holds them (#179 replaced the card).
    Object.assign(box.style, { background: 'var(--ab-card)', padding: '4px 14px 12px' });
    for (const name of names) {
      const row = document.createElement('p');
      row.dataset.row = name;
      // Tight around its glyphs, so a snapshot's tolerance is a share of the glyphs.
      Object.assign(row.style, { display: 'inline-flex', gap: '6px', padding: '4px', alignItems: 'center', margin: '2px 0', fontSize: '.9rem' });
      const add = (g) => { g.style.marginRight = '7px'; row.append(g); };
      for (const size of sizes) { add(glyph(name, { size, label: `${name} ${size}` })); }
      if (driven) { add(glyph(name, { size: 24, driven: true, label: `${name} driven` })); }
      box.append(row);
    }
    document.body.prepend(box);
  }, { names, sizes, driven });
  // Every <use> has drawn: the sprite is fetched once and shared.
  await page.waitForFunction(() => [...document.querySelectorAll('#glyph-gallery use')].every((u) => u.getBBox().width > 0));
}

// Pixels of a screenshot, read in a blank page: the app's CSP rightly refuses data: images to
// its own scripts. Returns { width, height, lum } with relative luminance per pixel (0-1).
async function luminance(context, png) {
  const blank = await context.newPage();
  const out = await blank.evaluate(async (b64) => {
    const img = new Image();
    img.src = `data:image/png;base64,${b64}`;
    await img.decode();
    const c = document.createElement('canvas');
    [c.width, c.height] = [img.width, img.height];
    const x = c.getContext('2d');
    x.drawImage(img, 0, 0);
    const d = x.getImageData(0, 0, img.width, img.height).data;
    const lin = (v) => { const s = v / 255; return s <= 0.04045 ? s / 12.92 : ((s + 0.055) / 1.055) ** 2.4; };
    const lum = [];
    for (let i = 0; i < d.length; i += 4) { lum.push(0.2126 * lin(d[i]) + 0.7152 * lin(d[i + 1]) + 0.0722 * lin(d[i + 2])); }
    return { width: img.width, height: img.height, lum };
  }, png.toString('base64'));
  await blank.close();
  return out;
}

const THEMES = [
  { name: 'light', media: { colorScheme: 'light' } },
  { name: 'dark', media: { colorScheme: 'dark' } },
  { name: 'forced-colors', media: { colorScheme: 'light', forcedColors: 'active' }, chromiumOnly: true },
];

for (const theme of THEMES) {
  test.describe(`in ${theme.name}`, () => {
    test.use({ reducedMotion: 'reduce' });

    for (const name of GLYPHS) {
      test(`the ${name} glyph matches its snapshot at every size, and with the driven badge`, async ({ page, browserName }) => {
        test.skip(theme.chromiumOnly && browserName !== 'chromium', 'forced colours are emulated in Chromium only');
        // One engine's pictures: WebKit's anti-aliasing differs between platforms. Both engines
        // draw the glyphs in the tests below.
        test.skip(browserName !== 'chromium', 'snapshots are Chromium\'s');
        await page.emulateMedia(theme.media);
        await openDemo(page);
        await gallery(page, [name]);
        await expect(page.locator(`#glyph-gallery [data-row="${name}"]`)).toHaveScreenshot(`${name}-${theme.name}.png`, { maxDiffPixelRatio: 0.02 });
      });
    }

    test('every glyph is drawn in its role colour, from the sprite, with nothing of its own', async ({ page, browserName }) => {
      test.skip(theme.chromiumOnly && browserName !== 'chromium', 'forced colours are emulated in Chromium only');
      await page.emulateMedia(theme.media);
      await openDemo(page);
      await gallery(page);
      const drawn = await page.evaluate(() => {
        const probe = (v) => {
          const i = document.createElement('i');
          i.style.forcedColorAdjust = 'none';
          i.style.color = `var(--ab-${v})`;
          document.body.append(i);
          const c = getComputedStyle(i).color;
          i.remove();
          return c;
        };
        const roles = Object.fromEntries(['needs', 'active', 'agent', 'danger', 'neutral'].map((r) => [r, probe(r)]));
        const rows = Object.fromEntries([...document.querySelectorAll('#glyph-gallery [data-row]')].map((row) => {
          const [plain] = row.querySelectorAll('.glyph:not(.driven)');
          const driven = row.querySelector('.glyph.driven');
          return [row.dataset.row, {
            colour: getComputedStyle(plain).color,
            drivenColour: getComputedStyle(driven).color,
            badge: getComputedStyle(driven.querySelector('.glyph-badge')).color,
            refs: [...row.querySelectorAll('use')].map((u) => u.getAttribute('href').replace(/^.*#/, '')),
            paths: row.querySelectorAll('path, circle, rect').length,
          }];
        }));
        return { roles, rows };
      });
      const { roles, rows } = drawn;
      const expected = { idle: 'neutral', working: 'active', delegating: 'active', needs: 'needs', done: 'neutral', failed: 'danger', ended: 'neutral' };
      for (const [name, r] of Object.entries(expected)) {
        expect(rows[name].colour, name).toBe(roles[r]);
        // Driven: the active colour turns to agent; every other role stays.
        expect(rows[name].drivenColour, `${name} driven`).toBe(roles[r === 'active' ? 'agent' : r]);
        expect(rows[name].badge, `${name}'s badge`).toBe(roles.agent);
        // Only the generated sprite's symbols: no shape of the client's own.
        expect(rows[name].paths, name).toBe(0);
        expect(rows[name].refs.every((ref) => ref.startsWith(`glyph-${name}`) || ref === 'glyph-driven-badge'), name).toBe(true);
      }
      expect(await page.evaluate(() => window.__cspViolations)).toEqual([]);
    });
  });
}

test.describe('in greyscale', () => {
  test.use({ reducedMotion: 'reduce', deviceScaleFactor: 4 });
  for (const scheme of ['light', 'dark']) {
    test(`every pair of glyphs is told apart by shape alone, in ${scheme}`, async ({ page, context }) => {
      await page.emulateMedia({ colorScheme: scheme });
      await openDemo(page);
      await gallery(page, GLYPHS, { sizes: [16], driven: false });
      const shots = {};
      for (const name of GLYPHS) { shots[name] = await luminance(context, await page.locator(`#glyph-gallery [data-row="${name}"] .glyph`).screenshot()); }
      // The share of pixels whose grey differs clearly between the two: a glyph is told from
      // another by its shape, not by a shade, so a few pixels of anti-aliasing never count.
      const apart = (a, b) => a.lum.filter((v, i) => Math.abs(v - b.lum[i]) > 0.15).length / a.lum.length;
      const pairs = [];
      for (let i = 0; i < GLYPHS.length; i++) {
        for (let j = i + 1; j < GLYPHS.length; j++) { pairs.push([GLYPHS[i], GLYPHS[j], apart(shots[GLYPHS[i]], shots[GLYPHS[j]])]); }
      }
      expect(pairs).toHaveLength(21);
      for (const [a, b, share] of pairs) { expect(share, `${a} and ${b} differ in ${(share * 100).toFixed(1)}% of their pixels`).toBeGreaterThan(0.04); }
    });
  }
});

test('working turns once per 1.2 s and delegating\'s satellites once per 8 s, and nothing else on a glyph moves', async ({ page }) => {
  await openDemo(page);
  await gallery(page, ['working', 'delegating', 'needs', 'idle'], { sizes: [16], driven: true });
  const motion = await page.evaluate(() => document.getAnimations()
    .filter((a) => a.effect?.target?.closest?.('#glyph-gallery'))
    .map((a) => ({ part: a.effect.target.getAttribute('class'), row: a.effect.target.closest('[data-row]').dataset.row, ms: a.effect.getTiming().duration, iterations: a.effect.getTiming().iterations })));
  const by = (row, part) => motion.filter((m) => m.row === row && m.part === part).map((m) => [m.ms, m.iterations]);
  expect(by('working', 'glyph-spin')).toEqual([[1200, Infinity], [1200, Infinity]]);
  expect(by('delegating', 'glyph-orbit')).toEqual([[8000, Infinity], [8000, Infinity]]);
  expect(motion.filter((m) => m.row === 'needs' || m.row === 'idle')).toEqual([]);
});

// The compositor turns them (#610, #505): with nothing changing but glyphs turning, the page
// recalculates no style and lays nothing out from one frame to the next. Turning an SVG child
// instead cost both on every frame, about 120 of each in these 2 s.
test('turning glyphs cost the page no style or layout work from frame to frame', async ({ page, browserName }) => {
  test.skip(browserName !== 'chromium', 'only Chromium counts style and layout passes (DevTools Performance)');
  await openDemo(page);
  await gallery(page, ['working', 'delegating'], { sizes: SIZES, driven: true });
  expect(await page.evaluate(() => document.getAnimations().filter((a) => a.playState === 'running').length)).toBeGreaterThan(10);
  const cdp = await page.context().newCDPSession(page);
  await cdp.send('Performance.enable');
  // Past the gallery's own drawing, and past the older turns the open session draws a few at a
  // time (#1622): the same turns, frame after frame.
  await page.evaluate(() => new Promise((done) => {
    let last = -1;
    let same = 0;
    const frame = () => {
      const n = document.querySelectorAll('.history-turn').length;
      same = n === last ? same + 1 : 0;
      last = n;
      if (same >= 30) { done(); } else { requestAnimationFrame(frame); }
    };
    frame();
  }));
  await page.waitForTimeout(500);
  const count = async () => Object.fromEntries((await cdp.send('Performance.getMetrics')).metrics.map((m) => [m.name, m.value]));
  const before = await count();
  await page.waitForTimeout(2000);
  const after = await count();
  const styles = after.RecalcStyleCount - before.RecalcStyleCount;
  const layouts = after.LayoutCount - before.LayoutCount;
  const line = `Turning glyphs (#610), over 2 s: ${styles} style recalculations, ${layouts} layouts`;
  console.log(line);
  test.info().annotations.push({ type: 'glyph frames', description: line });
  if (process.env.GITHUB_STEP_SUMMARY) { appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${line}\n\n`); }
  expect(styles).toBeLessThanOrEqual(6);
  expect(layouts).toBeLessThanOrEqual(6);
});

test('reduced motion stops every glyph\'s animation', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' });
  await openDemo(page);
  await gallery(page);
  const running = await page.evaluate(() => document.getAnimations().filter((a) => a.playState === 'running').length);
  expect(running).toBe(0);
  const parts = await page.locator('.glyph-spin, .glyph-orbit').evaluateAll((els) => els.map((e) => getComputedStyle(e).animationName));
  expect(parts.length).toBeGreaterThan(10);
  expect(new Set(parts)).toEqual(new Set(['none']));
});

for (const width of [390, 900, 1440]) {
  test(`at ${width} px every session's row and header lead with its glyph, at 16 px, on their first line, beside words however long they are`, async ({ page }) => {
    await page.setViewportSize({ width, height: 900 });
    await openDemo(page);
    // Words long enough to wrap on a phone, and a name with nowhere to break (the protocol allows
    // 120 bytes): they wrap beside the glyph, never leaving it alone or running out of the pane.
    await page.evaluate(() => {
      const d = window.agentBridgeDemo;
      const s = d.host.get('019a1b2c-codex-example');
      const t = new Date(Date.now() + 1000).toISOString();
      d.upsert({
        ...s, status: 'waiting', statusSince: t, updatedAt: t, driver: 'agent', waitingOn: 'b1',
        subagents: [{ id: 'b1', kind: 'background', name: 'CheckTheUbuntuCIFailureOnTheRelayAndTheWitnessAndTheHostLogsAgain', status: 'waiting' }],
        question: { id: 'q-b1', kind: 'form', message: 'Which run?', fields: [], askedAt: t, answerAtMachine: true, agent: 'b1' },
      });
    });
    await expect(page.locator('.session[data-rule="2"]')).toHaveCount(1);
    // Rows: the glyph before the title, centred on the title's line (ui.md §5.1).
    const rows = await page.locator('.session .row-main').evaluateAll((els) => els.map((b) => {
      const gr = b.querySelector('.glyph').getBoundingClientRect();
      const tr = b.querySelector('.row-title').getBoundingClientRect();
      const cr = b.getBoundingClientRect();
      return { inside: tr.right <= cr.right, w: gr.width, h: gr.height, before: gr.right <= tr.left, centred: Math.abs((gr.top + gr.bottom) / 2 - (tr.top + tr.bottom) / 2) <= 3 };
    }));
    expect(rows).toHaveLength(4);
    for (const r of rows) { expect(r).toEqual({ inside: true, w: 16, h: 16, before: true, centred: true }); }
    // Headers: the glyph leads the meta line, centred on the words' first line, and the words stay
    // inside the pane.
    for (const id of await page.locator('.session').evaluateAll((els) => els.map((e) => e.dataset.id))) {
      const v = await open(page, id);
      const l = await v.locator('.status-line').evaluate((line) => {
        const g = line.querySelector('.glyph');
        const text = line.querySelector('.status-text');
        const gr = g.getBoundingClientRect();
        const first = text.getClientRects()[0];
        const tr = text.getBoundingClientRect();
        const cr = line.closest('.session-header').getBoundingClientRect();
        return { inside: tr.right <= cr.right, first: line.firstElementChild === g, w: gr.width, h: gr.height, before: gr.right <= first.left, centred: Math.abs((gr.top + gr.bottom) / 2 - (first.top + first.bottom) / 2) <= 3 };
      });
      expect(l, id).toEqual({ inside: true, first: true, w: 16, h: 16, before: true, centred: true });
    }
    // A running tool's glyph is the working one, at 12.
    await open(page, CLAUDE);
    const tool = view(page, CLAUDE).locator('.tool-line .glyph');
    await expect(tool).toHaveAttribute('data-glyph', 'working');
    expect(await tool.evaluate((e) => e.getBoundingClientRect().width)).toBe(12);
  });
}
