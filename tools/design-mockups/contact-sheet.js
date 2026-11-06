// Tiles rendered board PNGs into one contact sheet, offline and deterministically.
// Usage: node contact-sheet.js <dir> <prefix> [out.png] [columns] [--note=<text>]
// Example: node contact-sheet.js ../../docs/design/proposals/384-first-run proposal-384-
// --note replaces the subtitle's "proposal mockups, not approved", for a decided proposal.
const fs = require('fs');
const path = require('path');
const { PNG } = require('pngjs');
const { chromium } = require('playwright');

const args = process.argv.slice(2);
const noteArg = args.find(a => a.startsWith('--note='));
const [dirArg, prefix = '', outArg, colsArg] = args.filter(a => !a.startsWith('--note='));
const note = noteArg ? noteArg.slice('--note='.length) : 'proposal mockups, not approved';
if (!dirArg) {
  console.error('usage: node contact-sheet.js <dir> <prefix> [out.png] [columns] [--note=<text>]');
  process.exit(2);
}
const dir = path.resolve(dirArg);
const out = path.resolve(outArg || path.join(dir, 'contact-sheet.png'));
const cols = Number(colsArg) || 4;
const cellW = 360;
const cellH = 260;
const gap = 16;

const files = fs.readdirSync(dir)
  .filter(f => f.startsWith(prefix) && f.endsWith('.png') && path.resolve(dir, f) !== out)
  .sort();
if (files.length === 0) {
  console.error(`no ${prefix}*.png in ${dir}`);
  process.exit(1);
}

const fontDir = path.join(__dirname, 'node_modules', '@fontsource-variable', 'inter', 'files');
const font = fs.readdirSync(fontDir).find(f => /latin-wght-normal\.woff2$/.test(f));
const fontUrl = font ? `data:font/woff2;base64,${fs.readFileSync(path.join(fontDir, font)).toString('base64')}` : '';

const esc = s => s.replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
const cells = files.map(f => {
  const buf = fs.readFileSync(path.join(dir, f));
  const { width, height } = PNG.sync.read(buf, { skipRescale: true });
  const scale = Math.min(cellW / width, cellH / height);
  const w = Math.round(width * scale);
  const h = Math.round(height * scale);
  return `<figure><div class="img"><img src="data:image/png;base64,${buf.toString('base64')}" width="${w}" height="${h}" alt=""></div><figcaption>${esc(f.replace(/\.png$/, ''))}</figcaption></figure>`;
}).join('');

const rows = Math.ceil(files.length / cols);
const pageW = cols * cellW + (cols + 1) * gap;
const pageH = 56 + rows * (cellH + 28 + gap) + gap;
const html = `<!doctype html><html><head><meta charset="utf-8"><style>
${fontUrl ? `@font-face { font-family: 'Inter Variable'; src: url(${fontUrl}) format('woff2'); font-weight: 100 900; }` : ''}
html, body { margin: 0; background: #F3F3F3; font-family: 'Inter Variable', 'Segoe UI', sans-serif; color: #1F2328; }
h1 { margin: 0; padding: ${gap}px ${gap}px 0; font-size: 20px; line-height: 28px; font-weight: 600; }
p { margin: 0 ${gap}px; font-size: 12px; color: #59636E; }
main { display: grid; grid-template-columns: repeat(${cols}, ${cellW}px); gap: ${gap}px; padding: ${gap}px; }
figure { margin: 0; }
.img { width: ${cellW}px; height: ${cellH}px; display: grid; place-items: center; background: #E6E6E6; border-radius: 4px; overflow: hidden; }
figcaption { font-size: 12px; line-height: 16px; padding-top: 6px; }
</style></head><body><h1>${esc(prefix || path.basename(dir))}</h1><p>${files.length} boards · ${esc(note)}</p><main>${cells}</main></body></html>`;

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: pageW, height: pageH }, deviceScaleFactor: 1 });
  await page.setContent(html, { waitUntil: 'load' });
  await page.evaluate(() => document.fonts.ready);
  await page.screenshot({ path: out, fullPage: true, animations: 'disabled' });
  await browser.close();
  console.log(`wrote ${path.relative(process.cwd(), out)} (${files.length} boards)`);
})().catch(e => { console.error(e); process.exit(1); });
