// The design language's colours in the page (docs/design/ui.md §3, #177): the generated tokens
// in light, dark and forced colours, and the generated sprite drawing a glyph under the CSP.
import { test, expect } from '@playwright/test';
import { readFileSync } from 'node:fs';

const tokens = JSON.parse(readFileSync(new URL('../../design/tokens.json', import.meta.url), 'utf8'));
const role = tokens.color.role;
const surface = tokens.surface.windows;
const COPILOT = '02d9e5d5-6b35-49bd-b083-39572fbff648';
const CLAUDE = '6f1c2b7e-claude-example';

const rgb = (hex) => {
  const n = parseInt(hex.slice(1), 16);
  return `rgb(${(n >> 16) & 255}, ${(n >> 8) & 255}, ${n & 255})`;
};

async function openDemo(page) {
  await page.addInitScript(() => {
    window.__cspViolations = [];
    document.addEventListener('securitypolicyviolation', (e) => window.__cspViolations.push(`${e.violatedDirective} ${e.blockedURI}`));
  });
  await page.goto('?demo=1&static=1');
  await page.waitForSelector('html[data-demo-ready]');
}

// A style once the element's transitions have finished (the card edges ease over 0.4 s).
const settled = (locator, prop) => locator.evaluate(async (e, p) => {
  await Promise.all(e.getAnimations().map((a) => a.finished));
  return getComputedStyle(e)[p];
}, prop);

for (const scheme of ['light', 'dark']) {
  test(`in ${scheme} the page, its cards, controls and status take the generated roles and surfaces`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme });
    await openDemo(page);
    const v = (r) => r[scheme];
    expect(await page.locator('body').evaluate((e) => getComputedStyle(e).backgroundColor)).toBe(rgb(surface[scheme].shell));
    const copilot = page.locator(`.session[data-id="${COPILOT}"]`);
    const claude = page.locator(`.session[data-id="${CLAUDE}"]`);
    expect(await settled(copilot.locator('.frame'), 'backgroundColor')).toBe(rgb(surface[scheme].card));
    // Status in the status roles: working in active, waiting in needs, its words in needs' text.
    expect(await settled(claude.locator('.frame'), 'borderTopColor')).toBe(rgb(v(role.active)));
    expect(await settled(copilot.locator('.frame'), 'borderTopColor')).toBe(rgb(v(role.needs)));
    expect(await copilot.locator('.status-text').evaluate((e) => getComputedStyle(e).color)).toBe(rgb(role.needs[`text${scheme === 'light' ? 'Light' : 'Dark'}`]));
    // Active is a glyph colour, never words: the working card says so in plain text.
    expect(await claude.locator('.status-text').evaluate((e) => getComputedStyle(e).color)).toBe(rgb(surface[scheme].text));
    // Controls are bridge, never a status colour.
    expect(await page.locator('#demo-banner .button').evaluate((e) => getComputedStyle(e).color)).toBe(rgb(v(role.bridge)));
    expect(await page.evaluate(() => window.__cspViolations)).toEqual([]);
  });
}

test('in forced colours every role is a system colour', async ({ page, browserName }) => {
  test.skip(browserName !== 'chromium', 'forced colours are emulated in Chromium only');
  await page.emulateMedia({ forcedColors: 'active' });
  await openDemo(page);
  const resolved = await page.evaluate(() => {
    const probe = (value) => {
      const i = document.createElement('i');
      i.style.forcedColorAdjust = 'none';
      i.style.color = value;
      document.body.append(i);
      const c = getComputedStyle(i).color;
      i.remove();
      return c;
    };
    return {
      bridge: probe('var(--ab-bridge)'), needs: probe('var(--ab-needs)'), danger: probe('var(--ab-danger)'),
      text2: probe('var(--ab-text2)'), card: probe('var(--ab-card)'),
      Highlight: probe('Highlight'), CanvasText: probe('CanvasText'), Canvas: probe('Canvas'),
    };
  });
  expect(resolved.bridge).toBe(resolved.Highlight);
  expect(resolved.needs).toBe(resolved.Highlight);
  expect(resolved.danger).toBe(resolved.CanvasText);
  expect(resolved.text2).toBe(resolved.CanvasText);
  expect(resolved.card).toBe(resolved.Canvas);
  expect(resolved.bridge).not.toBe(rgb(role.bridge.light));
  // A filled control keeps its label: the browser's backplate behind it once hid the text.
  const send = page.getByRole('button', { name: 'Send', exact: true }).first();
  expect(await send.evaluate((e) => getComputedStyle(e).forcedColorAdjust)).toBe('none');
  expect(await send.evaluate((e) => getComputedStyle(e).backgroundColor)).toBe(resolved.Highlight);
  expect(await send.evaluate((e) => getComputedStyle(e).color)).not.toBe(resolved.Highlight);
});

