// Records docs/design/motion-preview.html to docs/design/motion-preview.gif, and the same with
// reduced motion to motion-preview-reduced.gif, offline. Usage: node motion-preview.js
// The page is served from the repository over loopback, because a page opened from file://
// can't <use> the sprite. Frames are taken on a fixed clock (the page's __seek), not in real
// time, so a slow machine records the same GIF.
const fs = require('fs'), path = require('path'), http = require('http');
const { chromium } = require('playwright');
const { PNG } = require('pngjs');
const { GIFEncoder, quantize, applyPalette } = require('gifenc');

const root = path.resolve(__dirname, '..', '..');
const out = path.join(root, 'docs', 'design');
const STEP = 30; // ms per frame: GIF delays are in hundredths, and 30 keeps an 83 ms fade visible.
const TYPES = { '.html': 'text/html', '.css': 'text/css', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };

function serve() {
  const server = http.createServer((req, res) => {
    const file = path.join(root, decodeURIComponent(new URL(req.url, 'http://x').pathname));
    if (!file.startsWith(root) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { res.writeHead(404).end(); return; }
    res.writeHead(200, { 'content-type': TYPES[path.extname(file)] ?? 'application/octet-stream' }).end(fs.readFileSync(file));
  });
  return new Promise((resolve) => server.listen(0, '127.0.0.1', () => resolve(server)));
}

async function record(browser, port, reducedMotion, name) {
  const page = await browser.newPage({ viewport: { width: 390, height: 640 }, colorScheme: 'dark', reducedMotion });
  await page.goto(`http://127.0.0.1:${port}/docs/design/motion-preview.html?capture`);
  await page.evaluate(() => document.fonts.ready);
  const end = await page.evaluate(() => window.__end);
  const frames = [];
  for (let t = 0; t <= end; t += STEP) {
    await page.evaluate((at) => window.__seek(at), t);
    const png = PNG.sync.read(await page.screenshot());
    const last = frames[frames.length - 1];
    if (last && Buffer.compare(last.data, png.data) === 0) { last.delay += STEP; } else { frames.push({ data: png.data, delay: STEP, width: png.width, height: png.height }); }
  }
  await page.close();
  // One palette for every frame: the UI has few colours, and a shared palette keeps them steady.
  const sample = Buffer.concat(frames.filter((_, i) => i % Math.max(1, Math.floor(frames.length / 12)) === 0).map((f) => f.data));
  const palette = quantize(new Uint8Array(sample), 256);
  const gif = GIFEncoder();
  for (const f of frames) {
    gif.writeFrame(applyPalette(new Uint8Array(f.data), palette), f.width, f.height, { palette, delay: f.delay });
  }
  gif.finish();
  fs.writeFileSync(path.join(out, name), gif.bytes());
  console.log(`${name}: ${frames.length} frames, ${(gif.bytes().length / 1024).toFixed(0)} KB`);
}

(async () => {
  const server = await serve();
  const browser = await chromium.launch();
  try {
    await record(browser, server.address().port, 'no-preference', 'motion-preview.gif');
    await record(browser, server.address().port, 'reduce', 'motion-preview-reduced.gif');
  } finally {
    await browser.close();
    server.close();
  }
})();
