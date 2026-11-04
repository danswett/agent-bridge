// Is Slipgates Sans too condensed? (#544 addendum, §11 of 544-custom-sans.md.)
// Screenshots of the real PWA in its demo mode, before (the branch's base, whose text face is the
// system stack `system-ui, -apple-system, "Segoe UI", Roboto, sans-serif`) and after (this
// branch, Slipgates Sans), plus the alternatives drawn into the same page; and the length of one
// paragraph and one title in every face, measured in Chromium.
//
//   node width.mjs --before <url> --after <url> --fonts <dir> --out <dir> --results <file>
//
// <url> is a PWA served by pwa/test/serve.mjs (…/agent-bridge/app/). <dir> holds the comparison
// faces under these names, from github.com/google/fonts (ofl/<family>/): Roboto[-Italic].ttf,
// IBMPlexSans[-Italic].ttf, NotoSans[-Italic].ttf, AtkinsonHyperlegibleNext[-Italic].ttf,
// InterTight.ttf (upright; measured, never drawn italic), and SlipgatesSans[-Italic].ttf from
// design/fonts/slipgates-sans/ttf. Every face comes with its own
// italic and a weight axis, and every override sets font-synthesis: none, so nothing is faked.
// The overrides go in as a style tag, so the page's CSP is bypassed. Run it from pwa/ so that
// `playwright` resolves.
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { createRequire } from 'node:module';
import path from 'node:path';

const { chromium } = createRequire(path.join(process.cwd(), 'noop.js'))('playwright');

const arg = (n) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : undefined; };
const BEFORE = arg('--before'); const AFTER = arg('--after');
const FONTS = arg('--fonts'); const OUT = arg('--out'); const RESULTS = arg('--results');
const CLAUDE = '6f1c2b7e-claude-example';

// The faces drawn into the page in place of the text face. x is the face's own x-height per em
// (measure/width.py), which inline mono is matched to, as tokens.css does for the shipped faces.
const FACES = {
  roboto: { family: 'Cmp Roboto', file: 'Roboto', x: 0.528 },
  plex: { family: 'Cmp IBM Plex Sans', file: 'IBMPlexSans', x: 0.516 },
  noto: { family: 'Cmp Noto Sans', file: 'NotoSans', x: 0.536 },
  atkinson: { family: 'Cmp Atkinson Hyperlegible Next', file: 'AtkinsonHyperlegibleNext', x: 0.496 },
  // Measured only: the interim wordmark face (#558). Upright only, so it is never drawn italic here.
  intertight: { family: 'Cmp Inter Tight', file: 'InterTight', x: 0.546, upright: true },
  // The variable TTF the Apple and Android outputs ship, with its opsz axis left to the engine:
  // Chromium, like CoreText, sets opsz from the size, so a 20 px title gets the opsz 20 cut.
  bauto: { family: 'Cmp Slipgates Sans auto opsz', file: 'SlipgatesSans', x: 0.546 },
};

function faceCss(f) {
  return (f.upright ? [''] : ['', '-Italic']).map((s) => `@font-face { font-family: "${f.family}"; src: url("__cmp/${f.file}${s}.ttf") format("truetype");
    font-weight: 100 900; font-style: ${s ? 'italic' : 'normal'}; }`).join('\n');
}
function overrideCss(f) {
  return `${faceCss(f)}
    :root, :root[data-platform] { --ab-font-sans: "${f.family}", sans-serif; --font-text: "${f.family}", sans-serif;
      --ab-inline-mono-x: ${f.x}; }
    * { font-synthesis: none; }`;
}

// +1% on text, +2% on titles and anything SemiBold or heavier: set where the text is, on every
// element holding text in the text face (inline mono keeps its own spacing).
async function track(page) {
  await page.evaluate(() => {
    for (const el of document.querySelectorAll('body *')) {
      const cs = getComputedStyle(el);
      if (/Mono/.test(cs.fontFamily) || ![...el.childNodes].some((n) => n.nodeType === 3 && n.textContent.trim())) { continue; }
      const title = Number(cs.fontWeight) >= 600 || parseFloat(cs.fontSize) >= 20;
      el.style.letterSpacing = title ? '0.02em' : '0.01em';
    }
  });
}

async function serveFonts(context) {
  await context.route('**/__cmp/*', async (route) => {
    const name = decodeURIComponent(new URL(route.request().url()).pathname.split('/').pop());
    route.fulfill({ body: await readFile(path.join(FONTS, name)), contentType: 'font/ttf' });
  });
}

const DESKTOP = { viewport: { width: 1440, height: 900 } };
const PHONE = {
  viewport: { width: 412, height: 915 }, deviceScaleFactor: 2, isMobile: true, hasTouch: true,
  userAgent: 'Mozilla/5.0 (Linux; Android 15; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Mobile Safari/537.36',
};

