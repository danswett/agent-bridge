// Draws design/preview.png: the glyphs, icons and colour roles in dark and light, from the
// same tokens and sprite the clients get. design/README.md asks for it whenever a glyph, icon
// or role changes. It needs a browser, so it is not part of generate.mjs or its CI check:
//
//   cd pwa && npm ci && npx playwright install chromium && cd ..
//   node tools/design-gen/preview.mjs

import { createRequire } from 'node:module';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { repoRoot } from './generate.mjs';
import { loadTokens, roleTokens, surfaceTokens } from './lib/tokens.mjs';
import { readDesignDir } from './lib/svg.mjs';
import { spriteSvg } from './lib/emit-pwa.mjs';

const GLYPHS = [
  ['needs', 'needs'], ['working', 'active'], ['delegating', 'active'], ['idle', 'neutral'], ['done', 'neutral'], ['failed', 'danger'],
  ['ended', 'neutral'], ['working', 'agent', 'working + driven'],
];

export function previewHtml(root = repoRoot) {
  const design = path.join(root, 'design');
  const tokens = loadTokens(design);
  const glyphs = readDesignDir(path.join(design, 'glyphs'), 'glyph');
  const icons = readDesignDir(path.join(design, 'icons'), 'icon').filter((i) => i.name !== 'mark');
  const roles = roleTokens(tokens);
  const surfaces = surfaceTokens(tokens);
  const vars = (theme) => [...roles, ...surfaces].map((t) => `${t.css}: ${t[theme]};`).join(' ');
  const sprite = spriteSvg(glyphs, icons).replace(/^<svg[^>]*>/, '<svg class="sprite" aria-hidden="true">').replace(/<!--[\s\S]*?-->/, '');
  const use = (id, cls = '') => `<svg class="${cls}" viewBox="0 0 16 16"><use href="#${id}"/></svg>`;
  const half = (theme, title) => `
  <section class="${theme}">
    <h1>${title}</h1>
    <h2>Status glyphs (16 grid, shown at 32)</h2>
    <div class="grid">${GLYPHS.map(([g, role, label]) => `
      <figure class="${role}"><span class="glyph">${use(`glyph-${g}`)}${label ? use('glyph-driven-badge') : ''}</span><figcaption>${label ?? g}</figcaption></figure>`).join('')}
    </div>
    <h2>Icons (20 grid, shown at 28)</h2>
    <div class="grid">${icons.map((i) => `
      <figure><svg class="icon" viewBox="0 0 20 20"><use href="#icon-${i.name}"/></svg><figcaption>${i.name}</figcaption></figure>`).join('')}
    </div>
    <h2>Colour roles</h2>
    <div class="grid">${Object.keys(tokens.color.role).map((r) => `
      <figure><span class="swatch ${r}"></span><figcaption>${r}</figcaption></figure>`).join('')}
    </div>
  </section>`;
  return `<!doctype html><html><head><meta charset="utf-8"><style>
  * { box-sizing: border-box; margin: 0; }
  body { display: grid; grid-template-columns: 1fr 1fr; width: 1440px; font-family: "Segoe UI", system-ui, sans-serif; }
  .sprite { position: absolute; width: 0; height: 0; }
  section { padding: 26px 28px 12px; background: var(--ab-shell); color: var(--ab-text); --ab-glyph-knockout: var(--ab-shell); --ab-glyph-ink: var(--ab-needs-ink); }
  .dark { ${vars('dark')} }
  .light { ${vars('light')} }
  h1 { font-size: 18px; font-weight: 600; margin-bottom: 18px; }
  h2 { font-size: 12px; font-weight: 600; color: var(--ab-text2); margin: 0 0 10px; }
  .grid { display: grid; grid-template-columns: repeat(6, 98px); row-gap: 10px; margin-bottom: 18px; }
  figure { display: grid; justify-items: center; gap: 4px; }
  figcaption { font-size: 11px; color: var(--ab-text2); text-align: center; max-width: 80px; line-height: 14px; }
  .glyph { position: relative; width: 32px; height: 32px; }
  .glyph svg { position: absolute; inset: 0; width: 32px; height: 32px; }
  .icon { width: 28px; height: 28px; color: var(--ab-text); }
  .needs { color: var(--ab-needs); } .active { color: var(--ab-active); } .agent { color: var(--ab-agent); }
  .danger { color: var(--ab-danger); } .neutral { color: var(--ab-neutral); } .bridge { color: var(--ab-bridge); }
  .swatch { display: block; width: 40px; height: 24px; border-radius: 4px; background: currentColor; }
  </style></head><body>${sprite}${half('dark', 'Dark')}${half('light', 'Light')}</body></html>`;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) {
  const require = createRequire(path.join(repoRoot, 'pwa', 'package.json'));
  const { chromium } = require('@playwright/test');
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 1440, height: 600 }, deviceScaleFactor: 2 });
  await page.setContent(previewHtml());
  const height = await page.evaluate(() => document.body.scrollHeight);
  await page.setViewportSize({ width: 1440, height });
  const out = path.join(repoRoot, 'design', 'preview.png');
  // Drawn at 2x and saved at 1x, so thin strokes keep their anti-aliasing.
  await page.screenshot({ path: out, fullPage: true, scale: 'css' });
  await browser.close();
  console.log(`Wrote ${path.relative(repoRoot, out)} (1440 × ${height})`);
}
