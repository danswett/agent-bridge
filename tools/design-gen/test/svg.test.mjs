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

test('a cap or join XAML has no equal, and a transform with the wrong count of numbers, are refused', () => {
  const line = (extra) => `<path d="M2 8L14 8" stroke="currentColor" ${extra}/>`;
  assert.throws(() => readDesignSvg(svgFile('miterclip', line('stroke-linejoin="miter-clip"')), 'glyph'), /not one of miter, round, bevel/);
  assert.throws(() => readDesignSvg(svgFile('inherit', line('stroke-linecap="inherit"')), 'glyph'), /not one of butt, round, square/);
  assert.throws(() => readDesignSvg(svgFile('rootjoin', line(''), 'viewBox="0 0 16 16" stroke-linejoin="arcs"'), 'glyph'), /not one of/);
  const g = (t) => `<g transform="${t}">${line('')}</g>`;
  assert.throws(() => readDesignSvg(svgFile('rot0', g('rotate()')), 'glyph'), /rotate\(\) takes 1 or 3 numbers, not 0/);
  assert.throws(() => readDesignSvg(svgFile('rot2', g('rotate(45 8)')), 'glyph'), /takes 1 or 3 numbers, not 2/);
  assert.throws(() => readDesignSvg(svgFile('tr3', g('translate(1 2 3)')), 'glyph'), /translate\(\) takes 1 or 2 numbers, not 3/);
  assert.doesNotThrow(() => readDesignSvg(svgFile('fine', g('rotate(45 8 8) translate(1)')), 'glyph'));
});

test('a translucent part with both fill and stroke, and a dashed stroke with no width, are refused', () => {
  assert.throws(() => readDesignSvg(svgFile('both', '<circle cx="8" cy="8" r="5" fill="currentColor" stroke="currentColor" fill-opacity=".5" stroke-opacity=".5"/>'), 'glyph'), /XAML would flatten them/);
  assert.throws(() => readDesignSvg(svgFile('groupop', '<g fill-opacity=".4" stroke-opacity=".4"><circle cx="8" cy="8" r="5" fill="currentColor" stroke="currentColor"/></g>'), 'glyph'), /XAML would flatten them/);
  assert.throws(() => readDesignSvg(svgFile('zero', '<path d="M2 8L14 8" stroke="currentColor" stroke-width="0" stroke-dasharray="2 2"/>'), 'glyph'), /stroke-width above 0/);
  assert.doesNotThrow(() => readDesignSvg(svgFile('whole', '<circle cx="8" cy="8" r="5" fill="currentColor" stroke="currentColor" opacity=".5"/>'), 'glyph'));
  assert.doesNotThrow(() => readDesignSvg(svgFile('onepaint', '<circle cx="8" cy="8" r="5" fill="none" stroke="currentColor" stroke-opacity=".3"/>'), 'glyph'));
});

test('an opacity outside 0 to 1, a shape with no size and a negative length are refused, as SVG would not draw them as XAML does', () => {
  const bad = [
    ['op', '<circle cx="8" cy="8" r="5" fill="currentColor" opacity="1.5"/>', /from 0 to 1/],
    ['sop', '<circle cx="8" cy="8" r="5" fill="none" stroke="currentColor" stroke-opacity="-0.2"/>', /from 0 to 1/],
    ['r0', '<circle cx="8" cy="8" r="0" fill="none" stroke="currentColor"/>', /above 0/],
    ['w0', '<rect x="2" y="2" width="0" height="12" fill="none" stroke="currentColor" stroke-linecap="round"/>', /above 0/],
    ['rxneg', '<rect x="2" y="2" width="12" height="12" rx="-2" fill="currentColor"/>', /may not be negative/],
    ['swneg', '<path d="M2 8L14 8" stroke="currentColor" stroke-width="-1"/>', /may not be negative/],
    ['dash0', '<path d="M2 8L14 8" stroke="currentColor" stroke-dasharray="0 0"/>', /negative or all 0/],
  ];
  for (const [name, body, re] of bad) { assert.throws(() => readDesignSvg(svgFile(name, body), 'glyph'), re, name); }
});

test('a part may clear the dashes it inherits with stroke-dasharray none', () => {
  const svg = readDesignSvg(svgFile('undash', '<g stroke-dasharray="2 2"><path d="M2 8L14 8" stroke="currentColor" stroke-dasharray="none"/></g>'), 'glyph');
  assert.equal(parts(svg)[0].style['stroke-dasharray'], 'none');
});

test('an arc with a negative radius takes its absolute value and one with a zero radius is a line, as SVG draws them', () => {
  assert.equal(formatCommands(parsePathData('M2 8A-3 -3 0 0 1 8 8a0 4 0 0 1 4 0')), 'M2,8 A3,3 0 0 1 8,8 L12,8');
});

test('transform functions separated by a comma read as they do in SVG, and anything else between them is refused', () => {
  const g = (t) => `<g transform="${t}"><path d="M2 8L14 8" stroke="currentColor"/></g>`;
  const comma = parts(readDesignSvg(svgFile('comma', g('translate(1 0), translate(0 2)')), 'glyph'))[0];
  assert.equal(formatCommands(comma.commands), 'M3,10 L15,10');
  assert.throws(() => readDesignSvg(svgFile('junk', g('translate(1 0) ; translate(0 2)')), 'glyph'), /cannot read transform/);
});

test('a dotted attribute name is read whole, so it cannot pass for one the subset checks', () => {
  assert.throws(() => readDesignSvg(svgFile('dotted', '<circle cx="8" cy="8" r="5" fill="currentColor" data.opacity="0"/>'), 'glyph'), /attribute data\.opacity is not in the design subset/);
  assert.throws(() => readDesignSvg(svgFile('twice', '<circle cx="8" cy="8" r="5" fill="currentColor" fill="none"/>'), 'glyph'), /repeats fill/);
});

test('a square viewBox is read by its numbers, so 16 and 16.0 are the same side', () => {
  const svg = readDesignSvg(svgFile('vb16', '<circle cx="8" cy="8" r="5" fill="currentColor"/>', 'viewBox="0 0 16 16.0"'), 'glyph');
  assert.equal(svg.size, 16);
});

test('a viewBox with no area is refused, as WinUI cannot size a glyph by it', () => {
  for (const vb of ['0 0 -16 -16', '0 0 0 0']) {
    assert.throws(() => readDesignSvg(svgFile('novb', '<circle cx="8" cy="8" r="5" fill="currentColor"/>', `viewBox="${vb}"`), 'glyph'), /sides above 0/, vb);
  }
});

test('a part with two token classes, its own or its group\'s, is refused, since the clients would each pick one', () => {
  assert.throws(() => readDesignSvg(svgFile('two', '<circle class="knockout ink" cx="8" cy="8" r="5" fill="#FFFFFF"/>'), 'glyph'), /one token class, not knockout and ink/);
  assert.throws(() => readDesignSvg(svgFile('inherited', '<g class="ink"><circle class="knockout" cx="8" cy="8" r="5" fill="#FFFFFF"/></g>'), 'glyph'), /one token class, not ink and knockout/);
  assert.doesNotThrow(() => readDesignSvg(svgFile('same', '<g class="ink"><circle class="ink spin" cx="8" cy="8" r="5" fill="#FFFFFF"/></g>'), 'glyph'));
});
