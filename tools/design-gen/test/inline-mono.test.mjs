import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { brotliDecompressSync } from 'node:zlib';
import { repoRoot } from '../generate.mjs';
import { loadTokens, fontXHeight, inlineMono, inlineMonoSizes, monoFont } from '../lib/tokens.mjs';
import { platformSelector, tokensCss } from '../lib/emit-pwa.mjs';
import { designXaml } from '../lib/emit-xaml.mjs';

// Inline mono beside the system face (docs/design/ui.md §4.2, #215): the x-heights the tokens
// record are the fonts' own, and every client scales mono by them.
const tokens = loadTokens(path.join(repoRoot, 'design'));
const ratioOf = (f) => f.xHeight / f.unitsPerEm;

test('Agent Bridge Mono\'s x-height in tokens.json is the bundled font\'s own, measured from every face\'s file', () => {
  const font = monoFont(tokens);
  for (const face of font.faces) {
    const measured = fontXHeight(readFileSync(path.join(repoRoot, font.dir, 'ttf', `${face.file}.ttf`)));
    assert.deepEqual({ xHeight: measured.xHeight, unitsPerEm: measured.unitsPerEm }, { xHeight: tokens.type.inlineMono.mono.xHeight, unitsPerEm: tokens.type.inlineMono.mono.unitsPerEm }, face.file);
  }
});

// The width the fonts were built at, in units per em: each face's average advance (OS/2
// xAvgCharWidth) and widest advance (hhea advanceWidthMax), equal in a monospace. A TTF's
// tables are read straight off its directory; a WOFF2's from its brotli stream, where they
// lie end to end in directory order (OS/2 and hhea are never transformed, so they read as
// they would in the TTF).
const WOFF2_TAGS = ['cmap', 'head', 'hhea', 'hmtx', 'maxp', 'name', 'OS/2', 'post', 'cvt ', 'fpgm', 'glyf', 'loca'];
function fontTables(bytes) {
  const b = Buffer.from(bytes);
  if (b.toString('latin1', 0, 4) !== 'wOF2') {
    const at = (tag) => { for (let i = 0; i < b.readUInt16BE(4); i++) { if (b.toString('latin1', 12 + i * 16, 16 + i * 16) === tag) return b.subarray(b.readUInt32BE(20 + i * 16)); } };
    return { os2: at('OS/2'), hhea: at('hhea') };
  }
  let p = 48;
  const base128 = () => { let v = 0; for (;;) { const byte = b[p++]; v = v * 128 + (byte & 0x7f); if (!(byte & 0x80)) return v; } };
  const directory = [];
  for (let i = 0, n = b.readUInt16BE(12); i < n; i++) {
    const flags = b[p++];
    const tag = (flags & 0x3f) === 63 ? b.toString('latin1', p, (p += 4)) : WOFF2_TAGS[flags & 0x3f] ?? '?';
    const version = flags >> 6;
    const length = base128();
    const transformed = tag === 'glyf' || tag === 'loca' ? version === 0 : version !== 0;
    directory.push({ tag, length: transformed ? base128() : length });
  }
  const data = brotliDecompressSync(b.subarray(p, p + b.readUInt32BE(20)));
  const tables = {};
  for (let at = 0, i = 0; i < directory.length; at += directory[i++].length) { tables[directory[i].tag] = data.subarray(at); }
  return { os2: tables['OS/2'], hhea: tables.hhea };
}
const advances = (bytes) => { const { os2, hhea } = fontTables(bytes); return { average: os2.readInt16BE(2), widest: hhea.readUInt16BE(10) }; };