// The demo's turn is one line; a reading paragraph needs more. Two paragraphs go in after it, in
// the same element and with the same inline code element the app drew, so the app's own CSS sets
// them (the board says so).
async function lengthen(page) {
  await page.evaluate(() => {
    const view = document.querySelector('.session-view:not([hidden])');
    const p = [...view.querySelectorAll('.stream *')].find((e) => e.firstChild?.nodeType === 3 && e.textContent.startsWith('Splitting'));
    if (!p) { throw new Error('no demo turn to lengthen'); }
    const code = p.querySelector('code');
    const make = (parts) => {
      const n = p.cloneNode(false);
      for (const part of parts) {
        if (Array.isArray(part)) { const c = code.cloneNode(false); c.textContent = part[0]; n.append(c); } else { n.append(part); }
      }
      return n;
    };
    p.after(
      make(['The build finished, but two tests in the parser suite failed after the rename: the fixture still points at ', ['tests/fixtures/old'], ', and the snapshot keeps the previous name.']),
      make(['Shall I update both, rerun ', ['dotnet test'], ' and push the branch? It is a small change: 14 lines in 3 files, and nothing outside the relay client moves.']),
    );
  });
}

async function shot(browser, { base, device, dpr, scheme, css, tracked, view, file, clip }) {
  const context = await browser.newContext({ ...device, deviceScaleFactor: dpr ?? device.deviceScaleFactor ?? 1, colorScheme: scheme, serviceWorkers: 'block', bypassCSP: true });
  await serveFonts(context);
  const page = await context.newPage();
  await page.goto(`${base}?demo=1&static=1`);
  await page.waitForSelector('html[data-demo-ready]');
  if (css) { await page.addStyleTag({ content: css }); }
  if (view) {
    await page.locator(`.session[data-id="${CLAUDE}"] .row-main`).click();
    await page.locator(`.session-view[data-id="${CLAUDE}"]`).waitFor({ state: 'visible' });
    await lengthen(page);
  }
  await page.evaluate(() => document.fonts.ready);
  await page.waitForTimeout(400);
  await page.evaluate(() => Promise.all(document.getAnimations().filter((a) => Number.isFinite(a.effect?.getComputedTiming?.().endTime)).map((a) => a.finished.catch(() => {}))));
  if (tracked) { await track(page); }
  // The faces the engine actually drew the body text and a title with: proof of what is shown.
  const cdp = await context.newCDPSession(page);
  await cdp.send('DOM.enable'); await cdp.send('CSS.enable');
  const { root } = await cdp.send('DOM.getDocument');
  const used = {};
  for (const [k, sel] of Object.entries({ row: '.session .row-main', title: '.session-view:not([hidden]) .session-title' })) {
    const { nodeId } = await cdp.send('DOM.querySelector', { nodeId: root.nodeId, selector: sel });
    if (!nodeId) { continue; }
    const { fonts } = await cdp.send('CSS.getPlatformFontsForNode', { nodeId });
    used[k] = fonts.map((f) => `${f.familyName}${f.postScriptName ? ` (${f.postScriptName})` : ''}`).join(', ');
  }
  await page.screenshot({ path: path.join(OUT, file), clip });
  await context.close();
  return { file, used };
}

// One paragraph and one title, set on one line in each face: the paragraph's length at the
// reading size (15 px) gives characters per 770 px reading line; the title's at 20 px SemiBold, and
// at 34 px, an iPhone's Large Title, where an automatic opsz reaches the display cut.
const PARA = 'The build finished, but two tests in the parser suite failed after the rename: the fixture still points at the old folder, and the snapshot keeps the previous name. Shall I update both and run the suite again?';
const TITLE = 'Rename the parser fixtures';

