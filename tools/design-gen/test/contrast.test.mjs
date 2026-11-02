import { test } from 'node:test';
import assert from 'node:assert/strict';
import { loadTokens } from '../lib/tokens.mjs';
import { contrastPairs, failures, describe } from '../lib/contrast.mjs';
import { repoRoot } from '../generate.mjs';
import path from 'node:path';

const tokens = loadTokens(path.join(repoRoot, 'design'));
const pairs = contrastPairs(tokens);
const covered = (fg, kind) => ['dark', 'light'].every((theme) => tokens.color.contrast.surfaces.every((bg) => pairs.some((p) => p.theme === theme && p.fg === fg && p.bg === bg && p.kind === kind)));

test('every colour role reaches 3:1 as a graphic on every surface, in dark and light', () => {
  for (const role of Object.keys(tokens.color.role)) { assert.ok(covered(role, 'graphic'), `${role} is measured as a graphic on every surface`); }
  assert.deepEqual(failures(pairs.filter((p) => p.kind === 'graphic')).map(describe), []);
});

test('every text token reaches 4.5:1 on every surface, in dark and light', () => {
  for (const fg of ['surface.text', 'surface.text2', 'surface.text3', 'needs.text', 'needs.head', 'agent.text', 'danger.text', 'bridge']) {
    assert.ok(covered(fg, 'text'), `${fg} is measured as text on every surface`);
  }
  // The question card: every kind of words that can sit in it, on it.
  for (const fg of ['needs.text', 'danger.text', 'agent.text', 'bridge', 'surface.text2']) {
    assert.ok(pairs.some((p) => p.theme === 'dark' && p.fg === fg && p.bg === 'needs.fill on card' && p.kind === 'text'), ` is measured on the needs fill`);
  }
  assert.ok(pairs.some((p) => p.fg === 'bridge.on' && p.bg === 'bridge'), 'text on a bridge button is measured');
  assert.deepEqual(failures(pairs.filter((p) => p.kind === 'text')).map(describe), []);
});

test('the needs glyph\'s "!" stands out from its disc', () => {
  assert.ok(pairs.some((p) => p.fg === 'needs.ink' && p.bg === 'needs' && p.theme === 'light'));
  assert.deepEqual(failures(pairs.filter((p) => p.fg === 'needs.ink')).map(describe), []);
});

test('the check finds the two light tokens #183 reported', () => {
  const before = structuredClone(tokens);
  before.color.role.needs.light = '#E8A10C';
  before.surface.windows.light.text3 = 'rgba(0,0,0,0.52)';
  const found = failures(contrastPairs(before));
  assert.ok(found.some((p) => p.theme === 'light' && p.fg === 'needs' && p.bg === 'card' && p.kind === 'graphic' && p.ratio < 2.3), 'needs at #E8A10C is 2.20:1 on the card');
  assert.ok(found.some((p) => p.theme === 'light' && p.fg === 'surface.text3' && p.bg === 'shell' && p.ratio < 4.2), 'text3 at 0.52 is 4.16:1 on the shell');
  assert.ok(found.every((p) => p.theme === 'light' && (p.fg === 'needs' || p.fg === 'surface.text3')), `only those two: ${found.map(describe).join('; ')}`);
});


test('the check finds danger set as words on the dark needs fill, which is why words in danger use danger.text', () => {
  const asRole = structuredClone(tokens);
  asRole.color.contrast.roleText.push('danger');
  const found = failures(contrastPairs(asRole));
  assert.ok(found.some((p) => p.theme === 'dark' && p.fg === 'danger' && p.bg === 'needs.fill on card' && p.kind === 'text' && p.ratio < 3.8), found.map(describe).join('; '));
});
test('words on a tint are measured on it over each surface it sits on, and only the words it allows', () => {
  const onTint = pairs.filter((p) => p.bg.startsWith('subtle on ') || p.bg.startsWith('selected on '));
  assert.ok(onTint.some((p) => p.theme === 'light' && p.fg === 'bridge' && p.bg === 'subtle on card' && p.kind === 'text'));
  assert.ok(!onTint.some((p) => p.fg === 'surface.text3' || p.bg.includes('fill')), 'text3 and role fills are not on a tint');
  // text3 is 4.48:1 on subtle over the dark card, which is why a tint does not allow it.
  const wider = structuredClone(tokens);
  wider.color.contrast.tints.subtle.push('surface.text3');
  assert.ok(failures(contrastPairs(wider)).some((p) => p.theme === 'dark' && p.fg === 'surface.text3' && p.bg === 'subtle on card'));
});
test('hyperlinks, words on an accent fill and selected text reach 4.5:1 on the Bridge accent palette, in dark and light', () => {
  const accent = pairs.filter((p) => p.fg.startsWith('accent.'));
  for (const fg of ['accent.link', 'accent.linkPointerOver', 'accent.linkPressed']) { assert.ok(covered(fg, 'text'), `${fg} is measured as text on every surface`); }
  assert.ok(accent.some((p) => p.theme === 'dark' && p.fg === 'accent.link' && p.bg === 'needs.fill on card'), 'a link in a question is measured on the needs fill');
  assert.ok(accent.some((p) => p.theme === 'dark' && p.fg === 'accent.textOnFill' && p.bg === 'accent.light2'));
  assert.ok(accent.some((p) => p.theme === 'light' && p.fg === 'accent.textOnFill' && p.bg === 'accent.dark1'));
  assert.ok(accent.some((p) => p.fg === 'accent.selectedText' && p.bg === 'accent.base'));
  assert.deepEqual(failures(accent).map(describe), []);
});

test('the check finds a SystemAccentColor too light for white selected text', () => {
  const lighter = structuredClone(tokens);
  lighter.color.accent.windows.base = '#0D9488';
  const found = failures(contrastPairs(lighter));
  assert.ok(found.some((p) => p.fg === 'accent.selectedText' && p.ratio < 3.8), found.map(describe).join('; '));
});
