import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { brotliDecompressSync } from 'node:zlib';
import { repoRoot } from '../generate.mjs';
import { loadTokens, fontXHeight, inlineMono, inlineMonoSizes, monoFont, sansFont } from '../lib/tokens.mjs';
import { platformSelector, tokensCss } from '../lib/emit-pwa.mjs';
import { designXaml } from '../lib/emit-xaml.mjs';

// Inline mono beside the text face (docs/design/ui.md §4.2, #215): the x-heights the tokens
// record are the fonts' own, and every client scales mono by them.
const tokens = loadTokens(path.join(repoRoot, 'design'));
const ratioOf = (f) => f.xHeight / f.unitsPerEm;

test('Slipgates Mono\'s x-height in tokens.json is the bundled font\'s own, measured from every face\'s file', () => {
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
function fontTables(b) {
  if (b.toString('latin1', 0, 4) !== 'wOF2') {
    const at = (tag) => {
      for (let i = 0, r = 12; i < b.readUInt16BE(4); i++, r += 16) {
        if (b.toString('latin1', r, r + 4) === tag) { const offset = b.readUInt32BE(r + 8); return b.subarray(offset, offset + b.readUInt32BE(r + 12)); }
      }
    };
    return { os2: at('OS/2'), hhea: at('hhea') };
  }
  if (b.toString('latin1', 4, 8) === 'ttcf') { throw new Error('a WOFF2 collection: its collection directory is not read here'); }
  let p = 48;
  const base128 = () => { let v = 0; for (;;) { const byte = b[p++]; v = v * 128 + (byte & 0x7f); if (!(byte & 0x80)) return v; } };
  const directory = [];
  for (let i = 0, n = b.readUInt16BE(12); i < n; i++) {
    const flags = b[p++];
    const tag = (flags & 0x3f) === 63 ? b.toString('latin1', p, (p += 4)) : WOFF2_TAGS[flags & 0x3f] ?? `known-${flags & 0x3f}`;
    const version = flags >> 6;
    const length = base128();
    const transformed = tag === 'glyf' || tag === 'loca' ? version === 0 : version !== 0;
    directory.push({ tag, length: transformed ? base128() : length });
  }
  const data = brotliDecompressSync(b.subarray(p, p + b.readUInt32BE(20)));
  const tables = {};
  let at = 0;
  for (const entry of directory) {
    tables[entry.tag] = data.subarray(at, at + entry.length);
    at += entry.length;
  }
  return { os2: tables['OS/2'], hhea: tables.hhea };
}
const advances = (file) => {
  const { os2, hhea } = fontTables(readFileSync(file));
  assert.ok(os2?.length >= 4 && hhea?.length >= 12, `no OS/2 or hhea table read from ${file}`);
  return { average: os2.readInt16BE(2), widest: hhea.readUInt16BE(10) };
};

test('every bundled face of Slipgates Mono, TTF and WOFF2, is the build plan\'s normal 600-unit width, not the condensed 560 (#295)', () => {
  const font = monoFont(tokens);
  const plan = readFileSync(path.join(repoRoot, font.dir, 'private-build-plans.toml'), 'utf8');
  const shape = /\[buildPlans\.SlipgatesMono\.widths\.Normal\]\s+shape = (\d+)/.exec(plan);
  assert.ok(shape, 'could not read widths.Normal.shape from private-build-plans.toml');
  const width = Number(shape[1]);
  // The owner's decision on #295: Cascadia Mono's and Consolas' proportions, one face everywhere.
  assert.equal(width, 600);
  for (const face of font.faces) {
    for (const file of [`ttf/${face.file}.ttf`, `woff2/${face.file}.woff2`]) {
      assert.deepEqual(advances(path.join(repoRoot, font.dir, file)), { average: width, widest: width }, file);
    }
  }
});

// The text face is Slipgates Sans on every platform (#544), so each platform's recorded x-height
// is the bundled font's own: measured from its upright and italic files and its collection.
test('every platform\'s text x-height is Slipgates Sans\', measured from the bundled files', () => {
  const font = sansFont(tokens);
  const files = [...font.variableFiles.map((v) => `ttf/${v.file}.ttf`), font.collection];
  for (const file of files) {
    const measured = fontXHeight(readFileSync(path.join(repoRoot, font.dir, file)));
    for (const platform of ['windows', 'apple', 'android']) {
      const recorded = tokens.type.inlineMono.text[platform];
      assert.equal(recorded.face, font.family, platform);
      assert.deepEqual({ xHeight: measured.xHeight, unitsPerEm: measured.unitsPerEm }, { xHeight: recorded.xHeight, unitsPerEm: recorded.unitsPerEm }, `${platform}: ${file}`);
    }
  }
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

test('the app gets inline mono\'s size for each Windows ramp role: the role\'s size at the text face\'s scale, never under 11 px', () => {
  const xaml = designXaml(tokens, [], []);
  const scale = inlineMono(tokens).platforms.find((p) => p.name === 'windows').scale;
  const sizes = inlineMonoSizes(tokens);
  assert.deepEqual(sizes.map((s) => s.role), ['row', 'reading', 'meta', 'sectionHeader']);
  for (const s of sizes) {
    assert.equal(s.value, Math.max(11, Math.round(s.size * scale * 100) / 100), s.role);
    assert.ok(xaml.includes(`<x:Double x:Key="${s.xaml}">${s.value}</x:Double>`), s.xaml);
  }
  assert.ok(xaml.includes(`<x:Double x:Key="AbInlineMonoScale">${scale}</x:Double>`));
  // The meta role is 12 px: at the text face's scale, still 11 px or more.
  assert.ok(sizes.every((s) => s.value >= 11));
});