async function lineLengths(browser) {
  const context = await browser.newContext({ ...DESKTOP, serviceWorkers: 'block', bypassCSP: true });
  await serveFonts(context);
  const page = await context.newPage();
  await page.goto(`${AFTER}?demo=1&static=1`);
  await page.waitForSelector('html[data-demo-ready]');
  // Segoe UI Variable is the machine's own, by its named instances (it has no italic to fake).
  const segoe = `@font-face { font-family: "Cmp Segoe UI Variable Text"; src: local("Segoe UI Variable Text"); font-weight: 400; }
    @font-face { font-family: "Cmp Segoe UI Variable Text"; src: local("Segoe UI Variable Text Semibold"); font-weight: 600; }`;
  const css = `${segoe}\n${Object.values(FACES).map(faceCss).join('\n')}`;
  await page.addStyleTag({ content: `${css}\n* { font-synthesis: none; }` });
  const rows = [
    ['Segoe UI (system-ui in Chromium on Windows: the PWA today)', 'system-ui', ''],
    ['Segoe UI Variable Text (the Windows app today)', '"Cmp Segoe UI Variable Text"', ''],
    ['Roboto (system-ui on Android)', '"Cmp Roboto"', ''],
    ['Slipgates Sans B, as shipped (text cut, opsz 14)', '"Slipgates Sans"', ''],
    ['B, tracked (+1% text, +2% titles)', '"Slipgates Sans"', 'track'],
    ['B, opsz auto (Apple/Android variable TTF)', '"Cmp Slipgates Sans auto opsz"', ''],
    ['B, text cut forced (opsz 14 at every size)', '"Cmp Slipgates Sans auto opsz"', 'opsz14'],
    ['IBM Plex Sans', '"Cmp IBM Plex Sans"', ''],
    ['Noto Sans', '"Cmp Noto Sans"', ''],
    ['Atkinson Hyperlegible Next', '"Cmp Atkinson Hyperlegible Next"', ''],
    ['Inter Tight (interim wordmark, #558)', '"Cmp Inter Tight"', ''],
  ];
  const out = [];
  for (const [name, family, mod] of rows) {
    out.push({ face: name, ...await page.evaluate(async ({ family, mod, PARA, TITLE }) => {
      const make = (text, size, weight, ls) => {
        const s = document.createElement('span');
        Object.assign(s.style, { position: 'absolute', whiteSpace: 'nowrap', fontFamily: family, fontSize: `${size}px`, fontWeight: weight, letterSpacing: ls, left: '0', top: '0' });
        if (mod === 'opsz14') { s.style.fontVariationSettings = '"opsz" 14'; }
        s.textContent = text; document.body.append(s); return s;
      };
      const p = make(PARA, 15, 400, mod === 'track' ? '0.01em' : 'normal');
      const t = make(TITLE, 20, 600, mod === 'track' ? '0.02em' : 'normal');
      const big = make(TITLE, 34, 600, mod === 'track' ? '0.02em' : 'normal');
      await document.fonts.ready;
      const pw = p.getBoundingClientRect().width; const tw = t.getBoundingClientRect().width;
      const bw = big.getBoundingClientRect().width;
      p.remove(); t.remove(); big.remove();
      return { paraPx: Math.round(pw), charsPer770: Math.round(PARA.length / (pw / 770)), titlePx: Math.round(tw), largeTitlePx: Math.round(bw) };
    }, { family, mod, PARA, TITLE }) });
  }
  await context.close();
  return out;
}

await mkdir(OUT, { recursive: true });
const browser = await chromium.launch();
const shots = [];
// The list and the conversation, without the demo banner and the details pane.
const SESSION_CLIP = { x: 49, y: 140, width: 1056, height: 440 };
const B_CSS = (f) => overrideCss(FACES[f]);
for (const job of [
  // The pair the owner sees: a desktop (Windows, 100 %, dark), and an Android phone (light).
  { base: BEFORE, device: DESKTOP, scheme: 'dark', view: true, file: 'desktop-before-dark.png' },
  { base: AFTER, device: DESKTOP, scheme: 'dark', view: true, file: 'desktop-after-dark.png' },
  { base: BEFORE, device: DESKTOP, dpr: 1.5, scheme: 'light', view: true, file: 'desktop-before-light-150.png', clip: SESSION_CLIP },
  { base: AFTER, device: DESKTOP, dpr: 1.5, scheme: 'light', view: true, file: 'desktop-after-light-150.png', clip: SESSION_CLIP },
  { base: BEFORE, device: PHONE, scheme: 'light', css: overrideCss(FACES.roboto), file: 'phone-before-list.png' },
  { base: AFTER, device: PHONE, scheme: 'light', file: 'phone-after-list.png' },
  { base: BEFORE, device: PHONE, scheme: 'dark', css: overrideCss(FACES.roboto), view: true, file: 'phone-before-view.png' },
  { base: AFTER, device: PHONE, scheme: 'dark', view: true, file: 'phone-after-view.png' },
  // The alternatives, in the same session view on a desktop, dark, 100 %.
  { base: AFTER, device: DESKTOP, scheme: 'dark', view: true, file: 'alt-b-now.png', clip: SESSION_CLIP },
  { base: AFTER, device: DESKTOP, scheme: 'dark', view: true, tracked: true, file: 'alt-b-tracked.png', clip: SESSION_CLIP },
  { base: AFTER, device: DESKTOP, scheme: 'dark', view: true, css: B_CSS('bauto'), file: 'alt-b-auto-opsz.png', clip: SESSION_CLIP },
  { base: AFTER, device: DESKTOP, scheme: 'dark', view: true, css: B_CSS('plex'), file: 'alt-plex.png', clip: SESSION_CLIP },
  { base: AFTER, device: DESKTOP, scheme: 'dark', view: true, css: B_CSS('noto'), file: 'alt-noto.png', clip: SESSION_CLIP },
  { base: BEFORE, device: DESKTOP, scheme: 'dark', view: true, file: 'alt-segoe.png', clip: SESSION_CLIP },
]) { shots.push(await shot(browser, job)); }
const lengths = await lineLengths(browser);
await browser.close();
await writeFile(RESULTS, `${JSON.stringify({ shots, lengths }, null, 1)}\n`);
console.log(JSON.stringify({ shots, lengths }, null, 1));
