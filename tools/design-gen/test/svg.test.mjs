import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { parsePathData, formatCommands, readDesignSvg, parts } from '../lib/svg.mjs';
import { repoRoot } from '../generate.mjs';
import { spriteSvg } from '../lib/emit-pwa.mjs';

const scratch = path.join(repoRoot, 'tools', 'design-gen', '.scratch', 'svg');
function svgFile(name, body, root = 'viewBox="0 0 16 16"') {
  mkdirSync(scratch, { recursive: true });
  const f = path.join(scratch, `${name}.svg`);
  writeFileSync(f, `<svg xmlns="http://www.w3.org/2000/svg" ${root}>${body}</svg>`);
  return f;
}
after(() => rmSync(scratch, { recursive: true, force: true }));

test('relative commands, H and V, and arc flags written together become absolute commands', () => {
  // As the icons write them: a5 5 0 01-7.1-7.1 has its two flags and a sign run together.
  const cmds = parsePathData('M2 3h4v1l-1 1a2 2 0 01-2 2z');
  assert.equal(formatCommands(cmds), 'M2,3 L6,3 L6,4 L5,5 A2,2 0 0 1 3,7 Z');
  assert.equal(formatCommands(parsePathData('M1 1 2 2 3 3')), 'M1,1 L2,2 L3,3', 'pairs after M are lines');
  assert.throws(() => parsePathData('M0 0Q1 1 2 2'), /not in the design subset/);
});

test('a circle starts at 3 o\'clock and runs clockwise, as SVG draws it, so its dashes fall the same way', () => {
  const svg = readDesignSvg(svgFile('circle', '<circle cx="8" cy="8" r="5" fill="none" stroke="currentColor"/>'), 'glyph');
  assert.equal(formatCommands(parts(svg)[0].commands), 'M13,8 A5,5 0 0 1 3,8 A5,5 0 0 1 13,8 Z');
});

test('the driven badge\'s rotated rounded square is turned about its own centre', () => {
  const svg = readDesignSvg(path.join(repoRoot, 'design', 'glyphs', 'driven-badge.svg'), 'glyph');
  const [first] = parts(svg)[0].commands;
  // (12, 11.2), where the square's top edge starts, turned 45° about (13.3, 13.3).
  assert.deepEqual(first.p.map((v) => Math.round(v * 1000) / 1000), [13.866, 10.896]);
});

test('a hard-coded colour is refused unless its class names the token that replaces it', () => {
  assert.throws(() => readDesignSvg(svgFile('red', '<circle cx="8" cy="8" r="5" fill="#FF0000"/>'), 'glyph'), /colour comes from the role/);
  const ink = readDesignSvg(svgFile('ink', '<circle class="ink" cx="8" cy="8" r="5" fill="#14100A"/>'), 'glyph');
  assert.deepEqual(parts(ink)[0].classes, ['ink']);
});

test('a class the design README does not define is refused, since it would quietly draw in currentColor', () => {
  assert.throws(() => readDesignSvg(svgFile('pulse', '<circle class="pulse" cx="8" cy="8" r="5" fill="currentColor"/>'), 'glyph'), /class "pulse"/);
});

test('a closed shape with no fill is refused, since SVG would fill it black', () => {
  assert.throws(() => readDesignSvg(svgFile('nofill', '<circle cx="8" cy="8" r="5" stroke="currentColor"/>'), 'glyph'), /needs a fill/);
  assert.doesNotThrow(() => readDesignSvg(svgFile('line', '<path d="M2 2l4 4" stroke="currentColor"/>'), 'glyph'));
});

test('numbers after a Z are refused, rather than read as a Z repeated without end', () => {
  assert.throws(() => parsePathData('M0 0Z1 1'), /expected a command/);
  assert.equal(formatCommands(parsePathData('M0 0L1 1ZM2 2L3 3Z')), 'M0,0 L1,1 Z M2,2 L3,3 Z');
});

test('a part keeps the paint opacity it inherits from its group, and one XAML cannot reproduce is refused', () => {
  const faint = readDesignSvg(svgFile('faint', '<g stroke-opacity="0.3"><circle cx="8" cy="8" r="5" fill="none" stroke="currentColor" opacity="0.5"/></g>'), 'glyph');
  assert.equal(parts(faint)[0].opacity, 0.15);
  assert.throws(() => readDesignSvg(svgFile('mixed', '<circle cx="8" cy="8" r="5" fill="currentColor" fill-opacity="0.2" stroke="currentColor" stroke-opacity="0.8"/>'), 'glyph'), /opacities must match/);
});

test('a token class on a group draws the group\'s paint from the token in the sprite', () => {
  const svg = readDesignSvg(svgFile('inkgroup', '<g class="ink" stroke="#14100A"><path d="M8 4v4"/></g>'), 'glyph');
  const sprite = spriteSvg([svg], []);
  assert.match(sprite, /<g class="ink" stroke="var\(--ab-glyph-ink\)">/);
  assert.ok(!sprite.includes('#14100A'));
});
test('a path that encloses an area without a Z still needs a fill, as SVG would fill it black', () => {
  assert.throws(() => readDesignSvg(svgFile('triangle', '<path d="M2 2L14 2L8 14" stroke="currentColor"/>'), 'glyph'), /needs a fill/);
  assert.doesNotThrow(() => readDesignSvg(svgFile('cross', '<path d="M2 2L6 6M6 2L2 6" stroke="currentColor"/>'), 'glyph'));
});

test('an opacity, transform or class on the root svg is refused, since neither client would keep it', () => {
  for (const attr of ['opacity="0"', 'transform="rotate(45)"', 'class="spin"', 'stroke-opacity="0.5"']) {
    assert.throws(() => readDesignSvg(svgFile('root', '<circle cx="8" cy="8" r="5" fill="currentColor"/>', `viewBox="0 0 16 16" ${attr}`), 'glyph'), /on <svg> is not in the design subset/, attr);
  }
});

test('a length in percent or a unit is refused, since XAML geometry takes only plain numbers', () => {
  const bad = [
    ['pct', '<circle cx="50%" cy="8" r="5" fill="currentColor"/>'],
    ['px', '<rect x="2" y="2" width="12px" height="12" fill="currentColor"/>'],
    ['dash', '<path d="M2 8L14 8" stroke="currentColor" stroke-dasharray="2 10%"/>'],
    ['stroke', '<path d="M2 8L14 8" stroke="currentColor" stroke-width="0.1em"/>'],
    ['turn', '<g transform="rotate(45deg 8 8)"><circle cx="8" cy="8" r="5" fill="currentColor"/></g>'],
  ];
  for (const [name, body] of bad) { assert.throws(() => readDesignSvg(svgFile(name, body), 'glyph'), /plain number/, name); }
  assert.throws(() => readDesignSvg(svgFile('rootdash', '<path d="M2 8L14 8" stroke="currentColor"/>', 'viewBox="0 0 16 16" stroke-width="5%"'), 'glyph'), /plain number/);
  assert.throws(() => readDesignSvg(svgFile('vb', '<circle cx="8" cy="8" r="5" fill="currentColor"/>', 'viewBox="0 0 16px 16px"'), 'glyph'), /square viewBox/);
  assert.doesNotThrow(() => readDesignSvg(svgFile('ok', '<path d="M2 8L14 8" stroke="currentColor" stroke-width="1.5" stroke-dasharray="2,1e0" opacity=".5"/>'), 'glyph'));
});
