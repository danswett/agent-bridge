import { test, after } from 'node:test';
import assert from 'node:assert/strict';
import { mkdirSync, rmSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { parsePathData, formatCommands, readDesignSvg, parts } from '../lib/svg.mjs';
import { repoRoot } from '../generate.mjs';

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