test('every bundled face of Agent Bridge Mono, TTF and WOFF2, is the build plan\'s normal 600-unit width, not the condensed 560 (#295)', () => {
  const font = monoFont(tokens);
  const plan = readFileSync(path.join(repoRoot, font.dir, 'private-build-plans.toml'), 'utf8');
  const width = Number(/\[buildPlans\.AgentBridgeMono\.widths\.Normal\]\s+shape = (\d+)/.exec(plan)?.[1]);
  // The owner's decision on #295: Cascadia Mono's and Consolas' proportions, one face everywhere.
  assert.equal(width, 600);
  for (const face of font.faces) {
    for (const file of [`ttf/${face.file}.ttf`, `woff2/${face.file}.woff2`]) {
      assert.deepEqual(advances(readFileSync(path.join(repoRoot, font.dir, file))), { average: width, widest: width }, file);
    }
  }
});

// The text faces aren't ours to bundle, so each is measured where it is installed: Segoe UI
// Variable on any Windows machine (CI's Windows runner among them).
const SEGOE = 'C:\\Windows\\Fonts\\SegUIVar.ttf';
test('the Windows text face\'s x-height is Segoe UI Variable\'s, measured from the installed font', { skip: !existsSync(SEGOE) && 'Segoe UI Variable is not installed here' }, () => {
  const measured = fontXHeight(readFileSync(SEGOE));
  const recorded = tokens.type.inlineMono.text.windows;
  assert.equal(ratioOf(measured), ratioOf(recorded));
});

test('inline mono at each platform\'s scale has that platform\'s text x-height, and is never made larger than the text', () => {
  const { mono, platforms } = inlineMono(tokens);
  assert.deepEqual(platforms.map((p) => p.name), ['windows', 'apple', 'android']);
  for (const p of platforms) {
    const text = ratioOf(tokens.type.inlineMono.text[p.name]);
    assert.ok(Math.abs(p.scale * mono - text) < 0.0005, `${p.name}: ${p.scale} × ${mono} ≠ ${text}`);
    assert.ok(Math.abs(p.x - text) < 0.0001, p.name);
  }
});

test('the PWA gets each platform\'s ratio and scale, with Windows\' as the default', () => {
  const css = tokensCss(tokens);
  const { platforms } = inlineMono(tokens);
  const windows = platforms.find((p) => p.name === 'windows');
  assert.match(css, new RegExp(`:root \\{[\\s\\S]*--ab-inline-mono-x: ${windows.x}; --ab-inline-mono-scale: ${windows.scale};`));
  for (const p of platforms.filter((x) => x.name !== 'windows')) {
    assert.ok(css.includes(`${platformSelector(p.name)} { --ab-inline-mono-x: ${p.x}; --ab-inline-mono-scale: ${p.scale}; }`), p.name);
  }
});

test('Apple\'s face is set on every platform the page names as Apple\'s: a Mac, an iPhone and an iPad', () => {
  const css = tokensCss(tokens);
  const apple = inlineMono(tokens).platforms.find((p) => p.name === 'apple');
  assert.ok(css.includes(`:root[data-platform="macos"], :root[data-platform="ios"], :root[data-platform="ipados"] { --ab-inline-mono-x: ${apple.x};`));
  assert.ok(!css.includes('data-platform="apple"'));
});

test('the app gets inline mono\'s size for each Windows ramp role: the role\'s size at Segoe\'s scale, never under 11 px', () => {
  const xaml = designXaml(tokens, [], []);
  const scale = inlineMono(tokens).platforms.find((p) => p.name === 'windows').scale;
  const sizes = inlineMonoSizes(tokens);
  assert.deepEqual(sizes.map((s) => s.role), ['row', 'reading', 'meta', 'sectionHeader']);
  for (const s of sizes) {
    assert.equal(s.value, Math.max(11, Math.round(s.size * scale * 100) / 100), s.role);
    assert.ok(xaml.includes(`<x:Double x:Key="${s.xaml}">${s.value}</x:Double>`), s.xaml);
  }
  assert.ok(xaml.includes(`<x:Double x:Key="AbInlineMonoScale">${scale}</x:Double>`));
  // The meta role is 12 px: at Segoe's scale, still 11 px or more.
  assert.ok(sizes.every((s) => s.value >= 11));
});