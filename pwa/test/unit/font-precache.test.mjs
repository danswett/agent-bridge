import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, readFileSync, readdirSync, rmSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
import { copyFonts, shellFiles } from '../../tools/build-site.mjs';

const pwa = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const repo = path.dirname(pwa);
const removed = [
  'design/fonts/SlipgatesMono-Medium.woff2',
  'design/fonts/SlipgatesMono-MediumItalic.woff2',
  'design/brand.js',
  'fixtures/sessions-list.json',
];
const shell = () => [...new Set(shellFiles(readFileSync(path.join(pwa, 'sw.js'), 'utf8')).map((f) => f || 'index.html'))];
const fontFamilies = JSON.parse(readFileSync(path.join(repo, 'design', 'tokens.json'), 'utf8')).type;
const originalFont = (file) => path.join(repo, file.startsWith('SlipgatesSans') ? fontFamilies.sans.files : fontFamilies.mono.files, 'woff2', file);
const sourceFile = (file) => file.startsWith('design/fonts/') ? originalFont(path.basename(file)) : path.join(pwa, file);
const scripts = readdirSync(path.join(pwa, 'js'), { recursive: true }).filter((f) => f.endsWith('.js'));
const scriptSource = (file) => readFileSync(path.join(pwa, 'js', file), 'utf8');
const withoutComments = (source, loader = 'js') => {
  const { transformSync } = createRequire(import.meta.url)('esbuild');
  return transformSync(source, { loader, legalComments: 'none' }).code;
};

test('the offline shell excludes unused medium faces and bundled or demo-only files', (t) => {
  const after = shell();
  const before = [...new Set([...after, ...removed])];
  const bytes = (files) => files.reduce((total, file) => total + statSync(sourceFile(file)).size, 0);
  // #2454, audit head 7b31a6b7: 2,462,578 raw built-shell bytes, of which 1,601,076
  // were fonts. This is a same-checkout SOURCE file-size sum, not that built inventory
  // or HTTP bytes: BUNDLE is empty before the publishing build supplies its chunks.
  t.diagnostic(JSON.stringify({
    measurement: 'source file-size sum; unique static shell entries; no generated bundle or HTTP measurement',
    before: bytes(before), after: bytes(after), saved: bytes(before) - bytes(after),
    files: after,
  }));
  assert.deepEqual(after.filter((file) => removed.includes(file)), [], 'removed precache entries returned');
  const fonts = after.filter((file) => file.endsWith('.woff2'));
  assert.deepEqual(fonts.map((file) => path.basename(file)).sort(), [
    'SlipgatesMono-Bold.woff2', 'SlipgatesMono-BoldItalic.woff2',
    'SlipgatesMono-Italic.woff2', 'SlipgatesMono-Regular.woff2',
    'SlipgatesMono-SemiBold.woff2', 'SlipgatesMono-SemiBoldItalic.woff2',
    'SlipgatesSans-Italic.woff2', 'SlipgatesSans.woff2',
  ], 'every genuinely used weight and style remains available offline');
});

test('every served font preserves every original glyph, outline, metric and shaping table byte for byte', () => {
  const out = path.join(pwa, '.site', 'unit-font-precache');
  mkdirSync(out, { recursive: true });
  try {
    const copied = copyFonts(out).filter((file) => file.endsWith('.woff2'));
    for (const file of copied) {
      assert.deepEqual(readFileSync(path.join(out, 'design', 'fonts', file)), readFileSync(originalFont(file)), `${file}: no character subset or font data may be lost`);
    }
    for (const file of removed.filter((file) => file.endsWith('.woff2'))) {
      assert.ok(copied.includes(path.basename(file)), 'uncached medium faces still ship for on-demand compatibility');
    }
  } finally {
    rmSync(out, { recursive: true, force: true });
  }
});

test('PWA rules and inline styles never request the uncached medium weight, including inherited mono', () => {
  // A 500-weight sans ancestor would also make inline mono inherit 500. Face declarations
  // are not usage: they remain intact for on-demand compatibility and other clients.
  for (const file of ['app.css', 'design/tokens.css']) {
    const css = withoutComments(readFileSync(path.join(pwa, file), 'utf8'), 'css').replace(/@font-face\s*\{[^}]*\}/g, '');
    assert.doesNotMatch(css, /\bfont(?:-weight)?\s*:[^;}]*\b500\b/, `${file}: cache the medium faces before using weight 500`);
  }
  for (const file of scripts) {
    const source = withoutComments(scriptSource(file));
    assert.doesNotMatch(source, /\bfont(?:-weight)?\s*:[^;}"'`]*\b500\b|(?:font(?:Weight)?|["']font(?:-weight)?["'])\s*(?:=|,|:)\s*["']?500\b/, `${file}: inline medium styles need their faces cached`);
  }
  assert.doesNotMatch(readFileSync(path.join(pwa, 'index.html'), 'utf8'), /\bstyle\s*=/i, 'the page has no inline style outside this guard');
});

test('the removed brand module is a bundled import and the removed fixture is fetched only by the demo', () => {
  const references = (needle) => scripts.filter((file) => withoutComments(scriptSource(file)).includes(needle));
  assert.deepEqual(references('design/brand.js'), ['brand.js']);
  assert.match(withoutComments(scriptSource('brand.js')), /import\s*\{\s*BRAND\s*\}\s*from\s*["']\.\.\/design\/brand\.js["']/);
  assert.deepEqual(references('fixtures/sessions-list.json'), ['main.js']);
  const main = withoutComments(scriptSource('main.js'));
  const demo = main.match(/^async function startDemo\(\) \{[\s\S]*?^\}/m)?.[0];
  assert.ok(demo, 'the fixture fetch must stay within the demo entry');
  assert.match(demo, /fetch\(["']fixtures\/sessions-list\.json["']/);
  const outsideDemo = main.replace(demo, '');
  assert.ok(!outsideDemo.includes('fixtures/sessions-list.json'));
  assert.equal([...outsideDemo.matchAll(/\bstartDemo\s*\(/g)].length, 1, 'no non-demo caller fetches the fixture');
  assert.match(outsideDemo, /params\.get\(["']demo["']\) === ["']1["'] \? startDemo\(\) : startLive\(\)/);
});
