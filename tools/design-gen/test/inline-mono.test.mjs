import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { repoRoot } from '../generate.mjs';
import { loadTokens, fontXHeight, inlineMono, inlineMonoSizes, monoFont } from '../lib/tokens.mjs';
import { tokensCss } from '../lib/emit-pwa.mjs';
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
    assert.ok(css.includes(`:root[data-platform="${p.name}"] { --ab-inline-mono-x: ${p.x}; --ab-inline-mono-scale: ${p.scale}; }`), p.name);
  }
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