test('the generated sprite draws the needs glyph in its role colour with its ink, under the page\'s CSP', async ({ page, context }) => {
  await page.emulateMedia({ colorScheme: 'light' });
  await openDemo(page);
  // Built through the DOM, as the app builds everything: no markup is parsed.
  const loaded = page.waitForResponse((r) => r.url().endsWith('/design/sprite.svg') && r.ok());
  await page.evaluate(() => {
    const NS = 'http://www.w3.org/2000/svg';
    const svg = document.createElementNS(NS, 'svg');
    svg.setAttribute('id', 'probe-glyph');
    svg.setAttribute('width', '160');
    svg.setAttribute('height', '160');
    svg.style.color = 'var(--ab-needs)';
    svg.style.background = 'var(--ab-card)';
    const use = document.createElementNS(NS, 'use');
    use.setAttribute('href', 'design/sprite.svg#glyph-needs');
    use.setAttribute('width', '160');
    use.setAttribute('height', '160');
    svg.append(use);
    document.body.prepend(svg);
  });
  await loaded;
  await page.waitForTimeout(100);
  // Checked before the screenshot, whose own caret-hiding style WebKit reports as inline.
  expect(await page.evaluate(() => window.__cspViolations)).toEqual([]);
  const shot = (await page.locator('#probe-glyph').screenshot()).toString('base64');
  // Read the pixels in a blank page: the app's CSP rightly refuses data: images to scripts.
  const blank = await context.newPage();
  const at = await blank.evaluate(async (b64) => {
    const img = new Image();
    img.src = `data:image/png;base64,${b64}`;
    await img.decode();
    const c = document.createElement('canvas');
    [c.width, c.height] = [img.width, img.height];
    const x = c.getContext('2d');
    x.drawImage(img, 0, 0);
    const px = (u, v) => { const [r, g, b] = x.getImageData(Math.round(u * img.width / 16), Math.round(v * img.height / 16), 1, 1).data; return `rgb(${r}, ${g}, ${b})`; };
    // The disc beside the "!", the "!" itself, and outside the disc.
    return { disc: px(4.5, 8), ink: px(8, 6.5), outside: px(0.4, 0.4) };
  }, shot);
  expect(at.disc).toBe(rgb(role.needs.light));
  expect(at.ink).toBe(rgb(role.needs.inkLight));
  expect(at.outside).toBe(rgb(surface.light.card));
});

test('code is set in Agent Bridge Mono, loaded from the site under the CSP, with the fallback stack behind it', async ({ page }) => {
  const fonts = [];
  page.on('response', (r) => { if (r.url().includes('/design/fonts/')) { fonts.push(`${r.status()} ${r.url().split('/').pop()}`); } });
  await openDemo(page);
  const code = page.locator('.md code').first();
  await expect(code).toBeVisible();
  expect(await code.evaluate((e) => getComputedStyle(e).fontFamily)).toMatch(/^"?Agent Bridge Mono"?, "?Cascadia Mono"?, "?SF Mono"?, "?Roboto Mono"?, ui-monospace, monospace$/);
  const loaded = await page.evaluate(async () => {
    const faces = await document.fonts.load('400 13px "Agent Bridge Mono"');
    await document.fonts.load('italic 600 13px "Agent Bridge Mono"');
    return { regular: faces.length, status: [...document.fonts].filter((f) => f.family.replace(/"/g, '') === 'Agent Bridge Mono' && f.status === 'loaded').map((f) => `${f.weight} ${f.style}`).sort() };
  });
  expect(loaded.regular).toBeGreaterThan(0);
  expect(loaded.status).toEqual(expect.arrayContaining(['400 normal', '600 italic']));
  expect(fonts).toEqual(expect.arrayContaining(['200 AgentBridgeMono-Regular.woff2', '200 AgentBridgeMono-SemiBoldItalic.woff2']));
  expect(await page.evaluate(() => window.__cspViolations)).toEqual([]);
});
